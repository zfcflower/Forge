using System.Diagnostics;
using System.Text;
using Forge.Ipc;
using Forge.Plugin.Sdk;

namespace Forge.ExtensionHost;

/// <summary>
/// One <c>Forge.PluginWorker</c> process with its operating-system containment, worker pipe, peer and the bounded tail
/// of its output. It is the only code path that starts a worker, so every worker - a plugin's or a script's - is
/// assigned to its job object / process group before its peer is connected, and every stop path (timeout, cancellation,
/// crash, revoke, shutdown) tears the whole contained tree down the same way: best-effort <c>worker.stop</c>, the
/// containment kill, then <see cref="Process.Kill(bool)"/> as a fallback.
/// </summary>
internal sealed class WorkerProcess : IAsyncDisposable
{
    /// <summary>How long a stopping worker's line pumps get to drain what its pipes still hold.</summary>
    private static readonly TimeSpan PumpDrainWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a stopping worker gets to answer <c>worker.stop</c> and exit before the tree is killed.</summary>
    private static readonly TimeSpan StopGraceWindow = TimeSpan.FromSeconds(2);

    private readonly NamedPipeServerTransport transport;
    private readonly WorkerProcessOwnership ownership;
    private readonly TailBuffer log;
    private readonly HostCleanupReport? cleanup;
    private readonly WorkerProcessOwnership.SpawnedWorker? spawned;
    private readonly Task[] pumps;
    private readonly Lock disposeSync = new();
    private Task? disposal;
    private int disposed;

    private WorkerProcess(
        string ownerLabel,
        string pipeName,
        Process process,
        WorkerProcessOwnership ownership,
        NamedPipeServerTransport transport,
        IpcPeer peer,
        TailBuffer log,
        HostCleanupReport? cleanup,
        WorkerProcessOwnership.SpawnedWorker? spawned,
        Task[]? pumps = null)
    {
        OwnerLabel = ownerLabel;
        PipeName = pipeName;
        Process = process;
        this.ownership = ownership;
        this.transport = transport;
        Peer = peer;
        this.log = log;
        this.cleanup = cleanup;
        this.spawned = spawned;
        this.pumps = pumps ?? [];
    }

    public string OwnerLabel { get; }

    public string PipeName { get; }

    public Process Process { get; }

    public IpcPeer Peer { get; }

    public bool IsAlive
    {
        get
        {
            if (Volatile.Read(ref disposed) != 0 || Peer.IsClosed)
            {
                return false;
            }

            try
            {
                return !Process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public string LogSuffix() => log.Suffix();

    /// <summary>
    /// Starts a worker for <paramref name="ownerLabel"/>, assigns its containment before anything else and connects the
    /// pipe. <paramref name="configure"/> runs once the peer exists and before it accepts messages, so the caller's
    /// request handlers cannot miss the first request. <paramref name="onProcessStarted"/> runs as soon as the operating
    /// system created the process - before containment is assigned and before it can connect - so a pool that must be able
    /// to kill it during shutdown owns it from the first possible moment instead of from the first moment it is published.
    /// The worker peer has no default deadline: every request carries its own budget, so a 600 s script is not cut short by
    /// the old two-minute default while load, stop and metadata requests stay bounded by explicit deadlines in their
    /// callers.
    /// </summary>
    public static async Task<WorkerProcess> StartAsync(
        HostOptions options,
        string ownerLabel,
        IReadOnlyList<string> arguments,
        Action<WorkerProcess>? configure,
        CancellationToken ct,
        Action<Process>? onProcessStarted = null,
        HostCleanupReport? report = null,
        string? executablePath = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLabel);

        if ((executablePath ?? options.WorkerPath) is not { } workerPath)
        {
            throw new HostOperationException(
                "worker_missing",
                $"No plugin worker executable was found; pass --worker <path> or set {HostOptions.WorkerEnvironmentVariable}.");
        }

        var pipeName = BuildPipeName(ownerLabel);
        var runsAsLibrary = workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var executable = runsAsLibrary ? options.RuntimePath : workerPath;
        var workerArguments = new List<string>(arguments.Count + 3);
        if (runsAsLibrary)
        {
            workerArguments.Add(workerPath);
        }

        workerArguments.Add("--pipe");
        workerArguments.Add(pipeName);
        foreach (var argument in arguments)
        {
            workerArguments.Add(argument);
        }

        var log = new TailBuffer(4096);
        var ownership = WorkerProcessOwnership.Create();
        Process? started = null;
        WorkerProcessOwnership.SpawnedWorker? spawned = null;
        try
        {
            if (WorkerProcessOwnership.SpawnsChildNatively)
            {
                // POSIX: the child has to be in its own process group before it runs anything, and only the spawn call can
                // do that, so the host starts it natively instead of through Process and takes the pipes itself.
                try
                {
                    spawned = WorkerProcessOwnership.Spawn(executable, workerArguments);
                }
                catch (HostOperationException)
                {
                    ownership.Dispose();
                    throw;
                }

                if (!TryOpenProcessHandle(spawned.ProcessId, out started))
                {
                    ownership.Dispose();
                    spawned.Dispose();
                    throw new HostOperationException(
                        "worker_start_failed",
                        $"The plugin worker '{executable}' (pid {spawned.ProcessId}) exited before the host could open a handle to it.");
                }
            }
            else
            {
                var startInfo = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    FileName = executable,
                };

                foreach (var argument in workerArguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                started = Process.Start(startInfo);
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ownership.Dispose();
            spawned?.Dispose();
            throw new HostOperationException(
                "worker_start_failed",
                $"The plugin worker '{executable}' could not be started: {exception.Message}");
        }

        if (started is null)
        {
            ownership.Dispose();
            spawned?.Dispose();
            throw new HostOperationException("worker_start_failed", $"Starting '{executable}' produced no process.");
        }

        var process = started;
        onProcessStarted?.Invoke(process);
        try
        {
            if (spawned is null)
            {
                ownership.Assign(process);
            }
            else
            {
                ownership.Assign(spawned.ProcessId);
            }
        }
        catch (HostOperationException)
        {
            ReportUnverifiedStart(report, ownerLabel, KillProcessTree(ownership, process));
            ownership.Dispose();
            spawned?.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            ReportUnverifiedStart(report, ownerLabel, KillProcessTree(ownership, process));
            ownership.Dispose();
            spawned?.Dispose();
            throw new HostOperationException(
                "worker_start_failed",
                $"The plugin worker for '{ownerLabel}' could not be contained before it ran any plugin code: {exception.Message}");
        }

        Task[] pumps = spawned is null ? [] : PumpLines(spawned, log);
        if (spawned is null)
        {
            process.ErrorDataReceived += (_, args) => log.Append(args.Data);
            process.OutputDataReceived += (_, args) => log.Append(args.Data);
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
        }

        var transport = new NamedPipeServerTransport(pipeName, new IpcPipeOptions { ConnectTimeout = options.ConnectTimeout });
        var peer = new IpcPeer(transport, new IpcPeerOptions { DefaultTimeout = Timeout.InfiniteTimeSpan });
        var worker = new WorkerProcess(ownerLabel, pipeName, process, ownership, transport, peer, log, report, spawned, pumps);
        peer.ProtocolError += error => log.Append("protocol error: " + error.Message);
        configure?.Invoke(worker);
        try
        {
            await peer.StartAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The start was aborted on purpose (the shutdown fence cancels in-flight spawns): the process is killed and
            // the cancellation travels on as a cancellation, so the caller can report "the host is stopping" instead of a
            // start failure nobody asked for.
            await worker.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await worker.DisposeAsync().ConfigureAwait(false);
            throw new HostOperationException(
                "worker_start_failed",
                $"The plugin worker for '{ownerLabel}' did not connect on pipe '{pipeName}': {exception.Message}{log.Suffix()}");
        }

        return worker;
    }

    /// <summary>
    /// Offers <c>worker.stop</c> and lets the worker leave within the grace window, then kills the whole contained tree
    /// through the job object or process group and closes that containment. The containment, not the worker being alive, is
    /// what makes descendants die - including when the worker already exited on its own. Disposal is single-flight: two
    /// callers that both decide to stop this process (a shutdown and a failed start, a revoke and a crash recovery) await
    /// the same completion instead of one of them returning while the tree is still alive.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (disposeSync)
        {
            disposal ??= DisposeCoreAsync();
            return new ValueTask(disposal);
        }
    }

    private static void ReportUnverifiedStart(HostCleanupReport? report, string ownerLabel, string? problem)
    {
        if (problem is not null)
        {
            report?.Record($"the uncontained worker of '{ownerLabel}' could not be verified as stopped: {problem}");
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        var pid = -1;
        try
        {
            pid = Process.Id;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
        }

        await StopGracefullyAsync().ConfigureAwait(false);
        var cleanupProblem = KillProcessTree(ownership, Process);
        ownership.Dispose();

        if (spawned is not null)
        {
            // The child is dead, so its pipes end at once; let the line pumps finish so the tail is complete, then close
            // the readable ends the host held.
            await Task.WhenAny(Task.WhenAll(pumps), Task.Delay(PumpDrainWindow)).ConfigureAwait(false);
            spawned.Dispose();
        }

        try
        {
            await Peer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanup?.Record($"the worker pipe of '{OwnerLabel}' (pid {pid}) did not close cleanly", exception);
        }

        try
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanup?.Record($"the worker transport of '{OwnerLabel}' (pid {pid}) did not close cleanly", exception);
        }

        Process.Dispose();

        // The residue report comes last: a kill that reported failure is only a problem when the process is still there.
        // The containment kill is best effort, so "the process is gone" is the evidence, never the native return value.
        if (cleanupProblem is not null)
        {
            cleanup?.Record(
                $"the worker of '{OwnerLabel}' (pid {pid}) could not be verified as stopped: {cleanupProblem}");
        }
    }

    private async Task StopGracefullyAsync()
    {
        if (Peer.IsClosed)
        {
            return;
        }

        using var deadline = new CancellationTokenSource(StopGraceWindow);
        try
        {
            await Peer.RequestAsync(ExtensionProtocol.WorkerStop, IpcEmpty.Instance, deadline.Token).ConfigureAwait(false);
            await Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Reads a natively spawned child's redirected stdout and stderr into <paramref name="log"/>. The job-object
    /// path lets <see cref="Process"/> do this through its data-received events; a child the host spawned itself owns
    /// those pipes, so it reads them here. Both pumps end when the child's write ends close.</summary>
    private static Task[] PumpLines(WorkerProcessOwnership.SpawnedWorker child, TailBuffer log) =>
    [
        PumpAsync(child.StandardOutput, log),
        PumpAsync(child.StandardError, log),
    ];

    private static Task PumpAsync(Stream stream, TailBuffer log) => Task.Run(async () =>
    {
        var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                log.Append(line);
            }
        }
        catch (Exception exception) when (exception is ObjectDisposedException or IOException)
        {
            // The worker and its pipe are gone; what it wrote is already in the tail.
        }
    });

    /// <summary>Opens the host's handle to a child it started natively. The handle is only the fallback for
    /// <see cref="Process.Kill(bool)"/> and the identity the rest of the host tracks; the containment itself is the
    /// process group, which needs nothing but the pid. <see cref="Process.GetProcessById(int)"/> reports a pid that is
    /// already gone as a missing process, which is the only way this can fail.</summary>
    private static bool TryOpenProcessHandle(int processId, out Process? process)
    {
        try
        {
            process = Process.GetProcessById(processId);
            return true;
        }
        catch (ArgumentException)
        {
            process = null;
            return false;
        }
    }

    private static string BuildPipeName(string ownerLabel)
    {
        const string prefix = "forge-worker-";
        var suffix = "-" + Guid.NewGuid().ToString("N")[..12];
        // Linux/macOS keep the socket file under the temporary directory and cap the whole path, so the readable plugin
        // token only gets what the budget leaves after the fixed parts; uniqueness always comes from the GUID. The
        // budget is in bytes, and a plugin id may contain non-ASCII letters, so the cut is measured in UTF-8 bytes.
        var budget = Math.Max(0, IpcPipe.MaxNameLength - prefix.Length - suffix.Length);
        var token = PluginIds.PipeToken(ownerLabel);
        var keep = token.Length;
        while (keep > 0 && Encoding.UTF8.GetByteCount(token.AsSpan(0, keep)) > budget)
        {
            keep--;
        }

        return prefix + token[..keep] + suffix;
    }

    /// <summary>Kills a worker together with everything it started: the job object or process group first, so remnants
    /// of a worker that already exited are still owned, then <see cref="Process.Kill(bool)"/> as the fallback. Returns a
    /// description of what could not be verified as stopped, or <c>null</c> once the worker process is observed gone. The
    /// native containment kill is best effort: a failed <c>TerminateJobObject</c> is not proof of a survivor, so the
    /// verification is the process state after the fallback kill and the bounded wait.</summary>
    private static string? KillProcessTree(WorkerProcessOwnership ownership, Process process)
    {
        string? nativeProblem = null;
        string? fallbackProblem = null;
        try
        {
            if (!ownership.Kill(out var problem))
            {
                nativeProblem = problem;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception or IOException or ObjectDisposedException)
        {
            nativeProblem = exception.Message;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception or IOException or ObjectDisposedException)
        {
            fallbackProblem = exception.Message;
        }

        var exited = false;
        try
        {
            exited = process.WaitForExit(5000);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception or IOException or ObjectDisposedException)
        {
            fallbackProblem ??= exception.Message;
        }

        if (exited)
        {
            return null;
        }

        var detail = string.Join(
            "; ",
            new[]
            {
                nativeProblem is null ? null : "the containment kill failed (" + nativeProblem + ")",
                fallbackProblem is null ? null : "the fallback kill failed (" + fallbackProblem + ")",
                "the process was still alive 5 s after the kill",
            }.Where(static part => part is not null));
        return detail;
    }
}

/// <summary>Bounded tail of a worker's stdout/stderr, used to explain startup and load failures.</summary>
internal sealed class TailBuffer(int capacity)
{
    private readonly StringBuilder text = new();

    private readonly Lock gate = new();

    public void Append(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        lock (gate)
        {
            text.Append(line).Append('\n');
            if (text.Length > capacity * 2)
            {
                text.Remove(0, text.Length - capacity);
            }
        }
    }

    public string Suffix()
    {
        lock (gate)
        {
            if (text.Length == 0)
            {
                return string.Empty;
            }

            var start = Math.Max(0, text.Length - capacity);
            return "\n  worker output: " + text.ToString(start, text.Length - start).TrimEnd();
        }
    }
}
