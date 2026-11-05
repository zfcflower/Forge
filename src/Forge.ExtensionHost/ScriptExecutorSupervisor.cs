using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Forge.Ipc;
using Forge.Plugin.Sdk;
using Forge.ScriptRuntime;
using Forge.Scripting;

namespace Forge.ExtensionHost;

/// <summary>
/// The single-use script executor supervisor. One <c>run_code</c> call compiles with the host's own
/// <see cref="ScriptCodeCompiler"/> (the single owner of the compilation cache), spawns a fresh
/// <c>Forge.ScriptExecutor</c> under the same per-process containment plugin workers use, delivers the artifact in
/// memory (<c>executor.load</c>), runs it once (<c>executor.run</c>) and then kills and waits for the whole process tree
/// before it returns. Nothing is ever pooled, returned or reused: an executed process is dead, and no call can inherit
/// another call's image, thread, static state or handle.
/// <para>
/// The request's budget is one absolute deadline taken at request entry: the wait for a slot, the compilation, the spawn,
/// the load and the run all draw from it, and the executor is handed only what is left. The host keeps the hard deadline
/// and the kill for itself, so it may win before the executor reports anything - and then it says the tree was killed
/// instead of claiming the script stopped.
/// </para>
/// <para>
/// The supervisor is fenced by the host's shutdown signal, exactly like the plugin worker pool: a call that arrives after
/// the fence starts nothing, an in-flight spawn or load is cancelled and its process is killed by whoever started it, and
/// <see cref="DisposeAsync"/> waits (bounded) for those in-flight starts and then kills whatever is left, so no executor
/// created around the shutdown can escape the drain.
/// </para>
/// </summary>
internal sealed class ScriptExecutorSupervisor : IAsyncDisposable
{
    /// <summary>Largest script budget the main program may send; larger values are clamped to it.</summary>
    internal const int MaxScriptTimeoutMilliseconds = 600_000;

    /// <summary>Budget used when a request carries none.</summary>
    internal const int DefaultScriptTimeoutMilliseconds = 60_000;

    /// <summary>How long a cancelled script gets to stop on its own before its process tree is killed.</summary>
    internal static readonly TimeSpan CancelGraceWindow = TimeSpan.FromSeconds(2);

    /// <summary>How long shutdown waits for leased script calls and in-flight executor starts to unwind.</summary>
    private static readonly TimeSpan ShutdownDrainWindow = TimeSpan.FromSeconds(10);

    /// <summary>How long the load acknowledgement may take; bounded so a wedged executor cannot eat the whole budget.</summary>
    private static readonly TimeSpan LoadWindow = TimeSpan.FromSeconds(30);

    private readonly HostOptions options;
    private readonly string? executorPath;
    private readonly ScriptCodeCompiler compiler;
    private readonly Func<JsonElement, CancellationToken, Task<IpcMessage>> forwardToolCall;
    private readonly HostStopSignal stop;
    private readonly HostCleanupReport cleanup;
    private readonly SemaphoreSlim slots;
    private readonly Lock sync = new();

    /// <summary>Executions whose process exists: registered from the moment the operating system created the process, so
    /// the shutdown drain kills what it finds instead of trusting a set the spawn has not reached yet.</summary>
    private readonly List<ScriptExecution> live = [];

    private readonly CancellationTokenSource lifetime = new();
    private int inFlight;
    private int starts;
    private TaskCompletionSource? idleAll;
    private TaskCompletionSource? startsIdle;
    private Task? disposal;
    private int disposed;

    public ScriptExecutorSupervisor(
        HostOptions options,
        ScriptCodeCompiler compiler,
        Func<JsonElement, CancellationToken, Task<IpcMessage>> forwardToolCall,
        HostStopSignal stop,
        HostCleanupReport cleanup)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(forwardToolCall);
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(cleanup);
        this.options = options;

        // An in-process host (a test, an embedder) may leave the path unset; the same discovery the command line uses
        // then applies, so a host never silently loses its executor. A path that was named explicitly (--executor or the
        // environment variable) never falls back to discovery: when it does not exist the call fails closed instead of
        // running an executor nobody asked for.
        executorPath = options.ExecutorPath ??
            (options.RequestedExecutorPath is null ? HostOptions.FindExecutorExecutable() : null);
        this.compiler = compiler;
        this.forwardToolCall = forwardToolCall;
        this.stop = stop;
        this.cleanup = cleanup;
        var count = Math.Clamp(options.ScriptWorkerCount, 1, HostOptions.MaxScriptWorkers);
        slots = new SemaphoreSlim(count, count);
    }

    /// <summary>The host's single compilation-cache owner.</summary>
    internal ScriptCodeCompiler Compiler => compiler;

    public async Task<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        stop.ThrowIfStopping("the script");
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new HostOperationException("host_stopping", "The extension host is stopping; the script was not started.");
        }

        var budget = ClampBudget(request.TimeoutMilliseconds);
        var elapsed = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(budget);
        using var queueWait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, ct, lifetime.Token);
        try
        {
            await slots.WaitAsync(queueWait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref disposed) != 0 || stop.IsStopping)
            {
                throw new HostOperationException("host_stopping", "The extension host is stopping; the script was not started.");
            }

            return new CodeExecutionResult(
                $"The script was not started: its {Seconds(budget)} s budget ended while it waited for a free script slot.",
                IsError: true);
        }

        if (Volatile.Read(ref disposed) != 0 || stop.IsStopping)
        {
            slots.Release();
            throw new HostOperationException("host_stopping", "The extension host is stopping; the script was not started.");
        }

        BeginExecution();
        try
        {
            return await ExecuteCoreAsync(request, budget, elapsed, deadline.Token, ct).ConfigureAwait(false);
        }
        catch (HostOperationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (
            !ct.IsCancellationRequested &&
            (Volatile.Read(ref disposed) != 0 || stop.IsStopping || lifetime.IsCancellationRequested))
        {
            throw new HostOperationException(
                "host_stopping",
                "The extension host is stopping; the script executor that was starting was killed and the call ended.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The budget ended while the executor was starting. This is the call's own deadline, not a caller
            // cancellation, so it is reported as a budget expiry and no script was charged for it; whatever process the
            // start had created is already killed by the start path.
            return new CodeExecutionResult(
                $"The script was not started: its {Seconds(budget)} s budget ended while a script executor was starting.",
                IsError: true);
        }
        finally
        {
            slots.Release();
            EndExecution();
        }
    }

    /// <summary>
    /// Shuts the supervisor down and waits for the <em>whole</em> shutdown. Disposal is single-flight: every caller awaits
    /// the same completion instead of the losers returning while the winner is still killing processes.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            disposal ??= DisposeCoreAsync();
            return new ValueTask(disposal);
        }
    }

    private async Task<CodeExecutionResult> ExecuteCoreAsync(
        CodeExecutionRequest request,
        int budget,
        Stopwatch elapsed,
        CancellationToken deadlineToken,
        CancellationToken ct)
    {
        using var acquire = CancellationTokenSource.CreateLinkedTokenSource(deadlineToken, ct, lifetime.Token);

        // The host compiles in-process and owns the cache; a malformed script never spawns anything.
        var compiled = await compiler.CompileAsync(request.Code, request.Options ?? new CodeExecutionOptions(), acquire.Token)
            .ConfigureAwait(false);
        if (!compiled.Ok)
        {
            return new CodeExecutionResult(compiled.Error!, IsError: true);
        }

        var execution = await StartExecutionAsync(acquire.Token).ConfigureAwait(false);
        var result = new CodeExecutionResult("The script executor returned no result.", IsError: true);
        ActiveRun? run = null;
        var dispatched = false;
        try
        {
            var context = request.Context!;
            run = execution.Runs.Begin(context.RunId, CallBinding.RunCodeIdentity, CallBinding.RootOf(context), context);

            await LoadAsync(execution, compiled, acquire.Token).ConfigureAwait(false);

            // The executor gets exactly the time that is left after the queue, the compilation, the spawn and the load, so
            // a short budget that is still runnable stays runnable. The host keeps the hard deadline.
            var remaining = TimeSpan.FromMilliseconds(budget) - elapsed.Elapsed;
            var wireLimit = remaining <= TimeSpan.Zero
                ? 1
                : (int)Math.Clamp(Math.Ceiling(remaining.TotalMilliseconds), 1, MaxScriptTimeoutMilliseconds);
            var payload = HostJson.ToElement(new ScriptRunRequest(
                context,
                wireLimit,
                request.Tools ?? [],
                request.Options ?? new CodeExecutionOptions()));

            dispatched = true;
            var response = await DispatchAsync(execution, payload, deadlineToken, ct).ConfigureAwait(false);
            result = ReadResult(response);
            run.End();
            run = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The grace window is honoured, but the call's own deadline can end it early, so the text follows whichever of
            // the two ended the wait.
            var grace = deadlineToken.IsCancellationRequested
                ? "The script was cancelled and its budget ended before it stopped; its executor process tree was killed."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"The script was cancelled and did not stop within {CancelGraceWindow.TotalSeconds:0.#} s; its executor process tree was killed.");
            throw new OperationCanceledException(
                dispatched
                    ? grace
                    : "The script was cancelled before its run started; its executor process tree was killed.");
        }
        catch (OperationCanceledException) when (Volatile.Read(ref disposed) != 0 || stop.IsStopping || lifetime.IsCancellationRequested)
        {
            throw new HostOperationException(
                "host_stopping",
                "The extension host is stopping; the script executor was killed and the call ended.");
        }
        catch (OperationCanceledException)
        {
            // Wording follows `dispatched`: before `executor.run` went out the script never started running, so a budget
            // that ends during the load is reported as exactly that.
            result = new CodeExecutionResult(
                dispatched
                    ? $"The script did not finish within {Seconds(budget)} s; its whole executor process tree was killed."
                    : $"The script was not started: its {Seconds(budget)} s budget ended while the script image was loading; its whole executor process tree was killed.",
                IsError: true);
        }
        catch (IpcRemoteException exception)
        {
            result = new CodeExecutionResult(
                $"The script executor refused the run: {exception.Message}{execution.Process.LogSuffix()}",
                IsError: true);
        }
        catch (ScriptArtifactException exception)
        {
            result = new CodeExecutionResult($"The script artifact was refused: {exception.Message}", IsError: true);
        }
        catch (Exception exception) when (exception is IpcConnectionClosedException or IpcTimeoutException or IOException or InvalidOperationException)
        {
            result = new CodeExecutionResult(
                $"The script executor failed: {exception.Message}{execution.Process.LogSuffix()}",
                IsError: true);
        }
        finally
        {
            run?.End();

            // Nothing outlives the call: the whole tree is killed and waited for before this returns, whatever the
            // outcome was, and the execution leaves the registry so no call and no shutdown sees it again. A tree that is
            // still alive afterwards is appended to the result and never returned as success.
            var problem = await execution.DisposeAsync().ConfigureAwait(false);
            lock (sync)
            {
                live.Remove(execution);
            }

            if (problem is not null)
            {
                result = new CodeExecutionResult(
                    $"{result.Content}\nIts executor process could not be verified as stopped: {problem}",
                    IsError: true,
                    result.RequiresTermination);
            }
        }

        return result;
    }

    /// <summary>
    /// Starts one executor and adopts it into <see cref="live"/>. The attempt is registered under the same lock that sets
    /// the shutdown fence, so once the fence is up no new start can begin; the raw process is registered with the
    /// supervisor the moment it exists, so the shutdown can kill an executor that was never published.
    /// </summary>
    private async Task<ScriptExecution> StartExecutionAsync(CancellationToken ct)
    {
        ScriptExecution? execution = null;
        Process? process = null;
        var adopted = false;
        if (!TryBeginStart())
        {
            throw new HostOperationException("host_stopping", "The extension host is stopping; no script executor was started.");
        }

        try
        {
            if (executorPath is not { } path)
            {
                throw new HostOperationException(
                    "executor_missing",
                    options.RequestedExecutorPath is { } requested
                        ? $"The script executor '{requested}' does not exist or is not a file; pass --executor <path> or set {HostOptions.ExecutorEnvironmentVariable}."
                        : $"No script executor executable was found; pass --executor <path> or set {HostOptions.ExecutorEnvironmentVariable}.");
            }

            await WorkerProcess.StartAsync(
                options,
                "script",
                [],
                created =>
                {
                    execution = new ScriptExecution(created, cleanup);
                    var current = execution;
                    created.Peer.RequestReceived += (message, token) =>
                        BoundToolCallHandler.ServeAsync(created, current.Runs, message, forwardToolCall, token);
                },
                ct,
                started =>
                {
                    process = started;
                    lock (sync)
                    {
                        if (Volatile.Read(ref disposed) == 0)
                        {
                            spawning.Add(started);
                        }
                    }
                },
                cleanup,
                path).ConfigureAwait(false);

            if (execution is null)
            {
                throw new HostOperationException("worker_start_failed", "The script executor produced no session.");
            }

            lock (sync)
            {
                spawning.Remove(execution.Process.Process);
                if (Volatile.Read(ref disposed) == 0)
                {
                    live.Add(execution);
                    adopted = true;
                }
            }

            if (!adopted)
            {
                // Outside the lock: killing a process must never happen while the registry lock is held.
                await execution.DisposeAsync().ConfigureAwait(false);
                throw new HostOperationException(
                    "host_stopping",
                    "The extension host is stopping; the script executor that was starting was discarded.");
            }

            return execution;
        }
        finally
        {
            EndStart(process);
        }
    }

    /// <summary>Delivers the artifact over the executor's pipe and waits for the acknowledgement. A refusal is the
    /// executor's error reply, so it is turned into <c>executor_load_failed</c> here instead of travelling out as an
    /// unclassified IPC failure.</summary>
    private static async Task LoadAsync(ScriptExecution execution, ScriptCompilation compiled, CancellationToken ct)
    {
        var payload = HostJson.ToElement(new ScriptArtifactLoadRequest(
            ScriptArtifact.Encode(compiled.Image!),
            compiled.Descriptor!));
        using var deadline = new CancellationTokenSource(LoadWindow);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            await execution.Process.Peer
                .SendRequestAsync(ScriptExecutorProtocol.Load, payload, wait.Token)
                .ConfigureAwait(false);
        }
        catch (IpcRemoteException exception)
        {
            throw new HostOperationException(
                "executor_load_failed",
                $"The script executor refused the image: {exception.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HostOperationException(
                "executor_load_failed",
                $"The script executor did not load the image within {LoadWindow.TotalSeconds:0.#} s.");
        }
    }

    /// <summary>
    /// Sends <c>executor.run</c> and supervises it under both tokens: the caller's cancellation gets a cooperative grace
    /// window before the tree is killed, the shutdown fence ends the call without blaming the script, and the call's
    /// absolute deadline - which no executor can overrun because its own peer has no default timeout - stops the wait at
    /// once and leaves the kill to the caller's finally.
    /// </summary>
    private async Task<IpcMessage> DispatchAsync(
        ScriptExecution execution,
        JsonElement payload,
        CancellationToken deadlineToken,
        CancellationToken ct)
    {
        using var dispatch = new CancellationTokenSource();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(deadlineToken, ct, lifetime.Token);
        ct.ThrowIfCancellationRequested();
        var pending = execution.Process.Peer.SendRequestAsync(ScriptExecutorProtocol.Run, payload, dispatch.Token);
        try
        {
            try
            {
                return await pending.WaitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Graceful first: a script that stops by itself inside the window is reported normally. One that does not
                // is killed with its whole tree by the caller's finally, and its slot is never reused. The grace stays
                // bounded by the call's deadline; an ordinary completion still propagates as before.
                try
                {
                    return await pending.WaitAsync(CancelGraceWindow, deadlineToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
                catch (OperationCanceledException) when (!deadlineToken.IsCancellationRequested)
                {
                    throw;
                }

                await dispatch.CancelAsync().ConfigureAwait(false);
                await DrainAsync(pending).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException)
            {
                // The budget or the shutdown fence ended the wait. The host owns the hard deadline, so the wait stops here
                // and the caller's finally kills the tree; no grace window, because the executor was never asked to stop.
                await dispatch.CancelAsync().ConfigureAwait(false);
                await DrainAsync(pending).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            if (!pending.IsCompleted)
            {
                await dispatch.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    private static CodeExecutionResult ReadResult(IpcMessage response)
    {
        if (response.Result is not { } element)
        {
            return new CodeExecutionResult("The script executor returned no result.", IsError: true);
        }

        try
        {
            var result = element.Deserialize<CodeExecutionResult>(HostJson.Options);
            return result is null
                ? new CodeExecutionResult("The script executor returned no result.", IsError: true)
                : result with { Content = result.Content ?? string.Empty };
        }
        catch (JsonException exception)
        {
            return new CodeExecutionResult(
                $"The script executor returned an unreadable result: {exception.Message}",
                IsError: true);
        }
    }

    private async Task DisposeCoreAsync()
    {
        // The fence is raised under the same lock that guards the spawn bookkeeping, so a spawn that reached the supervisor
        // either registered before this point or sees the fence and refuses to start at all: no process is created after
        // the drain began.
        lock (sync)
        {
            disposed = 1;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);

        // In-flight starts are cancelled through the lifetime token; give them a bounded window to unwind, so the ordinary
        // path kills and disposes its own process and only a start that cannot observe the cancellation is left over.
        await WaitForStartsAsync(ShutdownDrainWindow).ConfigureAwait(false);

        ScriptExecution[] executions;
        Process[] stragglers;
        lock (sync)
        {
            executions = [.. live];
            live.Clear();
            stragglers = [.. spawning];
            spawning.Clear();
        }

        foreach (var execution in executions)
        {
            try
            {
                // The problem itself is recorded once by the single-flight disposal this call shares with the executing
                // path; only an exception is recorded here.
                await execution.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanup.Record("a script executor could not be stopped cleanly", exception);
            }
        }

        // A start that is still running past the window may be blocked where it cannot observe the cancellation. Its
        // process is owned here from the moment it was created, so killing it here is what makes "disposed" mean no script
        // executor is left alive; the start path then fails on the dead process and disposes its own wrapper.
        foreach (var process in stragglers)
        {
            if (KillStraggler(process) is { } problem)
            {
                cleanup.Record($"a script executor whose start outlived the shutdown window was not verified as stopped: {problem}");
            }
        }

        await WaitForIdleAsync(ShutdownDrainWindow).ConfigureAwait(false);
    }

    /// <summary>Kills a process whose start never reached the registry, together with anything it started. Returns a
    /// description when the process could not be verified as gone.</summary>
    private static string? KillStraggler(Process process)
    {
        var pid = TryGetProcessId(process);
        string? problem = null;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception or IOException or ObjectDisposedException)
        {
            // A wrapper that was already disposed by its own start path is not a survivor: the pid is what decides.
            if (!ProcessRunning(pid))
            {
                return null;
            }

            problem = exception.Message;
        }

        var exited = false;
        try
        {
            exited = process.WaitForExit(5000);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception or IOException or ObjectDisposedException)
        {
            problem ??= exception.Message;
        }

        if (!exited)
        {
            exited = !ProcessRunning(pid);
        }

        return exited
            ? null
            : problem is null
                ? "the process was still alive 5 s after the kill"
                : $"{problem}; the process was still alive 5 s after the kill";
    }

    private static int TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or ObjectDisposedException)
        {
            return -1;
        }
    }

    private static bool ProcessRunning(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static int ClampBudget(int requested) =>
        requested <= 0
            ? DefaultScriptTimeoutMilliseconds
            : Math.Min(requested, MaxScriptTimeoutMilliseconds);

    private static string Seconds(int milliseconds) =>
        (milliseconds / 1000d).ToString("0.###", CultureInfo.InvariantCulture);

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

    private void BeginExecution()
    {
        lock (sync)
        {
            inFlight++;
        }
    }

    private void EndExecution()
    {
        TaskCompletionSource? complete = null;
        lock (sync)
        {
            inFlight--;
            if (inFlight == 0 && idleAll is { } waiting)
            {
                complete = waiting;
                idleAll = null;
            }
        }

        complete?.TrySetResult();
    }

    private bool TryBeginStart()
    {
        lock (sync)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return false;
            }

            starts++;
            return true;
        }
    }

    private void EndStart(Process? process)
    {
        TaskCompletionSource? complete = null;
        lock (sync)
        {
            if (process is not null)
            {
                spawning.Remove(process);
            }

            if (--starts == 0 && startsIdle is { } waiting)
            {
                complete = waiting;
                startsIdle = null;
            }
        }

        complete?.TrySetResult();
    }

    private async Task WaitForStartsAsync(TimeSpan bound)
    {
        Task pending;
        lock (sync)
        {
            if (starts == 0)
            {
                return;
            }

            startsIdle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = startsIdle.Task;
        }

        await Task.WhenAny(pending, Task.Delay(bound)).ConfigureAwait(false);
    }

    private async Task WaitForIdleAsync(TimeSpan bound)
    {
        Task pending;
        lock (sync)
        {
            if (inFlight == 0)
            {
                return;
            }

            idleAll ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = idleAll.Task;
        }

        await Task.WhenAny(pending, Task.Delay(bound)).ConfigureAwait(false);
    }

    /// <summary>Processes of executors that are being started right now: created by the operating system inside
    /// <see cref="WorkerProcess.StartAsync"/> but not yet adopted into <see cref="live"/>.</summary>
    private readonly HashSet<Process> spawning = [];

    /// <summary>One executor process and the runs it holds (at most one). Disposal kills the whole tree, waits for it and
    /// reports whether it could verify the process is gone.</summary>
    private sealed class ScriptExecution(WorkerProcess process, HostCleanupReport? cleanup)
    {
        private readonly Lock disposeSync = new();

        private Task<string?>? disposal;

        public WorkerProcess Process { get; } = process;

        public ActiveRunTable Runs { get; } = new();

        /// <summary>Kills the tree once; a second caller awaits the same completion. Returns what could not be verified as
        /// stopped, or <c>null</c> once the process is observed gone.</summary>
        public ValueTask<string?> DisposeAsync()
        {
            lock (disposeSync)
            {
                disposal ??= DisposeCoreAsync();
                return new ValueTask<string?>(disposal);
            }
        }

        private async Task<string?> DisposeCoreAsync()
        {
            var pid = -1;
            try
            {
                pid = Process.Process.Id;
            }
            catch (InvalidOperationException)
            {
            }

            Runs.EndAll();
            await Process.DisposeAsync().ConfigureAwait(false);
            if (StillRunning(pid) is not { } problem)
            {
                return null;
            }

            cleanup?.Record($"a script executor (pid {pid}) could not be verified as stopped: {problem}");
            return problem;
        }

        /// <summary>What could not be verified as stopped: a process that is still running, or a state that could not be
        /// read at all. <c>null</c> once the process is observed gone; a probe failure is never reported as "gone".</summary>
        private static string? StillRunning(int pid)
        {
            if (pid <= 0)
            {
                return null;
            }

            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                return process.HasExited ? null : $"its process (pid {pid}) was still running after the kill";
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                return $"the state of its process (pid {pid}) could not be read: {exception.Message}";
            }
        }
    }
}
