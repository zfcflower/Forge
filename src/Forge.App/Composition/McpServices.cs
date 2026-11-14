using Forge.App.Approval;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Mcp;

namespace Forge.App.Composition;

/// <summary>
/// MCP server management. The config is read from the global and project <c>mcp.json</c> files; a broken entry becomes a
/// diagnostic instead of an exception, so a bad config never stops the app from starting.
/// <para>Two views of the same files: <see cref="Manager"/> runs the servers from the expanded config (a <c>${VAR}</c> becomes
/// its value), while <see cref="ReadConfigAsync"/> and the save/remove methods work on the files themselves through
/// <see cref="McpConfigStore"/>, so an edit in the settings page never writes an expanded secret back. A save always ends in
/// <see cref="ApplyConfigAsync"/>: re-read, then diff into the running manager, so the page and the running servers never
/// disagree about what the file says.</para>
/// </summary>
internal sealed class McpServices : IAsyncDisposable
{
    private readonly McpServerManager manager;
    private readonly McpConfigStore store;
    private readonly PreferencesStore preferences;
    private readonly INotifications? notifications;
    private readonly HttpClient? http;
    private readonly SemaphoreSlim applyGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;

    private McpServices(
        McpServerManager manager,
        McpConfigStore store,
        PreferencesStore preferences,
        INotifications? notifications,
        HttpClient? http)
    {
        this.manager = manager;
        this.store = store;
        this.preferences = preferences;
        this.notifications = notifications;
        this.http = http;
        manager.StateChanged += OnServerStateChanged;
    }

    /// <summary>How many servers the current config defines; follows a save.</summary>
    public int ConfiguredServers => manager.ServerNames.Count;

    /// <summary>
    /// How many MCP tools one request may offer the model (<c>mcpToolSearchLimit</c>); 0 offers every ready tool. Read live, so a
    /// change reaches the next turn of every session.
    /// </summary>
    public int ToolSearchLimit => preferences.Current.McpToolSearchLimit;

    /// <summary>How many problems the last config read reported; follows a save.</summary>
    public int ConfigDiagnostics => manager.Diagnostics.Count;

    public McpServerManager Manager => manager;

    /// <summary>The file layer, exposed so the settings page can name the two files it edits.</summary>
    public McpConfigStore Store => store;

    public IReadOnlyList<McpServerState> States => manager.States;

    /// <param name="http">
    /// The client of every HTTP server (the composition routes it through the network stack); owned by the services and
    /// disposed after the manager. Null keeps the transport's own default client.
    /// </param>
    public static McpServices Create(
        AppPaths paths,
        PreferencesStore preferences,
        INotifications? notifications = null,
        HttpClient? http = null) =>
        Create(new McpConfigStore(McpConfigLoader.GlobalPath, paths.WorkspaceDirectory), preferences, notifications, http);

    /// <summary>Same wiring over explicit files, for a test that has no user profile to read.</summary>
    /// <param name="transportFactory">
    /// Replaces the transport a server would spawn. A test uses it to answer the handshake in memory, which is the only way to
    /// reach a ready server (and therefore a ready server's tools and their server lookup) without launching a process.
    /// </param>
    public static McpServices Create(
        McpConfigStore store,
        PreferencesStore preferences,
        INotifications? notifications = null,
        HttpClient? http = null,
        Func<McpServerConfig, IMcpTransport>? transportFactory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(preferences);

        // The manager reads these options whenever it needs one, so a changed timeout or reconnect delay applies to the next
        // connection without restarting a server or rebuilding the manager.
        var manager = new McpServerManager(
            LoadConfig(store),
            () => ServerOptions(preferences.Current, http, transportFactory));
        return new McpServices(manager, store, preferences, notifications, http);
    }

    /// <summary>Starts every enabled server in the background; per-server failures stay in <see cref="States"/>.</summary>
    public async Task StartAsync()
    {
        if (ConfiguredServers == 0 || disposed)
        {
            return;
        }

        try
        {
            await manager.StartAllAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Both files with their entries verbatim and their problems; read off the UI thread by the caller.</summary>
    public Task<McpConfigSnapshot> ReadConfigAsync(CancellationToken ct) =>
        BackgroundRead.RunAsync(_ => Task.FromResult(store.Read()), ct);

    /// <summary>
    /// Adds or replaces one entry, imports entries verbatim, removes one, or flips one's <c>disabled</c> flag — then re-reads
    /// both files and applies them to the running manager. Failures from the file (a root that is not an object, a locked file)
    /// surface as exceptions for the page to show; nothing is half-applied, because the manager is only touched after the write.
    /// </summary>
    public Task SaveAsync(string path, McpServerEntry entry, CancellationToken ct) =>
        WriteThenApplyAsync(() => store.Upsert(path, entry), ct);

    public Task ImportAsync(string path, IReadOnlyList<McpServerEntry> entries, CancellationToken ct) =>
        WriteThenApplyAsync(() => store.Import(path, entries), ct);

    public Task RemoveAsync(string path, string name, CancellationToken ct) =>
        WriteThenApplyAsync(() => store.Remove(path, name), ct);

    public Task SetDisabledAsync(string path, string name, bool disabled, CancellationToken ct) =>
        WriteThenApplyAsync(() => store.SetDisabled(path, name, disabled), ct);

    /// <summary>Picks the file up after a hand edit (the page's "re-read" action).</summary>
    public Task ReloadAsync(CancellationToken ct) => ApplyAsync(ct);

    /// <summary>
    /// What an approval card may show about the server behind an exposed tool name: the command line and <em>the names</em> of
    /// its env vars and headers. Values are deliberately absent — the card is a screenshot away from being shared.
    /// </summary>
    public McpApprovalServer? DescribeForApproval(string toolName)
    {
        if (manager.ConfigOfTool(toolName) is not { } config)
        {
            return null;
        }

        return new McpApprovalServer(
            config.Name,
            config.IsHttp ? "http" : "stdio",
            config.Command,
            config.Args,
            config.WorkingDirectory,
            Keys(config.Env),
            Keys(config.Headers),
            config.Url);
    }

    /// <summary>The tools one server exposes right now; empty while it is stopped or failed.</summary>
    public IReadOnlyList<McpToolAdapter> ToolsOf(string serverName) => manager.ToolsOf(serverName);

    /// <summary>The settings page's test: connect, list the tools and report how long it took.</summary>
    public Task<McpServerProbe> ProbeAsync(string serverName, CancellationToken ct) => manager.ProbeAsync(serverName, ct);

    /// <summary>The tail of a server's stderr, cut to <paramref name="lines"/> lines for display.</summary>
    public static string? Tail(string? stderr, int lines)
    {
        if (string.IsNullOrEmpty(stderr) || lines <= 0)
        {
            return stderr;
        }

        var end = stderr.Length;
        var found = 0;
        for (var index = end - 1; index >= 0; index--)
        {
            if (stderr[index] != '\n')
            {
                continue;
            }

            found++;
            if (found == lines)
            {
                return stderr[Math.Min(index + 1, end)..];
            }
        }

        return stderr;
    }

    /// <summary>
    /// A server that failed says so in the settings section and nowhere else; a toast is what tells the user the tools
    /// they expect are missing. One toast per server: the key is the server's name.
    /// </summary>
    private void OnServerStateChanged(McpServerState state)
    {
        if (state.IsReady || state.LastError is not { Length: > 0 } error)
        {
            return;
        }

        notifications.Notify(
            NotificationLevel.Warning,
            NotificationsStrings.McpFailedTitle,
            LocText.Literal(state.Name + " · " + error),
            "mcp:" + state.Name);
    }

    public int ReadyCount()
    {
        var ready = 0;
        foreach (var state in manager.States)
        {
            if (state.IsReady)
            {
                ready++;
            }
        }

        return ready;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await lifetime.CancelAsync().ConfigureAwait(false);
        manager.StateChanged -= OnServerStateChanged;
        await manager.DisposeAsync().ConfigureAwait(false);
        // The gate is deliberately not disposed: an apply still winding down would throw on release, and a SemaphoreSlim without
        // a wait handle owns nothing that needs freeing.
        lifetime.Dispose();
        http?.Dispose();
    }

    /// <summary>The manager's options from the live preferences; everything else keeps the library defaults.</summary>
    private static McpServerManagerOptions ServerOptions(
        AppPreferences preferences,
        HttpClient? http,
        Func<McpServerConfig, IMcpTransport>? transportFactory) => new()
    {
        HttpClient = http,
        TransportFactory = transportFactory,
        RequestTimeout = TimeSpan.FromSeconds(preferences.McpRequestTimeoutSeconds),
        InitialReconnectDelay = TimeSpan.FromSeconds(preferences.McpReconnectInitialDelaySeconds),
    };

    private Task WriteThenApplyAsync(Action write, CancellationToken ct) =>
        BackgroundRead.RunAsync(
            _ =>
            {
                write();
                return ApplyCoreAsync(ct);
            },
            ct);

    /// <summary>Re-reads both files and diffs the result into the running manager; one apply at a time.</summary>
    private Task ApplyAsync(CancellationToken ct) => BackgroundRead.RunAsync(_ => ApplyCoreAsync(ct), ct);

    private async Task ApplyCoreAsync(CancellationToken ct)
    {
        await applyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            await manager.ApplyConfigAsync(LoadConfig(store), ct).ConfigureAwait(false);
        }
        finally
        {
            applyGate.Release();
        }
    }

    private static McpConfig LoadConfig(McpConfigStore store)
    {
        try
        {
            return store.Load();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return McpConfig.Empty;
        }
    }

    private static string[] Keys(IReadOnlyDictionary<string, string> pairs)
    {
        if (pairs.Count == 0)
        {
            return [];
        }

        var keys = new string[pairs.Count];
        var index = 0;
        foreach (var key in pairs.Keys)
        {
            keys[index++] = key;
        }

        Array.Sort(keys, StringComparer.Ordinal);
        return keys;
    }
}
