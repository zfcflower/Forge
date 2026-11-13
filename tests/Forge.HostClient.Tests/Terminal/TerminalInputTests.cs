using Forge.Abstractions;
using Forge.Ipc;
using Xunit;

namespace Forge.HostClient.Tests.Terminal;

/// <summary>terminal.input 的分片与保序（任务条目 5）：大于 64 KiB 的写入拆成多条通知，
/// 每条解码后不超过 64 KiB，按序拼接与原文完全一致；并发写入不交错。</summary>
public sealed class TerminalInputTests
{
    private const int MaxFrameBytes = 64 * 1024;

    [Fact]
    public async Task Write_over_64KiB_splits_into_bounded_frames_that_concatenate_exactly()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var session = await TerminalFixtures.StartSessionAsync(host, client, "t-input", limit.Token);

        var original = new byte[200_000];
        for (var index = 0; index < original.Length; index++)
        {
            original[index] = (byte)(index % 251);
        }

        await session.WriteAsync(original, limit.Token);

        var frames = await host.WaitForNotificationsAsync(TerminalOps.Input, 4);
        Assert.Equal(4, frames.Count);
        var decoded = frames.SelectMany(TerminalFixtures.DecodeData).ToArray();
        foreach (var frame in frames)
        {
            var payload = TerminalFixtures.DecodeData(frame);
            Assert.True(payload.Length <= MaxFrameBytes, $"frame of {payload.Length} bytes exceeds 64 KiB");
            Assert.Equal("t-input", frame.Arguments!.Value.GetProperty("terminalId").GetString());
        }

        Assert.Equal(original, decoded);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Write_exactly_64KiB_sends_a_single_frame()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var session = await TerminalFixtures.StartSessionAsync(host, client, "t-exact", limit.Token);

        var original = new byte[MaxFrameBytes];
        new Random(42).NextBytes(original);
        await session.WriteAsync(original, limit.Token);

        var frames = await host.WaitForNotificationsAsync(TerminalOps.Input, 1);
        Assert.Equal(original, TerminalFixtures.DecodeData(Assert.Single(frames)));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_writes_keep_each_notification_homogeneous_and_ordered()
    {
        // 同一终端的并发 WriteAsync 经 writeGate 串行：Host 侧按到达序拼接只能是 A+B 或 B+A，
        // 且单条通知绝不混入另一串的字节。无门控时帧级交错（A1,B1,A2,B2）会让拼接落到两者之外。
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var session = await TerminalFixtures.StartSessionAsync(host, client, "t-race", limit.Token);

        var first = Enumerable.Repeat((byte)0x41, 70_000).ToArray();
        var second = Enumerable.Repeat((byte)0x42, 70_000).ToArray();

        await Task.WhenAll(
            session.WriteAsync(first, limit.Token).AsTask(),
            session.WriteAsync(second, limit.Token).AsTask());

        var frames = await host.WaitForNotificationsAsync(TerminalOps.Input, 4);
        Assert.Equal(4, frames.Count);
        var payloads = frames.Select(TerminalFixtures.DecodeData).ToArray();
        foreach (var payload in payloads)
        {
            // 单条通知内部同质：要么全 0x41，要么全 0x42。
            Assert.True(
                payload.All(b => b == 0x41) || payload.All(b => b == 0x42),
                "one terminal.input frame mixed bytes of both writers");
        }

        var concatenated = payloads.SelectMany(payload => payload).ToArray();
        var candidateA = first.Concat(second).ToArray();
        var candidateB = second.Concat(first).ToArray();
        Assert.True(
            concatenated.SequenceEqual(candidateA) || concatenated.SequenceEqual(candidateB),
            "concurrent writes interleaved at frame granularity");
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Write_empty_input_sends_nothing()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var session = await TerminalFixtures.StartSessionAsync(host, client, "t-empty", limit.Token);

        await session.WriteAsync(ReadOnlyMemory<byte>.Empty, limit.Token);

        Assert.Empty(host.Notifications(TerminalOps.Input));
        await session.DisposeAsync();
    }
}
