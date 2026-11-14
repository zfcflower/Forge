using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Approval;
using Forge.App.Composition;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.Mcp;
using Xunit;

namespace Forge.App.Tests.Mcp;

/// <summary>
/// The app-facing MCP wiring over a server that really became ready: <see cref="McpServices.Create"/>'s transport factory
/// plugs the loopback transport of the previous batch into the real manager, so the handshake, the tool catalog and the
/// session runtime's per-turn tool selection all run end to end without spawning a process. Every test writes its own
/// throw-away <c>mcp.json</c> under a temp root and deletes it in <see cref="Dispose"/>.
/// </summary>
public sealed class McpReadyServerTests : IDisposable
{
    /// <summary>Three tools in ordinal name order, so "catalog order" and "name order" agree and the cut is predictable.</summary>
    private static readonly (string Name, string? Description)[] ThreeTools =
        [("alpha", "the first tool"), ("beta", "the second tool"), ("gamma", "the third tool")];

    private readonly string root = TestTempFiles.NewDirectory("forge-mcp-ready-");

    public McpReadyServerTests() { }

    public void Dispose() => TestTempFiles.DeleteTree(root);

    [Fact]
    public async Task DescribeForApproval_describes_a_ready_server_without_leaking_secret_values()
    {
        TestTempFiles.WriteFile(
            root,
            "global/mcp.json",
            """{"mcpServers":{"srv":{"command":"fake-srv","args":["--serve","7"],"env":{"Z_TOKEN":"z-secret","A_TOKEN":"a-secret"},"headers":{"X-Auth":"x-secret","A-Header":"h-secret"}}}}""");
        await using var preferences = Preferences();
        await using var services = await ReadyAsync(Store(), preferences, [("echo", "Echoes text")]);

        var described = Assert.IsType<McpApprovalServer>(services.DescribeForApproval("srv__echo"));

        Assert.Equal("srv", described.ServerName);
        Assert.Equal("stdio", described.Transport);
        Assert.Equal("fake-srv", described.Command);
        Assert.Equal(["--serve", "7"], described.Args);
        Assert.Null(described.WorkingDirectory);
        Assert.Null(described.Url);
        // Names only, ordinally sorted; the values must never reach the card.
        Assert.Equal(["A_TOKEN", "Z_TOKEN"], described.EnvKeys);
        Assert.Equal(["A-Header", "X-Auth"], described.HeaderKeys);
        Assert.DoesNotContain("z-secret", described.EnvKeys);
        Assert.DoesNotContain("x-secret", described.HeaderKeys);
    }

    [Fact]
    public async Task Without_a_limit_the_runtime_offers_every_ready_tool_and_no_search_tool()
    {
        TestTempFiles.WriteFile(root, "global/mcp.json", GlobalJson());
        await using var preferences = Preferences();
        await using var services = await ReadyAsync(Store(), preferences, ThreeTools);

        var runtime = Runtime(AgentServices(services));
        try
        {
            var tools = runtime.McpToolsForTest();

            Assert.Equal(["srv__alpha", "srv__beta", "srv__gamma"], tools.Select(static tool => tool.Definition.Name));
            Assert.DoesNotContain(tools, static tool => tool.Definition.Name == McpToolSearchTool.Name);
        }
        finally
        {
            runtime.Dispose();
        }
    }

    [Fact]
    public async Task A_limit_below_the_catalog_size_trims_the_tools_and_adds_the_search_tool()
    {
        TestTempFiles.WriteFile(root, "global/mcp.json", GlobalJson());
        await using var preferences = Preferences();
        preferences.Current.McpToolSearchLimit = 2;
        await using var services = await ReadyAsync(Store(), preferences, ThreeTools);

        var runtime = Runtime(AgentServices(services));
        try
        {
            var tools = runtime.McpToolsForTest();
            var adapters = tools.OfType<McpToolAdapter>().ToList();

            Assert.True(adapters.Count <= 2, "a limit of 2 must keep the surface at or below 2 MCP tools");
            Assert.Equal(["srv__alpha", "srv__beta"], adapters.Select(static adapter => adapter.ExposedName));
            Assert.Contains(tools, static tool => tool.Definition.Name == McpToolSearchTool.Name);
        }
        finally
        {
            runtime.Dispose();
        }
    }

    [Fact]
    public async Task Executing_the_search_tool_pins_its_match_so_the_next_surface_offers_it()
    {
        TestTempFiles.WriteFile(root, "global/mcp.json", GlobalJson());
        await using var preferences = Preferences();
        preferences.Current.McpToolSearchLimit = 2;
        await using var services = await ReadyAsync(Store(), preferences, ThreeTools);

        var runtime = Runtime(AgentServices(services));
        try
        {
            var before = runtime.McpToolsForTest();
            Assert.DoesNotContain(before, static tool => tool.Definition.Name == "srv__gamma");

            var search = Assert.Single(before.OfType<McpToolSearchTool>());
            var result = await search.InvokeAsync(
                new ToolCall("call-1", McpToolSearchTool.Name, """{"query":"gamma","limit":1}"""),
                new ToolContext("s1", Path.Combine(root, "ws")),
                CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Contains("srv__gamma", result.Content);

            // The match is pinned into the session's exposure, so the next tool list really offers it.
            var after = runtime.McpToolsForTest();
            Assert.Contains(after, static tool => tool.Definition.Name == "srv__gamma");
            Assert.Contains(after, static tool => tool.Definition.Name == McpToolSearchTool.Name);
            Assert.Equal(2, after.OfType<McpToolAdapter>().Count());
        }
        finally
        {
            runtime.Dispose();
        }
    }

    private static string GlobalJson() => """{"mcpServers":{"srv":{"command":"fake-srv"}}}""";

    private PreferencesStore Preferences() => new(Path.Combine(root, "settings.json"));

    private McpConfigStore Store() => new(Path.Combine(root, "global", "mcp.json"), Path.Combine(root, "ws"));

    /// <summary>
    /// Creates the services over the temp files and waits for one server to become ready through the loopback transport.
    /// The ready assertion is a precondition shared by every test, not the behavior under test.
    /// </summary>
    private static async Task<McpServices> ReadyAsync(
        McpConfigStore store,
        PreferencesStore preferences,
        IReadOnlyList<(string Name, string? Description)> tools,
        string serverName = "srv")
    {
        var script = new ScriptedMcpServer(tools);
        var services = McpServices.Create(
            store,
            preferences,
            notifications: null,
            http: null,
            server => new LoopbackMcpTransport(server.Name) { Responder = script.Respond });

        var state = await services.Manager.StartAsync(serverName, CancellationToken.None);

        Assert.Equal(McpServerStatus.Ready, state.Status);
        return services;
    }

    private static AgentRuntimeServices AgentServices(McpServices mcp)
    {
        var services = new AgentRuntimeServices(
            static _ => throw new NotSupportedException("the MCP ready-server tests never call a model"),
            HostClient: null,
            new EmptySessionStore(DateTimeOffset.UnixEpoch),
            new EmptySessionCatalog(DateTimeOffset.UnixEpoch),
            new EmptyTodoStore("todo-1", DateTimeOffset.UnixEpoch),
            new ModelSettings("main-model"))
        {
            Mcp = mcp,
        };
        return services;
    }

    private static SessionRuntime Runtime(AgentRuntimeServices services) =>
        new(services, new SessionInfo("s1", Path.Combine(Path.GetTempPath(), "ws-s1"), "t", DateTimeOffset.UnixEpoch));
}
