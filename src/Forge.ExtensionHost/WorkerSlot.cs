using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Forge.Ipc;
using Forge.Plugin.Sdk;

namespace Forge.ExtensionHost;

/// <summary>
/// One <c>Forge.PluginWorker</c> process for one plugin. Loads are serialized, executions run concurrently up to
/// <see cref="HostOptions.MaxConcurrentExecutionsPerWorker"/>, and a timeout, a crash or an uncooperative tool kills the
/// whole process tree before the worker is restarted and re-loaded (a cancellation token cannot stop hostile code).
/// </summary>
/// <param name="forwardToolCall">Routes a worker's <c>host.tool_call</c> to the main program and returns its response.</param>
internal sealed class WorkerSlot(
    string pluginId,
    HostOptions options,
    Func<IpcMessage, CancellationToken, Task<IpcMessage>> forwardToolCall) : IAsyncDisposable
{
    private const int MaxConsecutiveFailures = 3;

    private readonly SemaphoreSlim gate = new(1, 1);

    private readonly SemaphoreSlim concurrency = new(Math.Max(1, options.MaxConcurrentExecutionsPerWorker));

    private WorkerSession? current;

    private string version = string.Empty;

    private string assemblyPath = string.Empty;

    private int failures;

    private int disposed;

    public string PluginId { get; } = pluginId;

    /// <summary>Raised after a crashed worker was restarted and re-loaded, so the host re-publishes its tools.</summary>
    public Func<IReadOnlyList<ToolDeclaration>, CancellationToken, Task>? Reloaded { get; set; }

    public IReadOnlyList<ToolDeclaration> Tools => Volatile.Read(ref current)?.Tools ?? [];

    /// <summary>Starts the worker when it is not running for exactly this version and assembly, and loads the plugin into
    /// it. Returns <c>true</c> when a new worker process was started.</summary>
    public async Task<bool> EnsureLoadedAsync(string pluginVersion, string pluginAssemblyPath, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (Volatile.Read(ref failures) > MaxConsecutiveFailures)
            {
                throw new HostOperationException(
                    "worker_disabled",
                    $"The plugin worker for '{PluginId}' failed {MaxConsecutiveFailures + 1} times in a row and stays stopped until the plugins are refreshed.");
            }

            var session = Volatile.Read(ref current);
            if (session is not null &&
                session.IsAlive &&
                session.Loaded &&
                string.Equals(version, pluginVersion, StringComparison.Ordinal) &&
                string.Equals(assemblyPath, pluginAssemblyPath, StringComparison.Ordinal))
            {
                return false;
            }

            if (session is not null)
            {
                Volatile.Write(ref current, null);
                await session.DisposeAsync().ConfigureAwait(false);
            }

            version = pluginVersion;
            assemblyPath = pluginAssemblyPath;
            await StartAndLoadAsync(ct).ConfigureAwait(false);
            failures = 0;
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Forwards one execution to the worker. A dead or hung worker is killed and restarted, and the in-flight
    /// call fails with an error that names the cause.</summary>
    public async Task<ToolCallResult> ExecuteAsync(PluginExecuteRequest request, string hostPipeName, CancellationToken ct)
    {
        await concurrency.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var session = Volatile.Read(ref current) ??
                throw new HostOperationException("worker_not_loaded", $"The plugin worker for '{PluginId}' is not running.");

            if (!session.IsAlive)
            {
                var restartNote = await RecoverAsync(session, RecoveryCause.Exited).ConfigureAwait(false);
                throw new HostOperationException(
                    "worker_restarted",
                    restartNote ?? $"The plugin worker for '{PluginId}' exited; the call failed and the worker was restarted.");
            }

            var payload = HostJson.ToElement(new WorkerExecuteRequest(
                request.PluginId,
                request.Tool,
                request.Args,
                request.SessionId,
                request.WorkingDir,
                hostPipeName));

            IpcMessage response;
            try
            {
                response = await session.Peer.SendRequestAsync(ExtensionProtocol.WorkerExecute, payload, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IpcTimeoutException or IpcConnectionClosedException)
            {
                var cause = exception is IpcTimeoutException ? RecoveryCause.TimedOut : RecoveryCause.Exited;
                var reason = exception is IpcTimeoutException
                    ? $"Tool '{request.Tool}' of plugin '{PluginId}' exceeded {options.ToolTimeout.TotalSeconds:0.#} s; the whole worker process tree was killed"
                    : $"The plugin worker of '{PluginId}' exited during tool '{request.Tool}'";
                var restartNote = await RecoverAsync(session, cause).ConfigureAwait(false);
                throw new HostOperationException(
                    cause == RecoveryCause.TimedOut ? "tool_timeout" : "worker_failed",
                    restartNote is null ? reason + " and restarted; the call failed." : reason + "; " + restartNote);
            }

            ToolCallResult? result;
            try
            {
                result = response.Result is { } element ? element.Deserialize<ToolCallResult>(HostJson.Options) : null;
            }
            catch (JsonException exception)
            {
                throw new HostOperationException("worker_protocol", $"The plugin worker of '{PluginId}' returned an unreadable result: {exception.Message}");
            }

            failures = 0;
            return result ?? new ToolCallResult(string.Empty, false);
        }
        finally
        {
            concurrency.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
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
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private enum RecoveryCause
    {
        Exited = 0,
        TimedOut = 1,
    }

    /// <summary>Kills the given session, restarts the worker and re-loads the plugin. Returns <c>null</c> when the worker
    /// is running again, or a note explaining why it is not.</summary>
    private async Task<string?> RecoverAsync(WorkerSession session, RecoveryCause cause)
    {
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(Volatile.Read(ref current), session))
            {
                return null;
            }

            Volatile.Write(ref current, null);
            await session.DisposeAsync().ConfigureAwait(false);
            failures++;
            if (failures > MaxConsecutiveFailures)
            {
                return $"the worker for '{PluginId}' failed {failures} times in a row and stays stopped until the plugins are refreshed";
            }

            try
            {
                var tools = await StartAndLoadAsync(CancellationToken.None).ConfigureAwait(false);
                if (Reloaded is { } reloaded)
                {
                    await reloaded(tools, CancellationToken.None).ConfigureAwait(false);
                }

                return null;
            }
            catch (Exception exception) when (exception is HostOperationException or IpcTimeoutException or IpcConnectionClosedException or IOException)
            {
                return $"restarting the worker for '{PluginId}' failed ({cause.ToString().ToLowerInvariant()}): {exception.Message}";
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<ToolDeclaration>> StartAndLoadAsync(CancellationToken ct)
    {
        var session = await StartAsync(ct).ConfigureAwait(false);
        var loaded = false;
        try
        {
            var payload = HostJson.ToElement(new WorkerLoadRequest(PluginId, version, assemblyPath));
            var response = await session.Peer.SendRequestAsync(ExtensionProtocol.WorkerLoad, payload, ct).ConfigureAwait(false);
            session.Tools = ReadTools(response);
            session.Loaded = true;
            loaded = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new HostOperationException(
                "worker_load_failed",
                $"The plugin worker for '{PluginId}' could not load the plugin: {exception.Message}{session.LogSuffix()}");
        }
        finally
        {
            if (!loaded)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                Volatile.Write(ref current, session);
            }
        }

        return session.Tools;
    }

    private async Task<WorkerSession> StartAsync(CancellationToken ct)
    {
        if (options.WorkerPath is not { } workerPath)
        {
            throw new HostOperationException(
                "worker_missing",
                $"No plugin worker executable was found; pass --worker <path> or set {HostOptions.WorkerEnvironmentVariable}.");
        }

        var pipeName = BuildPipeName(PluginId);
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        if (workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = options.RuntimePath;
            startInfo.ArgumentList.Add(workerPath);
        }
        else
        {
            startInfo.FileName = workerPath;
        }

        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--dotnet");
        startInfo.ArgumentList.Add(options.RuntimePath);

        var log = new TailBuffer(4096);
        Process process;
        try
        {
            process = Process.Start(startInfo) ??
                throw new HostOperationException("worker_start_failed", $"Starting '{startInfo.FileName}' produced no process.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new HostOperationException(
                "worker_start_failed",
                $"The plugin worker '{startInfo.FileName}' could not be started: {exception.Message}");
        }

        process.ErrorDataReceived += (_, args) => log.Append(args.Data);
        process.OutputDataReceived += (_, args) => log.Append(args.Data);
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        var transport = new NamedPipeServerTransport(pipeName, new IpcPipeOptions { ConnectTimeout = options.ConnectTimeout });
        var peer = new IpcPeer(transport, new IpcPeerOptions { DefaultTimeout = options.ToolTimeout });
        var session = new WorkerSession(pipeName, process, transport, peer, log);
        peer.RequestReceived += (message, token) => HandleWorkerRequestAsync(session, message, token);
        peer.ProtocolError += error => log.Append("protocol error: " + error.Message);
        try
        {
            await peer.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new HostOperationException(
                "worker_start_failed",
                $"The plugin worker for '{PluginId}' did not connect on pipe '{pipeName}': {exception.Message}{log.Suffix()}");
        }

        return session;
    }

    /// <summary>Serves the worker's <c>host.tool_call</c>: forwarded to the main program over the main pipe, and its
    /// result relayed back to the worker.</summary>
    private async Task HandleWorkerRequestAsync(WorkerSession session, IpcMessage message, CancellationToken ct)
    {
        if (!string.Equals(message.Operation, ExtensionProtocol.HostToolCall, StringComparison.Ordinal))
        {
            await TryRespondErrorAsync(session, message.Id, new IpcError("unhandled", $"The extension host does not serve '{message.Operation}'."), ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var forwarded = await forwardToolCall(message, ct).ConfigureAwait(false);
            await session.Peer.RespondAsync(message.Id, forwarded.Result ?? HostJson.EmptyObject, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TryRespondErrorAsync(session, message.Id, new IpcError("cancelled", "The host tool call was cancelled."), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await TryRespondErrorAsync(session, message.Id, new IpcError("host_tool_call_failed", exception.Message), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task TryRespondErrorAsync(WorkerSession session, long requestId, IpcError error, CancellationToken ct)
    {
        try
        {
            await session.Peer.RespondErrorAsync(requestId, error, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or IpcConnectionClosedException or OperationCanceledException)
        {
        }
    }

    private static IReadOnlyList<ToolDeclaration> ReadTools(IpcMessage response)
    {
        if (response.Result is not { } element)
        {
            return [];
        }

        var payload = element.Deserialize<WorkerLoadResponse>(HostJson.Options);
        var tools = payload?.Tools;
        return tools is { Count: > 0 } ? [.. tools] : [];
    }

    private static string BuildPipeName(string pluginId)
    {
        var token = PluginIds.PipeToken(pluginId);
        var unique = Guid.NewGuid().ToString("N")[..12];
        return $"forge-worker-{token}-{unique}";
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception or IOException)
        {
        }
    }

    /// <summary>One worker connection: its process, pipe, peer and the tools the plugin registered.</summary>
    private sealed class WorkerSession(
        string pipeName,
        Process process,
        NamedPipeServerTransport transport,
        IpcPeer peer,
        TailBuffer log) : IAsyncDisposable
    {
        private int disposed;

        public string PipeName { get; } = pipeName;

        public Process Process { get; } = process;

        public IpcPeer Peer { get; } = peer;

        public IReadOnlyList<ToolDeclaration> Tools { get; set; } = [];

        public bool Loaded { get; set; }

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

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            if (!Peer.IsClosed)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await Peer.SendRequestAsync(ExtensionProtocol.WorkerStop, HostJson.EmptyObject, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            try
            {
                await Peer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            KillProcessTree(Process);
            Process.Dispose();
        }
    }

    /// <summary>Bounded tail of a worker's stdout/stderr, used to explain startup and load failures.</summary>
    private sealed class TailBuffer(int capacity)
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
}
