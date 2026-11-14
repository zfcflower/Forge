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

    [Fact]
    public async Task A_search_never_pins_more_tools_than_one_request_may_offer()
    {
        TestTempFiles.WriteFile(root, "global/mcp.json", GlobalJson());
        await using var preferences = Preferences();
        preferences.Current.McpToolSearchLimit = 2;
        await using var services = await ReadyAsync(Store(), preferences, ThreeTools);

        var runtime = Runtime(AgentServices(services));
        try
        {
            var search = Assert.Single(runtime.McpToolsForTest().OfType<McpToolSearchTool>());

            // All three descriptions contain "tool" and the call asks for three, but a request may only offer two: the answer
            // must not call a tool "callable" that the next surface then cuts.
            var result = await search.InvokeAsync(
                new ToolCall("call-1", McpToolSearchTool.Name, """{"query":"tool","limit":3}"""),
                new ToolContext("s1", Path.Combine(root, "ws")),
                CancellationToken.None);

            Assert.False(result.IsError);
            Assert.StartsWith("2 MCP tools are now available", result.Content);
            var offered = runtime.McpToolsForTest().OfType<McpToolAdapter>().Select(static adapter => adapter.ExposedName).ToList();
            var named = result.Content.Split('\n')
                .Where(static line => line.StartsWith("- ", StringComparison.Ordinal))
                .Select(static line => line[2..line.IndexOf(' ', 2)])
                .ToList();
            Assert.Equal(2, named.Count);
            Assert.All(named, name => Assert.Contains(name, offered));
        }
        finally
        {
            runtime.Dispose();
        }
    }

    [Fact]
    public async Task A_web_or_plugin_tool_keeps_its_name_when_an_mcp_tool_advertises_the_same_one()
    {
        TestTempFiles.WriteFile(root, "global/mcp.json", GlobalJson());
        await using var preferences = Preferences();
        await using var services = await ReadyAsync(Store(), preferences, ThreeTools);
        var owned = new NamedTool("srv__beta");
        var agentServices = AgentServices(services);
        agentServices.WebTools = () => [owned];

        var runtime = Runtime(agentServices);
        try
        {
            var tools = runtime.BuildTools();

            var beta = Assert.Single(tools, static tool => tool.Definition.Name == "srv__beta");
            Assert.Same(owned, beta);
            Assert.Equal(["srv__alpha", "srv__gamma"], tools.OfType<McpToolAdapter>().Select(static adapter => adapter.ExposedName));
        }
        finally
        {
            runtime.Dispose();
        }
    }

    [Fact]
    public async Task A_server_added_by_a_save_starts_in_the_background_and_its_tools_reach_the_session()
    {
        await using var preferences = Preferences();
        var script = new ScriptedMcpServer([("echo", "Echoes text")]);
        await using var services = McpServices.Create(
            Store(),
            preferences,
            notifications: null,
            http: null,
            server => new LoopbackMcpTransport(server.Name) { Responder = script.Respond });
        Assert.Empty(services.Manager.ServerNames);

        await services.SaveAsync(
            services.Store.GlobalPath,
            new McpServerEntry { Name = "added", Command = "fake-added" },
            CancellationToken.None);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (services.Manager.Tools.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(McpServerStatus.Ready, services.Manager.GetState("added").Status);
        var runtime = Runtime(AgentServices(services));
        try
        {
            Assert.Equal(["added__echo"], runtime.McpToolsForTest().Select(static tool => tool.Definition.Name));
        }
        finally
        {
            runtime.Dispose();
        }
    }

    [Fact]
    public async Task A_disabled_server_added_by_a_save_is_not_started()
    {
        await using var preferences = Preferences();
        var transports = 0;
        await using var services = McpServices.Create(
            Store(),
            preferences,
            notifications: null,
            http: null,
            server =>
            {
                Interlocked.Increment(ref transports);
                return new LoopbackMcpTransport(server.Name) { Responder = new ScriptedMcpServer().Respond };
            });

        await services.SaveAsync(
            services.Store.GlobalPath,
            new McpServerEntry { Name = "off", Command = "fake-off", Disabled = true },
            CancellationToken.None);
        await Task.Delay(200);

        Assert.Equal(0, Volatile.Read(ref transports));
        Assert.Equal(McpServerStatus.Stopped, services.Manager.GetState("off").Status);
    }

    [Fact]
    public async Task SameMcpTools_holds_an_unchanged_surface_and_republishes_a_changed_side_effect()
    {
        var plain = new McpConfig([new McpServerConfig { Name = "srv", Command = "fake" }], []);
        var readOnly = new McpConfig([new McpServerConfig { Name = "srv", Command = "fake", ReadOnlyTools = ["echo"] }], []);
        await using var first = await ReadyMcpCatalog.StartAsync(plain);
        await using var again = await ReadyMcpCatalog.StartAsync(plain);
        await using var changed = await ReadyMcpCatalog.StartAsync(readOnly);
        var held = first.Tools.Cast<ITool>().ToList();

        // Same instances, or fresh instances with the same definition and side effect: nothing to publish.
        Assert.True(SessionRuntime.SameMcpTools(first.Tools, truncated: false, [], held));
        Assert.True(SessionRuntime.SameMcpTools(again.Tools, truncated: false, [], held));

        // The entry made the tool read-only: the held adapter would keep the wrong approval side effect, so the surface must
        // be published again.
        Assert.False(SessionRuntime.SameMcpTools(changed.Tools, truncated: false, [], held));

        // The search tool appearing, or a held adapter the selection dropped, also republishes.
        Assert.False(SessionRuntime.SameMcpTools(first.Tools, truncated: true, [], held));
        Assert.False(SessionRuntime.SameMcpTools([], truncated: false, [], held));
    }

    [Fact]
    public async Task SameMcpTools_expects_a_name_a_built_in_or_another_tool_shadows_to_be_absent()
    {
        await using var catalog = await ReadyMcpCatalog.OneServerAsync("srv", [("echo", "Echoes text")]);
        var shadow = new NamedTool("srv__echo");

        // A built-in of the same name: the tool surface drops the MCP copy, so the held external set never carries it.
        Assert.True(SessionRuntime.SameMcpTools(catalog.Tools, truncated: false, [shadow], []));

        // A plugin or web tool of the same name: it is held instead of the adapter.
        Assert.True(SessionRuntime.SameMcpTools(catalog.Tools, truncated: false, [], [shadow]));

        // Nobody holds the name: the adapter is missing and must be published.
        Assert.False(SessionRuntime.SameMcpTools(catalog.Tools, truncated: false, [], []));
    }

    private sealed class NamedTool(string name) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, "owned elsewhere", "{}");

        public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            ValueTask.FromResult(new ToolResult("owned"));
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
