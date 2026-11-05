using Forge.Mcp;

namespace Forge.App.Composition;

/// <summary>
/// MCP server management. The config is read from the global and project <c>mcp.json</c> files; a broken entry becomes a
/// diagnostic instead of an exception, so a bad config never stops the app from starting.
/// </summary>
internal sealed class McpServices : IAsyncDisposable
{
    private readonly McpServerManager manager;
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;

    private McpServices(McpServerManager manager)
    {
        this.manager = manager;
        ConfigDiagnostics = manager.Diagnostics.Count;
        ConfiguredServers = manager.ServerNames.Count;
    }

    public int ConfiguredServers { get; }

    public int ConfigDiagnostics { get; }

    public McpServerManager Manager => manager;

    public IReadOnlyList<McpServerState> States => manager.States;

    public static McpServices Create(AppPaths paths)
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

        return new McpServices(new McpServerManager(config));
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
    }
}
