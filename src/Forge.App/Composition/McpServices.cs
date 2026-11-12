using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.Mcp;

namespace Forge.App.Composition;

/// <summary>
/// MCP server management. The config is read from the global and project <c>mcp.json</c> files; a broken entry becomes a
/// diagnostic instead of an exception, so a bad config never stops the app from starting.
/// </summary>
internal sealed class McpServices : IAsyncDisposable
{
    private readonly McpServerManager manager;
    private readonly INotifications? notifications;
    private readonly HttpClient? http;
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;

    private McpServices(McpServerManager manager, INotifications? notifications, HttpClient? http)
    {
        this.manager = manager;
        this.notifications = notifications;
        this.http = http;
        manager.StateChanged += OnServerStateChanged;
        ConfigDiagnostics = manager.Diagnostics.Count;
        ConfiguredServers = manager.ServerNames.Count;
    }

    public int ConfiguredServers { get; }

    public int ConfigDiagnostics { get; }

    public McpServerManager Manager => manager;

    public IReadOnlyList<McpServerState> States => manager.States;

    /// <param name="http">
    /// The client of every HTTP server (the composition routes it through the network stack); owned by the services and
    /// disposed after the manager. Null keeps the transport's own default client.
    /// </param>
    public static McpServices Create(AppPaths paths, INotifications? notifications = null, HttpClient? http = null)
    {
        McpConfig config;
        try
        {
            config = McpConfigLoader.Load(paths.WorkspaceDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            config = McpConfig.Empty;
        }

        return new McpServices(new McpServerManager(config, new McpServerManagerOptions { HttpClient = http }), notifications, http);
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
        lifetime.Dispose();
        await manager.DisposeAsync().ConfigureAwait(false);
        http?.Dispose();
    }
}
