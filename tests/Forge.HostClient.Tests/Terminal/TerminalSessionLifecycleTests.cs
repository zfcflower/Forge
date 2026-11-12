using Forge.Abstractions;
using Forge.Ipc;
using Xunit;

namespace Forge.HostClient.Tests.Terminal;

/// <summary>退出语义、terminal.stop 与 resize/foreground（任务条目 4、10、11）。
/// 注意：实现把 Host 业务错误/超时在 HasForegroundProcessAsync 里吞掉转 false（仅记诊断），
/// 而 ResizeAsync 的同类错误照常抛给调用方——两组测试按此实际行为断言。</summary>
public sealed class TerminalSessionLifecycleTests
{
    [Fact]
    public async Task Exit_completes_output_and_reports_the_code_then_ignores_late_calls()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        const string terminalId = "t-exit";
        var session = await TerminalFixtures.StartSessionAsync(host, client, terminalId, limit.Token);

        await host.NotifyClientAsync(TerminalOps.Output, TerminalFixtures.OutputJson(terminalId, 0, "tail"));
        await host.NotifyClientAsync(TerminalOps.Exit, TerminalFixtures.ExitJson(terminalId, 7));
        await host.SyncAsync();

        Assert.Equal(7, await session.Exited.Bounded());
        var body = await TerminalFixtures.DrainAsync(session);
        Assert.Equal("tail", System.Text.Encoding.UTF8.GetString(body));

        // 退出后：写与缩放在本地直接忽略，不上 IPC；前台探测恒 false 且不发请求。
        var inputsBefore = host.Notifications(TerminalOps.Input).Count;
        await session.WriteAsync("late"u8.ToArray(), limit.Token);
        Assert.Equal(inputsBefore, host.Notifications(TerminalOps.Input).Count);

        await session.ResizeAsync(new TerminalSize(100, 30), limit.Token);
        Assert.Empty(host.Requests(TerminalOps.Resize));

        Assert.False(await session.HasForegroundProcessAsync(limit.Token));
        Assert.Empty(host.Requests(TerminalOps.Foreground));
    }

    [Fact]
    public async Task Duplicate_exit_is_ignored_and_keeps_the_first_code()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        const string terminalId = "t-dup-exit";
        var session = await TerminalFixtures.StartSessionAsync(host, client, terminalId, limit.Token);

        await host.NotifyClientAsync(TerminalOps.Exit, TerminalFixtures.ExitJson(terminalId, 2));
        await host.SyncAsync();
        await host.NotifyClientAsync(TerminalOps.Exit, TerminalFixtures.ExitJson(terminalId, 9));
        await host.SyncAsync();

        Assert.Equal(2, await session.Exited.Bounded());
    }

    [Fact]
    public async Task Dispose_sends_stop_with_the_terminal_id_and_reports_the_host_exit_code()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        const string terminalId = "t-stop";
        TerminalFixtures.AnswerStart(host, terminalId);
        host.On(
            TerminalOps.Stop,
            async (message, _) =>
            {
                var id = message.Arguments!.Value.GetProperty("terminalId").GetString()!;
                await host.NotifyClientAsync(TerminalOps.Exit, TerminalFixtures.ExitJson(id, 4));
                return Json.Element("{}");
            });
        var session = await client.StartAsync(TerminalFixtures.Request(), limit.Token);

        await session.DisposeAsync();

        var stop = Assert.Single(host.Requests(TerminalOps.Stop));
        Assert.Equal(terminalId, stop.Arguments!.Value.GetProperty("terminalId").GetString());
        Assert.Equal(4, await session.Exited.Bounded());
        await session.Output.Completion.Bounded();

        // 幂等：重复 dispose 不再发 stop。
        await session.DisposeAsync();
        Assert.Single(host.Requests(TerminalOps.Stop));
    }

    [Fact]
    public async Task Dispose_without_a_host_exit_falls_back_to_minus_one_with_normal_output_completion()
    {
        // Host 对 stop 应答了 {} 但没发 exit：会话本地收口，Exited 为 -1（“结束了但码未知”），
        // Output 正常完成而非 fault。
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var session = await TerminalFixtures.StartSessionAsync(host, client, "t-stop-quiet", limit.Token);

        await session.DisposeAsync();

        Assert.Single(host.Requests(TerminalOps.Stop));
        Assert.Equal(-1, await session.Exited.Bounded());
        await session.Output.Completion.Bounded();
    }

    [Fact]
    public async Task Resize_sends_columns_and_rows_and_succeeds()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var session = await TerminalFixtures.StartSessionAsync(host, client, "t-resize", limit.Token);

        await session.ResizeAsync(new TerminalSize(132, 43), limit.Token);

        var resize = Assert.Single(host.Requests(TerminalOps.Resize));
        Assert.Equal("t-resize", resize.Arguments!.Value.GetProperty("terminalId").GetString());
        Assert.Equal(132, resize.Arguments!.Value.GetProperty("columns").GetInt32());
        Assert.Equal(43, resize.Arguments!.Value.GetProperty("rows").GetInt32());
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Resize_propagates_host_business_errors_to_the_caller()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        TerminalFixtures.AnswerStart(host, "t-resize-err");
        host.On(TerminalOps.Resize, (_, _) => throw new FakeHostError("gone", "unknown terminal"));
        var session = await client.StartAsync(TerminalFixtures.Request(), limit.Token);

        var failure = await Assert.ThrowsAsync<IpcRemoteException>(
            () => session.ResizeAsync(new TerminalSize(80, 24), limit.Token).AsTask());

        Assert.Equal("gone", failure.Code);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Resize_timeout_propagates_to_the_caller()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(
            host, options: new ExtensionHostClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(250) });
        using var limit = TestLimit.Create();
        TerminalFixtures.AnswerStart(host, "t-resize-timeout");
        host.On(TerminalOps.Resize, FakeHost.NeverAnswer);
        var session = await client.StartAsync(TerminalFixtures.Request(), limit.Token);

        await Assert.ThrowsAsync<IpcTimeoutException>(
            () => session.ResizeAsync(new TerminalSize(80, 24), limit.Token).AsTask());

        await session.DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Foreground_reads_the_busy_flag(bool busy)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        TerminalFixtures.AnswerStart(host, "t-fg");
        host.On(TerminalOps.Foreground, (_, _) => Task.FromResult(Json.Element(busy ? """{"busy":true}""" : """{"busy":false}""")));
        var session = await client.StartAsync(TerminalFixtures.Request(), limit.Token);

        Assert.Equal(busy, await session.HasForegroundProcessAsync(limit.Token));

        var probe = Assert.Single(host.Requests(TerminalOps.Foreground));
        Assert.Equal("t-fg", probe.Arguments!.Value.GetProperty("terminalId").GetString());
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Foreground_returns_false_on_host_business_errors_and_records_a_diagnostic()
    {
        // 实际行为：实现吞掉 IpcRemoteException 转 false（只记诊断），不是抛给调用方。
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        TerminalFixtures.AnswerStart(host, "t-fg-err");
        host.On(TerminalOps.Foreground, (_, _) => throw new FakeHostError("gone", "unknown terminal"));
        var session = await client.StartAsync(TerminalFixtures.Request(), limit.Token);

        Assert.False(await session.HasForegroundProcessAsync(limit.Token));

        BoundRoots.AssertDiagnostic(client, "terminal.foreground");
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Foreground_returns_false_on_timeout_and_records_a_diagnostic()
    {
        // 实际行为：超时同样被吞掉转 false（只记诊断），不是抛 IpcTimeoutException。
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(
            host, options: new ExtensionHostClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(250) });
        using var limit = TestLimit.Create();
        TerminalFixtures.AnswerStart(host, "t-fg-timeout");
        host.On(TerminalOps.Foreground, FakeHost.NeverAnswer);
        var session = await client.StartAsync(TerminalFixtures.Request(), limit.Token);

        Assert.False(await session.HasForegroundProcessAsync(limit.Token));

        BoundRoots.AssertDiagnostic(client, "terminal.foreground");
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Foreground_without_a_busy_flag_reports_false_and_records_a_diagnostic()
    {
        // 读不到 busy 字段 ≠ “没有前台进程”：实现同样按 false 处理并记诊断。
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        TerminalFixtures.AnswerStart(host, "t-fg-bad");
        host.Answer(TerminalOps.Foreground, "{}");
        var session = await client.StartAsync(TerminalFixtures.Request(), limit.Token);

        Assert.False(await session.HasForegroundProcessAsync(limit.Token));

        BoundRoots.AssertDiagnostic(client, "terminal.foreground");
        await session.DisposeAsync();
    }
}
