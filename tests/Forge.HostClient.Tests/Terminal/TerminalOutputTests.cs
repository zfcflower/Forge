using System.Threading.Channels;
using Forge.Abstractions;
using Forge.Ipc;
using Xunit;

namespace Forge.HostClient.Tests.Terminal;

/// <summary>输出确认节奏、seq 校验与早到输出（任务条目 6、7、9）。</summary>
public sealed class TerminalOutputTests
{
    [Fact]
    public async Task Output_is_not_acknowledged_before_the_consumer_reads_it()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        const string terminalId = "t-ack-quiet";
        var session = await TerminalFixtures.StartSessionAsync(host, client, terminalId, limit.Token);

        var chunk = new byte[16 * 1024];
        new Random(7).NextBytes(chunk);
        for (var seq = 0; seq < 5; seq++)
        {
            await host.NotifyClientAsync(TerminalOps.Output, TerminalFixtures.OutputJson(terminalId, seq, chunk));
        }

        await host.SyncAsync();

        Assert.Empty(host.Notifications(TerminalOps.Ack));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Consuming_5x16KiB_acks_the_total_in_frames_within_64KiB()
    {
        // 实现规则：累计未确认 ≥64 KiB 或队列读空时发一次 ack。5×16 KiB 应收敛为
        // 65536（第 4 块读完触发）+ 16384（读空触发），累计恰好等于解码总字节。
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        const string terminalId = "t-ack";
        var session = await TerminalFixtures.StartSessionAsync(host, client, terminalId, limit.Token);

        var expected = new List<byte>();
        var random = new Random(11);
        for (var seq = 0; seq < 5; seq++)
        {
            var chunk = new byte[16 * 1024];
            random.NextBytes(chunk);
            expected.AddRange(chunk);
            await host.NotifyClientAsync(TerminalOps.Output, TerminalFixtures.OutputJson(terminalId, seq, chunk));
        }

        await host.SyncAsync();

        var received = new List<byte>();
        for (var index = 0; index < 5; index++)
        {
            received.AddRange((await session.Output.ReadAsync(limit.Token)).ToArray());
        }

        Assert.Equal(expected.ToArray(), received.ToArray());

        var acks = await host.WaitForNotificationsAsync(TerminalOps.Ack, 2);
        Assert.Equal(2, acks.Count);
        var sizes = acks.Select(ack => ack.Arguments!.Value.GetProperty("bytes").GetInt64()).ToArray();
        Assert.Equal([65536L, 16384L], sizes);
        foreach (var size in sizes)
        {
            Assert.True(size is > 0 and <= 64 * 1024, $"ack of {size} bytes breaks the 64 KiB rule");
        }

        Assert.Equal(5L * 16 * 1024, TerminalFixtures.AckTotal(acks));
        foreach (var ack in acks)
        {
            Assert.Equal(terminalId, ack.Arguments!.Value.GetProperty("terminalId").GetString());
        }

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Skipped_sequence_faults_output_and_exited_with_a_protocol_error()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        const string terminalId = "t-gap";
        var session = await TerminalFixtures.StartSessionAsync(host, client, terminalId, limit.Token);

        await host.NotifyClientAsync(TerminalOps.Output, TerminalFixtures.OutputJson(terminalId, 0, "first"));
        await host.SyncAsync();
        Assert.Equal("first", System.Text.Encoding.UTF8.GetString((await session.Output.ReadAsync(limit.Token)).ToArray()));

        await host.NotifyClientAsync(TerminalOps.Output, TerminalFixtures.OutputJson(terminalId, 2, "skipped-one"));
        await host.SyncAsync();

        var outputFailure = await Assert.ThrowsAsync<InvalidDataException>(
            () => session.Output.Completion.Bounded());
        Assert.Contains("frame 2", outputFailure.Message);
        Assert.Contains("frame 1", outputFailure.Message);

        var exitedFailure = await Assert.ThrowsAsync<InvalidDataException>(() => session.Exited.Bounded());
        Assert.Contains("frame 2", exitedFailure.Message);

        // 通道读端看到的是 ChannelClosedException，其 Inner 才是协议错误本身。
        var readFailure = await Assert.ThrowsAsync<ChannelClosedException>(
            () => session.Output.ReadAsync(limit.Token).AsTask());
        Assert.IsType<InvalidDataException>(readFailure.InnerException);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Output_before_the_start_answer_is_buffered_and_replayed_in_order()
    {
        // 早到输出是显式设计：start 应答在途时先到的 output/exit 缓存在客户端，
        // 注册时按 seq 重放，首批字节不丢失、exit 正常收口。
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        const string terminalId = "t-early";
        var releaseAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On(
            TerminalOps.Start,
            async (_, ct) =>
            {
                await releaseAnswer.Task.WaitAsync(ct).ConfigureAwait(false);
                return Json.Element(TerminalFixtures.StartResultJson(terminalId));
            });

        using var limit = TestLimit.Create();
        var starting = client.StartAsync(TerminalFixtures.Request(), CancellationToken.None).AsTask();
        await host.WaitForRequestsAsync(TerminalOps.Start, 1);

        await host.NotifyClientAsync(TerminalOps.Output, TerminalFixtures.OutputJson(terminalId, 0, "hello "));
        await host.NotifyClientAsync(TerminalOps.Output, TerminalFixtures.OutputJson(terminalId, 1, "world"));
        await host.NotifyClientAsync(TerminalOps.Exit, TerminalFixtures.ExitJson(terminalId, 5));
        // 往返一次：确认 Host 发出的三帧都已在客户端处理完，应答才放行，
        // 于是三帧必走 early-buffer 路径而非注册后的直达路径。
        await host.SyncAsync();
        releaseAnswer.TrySetResult();

        var session = await starting.Bounded();
        Assert.Equal(terminalId, session.Id);

        var body = await TerminalFixtures.DrainAsync(session);
        Assert.Equal("hello world", System.Text.Encoding.UTF8.GetString(body));
        Assert.Equal(5, await session.Exited.Bounded());
    }

    [Fact]
    public async Task Early_exit_without_output_still_reports_the_exit_code()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        const string terminalId = "t-early-exit";
        var releaseAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On(
            TerminalOps.Start,
            async (_, ct) =>
            {
                await releaseAnswer.Task.WaitAsync(ct).ConfigureAwait(false);
                return Json.Element(TerminalFixtures.StartResultJson(terminalId));
            });

        var starting = client.StartAsync(TerminalFixtures.Request(), CancellationToken.None).AsTask();
        await host.WaitForRequestsAsync(TerminalOps.Start, 1);

        await host.NotifyClientAsync(TerminalOps.Exit, TerminalFixtures.ExitJson(terminalId, 3));
        await host.SyncAsync();
        releaseAnswer.TrySetResult();

        var session = await starting.Bounded();
        Assert.Equal(3, await session.Exited.Bounded());
        Assert.Empty(await TerminalFixtures.DrainAsync(session));
    }
}
