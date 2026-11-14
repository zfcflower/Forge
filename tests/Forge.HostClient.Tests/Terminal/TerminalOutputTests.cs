using System.Threading.Channels;
using Forge.Abstractions;
using Forge.Ipc;
using Forge.Ipc.Protocol;
using Xunit;

namespace Forge.HostClient.Tests.Terminal;

/// <summary>输出确认节奏、seq 校验与早到输出（任务条目 6、7、9）。</summary>
public sealed class TerminalOutputTests
{
    [Fact]
    public async Task Output_is_not_acknowledged_before_the_consumer_reads_it()
    {
        await using var harness = await HostClientHarness.StartAsync();
        const string terminalId = "t-ack-quiet";
        var session = await TerminalFixtures.StartSessionAsync(harness.Host, harness.Client, terminalId, harness.Limit.Token);

        var chunk = new byte[16 * 1024];
        new Random(7).NextBytes(chunk);
        for (var seq = 0; seq < 5; seq++)
        {
            await harness.Host.NotifyClientAsync(TerminalOperations.Output, TerminalFixtures.Output(terminalId, seq, chunk));
        }

        await harness.Host.SyncAsync();

        Assert.Empty(harness.Host.Notifications(TerminalOperations.Ack));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Consuming_5x16KiB_acks_the_total_in_frames_within_64KiB()
    {
        // 实现规则：累计未确认 ≥64 KiB 或队列读空时发一次 ack。5×16 KiB 应收敛为
        // 65536（第 4 块读完触发）+ 16384（读空触发），累计恰好等于解码总字节。
        await using var harness = await HostClientHarness.StartAsync();
        const string terminalId = "t-ack";
        var session = await TerminalFixtures.StartSessionAsync(harness.Host, harness.Client, terminalId, harness.Limit.Token);

        var expected = new List<byte>();
        var random = new Random(11);
        for (var seq = 0; seq < 5; seq++)
        {
            var chunk = new byte[16 * 1024];
            random.NextBytes(chunk);
            expected.AddRange(chunk);
            await harness.Host.NotifyClientAsync(TerminalOperations.Output, TerminalFixtures.Output(terminalId, seq, chunk));
        }

        await harness.Host.SyncAsync();

        var received = new List<byte>();
        for (var index = 0; index < 5; index++)
        {
            received.AddRange((await session.Output.ReadAsync(harness.Limit.Token)).ToArray());
        }

        Assert.Equal(expected.ToArray(), received.ToArray());

        var acks = await harness.Host.WaitForNotificationsAsync(TerminalOperations.Ack, 2);
        Assert.Equal(2, acks.Count);
        var sizes = acks.Select(ack => ack.Read<TerminalAck>().Bytes).ToArray();
        Assert.Equal([65536L, 16384L], sizes);
        foreach (var size in sizes)
        {
            Assert.True(size is > 0 and <= 64 * 1024, $"ack of {size} bytes breaks the 64 KiB rule");
        }

        Assert.Equal(5L * 16 * 1024, TerminalFixtures.AckTotal(acks));
        foreach (var ack in acks)
        {
            Assert.Equal(terminalId, ack.Read<TerminalAck>().TerminalId);
        }

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Skipped_sequence_faults_output_and_exited_with_a_protocol_error()
    {
        await using var harness = await HostClientHarness.StartAsync();
        const string terminalId = "t-gap";
        var session = await TerminalFixtures.StartSessionAsync(harness.Host, harness.Client, terminalId, harness.Limit.Token);

        await harness.Host.NotifyClientAsync(TerminalOperations.Output, TerminalFixtures.Output(terminalId, 0, "first"));
        await harness.Host.SyncAsync();
        Assert.Equal("first", System.Text.Encoding.UTF8.GetString((await session.Output.ReadAsync(harness.Limit.Token)).ToArray()));

        await harness.Host.NotifyClientAsync(TerminalOperations.Output, TerminalFixtures.Output(terminalId, 2, "skipped-one"));
        await harness.Host.SyncAsync();

        var outputFailure = await Assert.ThrowsAsync<InvalidDataException>(
            () => session.Output.Completion.Bounded());
        Assert.Contains("frame 2", outputFailure.Message);
        Assert.Contains("frame 1", outputFailure.Message);

        var exitedFailure = await Assert.ThrowsAsync<InvalidDataException>(() => session.Exited.Bounded());
        Assert.Contains("frame 2", exitedFailure.Message);

        // 通道读端看到的是 ChannelClosedException，其 Inner 才是协议错误本身。
        var readFailure = await Assert.ThrowsAsync<ChannelClosedException>(
            () => session.Output.ReadAsync(harness.Limit.Token).AsTask());
        Assert.IsType<InvalidDataException>(readFailure.InnerException);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Output_without_a_terminal_id_is_dropped_with_a_diagnostic()
    {
        await using var harness = await HostClientHarness.StartAsync();
        const string terminalId = "t-no-id";
        var session = await TerminalFixtures.StartSessionAsync(harness.Host, harness.Client, terminalId, harness.Limit.Token);

        await harness.Host.NotifyClientAsync(TerminalOperations.Output, new TerminalData("", 0, "lost"u8.ToArray()));
        await harness.Host.NotifyClientAsync(TerminalOperations.Output, TerminalFixtures.Output(terminalId, 0, "kept"));
        await harness.Host.SyncAsync();

        BoundRoots.AssertDiagnostic(harness.Client, "terminal.output carried no usable payload: the message carries no terminal id.");
        Assert.Equal("kept", System.Text.Encoding.UTF8.GetString((await session.Output.ReadAsync(harness.Limit.Token)).ToArray()));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Output_with_a_negative_sequence_is_dropped_with_a_diagnostic()
    {
        await using var harness = await HostClientHarness.StartAsync();
        const string terminalId = "t-negative";
        var session = await TerminalFixtures.StartSessionAsync(harness.Host, harness.Client, terminalId, harness.Limit.Token);

        await harness.Host.NotifyClientAsync(TerminalOperations.Output, TerminalFixtures.Output(terminalId, -3, "lost"));
        await harness.Host.NotifyClientAsync(TerminalOperations.Output, TerminalFixtures.Output(terminalId, 0, "kept"));
        await harness.Host.SyncAsync();

        BoundRoots.AssertDiagnostic(harness.Client, "the message carries a negative sequence number (-3).");
        Assert.Equal("kept", System.Text.Encoding.UTF8.GetString((await session.Output.ReadAsync(harness.Limit.Token)).ToArray()));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Undecodable_output_and_exit_are_dropped_with_diagnostics()
    {
        await using var harness = await HostClientHarness.StartAsync();
        const string terminalId = "t-undecodable";
        var session = await TerminalFixtures.StartSessionAsync(harness.Host, harness.Client, terminalId, harness.Limit.Token);

        await harness.Host.NotifyClientAsync(TerminalOperations.Output, HostWire.Payload("not terminal data"));
        await harness.Host.NotifyClientAsync(TerminalOperations.Output, RawPayload.None);
        await harness.Host.NotifyClientAsync(TerminalOperations.Exit, HostWire.Nil);
        await harness.Host.NotifyClientAsync(TerminalOperations.Exit, new TerminalExit("", 1));
        await harness.Host.SyncAsync();

        BoundRoots.AssertDiagnostic(harness.Client, "terminal.output carried no usable payload: The payload of 'terminal.output' is not a 'TerminalData'.");
        BoundRoots.AssertDiagnostic(harness.Client, "terminal.output carried no usable payload: 'terminal.output' carries no payload.");
        BoundRoots.AssertDiagnostic(harness.Client, "terminal.exit carried no usable payload: The payload of 'terminal.exit' is nil.");
        BoundRoots.AssertDiagnostic(harness.Client, "terminal.exit carried no usable payload: the message carries no terminal id.");
        Assert.False(session.Exited.IsCompleted);

        await harness.Host.NotifyClientAsync(TerminalOperations.Exit, TerminalFixtures.Exit(terminalId, 6));
        Assert.Equal(6, await session.Exited.Bounded());
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
            TerminalOperations.Start,
            async (_, ct) =>
            {
                await releaseAnswer.Task.WaitAsync(ct).ConfigureAwait(false);
                return HostReply.Of(TerminalFixtures.StartResult(terminalId));
            });

        using var limit = TestLimit.Create();
        var starting = client.StartAsync(TerminalFixtures.Request(), CancellationToken.None).AsTask();
        await host.WaitForRequestsAsync(TerminalOperations.Start, 1);

        await host.NotifyClientAsync(TerminalOperations.Output, TerminalFixtures.Output(terminalId, 0, "hello "));
        await host.NotifyClientAsync(TerminalOperations.Output, TerminalFixtures.Output(terminalId, 1, "world"));
        await host.NotifyClientAsync(TerminalOperations.Exit, TerminalFixtures.Exit(terminalId, 5));
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
            TerminalOperations.Start,
            async (_, ct) =>
            {
                await releaseAnswer.Task.WaitAsync(ct).ConfigureAwait(false);
                return HostReply.Of(TerminalFixtures.StartResult(terminalId));
            });

        var starting = client.StartAsync(TerminalFixtures.Request(), CancellationToken.None).AsTask();
        await host.WaitForRequestsAsync(TerminalOperations.Start, 1);

        await host.NotifyClientAsync(TerminalOperations.Exit, TerminalFixtures.Exit(terminalId, 3));
        await host.SyncAsync();
        releaseAnswer.TrySetResult();

        var session = await starting.Bounded();
        Assert.Equal(3, await session.Exited.Bounded());
        Assert.Empty(await TerminalFixtures.DrainAsync(session));
    }
}
