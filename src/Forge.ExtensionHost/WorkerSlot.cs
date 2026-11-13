using Forge.Ipc;
using Forge.Ipc.Protocol;
using Forge.Plugin.Sdk;

namespace Forge.ExtensionHost;

/// <summary>What a worker load produced: the tool declarations (also carried inside the contribution set, kept for the
/// tools.register snapshot), the complete non-tool contribution set and the generation of the load they came from. The
/// generation is what lets the host drop a publication that a later load has already superseded.</summary>
internal sealed record WorkerLoadout(
    IReadOnlyList<ToolDeclaration> Tools,
    PluginContributionSet Contributions,
    bool Started,
    long Generation);

/// <summary>
/// One <c>Forge.PluginWorker</c> process for one plugin. Loads are serialized, executions run concurrently up to
/// <see cref="HostOptions.MaxConcurrentExecutionsPerWorker"/>, and a timeout, a crash, a cancelled call that does not
/// stop cooperatively, or an uncooperative tool kills the whole process tree before the worker is restarted and
/// re-loaded (a cancellation token cannot stop hostile code). Every abnormal event is charged against the plugin's
/// failure budget in <see cref="PluginState"/>, which survives this slot being replaced.
/// <para>
/// Each dispatch registers an <see cref="ActiveRun"/> in its session before the request leaves the host. A worker's
/// <c>host.tool_call</c> is only served when its <c>runId</c> is in that session's table: the plugin identity and the
/// context come from the registered run, never from the message, so a worker cannot claim another plugin, session,
/// working directory or depth, and a call that arrives after its execution ended (or from another worker) is refused.
/// </para>
/// </summary>
/// <param name="forwardToolCall">Forwards one bound call to the main program and returns its result.</param>
/// <param name="stop">The host's shutdown fence; every path that could start, load or recover a worker rechecks it under
/// the slot gate, and an in-flight start is cancelled by it.</param>
internal sealed class WorkerSlot(
    string pluginId,
    HostOptions options,
    PluginState state,
    Func<HostToolCallForward, CancellationToken, Task<ToolResult>> forwardToolCall,
    HostStopSignal stop,
    RootWaitGraph waits,
    HostCleanupReport cleanup) : IAsyncDisposable
{
    /// <summary>How long a cancelled execution may keep running before its process tree is killed.</summary>
    internal static readonly TimeSpan CancelGraceWindow = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim gate = new(1, 1);

    private readonly SemaphoreSlim concurrency = new(Math.Max(1, options.MaxConcurrentExecutionsPerWorker));

    /// <summary>Cancelled when this slot is disposed, so a call queued for one of its execution slots stops waiting for a
    /// worker that is being torn down instead of waiting out its whole budget.</summary>
    private readonly CancellationTokenSource lifetime = new();

    private WorkerSession? current;

    /// <summary>The worker of a start that is still in flight: created by the operating system but not yet published as
    /// <see cref="current"/>. The shutdown drain owns it from the first moment it exists, so a start that is stuck in a
    /// connect or a load window cannot hold the drain open and cannot survive it.</summary>
    private WorkerSession? starting;

    private readonly Lock startSync = new();

    private PluginImage? image;

    private string version = string.Empty;

    private int disposed;

    private Task? disposal;

    private readonly Lock disposeSync = new();

    public string PluginId { get; } = pluginId;

    /// <summary>Links a queued execution to this slot's life: disposing the slot ends the wait, it never lingers.</summary>
    public CancellationToken Lifetime => lifetime.Token;

    /// <summary>Raised after a crashed worker was restarted and re-loaded, so the host re-publishes its contributions.</summary>
    public Func<WorkerLoadout, CancellationToken, Task>? Reloaded { get; set; }

    public IReadOnlyList<ToolDeclaration> Tools => Volatile.Read(ref current)?.Tools ?? [];

    public PluginContributionSet Contributions => Volatile.Read(ref current)?.Contributions ?? new PluginContributionSet();

    /// <summary>Starts the worker when it is not running for exactly this version and image, and loads the plugin into
    /// it. The decision is about the bytes, not the version string: a live worker is reused only when it already holds
    /// exactly the incoming image (same assembly bytes and the same digest for every loadable file). A same-version
    /// update therefore replaces the worker instead of serving the code it loaded earlier.</summary>
    public async Task<WorkerLoadout> EnsureLoadedAsync(
        string pluginVersion,
        PluginImage pluginImage,
        IReadOnlyList<Permission> permissions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pluginImage);

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Checked under the gate, so a shutdown that already snapshotted this slot cannot be followed by a load.
            stop.ThrowIfStopping($"a worker for plugin '{PluginId}'");
            if (Volatile.Read(ref disposed) != 0)
            {
                throw new HostOperationException("host_stopping", $"The worker for plugin '{PluginId}' was stopped.");
            }

            ThrowIfDisabled();

            var session = Volatile.Read(ref current);
            if (session is not null &&
                session.Process.IsAlive &&
                session.Loaded &&
                string.Equals(version, pluginVersion, StringComparison.Ordinal) &&
                SameImage(Volatile.Read(ref image), pluginImage))
            {
                return new WorkerLoadout(session.Tools, session.Contributions, Started: false, session.Generation);
            }

            if (session is not null)
            {
                Volatile.Write(ref current, null);
                await session.DisposeAsync().ConfigureAwait(false);
            }

            version = pluginVersion;
            try
            {
                using var start = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
                var loaded = await StartAndLoadAsync(pluginVersion, pluginImage, permissions, start.Token).ConfigureAwait(false);
                return loaded with { Started = true };
            }
            catch (OperationCanceledException) when (stop.IsStopping && !ct.IsCancellationRequested)
            {
                throw new HostOperationException(
                    "host_stopping",
                    $"The extension host is stopping; plugin '{PluginId}' was not loaded.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                state.ChargeFailure();
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Forwards one tool execution to the worker under the request's own deadline. A dead or hung worker is
    /// killed and restarted, and the in-flight call fails with an error that names the cause. The deadline is the one the
    /// host derived at request entry, so a call that waited for a slot gets only what is left of it.</summary>
    public async Task<ToolResult> ExecuteAsync(PluginExecuteRequest request, CallDeadline budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var context = request.Context ??
            throw new HostOperationException("invalid_context", "plugin.execute requires the bound call context.");
        return await RunAsync(
            request.PluginId,
            context,
            ExtensionProtocol.WorkerExecute,
            remaining => new WorkerExecuteRequest(
                request.PluginId,
                request.Tool,
                request.ArgumentsJson,
                options.PipeName,
                context,
                remaining),
            $"Tool '{request.Tool}' of plugin '{PluginId}'",
            response => Decode(response, "tool result", EmptyToolResult),
            budget,
            ct).ConfigureAwait(false);
    }

    private static readonly ToolResult EmptyToolResult = new(string.Empty);

    /// <summary>Runs one contributed command in the worker under the request's own deadline.</summary>
    public async Task<CommandOutput> ExecuteCommandAsync(CommandExecutionRequest request, CallDeadline budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var context = request.Context ??
            throw new HostOperationException("invalid_context", "plugin.command requires the bound call context.");
        return await RunAsync(
            request.PluginId,
            context,
            ExtensionProtocol.WorkerCommand,
            remaining => request with { Context = context, TimeoutMilliseconds = remaining },
            $"Command '{request.Command}' of plugin '{PluginId}'",
            response => Decode(response, "command result", new CommandOutput(null, null, true)),
            budget,
            ct).ConfigureAwait(false);
    }

    /// <summary>Evaluates one prompt contributor in the worker under the request's own deadline.</summary>
    public async Task<PromptOutput> EvaluatePromptAsync(PromptExecutionRequest request, CallDeadline budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var context = request.Context ??
            throw new HostOperationException("invalid_context", "plugin.prompt requires the bound call context.");
        return await RunAsync(
            request.PluginId,
            context,
            ExtensionProtocol.WorkerPrompt,
            remaining => request with { Context = context, TimeoutMilliseconds = remaining },
            $"Prompt contributor '{request.ContributorId}' of plugin '{PluginId}'",
            response => Decode(response, "prompt result", new PromptOutput(null)),
            budget,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops this slot's worker for good. Disposal is single-flight: every caller awaits the same completion, so a shutdown
    /// that races a revoke or a refresh cannot return while the other one is still killing the process tree.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (disposeSync)
        {
            disposal ??= DisposeCoreAsync();
            return new ValueTask(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref disposed, 1);

        // Cancels every execution that is still queued for one of this slot's concurrency slots: the wait ends now instead
        // of lingering until its budget, and the queued call then finds the worker gone. Its wait-for edge is released by
        // the waiter's own finally.
        lifetime.Cancel();

        // A start that is still in flight holds the gate while it waits for its pipe, its connect or its load. That wait is
        // not a reason to keep the shutdown open: its process is owned here from the moment it existed, so it is killed and
        // waited for first, which makes the start fail fast and release the gate below.
        WorkerSession? pending;
        lock (startSync)
        {
            pending = starting;
        }

        if (pending is not null)
        {
            try
            {
                await pending.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanup.Record($"the starting worker for plugin '{PluginId}' could not be stopped cleanly", exception);
            }
        }

        WorkerSession? session;
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            session = Volatile.Read(ref current);
            Volatile.Write(ref current, null);
        }
        finally
        {
            gate.Release();
        }

        if (session is not null)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanup.Record($"the worker for plugin '{PluginId}' could not be stopped cleanly", exception);
            }
        }
    }

    private enum RecoveryCause
    {
        Exited = 0,
        TimedOut = 1,
        Cancelled = 2,
    }

    /// <summary>
    /// One dispatch: takes a concurrency slot, registers the run, sends the request and supervises it. Every call queues
    /// for a slot, including a nested one: cross-root nesting was already refused under the plugin gate (which is what
    /// keeps two call chains from waiting on each other), so a sibling of the same root only waits for a same-root call to
    /// finish.
    /// <para>
    /// A wait that is actually queued is registered in the host's root-scoped wait-for graph first. If the edge would
    /// close a potential cycle between two branches of the same root, the call is refused at once with
    /// <c>plugin_wait_cycle</c> instead of waiting for its budget; identical queued edges are reference counted, so the
    /// ordinary same-root fan-out to one target still queues. The wait is linked to the caller, the shutdown fence, this
    /// slot's life and the call's absolute deadline, so none of them can be outlived; when the deadline expires while the
    /// call is still queued it fails as <c>plugin_queue_timeout</c> without killing a worker or charging a failure.
    /// </para>
    /// <para>
    /// Dispatch receives only what is left of the budget. The caller's cancellation gets a cooperative grace window before
    /// the process tree is killed, while an expired deadline kills it immediately - the two cases stay distinct.
    /// </para>
    /// </summary>
    private async Task<TResult> RunAsync<TArgs, TResult>(
        string targetPluginId,
        ExtensionCallContext context,
        string operation,
        Func<int, TArgs> payloadForBudget,
        string description,
        Func<IpcMessage, TResult> readResult,
        CallDeadline budget,
        CancellationToken ct)
    {
        await WaitForSlotAsync(context, budget, ct, description).ConfigureAwait(false);
        try
        {
            ThrowIfDisabled();

            if (budget.Remaining <= TimeSpan.Zero)
            {
                // The slot became free only after the deadline had already passed: the call was never executed, so it is a
                // queue expiry, not a worker timeout.
                throw BudgetEndedWhileQueued(description, budget);
            }

            var session = Volatile.Read(ref current) ??
                throw new HostOperationException("worker_not_loaded", $"The plugin worker for '{PluginId}' is not running.");

            if (!session.Process.IsAlive)
            {
                var restartNote = await RecoverAsync(session, RecoveryCause.Exited).ConfigureAwait(false);
                throw new HostOperationException(
                    "worker_restarted",
                    restartNote ?? $"The plugin worker for '{PluginId}' exited; the call failed and the worker was restarted.");
            }

            using var run = session.Runs.Begin(
                context.RunId,
                targetPluginId,
                CallBinding.RootOf(context),
                context);
            var wireLimit = Math.Max(1, (int)Math.Min(budget.Total.TotalMilliseconds, int.MaxValue));
            var payload = payloadForBudget(PluginCallLimits.ToWireMilliseconds(budget.Remaining, wireLimit));
            var response = await DispatchAsync(session, operation, payload, description, budget, ct).ConfigureAwait(false);
            state.RecordSuccess();
            return readResult(response);
        }
        finally
        {
            concurrency.Release();
        }
    }

    /// <summary>
    /// Takes one execution slot, waiting under the call's budget, the shutdown fence and this slot's life. A wait that has
    /// to queue is registered with the host's wait-for graph so two branches of one root cannot wait for each other; the
    /// registration is released however the wait ends.
    /// </summary>
    private async Task WaitForSlotAsync(
        ExtensionCallContext context,
        CallDeadline budget,
        CancellationToken ct,
        string description)
    {
        if (concurrency.Wait(0))
        {
            return;
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, lifetime.Token);
        RootWaitGraph.WaitLease? lease = null;
        if (SourcePluginOf(context) is { } source)
        {
            lease = waits.TryEnter(CallBinding.RootOf(context), source, PluginId, out var cycle);
            if (lease is null)
            {
                throw new HostOperationException("plugin_wait_cycle", cycle);
            }
        }

        try
        {
            await concurrency.WaitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && stop.IsStopping)
        {
            throw new HostOperationException(
                "host_stopping",
                $"The extension host is stopping; {description} was not dispatched.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.Expired)
        {
            throw BudgetEndedWhileQueued(description, budget);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HostOperationException(
                "worker_not_loaded",
                $"The plugin worker for '{PluginId}' was stopped; {description} was not dispatched.");
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <summary>The plugin whose code issued this call: the last link of the trusted chain. A root script or a direct
    /// main-program call has no plugin owner, and <c>$run_code</c> is not a plugin-held execution slot, so neither
    /// registers a wait-for edge.</summary>
    private static string? SourcePluginOf(ExtensionCallContext context)
    {
        var chain = context.PluginChain;
        if (chain is not { Count: > 0 } || chain[^1] is not { Length: > 0 } source)
        {
            return null;
        }

        return string.Equals(source, CallBinding.RunCodeIdentity, StringComparison.Ordinal) ? null : source;
    }

    /// <summary>The call's budget ended while it was waiting for an execution slot: nothing was dispatched, so nothing is
    /// killed and the plugin's failure budget is not touched.</summary>
    private static HostOperationException BudgetEndedWhileQueued(string description, CallDeadline budget) =>
        new(
            "plugin_queue_timeout",
            $"{description} was not dispatched: its {budget.Seconds} s budget ended while it waited for a free execution "
            + "slot. No worker was killed and no failure was charged.");

    /// <summary>
    /// Sends one worker request and supervises it: the caller's cancellation gets a cooperative grace window before the
    /// process tree is killed, the host's shutdown fence ends the call without killing a worker the teardown already owns,
    /// and the call's absolute deadline - not a timer restarted after the queue - kills the tree when it expires.
    /// </summary>
    private async Task<IpcMessage> DispatchAsync<TArgs>(
        WorkerSession session,
        string operation,
        TArgs payload,
        string description,
        CallDeadline budget,
        CancellationToken ct)
    {
        using var execution = new CancellationTokenSource();
        ct.ThrowIfCancellationRequested();
        var pending = session.Process.Peer.RequestAsync(operation, payload, execution.Token);
        try
        {
            try
            {
                return await pending.WaitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return await AwaitCancelGraceAsync(session, description, pending, execution).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsStopping)
            {
                // The shutdown owns the worker now; it kills the tree itself. Answering with a timeout would blame the
                // plugin for a deadline the shutdown pre-empted, so this ends as a shutdown cancellation instead.
                await execution.CancelAsync().ConfigureAwait(false);
                await DrainAsync(pending).ConfigureAwait(false);
                throw new HostOperationException(
                    "host_stopping",
                    $"The extension host is stopping; {description} was cancelled.");
            }
            catch (OperationCanceledException)
            {
                await execution.CancelAsync().ConfigureAwait(false);
                await DrainAsync(pending).ConfigureAwait(false);
                var restartNote = await RecoverAsync(session, RecoveryCause.TimedOut).ConfigureAwait(false);
                throw new HostOperationException(
                    "tool_timeout",
                    $"{description} exceeded {budget.Total.TotalSeconds:0.#} s; the whole worker process tree was killed"
                    + (restartNote is null ? " and restarted; the call failed." : "; " + restartNote));
            }
        }
        catch (Exception exception) when (exception is IpcTimeoutException or IpcConnectionClosedException)
        {
            var exited = exception is IpcConnectionClosedException;
            var restartNote = await RecoverAsync(session, exited ? RecoveryCause.Exited : RecoveryCause.TimedOut).ConfigureAwait(false);
            throw new HostOperationException(
                exited ? "worker_failed" : "tool_timeout",
                (exited
                    ? $"The plugin worker of '{PluginId}' exited during {description}"
                    : $"{description} exceeded {budget.Total.TotalSeconds:0.#} s; the whole worker process tree was killed")
                + (restartNote is null ? " and restarted; the call failed." : "; " + restartNote));
        }
        finally
        {
            if (!pending.IsCompleted)
            {
                await execution.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Waits the grace window for a cancelled call to finish on its own; kills and restarts the worker when it
    /// does not, which is what actually stops a plugin that ignores cancellation.</summary>
    private async Task<IpcMessage> AwaitCancelGraceAsync(
        WorkerSession session,
        string description,
        Task<IpcMessage> pending,
        CancellationTokenSource execution)
    {
        try
        {
            return await pending.WaitAsync(CancelGraceWindow, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (Exception exception) when (exception is IpcTimeoutException or IpcConnectionClosedException)
        {
            var timedOut = exception is IpcTimeoutException;
            var restartNote = await RecoverAsync(session, timedOut ? RecoveryCause.TimedOut : RecoveryCause.Cancelled).ConfigureAwait(false);
            throw new HostOperationException(
                timedOut ? "tool_timeout" : "worker_failed",
                (timedOut
                    ? $"{description} exceeded its deadline while being cancelled"
                    : $"The plugin worker of '{PluginId}' exited while {description} was being cancelled")
                + (restartNote is null ? "; the call failed." : "; " + restartNote));
        }

        await execution.CancelAsync().ConfigureAwait(false);
        await DrainAsync(pending).ConfigureAwait(false);
        state.ChargeFailure();
        await RecoverAsync(session, RecoveryCause.Cancelled, charge: false).ConfigureAwait(false);
        throw new HostOperationException(
            "cancelled",
            $"{description} was cancelled and did not finish within {CancelGraceWindow.TotalSeconds:0.#} s; its worker process tree was killed.");
    }

    /// <summary>Kills the given session, restarts the worker and re-loads the plugin. Returns <c>null</c> when the worker
    /// is running again, or a note explaining why it is not. It never restarts once the host is stopping: recovery runs
    /// with <see cref="CancellationToken.None"/> on purpose (the call that noticed the crash is already failing), so the
    /// shutdown fence - rechecked under the slot gate - is what keeps a disposed host from resurrecting a worker.</summary>
    private async Task<string?> RecoverAsync(WorkerSession session, RecoveryCause cause, bool charge = true)
    {
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(Volatile.Read(ref current), session))
            {
                return $"the worker for '{PluginId}' is no longer running.";
            }

            Volatile.Write(ref current, null);
            await session.DisposeAsync().ConfigureAwait(false);
            if (Volatile.Read(ref disposed) != 0 || stop.IsStopping)
            {
                return $"the worker for '{PluginId}' was stopped because the extension host is stopping.";
            }

            var failures = charge ? state.ChargeFailure() : state.Failures;
            if (failures >= PluginState.FailureBudget)
            {
                return $"the worker for '{PluginId}' failed {failures} times in a row and stays stopped until the plugins are refreshed";
            }

            try
            {
                using var restart = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var loadout = await StartAndLoadAsync(version, LoadImage(), LoadPermissions(), restart.Token)
                    .ConfigureAwait(false);
                if (Reloaded is { } reloaded)
                {
                    await reloaded(loadout, CancellationToken.None).ConfigureAwait(false);
                }

                return null;
            }
            catch (OperationCanceledException) when (stop.IsStopping)
            {
                return $"the extension host is stopping, so the worker for '{PluginId}' was not restarted.";
            }
            catch (Exception exception) when (exception is HostOperationException or IpcTimeoutException or IpcConnectionClosedException or IOException)
            {
                var total = state.ChargeFailure();
                return $"restarting the worker for '{PluginId}' failed ({cause.ToString().ToLowerInvariant()}): {exception.Message}"
                    + (total >= PluginState.FailureBudget ? " It stays stopped until the plugins are refreshed." : string.Empty);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The image of the version the slot was last loaded for; recovery re-loads exactly that.</summary>
    private PluginImage LoadImage() => Volatile.Read(ref image) ??
        throw new HostOperationException("worker_not_loaded", $"Plugin '{PluginId}' has no recorded image to reload.");

    private IReadOnlyList<Permission> LoadPermissions() => Volatile.Read(ref permissions) ?? [];

    private IReadOnlyList<Permission>? permissions;

    /// <summary>True when the worker already holds exactly these bytes, so nothing has to be reloaded.</summary>
    private static bool SameImage(PluginImage? loaded, PluginImage wanted)
    {
        if (loaded is null ||
            !string.Equals(loaded.AssemblyPath, wanted.AssemblyPath, StringComparison.Ordinal) ||
            !string.Equals(loaded.AssemblySha256, wanted.AssemblySha256, StringComparison.Ordinal) ||
            loaded.Files.Count != wanted.Files.Count)
        {
            return false;
        }

        foreach (var file in wanted.Files)
        {
            if (!loaded.Files.TryGetValue(file.Key, out var digest) ||
                !string.Equals(digest, file.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void ThrowIfDisabled()
    {
        if (!state.IsExhausted)
        {
            return;
        }

        throw new HostOperationException(
            "worker_disabled",
            $"The plugin worker for '{PluginId}' failed {state.Failures} times in a row and stays stopped until the plugins are refreshed.");
    }

    private async Task<WorkerLoadout> StartAndLoadAsync(
        string pluginVersion,
        PluginImage pluginImage,
        IReadOnlyList<Permission> manifestPermissions,
        CancellationToken ct)
    {
        WorkerSession? session = null;
        var loaded = false;
        try
        {
            session = await StartAsync(ct).ConfigureAwait(false);
            var payload = new WorkerLoadRequest(
                PluginId,
                pluginVersion,
                pluginImage.AssemblyPath,
                pluginImage.AssemblySha256,
                pluginImage.Files,
                manifestPermissions);
            using var deadline = new CancellationTokenSource(LoadTimeout);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            IpcMessage response;
            try
            {
                response = await session.Process.Peer.RequestAsync(ExtensionProtocol.WorkerLoad, payload, wait.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HostOperationException(
                    "worker_load_failed",
                    $"The plugin worker for '{PluginId}' did not load the plugin within {LoadTimeout.TotalSeconds:0.#} s.");
            }

            var contributions = Decode<WorkerLoadResponse?>(response, "load result", null)?.Contributions
                ?? new PluginContributionSet();
            session.Tools = contributions.Tools is { Count: > 0 } tools ? [.. tools] : [];
            session.Contributions = contributions;
            session.Loaded = true;
            loaded = true;
            session.Generation = state.NextLoadGeneration();
            Volatile.Write(ref image, pluginImage);
            Volatile.Write(ref permissions, manifestPermissions);
            Volatile.Write(ref current, session);
            return new WorkerLoadout(session.Tools, session.Contributions, Started: false, session.Generation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HostOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new HostOperationException(
                "worker_load_failed",
                $"The plugin worker for '{PluginId}' could not load the plugin: {exception.Message}{session?.Process.LogSuffix()}");
        }
        finally
        {
            if (!loaded && session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private TimeSpan LoadTimeout => options.ToolTimeout > TimeSpan.Zero ? options.ToolTimeout : TimeSpan.FromSeconds(120);

    private async Task<WorkerSession> StartAsync(CancellationToken ct)
    {
        WorkerSession? session = null;
        try
        {
            var worker = await WorkerProcess.StartAsync(
                options,
                PluginId,
                [],
                created =>
                {
                    session = new WorkerSession(created);
                    lock (startSync)
                    {
                        starting = session;
                    }

                    created.Peer.RequestReceived += (message, token) => HandleWorkerRequestAsync(session, message, token);
                },
                ct,
                report: cleanup).ConfigureAwait(false);
            return session ?? throw new HostOperationException("worker_start_failed", $"The plugin worker for '{PluginId}' produced no session.");
        }
        finally
        {
            // The start is over: from here the session is either published as current or already disposed, so the drain
            // must not find it again here.
            lock (startSync)
            {
                starting = null;
            }
        }
    }

    /// <summary>Serves the worker's bound <c>host.tool_call</c> through the shared bound-call handler.</summary>
    private Task HandleWorkerRequestAsync(WorkerSession session, IpcMessage message, CancellationToken ct) =>
        BoundToolCallHandler.ServeAsync(session.Process, session.Runs, message, forwardToolCall, ct);

    /// <summary>Decodes a worker's result; a response without a payload yields <paramref name="missing"/>, and one that is
    /// not the documented type is a <c>worker_protocol</c> failure.</summary>
    private static TResult Decode<TResult>(IpcMessage response, string what, TResult missing)
    {
        if (!response.HasPayload)
        {
            return missing;
        }

        try
        {
            return response.Read<TResult>();
        }
        catch (IpcProtocolException exception)
        {
            throw new HostOperationException("worker_protocol", $"The plugin worker returned an unreadable {what}: {exception.Message}");
        }
    }

    /// <summary>Observes a request that was already cancelled so its failure is not left unobserved.</summary>
    private static async Task DrainAsync(Task pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>One worker connection: its process, containment, peer, the contribution set the plugin registered and the
    /// executions still in flight in it.</summary>
    private sealed class WorkerSession(WorkerProcess process) : IAsyncDisposable
    {
        private readonly Lock disposeSync = new();

        private Task? disposal;

        public WorkerProcess Process { get; } = process;

        public ActiveRunTable Runs { get; } = new();

        public IReadOnlyList<ToolDeclaration> Tools { get; set; } = [];

        public PluginContributionSet Contributions { get; set; } = new();

        public bool Loaded { get; set; }

        /// <summary>Generation of the load this session carries, so publications of it can be ordered.</summary>
        public long Generation { get; set; }

        public ValueTask DisposeAsync()
        {
            lock (disposeSync)
            {
                disposal ??= DisposeCoreAsync();
                return new ValueTask(disposal);
            }
        }

        private async Task DisposeCoreAsync()
        {
            Runs.EndAll();
            await Process.DisposeAsync().ConfigureAwait(false);
        }
    }
}
