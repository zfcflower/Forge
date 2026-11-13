using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Sdk = Forge.Plugin.Sdk;

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
            var fingerprint = RunCodeToolSurface.Fingerprint(surface);
            var current = Volatile.Read(ref definition);
            if (current is null || fingerprint != definitionFingerprint)
            {
                current = new ToolDefinition(Name, RunCodeDescription.Build(surface, options), SchemaJson);
                definitionFingerprint = fingerprint;
                Volatile.Write(ref definition, current);
            }

            return current;
        }
    }

    public ToolSideEffect SideEffect => ToolSideEffect.Execute;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (!TryParseArguments(call.ArgumentsJson, out var code, out var timeout, out var argumentError))
        {
            return new ToolResult(argumentError, IsError: true);
        }

        var declarations = RunCodeToolSurface.Declarations(RunCodeToolSurface.Snapshot(registry.Tools));
        return await client
            .InvokeCodeAsync(registry, options.Execution, code, declarations, timeout, context, ct)
            .ConfigureAwait(false);
    }

    private bool TryParseArguments(string argumentsJson, out string code, out TimeSpan timeout, out string error)
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

            if (code.Length > options.Execution.MaxScriptChars)
            {
                error = $"The script is {code.Length} chars; the limit is {options.Execution.MaxScriptChars}.";
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

    /// <summary>Content hash of a snapshot. It covers every field the description depends on and nothing else, so a
    /// description is rebuilt exactly when the callable surface actually changes - not when a tool object is replaced
    /// by an equal one, and not when a proxy is added to or removed from the registry.</summary>
    internal static int Fingerprint(List<RunCodeToolEntry> surface)
    {
        var hash = new HashCode();
        foreach (var entry in surface)
        {
            hash.Add(entry.Name, StringComparer.Ordinal);
            hash.Add(entry.Description, StringComparer.Ordinal);
            hash.Add(entry.SchemaJson, StringComparer.Ordinal);
            hash.Add((int)entry.SideEffect);
        }

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

/// <summary>Builds <c>run_code</c>'s description: the script surface, the enforced budgets, and every callable tool
/// with its side effect and input schema. Output is ordinal-sorted and depends only on the registry and options.</summary>
internal static class RunCodeDescription
{
    private const int OneLineChars = 220;

    private static readonly (string Form, string Meaning)[] Surface =
    [
        ("""await Tools.Call("tool_name", new { arg = 1 })""", "tool result text; throws ToolCallException when the call fails or is denied"),
        ("""await Tools.Call<T>("tool_name", new { arg = 1 })""", "result deserialized from JSON into T"),
        ("""await Tools.CallJson("tool_name", "{\"arg\":1}")""", "same as Call, for hand-written JSON"),
        ("Tools.Json<T>(jsonText)", "parse JSON text into T"),
        ("Log(value)", "captured and appended to the result"),
        ("Cancellation", "token cancelled when the run times out; call ThrowIfCancellationRequested() in long loops"),
    ];

    private static readonly int SurfaceWidth = WidestForm() + 2;

    public static string Build(List<RunCodeToolEntry> callable, RunCodeProxyOptions options)
    {
        var text = new StringBuilder(2048 + (callable.Count * 320));
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
            .Append(options.Execution.MaxConcurrentToolCalls.ToString(CultureInfo.InvariantCulture))
            .Append(" inner calls in flight; inner results are cut at ")
            .Append(options.Execution.MaxInnerResultChars.ToString(CultureInfo.InvariantCulture))
            .Append(" chars and the returned result at ")
            .Append(options.Execution.MaxResultChars.ToString(CultureInfo.InvariantCulture))
            .Append(" chars (logs at ")
            .Append(options.Execution.MaxLogChars.ToString(CultureInfo.InvariantCulture))
            .Append(").\n");
        text.Append("Each script runs in its own worker process with an exclusive slot: a run that exceeds its budget is terminated with that process, and the main program never runs script code itself. That is process separation, not an OS sandbox - same-account file and network access stays possible through the tools you call. run_code cannot be invoked from inside another extension invocation - a script or a plugin callback - and a plugin may not be called back into while it is already on the call chain. Every inner tool call is approval-checked on its own, exactly like a direct call.\n\n");

        text.Append("Tools (")
            .Append(callable.Count.ToString(CultureInfo.InvariantCulture))
            .Append("), sorted by name:\n");
        foreach (var tool in callable)
        {
            text.Append("  ")
                .Append(tool.Name)
                .Append(" [")
                .Append(tool.SideEffect switch
                {
                    ToolSideEffect.ReadOnly => "read-only",
                    ToolSideEffect.Write => "write",
                    _ => "execute",
                })
                .Append("]: ")
                .Append(OneLine(tool.Description))
                .Append(" args=")
                .Append(Compact(tool.SchemaJson))
                .Append('\n');
        }

        return text.ToString();
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

    private static string OneLine(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "(no description)";
        }

        var trimmed = description.AsSpan().TrimStart();
        var end = trimmed.IndexOf('\n');
        var line = (end < 0 ? trimmed : trimmed[..end]).Trim();
        return line.Length <= OneLineChars ? line.ToString() : string.Concat(line[..OneLineChars], "...");
    }

    private static string Compact(string schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            var buffer = new ArrayBufferWriter<byte>(schemaJson.Length);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                document.RootElement.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            return schemaJson.Trim();
        }
    }
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

        var execution = options.Execution ?? throw new ArgumentNullException(nameof(options), "Execution is required.");
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
