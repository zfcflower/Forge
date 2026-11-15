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
    private readonly IMcpTokenStore? tokenStore;
    private readonly IMcpOAuthUi? oauthUi;
    private readonly TimeProvider? oauthTime;
    private McpOAuth? oauth;
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
        var initial = options();
        tokenStore = initial.TokenStore;
        oauthUi = initial.OAuthUi;
        oauthTime = initial.TimeProvider;
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

    /// <summary>A server's <c>notifications/resources/list_changed</c> arrived; the name is the configured server.</summary>
    public event Action<string>? ResourcesChanged;

    /// <summary>A server's <c>notifications/resources/updated</c> arrived for one subscribed resource.</summary>
    public event Action<string, McpResourceUpdated>? ResourceUpdated;

    /// <summary>True when both the token store and the browser UI are wired, so a server can run the interactive sign-in.</summary>
    public bool IsOAuthAvailable => tokenStore is not null && oauthUi is not null;

    /// <summary>The <see cref="McpServerState.LastError"/> a 401 without a usable token records; the page localizes it via <see cref="McpServerState.NeedsSignIn"/>.</summary>
    public const string SignInRequiredMessage = "Sign-in required";

    /// <summary>Whether this server holds a token in memory (loaded by a connect or minted by a sign-in). Unknown names read false.</summary>
    public bool IsSignedIn(string name)
    {
        lock (gate)
        {
            return servers.TryGetValue(name, out var server) && server.CachedToken is not null;
        }
    }

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
                managed.CachedToken = null;
                managed.OAuthChallenge = null;
                managed.NeedsSignIn = false;
                // A changed entry may point at a different server entirely: the old resource URIs are not its to keep.
                managed.Subscriptions.Clear();
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

    /// <summary>Runs the interactive OAuth sign-in for one streamable HTTP server, stores the tokens and reconnects.</summary>
    public async Task SignInAsync(string name, CancellationToken ct)
    {
        var server = Resolve(name);
        if (tokenStore is null || oauthUi is null)
        {
            throw new McpException($"OAuth is not available for MCP server '{name}': no token store or browser UI was wired.");
        }

        var challenge = server.OAuthChallenge;
        if (challenge is null)
        {
            // Trigger a connect through the start gate, so a pending reconnect timer cannot race a second client into existence;
            // the transport's 401 hook records the RFC 9728 challenge. A server that never advertised OAuth has nothing to
            // sign in to.
            try
            {
                await EnsureClientAsync(name, ct).ConfigureAwait(false);
            }
            catch (McpException)
            {
            }

            challenge = server.OAuthChallenge;
        }

        if (challenge is null)
        {
            throw new McpException($"MCP server '{name}' did not advertise OAuth (no resource_metadata challenge).");
        }

        var key = OAuthKey(server.Config, challenge.Resource);
        var tokens = await OAuth.SignInAsync(challenge, ct).ConfigureAwait(false);
        await tokenStore.SaveAsync(key, tokens, ct).ConfigureAwait(false);
        lock (gate)
        {
            server.CachedToken = tokens;
            server.OAuthChallenge = challenge;
            server.NeedsSignIn = false;
            server.LastError = null;
        }

        await RestartAsync(name, ct).ConfigureAwait(false);
    }

    /// <summary>Clears a server's stored token and stops it; the next connect 401s and shows the sign-in action again.</summary>
    public async Task SignOutAsync(string name, CancellationToken ct)
    {
        var server = Resolve(name);
        if (tokenStore is null)
        {
            return;
        }

        var key = OAuthKey(server.Config, server.OAuthChallenge?.Resource);
        await tokenStore.DeleteAsync(key, ct).ConfigureAwait(false);
        lock (gate)
        {
            server.CachedToken = null;
            server.OAuthChallenge = null;
            server.NeedsSignIn = false;
        }

        await StopAsync(name, ct).ConfigureAwait(false);
    }

    /// <summary>Lists one server's resources; empty when it is not ready or advertises no resources.</summary>
    public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(string name, CancellationToken ct) =>
        RunAgainstServerAsync(name, ct, async client =>
            client.Capabilities.Resources ? await client.ListResourcesAsync(ct).ConfigureAwait(false) : []);

    public Task<McpResourceResult> ReadResourceAsync(string name, string uri, CancellationToken ct) =>
        RunAgainstServerAsync(name, ct, client => client.ReadResourceAsync(uri, ct));

    public async Task SubscribeAsync(string name, string uri, CancellationToken ct)
    {
        var server = Resolve(name);
        await RunAgainstServerAsync(name, ct, async client =>
        {
            if (!client.Capabilities.ResourcesSubscribe)
            {
                throw new McpException($"MCP server '{name}' does not support resource subscriptions.");
            }

            await client.SubscribeAsync(uri, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        lock (gate)
        {
            // The manager keeps the set the user asked for, not the client's: a subscription lives only as long as the session
            // that sent it, so this set is what a reconnect replays (see ReplaySubscriptionsAsync).
            server.Subscriptions.Add(uri);
        }
    }

    /// <summary>The resources the manager keeps subscribed for one server; empty for a name the config no longer has.</summary>
    public IReadOnlyList<string> SubscriptionsOf(string name)
    {
        lock (gate)
        {
            return servers.TryGetValue(name, out var server) ? [.. server.Subscriptions] : [];
        }
    }

    /// <summary>Whether the manager holds a subscription for one resource; the settings page's rows read their badge from it.</summary>
    public bool IsSubscribed(string name, string uri)
    {
        lock (gate)
        {
            return servers.TryGetValue(name, out var server) && server.Subscriptions.Contains(uri);
        }
    }

    public async Task UnsubscribeAsync(string name, string uri, CancellationToken ct)
    {
        var server = Resolve(name);
        await RunAgainstServerAsync(name, ct, client => client.UnsubscribeAsync(uri, ct)).ConfigureAwait(false);
        lock (gate)
        {
            server.Subscriptions.Remove(uri);
        }
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
        return await RunAgainstServerAsync(
            serverName,
            ct,
            client => client.CallToolAsync(toolName, argumentsJson, ct)).ConfigureAwait(false);
    }

    internal async Task<McpToolCallResult> InvokeToolAsync(
        string serverName,
        string toolName,
        string argumentsJson,
        Action<string>? progress,
        CancellationToken ct)
    {
        return await RunAgainstServerAsync(
            serverName,
            ct,
            client => client.CallToolAsync(toolName, argumentsJson, ct, progress)).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one request against a server's session. A 401 that arrives mid-session (the token expired or was revoked while the
    /// connection stayed up) marks the server as needing sign-in before the failure reaches the caller, so the settings page's
    /// sign-in action appears instead of only a red line the user cannot act on.
    /// </summary>
    private async Task<TResult> RunAgainstServerAsync<TResult>(string name, CancellationToken ct, Func<McpClient, Task<TResult>> body)
    {
        var server = Resolve(name);
        try
        {
            var client = await EnsureClientAsync(name, ct).ConfigureAwait(false);
            return await body(client).ConfigureAwait(false);
        }
        catch (McpOAuthRequiredException exception)
        {
            RequireSignIn(server, exception);
            throw;
        }
    }

    private async Task RunAgainstServerAsync(string name, CancellationToken ct, Func<McpClient, Task> body)
    {
        var server = Resolve(name);
        try
        {
            var client = await EnsureClientAsync(name, ct).ConfigureAwait(false);
            await body(client).ConfigureAwait(false);
        }
        catch (McpOAuthRequiredException exception)
        {
            RequireSignIn(server, exception);
            throw;
        }
    }

    /// <summary>Records that the server needs an interactive sign-in, keeping the challenge a later sign-in starts from.</summary>
    private void RequireSignIn(ManagedServer server, McpOAuthRequiredException exception)
    {
        lock (gate)
        {
            server.NeedsSignIn = true;
            server.LastError = SignInRequiredMessage;
            server.OAuthChallenge = exception.Challenge ?? server.OAuthChallenge;
        }

        RaiseState(server);
    }

    private McpOAuth OAuth
    {
        get
        {
            if (oauth is not null)
            {
                return oauth;
            }

            if (tokenStore is null)
            {
                throw new McpException("OAuth is not configured for this MCP server manager.");
            }

            var client = Options.HttpClient ?? McpOAuth.DefaultClient;
            return oauth = new McpOAuth(client, oauthUi, oauthTime);
        }
    }

    /// <summary>
    /// The transport's 401 hook: load the cached token, refresh it when it is expired, and hand the access token back. A null
    /// result means "no usable token" — the transport then reports <see cref="McpOAuthRequiredException"/> and the server
    /// becomes needing-sign-in. This path never opens a browser.
    /// <para>The refresh is single-flight per server: with a rotating refresh token two concurrent 401s would each refresh and
    /// the slower one would present a token the server has already invalidated, which shows up as a sign-in failure the user
    /// never caused. The second caller therefore waits and then reuses whatever the first one stored.</para>
    /// </summary>
    private async ValueTask<string?> ProvideTokenAsync(ManagedServer server, McpOAuthChallenge challenge, CancellationToken ct)
    {
        if (tokenStore is null)
        {
            return null;
        }

        lock (gate)
        {
            server.OAuthChallenge = challenge;
        }

        var key = OAuthKey(server.Config, challenge.Resource);
        if (await CachedOrStoredTokenAsync(server, key, ct).ConfigureAwait(false) is not { } tokens)
        {
            return null;
        }

        if (!IsExpired(tokens))
        {
            return tokens.AccessToken;
        }

        await server.RefreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A 401 that arrived while this one waited may already have refreshed the token: reuse it instead of refreshing
            // (and rotating) a second time.
            tokens = server.CachedToken ?? tokens;
            if (!IsExpired(tokens))
            {
                return tokens.AccessToken;
            }

            if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            {
                return null;
            }

            var refreshed = await OAuth.RefreshAsync(tokens, challenge, ct).ConfigureAwait(false);
            lock (gate)
            {
                server.CachedToken = refreshed;
            }

            await tokenStore.SaveAsync(key, refreshed, ct).ConfigureAwait(false);
            return refreshed.AccessToken;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A rejected refresh (invalid or rotated grant), a network failure and an authorization server that is down are all
            // the same to the transport: no usable token right now. The server goes back to "sign in" instead of failing a
            // tool call with a detail the page cannot act on.
            return null;
        }
        finally
        {
            server.RefreshGate.Release();
        }
    }

    private async ValueTask<McpTokenSet?> CachedOrStoredTokenAsync(ManagedServer server, string key, CancellationToken ct)
    {
        McpTokenSet? tokens;
        lock (gate)
        {
            tokens = server.CachedToken;
        }

        tokens ??= await tokenStore!.LoadAsync(key, ct).ConfigureAwait(false);
        if (tokens is not null)
        {
            lock (gate)
            {
                server.CachedToken = tokens;
            }
        }

        return tokens;
    }

    private static bool IsExpired(McpTokenSet tokens) =>
        tokens.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);

    /// <summary>The store key is stable across reconnects: the URL origin plus the resource the tokens were issued for.</summary>
    internal static string OAuthKey(McpServerConfig config, string? resource)
    {
        var origin = config.IsHttp && Uri.TryCreate(config.Url, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : config.Name;
        return origin + '\u0000' + (resource ?? string.Empty);
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
            var transport = server.Transport ??= CreateTransport(server);
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
            created.ResourcesChanged += () => OnResourcesListChanged(server);
            created.ResourceUpdated += update => OnResourceUpdated(server, update);

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
            await ReplaySubscriptionsAsync(server, created, ct).ConfigureAwait(false);
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
            var needsSignIn = ex is McpOAuthRequiredException;
            var error = ex as McpException ?? new McpException($"Failed to start MCP server '{server.Config.Name}': {ex.Message}", ex);
            lock (gate)
            {
                server.Client = null;
                server.Status = McpServerStatus.Failed;
                server.LastError = needsSignIn ? SignInRequiredMessage : error.Message;
                server.NeedsSignIn = needsSignIn;
                // The challenge is what the sign-in action starts from, so a connect that 401'd keeps it even though no token
                // was ever handed to the transport.
                server.OAuthChallenge = (ex as McpOAuthRequiredException)?.Challenge ?? server.OAuthChallenge;
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
            (arguments, progress, token) => InvokeToolAsync(serverName, toolName, arguments, progress, token),
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
            server.NeedsSignIn = error is McpOAuthRequiredException;
            server.LastError = server.NeedsSignIn ? SignInRequiredMessage : error?.Message ?? "The MCP connection closed.";
            server.OAuthChallenge = (error as McpOAuthRequiredException)?.Challenge ?? server.OAuthChallenge;
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

    private void OnResourcesListChanged(ManagedServer server) => ResourcesChanged?.Invoke(server.Config.Name);

    private void OnResourceUpdated(ManagedServer server, McpResourceUpdated update) =>
        ResourceUpdated?.Invoke(server.Config.Name, update);

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

    private IMcpTransport CreateTransport(ManagedServer server)
    {
        var config = server.Config;
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
                BearerToken = server.CachedToken?.AccessToken,
                OAuthTokenProvider = tokenStore is null
                    ? null
                    : (challenge, ct) => ProvideTokenAsync(server, challenge, ct),
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

    /// <summary>
    /// A subscription belongs to the MCP session that sent it, so a reconnected client starts with none. The manager keeps the
    /// set the user asked for and replays it here: the settings page's resource browser then keeps receiving
    /// <c>notifications/resources/updated</c> across a dropped connection instead of going quiet until someone re-subscribes.
    /// A resource the server no longer has is reported on the state and skipped; it must not fail the connect.
    /// </summary>
    private async Task ReplaySubscriptionsAsync(ManagedServer server, McpClient client, CancellationToken ct)
    {
        string[] wanted;
        lock (gate)
        {
            wanted = [.. server.Subscriptions];
        }

        if (wanted.Length == 0 || !client.Capabilities.ResourcesSubscribe)
        {
            return;
        }

        string? failed = null;
        foreach (var uri in wanted)
        {
            try
            {
                await client.SubscribeAsync(uri, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is McpException or HttpRequestException)
            {
                failed ??= $"resources/subscribe for '{uri}' failed: {exception.Message}";
            }
        }

        if (failed is not null)
        {
            Mutate(server, managed => managed.LastError = failed);
        }
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
            server.ConsecutiveFailures,
            server.NeedsSignIn);
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
        public McpTokenSet? CachedToken;
        public McpOAuthChallenge? OAuthChallenge;
        public bool NeedsSignIn;

        /// <summary>The resources the user subscribed to; replayed onto every new session (see ReplaySubscriptionsAsync).</summary>
        public HashSet<string> Subscriptions = new(StringComparer.Ordinal);

        /// <summary>
        /// Serializes token refreshes for this server, so two concurrent 401s cannot rotate the refresh token twice. Deliberately
        /// not disposed: a refresh still winding down would throw on release, and a SemaphoreSlim without a wait handle owns
        /// nothing that needs freeing.
        /// </summary>
        public SemaphoreSlim RefreshGate { get; } = new(1, 1);

        /// <summary>The last stdio stderr tail seen, kept after the transport is gone (see <see cref="CaptureStderr"/>).</summary>
        public string? LastStderr;
    }
}
