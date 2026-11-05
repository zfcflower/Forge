using System.Text.Json;
using Forge.Approval;
using Forge.Ipc;
using Forge.Plugin.Sdk;
using Forge.Scripting;

namespace Forge.ExtensionHost;

/// <summary>
/// The resident host. It listens on the main-program pipe, discovers and compiles plugins, gates them on
/// <see cref="ApprovalEngine"/>, starts one worker process per plugin, publishes <c>tools.register</c> and routes
/// execution and plugin-to-main-program tool calls.
/// </summary>
public sealed class ExtensionHost : IAsyncDisposable
{
    private readonly HostOptions options;
    private readonly ApprovalStore approvals;
    private readonly PluginCatalog catalog;
    private readonly Dictionary<string, PluginEntry> plugins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkerSlot> workers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginState> states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Publication> publications = new(StringComparer.Ordinal);
    private readonly List<Task> retiring = [];
    private readonly List<PluginIssue> issues = [];
    private readonly Lock sync = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly HostCleanupReport cleanup = new();
    private readonly RootWaitGraph waits = new();
#if DEBUG
    private readonly IHostTestHook? testHook;
#endif
    private readonly ScriptCodeCompiler scriptCompiler = new();
    private readonly ScriptExecutorSupervisor scriptExecutors;
    private readonly HostStopSignal stop = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource connectionClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IIpcPeer? mainPeer;
    private int stopRequested;
    private int disposed;
    private int running;
    private int released;
    private int connectionUnsafe;
    private Task? shutdown;

    public ExtensionHost(HostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        Directory.CreateDirectory(options.DataDirectory);
        approvals = new ApprovalStore(options.DataDirectory);
        catalog = new PluginCatalog(options.PluginsDirectory);
        scriptExecutors = new ScriptExecutorSupervisor(options, scriptCompiler, ForwardToolCallAsync, stop, cleanup);
#if DEBUG
        testHook = FileHostTestHook.Activate(options.PipeName);
#endif
    }

    /// <summary>Every issue the last discovery reported, newest last.</summary>
    internal IReadOnlyList<PluginIssue> Issues
    {
        get
        {
            lock (sync)
            {
                return [.. issues];
            }
        }
    }

    /// <summary>Runs the host until <c>host.stop</c> or the main-program pipe closes.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Interlocked.Exchange(ref running, 1);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);

        var transport = new NamedPipeServerTransport(options.PipeName, new IpcPipeOptions { ConnectTimeout = options.ConnectTimeout });
        try
        {
            // No connection-wide deadline: a host.tool_call relay is bounded by the run's own lifetime (which ends with
            // its execution deadline), so a script with a 600 s budget is not cut to the old two-minute default.
            await using var peer = new IpcPeer(transport, new IpcPeerOptions { DefaultTimeout = Timeout.InfiniteTimeSpan });
            mainPeer = peer;
            peer.RequestReceived += OnRequestAsync;
            peer.ProtocolError += error => Console.Error.WriteLine($"forge-extension-host: dropped an inbound frame: {error.Message}");
            peer.Closed += _ => stopped.TrySetResult();
            try
            {
                await peer.StartAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or IpcTimeoutException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
            {
                Console.Error.WriteLine($"forge-extension-host: pipe '{options.PipeName}' could not be opened: {exception.Message}");
                await BeginShutdown().ConfigureAwait(false);
                return 1;
            }

            try
            {
                await RefreshAsync(linked.Token).ConfigureAwait(false);
                await stopped.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            // The fence goes up before anything is snapshotted or torn down, and before the main-program peer is closed:
            // from here on no path may start, load or recover a worker, so the snapshot below is complete. Every caller
            // that shuts the host down waits on the same drain, so none of them returns while another one is still killing
            // workers, and the drain is never entered twice.
            await BeginShutdown().ConfigureAwait(false);
        }
        finally
        {
            mainPeer = null;
            await transport.DisposeAsync().ConfigureAwait(false);
            connectionClosed.TrySetResult();
            ReleaseResources();
        }

        // A known cleanup failure is not a clean exit: the exit code tells the caller the host could not verify that every
        // worker process tree was gone. Returning 0 here would claim a clean stop the host did not observe.
        return cleanup.HasFailures ? 1 : 0;
    }

    /// <summary>
    /// Shuts the host down and waits for the whole shutdown: the worker drain, the close of the main-program pipe and the
    /// release of the approval store. Calling it twice, or concurrently with <see cref="RunAsync"/>'s own teardown, never
    /// returns early: every caller awaits the same completion, so a caller that disposed the host cannot go on to observe
    /// a host that is still serving frames or still killing a worker.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);

        // Stopping is irrevocable and comes first: every path that could still accept a call, start a worker or recover a
        // crashed one rechecks this fence, so what follows drains a set that cannot grow any more.
        var drain = BeginShutdown();
        await lifetime.CancelAsync().ConfigureAwait(false);
        await drain.ConfigureAwait(false);

        // RunAsync owns the pipe teardown; waiting for it here is what makes "disposed" mean the host answers nothing.
        if (Volatile.Read(ref running) != 0)
        {
            try
            {
                await connectionClosed.Task.WaitAsync(ConnectionCloseWindow).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                cleanup.Record(
                    $"the main-program pipe did not close within the {ConnectionCloseWindow.TotalSeconds:0} s shutdown window; "
                    + "the host may still be answering calls");
            }
        }

        ReleaseResources();

        // Disposing is not allowed to return quietly when the host could not verify that it tore everything down: a caller
        // that waited for DisposeAsync must not believe the workers are gone when they are not.
        if (cleanup.HasFailures)
        {
            throw new HostOperationException(
                "host_shutdown_failed",
                "The extension host could not verify a clean shutdown: " + cleanup.Describe());
        }
    }

    private async Task OnRequestAsync(IpcMessage message, CancellationToken ct)
    {
        if (mainPeer is not { } peer)
        {
            return;
        }

        // An operation that was refused because the fence was already up is answered normally (the caller learns why). An
        // operation that was already running and was aborted <em>by</em> the shutdown is not answered at all: the host is
        // tearing the connection down and cannot vouch for a reply, so the close that follows is the only truthful answer,
        // and the caller sees the connection end rather than a result from a host that is gone.
        var stoppingWhenDispatched = stop.IsStopping;
        JsonElement result;
        try
        {
            result = await DispatchAsync(message, ct).ConfigureAwait(false);
        }
        catch (HostOperationException exception)
        {
            if (!stoppingWhenDispatched && string.Equals(exception.Code, "host_stopping", StringComparison.Ordinal))
            {
                return;
            }

            await RespondErrorAsync(peer, message.Id, exception.Code, exception.Message).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            if (stop.IsStopping)
            {
                return;
            }

            await RespondErrorAsync(peer, message.Id, "cancelled", $"'{message.Operation}' was cancelled.").ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            await RespondErrorAsync(peer, message.Id, "handler_error", exception.Message).ConfigureAwait(false);
            return;
        }

        if (ConnectionUnsafe)
        {
            // The connection is no longer trustworthy (a frame was half written); replying would write onto a stream whose
            // framing may already be broken. The close that follows is the only truthful answer.
            return;
        }

        try
        {
            await peer.RespondAsync(message.Id, result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or IpcConnectionClosedException)
        {
        }

        if (Interlocked.Exchange(ref stopRequested, 0) != 0)
        {
            stopped.TrySetResult();
        }
    }

    private Task<JsonElement> DispatchAsync(IpcMessage message, CancellationToken ct)
    {
        // Everything except the stop request itself is refused once shutdown has begun, instead of racing the teardown
        // for a worker slot. The stop operation must stay answerable so the caller can finish its own shutdown.
        if (!string.Equals(message.Operation, ExtensionProtocol.HostStop, StringComparison.Ordinal))
        {
            stop.ThrowIfStopping($"'{message.Operation}'");
        }

        switch (message.Operation)
        {
            case ExtensionProtocol.PluginExecute:
                return ExecuteAsync(HostJson.Read<PluginExecuteRequest>(message), ct);
            case ExtensionProtocol.PluginApprove:
                return ApproveAsync(HostJson.Read<PluginApproveRequest>(message), ct);
            case ExtensionProtocol.PluginRevoke:
                return RevokeAsync(HostJson.Read<PluginRevokeRequest>(message), ct);
            case ExtensionProtocol.PluginsRefresh:
                return RefreshAsync(ct);
            case ExtensionProtocol.PluginsStatus:
                return StatusAsync(ct);
            case ExtensionProtocol.CodeExecute:
                return CodeExecuteAsync(HostJson.Read<CodeExecutionRequest>(message), ct);
            case ExtensionProtocol.PluginCommand:
                return CommandAsync(HostJson.Read<CommandExecutionRequest>(message), ct);
            case ExtensionProtocol.PluginPrompt:
                return PromptAsync(HostJson.Read<PromptExecutionRequest>(message), ct);
            case ExtensionProtocol.HostStop:
                Interlocked.Exchange(ref stopRequested, 1);

                // The fence goes up now, not after the answer is written: a load, a spawn or a recovery that is already in
                // flight (the startup refresh is the one that matters) is cancelled the moment the stop is received, so
                // host.stop cannot be answered by a host that is still waiting out a connect or a load window.
                stop.Begin();
                return Task.FromResult(HostJson.EmptyObject);
            default:
                throw new HostOperationException("unsupported_operation", $"The extension host does not serve '{message.Operation}'.");
        }
    }

    private async Task<JsonElement> ExecuteAsync(PluginExecuteRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PluginId) || string.IsNullOrWhiteSpace(request.Tool))
        {
            throw new HostOperationException("invalid_request", "pluginId and tool are required.");
        }

        var context = CallBinding.ResolveForPluginExecute(request);
        var nested = context.PluginChain.Count > 0;

        // One absolute deadline for the whole call. The gate wait, the re-hash/re-approval, the worker load and the queue
        // for an execution slot all draw from it; the worker is handed only what is left, never a fresh timer.
        using var budget = new CallDeadline(
            PluginCallLimits.ToolTimeout(options, request.TimeoutMilliseconds), ct, stop.Token);

        var captured = Find(request.PluginId) ??
            throw new HostOperationException("unknown_plugin", $"No plugin '{request.PluginId}' was discovered.");
        var state = StateOf(captured.Id);
        await EnterCallAsync(captured, state, context, nested, budget, ct, "the tool call").ConfigureAwait(false);
        WorkerSlot slot;
        PluginState.Lease lease;
        try
        {
            var entry = Current(captured);
            var evaluation = await EvaluateAsync(entry, budget.Token).ConfigureAwait(false);
            RequireAllowed(entry, evaluation.Decision);
            if (evaluation.Image is not { } image)
            {
                throw new HostOperationException("plugin_not_loadable", $"Plugin '{entry.Id}' {entry.Version} has no loadable assembly.");
            }

            slot = GetWorker(entry);
            var loadout = await slot.EnsureLoadedAsync(entry.Version, image, entry.Manifest.Permissions, budget.Token).ConfigureAwait(false);
            entry.Tools = loadout.Tools;
            entry.Contributions = loadout.Contributions;

            var rechecked = ApprovalEngine.Evaluate(Candidate(entry, evaluation), approvals);
            entry.Decision = rechecked;
            RequireAllowed(entry, rechecked);

            await PublishAsync(entry.Id, loadout, budget.Token).ConfigureAwait(false);
            if (!Contains(loadout.Tools, request.Tool))
            {
                throw new HostOperationException("unknown_tool", $"Plugin '{entry.Id}' does not declare a tool named '{request.Tool}'.");
            }

            lease = state.BeginExecution(entry.Id, CallBinding.RootOf(context), nested);
        }
        catch (OperationCanceledException) when (BudgetEndedBeforeDispatch(budget, ct))
        {
            throw BudgetEnded("the tool call", budget);
        }
        finally
        {
            state.Exit();
        }

        using (lease)
        {
            var result = await slot.ExecuteAsync(request with { Context = context }, budget, ct).ConfigureAwait(false);
            return HostJson.ToElement(new PluginExecuteResponse(result.Content, result.IsError));
        }
    }

    /// <summary>
    /// Runs one contributed command. It passes exactly the checks a tool execution passes - the current approved image is
    /// re-hashed and re-approved, the worker is loaded only from those bytes, and a worker crash or timeout charges the
    /// plugin's failure budget - and only then reaches the command body.
    /// </summary>
    private async Task<JsonElement> CommandAsync(CommandExecutionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PluginId) || string.IsNullOrWhiteSpace(request.Command))
        {
            throw new HostOperationException("invalid_request", "pluginId and command are required.");
        }

        var context = CallBinding.Require(request.Context, request.PluginId, ExtensionProtocol.PluginCommand);
        var nested = context.PluginChain.Count > 0;
        using var budget = new CallDeadline(
            PluginCallLimits.RequestTimeout(options, request.TimeoutMilliseconds), ct, stop.Token);
        var captured = Find(request.PluginId) ??
            throw new HostOperationException("unknown_plugin", $"No plugin '{request.PluginId}' was discovered.");

        var state = StateOf(captured.Id);
        await EnterCallAsync(captured, state, context, nested, budget, ct, "the command call").ConfigureAwait(false);
        WorkerSlot slot;
        PluginState.Lease lease;
        try
        {
            var entry = Current(captured);
            var evaluation = await EvaluateAsync(entry, budget.Token).ConfigureAwait(false);
            RequireAllowed(entry, evaluation.Decision);
            if (evaluation.Image is not { } image)
            {
                throw new HostOperationException("plugin_not_loadable", $"Plugin '{entry.Id}' {entry.Version} has no loadable assembly.");
            }

            slot = GetWorker(entry);
            var loadout = await slot.EnsureLoadedAsync(entry.Version, image, entry.Manifest.Permissions, budget.Token).ConfigureAwait(false);
            entry.Tools = loadout.Tools;
            entry.Contributions = loadout.Contributions;

            var rechecked = ApprovalEngine.Evaluate(Candidate(entry, evaluation), approvals);
            entry.Decision = rechecked;
            RequireAllowed(entry, rechecked);

            await PublishAsync(entry.Id, loadout, budget.Token).ConfigureAwait(false);
            if (!ContainsCommand(loadout.Contributions.Commands, request.Command))
            {
                throw new HostOperationException(
                    "unknown_command",
                    $"Plugin '{entry.Id}' does not declare an executable command named '{request.Command}'.");
            }

            lease = state.BeginExecution(entry.Id, CallBinding.RootOf(context), nested);
        }
        catch (OperationCanceledException) when (BudgetEndedBeforeDispatch(budget, ct))
        {
            throw BudgetEnded("the command call", budget);
        }
        finally
        {
            state.Exit();
        }

        using (lease)
        {
            var output = await slot.ExecuteCommandAsync(request with { Context = context }, budget, ct).ConfigureAwait(false);
            return HostJson.ToElement(output);
        }
    }

    /// <summary>Evaluates one prompt contributor under the same approved-image, timeout and failure-budget rules.</summary>
    private async Task<JsonElement> PromptAsync(PromptExecutionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PluginId) || string.IsNullOrWhiteSpace(request.ContributorId))
        {
            throw new HostOperationException("invalid_request", "pluginId and contributorId are required.");
        }

        var context = CallBinding.Require(request.Context, request.PluginId, ExtensionProtocol.PluginPrompt);
        var nested = context.PluginChain.Count > 0;
        using var budget = new CallDeadline(
            PluginCallLimits.RequestTimeout(options, request.TimeoutMilliseconds), ct, stop.Token);
        var captured = Find(request.PluginId) ??
            throw new HostOperationException("unknown_plugin", $"No plugin '{request.PluginId}' was discovered.");

        var state = StateOf(captured.Id);
        await EnterCallAsync(captured, state, context, nested, budget, ct, "the prompt call").ConfigureAwait(false);
        WorkerSlot slot;
        PluginState.Lease lease;
        try
        {
            var entry = Current(captured);
            var evaluation = await EvaluateAsync(entry, budget.Token).ConfigureAwait(false);
            RequireAllowed(entry, evaluation.Decision);
            if (evaluation.Image is not { } image)
            {
                throw new HostOperationException("plugin_not_loadable", $"Plugin '{entry.Id}' {entry.Version} has no loadable assembly.");
            }

            slot = GetWorker(entry);
            var loadout = await slot.EnsureLoadedAsync(entry.Version, image, entry.Manifest.Permissions, budget.Token).ConfigureAwait(false);
            entry.Tools = loadout.Tools;
            entry.Contributions = loadout.Contributions;

            var rechecked = ApprovalEngine.Evaluate(Candidate(entry, evaluation), approvals);
            entry.Decision = rechecked;
            RequireAllowed(entry, rechecked);

            await PublishAsync(entry.Id, loadout, budget.Token).ConfigureAwait(false);
            if (!ContainsPrompt(loadout.Contributions.PromptContributors, request.ContributorId))
            {
                throw new HostOperationException(
                    "unknown_prompt",
                    $"Plugin '{entry.Id}' does not declare a prompt contributor named '{request.ContributorId}'.");
            }

            lease = state.BeginExecution(entry.Id, CallBinding.RootOf(context), nested);
        }
        catch (OperationCanceledException) when (BudgetEndedBeforeDispatch(budget, ct))
        {
            throw BudgetEnded("the prompt call", budget);
        }
        finally
        {
            state.Exit();
        }

        using (lease)
        {
            var output = await slot.EvaluatePromptAsync(request with { Context = context }, budget, ct).ConfigureAwait(false);
            return HostJson.ToElement(output);
        }
    }

    /// <summary>
    /// Runs one script on a leased script worker. The context must be the main program's bound context, run_code is a
    /// top-level entry point only, and the budget (queue + compile + run) is preserved up to the documented maximum
    /// instead of being cut to the old two-minute IPC default.
    /// </summary>
    private async Task<JsonElement> CodeExecuteAsync(CodeExecutionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            throw new HostOperationException("invalid_request", "code.execute requires a non-empty script.");
        }

        var context = CallBinding.Require(request.Context, CallBinding.RunCodeIdentity, ExtensionProtocol.CodeExecute);
        var result = await scriptExecutors
            .ExecuteAsync(request with { Context = context }, ct)
            .ConfigureAwait(false);
        return HostJson.ToElement(result);
    }

    /// <summary>
    /// Reports every discovered plugin from the catalog, the approval records (read fresh from the store, so an external
    /// revoke shows up) and a pure evaluation of the discovered hashes. It never loads or runs plugin code, so an
    /// unapproved plugin's permissions and errors can be shown before the user approves anything.
    /// </summary>
    private Task<JsonElement> StatusAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PluginEntry[] entries;
        lock (sync)
        {
            entries = [.. plugins.Values];
        }

        Array.Sort(entries, static (left, right) => string.CompareOrdinal(left.Id, right.Id));
        var statuses = new List<PluginStatusInfo>(entries.Length);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var record = approvals.Get(entry.Id, entry.Version);
            var decision = ApprovalEngine.Evaluate(
                new PluginRecord
                {
                    PluginId = entry.Id,
                    Version = entry.Version,
                    SourceHash = entry.SourceHash,
                    DependencyHash = entry.DependencyHash,
                    SdkVersion = entry.SdkVersion,
                },
                record);
            var status = entry.HasContent
                ? (record?.Status ?? PluginStatus.Draft).ToString().ToLowerInvariant()
                : "invalid";
            // Discovery/load failures are the errors; a stale approval (content no longer matching the record) is
            // reported as an error too. A plain draft is not an error - its status already says it is not approved.
            //
            // A manifest that changed on disk since discovery is reported as invalidated/needs-refresh even while the
            // persisted record still says "approved": that approval describes the discovery snapshot, and the next run or
            // approval would fail closed. Detecting it only reads manifest.json (the same cheap check the approval gate
            // uses) - it never loads or executes plugin code and never invents current content hashes. The record is left
            // untouched, so a refresh can re-derive the hashes and the user re-approves explicitly; status never grants.
            var error = FirstIssueMessage(entry) ??
                (ManifestChangedOnDisk(entry)
                    ? $"the manifest of {entry.Id} {entry.Version} changed on disk since discovery; refresh before "
                      + "approving or running it (the hashes reported here are the discovery snapshot, not the disk)"
                    : record is not null && record.Status != PluginStatus.Draft && !decision.IsAllowed
                        ? decision.Reason
                        : null);
            statuses.Add(new PluginStatusInfo(
                entry.Id,
                entry.Version,
                status,
                entry.SourceHash,
                entry.DependencyHash,
                entry.SdkVersion,
                [.. entry.Manifest.Permissions],
                error));
        }

        return Task.FromResult(HostJson.ToElement(new PluginStatusResponse(statuses)));
    }

    private static string? FirstIssueMessage(PluginEntry entry) =>
        entry.Issues.Count == 0 ? null : string.Join("; ", entry.Issues.Select(static issue => issue.Message));

    /// <summary>
    /// Enters one call: the Debug-only capture seam, then the plugin's dispatch gate, both under the call's absolute
    /// deadline. A deadline that expires here is reported as a queue timeout - the call never reached a worker - while
    /// the caller's own cancellation and the shutdown fence keep their existing meaning.
    /// </summary>
    private async Task EnterCallAsync(
        PluginEntry captured,
        PluginState state,
        ExtensionCallContext context,
        bool nested,
        CallDeadline budget,
        CancellationToken ct,
        string what)
    {
        try
        {
            await CaptureAsync(captured.Id, budget.Token).ConfigureAwait(false);
            await EnterPluginAsync(state, context, nested, captured.Id, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (BudgetEndedBeforeDispatch(budget, ct))
        {
            throw BudgetEnded(what, budget);
        }
    }

    /// <summary>True when a cancellation in the pre-dispatch section is the call's own budget expiring, not the caller
    /// cancelling and not the shutdown fence - only that case is reported as <c>plugin_queue_timeout</c>.</summary>
    private bool BudgetEndedBeforeDispatch(CallDeadline budget, CancellationToken ct) =>
        !ct.IsCancellationRequested && budget.Expired && !stop.IsStopping;

    /// <summary>The call's budget ended before anything was dispatched. Nothing the call had already dispatched is killed
    /// and the plugin's failure budget is untouched: a plugin that never ran for this call is not at fault.</summary>
    private static HostOperationException BudgetEnded(string what, CallDeadline budget) =>
        new(
            "plugin_queue_timeout",
            $"{what} was not dispatched: its {budget.Seconds} s budget ended during admission (waiting for the plugin, "
            + "re-hashing its content or loading its worker). Nothing this call had dispatched was killed and no failure "
            + "was charged.");

    /// <summary>
    /// Takes the plugin's dispatch gate for one execution. A top-level call (and every approval change) takes it
    /// exclusively and waits its turn; a nested call is admitted only when its own root already owns the gate - siblings of
    /// one root then run their hash/load/publish sections side by side and queue for a worker slot afterwards - and is
    /// refused at once with <c>plugin_busy</c> when an approval change, a refresh, a load of a different root or a queued
    /// exclusive caller holds or wants the gate. Refusing instead of waiting there is what keeps two call chains from
    /// blocking on each other, and admitting a same-root sibling instead of refusing it is what keeps one script's fan-out
    /// from failing against itself.
    /// </summary>
    private static async Task EnterPluginAsync(
        PluginState state,
        ExtensionCallContext context,
        bool nested,
        string pluginId,
        CancellationToken ct)
    {
        if (!nested)
        {
            await state.EnterAsync(ct).ConfigureAwait(false);
            return;
        }

        if (!state.TryEnterNested(CallBinding.RootOf(context)))
        {
            throw new HostOperationException(
                "plugin_busy",
                $"Plugin '{pluginId}' is busy with an approval change, a refresh or an execution of another root; a nested call does not wait for it. Retry after the other call finishes.");
        }
    }

    private async Task<JsonElement> ApproveAsync(PluginApproveRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PluginId))
        {
            throw new HostOperationException("invalid_request", "pluginId is required.");
        }

        var captured = Find(request.PluginId) ??
            throw new HostOperationException("unknown_plugin", $"No plugin '{request.PluginId}' was discovered.");

        var state = StateOf(captured.Id);
        await state.EnterAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = Current(captured);
            var version = string.IsNullOrWhiteSpace(request.Version) ? entry.Version : request.Version;
            if (!string.Equals(version, entry.Version, StringComparison.Ordinal))
            {
                throw new HostOperationException(
                    "version_mismatch",
                    $"Plugin '{entry.Id}' is installed at version '{entry.Version}', not '{version}'.");
            }

            if (!entry.HasContent)
            {
                throw new HostOperationException(
                    "plugin_not_loadable",
                    $"Plugin '{entry.Id}' {entry.Version} has no compiled content to approve.");
            }

            var sourceHash = string.IsNullOrWhiteSpace(request.SourceHash) ? entry.SourceHash : request.SourceHash;
            var dependencyHash = string.IsNullOrWhiteSpace(request.DependencyHash) ? entry.DependencyHash : request.DependencyHash;
            var sdkVersion = string.IsNullOrWhiteSpace(request.SdkVersion) ? entry.SdkVersion : request.SdkVersion;
            if (await RefusalReasonAsync(entry, sourceHash, dependencyHash, sdkVersion, ct).ConfigureAwait(false) is { } refusal)
            {
                WithdrawUnusableApproval(entry.Id, version);
                throw new HostOperationException(
                    "approval_mismatch",
                    $"The approval does not cover the current content of '{entry.Id}': {refusal}.");
            }

            approvals.SetApproved(entry.Id, version, sourceHash, dependencyHash, sdkVersion);
            await ApplyCoreAsync(entry, ct).ConfigureAwait(false);

            if (!entry.Decision.IsAllowed)
            {
                // The record was written from the hashes the caller sent, but the content re-read under the gate no
                // longer matches (for example the plugin directory changed between SetApproved and ApplyCoreAsync). It
                // must not stay Approved: a record that cannot cover what a run would load is exactly the
                // "looks approved, refuses to run" state this path exists to prevent. The withdrawal is hash-conditional
                // under the plugin gate, so a record a different writer legitimately re-approved in the meantime (newer
                // hashes or a non-Approved status) is left alone.
                WithdrawApprovalWrittenBy(entry.Id, version, sourceHash, dependencyHash, sdkVersion);
                throw new HostOperationException(
                    "approval_mismatch",
                    $"The approval does not cover the current content of '{entry.Id}': {entry.Decision.Reason}.");
            }
        }
        finally
        {
            state.Exit();
        }

        return HostJson.EmptyObject;
    }

    /// <summary>
    /// Compares the approval the main program sent with the plugin's content on disk now. An approval may only be
    /// recorded when its hashes describe exactly the bytes a run would load, so this runs before anything is persisted.
    /// Returns the reason the approval does not cover the content, or <c>null</c> when it does.
    /// </summary>
    private async Task<string?> RefusalReasonAsync(
        PluginEntry entry,
        string sourceHash,
        string dependencyHash,
        string sdkVersion,
        CancellationToken ct)
    {
        if (approvals.Get(entry.Id, entry.Version) is { Status: PluginStatus.Disabled or PluginStatus.Uninstalled } blocked)
        {
            return $"plugin {entry.Id} {entry.Version} is {blocked.Status.ToString().ToLowerInvariant()}";
        }

        PluginContent content;
        try
        {
            content = await PluginCatalog.SnapshotAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"the content of {entry.Id} {entry.Version} could not be read: {exception.Message}";
        }

        if (!string.Equals(content.SourceHash, sourceHash, StringComparison.Ordinal))
        {
            return "the source hash does not match the content on disk";
        }

        if (!string.Equals(content.DependencyHash, dependencyHash, StringComparison.Ordinal))
        {
            return "the dependency hash does not match the files on disk";
        }

        if (!string.Equals(entry.SdkVersion, sdkVersion, StringComparison.Ordinal))
        {
            return $"the plugin is installed against SDK '{entry.SdkVersion}', not '{sdkVersion}'";
        }

        if (ManifestChangedOnDisk(entry))
        {
            return "the manifest changed on disk (dependencies, permissions or the entry assembly); refresh before approving the plugin";
        }

        return null;
    }

    /// <summary>
    /// True when <c>manifest.json</c> on disk no longer declares what the discovery snapshot says it does: the manifest
    /// dependencies, the declared permissions or the entry assembly differ, or the manifest cannot be read at all.
    /// <para>
    /// The manifest is not part of the loadable image and the approval hash is computed from the discovery snapshot, so
    /// without this check an approval - and every execution - would be evaluated against metadata that is no longer on
    /// disk. Adding a dependency and running without a refresh is the case that matters most: the dependency hash covers
    /// the dependencies the host knows about, so a new one has to force a refresh and a new approval even when nothing
    /// loads that dependency yet. The comparison is fail-closed: an unreadable manifest counts as changed.
    /// </para>
    /// <para>
    /// Permissions and dependencies are compared as the semantic tuple sets the hash folds: sorted ordinally by their
    /// canonical (name, version) / (kind, scope) pairs, because the dependency hash is order-insensitive too and a
    /// reordering of the same declarations changes nothing an approval covers.
    /// </para>
    /// </summary>
    private static bool ManifestChangedOnDisk(PluginEntry entry)
    {
        if (!PluginManifestReader.TryRead(entry.Directory, out var onDisk, out _, out _))
        {
            return true;
        }

        return !SamePermissions(entry.Manifest.Permissions, onDisk.Permissions) ||
            !SameDependencies(entry.Manifest.Dependencies, onDisk.Dependencies) ||
            !string.Equals(entry.Manifest.EntryAssembly, onDisk.EntryAssembly, StringComparison.Ordinal);
    }

    private static bool SamePermissions(IReadOnlyList<Permission> stored, IReadOnlyList<Permission> current)
    {
        if (stored.Count != current.Count)
        {
            return false;
        }

        var left = new Permission[stored.Count];
        var right = new Permission[current.Count];
        for (var index = 0; index < stored.Count; index++)
        {
            left[index] = stored[index];
            right[index] = current[index];
        }

        Array.Sort(left, ComparePermissions);
        Array.Sort(right, ComparePermissions);
        for (var index = 0; index < left.Length; index++)
        {
            if (left[index].Kind != right[index].Kind ||
                !string.Equals(left[index].Scope, right[index].Scope, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameDependencies(IReadOnlyList<Dependency> stored, IReadOnlyList<Dependency> current)
    {
        if (stored.Count != current.Count)
        {
            return false;
        }

        var left = new Dependency[stored.Count];
        var right = new Dependency[current.Count];
        for (var index = 0; index < stored.Count; index++)
        {
            left[index] = stored[index];
            right[index] = current[index];
        }

        Array.Sort(left, CompareDependencies);
        Array.Sort(right, CompareDependencies);
        for (var index = 0; index < left.Length; index++)
        {
            if (!string.Equals(left[index].Name, right[index].Name, StringComparison.Ordinal) ||
                !string.Equals(left[index].Version, right[index].Version, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static int ComparePermissions(Permission left, Permission right)
    {
        var byKind = left.Kind.CompareTo(right.Kind);
        return byKind != 0 ? byKind : string.CompareOrdinal(left.Scope, right.Scope);
    }

    private static int CompareDependencies(Dependency left, Dependency right)
    {
        var byName = string.CompareOrdinal(left.Name, right.Name);
        return byName != 0 ? byName : string.CompareOrdinal(left.Version, right.Version);
    }

    /// <summary>
    /// Withdraws an approval this call itself just wrote, and only that one: the record must still be
    /// <see cref="PluginStatus.Approved"/> and still carry exactly the source, dependency and SDK hashes passed in. If
    /// either changed, someone else wrote a newer decision (the store is refreshed from disk on every read) and that
    /// decision is not this call's to undo. Runs under the plugin's gate, so no in-process approval change can interleave.
    /// </summary>
    private void WithdrawApprovalWrittenBy(
        string pluginId,
        string version,
        string sourceHash,
        string dependencyHash,
        string sdkVersion)
    {
        if (approvals.Get(pluginId, version) is not { Status: PluginStatus.Approved } record)
        {
            return;
        }

        if (!string.Equals(record.SourceHash, sourceHash, StringComparison.Ordinal) ||
            !string.Equals(record.DependencyHash, dependencyHash, StringComparison.Ordinal) ||
            !string.Equals(record.SdkVersion, sdkVersion, StringComparison.Ordinal))
        {
            return;
        }

        approvals.Revoke(pluginId, version);
    }

    /// <summary>Returns a record whose status claims approval for content its own hashes no longer describe to
    /// <see cref="PluginStatus.Draft"/>; a record that still holds a usable approval is left untouched.</summary>
    private void WithdrawUnusableApproval(string pluginId, string version)
    {
        if (approvals.Get(pluginId, version) is not { Status: PluginStatus.Approved } record)
        {
            return;
        }

        var decision = ApprovalEngine.Evaluate(
            new PluginRecord
            {
                PluginId = record.PluginId,
                Version = record.Version,
                SourceHash = record.SourceHash,
                DependencyHash = record.DependencyHash,
                SdkVersion = record.SdkVersion,
            },
            record);
        if (!decision.IsAllowed)
        {
            approvals.Revoke(pluginId, version);
        }
    }

    private async Task<JsonElement> RevokeAsync(PluginRevokeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PluginId))
        {
            throw new HostOperationException("invalid_request", "pluginId is required.");
        }

        var captured = Find(request.PluginId) ??
            throw new HostOperationException("unknown_plugin", $"No plugin '{request.PluginId}' was discovered.");

        var state = StateOf(captured.Id);
        await state.EnterAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = Current(captured);
            var version = string.IsNullOrWhiteSpace(request.Version) ? entry.Version : request.Version;
            if (!string.Equals(version, entry.Version, StringComparison.Ordinal))
            {
                throw new HostOperationException(
                    "version_mismatch",
                    $"Plugin '{entry.Id}' is installed at version '{entry.Version}', not '{version}'.");
            }

            approvals.Revoke(entry.Id, version);
            await UnregisterAllAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);

            // Killing the worker is what ends the executions already in flight; draining them afterwards guarantees
            // that nothing this plugin dispatched is still running when the revoke returns, so no worker of a revoked
            // plugin can be left behind and no later call can be recovered into a new one.
            await StopWorkerAsync(entry.Id).ConfigureAwait(false);
            await state.WaitForIdleAsync(RevokeDrainWindow, ct).ConfigureAwait(false);
            await ApplyCoreAsync(entry, ct).ConfigureAwait(false);
        }
        finally
        {
            state.Exit();
        }

        return HostJson.EmptyObject;
    }

    private async Task<JsonElement> RefreshAsync(CancellationToken ct)
    {
        await refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var scan = await catalog.DiscoverAsync(ct).ConfigureAwait(false);
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in scan.Plugins)
            {
                present.Add(entry.Id);
                var state = StateOf(entry.Id);
                await state.EnterAsync(ct).ConfigureAwait(false);
                try
                {
                    SyncRecord(entry);
                    lock (sync)
                    {
                        plugins[entry.Id] = entry;
                    }

                    await ApplyLockedAsync(entry, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    entry.Issues.Add(new PluginIssue(entry.Id, PluginIssueKind.InvalidPackage, exception.Message, entry.Directory));
                }
                finally
                {
                    state.Exit();
                }
            }

            foreach (var id in KnownPluginIds())
            {
                if (present.Contains(id))
                {
                    continue;
                }

                var state = StateOf(id);
                await state.EnterAsync(ct).ConfigureAwait(false);
                try
                {
                    lock (sync)
                    {
                        plugins.Remove(id);
                    }

                    await UnregisterAllAsync(id, ct).ConfigureAwait(false);
                    await StopWorkerAsync(id).ConfigureAwait(false);
                }
                finally
                {
                    state.Exit();
                }
            }

            lock (sync)
            {
                issues.Clear();
                var reported = new HashSet<PluginIssue>();
                foreach (var issue in scan.Issues)
                {
                    if (reported.Add(issue))
                    {
                        issues.Add(issue);
                    }
                }

                foreach (var entry in scan.Plugins)
                {
                    foreach (var issue in entry.Issues)
                    {
                        if (reported.Add(issue))
                        {
                            issues.Add(issue);
                        }
                    }
                }
            }

            foreach (var issue in Issues)
            {
                Console.Error.WriteLine("forge-extension-host: " + issue);
            }

            return HostJson.EmptyObject;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>Re-evaluates approval for one plugin and brings its worker and tool registration in line with the result,
    /// serialized against the plugin's executions and its other approval changes. A refresh also lifts an exhausted
    /// failure budget: a disabled slot and its published tools are dropped first, so the reload starts a fresh worker
    /// and registers its tools again. The caller holds the plugin's gate.</summary>
    private async Task ApplyLockedAsync(PluginEntry entry, CancellationToken ct)
    {
        var state = StateOf(entry.Id);
        if (state.IsExhausted)
        {
            await ForgetRegistrationAsync(entry.Id, ct).ConfigureAwait(false);
            await StopWorkerAsync(entry.Id).ConfigureAwait(false);
        }

        state.ResetFailures();
        await ApplyCoreAsync(entry, ct).ConfigureAwait(false);
    }

    private async Task ApplyCoreAsync(PluginEntry entry, CancellationToken ct)
    {
        var evaluation = await EvaluateAsync(entry, ct).ConfigureAwait(false);
        if (!evaluation.Decision.IsAllowed || evaluation.Image is not { } image)
        {
            await UnregisterAllAsync(entry.Id, ct).ConfigureAwait(false);
            await StopWorkerAsync(entry.Id).ConfigureAwait(false);
            return;
        }

        try
        {
            var slot = GetWorker(entry);
            var loadout = await slot
                .EnsureLoadedAsync(entry.Version, image, entry.Manifest.Permissions, ct)
                .ConfigureAwait(false);
            entry.Tools = loadout.Tools;
            entry.Contributions = loadout.Contributions;
            await PublishAsync(entry.Id, loadout, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HostOperationException or IpcTimeoutException or IpcConnectionClosedException or IOException)
        {
            entry.Issues.Add(new PluginIssue(entry.Id, PluginIssueKind.LoadFailed, exception.Message, entry.Directory));
            Console.Error.WriteLine($"forge-extension-host: {entry.Id} ({PluginIssueKind.LoadFailed}): {exception.Message}");
            await UnregisterAllAsync(entry.Id, ct).ConfigureAwait(false);
            await StopWorkerAsync(entry.Id).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the plugin's approval candidate from the bytes on disk now. The image is re-hashed and all of its file
    /// digests are folded into the dependency hash, so an approval only holds for exactly the files a call would load:
    /// a replaced artifact, a replaced private dependency or an edited source all need a refresh and a new approval.
    /// </summary>
    private async Task<PluginEvaluation> EvaluateAsync(PluginEntry entry, CancellationToken ct)
    {
        if (!entry.HasContent)
        {
            var blocked = ApprovalDecision.Block($"plugin {entry.Id} {entry.Version} has no loadable content");
            entry.Decision = blocked;
            return new PluginEvaluation(blocked, null, entry.SourceHash, entry.DependencyHash);
        }

        PluginContent content;
        try
        {
            content = await PluginCatalog.SnapshotAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var unreadable = ApprovalDecision.Require(
                $"the content of {entry.Id} {entry.Version} could not be read: {exception.Message}");
            entry.Decision = unreadable;
            return new PluginEvaluation(unreadable, null, entry.SourceHash, entry.DependencyHash);
        }

        var decision = string.Equals(content.SourceHash, entry.SourceHash, StringComparison.Ordinal) &&
            !ManifestChangedOnDisk(entry)
            ? ApprovalEngine.Evaluate(
                new PluginRecord
                {
                    PluginId = entry.Id,
                    Version = entry.Version,
                    SourceHash = content.SourceHash,
                    DependencyHash = content.DependencyHash,
                    SdkVersion = entry.SdkVersion,
                },
                approvals)
            : ApprovalDecision.Require(
                $"the content or the manifest of {entry.Id} {entry.Version} changed on disk; refresh before running it");

        entry.Decision = decision;
        return new PluginEvaluation(decision, content.Image, content.SourceHash, content.DependencyHash);
    }

    /// <summary>
    /// Keeps the persisted record pointing at the content that is on disk now. A plugin the store does not know yet gets
    /// a <see cref="PluginStatus.Draft"/> record, which is how the main program learns the hashes it has to approve; an
    /// existing record keeps its status and <see cref="PluginRecord.ApprovedHash"/>, so a changed source or dependency
    /// hash makes the approval stop matching instead of silently re-approving it.
    /// </summary>
    private void SyncRecord(PluginEntry entry)
    {
        if (!entry.HasContent)
        {
            return;
        }

        var record = approvals.Get(entry.Id, entry.Version);
        if (record is null)
        {
            approvals.Upsert(new PluginRecord
            {
                PluginId = entry.Id,
                Version = entry.Version,
                SourceHash = entry.SourceHash,
                DependencyHash = entry.DependencyHash,
                SdkVersion = entry.SdkVersion,
                Status = PluginStatus.Draft,
            });
            return;
        }

        if (record.Status is PluginStatus.Disabled or PluginStatus.Uninstalled ||
            (string.Equals(record.SourceHash, entry.SourceHash, StringComparison.Ordinal) &&
             string.Equals(record.DependencyHash, entry.DependencyHash, StringComparison.Ordinal) &&
             string.Equals(record.SdkVersion, entry.SdkVersion, StringComparison.Ordinal)))
        {
            return;
        }

        approvals.Upsert(record with
        {
            SourceHash = entry.SourceHash,
            DependencyHash = entry.DependencyHash,
            SdkVersion = entry.SdkVersion,
        });
    }

    /// <summary>Publishes the complete state of one plugin: the tool snapshot and the non-tool contribution set.</summary>
    private Task PublishAsync(string pluginId, WorkerLoadout loadout, CancellationToken ct) =>
        DeliverAsync(
            pluginId,
            loadout.Generation,
            new ToolsRegisterNotification(pluginId, loadout.Tools),
            new PluginContributionsNotification(pluginId, loadout.Contributions ?? new PluginContributionSet()),
            ct);

    /// <summary>
    /// Delivers one plugin's snapshot to the main program and records it only once the frame was written and flushed to the
    /// pipe.
    /// <para>
    /// A publication is what the main program knows about a plugin, so two things must hold. First, notifications of one
    /// plugin are serialized, so a later snapshot is never overtaken by an earlier one whose send completes late. Second,
    /// the dedup record is written <em>after</em> the send returns, so a cancelled or failed notification is not remembered
    /// as published: the next load, refresh or execution publishes it again instead of the metadata being lost for the
    /// lifetime of the host. The generation carried by the loadout identifies the snapshot: a notification from a load
    /// older than the last one sent is dropped rather than allowed to overwrite newer state.
    /// </para>
    /// <para>
    /// "Sent" here means what the IPC layer can offer: the transport wrote the whole frame and its flush completed. It is
    /// <em>not</em> an acknowledgement from the main program - the host never waits for one and never records a frame as
    /// understood, only as handed to the pipe. A frame whose write was cancelled half way leaves the stream untrustworthy,
    /// so the send helper retires the connection through the supervised shutdown instead of writing more frames.
    /// </para>
    /// <para>
    /// The send is bounded by its own deadline and does not ride the caller's token: cancelling the call between the load
    /// and the publish must not throw away metadata the main program has no other way to learn. While the host is stopping
    /// nothing is sent at all.
    /// </para>
    /// </summary>
    private async Task DeliverAsync(
        string pluginId,
        long generation,
        ToolsRegisterNotification tools,
        PluginContributionsNotification contributions,
        CancellationToken ct)
    {
        if (stop.IsStopping)
        {
            return;
        }

        var toolsPayload = HostJson.ToElement(tools);
        var contributionsPayload = HostJson.ToElement(contributions);
        var toolsText = toolsPayload.GetRawText();
        var contributionsText = contributionsPayload.GetRawText();

        var publication = PublicationOf(pluginId);
        await publication.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool sendTools;
            bool sendContributions;
            lock (sync)
            {
                // A snapshot older than the one already delivered is dropped: a delayed notification must never overwrite
                // newer state the main program already has.
                if (generation < publication.Generation)
                {
                    return;
                }

                sendTools = !string.Equals(toolsText, publication.Tools, StringComparison.Ordinal);
                sendContributions = !string.Equals(contributionsText, publication.Contributions, StringComparison.Ordinal);
                if (!sendTools && !sendContributions)
                {
                    publication.Generation = Math.Max(publication.Generation, generation);
                    return;
                }
            }

            if (mainPeer is not { } peer || stop.IsStopping)
            {
                return;
            }

            using var deadline = new CancellationTokenSource(PublicationSendWindow);
            if (sendTools &&
                await SendAsync(peer, ExtensionProtocol.ToolsRegister, toolsPayload, deadline.Token).ConfigureAwait(false))
            {
                lock (sync)
                {
                    if (generation >= publication.Generation)
                    {
                        publication.Tools = toolsText;
                        publication.Generation = generation;
                    }
                }
            }

            if (sendContributions &&
                await SendAsync(peer, ExtensionProtocol.ContributionsRegister, contributionsPayload, deadline.Token).ConfigureAwait(false))
            {
                lock (sync)
                {
                    if (generation >= publication.Generation)
                    {
                        publication.Contributions = contributionsText;
                        publication.Generation = generation;
                    }
                }
            }
        }
        finally
        {
            publication.Gate.Release();
        }
    }

    /// <summary>
    /// Withdraws everything the main program was told about one plugin. It is serialized with that plugin's publications and
    /// fences older ones off before anything is sent: the recorded generation is taken to the load the plugin is running
    /// now, so a notification computed from an <em>older</em> load than that is dropped instead of resurrecting a plugin
    /// that is being removed. A notification of exactly that current load is still sent - that is a plugin whose content is
    /// approved again and whose worker never stopped, and the approval gate, not the generation, decides whether it may
    /// run.
    /// <para>
    /// The recorded payloads are kept until their unregister was written and flushed: they are the pending-unregister
    /// tombstone. A transient write failure therefore leaves the plugin marked as still-known and the next revoke,
    /// removal or reset retries the unregister; it is never silently suppressed. The recorded generation is raised before
    /// the send, so a failed unregister still fences off publications computed from an older load.
    /// </para>
    /// </summary>
    private async Task UnregisterAllAsync(string pluginId, CancellationToken ct)
    {
        var state = StateOf(pluginId);
        var publication = PublicationOf(pluginId);
        await publication.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool hadTools;
            bool hadContributions;
            long fenced;
            lock (sync)
            {
                // The recorded payloads are the pending-unregister tombstone: they are what the main program was last told
                // about, so they stay here until their unregister has been written and flushed. A transient send failure
                // therefore leaves the tombstone in place and the next revoke/removal/reset retries it instead of the
                // unregister being suppressed for the rest of the host's life.
                hadTools = publication.Tools is not null;
                hadContributions = publication.Contributions is not null;

                // Publications of an older load than the one the plugin is running now are stale and must not resurrect it;
                // a publication of exactly the current load is a re-registration of content that is approved again, so it is
                // still allowed (the approval gate, not the generation, decides whether a plugin may run). The fence is
                // raised even when the send below fails: an older snapshot must never be published after this point.
                fenced = Math.Max(publication.Generation, state.CurrentLoadGeneration);
                publication.Generation = fenced;
            }

            if (mainPeer is not { } peer)
            {
                return;
            }

            using var deadline = new CancellationTokenSource(PublicationSendWindow);
            var payload = HostJson.ToElement(new ToolsUnregisterNotification(pluginId));
            if (hadTools && await SendAsync(peer, ExtensionProtocol.ToolsUnregister, payload, deadline.Token).ConfigureAwait(false))
            {
                lock (sync)
                {
                    // Only forget a payload this notification covered; a newer registration that completed meanwhile keeps
                    // its own recorded payload (it is serialized by the gate, so this is a defensive generation check, not a
                    // race the send can win).
                    if (publication.Generation == fenced)
                    {
                        publication.Tools = null;
                    }
                }
            }

            if (hadContributions &&
                await SendAsync(peer, ExtensionProtocol.ContributionsUnregister, payload, deadline.Token).ConfigureAwait(false))
            {
                lock (sync)
                {
                    if (publication.Generation == fenced)
                    {
                        publication.Contributions = null;
                    }
                }
            }
        }
        finally
        {
            publication.Gate.Release();
        }
    }

    /// <summary>Drops what the main program was told about a plugin's tools and contributions, so the next registration is
    /// sent even when the declarations are unchanged.</summary>
    private async Task ForgetRegistrationAsync(string pluginId, CancellationToken ct)
    {
        var publication = PublicationOf(pluginId);
        await publication.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                publication.Tools = null;
                publication.Contributions = null;
            }
        }
        finally
        {
            publication.Gate.Release();
        }
    }

    /// <summary>
    /// Re-publishes a plugin after its worker crashed and was restarted. A recovery runs outside the plugin gate, so it
    /// rechecks that the plugin is still discovered and still allowed to run: a recovery that finishes after a revoke, a
    /// removal or a failed re-evaluation must not put the tool snapshot back.
    /// </summary>
    private async Task RepublishAfterRecoveryAsync(string pluginId, WorkerLoadout loadout, CancellationToken ct)
    {
        PluginEntry? entry;
        lock (sync)
        {
            plugins.TryGetValue(pluginId, out entry);
        }

        if (entry is null || !entry.Decision.IsAllowed || !entry.HasContent)
        {
            return;
        }

        await PublishAsync(pluginId, loadout, ct).ConfigureAwait(false);
    }

    private Publication PublicationOf(string pluginId)
    {
        lock (sync)
        {
            if (!publications.TryGetValue(pluginId, out var publication))
            {
                publication = new Publication();
                publications[pluginId] = publication;
            }

            return publication;
        }
    }

    /// <summary>
    /// Writes one notification frame and returns <c>true</c> once the transport wrote and flushed it. <c>false</c> means
    /// the frame was not handed to the pipe completely, so the caller must not record it as published; the main program may
    /// or may not have seen a prefix, which is why a cancelled frame is treated as an unsafe connection.
    /// </summary>
    private async Task<bool> SendAsync(IIpcPeer peer, string operation, JsonElement payload, CancellationToken ct)
    {
        if (Volatile.Read(ref connectionUnsafe) != 0)
        {
            return false;
        }

        try
        {
            await peer.SendNotificationAsync(operation, payload, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or IpcConnectionClosedException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            // The deadline cancelled the flush of a frame whose bytes may already be on the pipe. The stream can no longer
            // be trusted (a later frame could be read as part of the half-written one), so the host is retired through its
            // ordinary supervised shutdown instead of writing anything else. The peer is deliberately not stopped here:
            // this runs inside a request handler and awaiting the peer's own teardown from there could deadlock.
            RetireUnsafeConnection($"the {operation} notification was cancelled while its frame was being written");
            return false;
        }
    }

    /// <summary>True once a frame could not be written completely and the main-program connection can no longer be trusted;
    /// nothing else is sent on it.</summary>
    private bool ConnectionUnsafe => Volatile.Read(ref connectionUnsafe) != 0;

    /// <summary>
    /// Marks the main-program connection as unusable and lets the host's normal shutdown run. It only flips a flag and ends
    /// <c>RunAsync</c>'s wait: the teardown, the worker drain and the pipe close stay with <c>RunAsync</c>, which is not the
    /// handler that noticed the problem. That is what keeps a failed frame write from stopping the peer inside its own
    /// dispatch and deadlocking on itself.
    /// </summary>
    private void RetireUnsafeConnection(string reason)
    {
        if (Interlocked.Exchange(ref connectionUnsafe, 1) != 0)
        {
            return;
        }

        cleanup.Diagnose($"{reason}; the main-program connection was retired because it can no longer be trusted");
        stopped.TrySetResult();
    }

    /// <summary>
    /// Stops one plugin's worker and kills its process tree. The slot leaves the registry and its disposal is registered
    /// as a retirement <em>under the same lock</em>, so a shutdown that snapshots the registry can never miss a slot that a
    /// revoke, a refresh or a failed load already removed: the retirement set is awaited by the shutdown and only leaves it
    /// once the process is gone.
    /// </summary>
    private Task StopWorkerAsync(string pluginId)
    {
        WorkerSlot? slot;
        Task retiringSlot;
        lock (sync)
        {
            workers.Remove(pluginId, out slot);
            if (slot is null)
            {
                return Task.CompletedTask;
            }

            retiringSlot = slot.DisposeAsync().AsTask();
            if (!retiringSlot.IsCompleted)
            {
                retiring.Add(retiringSlot);
                _ = retiringSlot.ContinueWith(
                    static (finished, state) =>
                    {
                        var host = (ExtensionHost)state!;
                        lock (host.sync)
                        {
                            host.retiring.Remove(finished);
                        }
                    },
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        return retiringSlot;
    }

    /// <summary>
    /// Raises the shutdown fence and returns the single drain every shutdown caller awaits. The fence is set and the task is
    /// created under the registry lock, so a path that checks the fence under that lock either sees it or finished
    /// registering its worker before the snapshot; and because only the first caller creates the task, a <c>DisposeAsync</c>
    /// that races <see cref="RunAsync"/>'s own teardown waits for that same drain instead of returning while the other side
    /// is still killing workers.
    /// </summary>
    private Task BeginShutdown()
    {
        lock (sync)
        {
            stop.Begin();

            // Queued plugin waits are over once stopping is declared; dropping their edges here keeps a stale edge from
            // refusing a call during the drain. The graph's lock is only ever taken after this one, never before it.
            waits.Clear();
            return shutdown ??= ShutdownAsync();
        }
    }

    /// <summary>
    /// Drains everything the host owns, in the documented order: snapshot, kill the workers, wait for the retirements that
    /// were already in progress, then the script pool (which kills leased and idle script workers and leaves nothing
    /// behind). No lock is held across an await.
    /// </summary>
    private async Task ShutdownAsync()
    {
        await DrainWorkersAsync().ConfigureAwait(false);

        // Cancels queued script calls (no executor is started for them) and kills every executor process tree.
        await scriptExecutors.DisposeAsync().ConfigureAwait(false);
    }

    private async Task DrainWorkersAsync()
    {
        WorkerSlot[] slots;
        lock (sync)
        {
            slots = [.. workers.Values];
            workers.Clear();
            publications.Clear();
        }

        foreach (var slot in slots)
        {
            try
            {
                await slot.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanup.Record($"the worker of plugin '{slot.PluginId}' could not be stopped cleanly", exception);
            }
        }

        await AwaitRetirementsAsync(ShutdownRetireWindow).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits (bounded) until every slot disposal that is still running has finished, so a shutdown that raced a revoke, a
    /// refresh or a failed load does not return while that path is still killing a worker.
    /// </summary>
    private async Task AwaitRetirementsAsync(TimeSpan bound)
    {
        var remaining = bound;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            Task[] pending;
            lock (sync)
            {
                pending = [.. retiring];
            }

            if (pending.Length == 0)
            {
                return;
            }

            var left = remaining - elapsed.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                cleanup.Record(
                    $"{pending.Length} worker stop(s) did not finish within the {bound.TotalSeconds:0} s shutdown window; "
                    + "the host cannot verify that those process trees are gone");
                return;
            }

            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(left)).ConfigureAwait(false);
        }
    }

    /// <summary>Releases the approval store exactly once, and only after the drains and the pipe teardown, so no handler
    /// observes a disposed store.</summary>
    private void ReleaseResources()
    {
        if (Interlocked.Exchange(ref released, 1) != 0)
        {
            return;
        }

        approvals.Dispose();
    }

    /// <summary>Bound on waiting for the executions a revoke just killed to unwind before it returns.</summary>
    private static readonly TimeSpan RevokeDrainWindow = TimeSpan.FromSeconds(10);

    /// <summary>Bound on one publication notification; a send is never unbounded and never rides a caller's token.</summary>
    private static readonly TimeSpan PublicationSendWindow = TimeSpan.FromSeconds(15);

    /// <summary>Bound on waiting for slot disposals that a revoke, a refresh or a failed load started earlier.</summary>
    private static readonly TimeSpan ShutdownRetireWindow = TimeSpan.FromSeconds(30);

    /// <summary>Bound on waiting for the main-program pipe to close once <see cref="DisposeAsync"/> began the shutdown.</summary>
    private static readonly TimeSpan ConnectionCloseWindow = TimeSpan.FromSeconds(30);

    private WorkerSlot GetWorker(PluginEntry entry)
    {
        var pluginId = entry.Id;
        lock (sync)
        {
            // After shutdown this must fail instead of recreating a slot the shutdown snapshot already passed.
            stop.ThrowIfStopping($"a worker for plugin '{pluginId}'");
            if (workers.TryGetValue(pluginId, out var existing))
            {
                return existing;
            }

            var slot = new WorkerSlot(pluginId, options, StateOf(pluginId), ForwardToolCallAsync, stop, waits, cleanup)
            {
                Reloaded = (loadout, token) => RepublishAfterRecoveryAsync(pluginId, loadout, token),
            };
            workers[pluginId] = slot;
            return slot;
        }
    }

    /// <summary>The per-plugin state the host keeps for the whole run of the host. It is never discarded, so the gate a
    /// request is waiting on stays the same one every approval change and every refresh takes for that plugin id.</summary>
    private PluginState StateOf(string pluginId)
    {
        lock (sync)
        {
            if (!states.TryGetValue(pluginId, out var state))
            {
                state = new PluginState();
                states[pluginId] = state;
            }

            return state;
        }
    }

    private static void RequireAllowed(PluginEntry entry, ApprovalDecision decision)
    {
        if (!decision.IsAllowed)
        {
            throw new HostOperationException(
                "plugin_not_approved",
                $"Plugin '{entry.Id}' {entry.Version} must not run: {decision.Reason}.");
        }
    }

    private static PluginRecord Candidate(PluginEntry entry, PluginEvaluation evaluation) => new()
    {
        PluginId = entry.Id,
        Version = entry.Version,
        SourceHash = evaluation.SourceHash,
        DependencyHash = evaluation.DependencyHash,
        SdkVersion = entry.SdkVersion,
    };

    /// <summary>
    /// Relays one bound <c>host.tool_call</c> to the main program, which owns permission checks and auditing. The payload
    /// is the host's own <c>{runId, pluginId, name, args}</c>; the main program accepts it only when the run id is in its
    /// pending table and the plugin identity matches.
    /// </summary>
    private Task<IpcMessage> ForwardToolCallAsync(JsonElement payload, CancellationToken ct)
    {
        if (mainPeer is not { } peer || peer.IsClosed || ConnectionUnsafe)
        {
            throw new HostOperationException("main_disconnected", "The main program is not connected.");
        }

        return peer.SendRequestAsync(ExtensionProtocol.HostToolCall, payload, ct);
    }

    private PluginEntry? Find(string pluginId)
    {
        lock (sync)
        {
            return plugins.TryGetValue(pluginId, out var entry) ? entry : null;
        }
    }

#if DEBUG
    private ValueTask CaptureAsync(string pluginId, CancellationToken ct) =>
        testHook is { } hook ? hook.AfterEntryCapturedAsync(pluginId, ct) : ValueTask.CompletedTask;
#else
    private static ValueTask CaptureAsync(string pluginId, CancellationToken ct) => ValueTask.CompletedTask;
#endif

    /// <summary>
    /// Resolves the entry a request must use once it holds the plugin's gate: a refresh that replaced or removed the
    /// plugin while the request was waiting invalidates the entry it captured before the gate, so the request fails
    /// instead of running bytes nobody approved any more.
    /// </summary>
    private PluginEntry Current(PluginEntry captured)
    {
        var current = Find(captured.Id);
        if (current is null)
        {
            throw new HostOperationException(
                "unknown_plugin",
                $"Plugin '{captured.Id}' was removed by a refresh; the request was dropped.");
        }

        if (!ReferenceEquals(current, captured))
        {
            throw new HostOperationException(
                "plugin_changed",
                $"Plugin '{captured.Id}' was replaced by a refresh ('{captured.Version}' -> '{current.Version}'); the request was dropped.");
        }

        return current;
    }

    private string[] KnownPluginIds()
    {
        lock (sync)
        {
            return [.. plugins.Keys];
        }
    }

    private static bool ContainsCommand(IReadOnlyList<CommandDeclaration> commands, string name)
    {
        for (var i = 0; i < commands.Count; i++)
        {
            if (string.Equals(commands[i].Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsPrompt(IReadOnlyList<PromptContributorDeclaration> prompts, string id)
    {
        for (var i = 0; i < prompts.Count; i++)
        {
            if (string.Equals(prompts[i].Id, id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(IReadOnlyList<ToolDeclaration> tools, string name)
    {
        for (var i = 0; i < tools.Count; i++)
        {
            if (string.Equals(tools[i].Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task RespondErrorAsync(IIpcPeer peer, long requestId, string code, string message)
    {
        if (ConnectionUnsafe)
        {
            return;
        }

        try
        {
            await peer.RespondErrorAsync(requestId, new IpcError(code, message), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or IpcConnectionClosedException)
        {
        }
    }
}
