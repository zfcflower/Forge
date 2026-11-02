using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Mcp.Tests.Fixtures;
using Xunit;
using static Forge.Mcp.Tests.Fixtures.ManagerFixture;

namespace Forge.Mcp.Tests.Manager;

public class McpServerManagerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Servers_are_lazy_until_started_and_start_publishes_prefixed_tools()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));
        var manager = fixture.Manager;

        Assert.Empty(fixture.Transports);
        var idle = manager.GetState("srv");
        Assert.Equal(McpServerStatus.Stopped, idle.Status);
        Assert.True(idle.Enabled);
        Assert.Empty(manager.Tools);

        var state = await manager.StartAsync("srv", CancellationToken.None);

        Assert.Equal(McpServerStatus.Ready, state.Status);
        Assert.True(state.IsReady);
        Assert.NotNull(state.ReadyAt);
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.Null(state.LastError);
        var tool = Assert.Single(manager.Tools);
        Assert.Equal("srv__echo", tool.ExposedName);
        Assert.Equal("srv", tool.ServerName);
        Assert.Equal("echo", tool.ToolName);
        Assert.Equal("[MCP server \"srv\"] Echoes text", tool.Definition.Description);
        Assert.Equal(JsonNormalize(ScriptedServer.ToolsListResultJson, "tools", 0, "inputSchema"), tool.Definition.InputSchemaJson);
        Assert.Equal(ToolSideEffect.Execute, tool.SideEffect);
        Assert.Single(fixture.Transports);
    }

    [Fact]
    public async Task Repeated_start_reuses_the_running_client_and_its_transport()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));

        await fixture.Manager.StartAsync("srv", CancellationToken.None);
        await fixture.Manager.StartAsync("srv", CancellationToken.None);
        await fixture.Manager.StartAllAsync(CancellationToken.None);

        var transport = Assert.Single(fixture.Transports);
        Assert.Equal(1, transport.StartCount);
    }

    [Fact]
    public async Task Concurrent_starts_connect_only_once()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Manager.StartAsync("srv", CancellationToken.None)));

        var transport = Assert.Single(fixture.Transports);
        Assert.Equal(1, transport.StartCount);
        Assert.Equal(1, transport.Sent.Count(message => JsonRpc.MethodOf(message) == McpProtocol.Initialize));
    }

    [Fact]
    public async Task State_changes_are_published_through_the_state_changed_event()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));
        var seen = new ConcurrentQueue<McpServerStatus>();
        fixture.Manager.StateChanged += state => seen.Enqueue(state.Status);

        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        Assert.Contains(McpServerStatus.Starting, seen);
        Assert.Equal(McpServerStatus.Ready, seen.Last());
    }

    [Fact]
    public async Task Initialize_failure_marks_the_server_failed_with_last_error_and_rethrows()
    {
        var server = new ScriptedServer().On(McpProtocol.Initialize, _ => throw new ScriptedRpcError(-32603, "license expired"));
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), name => server, maxReconnectAttempts: 0);

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => fixture.Manager.StartAsync("srv", CancellationToken.None));

        Assert.Contains("license expired", error.Message);
        var state = fixture.Manager.GetState("srv");
        Assert.Equal(McpServerStatus.Failed, state.Status);
        Assert.False(state.IsReady);
        Assert.Equal(error.Message, state.LastError);
        Assert.Equal(1, state.ConsecutiveFailures);
        Assert.Empty(fixture.Manager.Tools);
        Assert.Null(fixture.Manager.GetClient("srv"));
    }

    [Fact]
    public async Task Transport_factory_failure_marks_the_server_failed()
    {
        await using var fixture = new ManagerFixture(
            Of(Stdio("srv")),
            maxReconnectAttempts: 0,
            transportFor: _ => throw new InvalidOperationException("no runtime"));

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Manager.StartAsync("srv", CancellationToken.None));

        var state = fixture.Manager.GetState("srv");
        Assert.Equal(McpServerStatus.Failed, state.Status);
        Assert.NotNull(state.LastError);
    }

    [Fact]
    public async Task Caller_cancellation_during_start_is_not_recorded_as_a_failure()
    {
        var server = new ScriptedServer().On(McpProtocol.Initialize, _ => null);
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server, initialReconnectDelay: TimeSpan.FromMilliseconds(50));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Manager.StartAsync("srv", cts.Token));

        var state = fixture.Manager.GetState("srv");
        Assert.NotEqual(McpServerStatus.Failed, state.Status);
        Assert.Equal(0, state.ConsecutiveFailures);
    }

    [Fact]
    public async Task Start_all_starts_enabled_servers_and_isolates_a_failing_one()
    {
        var failing = new ScriptedServer().On(McpProtocol.Initialize, _ => throw new ScriptedRpcError(-32603, "broken"));
        await using var fixture = new ManagerFixture(
            Of(Stdio("good"), Stdio("bad"), Stdio("off", disabled: true)),
            name => name == "bad" ? failing : new ScriptedServer(),
            maxReconnectAttempts: 0);

        await fixture.Manager.StartAllAsync(CancellationToken.None);

        Assert.Equal(McpServerStatus.Ready, fixture.Manager.GetState("good").Status);
        Assert.Equal(McpServerStatus.Failed, fixture.Manager.GetState("bad").Status);
        var off = fixture.Manager.GetState("off");
        Assert.Equal(McpServerStatus.Stopped, off.Status);
        Assert.False(off.Enabled);
        Assert.Equal(["good__echo"], fixture.Manager.Tools.Select(tool => tool.ExposedName));
        Assert.Equal(0, fixture.TransportCount("off"));
    }

    [Fact]
    public async Task Disabled_servers_refuse_to_start_until_enabled()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv", disabled: true)));
        var manager = fixture.Manager;

        Assert.False(manager.IsEnabled("srv"));
        var error = await Assert.ThrowsAsync<McpException>(() => manager.StartAsync("srv", CancellationToken.None));
        Assert.Contains("disabled", error.Message);

        await manager.SetEnabledAsync("srv", true, CancellationToken.None);

        Assert.True(manager.IsEnabled("srv"));
        Assert.Equal(McpServerStatus.Ready, manager.GetState("srv").Status);
    }

    [Fact]
    public async Task Disabling_a_running_server_stops_it_and_withdraws_its_tools_until_re_enabled()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));
        var manager = fixture.Manager;
        await manager.StartAsync("srv", CancellationToken.None);
        var toolEvents = 0;
        manager.ToolsChanged += () => Interlocked.Increment(ref toolEvents);

        await manager.SetEnabledAsync("srv", false, CancellationToken.None);

        var state = manager.GetState("srv");
        Assert.Equal(McpServerStatus.Stopped, state.Status);
        Assert.False(state.Enabled);
        Assert.Empty(manager.Tools);
        Assert.Equal(1, fixture.Transports[0].StopCount);
        Assert.True(toolEvents > 0);

        await manager.SetEnabledAsync("srv", true, CancellationToken.None);

        Assert.Equal(McpServerStatus.Ready, manager.GetState("srv").Status);
        Assert.Equal(["srv__echo"], manager.Tools.Select(tool => tool.ExposedName));
    }

    [Fact]
    public async Task Stop_clears_error_and_failure_count_and_restart_reconnects_with_the_same_transport()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));
        var manager = fixture.Manager;
        await manager.StartAsync("srv", CancellationToken.None);

        await manager.StopAsync("srv", CancellationToken.None);
        var stopped = manager.GetState("srv");
        Assert.Equal(McpServerStatus.Stopped, stopped.Status);
        Assert.Null(stopped.ReadyAt);
        Assert.Null(stopped.LastError);
        Assert.Empty(manager.Tools);

        await manager.RestartAsync("srv", CancellationToken.None);

        Assert.Equal(McpServerStatus.Ready, manager.GetState("srv").Status);
        Assert.Single(fixture.Transports);
        Assert.Equal(2, fixture.Transports[0].StartCount);
    }

    [Fact]
    public async Task Transport_closing_after_ready_marks_failed_then_reconnects_automatically()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), initialReconnectDelay: TimeSpan.FromMilliseconds(400));
        var manager = fixture.Manager;
        await manager.StartAsync("srv", CancellationToken.None);
        var transport = fixture.Transports[0];

        transport.Drop(new McpTransportException("socket reset by peer"));

        Assert.True(await Eventually.Async(() => manager.GetState("srv").Status == McpServerStatus.Failed, Wait));
        var failed = manager.GetState("srv");
        Assert.Equal("socket reset by peer", failed.LastError);
        Assert.Equal(1, failed.ConsecutiveFailures);
        Assert.Empty(manager.Tools);
        Assert.True(await Eventually.Async(() => manager.GetState("srv").IsReady, Wait));
        var recovered = manager.GetState("srv");
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Null(recovered.LastError);
        Assert.Equal(2, transport.StartCount);
        Assert.Equal(["srv__echo"], manager.Tools.Select(tool => tool.ExposedName));
    }

    [Fact]
    public async Task Reconnect_attempts_back_off_exponentially_from_the_initial_delay()
    {
        var attempts = new ConcurrentQueue<long>();
        var server = new ScriptedServer().On(McpProtocol.Initialize, _ =>
        {
            attempts.Enqueue(Stopwatch.GetTimestamp());
            if (attempts.Count <= 2)
            {
                throw new ScriptedRpcError(-32603, "warming up");
            }

            return ScriptedServer.InitializeResultJson;
        });
        await using var fixture = new ManagerFixture(
            Of(Stdio("srv")),
            _ => server,
            maxReconnectAttempts: 5,
            initialReconnectDelay: TimeSpan.FromMilliseconds(120));

        await Assert.ThrowsAsync<McpProtocolException>(() => fixture.Manager.StartAsync("srv", CancellationToken.None));

        Assert.True(await Eventually.Async(() => fixture.Manager.GetState("srv").IsReady, Wait));
        var stamps = attempts.ToArray();
        Assert.Equal(3, stamps.Length);
        var first = Stopwatch.GetElapsedTime(stamps[0], stamps[1]);
        var second = Stopwatch.GetElapsedTime(stamps[1], stamps[2]);
        Assert.True(first >= TimeSpan.FromMilliseconds(100), $"first backoff was {first.TotalMilliseconds}ms");
        Assert.True(second >= TimeSpan.FromMilliseconds(220), $"second backoff was {second.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task Reconnect_delay_never_exceeds_the_configured_maximum()
    {
        var attempts = new ConcurrentQueue<long>();
        var server = new ScriptedServer().On(McpProtocol.Initialize, _ =>
        {
            attempts.Enqueue(Stopwatch.GetTimestamp());
            if (attempts.Count <= 2)
            {
                throw new ScriptedRpcError(-32603, "warming up");
            }

            return ScriptedServer.InitializeResultJson;
        });
        var options = new McpServerManagerOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(5),
            InitialReconnectDelay = TimeSpan.FromMilliseconds(100),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(100),
            MaxReconnectAttempts = 5,
            TransportFactory = config => new LoopbackTransport(config.Name) { Responder = server.Respond },
        };
        await using var manager = new McpServerManager(Of(Stdio("srv")), options);

        await Assert.ThrowsAsync<McpProtocolException>(() => manager.StartAsync("srv", CancellationToken.None));

        Assert.True(await Eventually.Async(() => manager.GetState("srv").IsReady, Wait));
        var stamps = attempts.ToArray();
        Assert.Equal(3, stamps.Length);
        var second = Stopwatch.GetElapsedTime(stamps[1], stamps[2]);
        Assert.True(second < TimeSpan.FromMilliseconds(180), $"second backoff was {second.TotalMilliseconds}ms; expected the 100ms cap");
    }

    [Fact]
    public async Task Reconnect_gives_up_after_max_attempts()
    {
        var attempts = 0;
        var server = new ScriptedServer().On(McpProtocol.Initialize, _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new ScriptedRpcError(-32603, "still down");
        });
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server, maxReconnectAttempts: 2);

        await Assert.ThrowsAsync<McpProtocolException>(() => fixture.Manager.StartAsync("srv", CancellationToken.None));

        Assert.True(await Eventually.Async(() => Volatile.Read(ref attempts) == 3, Wait));
        await Task.Delay(400);
        Assert.Equal(3, Volatile.Read(ref attempts));
        var state = fixture.Manager.GetState("srv");
        Assert.Equal(McpServerStatus.Failed, state.Status);
        Assert.Equal(3, state.ConsecutiveFailures);
    }

    [Fact]
    public async Task Zero_max_reconnect_attempts_never_retries_after_a_failure()
    {
        var attempts = 0;
        var server = new ScriptedServer().On(McpProtocol.Initialize, _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new ScriptedRpcError(-32603, "down");
        });
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server, maxReconnectAttempts: 0, initialReconnectDelay: TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAsync<McpProtocolException>(() => fixture.Manager.StartAsync("srv", CancellationToken.None));
        await Task.Delay(200);

        Assert.Equal(1, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task Stopping_a_failed_server_cancels_its_pending_reconnect()
    {
        var attempts = 0;
        var server = new ScriptedServer().On(McpProtocol.Initialize, _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new ScriptedRpcError(-32603, "down");
        });
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server, initialReconnectDelay: TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<McpProtocolException>(() => fixture.Manager.StartAsync("srv", CancellationToken.None));

        await fixture.Manager.StopAsync("srv", CancellationToken.None);
        await Task.Delay(500);

        Assert.Equal(1, Volatile.Read(ref attempts));
        Assert.Equal(McpServerStatus.Stopped, fixture.Manager.GetState("srv").Status);
    }

    [Fact]
    public async Task Tools_list_changed_notification_refreshes_the_snapshot_and_raises_tools_changed()
    {
        var tools = """{"tools":[{"name":"first","inputSchema":{"type":"object"}}]}""";
        var server = new ScriptedServer().On(McpProtocol.ToolsList, _ => tools);
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server);
        var manager = fixture.Manager;
        await manager.StartAsync("srv", CancellationToken.None);
        var changes = 0;
        manager.ToolsChanged += () => Interlocked.Increment(ref changes);
        var before = Volatile.Read(ref changes);

        tools = """{"tools":[{"name":"first","inputSchema":{"type":"object"}},{"name":"second","inputSchema":{"type":"object"}}]}""";
        fixture.Transports[0].Push(JsonRpc.Notification(McpProtocol.ToolsListChanged));

        Assert.True(await Eventually.Async(() => manager.Tools.Count == 2, Wait));
        Assert.Equal(["srv__first", "srv__second"], manager.Tools.Select(tool => tool.ExposedName));
        Assert.True(Volatile.Read(ref changes) > before);
    }

    [Fact]
    public async Task Refresh_tools_returns_the_server_list_for_one_server()
    {
        var tools = """{"tools":[]}""";
        var server = new ScriptedServer().On(McpProtocol.ToolsList, _ => tools);
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server);
        await fixture.Manager.StartAsync("srv", CancellationToken.None);
        Assert.Empty(fixture.Manager.Tools);

        tools = """{"tools":[{"name":"late","inputSchema":{"type":"object"}}]}""";
        var refreshed = await fixture.Manager.RefreshToolsAsync("srv", CancellationToken.None);

        Assert.Equal(["srv__late"], refreshed.Select(tool => tool.ExposedName));
        Assert.Equal(["srv__late"], fixture.Manager.Tools.Select(tool => tool.ExposedName));
    }

    [Fact]
    public async Task Server_without_tools_capability_publishes_no_tools_and_is_not_asked_for_them()
    {
        var server = new ScriptedServer().On(McpProtocol.Initialize, _ => """{"protocolVersion":"2025-06-18","capabilities":{"resources":{}}}""");
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server);

        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        Assert.Equal(McpServerStatus.Ready, fixture.Manager.GetState("srv").Status);
        Assert.Empty(fixture.Manager.Tools);
        Assert.DoesNotContain(McpProtocol.ToolsList, server.RequestMethods);
    }

    [Fact]
    public async Task Tools_with_the_same_server_and_colliding_sanitized_names_get_distinct_deterministic_names()
    {
        var tools = """{"tools":[{"name":"a.b","inputSchema":{"type":"object"}},{"name":"a_b","inputSchema":{"type":"object"}}]}""";
        var server = new ScriptedServer().On(McpProtocol.ToolsList, _ => tools);
        await using var first = new ManagerFixture(Of(Stdio("srv")), _ => server);
        await using var second = new ManagerFixture(Of(Stdio("srv")), _ => server);

        await first.Manager.StartAsync("srv", CancellationToken.None);
        await second.Manager.StartAsync("srv", CancellationToken.None);

        var firstNames = first.Manager.Tools.Select(tool => tool.ExposedName).ToArray();
        var secondNames = second.Manager.Tools.Select(tool => tool.ExposedName).ToArray();
        Assert.Equal(2, firstNames.Distinct().Count());
        Assert.Equal(firstNames, secondNames);
        Assert.All(firstNames, name => Assert.StartsWith("srv__a_b", name));
    }

    [Fact]
    public async Task Tool_names_colliding_across_servers_are_disambiguated_in_start_order()
    {
        var tools = new Dictionary<string, string>
        {
            ["a"] = """{"tools":[{"name":"_b","inputSchema":{"type":"object"}}]}""",
            ["a_"] = """{"tools":[{"name":"b","inputSchema":{"type":"object"}}]}""",
        };
        await using var fixture = new ManagerFixture(
            Of(Stdio("a"), Stdio("a_")),
            name => new ScriptedServer().On(McpProtocol.ToolsList, _ => tools[name]));
        var manager = fixture.Manager;

        await manager.StartAsync("a", CancellationToken.None);
        await manager.StartAsync("a_", CancellationToken.None);

        var names = manager.Tools.Select(tool => tool.ExposedName).ToArray();
        Assert.Equal(2, names.Distinct().Count());
        Assert.Equal("a___b", manager.Tools.Single(tool => tool.ServerName == "a").ExposedName);
        Assert.StartsWith("a___b_", manager.Tools.Single(tool => tool.ServerName == "a_").ExposedName);
        Assert.All(names, name => Assert.True(name.Length <= McpToolNaming.MaxLength));
    }

    [Fact]
    public async Task Read_only_tools_from_config_map_to_read_only_side_effects()
    {
        var tools = """{"tools":[{"name":"view","inputSchema":{"type":"object"}},{"name":"write","inputSchema":{"type":"object"}}]}""";
        var server = new ScriptedServer().On(McpProtocol.ToolsList, _ => tools);
        await using var fixture = new ManagerFixture(Of(Stdio("srv", readOnlyTools: ["view"])), _ => server);

        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        var byName = fixture.Manager.Tools.ToDictionary(tool => tool.ToolName);
        Assert.Equal(ToolSideEffect.ReadOnly, byName["view"].SideEffect);
        Assert.Equal(ToolSideEffect.Execute, byName["write"].SideEffect);
    }

    [Fact]
    public async Task Adapter_invocation_forwards_arguments_and_flattens_the_result()
    {
        var seen = new ConcurrentQueue<string>();
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, root =>
        {
            seen.Enqueue(root.GetRawText());
            return """{"content":[{"type":"text","text":"hello"},{"type":"text","text":"world"}]}""";
        });
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server);
        await fixture.Manager.StartAsync("srv", CancellationToken.None);
        var tool = fixture.Manager.Tools[0];

        var result = await tool.InvokeAsync(new ToolCall("c1", "srv__echo", """{"text":"hi"}"""), new ToolContext("s", "/work"), CancellationToken.None);

        Assert.Equal(new ToolResult("hello\nworld", false), result);
        var request = Assert.Single(seen);
        using var document = JsonDocument.Parse(request);
        Assert.Equal("echo", document.RootElement.GetProperty("params").GetProperty("name").GetString());
        Assert.Equal("hi", document.RootElement.GetProperty("params").GetProperty("arguments").GetProperty("text").GetString());
    }

    [Fact]
    public async Task Adapter_reports_server_is_error_results_without_throwing()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => """{"content":[{"type":"text","text":"denied"}],"isError":true}""");
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server);
        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        var result = await fixture.Manager.Tools[0].InvokeAsync(new ToolCall("c", "srv__echo", "{}"), new ToolContext("s", "/"), CancellationToken.None);

        Assert.Equal(new ToolResult("denied", true), result);
    }

    [Fact]
    public async Task Adapter_turns_protocol_errors_into_error_results_prefixed_with_the_server_name()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => throw new ScriptedRpcError(-32602, "field 'text' is required", """{"path":"text"}"""));
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server);
        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        var result = await fixture.Manager.Tools[0].InvokeAsync(new ToolCall("c", "srv__echo", "{}"), new ToolContext("s", "/"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("srv: tools/call failed with JSON-RPC error -32602: field 'text' is required\n{\"path\":\"text\"}", result.Content);
    }

    [Fact]
    public async Task Adapter_turns_timeouts_into_error_results_instead_of_exceptions()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => null);
        await using var fixture = new ManagerFixture(
            Of(Stdio("srv", timeout: TimeSpan.FromMilliseconds(150))),
            _ => server);
        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        var result = await fixture.Manager.Tools[0].InvokeAsync(new ToolCall("c", "srv__echo", "{}"), new ToolContext("s", "/"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("srv: tools/call did not complete within 0.15s.", result.Content);
    }

    [Fact]
    public async Task Adapter_lets_caller_cancellation_propagate()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => null);
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server, requestTimeout: TimeSpan.FromSeconds(30));
        await fixture.Manager.StartAsync("srv", CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Manager.Tools[0].InvokeAsync(new ToolCall("c", "srv__echo", "{}"), new ToolContext("s", "/"), cts.Token).AsTask());
    }

    [Fact]
    public async Task Adapter_truncates_long_output_and_reports_how_much_was_omitted()
    {
        var text = new string('a', 10) + new string('b', 40);
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => $$"""{"content":[{"type":"text","text":"{{text}}"}]}""");
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server, maxResultChars: 10);
        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        var result = await fixture.Manager.Tools[0].InvokeAsync(new ToolCall("c", "srv__echo", "{}"), new ToolContext("s", "/"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("aaaaaaaaaa\n…[output truncated: 40 characters omitted]", result.Content);
    }

    [Fact]
    public async Task Output_within_the_limit_is_returned_untouched()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => """{"content":[{"type":"text","text":"exact"}]}""");
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server, maxResultChars: 5);
        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        var result = await fixture.Manager.Tools[0].InvokeAsync(new ToolCall("c", "srv__echo", "{}"), new ToolContext("s", "/"), CancellationToken.None);

        Assert.Equal("exact", result.Content);
    }

    [Fact]
    public async Task Adapter_description_without_text_uses_the_bracketed_server_tag_only()
    {
        var tools = """{"tools":[{"name":"bare","description":"   ","inputSchema":{"type":"object"}}]}""";
        var server = new ScriptedServer().On(McpProtocol.ToolsList, _ => tools);
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server);
        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        Assert.Equal("[MCP server \"srv\"]", fixture.Manager.Tools[0].Definition.Description);
    }

    [Fact]
    public async Task Unknown_server_names_and_blank_names_are_rejected()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));
        var manager = fixture.Manager;

        var unknown = Assert.Throws<McpException>(() => manager.GetState("nope"));
        Assert.Contains("Unknown MCP server 'nope'", unknown.Message);
        await Assert.ThrowsAsync<McpException>(() => manager.StartAsync("nope", CancellationToken.None));
        Assert.Throws<ArgumentException>(() => manager.GetState(" "));
        Assert.Null(manager.GetClient("srv"));
    }

    [Fact]
    public async Task Manager_exposes_configuration_names_diagnostics_and_a_state_snapshot()
    {
        var config = new McpConfig(
            [Stdio("one"), Stdio("two", disabled: true)],
            [new McpConfigDiagnostic("global.json", "broken", "Neither \"command\" nor \"url\" is set.")]);
        await using var fixture = new ManagerFixture(config);
        var manager = fixture.Manager;

        Assert.Equal(["one", "two"], manager.ServerNames);
        Assert.Equal("broken", Assert.Single(manager.Diagnostics).ServerName);
        Assert.Equal("fake-two", manager.GetServer("two").Command);
        Assert.True(manager.IsEnabled("one"));
        Assert.False(manager.IsEnabled("two"));
        Assert.Equal(["one", "two"], manager.States.Select(state => state.Name));
        Assert.Equal(McpServerStatus.Stopped, manager.States.Single(state => state.Name == "two").Status);
    }

    [Fact]
    public async Task Dispose_disposes_transports_and_is_idempotent()
    {
        var fixture = new ManagerFixture(Of(Stdio("srv")));
        await fixture.Manager.StartAsync("srv", CancellationToken.None);
        var transport = fixture.Transports[0];

        await fixture.Manager.DisposeAsync();
        await fixture.Manager.DisposeAsync();

        Assert.Equal(1, transport.DisposeCount);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task Tool_registry_source_follows_the_manager_and_raises_changed()
    {
        var tools = """{"tools":[{"name":"one","inputSchema":{"type":"object"}}]}""";
        var server = new ScriptedServer().On(McpProtocol.ToolsList, _ => tools);
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server);
        using var source = new McpToolRegistrySource(fixture.Manager);
        var changed = 0;
        source.Changed += () => Interlocked.Increment(ref changed);
        Assert.Empty(source.Tools);

        await fixture.Manager.StartAllAsync(CancellationToken.None);

        Assert.Equal(["srv__one"], source.Tools.Select(tool => tool.Definition.Name));
        Assert.True(source.TryGet("srv__one", out var found));
        Assert.Same(source.Adapters[0], found);
        Assert.False(source.TryGet("srv__missing", out _));
        Assert.True(Volatile.Read(ref changed) > 0);

        var before = Volatile.Read(ref changed);
        tools = """{"tools":[{"name":"one","inputSchema":{"type":"object"}},{"name":"two","inputSchema":{"type":"object"}}]}""";
        fixture.Transports[0].Push(JsonRpc.Notification(McpProtocol.ToolsListChanged));

        Assert.True(await Eventually.Async(() => source.Tools.Count == 2, Wait));
        Assert.True(Volatile.Read(ref changed) > before);
        Assert.True(source.TryGet("srv__two", out _));
    }

    [Fact]
    public async Task Tool_registry_source_stops_reacting_after_dispose()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));
        var source = new McpToolRegistrySource(fixture.Manager);
        var changed = 0;
        source.Changed += () => Interlocked.Increment(ref changed);
        source.Dispose();
        source.Dispose();

        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        Assert.Equal(0, Volatile.Read(ref changed));
    }

    [Fact]
    public void Tool_registry_source_rejects_a_null_manager()
    {
        Assert.Throws<ArgumentNullException>(() => new McpToolRegistrySource(null!));
    }

    private static string JsonNormalize(string json, string arrayProperty, int index, string property)
    {
        using var document = JsonDocument.Parse(json);
        var element = document.RootElement.GetProperty(arrayProperty)[index].GetProperty(property);
        return element.GetRawText();
    }
}
