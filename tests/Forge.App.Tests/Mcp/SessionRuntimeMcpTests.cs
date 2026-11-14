using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.Mcp;
using Xunit;

namespace Forge.App.Tests.Mcp;

/// <summary>
/// The session runtime's MCP wiring: with no services there are no MCP tools; with services whose servers never became
/// ready the surface is still empty, because the catalog is empty and nothing is hidden.
/// </summary>
public sealed class SessionRuntimeMcpTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-runtime-mcp-");

    public void Dispose() => TestTempFiles.DeleteTree(root);

    private static AgentRuntimeServices Services() => new(
        static _ => throw new NotSupportedException("the MCP surface tests never call a model"),
        HostClient: null,
        new EmptySessionStore(DateTimeOffset.UnixEpoch),
        new EmptySessionCatalog(DateTimeOffset.UnixEpoch),
        new EmptyTodoStore("todo-1", DateTimeOffset.UnixEpoch),
        new ModelSettings("main-model"));

    private static SessionRuntime Build(AgentRuntimeServices services) =>
        new(services, new SessionInfo("s1", Path.Combine(Path.GetTempPath(), "ws-s1"), "t", DateTimeOffset.UnixEpoch));

    [Fact]
    public void Without_mcp_services_the_runtime_offers_no_mcp_tools()
    {
        var runtime = Build(Services());

        Assert.Empty(runtime.McpToolsForTest());

        runtime.Dispose();
    }

    [Fact]
    public async Task With_mcp_services_but_no_ready_server_the_runtime_offers_no_mcp_tools()
    {
        TestTempFiles.WriteFile(root, "global/mcp.json", """{"mcpServers":{"srv":{"command":"never-spawned"}}}""");
        await using var preferences = new PreferencesStore(Path.Combine(root, "settings.json"));
        await using var mcp = McpServices.Create(
            new McpConfigStore(Path.Combine(root, "global", "mcp.json"), Path.Combine(root, "ws")),
            preferences,
            null,
            null);
        Assert.Equal(McpServerStatus.Stopped, Assert.Single(mcp.States).Status);

        var services = Services();
        services.Mcp = mcp;
        var runtime = Build(services);

        Assert.Empty(runtime.McpToolsForTest());

        // A limit does not add the search tool while no server is ready: the catalog is empty, so nothing is hidden.
        preferences.Current.McpToolSearchLimit = 5;
        Assert.Empty(runtime.McpToolsForTest());

        runtime.Dispose();
    }
}
