using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Mcp.Tests.Fixtures;
using Xunit;
using static Forge.Mcp.Tests.Fixtures.ManagerFixture;

namespace Forge.Mcp.Tests.Manager;

public class McpServerManagerApplyTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ApplyConfig_registers_new_servers_without_starting_them()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("a")));
        var manager = fixture.Manager;

        await manager.ApplyConfigAsync(Of(Stdio("a"), Stdio("b", disabled: true), Stdio("c")), CancellationToken.None);

        Assert.Equal(["a", "b", "c"], manager.ServerNames);
        Assert.Equal(["a", "b", "c"], manager.States.Select(state => state.Name));
        Assert.Equal(0, fixture.TransportCount("b"));
        Assert.Equal(0, fixture.TransportCount("c"));
        var b = manager.GetState("b");
        Assert.Equal(McpServerStatus.Stopped, b.Status);
        Assert.False(b.Enabled);
        var c = manager.GetState("c");
        Assert.Equal(McpServerStatus.Stopped, c.Status);
        Assert.True(c.Enabled);
    }

    [Fact]
    public async Task ApplyConfig_removes_a_server_its_transport_its_tools_and_its_state()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("gone"), Stdio("stay")));
        var manager = fixture.Manager;
        await manager.StartAllAsync(CancellationToken.None);
        Assert.Equal(["gone__echo", "stay__echo"], manager.Tools.Select(tool => tool.ExposedName));

        await manager.ApplyConfigAsync(Of(Stdio("stay")), CancellationToken.None);

        Assert.Equal(["stay"], manager.ServerNames);
        Assert.Equal(["stay__echo"], manager.Tools.Select(tool => tool.ExposedName));
        Assert.Equal(1, fixture.Transports.Single(transport => transport.Name == "gone").DisposeCount);
        Assert.Throws<McpException>(() => manager.GetState("gone"));
        Assert.Equal(McpServerStatus.Ready, manager.GetState("stay").Status);
    }

    [Fact]
    public async Task ApplyConfig_restarts_a_changed_server_and_applies_the_new_entry()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));
        var manager = fixture.Manager;
        await manager.StartAsync("srv", CancellationToken.None);
        Assert.Equal(ToolSideEffect.Execute, manager.Tools[0].SideEffect);

        await manager.ApplyConfigAsync(Of(Stdio("srv", readOnlyTools: ["echo"])), CancellationToken.None);

        Assert.Equal(McpServerStatus.Ready, manager.GetState("srv").Status);
        var tool = Assert.Single(manager.Tools);
        Assert.Equal(ToolSideEffect.ReadOnly, tool.SideEffect);

        // A changed entry gets a transport of its own: the old one captured the old command, arguments and environment when it
        // was created, so reusing it would restart the process the file no longer describes.
        Assert.Equal(2, fixture.Transports.Count);
        Assert.Equal(1, fixture.Transports[0].StartCount);
        Assert.Equal(1, fixture.Transports[1].StartCount);
        Assert.Equal(1, fixture.Transports[0].DisposeCount);
    }

    [Fact]
    public async Task ApplyConfig_restarts_a_changed_entry_on_a_transport_built_from_the_new_entry()
    {
        var built = new List<McpServerConfig>();
        var server = new ScriptedServer();
        await using var fixture = new ManagerFixture(
            Of(Stdio("srv", args: ["--one"])),
            transportFor: config =>
            {
                lock (built)
                {
                    built.Add(config);
                }

                return new LoopbackTransport(config.Name) { Responder = server.Respond };
            });
        var manager = fixture.Manager;
        await manager.StartAsync("srv", CancellationToken.None);
        Assert.Equal(["--one"], built[0].Args);

        await manager.ApplyConfigAsync(Of(Stdio("srv", args: ["--two"])), CancellationToken.None);

        Assert.Equal(2, built.Count);
        Assert.Equal(["--two"], built[1].Args);
    }

    [Fact]
    public async Task ApplyConfig_stops_a_server_whose_new_entry_is_disabled()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));
        var manager = fixture.Manager;
        await manager.StartAsync("srv", CancellationToken.None);
        Assert.True(manager.IsEnabled("srv"));

        await manager.ApplyConfigAsync(Of(Stdio("srv", disabled: true)), CancellationToken.None);

        var state = manager.GetState("srv");
        Assert.Equal(McpServerStatus.Stopped, state.Status);
        Assert.False(state.Enabled);
        Assert.Empty(manager.Tools);
        Assert.Equal(1, fixture.Transports[0].StartCount);
    }

    [Fact]
    public async Task ApplyConfig_enables_a_server_whose_entry_stopped_being_disabled()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv", disabled: true)));
        var manager = fixture.Manager;
        Assert.False(manager.IsEnabled("srv"));

        // A hand edit dropped `disabled`: the file is what decides, so the server comes back up.
        await manager.ApplyConfigAsync(Of(Stdio("srv")), CancellationToken.None);

        var state = manager.GetState("srv");
        Assert.True(state.Enabled);
        Assert.Equal(McpServerStatus.Ready, state.Status);
    }

    [Fact]
    public async Task ApplyConfig_keeps_a_server_running_when_the_entry_did_not_change()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));
        var manager = fixture.Manager;
        await manager.StartAsync("srv", CancellationToken.None);

        await manager.ApplyConfigAsync(Of(Stdio("srv")), CancellationToken.None);

        Assert.Equal(McpServerStatus.Ready, manager.GetState("srv").Status);
        Assert.Equal(1, fixture.Transports[0].StartCount);
    }

    [Fact]
    public async Task ApplyConfig_replaces_the_manager_diagnostics()
    {
        var first = new McpConfig([Stdio("srv")], [new McpConfigDiagnostic("a.json", "x", "first")]);
        var second = new McpConfig([Stdio("srv")], [new McpConfigDiagnostic("b.json", null, "second"), new McpConfigDiagnostic("c.json", "y", "third")]);
        await using var fixture = new ManagerFixture(first);

        await fixture.Manager.ApplyConfigAsync(second, CancellationToken.None);

        Assert.Equal(["second", "third"], fixture.Manager.Diagnostics.Select(diagnostic => diagnostic.Message));
    }

    [Fact]
    public async Task ApplyConfig_marks_a_failed_restart_on_that_server_only_and_does_not_throw()
    {
        var initializeCount = 0;
        var failing = new ScriptedServer().On(McpProtocol.Initialize, _ =>
        {
            if (Interlocked.Increment(ref initializeCount) == 1)
            {
                return ScriptedServer.InitializeResultJson;
            }

            throw new ScriptedRpcError(-32603, "restart denied");
        });
        await using var fixture = new ManagerFixture(
            Of(Stdio("bad"), Stdio("good")),
            name => name == "bad" ? failing : new ScriptedServer(),
            maxReconnectAttempts: 0);
        var manager = fixture.Manager;
        await manager.StartAsync("bad", CancellationToken.None);
        await manager.StartAsync("good", CancellationToken.None);

        await manager.ApplyConfigAsync(Of(Stdio("bad", readOnlyTools: ["echo"]), Stdio("good")), CancellationToken.None);

        var bad = manager.GetState("bad");
        Assert.Equal(McpServerStatus.Failed, bad.Status);
        Assert.Contains("restart denied", bad.LastError);
        Assert.Equal(McpServerStatus.Ready, manager.GetState("good").Status);
        Assert.Equal(1, fixture.Transports.Single(transport => transport.Name == "good").StartCount);
        Assert.Equal(["good__echo"], manager.Tools.Select(tool => tool.ExposedName));
    }

    [Fact]
    public async Task ApplyConfig_rejects_a_null_config()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));

        await Assert.ThrowsAsync<ArgumentNullException>(() => fixture.Manager.ApplyConfigAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task ApplyConfig_observes_a_cancelled_token_while_stopping_a_removed_server()
    {
        var hanging = new ScriptedServer().On(McpProtocol.Initialize, _ => null);
        await using var fixture = new ManagerFixture(
            Of(Stdio("slow")),
            _ => hanging,
            requestTimeout: TimeSpan.FromSeconds(2));
        var manager = fixture.Manager;
        var startTask = manager.StartAsync("slow", CancellationToken.None);
        await Eventually.AssertAsync(() => manager.GetState("slow").Status == McpServerStatus.Starting, "the start to reach the handshake");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ApplyConfigAsync(Of(), cts.Token));

        // The abandoned start fails on its own request timeout and releases the start gate.
        await Assert.ThrowsAsync<McpTimeoutException>(() => startTask);
    }

    [Fact]
    public async Task ApplyConfig_concurrent_with_start_does_not_deadlock_or_throw()
    {
        var baseConfig = Of(Stdio("a"), Stdio("b"));
        var withToggle = Of(Stdio("a"), Stdio("b"), Stdio("toggle"));
        await using var fixture = new ManagerFixture(baseConfig);
        var manager = fixture.Manager;

        var apply = Task.Run(async () =>
        {
            for (var i = 0; i < 8; i++)
            {
                await manager.ApplyConfigAsync(withToggle, CancellationToken.None);
                await manager.ApplyConfigAsync(baseConfig, CancellationToken.None);
            }
        });
        var start = Task.Run(() => manager.StartAllAsync(CancellationToken.None));

        await Task.WhenAll(apply, start).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(["a", "b"], manager.ServerNames);
        Assert.True(manager.GetState("a").IsReady);
        Assert.True(manager.GetState("b").IsReady);
    }

    [Fact]
    public async Task Options_are_read_again_for_each_connection_so_a_changed_timeout_applies_without_rebuilding_the_manager()
    {
        var transport = new LoopbackTransport("srv");
        transport.Responder = new ScriptedServer().Respond;
        McpServerManagerOptions Options(TimeSpan timeout) => new()
        {
            RequestTimeout = timeout,
            InitialReconnectDelay = TimeSpan.FromMilliseconds(20),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(100),
            MaxReconnectAttempts = 0,
            TransportFactory = _ => transport,
        };
        var current = Options(TimeSpan.FromSeconds(5));
        await using var manager = new McpServerManager(Of(Stdio("srv")), () => current);

        await manager.StartAsync("srv", CancellationToken.None);
        await manager.StopAsync("srv", CancellationToken.None);

        transport.Responder = new ScriptedServer().On(McpProtocol.Initialize, _ => null).Respond;
        current = Options(TimeSpan.FromMilliseconds(150));

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<McpTimeoutException>(() => manager.RestartAsync("srv", CancellationToken.None));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"restart took {watch.Elapsed.TotalSeconds:F1}s; the new timeout was not read");
    }

    [Fact]
    public async Task Backoff_delays_are_read_fresh_for_each_scheduled_reconnect()
    {
        var attempts = new ConcurrentQueue<long>();
        var stateful = new ScriptedServer().On(McpProtocol.Initialize, _ =>
        {
            attempts.Enqueue(Stopwatch.GetTimestamp());
            if (attempts.Count <= 2)
            {
                throw new ScriptedRpcError(-32603, "warming up");
            }

            return ScriptedServer.InitializeResultJson;
        });
        McpServerManagerOptions Options(TimeSpan delay) => new()
        {
            RequestTimeout = TimeSpan.FromSeconds(5),
            InitialReconnectDelay = delay,
            MaxReconnectDelay = TimeSpan.FromSeconds(30),
            MaxReconnectAttempts = 5,
            TransportFactory = config => new LoopbackTransport(config.Name) { Responder = stateful.Respond },
        };
        var current = Options(TimeSpan.FromSeconds(2));
        await using var manager = new McpServerManager(Of(Stdio("srv")), () => current);

        await Assert.ThrowsAsync<McpProtocolException>(() => manager.StartAsync("srv", CancellationToken.None));

        current = Options(TimeSpan.FromMilliseconds(20));
        Assert.True(await Eventually.Async(() => manager.GetState("srv").IsReady, Wait));

        var stamps = attempts.ToArray();
        Assert.Equal(3, stamps.Length);
        var first = Stopwatch.GetElapsedTime(stamps[0], stamps[1]);
        var second = Stopwatch.GetElapsedTime(stamps[1], stamps[2]);
        Assert.True(first >= TimeSpan.FromMilliseconds(1500), $"first backoff was {first.TotalMilliseconds:F0}ms; expected the initial 2s");
        Assert.True(second < TimeSpan.FromMilliseconds(1500), $"second backoff was {second.TotalMilliseconds:F0}ms; expected the 20ms read from the new options");
    }

    [Fact]
    public async Task ConfigOfTool_resolves_an_exposed_name_back_to_the_server_config()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));
        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        var config = fixture.Manager.ConfigOfTool("srv__echo");

        Assert.NotNull(config);
        Assert.Equal("srv", config.Name);
        Assert.Equal("fake-srv", config.Command);
    }

    [Fact]
    public async Task ConfigOfTool_resolves_a_disambiguated_exposed_name()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("a.b")));
        await fixture.Manager.StartAsync("a.b", CancellationToken.None);

        var tool = Assert.Single(fixture.Manager.Tools);

        Assert.NotEqual("a_b__echo", tool.ExposedName);
        Assert.Equal("a.b", fixture.Manager.ConfigOfTool(tool.ExposedName)!.Name);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("srv__missing")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ConfigOfTool_returns_null_for_unknown_or_blank_names(string name)
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));

        Assert.Null(fixture.Manager.ConfigOfTool(name));
    }

    [Fact]
    public async Task ToolsOf_lists_a_servers_tools_only_after_it_is_ready()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));
        var manager = fixture.Manager;

        Assert.Empty(manager.ToolsOf("srv"));
        await manager.StartAsync("srv", CancellationToken.None);

        Assert.Equal(["srv__echo"], manager.ToolsOf("srv").Select(tool => tool.ExposedName));
    }

    [Fact]
    public async Task ToolsOf_rejects_an_unknown_server()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));

        Assert.Throws<McpException>(() => fixture.Manager.ToolsOf("nope"));
    }

    [Fact]
    public async Task ApplyConfig_lets_the_last_of_two_entries_with_the_same_name_win()
    {
        var config = new McpConfig([Stdio("dup", disabled: true), Stdio("dup")], []);
        await using var fixture = new ManagerFixture(Of());
        var manager = fixture.Manager;

        await manager.ApplyConfigAsync(config, CancellationToken.None);

        Assert.Equal(["dup"], manager.ServerNames);
        var state = Assert.Single(manager.States);
        Assert.True(state.Enabled);
        Assert.Equal("fake-dup", manager.GetServer("dup").Command);

        await manager.StartAsync("dup", CancellationToken.None);

        Assert.Equal(["dup__echo"], manager.Tools.Select(tool => tool.ExposedName));
        Assert.Equal(1, fixture.TransportCount("dup"));
    }

    [Fact]
    public async Task Probe_reports_ready_with_the_tool_count_and_the_elapsed_time()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));

        var probe = await fixture.Manager.ProbeAsync("srv", CancellationToken.None);

        Assert.True(probe.IsReady);
        Assert.Equal("srv", probe.Name);
        Assert.Null(probe.Error);
        Assert.True(probe.Capabilities.Tools);
        var tool = Assert.Single(probe.Tools);
        Assert.Equal("srv__echo", tool.ExposedName);
        Assert.True(probe.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Probe_reports_failed_with_the_error_when_the_server_cannot_start()
    {
        var failing = new ScriptedServer().On(McpProtocol.Initialize, _ => throw new ScriptedRpcError(-32603, "no dice"));
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => failing, maxReconnectAttempts: 0);

        var probe = await fixture.Manager.ProbeAsync("srv", CancellationToken.None);

        Assert.False(probe.IsReady);
        Assert.Equal(McpServerStatus.Failed, probe.Status);
        Assert.Contains("no dice", probe.Error);
        Assert.Empty(probe.Tools);
    }

    [Fact]
    public async Task Probe_rethrows_the_callers_cancellation()
    {
        var hanging = new ScriptedServer().On(McpProtocol.Initialize, _ => null);
        await using var fixture = new ManagerFixture(
            Of(Stdio("srv")),
            _ => hanging,
            requestTimeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        var probe = fixture.Manager.ProbeAsync("srv", cts.Token);
        await Eventually.AssertAsync(() => fixture.Manager.GetState("srv").Status == McpServerStatus.Starting, "the probe to reach the handshake");

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
    }

    [Fact]
    public async Task Probe_failure_carries_the_stderr_tail_of_the_server_state()
    {
        var options = FakeStdioServer.Options();
        var config = new McpConfig(
            [new McpServerConfig { Name = "fake", Command = options.Command, Args = options.Args }],
            []);
        await using var manager = new McpServerManager(config, new McpServerManagerOptions
        {
            RequestTimeout = Wait,
            InitialReconnectDelay = TimeSpan.FromMilliseconds(20),
            MaxReconnectAttempts = 0,
        });
        await manager.StartAsync("fake", CancellationToken.None);

        var exit = manager.Tools.Single(tool => tool.ToolName == "exit");
        await exit.InvokeAsync(new ToolCall("1", exit.ExposedName, "{}"), new ToolContext("s", Path.GetTempPath()), CancellationToken.None);
        await Eventually.AssertAsync(
            () => manager.GetState("fake") is { Status: McpServerStatus.Failed, StderrTail: { } tail } &&
                  tail.Contains("fake-ready", StringComparison.Ordinal),
            "the exited server to reach a failed state with its stderr captured");

        await manager.SetEnabledAsync("fake", false, CancellationToken.None);

        var probe = await manager.ProbeAsync("fake", CancellationToken.None);

        Assert.Equal(McpServerStatus.Failed, probe.Status);
        Assert.Contains("disabled", probe.Error);
        Assert.Contains("fake-ready", probe.StderrTail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_of_an_unknown_server_reports_failed_instead_of_throwing()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv")));

        var probe = await fixture.Manager.ProbeAsync("nope", CancellationToken.None);

        Assert.Equal(McpServerStatus.Failed, probe.Status);
        Assert.Contains("Unknown MCP server 'nope'", probe.Error);
    }
}
