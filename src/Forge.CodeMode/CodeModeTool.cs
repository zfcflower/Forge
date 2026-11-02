using System.Globalization;
using System.Text.Json;
using Forge.Abstractions;
using Microsoft.CodeAnalysis.Scripting;

namespace Forge.CodeMode;

/// <summary>
/// <c>run_code</c>: compiles a C# script, runs it against a <see cref="ToolsFacade"/> and returns the script's
/// value plus captured logs as one tool result. The definition is Execute because a script may reach write
/// tools, but every inner call is separately measured against the approval policy.
/// </summary>
public sealed class CodeModeTool : ITool
{
    public const string Name = "run_code";

    private const string SchemaJson =
        """{"type":"object","properties":{"code":{"type":"string","description":"C# script body. Top-level await/var/return are allowed and the final expression is the result."},"timeout_seconds":{"type":"number","description":"Wall-clock budget for the whole script; clamped to the maximum stated in the description."}},"required":["code"],"additionalProperties":false}""";

    private readonly IToolRegistry registry;
    private readonly Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>> approval;
    private readonly CodeModeOptions options;
    private readonly ScriptCompiler compiler;
    private ToolDefinition? definition;
    private int definitionFingerprint = int.MinValue;

    public CodeModeTool(
        IToolRegistry tools,
        Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>> approval,
        CodeModeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(approval);
        this.registry = tools;
        this.approval = approval;
        this.options = (options ?? CodeModeOptions.Default).Validated();
        compiler = new ScriptCompiler(this.options);
    }

    public ToolDefinition Definition
    {
        get
        {
            var fingerprint = Fingerprint();
            var current = Volatile.Read(ref definition);
            if (current is null || fingerprint != definitionFingerprint)
            {
                current = new ToolDefinition(Name, ToolDescription.Build(registry.Tools, options, Name), SchemaJson);
                definitionFingerprint = fingerprint;
                Volatile.Write(ref definition, current);
            }

            return current;
        }
    }

    public ToolSideEffect SideEffect => ToolSideEffect.Execute;

    /// <summary>Compiles and runs a trivial script so the first real call does not pay Roslyn's start-up cost.</summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        const string code = "1 + 1";
        var compiled = await compiler.GetAsync(code, ct).ConfigureAwait(false);
        if (!compiled.Ok)
        {
            return;
        }

        var log = new ScriptLog(options.MaxLogChars);
        var globals = CreateGlobals(new ToolContext("warmup", Environment.CurrentDirectory), CancellationToken.None, log);
        await compiled.Runner(ct)(globals, ct).ConfigureAwait(false);
    }

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (!TryParseArguments(call.ArgumentsJson, out var code, out var timeout, out var argumentError))
        {
            return new ToolResult(argumentError, IsError: true);
        }

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(timeout);
        var runToken = runCts.Token;
        var log = new ScriptLog(options.MaxLogChars);
        var globals = CreateGlobals(context, runToken, log);

        try
        {
            var compiled = await compiler.GetAsync(code, runToken).ConfigureAwait(false);
            if (!compiled.Ok)
            {
                return new ToolResult(AppendLog(compiled.Error!, log), IsError: true);
            }

            var value = await RunAsync(compiled.Runner(runToken), globals, runToken).ConfigureAwait(false);
            value = await UnwrapAsync(value, runToken).ConfigureAwait(false);
            var text = TextBounds.Truncate(ScriptOutput.Format(value), options.MaxResultChars, "result");
            return new ToolResult(AppendLog(text, log));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && runToken.IsCancellationRequested)
        {
            return new ToolResult(
                AppendLog($"The script was stopped after {Seconds(timeout)} s (run_code timeout). Narrow the work, raise timeout_seconds, or split it into several calls.", log),
                IsError: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolResult(AppendLog("The script threw " + ScriptOutput.Describe(ex), log), IsError: true);
        }
    }

    /// <summary>
    /// Waits for the script without trusting it to observe cancellation: a script awaiting something that ignores the
    /// token still returns control when the budget runs out. The abandoned run keeps its thread until it ends.
    /// </summary>
    private static async Task<object?> RunAsync(ScriptRunner<object?> runner, ScriptGlobals globals, CancellationToken runToken)
    {
        var run = runner(globals, runToken);
        try
        {
            return await run.WaitAsync(runToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Observe(run);
            throw;
        }
    }

    private static async ValueTask<object?> UnwrapAsync(object? value, CancellationToken runToken)
    {
        if (value is not Task pending)
        {
            return value;
        }

        try
        {
            await pending.WaitAsync(runToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Observe(pending);
            throw;
        }

        var resultProperty = pending.GetType().GetProperty("Result");
        return resultProperty is null ? null : resultProperty.GetValue(pending);
    }

    private static void Observe(Task task)
    {
        if (task.IsCompleted)
        {
            return;
        }

        _ = task.ContinueWith(
            static abandoned => _ = abandoned.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private ScriptGlobals CreateGlobals(ToolContext context, CancellationToken runToken, ScriptLog log) =>
        new(
            new ToolsFacade(registry, context, approval, Name, options.MaxConcurrentToolCalls, options.MaxInnerResultChars, runToken),
            log,
            runToken);

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

            if (code.Length > options.MaxScriptChars)
            {
                error = $"The script is {code.Length} chars; the limit is {options.MaxScriptChars}.";
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

                timeout = options.ClampTimeout(TimeSpan.FromSeconds(seconds));
            }

            return true;
        }
    }

    private static string AppendLog(string text, ScriptLog log)
    {
        var captured = log.Snapshot();
        return captured.Length == 0 ? text : text + "\n[log]\n" + captured;
    }

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private int Fingerprint()
    {
        var hash = new HashCode();
        foreach (var tool in registry.Tools)
        {
            hash.Add(tool.Definition.Name, StringComparer.Ordinal);
            hash.Add(tool.Definition.Description, StringComparer.Ordinal);
            hash.Add(tool.Definition.InputSchemaJson, StringComparer.Ordinal);
            hash.Add((int)tool.SideEffect);
        }

        return hash.ToHashCode();
    }
}
