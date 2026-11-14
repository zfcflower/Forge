using Forge.Abstractions;

namespace Forge.Mcp;

/// <summary>
/// Owns one <see cref="McpClient"/> per configured server: lazy start on first use, reconnect with backoff,
/// the current tool snapshot, and per-server status with the last error and stderr tail.
/// <para>The server set follows the config file: <see cref="ApplyConfigAsync"/> diffs a freshly read config against the
/// running one (add, remove, replace) instead of recreating the manager, so an edit in the settings page keeps every other
/// server's process and tools alive.</para>
/// </summary>
public sealed class McpServerManager : IAsyncDisposable
{
    private readonly Func<McpServerManagerOptions> optionsAccessor;
    private readonly Dictionary<string, ManagedServer> servers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> nameByIdentity = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> identityByName = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Lock gate = new();
    private McpToolAdapter[] tools = [];
    private McpConfigDiagnostic[] diagnostics = [];
    private bool disposed;

    public McpServerManager(McpConfig config, McpServerManagerOptions? options = null)
        : this(config, options is null ? static () => new McpServerManagerOptions() : () => options)
    {
    }

    /// <summary>
    /// The same manager, with its options read through <paramref name="options"/> every time one is needed. The app passes an
    /// accessor over the live preferences, so a changed request timeout or reconnect delay applies to the next connection
    /// without restarting a single server.
    /// </summary>
    public McpServerManager(McpConfig config, Func<McpServerManagerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);
        optionsAccessor = options;
        diagnostics = [.. config.Diagnostics];
        foreach (var server in config.Servers)
        {
            servers[server.Name] = new ManagedServer(server) { Enabled = !server.Disabled };
        }
    }

    /// <summary>The configured servers, in the order the config files listed them; a snapshot taken under the gate.</summary>
    public IReadOnlyList<string> ServerNames
    {
        get
        {
            lock (gate)
            {
                return [.. servers.Keys];
            }
        }
    }

    /// <summary>Problems the last config read reported (a broken entry, an unreadable file); empty when it was clean.</summary>
    public IReadOnlyList<McpConfigDiagnostic> Diagnostics => Volatile.Read(ref diagnostics);

    private McpServerManagerOptions Options => optionsAccessor();

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
        var names = ServerNames;
        var tasks = new Task[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            tasks[i] = StartSafelyAsync(names[i], ct);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes the running servers match <paramref name="config"/>, which is what a save in the settings page calls. A server
    /// that disappeared is stopped and its transport released; a server whose entry changed is restarted with the new entry (its
    /// old process must not keep running on a config the file no longer has); a new one is registered enabled and starts lazily
    /// on first use. A failure while restarting is recorded on that server's state and never fails the whole apply.
    /// </summary>
    public async Task ApplyConfigAsync(McpConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        List<ManagedServer> removed = [];
        List<ManagedServer> replaced = [];
        List<McpServerConfig> restarted = [];
        List<ManagedServer> added = [];
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            diagnostics = [.. config.Diagnostics];
            // A hand-built config may list one name twice; the loader never does, and the last entry must win so the result does
            // not depend on which half of the apply saw which copy.
            var wanted = new Dictionary<string, McpServerConfig>(config.Servers.Count, StringComparer.Ordinal);
            foreach (var server in config.Servers)
            {
                wanted[server.Name] = server;
            }

            var incoming = new HashSet<string>(wanted.Count, StringComparer.Ordinal);
            foreach (var server in wanted.Values)
            {
                incoming.Add(server.Name);
                if (!servers.TryGetValue(server.Name, out var managed))
                {
                    managed = new ManagedServer(server) { Enabled = !server.Disabled };
                    servers[server.Name] = managed;
                    added.Add(managed);
                }
                else if (!SameConfig(managed.Config, server))
                {
                    replaced.Add(managed);
                    restarted.Add(server);
                }
            }

            foreach (var pair in servers.ToArray())
            {
                if (!incoming.Contains(pair.Key))
                {
                    servers.Remove(pair.Key);
                    removed.Add(pair.Value);
                }
            }
        }

        foreach (var server in removed)
        {
            await RetireAsync(server, ct).ConfigureAwait(false);
        }

        for (var i = 0; i < replaced.Count; i++)
        {
            var managed = replaced[i];
            await StopServerAsync(managed, ct).ConfigureAwait(false);

            // The transport captured the entry's command, arguments, environment and working directory (or its endpoint and
            // headers) when it was created: keeping it would restart the old process under the new entry's name. The entry in the
            // file is also what decides whether the server is enabled — a hand edit removing `disabled` has to take effect.
            await RecycleTransportAsync(managed).ConfigureAwait(false);
            lock (gate)
            {
                managed.Config = restarted[i];
                managed.Enabled = !restarted[i].Disabled;
                managed.ConsecutiveFailures = 0;
                managed.LastError = null;
                managed.ReadyAt = null;
            }

            RaiseState(managed);
            if (managed.Enabled)
            {
                try
                {
                    await EnsureClientAsync(managed.Config.Name, ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The state already carries the failure; a broken edit must not abort the rest of the apply.
                }
            }
        }

        foreach (var server in added)
        {
            RaiseState(server);
        }

        ToolsChanged?.Invoke();
    }

    /// <summary>
    /// The entry an exposed tool name belongs to, or null when the name is not a tool of a current server. Used by the approval
    /// card: the tool name a call carries is the exposed one, and the server it came from is what the user must judge.
    /// </summary>
    public McpServerConfig? ConfigOfTool(string exposedToolName)
    {
        if (string.IsNullOrWhiteSpace(exposedToolName))
        {
            return null;
        }

        lock (gate)
        {
            if (!identityByName.TryGetValue(exposedToolName, out var identity))
            {
                return null;
            }

            var separator = identity.IndexOf('\u0001', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return null;
            }

            return servers.TryGetValue(identity[..separator], out var server) ? server.Config : null;
        }
    }

    /// <summary>The state of a server, or null when the config no longer has it; never throws.</summary>
    private McpServerState? StateOrNull(string name)
    {
        lock (gate)
        {
            return servers.TryGetValue(name, out var server) ? StateOf(server) : null;
        }
    }

    /// <summary>
    /// Every configured entry in one snapshot, taken under the same lock. A caller that walks <see cref="ServerNames"/> and asks
    /// for each name separately can lose that race against <see cref="ApplyConfigAsync"/> removing one; this cannot.
    /// </summary>
    public IReadOnlyList<McpServerConfig> ConfiguredServers()
    {
        lock (gate)
        {
            var configs = new McpServerConfig[servers.Count];
            var index = 0;
            foreach (var server in servers.Values)
            {
                configs[index++] = server.Config;
            }

            return configs;
        }
    }

    /// <summary>The tools one server currently exposes; empty while it is stopped, failed or still starting.</summary>
    public IReadOnlyList<McpToolAdapter> ToolsOf(string name) => Resolve(name).Tools;

    /// <summary>
    /// The settings page's "test": connect (if needed), re-list the tools and report how long that took. Failures come back as
    /// a result rather than an exception, because the test button always has something to say.
    /// </summary>
    public async Task<McpServerProbe> ProbeAsync(string name, CancellationToken ct)
    {
        var started = TimeProvider.System.GetTimestamp();
        try
        {
            await EnsureClientAsync(name, ct).ConfigureAwait(false);
            await RefreshToolsAsync(name, ct).ConfigureAwait(false);
            var state = StateOrNull(name);
            var client = GetClient(name);
            return new McpServerProbe(
                name,
                state?.Status ?? McpServerStatus.Ready,
                TimeProvider.System.GetElapsedTime(started),
                ToolsOf(name),
                client?.Capabilities ?? McpServerCapabilities.None,
                state?.LastError,
                state?.StderrTail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // An unknown name must come back as a failed probe too: the button always has something to say, and GetState
            // would throw for a name that is not in the config at all.
            var state = StateOrNull(name);
            return new McpServerProbe(
                name,
                McpServerStatus.Failed,
                TimeProvider.System.GetElapsedTime(started),
                [],
                McpServerCapabilities.None,
                exception.Message,
                state?.StderrTail);
        }
    }

    public async Task StopAsync(string name, CancellationToken ct) =>
        await StopServerAsync(Resolve(name), ct).ConfigureAwait(false);

    /// <summary>Stops one server object: the named stop and the config apply share it.</summary>
    private async Task StopServerAsync(ManagedServer server, CancellationToken ct)
    {
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

    /// <summary>
    /// Tears a removed server down for good: its process, its client and its transport. It is already out of the map, so a
    /// resolve can no longer reach it and no new start can begin; a failure is swallowed for the same reason a stop's is.
    /// </summary>
    private async Task RetireAsync(ManagedServer server, CancellationToken ct)
    {
        await server.StartGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DetachAsync(server).ConfigureAwait(false);
            await DisposeTransportAsync(server).ConfigureAwait(false);
        }
        finally
        {
            server.StartGate.Release();
        }
    }

    /// <summary>Whether a freshly read entry is the same one the running server was started with.</summary>
    private static bool SameConfig(McpServerConfig left, McpServerConfig right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        string.Equals(left.Command, right.Command, StringComparison.Ordinal) &&
        string.Equals(left.WorkingDirectory, right.WorkingDirectory, StringComparison.Ordinal) &&
        string.Equals(left.Url, right.Url, StringComparison.Ordinal) &&
        left.Disabled == right.Disabled &&
        left.Timeout == right.Timeout &&
        SequenceEqual(left.Args, right.Args) &&
        SequenceEqual(left.ReadOnlyTools, right.ReadOnlyTools) &&
        PairsEqual(left.Env, right.Env) &&
        PairsEqual(left.Headers, right.Headers);

    private static bool SequenceEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PairsEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var value) || !string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
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
        var options = Options;
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

            // The transport is about to be recycled, and a failed start is exactly when its stderr explains why.
            CaptureStderr(server);
            RaiseState(server);

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

        CaptureStderr(server);

        if (server.Transport is { IsConnected: true })
        {
            await StopTransportAsync(server).ConfigureAwait(false);
        }

        ToolsChanged?.Invoke();
    }

    /// <summary>
    /// Keeps the tail of a stdio server's stderr on the managed server, so it outlives the transport that produced it: the state
    /// of a server that failed to start would otherwise lose the one line explaining why before the page could read it.
    /// </summary>
    private void CaptureStderr(ManagedServer server)
    {
        if (server.Transport is not StdioTransport stdio)
        {
            return;
        }

        var tail = stdio.StderrTail;
        if (string.IsNullOrEmpty(tail))
        {
            return;
        }

        lock (gate)
        {
            server.LastStderr = tail;
        }
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
        var options = Options;
        var maxSchema = options.MaxToolSchemaChars;
        var adapters = new List<McpToolAdapter>(infos.Count);
        string? skipped = null;
        var skippedCount = 0;
        lock (gate)
        {
            for (var i = 0; i < infos.Count; i++)
            {
                var info = infos[i];
                if (maxSchema > 0 && info.InputSchemaJson.Length > maxSchema)
                {
                    // Every request carries every offered schema: one oversized tool must not fill the context on its own.
                    skipped ??= info.Name;
                    skippedCount++;
                    continue;
                }

                adapters.Add(CreateAdapter(server, info, AssignNameLocked(server.Config.Name, info.Name), options));
            }

            server.Tools = [.. adapters];
            if (skippedCount > 0)
            {
                server.LastError =
                    $"{skippedCount} tool(s) not offered because the input schema exceeds {maxSchema} characters (first: '{skipped}').";
            }

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

    private McpToolAdapter CreateAdapter(ManagedServer server, McpToolInfo info, string exposedName, McpServerManagerOptions options)
    {
        var serverName = server.Config.Name;
        var toolName = info.Name;
        return new McpToolAdapter(
            serverName,
            toolName,
            exposedName,
            CapDescription(info.Description, options.MaxToolDescriptionChars),
            info.InputSchemaJson,
            server.Config.IsReadOnlyTool(toolName) ? ToolSideEffect.ReadOnly : ToolSideEffect.Execute,
            (arguments, token) => InvokeToolAsync(serverName, toolName, arguments, token),
            options.MaxResultChars);
    }

    private static string? CapDescription(string? description, int max)
    {
        if (description is null || max <= 0 || description.Length <= max)
        {
            return description;
        }

        // Never split a surrogate pair: half of one is not valid text for a provider's JSON encoder.
        var keep = Math.Max(0, max - 1);
        if (keep > 0 && char.IsHighSurrogate(description[keep - 1]))
        {
            keep--;
        }

        return string.Concat(description.AsSpan(0, keep), "…");
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
        var options = Options;
        if (options.MaxReconnectAttempts <= 0 || failures > options.MaxReconnectAttempts)
        {
            return;
        }

        var delay = Backoff(failures, options);
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

    private static TimeSpan Backoff(int failures, McpServerManagerOptions options)
    {
        var factor = Math.Pow(2, Math.Max(0, failures - 1));
        var ticks = options.InitialReconnectDelay.Ticks * factor;
        return ticks >= options.MaxReconnectDelay.Ticks
            ? options.MaxReconnectDelay
            : TimeSpan.FromTicks((long)ticks);
    }

    private IMcpTransport CreateTransport(McpServerConfig config)
    {
        var options = Options;
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
        var stderr = (server.Transport as StdioTransport)?.StderrTail ?? server.LastStderr;
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
        /// <summary>Replaced under the gate when a fresh config read changed this server's entry.</summary>
        public McpServerConfig Config { get; set; } = config;

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

        /// <summary>The last stdio stderr tail seen, kept after the transport is gone (see <see cref="CaptureStderr"/>).</summary>
        public string? LastStderr;
    }
}
