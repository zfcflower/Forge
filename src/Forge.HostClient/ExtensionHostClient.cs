using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient;

/// <summary>
/// Main-program side of the Extension Host contract. It owns the host process and the IPC connection, keeps
/// an atomic snapshot of the plugin tools the host registered as <see cref="ITool"/> proxies, and relays
/// plugin→main-program tool calls through the caller's resolver and approval path so the client never
/// bypasses permission checks.
/// </summary>
public sealed class ExtensionHostClient : IExtensionHostClient
{
    private readonly HostConnection connection;
    private readonly ExtensionHostClientOptions options;
    private readonly Func<string, ITool?>? toolResolver;
    private readonly Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>>? approvalPath;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly Lock toolsGate = new();
    private readonly Lock statusGate = new();
    private readonly Lock peerGate = new();
    private readonly Dictionary<string, PluginTools> toolsByPlugin = new(StringComparer.Ordinal);
    private IReadOnlyList<ITool> tools = [];
    private IReadOnlyDictionary<string, IReadOnlyList<ITool>> toolsByPluginView =
        new Dictionary<string, IReadOnlyList<ITool>>(StringComparer.Ordinal);
    private IpcPeer? peer;
    private NamedPipeClientTransport? transport;
    private HostProcess? hostProcess;
    private string? protocolProblem;
    private long nextCallId;
    private long nextGeneration;
    private long registeredGeneration;
    private int status = (int)HostStatus.Stopped;
    private int stopping;
    private int disposed;

    /// <summary>Connects to (or starts) a host without a main-program tool resolver: <c>host.tool_call</c>
    /// requests are answered with an unknown-tool error.</summary>
    public ExtensionHostClient(HostConnection connection)
        : this(connection, null, null, null)
    {
    }

    public ExtensionHostClient(HostConnection connection, ExtensionHostClientOptions? options)
        : this(connection, null, null, options)
    {
    }

    /// <summary>Connects to (or starts) a host and lets plugins call main-program tools: <paramref name="toolResolver"/>
    /// finds the tool and <paramref name="approvalPath"/> applies the main program's policy and user approval.</summary>
    public ExtensionHostClient(
        HostConnection connection,
        Func<string, ITool?>? toolResolver,
        Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>>? approvalPath)
        : this(connection, toolResolver, approvalPath, null)
    {
    }

    public ExtensionHostClient(
        HostConnection connection,
        Func<string, ITool?>? toolResolver,
        Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>>? approvalPath,
        ExtensionHostClientOptions? options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        this.connection = connection;
        this.toolResolver = toolResolver;
        this.approvalPath = approvalPath;
        this.options = options ?? new ExtensionHostClientOptions();
    }

    public HostStatus Status => (HostStatus)Volatile.Read(ref status);

    public IReadOnlyList<ITool> Tools => Volatile.Read(ref tools);

    /// <summary>The same proxies grouped by plugin id, as last reported by the host.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ITool>> ToolsByPlugin => Volatile.Read(ref toolsByPluginView);

    /// <summary>Id of the host process this client started; null when it connected to an existing host.</summary>
    public int? HostProcessId
    {
        get
        {
            lock (peerGate)
            {
                return hostProcess?.ProcessId;
            }
        }
    }

    public event Action? ToolsChanged;

    public event Action<HostStatus, string?>? StatusChanged;

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var current = Status;
            if (current is not (HostStatus.Stopped or HostStatus.Failed))
            {
                throw new InvalidOperationException(
                    $"The Extension Host client is {current}; stop it before starting it again.");
            }

            var recovering = current == HostStatus.Failed;
            Interlocked.Exchange(ref stopping, 0);
            Volatile.Write(ref protocolProblem, null);
            var launchesHost = !string.IsNullOrWhiteSpace(connection.HostExecutablePath);
            SetStatus(HostStatus.Starting, launchesHost
                ? $"Starting '{connection.HostExecutablePath}'."
                : $"Connecting to the Extension Host on pipe '{connection.PipeName}'.");

            try
            {
                var pipeName = IpcPipe.ValidatePipeName(connection.PipeName);
                HostProcess? started = null;
                if (launchesHost)
                {
                    started = HostProcess.Start(connection, options);
                    lock (peerGate)
                    {
                        hostProcess = started;
                    }
                }

                var connected = await ConnectAsync(pipeName, started, ct).ConfigureAwait(false);
                lock (peerGate)
                {
                    peer = connected.Peer;
                    transport = connected.Transport;
                }

                Interlocked.Exchange(ref registeredGeneration, connected.Generation);
                SetStatus(HostStatus.Running, null);
                if (recovering)
                {
                    DropToolsOfEarlierConnections();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                SetStatus(HostStatus.Stopped, "Starting the Extension Host was canceled.");
                await TeardownAsync(sendHostStop: false, clearTools: false).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                FailStatus(DescribeFailure(ex));
                await TeardownAsync(sendHostStop: false, clearTools: false).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task RefreshPluginsAsync(CancellationToken ct)
    {
        var active = RequireRunning();
        await active.SendRequestAsync(Sdk.ExtensionProtocol.PluginsRefresh, IpcJsonTypes.EmptyObject, ct)
            .ConfigureAwait(false);
    }

    public async Task ApprovePluginAsync(
        string pluginId, string version, string sourceHash, string dependencyHash, string sdkVersion, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var active = RequireRunning();
        var payload = HostProtocolJson.SerializeApprove(
            pluginId, version, sourceHash, dependencyHash, sdkVersion);
        await active.SendRequestAsync(Sdk.ExtensionProtocol.PluginApprove, payload, ct).ConfigureAwait(false);
    }

    public async Task RevokePluginAsync(string pluginId, string version, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var active = RequireRunning();
        var payload = HostProtocolJson.SerializeRevoke(pluginId, version);
        await active.SendRequestAsync(Sdk.ExtensionProtocol.PluginRevoke, payload, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await lifecycle.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
            try
            {
                await StopCoreAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not ObjectDisposedException)
            {
            }
        }
        catch (Exception ex) when (ex is TimeoutException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            lifecycle.Dispose();
        }
    }

    /// <summary>Sends one <c>plugin.execute</c> request and maps the reply to a tool result.</summary>
    internal async ValueTask<ToolResult> InvokePluginToolAsync(
        string pluginId, string toolName, ToolCall call, ToolContext context, CancellationToken ct)
    {
        var active = RequireRunning();
        if (!HostProtocolJson.TryParseArguments(call.ArgumentsJson, out var arguments))
        {
            return new ToolResult(
                $"The arguments of '{toolName}' are not valid JSON: {call.ArgumentsJson}", IsError: true);
        }

        var payload = HostProtocolJson.SerializePluginExecute(
            pluginId, toolName, arguments, context.SessionId, context.WorkingDirectory, context.AgentDepth);
        IpcMessage response;
        try
        {
            response = await active.SendRequestAsync(Sdk.ExtensionProtocol.PluginExecute, payload, ct)
                .ConfigureAwait(false);
        }
        catch (IpcRemoteException ex)
        {
            return new ToolResult(
                $"The Extension Host could not run '{toolName}' of plugin '{pluginId}': {ex.Message}", IsError: true);
        }

        if (!HostProtocolJson.TryReadToolOutput(response.Result, out var content, out var isError))
        {
            return new ToolResult(content, IsError: true);
        }

        return new ToolResult(content, IsError: isError);
    }

    private async Task StopCoreAsync()
    {
        Interlocked.Exchange(ref stopping, 1);
        try
        {
            await TeardownAsync(sendHostStop: true, clearTools: true).ConfigureAwait(false);
            SetStatus(HostStatus.Stopped, null);
        }
        finally
        {
            Interlocked.Exchange(ref stopping, 0);
        }
    }

    /// <summary>Drops the peer, transport and host process. The peers' own close path faults pending requests
    /// with <see cref="IpcConnectionClosedException"/>.</summary>
    private async Task TeardownAsync(bool sendHostStop, bool clearTools)
    {
        IpcPeer? active;
        lock (peerGate)
        {
            active = peer;
            peer = null;
        }

        var stopAcknowledged = false;
        if (active is not null)
        {
            if (sendHostStop && !active.IsClosed)
            {
                var bound = options.StopRequestTimeout > TimeSpan.Zero
                    ? options.StopRequestTimeout
                    : TimeSpan.FromSeconds(5);
                using var stopTimeout = new CancellationTokenSource(bound);
                try
                {
                    await active.SendRequestAsync(Sdk.ExtensionProtocol.HostStop, IpcJsonTypes.EmptyObject, stopTimeout.Token)
                        .ConfigureAwait(false);
                    stopAcknowledged = true;
                }
                catch (Exception ex) when (ex is IpcConnectionClosedException or IpcTimeoutException
                    or IpcRemoteException or IOException or ObjectDisposedException or OperationCanceledException
                    or InvalidOperationException)
                {
                }
            }

            await DisposeQuietlyAsync(active).ConfigureAwait(false);
        }

        NamedPipeClientTransport? pendingTransport;
        lock (peerGate)
        {
            pendingTransport = transport;
            transport = null;
        }

        if (pendingTransport is not null)
        {
            try
            {
                await pendingTransport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        HostProcess? pendingHost;
        lock (peerGate)
        {
            pendingHost = hostProcess;
            hostProcess = null;
        }

        if (pendingHost is not null)
        {
            try
            {
                await pendingHost.TerminateAsync(options.ProcessExitTimeout, waitForSelfExit: stopAcknowledged).ConfigureAwait(false);
            }
            finally
            {
                await pendingHost.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (clearTools)
        {
            ClearTools();
        }
    }

    private async Task<PeerBundle> ConnectAsync(string pipeName, HostProcess? started, CancellationToken ct)
    {
        var timeout = connection.StartTimeout > TimeSpan.Zero ? connection.StartTimeout : TimeSpan.FromSeconds(30);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        Exception? last = null;
        var delay = TimeSpan.FromMilliseconds(20);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var candidate = CreatePeer(pipeName);
            try
            {
                await candidate.Peer.StartAsync(token).ConfigureAwait(false);
                return candidate;
            }
            catch (Exception ex) when (ex is IpcTimeoutException or IOException or InvalidOperationException
                or OperationCanceledException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                last = ex;
                await DisposeQuietlyAsync(candidate.Peer).ConfigureAwait(false);
                try
                {
                    await candidate.Transport.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeError) when (disposeError is IOException or ObjectDisposedException or InvalidOperationException)
                {
                }
            }

            if (started is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"The Extension Host exited before accepting the connection on pipe '{connection.PipeName}'." +
                    OutputTail(started), last);
            }

            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                throw ConnectFailed(last, timeout, ct, oce);
            }

            delay = delay < TimeSpan.FromMilliseconds(250)
                ? TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2)
                : delay;
        }
    }

    private Exception ConnectFailed(Exception? last, TimeSpan timeout, CancellationToken ct, OperationCanceledException oce)
    {
        if (ct.IsCancellationRequested)
        {
            return new OperationCanceledException(
                "Connecting to the Extension Host was canceled.", last ?? oce, ct);
        }

        return new TimeoutException(
            $"The Extension Host did not accept a connection on pipe '{connection.PipeName}' within " +
            $"{timeout.TotalMilliseconds:F0} ms.{OutputTail(null)}", last ?? oce);
    }

    private PeerBundle CreatePeer(string pipeName)
    {
        var pipe = new NamedPipeClientTransport(pipeName, options.PipeOptions);
        var created = new IpcPeer(pipe, new IpcPeerOptions
        {
            DefaultTimeout = options.RequestTimeout ?? Timeout.InfiniteTimeSpan,
        });
        var generation = Interlocked.Increment(ref nextGeneration);
        created.Closed += OnPeerClosed;
        created.RequestReceived += (message, ct) => OnRequestAsync(created, message, ct);
        created.NotificationReceived += message => OnNotification(generation, message);
        created.VersionMismatch += OnVersionMismatch;
        return new PeerBundle(created, pipe, generation);
    }

    private void OnPeerClosed(Exception? cause)
    {
        var intentional = Volatile.Read(ref stopping) != 0;
        var next = intentional ? HostStatus.Stopped : HostStatus.Failed;
        lock (statusGate)
        {
            var current = (HostStatus)status;
            if (current is HostStatus.Stopped or HostStatus.Failed)
            {
                return;
            }

            Volatile.Write(ref status, (int)next);
        }

        RaiseStatus(next, intentional ? null : DescribeClose(cause));
    }

    private void OnVersionMismatch(IpcVersionMismatchException mismatch) =>
        Volatile.Write(ref protocolProblem, mismatch.Message);

    private void OnNotification(long generation, IpcMessage message)
    {
        if (generation < Volatile.Read(ref registeredGeneration))
        {
            return;
        }

        if (string.Equals(message.Operation, Sdk.ExtensionProtocol.ToolsRegister, StringComparison.Ordinal))
        {
            RegisterTools(generation, message.Arguments);
        }
        else if (string.Equals(message.Operation, Sdk.ExtensionProtocol.ToolsUnregister, StringComparison.Ordinal))
        {
            UnregisterTools(generation, message.Arguments);
        }
    }

    private void RegisterTools(long generation, JsonElement? payload)
    {
        if (!HostProtocolJson.TryReadToolsRegister(payload, out var pluginId, out var declarations))
        {
            return;
        }

        if (declarations.Count == 0)
        {
            SetPluginTools(pluginId, null, generation);
            return;
        }

        var proxies = new ExtensionHostTool[declarations.Count];
        for (var index = 0; index < proxies.Length; index++)
        {
            proxies[index] = new ExtensionHostTool(this, pluginId, declarations[index]);
        }

        SetPluginTools(pluginId, proxies, generation);
    }

    private void UnregisterTools(long generation, JsonElement? payload)
    {
        if (!HostProtocolJson.TryReadToolsUnregister(payload, out var pluginId))
        {
            return;
        }

        SetPluginTools(pluginId, null, generation);
    }

    private void SetPluginTools(string pluginId, IReadOnlyList<ExtensionHostTool>? proxies, long generation)
    {
        var changed = false;
        lock (toolsGate)
        {
            toolsByPlugin.TryGetValue(pluginId, out var registered);
            if (registered.Tools is not null && registered.Generation > generation)
            {
                return;
            }

            if (proxies is null || proxies.Count == 0)
            {
                changed = toolsByPlugin.Remove(pluginId);
            }
            else if (registered.Tools is null || !SameDeclarations(registered.Tools, proxies))
            {
                toolsByPlugin[pluginId] = new PluginTools(generation, proxies);
                changed = true;
            }
            else
            {
                toolsByPlugin[pluginId] = registered with { Generation = generation };
            }

            if (!changed)
            {
                return;
            }

            RebuildToolSnapshots();
        }

        RaiseToolsChanged();
    }

    /// <summary>Drops the entries an earlier connection registered; entries the current connection published - even
    /// ones that arrived before the Running transition - survive.</summary>
    private void DropToolsOfEarlierConnections()
    {
        var current = Volatile.Read(ref registeredGeneration);
        lock (toolsGate)
        {
            List<string>? stale = null;
            foreach (var (pluginId, registered) in toolsByPlugin)
            {
                if (registered.Generation < current)
                {
                    (stale ??= []).Add(pluginId);
                }
            }

            if (stale is null)
            {
                return;
            }

            foreach (var pluginId in stale)
            {
                toolsByPlugin.Remove(pluginId);
            }

            RebuildToolSnapshots();
        }

        RaiseToolsChanged();
    }

    private static bool SameDeclarations(
        IReadOnlyList<ExtensionHostTool> registered, IReadOnlyList<ExtensionHostTool> incoming)
    {
        if (registered.Count != incoming.Count)
        {
            return false;
        }

        for (var index = 0; index < registered.Count; index++)
        {
            if (!SameDeclaration(registered[index], incoming[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameDeclaration(ExtensionHostTool registered, ExtensionHostTool incoming) =>
        string.Equals(registered.PluginId, incoming.PluginId, StringComparison.Ordinal) &&
        registered.Definition == incoming.Definition &&
        registered.SideEffect == incoming.SideEffect;

    private void ClearTools()
    {
        lock (toolsGate)
        {
            if (toolsByPlugin.Count == 0 && tools.Count == 0)
            {
                return;
            }

            toolsByPlugin.Clear();
            RebuildToolSnapshots();
        }

        RaiseToolsChanged();
    }

    private void RebuildToolSnapshots()
    {
        if (toolsByPlugin.Count == 0)
        {
            tools = [];
            toolsByPluginView = new Dictionary<string, IReadOnlyList<ITool>>(StringComparer.Ordinal);
            return;
        }

        var view = new Dictionary<string, IReadOnlyList<ITool>>(toolsByPlugin.Count, StringComparer.Ordinal);
        var flat = new List<ITool>(toolsByPlugin.Count);
        foreach (var (pluginId, published) in toolsByPlugin)
        {
            var proxies = published.Tools;
            var copy = new ITool[proxies.Count];
            for (var index = 0; index < proxies.Count; index++)
            {
                copy[index] = proxies[index];
                flat.Add(proxies[index]);
            }

            view[pluginId] = copy;
        }

        tools = flat;
        toolsByPluginView = view;
    }

    private Task OnRequestAsync(IpcPeer active, IpcMessage message, CancellationToken ct) =>
        string.Equals(message.Operation, Sdk.ExtensionProtocol.HostToolCall, StringComparison.Ordinal)
            ? HandleHostToolCallAsync(active, message, ct)
            : RespondErrorAsync(active, message.Id, "unsupported", $"The main program does not handle '{message.Operation}'.");

    /// <summary>A plugin (via the host) calls a main-program tool: resolve, apply the caller's approval path, run.</summary>
    private async Task HandleHostToolCallAsync(IpcPeer active, IpcMessage request, CancellationToken ct)
    {
        if (!HostProtocolJson.TryReadHostToolCall(
                request.Arguments, out var name, out var argumentsJson, out var workingDirectory, out var sessionId,
                out var depth))
        {
            await RespondToolOutputAsync(active, request.Id, "The host.tool_call request has no tool name.", isError: true)
                .ConfigureAwait(false);
            return;
        }

        if (workingDirectory is not null && !Path.IsPathRooted(workingDirectory))
        {
            await RespondToolOutputAsync(
                    active, request.Id,
                    $"'{name}' was not run: the working directory '{workingDirectory}' carried by the host is not a " +
                    "rooted path.",
                    isError: true)
                .ConfigureAwait(false);
            return;
        }

        var tool = toolResolver?.Invoke(name);
        if (tool is null)
        {
            await RespondToolOutputAsync(active, request.Id, $"Unknown tool '{name}'.", isError: true).ConfigureAwait(false);
            return;
        }

        var approval = approvalPath;
        if (approval is null)
        {
            await RespondToolOutputAsync(
                    active, request.Id,
                    $"'{name}' was not run: the main program has no approval path for plugin tool calls.",
                    isError: true)
                .ConfigureAwait(false);
            return;
        }

        var directory = workingDirectory ?? FallbackWorkingDirectory();
        ApprovalDecision decision;
        try
        {
            decision = await approval(new ApprovalRequest(name, argumentsJson, tool.SideEffect, directory), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            await RespondToolOutputAsync(
                    active, request.Id, $"'{name}' was not run: the approval path failed: {ex.Message}", isError: true)
                .ConfigureAwait(false);
            return;
        }

        if (decision != ApprovalDecision.Allow)
        {
            await RespondToolOutputAsync(active, request.Id, $"The main program denied '{name}'.", isError: true)
                .ConfigureAwait(false);
            return;
        }

        var call = new ToolCall($"hostcall-{Interlocked.Increment(ref nextCallId)}", name, argumentsJson);
        ToolResult result;
        try
        {
            result = await tool.InvokeAsync(call, new ToolContext(sessionId, directory, depth), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            await RespondToolOutputAsync(active, request.Id, $"{ex.GetType().Name}: {ex.Message}", isError: true)
                .ConfigureAwait(false);
            return;
        }

        await RespondToolOutputAsync(active, request.Id, result.Content ?? string.Empty, result.IsError).ConfigureAwait(false);
    }

    private string FallbackWorkingDirectory() =>
        !string.IsNullOrWhiteSpace(options.HostToolWorkingDirectory)
            ? options.HostToolWorkingDirectory
            : Environment.CurrentDirectory;

    private static Task RespondToolOutputAsync(IpcPeer active, long requestId, string content, bool isError) =>
        active.IsClosed ? Task.CompletedTask : RespondAsync(active, requestId, content, isError);

    private static Task RespondErrorAsync(IpcPeer active, long requestId, string code, string message) =>
        active.IsClosed ? Task.CompletedTask : RespondErrorCoreAsync(active, requestId, code, message);

    private static async Task RespondErrorCoreAsync(IpcPeer active, long requestId, string code, string message)
    {
        try
        {
            await active.RespondErrorAsync(requestId, new IpcError(code, message), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcConnectionClosedException or IOException or ObjectDisposedException
            or InvalidOperationException)
        {
        }
    }

    private static async Task RespondAsync(IpcPeer active, long requestId, string content, bool isError)
    {
        try
        {
            await active.RespondAsync(
                    requestId, HostProtocolJson.SerializeHostToolOutput(content, isError), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcConnectionClosedException or IOException or ObjectDisposedException
            or InvalidOperationException)
        {
        }
    }

    private IpcPeer RequireRunning()
    {
        lock (peerGate)
        {
            if (peer is { IsClosed: false } active)
            {
                return active;
            }
        }

        var current = Status;
        throw current switch
        {
            HostStatus.Stopped => new InvalidOperationException(
                "The Extension Host client is not running; call StartAsync first."),
            HostStatus.Starting => new InvalidOperationException("The Extension Host client is still starting."),
            _ => new IpcConnectionClosedException(
                $"The Extension Host connection is closed (status {current})."),
        };
    }

    private void SetStatus(HostStatus next, string? detail, bool forceDetail = false)
    {
        lock (statusGate)
        {
            if ((HostStatus)status != next)
            {
                Volatile.Write(ref status, (int)next);
            }
            else if (!forceDetail)
            {
                return;
            }
        }

        RaiseStatus(next, detail);
    }

    private void FailStatus(string detail) => SetStatus(HostStatus.Failed, detail, forceDetail: true);

    private void RaiseStatus(HostStatus next, string? detail)
    {
        var handler = StatusChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(next, detail);
        }
        catch (Exception)
        {
        }
    }

    private void RaiseToolsChanged()
    {
        var handler = ToolsChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler();
        }
        catch (Exception)
        {
        }
    }

    private static async Task DisposeQuietlyAsync(IpcPeer active)
    {
        try
        {
            await active.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException
            or OperationCanceledException)
        {
        }
    }

    private string DescribeFailure(Exception failure)
    {
        var tail = OutputTail(null);
        return failure.Message + tail;
    }

    private string DescribeClose(Exception? cause)
    {
        var problem = Volatile.Read(ref protocolProblem);
        var text = cause is null
            ? $"The Extension Host connection on pipe '{connection.PipeName}' closed."
            : $"The Extension Host connection on pipe '{connection.PipeName}' closed: {cause.Message}";
        if (!string.IsNullOrWhiteSpace(problem))
        {
            text += " " + problem;
        }

        return text + OutputTail(null);
    }

    private string OutputTail(HostProcess? process)
    {
        HostProcess? started;
        lock (peerGate)
        {
            started = hostProcess;
        }

        started ??= process;
        var tail = started?.OutputTail();
        return string.IsNullOrWhiteSpace(tail) ? string.Empty : Environment.NewLine + tail;
    }

    private readonly record struct PeerBundle(IpcPeer Peer, NamedPipeClientTransport Transport, long Generation);

    /// <summary>One plugin's tool proxies plus the connection generation that registered them.</summary>
    private readonly record struct PluginTools(long Generation, IReadOnlyList<ExtensionHostTool> Tools);
}
