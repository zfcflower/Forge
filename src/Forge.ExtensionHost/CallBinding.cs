using Forge.Ipc;
using Forge.Ipc.Protocol;
using Forge.Plugin.Sdk;

namespace Forge.ExtensionHost;

/// <summary>
/// Validates the call context the main program assigned to an execution and keeps the per-worker-session table of
/// executions that are still in flight. The context is the main program's authority: the host checks its shape and its
/// chain, binds it to one execution, and never lets a worker-supplied value replace it. Only <c>runId</c> travels back
/// from the worker, and it only resolves against the table of the session that is running that execution.
/// </summary>
internal static class CallBinding
{
    /// <summary>Plugin identity the main program uses for a <c>run_code</c> execution; no plugin may claim it.</summary>
    public const string RunCodeIdentity = "$run_code";

    /// <summary>Longest call chain the main program may hand over; the host refuses longer ones.</summary>
    public const int MaxCallChain = 8;

    /// <summary>Deepest nesting the main program may report.</summary>
    public const int MaxDepth = 8;

    private const int MaxRunIdLength = 128;

    /// <summary>Validates the context every operation must carry; a missing context is refused.</summary>
    public static ExtensionCallContext Require(ExtensionCallContext? context, string targetIdentity, string operation) =>
        context is null
            ? throw new HostOperationException(
                "invalid_context",
                $"'{operation}' requires the call context assigned by the main program; a request without one is refused.")
            : Validate(context, targetIdentity, operation);

    /// <summary>
    /// The root execution this call belongs to. The main program assigns <c>rootRunId</c> from its own pending table: at
    /// the top level it equals <c>runId</c>, and a nested call inherits the root of the trusted parent run. The root is a
    /// call <em>token</em>, never a plugin name and never <c>$run_code</c>: independent scripts and independent calls of
    /// one plugin are different roots, so they never refuse one another as busy.
    /// <para>
    /// A context without a root run id is only accepted when it is a root call (empty chain), where the root is the run
    /// itself - that keeps direct root callers that do not track a root working. A nested context
    /// that arrives without one is refused: the host cannot infer it, and guessing from plugin identities is exactly what
    /// conflated independent scripts before.
    /// </para>
    /// </summary>
    public static string RootOf(ExtensionCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RootRunId is { Length: > 0 } root
            ? root
            : throw new HostOperationException(
                "invalid_context",
                "The call carries no root run id; a nested execution must inherit the root of its trusted parent call.");
    }

    private static ExtensionCallContext Validate(ExtensionCallContext context, string identity, string operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);

        if (!TryValidateRunId(context.RunId, out var problem))
        {
            throw new HostOperationException("invalid_context", problem);
        }

        if (context.SessionId is null)
        {
            throw new HostOperationException("invalid_context", $"'{operation}' carries no session id.");
        }

        if (string.IsNullOrWhiteSpace(context.WorkingDirectory) ||
            !Path.IsPathFullyQualified(context.WorkingDirectory))
        {
            throw new HostOperationException(
                "invalid_context",
                $"'{operation}' carries the working directory '{context.WorkingDirectory}', which is not a fully qualified path.");
        }

        if (context.Depth is < 0 or > MaxDepth)
        {
            throw new HostOperationException(
                "invalid_context",
                $"'{operation}' carries depth {context.Depth}; the host accepts 0-{MaxDepth}.");
        }

        var chain = context.PluginChain ??
            throw new HostOperationException("invalid_context", $"'{operation}' carries no plugin chain.");
        if (chain.Count > MaxCallChain)
        {
            throw new HostOperationException(
                "chain_too_long",
                $"'{operation}' carries a call chain of {chain.Count} links; the limit is {MaxCallChain}.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ancestor in chain)
        {
            if (string.IsNullOrWhiteSpace(ancestor))
            {
                throw new HostOperationException("invalid_context", $"'{operation}' carries a blank plugin chain link.");
            }

            if (!seen.Add(ancestor))
            {
                throw new HostOperationException(
                    "chain_cycle",
                    $"'{operation}' carries a call chain that names '{ancestor}' twice.");
            }

            if (string.Equals(ancestor, identity, StringComparison.Ordinal))
            {
                throw new HostOperationException(
                    "chain_cycle",
                    $"'{operation}' would call '{identity}', which is already on its own call chain; recursive plugin calls are refused.");
            }

            if (!string.Equals(ancestor, RunCodeIdentity, StringComparison.Ordinal) &&
                !PluginIds.IsSafeSegment(ancestor))
            {
                throw new HostOperationException(
                    "invalid_context",
                    $"'{operation}' carries the unusable plugin chain link '{ancestor}'.");
            }
        }

        if (chain.Count > 0 && context.Depth == 0)
        {
            throw new HostOperationException(
                "invalid_context",
                $"'{operation}' carries a call chain but reports depth 0; a nested execution must report its depth.");
        }

        var isCodeExecution = string.Equals(operation, ExtensionProtocol.CodeExecute, StringComparison.Ordinal);
        if (isCodeExecution)
        {
            // run_code is a top-level entry point only: neither a plugin nor an earlier script may nest another script.
            if (chain.Count > 0)
            {
                throw new HostOperationException(
                    "run_code_nested",
                    "run_code cannot be called from inside a plugin or a script; call the underlying tools directly.");
            }
        }
        else if (string.Equals(identity, RunCodeIdentity, StringComparison.Ordinal))
        {
            throw new HostOperationException(
                "reserved_identity",
                $"'{RunCodeIdentity}' is the reserved identity of run_code; no plugin may claim it.");
        }

        // The root token is the main program's authority; the host only checks its shape and refuses to invent one for
        // a nested call. A root call may omit it (older direct callers), and then its own run id is the root.
        string root;
        if (context.RootRunId is { } suppliedRoot)
        {
            if (!TryValidateRunId(suppliedRoot, out var rootProblem))
            {
                throw new HostOperationException("invalid_context", rootProblem);
            }

            root = suppliedRoot;
        }
        else if (chain.Count == 0)
        {
            root = context.RunId;
        }
        else
        {
            throw new HostOperationException(
                "invalid_context",
                $"'{operation}' is a nested call but carries no root run id; the host does not infer a root from plugin identities.");
        }

        return context.RootRunId is null ? context with { RootRunId = root } : context;
    }

    private static bool TryValidateRunId(string? runId, out string problem)
    {
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(runId))
        {
            problem = "The call carries no run id; unbound executions are refused.";
            return false;
        }

        if (runId.Length > MaxRunIdLength)
        {
            problem = $"The call carries a run id of {runId.Length} chars; the limit is {MaxRunIdLength}.";
            return false;
        }

        foreach (var character in runId)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                problem = "The call carries a run id with characters outside [A-Za-z0-9_-]; it is refused.";
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// One execution that is in flight in one worker session. It owns the immutable context the main program assigned, the
/// plugin identity that context belongs to, and a lifetime that is cancelled the moment the execution ends, so a
/// callback that was already being forwarded stops waiting on the main program instead of outliving its run.
/// </summary>
internal sealed class ActiveRun : IDisposable
{
    private readonly ActiveRunTable table;

    private readonly CancellationTokenSource lifetime = new();

    private int active = 1;

    internal ActiveRun(ActiveRunTable table, string runId, string pluginId, string root, ExtensionCallContext context)
    {
        this.table = table;
        RunId = runId;
        PluginId = pluginId;
        Root = root;
        Context = context;
    }

    public string RunId { get; }

    /// <summary>Plugin identity of this execution; a callback can never name another identity.</summary>
    public string PluginId { get; }

    /// <summary>Root run token this execution belongs to, used to refuse cross-root nesting deterministically.</summary>
    public string Root { get; }

    public ExtensionCallContext Context { get; }

    public CancellationToken Lifetime => lifetime.Token;

    public bool IsActive => Volatile.Read(ref active) != 0;

    public void End()
    {
        if (Interlocked.Exchange(ref active, 0) == 0)
        {
            return;
        }

        table.Remove(RunId);
        try
        {
            lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        lifetime.Dispose();
    }

    public void Dispose() => End();
}

/// <summary>
/// The executions in flight in one worker session, keyed by the run id the main program assigned. A callback resolves
/// here first: an id that was never registered in this session, belongs to another worker, or was ended is refused
/// before anything reaches the main program. The table dies with its session.
/// </summary>
internal sealed class ActiveRunTable
{
    private readonly Lock sync = new();

    private readonly Dictionary<string, ActiveRun> runs = new(StringComparer.Ordinal);

    public bool HasAny
    {
        get
        {
            lock (sync)
            {
                return runs.Count > 0;
            }
        }
    }

    /// <summary>Registers one execution. A duplicated run id is a binding failure, never a replacement.</summary>
    public ActiveRun Begin(string runId, string pluginId, string root, ExtensionCallContext context)
    {
        lock (sync)
        {
            if (runs.ContainsKey(runId))
            {
                throw new HostOperationException(
                    "duplicate_run_id",
                    $"The run id '{runId}' is already in flight in this worker; a run id may not be reused.");
            }

            var run = new ActiveRun(this, runId, pluginId, root, context);
            runs.Add(runId, run);
            return run;
        }
    }

    public ActiveRun? Find(string runId)
    {
        lock (sync)
        {
            return runs.TryGetValue(runId, out var run) && run.IsActive ? run : null;
        }
    }

    public void EndAll()
    {
        ActiveRun[] all;
        lock (sync)
        {
            if (runs.Count == 0)
            {
                return;
            }

            all = [.. runs.Values];
        }

        foreach (var run in all)
        {
            run.End();
        }
    }

    internal void Remove(string runId)
    {
        lock (sync)
        {
            runs.Remove(runId);
        }
    }
}

/// <summary>
/// Serves a worker's <c>host.tool_call</c> for one worker session. The only thing a worker may say is a
/// <see cref="HostToolCallRequest"/> (run id, tool name, raw argument JSON); the plugin identity comes from the registered
/// run (never from the message), and a run id that is not in flight in this session - unknown, ended, replayed, or
/// belonging to another worker - is refused before anything is forwarded. The forwarded call is cancelled with the run,
/// so an execution that ends takes its pending callback with it.
/// </summary>
internal static class BoundToolCallHandler
{
    public static async Task ServeAsync(
        WorkerProcess process,
        ActiveRunTable runs,
        IpcMessage message,
        Func<HostToolCallForward, CancellationToken, Task<ToolResult>> forwardToolCall,
        CancellationToken ct)
    {
        if (!string.Equals(message.Operation, ExtensionProtocol.HostToolCall, StringComparison.Ordinal))
        {
            await TryRespondErrorAsync(
                process,
                message.Id,
                new IpcError("unhandled", $"The extension host does not serve '{message.Operation}'."),
                ct).ConfigureAwait(false);
            return;
        }

        HostToolCallRequest call;
        try
        {
            call = HostArguments.Read<HostToolCallRequest>(message);
        }
        catch (HostOperationException exception)
        {
            await TryRespondErrorAsync(process, message.Id, new IpcError("unbound_tool_call", exception.Message), CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(call.RunId) || string.IsNullOrWhiteSpace(call.Name))
        {
            await TryRespondErrorAsync(
                process,
                message.Id,
                new IpcError(
                    "unbound_tool_call",
                    "The host.tool_call request carries no run id or no tool name; unbound calls are refused."),
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (string.Equals(call.Name, "run_code", StringComparison.Ordinal))
        {
            // No execution - plugin callback or script - may nest run_code; the main program enforces the same rule.
            await TryRespondErrorAsync(
                process,
                message.Id,
                new IpcError(
                    "run_code_nested",
                    "run_code cannot be called from inside a plugin or a script; call the underlying tools directly."),
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var run = runs.Find(call.RunId);
        if (run is null)
        {
            await TryRespondErrorAsync(
                process,
                message.Id,
                new IpcError(
                    "unbound_tool_call",
                    "The host.tool_call request is not bound to an execution that is in flight in this worker; it was refused."),
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Lifetime, ct);
            // The argument bytes are relayed untouched; only the endpoint that runs the tool parses them.
            var result = await forwardToolCall(
                    new HostToolCallForward(run.RunId, run.PluginId, call.Name, call.ArgumentsJson),
                    linked.Token)
                .ConfigureAwait(false);
            await process.Peer.RespondAsync(message.Id, result, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TryRespondErrorAsync(process, message.Id, new IpcError("cancelled", "The host tool call was cancelled."), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await TryRespondErrorAsync(process, message.Id, new IpcError("host_tool_call_failed", exception.Message), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task TryRespondErrorAsync(WorkerProcess process, long requestId, IpcError error, CancellationToken ct)
    {
        try
        {
            await process.Peer.RespondErrorAsync(requestId, error, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or IpcConnectionClosedException or OperationCanceledException)
        {
        }
    }
}
