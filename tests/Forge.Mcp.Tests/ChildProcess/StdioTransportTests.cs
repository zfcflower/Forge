using Forge.Abstractions;
using Forge.Mcp.Tests.Fixtures;
using Xunit;

namespace Forge.Mcp.Tests.ChildProcess;

public class StdioTransportTests
{
    // Cold-starting the child is part of the first MCP request, and a loaded CI runner can take longer than the
    // request budget to start Windows PowerShell; the budget therefore covers process start as well.
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task Handshake_list_and_call_work_over_a_real_child_process()
    {
        var transport = new StdioTransport(PlatformStdioServer.Options());
        await using var client = new McpClient(transport, Clients.DefaultOptions(Generous));

        await client.ConnectAsync(CancellationToken.None);

        Assert.Equal("fake-stdio", client.ServerInfo!.Name);
        Assert.True(client.Capabilities.ToolsListChanged);
        Assert.True(transport.IsConnected);
        Assert.NotNull(transport.ProcessId);
        var tools = await client.ListToolsAsync(CancellationToken.None);
        Assert.Equal(["echo", "env", "hang", "exit"], tools.Select(tool => tool.Name));
        var result = await client.CallToolAsync("echo", """{"text":"hi"}""", CancellationToken.None);
        Assert.Equal("echo:hi", result.Text);
    }

    [Fact]
    public async Task Environment_and_working_directory_are_applied_to_the_child()
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-mcp-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var transport = new StdioTransport(PlatformStdioServer.Options(
                env: new Dictionary<string, string> { ["FAKE_MCP_VALUE"] = "hello-env" },
                workingDirectory: directory));
            await using var client = new McpClient(transport, Clients.DefaultOptions(Generous));
            await client.ConnectAsync(CancellationToken.None);

            var result = await client.CallToolAsync("env", null, CancellationToken.None);

            Assert.StartsWith("hello-env|", result.Text);
            Assert.EndsWith(Path.GetFileName(directory), result.Text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Stderr_lines_are_captured_and_non_json_stdout_is_routed_to_the_log()
    {
        var transport = new StdioTransport(PlatformStdioServer.Options());
        await using var client = new McpClient(transport, Clients.DefaultOptions(Generous));
        await client.ConnectAsync(CancellationToken.None);

        // stdout pollution is copied into the stderr log by the stdout reader, while real stderr lines come from the
        // stderr reader: two independent tasks, so neither stream's arrival implies the other one was recorded yet.
        var arrived = await Eventually.Async(
            () => transport.StderrTail.Contains("[stdout] banner line", StringComparison.Ordinal) &&
                  transport.StderrTail.Contains("fake-ready", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
        Assert.True(arrived, $"stderr log: {transport.StderrTail}");
        Assert.Contains("fake-ready", transport.StderrTail, StringComparison.Ordinal);
        Assert.Contains("fake-ready", transport.StderrLines);
    }

    [Fact]
    public async Task Process_exit_faults_pending_requests_and_raises_closed_with_the_exit_code()
    {
        var transport = new StdioTransport(PlatformStdioServer.Options());
        await using var client = new McpClient(transport, Clients.DefaultOptions(Generous));
        Exception? closedWith = null;
        var closed = new TaskCompletionSource();
        client.Closed += error =>
        {
            closedWith = error;
            closed.TrySetResult();
        };
        await client.ConnectAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<McpConnectionClosedException>(() => client.CallToolAsync("exit", null, CancellationToken.None));

        Assert.Contains($"exited with code {PlatformStdioServer.ExitCode}", error.InnerException?.Message ?? string.Empty);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains($"exited with code {PlatformStdioServer.ExitCode}", closedWith?.Message ?? string.Empty);
        Assert.False(transport.IsConnected);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Graceful_close_shuts_the_child_down()
    {
        var transport = new StdioTransport(PlatformStdioServer.Options());
        await using var client = new McpClient(transport, Clients.DefaultOptions(Generous));
        await client.ConnectAsync(CancellationToken.None);
        var pid = transport.ProcessId;

        await client.CloseAsync(CancellationToken.None);

        Assert.False(transport.IsConnected);
        Assert.Null(transport.ProcessId);
        Assert.NotNull(pid);
        Assert.False(IsRunning(pid.Value));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Graceful_close_raises_closed_once_without_an_error()
    {
        var transport = new StdioTransport(PlatformStdioServer.Options());
        await using var client = new McpClient(transport, Clients.DefaultOptions(Generous));
        var closedCalls = new List<Exception?>();
        client.Closed += error => closedCalls.Add(error);
        await client.ConnectAsync(CancellationToken.None);

        await client.CloseAsync(CancellationToken.None);

        var single = Assert.Single(closedCalls);
        Assert.Null(single);
    }

    [Fact]
    public async Task Timed_out_request_sends_cancellation_and_handshake_messages_arrive_on_stdin_as_lines()
    {
        var transport = new StdioTransport(PlatformStdioServer.Options());
        // Warm the child up first: the 3 s budget below is about the cancelled call, not about process start.
        await transport.StartAsync(CancellationToken.None);
        Assert.True(
            await Eventually.Async(() => transport.StderrLines.Contains("fake-ready", StringComparer.Ordinal), Generous),
            $"the fake stdio server never reported readiness; stderr: {transport.StderrTail}");
        await using var client = new McpClient(transport, Clients.DefaultOptions(TimeSpan.FromSeconds(3)));
        await client.ConnectAsync(CancellationToken.None);

        var timeout = await Assert.ThrowsAsync<McpTimeoutException>(() => client.CallToolAsync("hang", null, CancellationToken.None));

        Assert.Equal(McpProtocol.ToolsCall, timeout.Method);
        var arrived = await Eventually.Async(() => ReceivedMethods(transport).Contains(McpProtocol.Cancelled), TimeSpan.FromSeconds(10));
        var received = ReceivedMethods(transport);
        Assert.True(arrived, $"child received: {string.Join('|', received)}; connected={transport.IsConnected} clientConnected={client.IsConnected}");
        Assert.Equal(McpProtocol.Initialize, received[0]);
        Assert.Equal(McpProtocol.Initialized, received[1]);
        Assert.Equal(McpProtocol.ToolsCall, received[^2]);
        Assert.Equal(McpProtocol.Cancelled, received[^1]);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Missing_executable_fails_to_start_without_leaking_environment_values()
    {
        const string secret = "s3cr3t-token-value";
        var missing = Path.Combine(Path.GetTempPath(), "forge-no-such-dir-" + Guid.NewGuid().ToString("N"), "server.exe");
        var transport = new StdioTransport(new McpStdioOptions
        {
            Command = missing,
            Env = new Dictionary<string, string> { ["API_TOKEN"] = secret },
        });
        await using var _ = transport;

        var error = await Assert.ThrowsAsync<McpTransportException>(() => transport.StartAsync(CancellationToken.None));

        Assert.Contains("Failed to start MCP server process", error.Message);
        Assert.Contains(missing, error.Message);
        Assert.DoesNotContain(secret, error.Message);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task Sending_before_start_reports_the_process_is_not_running()
    {
        var transport = new StdioTransport(PlatformStdioServer.Options());
        await using var _ = transport;

        var error = await Assert.ThrowsAsync<McpConnectionClosedException>(async () =>
            await transport.SendAsync(new byte[] { (byte)'{', (byte)'}' }, CancellationToken.None));

        Assert.Contains("is not running", error.Message);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task Start_is_idempotent_while_the_process_runs_and_dispose_stops_it()
    {
        var transport = new StdioTransport(PlatformStdioServer.Options());

        await transport.StartAsync(CancellationToken.None);
        var first = transport.ProcessId;
        await transport.StartAsync(CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(first, transport.ProcessId);
        await transport.DisposeAsync();
        await transport.DisposeAsync();

        Assert.Null(transport.ProcessId);
        Assert.False(transport.IsConnected);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.StartAsync(CancellationToken.None));
    }

    [Fact]
    public void Constructor_rejects_null_options_and_blank_commands()
    {
        Assert.Throws<ArgumentNullException>(() => new StdioTransport((McpStdioOptions)null!));
        Assert.Throws<ArgumentException>(() => new StdioTransport(" "));
    }

    [Fact]
    public async Task Manager_runs_a_stdio_server_and_restarts_it_after_the_process_exits()
    {
        var config = new McpConfig(
        [
            new McpServerConfig
            {
                Name = "fake",
                Command = PlatformStdioServer.Command,
                Args = PlatformStdioServer.Args,
            },
        ],
        []);
        await using var manager = new McpServerManager(config, new McpServerManagerOptions
        {
            RequestTimeout = Generous,
            InitialReconnectDelay = TimeSpan.FromMilliseconds(100),
            StderrCapacityChars = 4096,
        });
        var states = new System.Collections.Concurrent.ConcurrentQueue<McpServerState>();
        manager.StateChanged += states.Enqueue;

        await manager.StartAsync("fake", CancellationToken.None);

        var tool = manager.Tools.Single(candidate => candidate.ToolName == "echo");
        Assert.Equal("fake__echo", tool.ExposedName);
        var first = await tool.InvokeAsync(new ToolCall("1", tool.ExposedName, """{"text":"one"}"""), new ToolContext("s", Path.GetTempPath()), CancellationToken.None);
        Assert.Equal("echo:one", first.Content);

        var crash = manager.Tools.Single(candidate => candidate.ToolName == "exit");
        var exited = await crash.InvokeAsync(new ToolCall("2", crash.ExposedName, "{}"), new ToolContext("s", Path.GetTempPath()), CancellationToken.None);
        Assert.True(exited.IsError);
        Assert.StartsWith("fake: The MCP connection closed before 'tools/call' completed.", exited.Content, StringComparison.Ordinal);

        Assert.True(await Eventually.Async(() => states.Any(state => state.Status == McpServerStatus.Failed &&
                                                                     state.LastError is not null &&
                                                                     state.LastError.Contains($"exited with code {PlatformStdioServer.ExitCode}")), TimeSpan.FromSeconds(10)),
            string.Join(" | ", states.Select(state => $"{state.Status}:{state.LastError}")));
        // Readiness comes from the handshake on stdout; the restarted child's stderr line is recorded by the other
        // reader task and can land after it.
        var ready = await Eventually.Async(
            () => manager.GetState("fake") is { IsReady: true, StderrTail: { } tail } &&
                  tail.Contains("fake-ready", StringComparison.Ordinal),
            TimeSpan.FromSeconds(60));
        Assert.True(ready, $"restarted server state: {manager.GetState("fake").Status}, stderr: {manager.GetState("fake").StderrTail}");
        var restarted = manager.GetState("fake");
        Assert.Equal(0, restarted.ConsecutiveFailures);
        Assert.Contains("fake-ready", restarted.StderrTail ?? string.Empty, StringComparison.Ordinal);
        var again = await manager.Tools.Single(candidate => candidate.ToolName == "echo").InvokeAsync(
            new ToolCall("3", "fake__echo", """{"text":"two"}"""), new ToolContext("s", Path.GetTempPath()), CancellationToken.None);
        Assert.Equal("echo:two", again.Content);
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            return !System.Diagnostics.Process.GetProcessById(pid).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ReceivedMethods(StdioTransport transport) =>
        transport.StderrLines
            .Where(line => line.StartsWith("received:", StringComparison.Ordinal))
            .Select(line => line["received:".Length..])
            .ToArray();
}
