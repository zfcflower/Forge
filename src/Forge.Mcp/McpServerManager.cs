using Forge.Abstractions;

namespace Forge.Mcp;

/// <summary>
/// Owns one <see cref="McpClient"/> per configured server: lazy start on first use, reconnect with backoff,
/// the current tool snapshot, and per-server status with the last error and stderr tail.
/// </summary>
public sealed class McpServerManager : IAsyncDisposable
{
    private readonly McpServerManagerOptions options;
    private readonly Dictionary<string, ManagedServer> servers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> nameByIdentity = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> identityByName = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Lock gate = new();
    private McpToolAdapter[] tools = [];
    private bool disposed;

    public McpServerManager(McpConfig config, McpServerManagerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        this.options = options ?? new McpServerManagerOptions();
        Diagnostics = config.Diagnostics;
        foreach (var server in config.Servers)
        {
            servers[server.Name] = new ManagedServer(server) { Enabled = !server.Disabled };
        }

        ServerNames = [.. servers.Keys];
    }

    public IReadOnlyList<string> ServerNames { get; }

    public IReadOnlyList<McpConfigDiagnostic> Diagnostics { get; }

    /// <summary>The adapters of every server that is currently ready, ordered by exposed name.</summary>
    public IReadOnlyList<McpToolAdapter> Tools => Volatile.Read(ref tools);

    public IReadOnlyList<McpServerState> States
    {
        get
        {
            lock (gate)
            {
                return [.. servers.Values.Select(StateOf)];
            }
        }
    }

    public event Action? ToolsChanged;

    public event Action<McpServerState>? StateChanged;

    public bool IsEnabled(string name) => Resolve(name).Enabled;

    public McpServerConfig GetServer(string name) => Resolve(name).Config;

    public McpServerState GetState(string name)
    {
        lock (gate)
        {
            return StateOf(Resolve(name));
        }
    }

    public McpClient? GetClient(string name)
    {
        lock (gate)
        {
            return Resolve(name).Client;
        }
    }

    /// <summary>Starts the server if it is not running; failures are recorded on the state and rethrown.</summary>
    public async Task<McpServerState> StartAsync(string name, CancellationToken ct)
    {
        await EnsureClientAsync(name, ct).ConfigureAwait(false);
        return GetState(name);
    }

    public async Task StartAllAsync(CancellationToken ct)
    {
        var tasks = new Task[ServerNames.Count];
        for (var i = 0; i < ServerNames.Count; i++)
        {
            tasks[i] = StartSafelyAsync(ServerNames[i], ct);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task StopAsync(string name, CancellationToken ct)
    {
        var server = Resolve(name);
        await server.StartGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DetachAsync(server).ConfigureAwait(false);
        }
        finally
        {
            server.StartGate.Release();
        }

        Mutate(server, managed =>
        {
            managed.Status = McpServerStatus.Stopped;
            managed.ConsecutiveFailures = 0;
            managed.LastError = null;
            managed.ReadyAt = null;
        });
    }

    public async Task RestartAsync(string name, CancellationToken ct)
    {
        await StopAsync(name, ct).ConfigureAwait(false);
        await EnsureClientAsync(name, ct).ConfigureAwait(false);
    }

    public async Task SetEnabledAsync(string name, bool enabled, CancellationToken ct)
    {
        var server = Resolve(name);
        if (enabled)
        {
            Mutate(server, managed => managed.Enabled = true);
            await EnsureClientAsync(name, ct).ConfigureAwait(false);
            return;
        }

        Mutate(server, managed => managed.Enabled = false);
        await StopAsync(name, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpToolAdapter>> RefreshToolsAsync(string name, CancellationToken ct)
    {
        var server = Resolve(name);
        await RefreshToolsCoreAsync(server, ct).ConfigureAwait(false);
        return server.Tools;
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);

        foreach (var server in servers.Values)
        {
            server.Reconnect?.Cancel();
            await DetachAsync(server).ConfigureAwait(false);
            await DisposeTransportAsync(server).ConfigureAwait(false);
            server.StartGate.Dispose();
        }

        lifetime.Dispose();
    }

    internal async Task<McpClient> EnsureClientAsync(string name, CancellationToken ct)
    {
        var server = Resolve(name);
        if (!server.Enabled)
        {
            throw new McpException($"MCP server '{name}' is disabled.");
        }

        if (server.Client is { IsConnected: true } running)
        {
            return running;
        }

        await server.StartGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (server.Client is { IsConnected: true } current)
            {
                return current;
            }

            return await ConnectAsync(server, ct).ConfigureAwait(false);
        }
        finally
        {
            server.StartGate.Release();
        }
    }

    internal async Task<McpToolCallResult> InvokeToolAsync(string serverName, string toolName, string argumentsJson, CancellationToken ct)
    {
        var client = await EnsureClientAsync(serverName, ct).ConfigureAwait(false);
        return await client.CallToolAsync(toolName, argumentsJson, ct).ConfigureAwait(false);
    }

    private async Task<McpClient> ConnectAsync(ManagedServer server, CancellationToken ct)
    {
        await DetachAsync(server).ConfigureAwait(false);
        Mutate(server, managed =>
        {
            managed.IntentionalStop = false;
            managed.Status = McpServerStatus.Starting;
            managed.LastError = null;
            managed.ReadyAt = null;
        });

        McpClient? client = null;
        try
        {
            var transport = server.Transport ??= CreateTransport(server.Config);
            var created = new McpClient(transport, new McpClientOptions
            {
                ClientName = options.ClientName,
                RequestTimeout = server.Config.Timeout ?? options.RequestTimeout,
                Roots = options.Roots,
                DisposeTransport = false,
            });
            client = created;
            created.Closed += error => OnClientClosed(server, created, error);
            created.ToolsChanged += () => OnToolsListChanged(server);

            await created.ConnectAsync(ct).ConfigureAwait(false);
            lock (gate)
            {
                server.Client = created;
                server.ConsecutiveFailures = 0;
            }

            await RefreshToolsCoreAsync(server, ct).ConfigureAwait(false);
            lock (gate)
            {
                server.Status = McpServerStatus.Ready;
                server.ReadyAt = DateTimeOffset.UtcNow;
            }

            RaiseState(server);
            return created;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (gate)
            {
                server.Client = null;
                server.Status = McpServerStatus.Stopped;
                server.LastError = null;
                server.ReadyAt = null;
                server.ConsecutiveFailures = 0;
                server.Tools = [];
                PublishToolsLocked();
            }

            RaiseState(server);
            ToolsChanged?.Invoke();
            await DisposeClientAsync(client).ConfigureAwait(false);
            await RecycleTransportAsync(server).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            var error = ex as McpException ?? new McpException($"Failed to start MCP server '{server.Config.Name}': {ex.Message}", ex);
            lock (gate)
            {
                server.Client = null;
                server.Status = McpServerStatus.Failed;
                server.LastError = error.Message;
                server.ConsecutiveFailures++;
                server.Tools = [];
                PublishToolsLocked();
            }

            RaiseState(server);
            ToolsChanged?.Invoke();
            await DisposeClientAsync(client).ConfigureAwait(false);

            await RecycleTransportAsync(server).ConfigureAwait(false);

            if (!server.IntentionalStop)
            {
                ScheduleReconnect(server);
            }

            throw error;
        }
    }

    private static async Task DisposeClientAsync(McpClient? client)
    {
        if (client is null)
        {
            return;
        }

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private async Task DetachAsync(ManagedServer server)
    {
        McpClient? client;
        CancellationTokenSource? reconnect;
        lock (gate)
        {
            server.IntentionalStop = true;
            client = server.Client;
            server.Client = null;
            reconnect = server.Reconnect;
            server.Reconnect = null;
            server.Tools = [];
            PublishToolsLocked();
        }

        if (reconnect is not null)
        {
            await reconnect.CancelAsync().ConfigureAwait(false);
        }

        if (client is not null)
        {
            try
            {
                await client.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        if (server.Transport is { IsConnected: true })
        {
            await StopTransportAsync(server).ConfigureAwait(false);
        }

        ToolsChanged?.Invoke();
    }

    /// <summary>Stops the transport so a stdio child cannot outlive the detach; it stays reusable for the next connect.</summary>
    private async Task StopTransportAsync(ManagedServer server)
    {
        IMcpTransport? transport;
        lock (gate)
        {
            transport = server.Transport;
        }

        if (transport is null)
        {
            return;
        }

        try
        {
            await transport.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Drops the transport of a failed generation, so the next attempt starts from a clean one.</summary>
    private async Task RecycleTransportAsync(ManagedServer server)
    {
        await StopTransportAsync(server).ConfigureAwait(false);
        await DisposeTransportAsync(server).ConfigureAwait(false);
    }

    private async Task DisposeTransportAsync(ManagedServer server)
    {
        IMcpTransport? transport;
        lock (gate)
        {
            transport = server.Transport;
            server.Transport = null;
        }

        if (transport is null)
        {
            return;
        }

        try
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private async Task RefreshToolsCoreAsync(ManagedServer server, CancellationToken ct)
    {
        var client = server.Client;
        if (client is null || !client.IsInitialized || client.Capabilities.Tools is false)
        {
            ClearTools(server);
            return;
        }

        IReadOnlyList<McpToolInfo> infos;
        try
        {
            infos = await client.ListToolsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException)
        {
            lock (gate)
            {
                server.LastError = $"tools/list failed: {ex.Message}";
            }

            return;
        }

        PublishTools(server, infos);
    }

    private void PublishTools(ManagedServer server, IReadOnlyList<McpToolInfo> infos)
    {
        var adapters = new McpToolAdapter[infos.Count];
        lock (gate)
        {
            for (var i = 0; i < infos.Count; i++)
            {
                var info = infos[i];
                adapters[i] = CreateAdapter(server, info, AssignNameLocked(server.Config.Name, info.Name));
            }

            server.Tools = adapters;
            PublishToolsLocked();
        }

        ToolsChanged?.Invoke();
    }

    private void ClearTools(ManagedServer server)
    {
        lock (gate)
        {
            server.Tools = [];
            PublishToolsLocked();
        }

        ToolsChanged?.Invoke();
    }

    /// <summary>Caller holds <see cref="gate"/>: one exposed name per tool identity for the manager's lifetime.</summary>
    private string AssignNameLocked(string serverName, string toolName)
    {
        var identity = serverName + '\u0001' + toolName;
        if (nameByIdentity.TryGetValue(identity, out var assigned))
        {
            return assigned;
        }

        var stable = McpToolNaming.Build(serverName, toolName);
        if (!McpToolNaming.IsLossless(serverName, toolName))
        {
            stable = McpToolNaming.Disambiguate(stable, identity);
        }

        if (identityByName.TryGetValue(stable, out var holder) && !string.Equals(holder, identity, StringComparison.Ordinal))
        {
            var attempt = 0;
            do
            {
                stable = McpToolNaming.Disambiguate(
                    McpToolNaming.Build(serverName, toolName),
                    attempt == 0 ? identity : identity + "#" + attempt);
                attempt++;
            }
            while (identityByName.ContainsKey(stable));
        }

        nameByIdentity[identity] = stable;
        identityByName[stable] = identity;
        return stable;
    }

    private McpToolAdapter CreateAdapter(ManagedServer server, McpToolInfo info, string exposedName)
    {
        var serverName = server.Config.Name;
        var toolName = info.Name;
        return new McpToolAdapter(
            serverName,
            toolName,
            exposedName,
            info.Description,
            info.InputSchemaJson,
            server.Config.IsReadOnlyTool(toolName) ? ToolSideEffect.ReadOnly : ToolSideEffect.Execute,
            (arguments, token) => InvokeToolAsync(serverName, toolName, arguments, token),
            options.MaxResultChars);
    }

    private void OnClientClosed(ManagedServer server, McpClient client, Exception? error)
    {
        lock (gate)
        {
            if (disposed || server.IntentionalStop || !ReferenceEquals(server.Client, client))
            {
                return;
            }

            server.Client = null;
            server.Status = McpServerStatus.Failed;
            server.LastError = error?.Message ?? "The MCP connection closed.";
            server.ConsecutiveFailures++;
            server.Tools = [];
            server.ReadyAt = null;
            PublishToolsLocked();
        }

        RaiseState(server);
        ToolsChanged?.Invoke();
        ScheduleReconnect(server);
        _ = Task.Run(async () =>
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        });
    }

    private void OnToolsListChanged(ManagedServer server) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshToolsCoreAsync(server, lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        });

    private void ScheduleReconnect(ManagedServer server)
    {
        var failures = server.ConsecutiveFailures;
        if (options.MaxReconnectAttempts <= 0 || failures > options.MaxReconnectAttempts)
        {
            return;
        }

        var delay = Backoff(failures);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var previous = Interlocked.Exchange(ref server.Reconnect, cts);
        if (previous is not null)
        {
            previous.Cancel();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (ReferenceEquals(server.Reconnect, cts))
                    {
                        server.Reconnect = null;
                    }
                }

                await EnsureClientAsync(server.Config.Name, lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        });
    }

    private TimeSpan Backoff(int failures)
    {
        var factor = Math.Pow(2, Math.Max(0, failures - 1));
        var ticks = options.InitialReconnectDelay.Ticks * factor;
        return ticks >= options.MaxReconnectDelay.Ticks
            ? options.MaxReconnectDelay
            : TimeSpan.FromTicks((long)ticks);
    }

    private IMcpTransport CreateTransport(McpServerConfig config)
    {
        if (options.TransportFactory is { } factory)
        {
            return factory(config);
        }

        if (config.IsHttp)
        {
            return new StreamableHttpTransport(new McpHttpOptions
            {
                Endpoint = new Uri(config.Url!),
                Headers = config.Headers,
                HttpClient = options.HttpClient,
            });
        }

        return new StdioTransport(new McpStdioOptions
        {
            Command = config.Command!,
            Args = config.Args,
            Env = config.Env,
            WorkingDirectory = config.WorkingDirectory,
            StderrCapacityChars = options.StderrCapacityChars,
        });
    }

    private void PublishToolsLocked()
    {
        var snapshot = servers.Values
            .SelectMany(static server => server.Tools)
            .OrderBy(static tool => tool.ExposedName, StringComparer.Ordinal)
            .ToArray();
        Volatile.Write(ref tools, snapshot);
    }

    private void Mutate(ManagedServer server, Action<ManagedServer> mutate)
    {
        lock (gate)
        {
            mutate(server);
        }

        RaiseState(server);
    }

    private void RaiseState(ManagedServer server)
    {
        McpServerState state;
        lock (gate)
        {
            state = StateOf(server);
        }

        StateChanged?.Invoke(state);
    }

    private McpServerState StateOf(ManagedServer server)
    {
        var stderr = (server.Transport as StdioTransport)?.StderrTail;
        return new McpServerState(
            server.Config.Name,
            server.Status,
            server.Enabled,
            server.LastError,
            string.IsNullOrEmpty(stderr) ? null : stderr,
            server.ReadyAt,
            server.ConsecutiveFailures);
    }

    private ManagedServer Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (gate)
        {
            return servers.TryGetValue(name, out var server)
                ? server
                : throw new McpException($"Unknown MCP server '{name}'.");
        }
    }

    private async Task StartSafelyAsync(string name, CancellationToken ct)
    {
        try
        {
            await EnsureClientAsync(name, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private sealed class ManagedServer(McpServerConfig config)
    {
        public McpServerConfig Config { get; } = config;

        public SemaphoreSlim StartGate { get; } = new(1, 1);

        public bool Enabled;
        public McpServerStatus Status;
        public string? LastError;
        public DateTimeOffset? ReadyAt;
        public int ConsecutiveFailures;
        public bool IntentionalStop;
        public McpClient? Client;
        public IMcpTransport? Transport;
        public McpToolAdapter[] Tools = [];
        public CancellationTokenSource? Reconnect;
    }
}
