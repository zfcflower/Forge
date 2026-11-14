using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Sdk = Forge.Plugin.Sdk;
using CodeExecutionOptions = Forge.Plugin.Sdk.CodeExecutionOptions;

namespace Forge.HostClient;

/// <summary>
/// <c>run_code</c> as the main program exposes it: AOT-safe proxy that validates the arguments locally and sends
/// one <c>code.execute</c> request to the Extension Host, which owns compilation, the script worker and its
/// lifetime. The stable description is built from the main-program tool registry, so two turns with the same tools
/// send identical bytes.
/// </summary>
internal sealed class RunCodeProxyTool : ITool
{
    internal const string Name = "run_code";

    private const string SchemaJson =
        """{"type":"object","properties":{"code":{"type":"string","description":"C# script body. Top-level await/var/return are allowed and the final expression is the result."},"timeout_seconds":{"type":"number","description":"Wall-clock budget for the whole script; clamped to the maximum stated in the description."}},"required":["code"],"additionalProperties":false}""";

    private readonly ExtensionHostClient client;
    private readonly IToolRegistry registry;
    private readonly RunCodeProxyOptions options;
    private ToolDefinition? definition;
    private int definitionFingerprint = int.MinValue;

    internal RunCodeProxyTool(ExtensionHostClient client, IToolRegistry registry, RunCodeProxyOptions options)
    {
        this.client = client;
        this.registry = registry;
        this.options = options;
    }

    public ToolDefinition Definition
    {
        get
        {
            var surface = RunCodeToolSurface.Snapshot(registry.Tools);
            var execution = CurrentExecution;
            var fingerprint = RunCodeToolSurface.Fingerprint(
                surface, options.DefaultTimeout, options.MaxTimeout, execution);
            var current = Volatile.Read(ref definition);
            if (current is null || fingerprint != definitionFingerprint)
            {
                current = new ToolDefinition(Name, RunCodeDescription.Build(surface, options, execution), SchemaJson);
                definitionFingerprint = fingerprint;
                Volatile.Write(ref definition, current);
            }

            return current;
        }
    }

    public ToolSideEffect SideEffect => ToolSideEffect.Execute;

    /// <summary>The bounds in force right now: the live provider when one is configured, else the fixed options.
    /// Read once per description build and once per invocation, so a settings change reaches the next turn's prompt
    /// and every later script without rebuilding the tool. A provider that answers null is a misconfiguration and throws:
    /// falling back to <see cref="RunCodeProxyOptions.Execution"/> would silently run a script under budgets nobody chose.</summary>
    private CodeExecutionOptions CurrentExecution =>
        options.ExecutionProvider is { } provider
            ? provider() ?? throw new InvalidOperationException(
                "RunCodeProxyOptions.ExecutionProvider returned null; the run_code execution bounds are never optional.")
            : options.Execution;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var execution = CurrentExecution;
        if (!TryParseArguments(call.ArgumentsJson, execution, out var code, out var timeout, out var argumentError))
        {
            return new ToolResult(argumentError, IsError: true);
        }

        var declarations = RunCodeToolSurface.Declarations(RunCodeToolSurface.Snapshot(registry.Tools));
        return await client
            .InvokeCodeAsync(registry, execution, code, declarations, timeout, context, ct)
            .ConfigureAwait(false);
    }

    private bool TryParseArguments(
        string argumentsJson,
        CodeExecutionOptions execution,
        out string code,
        out TimeSpan timeout,
        out string error)
    {
        code = string.Empty;
        timeout = options.DefaultTimeout;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            error = "run_code needs {\"code\": \"...\"}.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException ex)
        {
            error = $"run_code arguments are not valid JSON: {ex.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "run_code arguments must be a JSON object.";
                return false;
            }

            if (!root.TryGetProperty("code", out var codeElement) || codeElement.ValueKind != JsonValueKind.String)
            {
                error = "run_code requires a 'code' string.";
                return false;
            }

            code = codeElement.GetString() ?? string.Empty;
            if (code.Trim().Length == 0)
            {
                error = "run_code requires a non-empty 'code' script.";
                return false;
            }

            if (code.Length > execution.MaxScriptChars)
            {
                error = $"The script is {code.Length} chars; the limit is {execution.MaxScriptChars}.";
                return false;
            }

            if (root.TryGetProperty("timeout_seconds", out var timeoutElement) &&
                timeoutElement.ValueKind != JsonValueKind.Null)
            {
                if (timeoutElement.ValueKind != JsonValueKind.Number ||
                    !timeoutElement.TryGetDouble(out var seconds) ||
                    !double.IsFinite(seconds) ||
                    seconds <= 0)
                {
                    error = "'timeout_seconds' must be a positive number.";
                    return false;
                }

                timeout = ClampSeconds(seconds);
            }

            return true;
        }
    }

    private TimeSpan ClampSeconds(double seconds)
    {
        var maximum = options.MaxTimeout;
        if (seconds >= maximum.TotalSeconds)
        {
            return maximum;
        }

        var requested = TimeSpan.FromSeconds(seconds);
        return requested < maximum ? requested : maximum;
    }
}

/// <summary>One callable tool as the script sees it: the four definition fields, read exactly once.</summary>
internal readonly record struct RunCodeToolEntry(
    string Name, string Description, string SchemaJson, ToolSideEffect SideEffect);

/// <summary>The single derivation of the callable tool surface. The description text, the change fingerprint and the
/// <c>code.execute</c> declarations all come from one <see cref="Snapshot"/> result, so the three can never disagree
/// about which tools are callable or which definition content was read.</summary>
internal static class RunCodeToolSurface
{
    /// <summary>Callable tools of a live registry, ordinal-sorted by name. Every <see cref="RunCodeProxyTool"/> is
    /// dropped <b>by type before any <c>Definition</c> is read</b>: a proxy derives its description from this same
    /// registry, so reading it while a proxy is registered (this one or a second proxy sharing the registry) would
    /// recurse without a base case and overflow the stack, which no handler can catch. Tools that are merely
    /// <i>named</i> <c>run_code</c> are dropped afterwards, by name, from the remaining (proxy-free) tools.</summary>
    internal static List<RunCodeToolEntry> Snapshot(IReadOnlyList<ITool> tools)
    {
        var entries = new List<RunCodeToolEntry>(tools.Count);
        foreach (var tool in tools)
        {
            if (tool is RunCodeProxyTool)
            {
                continue;
            }

            var definition = tool.Definition;
            if (string.Equals(definition.Name, RunCodeProxyTool.Name, StringComparison.Ordinal))
            {
                continue;
            }

            entries.Add(new RunCodeToolEntry(
                definition.Name,
                definition.Description ?? string.Empty,
                string.IsNullOrWhiteSpace(definition.InputSchemaJson) ? "{}" : definition.InputSchemaJson,
                tool.SideEffect));
        }

        entries.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return entries;
    }

    /// <summary>Content hash of a snapshot plus the budgets the description states. It covers every field the description
    /// depends on and nothing else, so a description is rebuilt exactly when the callable surface or one of the stated
    /// numbers actually changes - not when a tool object is replaced by an equal one, and not when a proxy is added to or
    /// removed from the registry.</summary>
    internal static int Fingerprint(List<RunCodeToolEntry> surface, TimeSpan defaultTimeout, TimeSpan maxTimeout, CodeExecutionOptions execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var hash = new HashCode();
        foreach (var entry in surface)
        {
            hash.Add(entry.Name, StringComparer.Ordinal);
            hash.Add(entry.Description, StringComparer.Ordinal);
            hash.Add(entry.SchemaJson, StringComparer.Ordinal);
            hash.Add((int)entry.SideEffect);
        }

        hash.Add(defaultTimeout);
        hash.Add(maxTimeout);
        hash.Add(execution.MaxConcurrentToolCalls);
        hash.Add(execution.MaxResultChars);
        hash.Add(execution.MaxInnerResultChars);
        hash.Add(execution.MaxLogChars);
        return hash.ToHashCode();
    }

    internal static Sdk.ToolDeclaration[] Declarations(List<RunCodeToolEntry> surface)
    {
        var declarations = new Sdk.ToolDeclaration[surface.Count];
        for (var index = 0; index < surface.Count; index++)
        {
            var entry = surface[index];
            declarations[index] = new Sdk.ToolDeclaration(
                entry.Name,
                entry.Description,
                entry.SchemaJson,
                entry.SideEffect switch
                {
                    ToolSideEffect.ReadOnly => Sdk.ToolSideEffect.ReadOnly,
                    ToolSideEffect.Write => Sdk.ToolSideEffect.Write,
                    _ => Sdk.ToolSideEffect.Execute,
                });
        }

        return declarations;
    }
}

/// <summary>Builds <c>run_code</c>'s description: the script surface, the enforced budgets and the names of the callable
/// tools. A tool's description and argument schema are deliberately <b>not</b> inlined: the model asks the script for them
/// through <c>Tools.Describe</c>, so a turn pays for one tool definition instead of one line plus one JSON schema per
/// callable tool. Output is ordinal-sorted and depends only on the registry and the budgets.</summary>
internal static class RunCodeDescription
{
    /// <summary>Width the tool-name line wraps at; the description is one line per turn of the prompt either way, and a
    /// wrapped list stays scannable in a client that renders it flat.</summary>
    private const int NamesLineWidth = 96;

    private static readonly (string Form, string Meaning)[] Surface =
    [
        ("""await Tools.Call("tool_name", new { arg = 1 })""", "tool result text; throws ToolCallException when the call fails or is denied"),
        ("""await Tools.Call<T>("tool_name", new { arg = 1 })""", "result deserialized from JSON into T"),
        ("""await Tools.CallJson("tool_name", "{\"arg\":1}")""", "same as Call, for hand-written JSON"),
        ("Tools.List()", "names of the tools this script may call"),
        ("""Tools.Describe("tool_name")""", "one tool's side effect, description and argument schema"),
        ("Tools.Json<T>(jsonText)", "parse JSON text into T"),
        ("Log(value)", "captured and appended to the result"),
        ("Cancellation", "token cancelled when the run times out; call ThrowIfCancellationRequested() in long loops"),
    ];

    private static readonly int SurfaceWidth = WidestForm() + 2;

    public static string Build(List<RunCodeToolEntry> callable, RunCodeProxyOptions options, CodeExecutionOptions execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var text = new StringBuilder(1024 + (callable.Count * 24));
        text.Append("Runs one C# script in the Extension Host's script worker and returns its value once, so a chain of tool calls costs a single round trip instead of one per call.\n\n");
        text.Append("Script surface:\n");
        foreach (var (form, meaning) in Surface)
        {
            text.Append("  ").Append(form.PadRight(SurfaceWidth)).Append(meaning).Append('\n');
        }

        text.Append("  The last expression, or a top-level `return`, is serialized as the result; `await` and `var` work at top level.\n");
        text.Append("  Awaiting calls one by one serializes them; use Task.WhenAll(...) to overlap independent calls.\n\n");
        text.Append("Language: C# 15 on .NET 11 - async/await, LINQ, collections, Task.WhenAll, System.Text.Json, System.Text.RegularExpressions, string/Math/DateTime/Guid/CultureInfo.\n");
        text.Append("Refused at compile time (the error names the line and column): file and network access, processes, reflection, Environment/AppContext, AppDomain, assembly loading, Console, explicit threads, P/Invoke, unsafe code, #r and #load.\n");
        text.Append("Limits: default timeout ")
            .Append(Seconds(options.DefaultTimeout))
            .Append(" s, maximum ")
            .Append(Seconds(options.MaxTimeout))
            .Append(" s (send timeout_seconds); at most ")
            .Append(execution.MaxConcurrentToolCalls.ToString(CultureInfo.InvariantCulture))
            .Append(" inner calls in flight; inner results are cut at ")
            .Append(execution.MaxInnerResultChars.ToString(CultureInfo.InvariantCulture))
            .Append(" chars and the returned result at ")
            .Append(execution.MaxResultChars.ToString(CultureInfo.InvariantCulture))
            .Append(" chars (logs at ")
            .Append(execution.MaxLogChars.ToString(CultureInfo.InvariantCulture))
            .Append(").\n");
        text.Append("Each script runs in its own worker process with an exclusive slot: a run that exceeds its budget is terminated with that process, and the main program never runs script code itself. That is process separation, not an OS sandbox - same-account file and network access stays possible through the tools you call. run_code cannot be invoked from inside another extension invocation - a script or a plugin callback - and a plugin may not be called back into while it is already on the call chain. Every inner tool call is approval-checked on its own, exactly like a direct call.\n\n");

        text.Append("Tools (")
            .Append(callable.Count.ToString(CultureInfo.InvariantCulture))
            .Append("), sorted by name; Tools.Describe(\"name\") returns one tool's description and argument schema:\n");
        AppendNames(text, callable);
        return text.ToString();
    }

    /// <summary>The callable names, comma-separated in one wrapped block. Names only: the side effect, description and
    /// schema of each are one <c>Tools.Describe</c> call away, which is the whole point of the shorter description.</summary>
    private static void AppendNames(StringBuilder text, List<RunCodeToolEntry> callable)
    {
        if (callable.Count == 0)
        {
            text.Append("  (none)\n");
            return;
        }

        text.Append("  ");
        var column = 2;
        for (var index = 0; index < callable.Count; index++)
        {
            var name = callable[index].Name;
            if (index > 0)
            {
                if (column + 2 + name.Length > NamesLineWidth)
                {
                    text.Append(",\n  ");
                    column = 2;
                }
                else
                {
                    text.Append(", ");
                    column += 2;
                }
            }

            text.Append(name);
            column += name.Length;
        }

        text.Append('\n');
    }

    private static int WidestForm()
    {
        var widest = 0;
        foreach (var (form, _) in Surface)
        {
            widest = Math.Max(widest, form.Length);
        }

        return widest;
    }

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Validation of the trusted <c>run_code</c> configuration. Invalid values are rejected when the tool is
/// created instead of being silently clamped, so a misconfigured budget never reaches a script.</summary>
internal static class RunCodeProxyOptionsExtensions
{
    internal static RunCodeProxyOptions Validated(this RunCodeProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DefaultTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.DefaultTimeout, "DefaultTimeout must be positive.");
        }

        if (options.MaxTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MaxTimeout, "MaxTimeout must be positive.");
        }

        if (options.MaxTimeout < options.DefaultTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MaxTimeout, "MaxTimeout must not be shorter than DefaultTimeout.");
        }

        var execution = options.ExecutionProvider is { } provider
            ? provider() ?? throw new ArgumentException(
                "ExecutionProvider returned null; the run_code execution bounds are never optional.", nameof(options))
            : options.Execution;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(execution.MaxConcurrentToolCalls, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(execution.MaxScriptChars, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(execution.MaxResultChars, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(execution.MaxInnerResultChars, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(execution.MaxLogChars, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(execution.MaxDiagnosticChars, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(execution.MaxDiagnosticCount, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(execution.CompilationCacheCapacity, 0);
        return options;
    }
}

/// <summary>Main-side truncation of callback results and script output.</summary>
internal static class TextBounds
{
    public static string Truncate(string text, int maxChars, string what)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        return new StringBuilder(maxChars + 96)
            .Append(text, 0, maxChars)
            .Append("\n[... ")
            .Append(what)
            .Append(" truncated: kept ")
            .Append(maxChars)
            .Append(" of ")
            .Append(text.Length)
            .Append(" chars]")
            .ToString();
    }
}
