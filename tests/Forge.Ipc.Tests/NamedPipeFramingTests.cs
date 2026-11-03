using System.Collections.Concurrent;
using System.IO.Pipes;
using Forge.Ipc.Tests.Support;
using Xunit;

namespace Forge.Ipc.Tests;

public sealed class NamedPipeFramingTests
{
    private const int SmallLimit = 1024;
    private const int SmallResynchronizeCap = 4096;

    [Fact]
    public async Task Oversized_write_is_rejected_before_any_bytes_are_sent()
    {
        var name = TestPipes.NewName();
        var options = new IpcPipeOptions { MaxFrameBytes = SmallLimit };
        var serverTask = IpcPipe.CreateServer(name, options);
        await using var client = await IpcPipe.CreateClient(name, options).Bounded();
        await using var server = await serverTask.Bounded();
        var sink = new FrameSink(server);

        var oversized = FrameCodec.Default.Serialize(IpcMessage.Notification("big", Json.Parse($"\"{TestFrames.Padded(2000)}\"")));
        await Assert.ThrowsAsync<ArgumentException>(async () => await client.SendAsync(oversized, CancellationToken.None));
        Assert.True(client.IsConnected);

        await client.SendAsync(FrameCodec.Default.Serialize(IpcMessage.Notification("small")), CancellationToken.None);
        var first = await sink.NextMessageAsync();

        Assert.Equal("small", first.Operation);
    }

    [Fact]
    public async Task Oversized_read_is_skipped_and_the_next_frame_is_delivered()
    {
        var name = TestPipes.NewName();
        var options = new IpcPipeOptions { MaxFrameBytes = SmallLimit, MaxResynchronizeBytes = SmallResynchronizeCap };
        var (server, raw) = await AcceptRawAsync(name, options);
        await using var _ = server;
        using var __ = raw;
        var sink = new FrameSink(server);
        var protocolErrors = new ConcurrentQueue<IpcProtocolException>();
        server.ProtocolError += protocolErrors.Enqueue;

        await WriteAsync(raw, TestFrames.Header(2000));
        await WriteAsync(raw, TestFrames.Filler(2000));
        await WriteAsync(raw, FrameCodec.Default.Serialize(IpcMessage.Notification("after")));
        var next = await sink.NextMessageAsync();

        Assert.Equal("after", next.Operation);
        Assert.Single(protocolErrors);
        Assert.True(server.IsConnected);
    }

    [Fact]
    public async Task Zero_length_frame_raises_a_protocol_error_and_the_next_frame_is_delivered()
    {
        var name = TestPipes.NewName();
        var (server, raw) = await AcceptRawAsync(name, new IpcPipeOptions());
        await using var _ = server;
        using var __ = raw;
        var sink = new FrameSink(server);
        var protocolErrors = new ConcurrentQueue<IpcProtocolException>();
        server.ProtocolError += protocolErrors.Enqueue;

        await WriteAsync(raw, TestFrames.Header(0));
        await WriteAsync(raw, FrameCodec.Default.Serialize(IpcMessage.Notification("after")));
        var next = await sink.NextMessageAsync();

        Assert.Equal("after", next.Operation);
        Assert.Contains("zero-length", Assert.Single(protocolErrors).Message, StringComparison.Ordinal);
        Assert.True(server.IsConnected);
    }

    [Fact]
    public async Task Peer_surfaces_a_malformed_frame_as_a_protocol_error_and_keeps_dispatching()
    {
        var name = TestPipes.NewName();
        var (server, raw) = await AcceptRawAsync(name, new IpcPipeOptions());
        await using var _ = server;
        using var __ = raw;
        await using var peer = Peers.Create(server);
        var errors = new ConcurrentQueue<IpcProtocolException>();
        peer.ProtocolError += errors.Enqueue;
        var notifications = new ConcurrentQueue<string>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.NotificationReceived += message =>
        {
            notifications.Enqueue(message.Operation);
            delivered.TrySetResult();
        };
        await peer.StartAsync(CancellationToken.None);

        await WriteAsync(raw, TestFrames.Prefix(System.Text.Encoding.UTF8.GetBytes("not json")));
        await TestAwait.UntilAsync(() => errors.Count == 1, "the malformed-frame protocol error");
        await WriteAsync(raw, FrameCodec.Default.Serialize(IpcMessage.Notification("after")));
        await delivered.Task.Bounded();

        Assert.Contains("valid message", Assert.Single(errors).Message, StringComparison.Ordinal);
        Assert.Equal(["after"], notifications.ToArray());
        Assert.False(peer.IsClosed);
    }

    [Fact]
    public async Task Frame_declaring_more_than_the_resynchronize_cap_closes_the_connection()
    {
        var name = TestPipes.NewName();
        var options = new IpcPipeOptions { MaxFrameBytes = SmallLimit, MaxResynchronizeBytes = SmallResynchronizeCap };
        var (server, raw) = await AcceptRawAsync(name, options);
        await using var _ = server;
        using var __ = raw;
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Closed += cause => closed.TrySetResult(cause);

        await WriteAsync(raw, TestFrames.Header(SmallResynchronizeCap + 1));

        Assert.IsType<IpcProtocolException>(await closed.Task.Bounded());
        Assert.False(server.IsConnected);
    }

    [Fact]
    public async Task Frame_declaring_exactly_the_resynchronize_cap_is_skipped()
    {
        var name = TestPipes.NewName();
        var options = new IpcPipeOptions { MaxFrameBytes = SmallLimit, MaxResynchronizeBytes = SmallResynchronizeCap };
        var (server, raw) = await AcceptRawAsync(name, options);
        await using var _ = server;
        using var __ = raw;
        var sink = new FrameSink(server);

        await WriteAsync(raw, TestFrames.Header(SmallResynchronizeCap));
        await WriteAsync(raw, TestFrames.Filler(SmallResynchronizeCap));
        await WriteAsync(raw, FrameCodec.Default.Serialize(IpcMessage.Notification("after")));
        var next = await sink.NextMessageAsync();

        Assert.Equal("after", next.Operation);
        Assert.True(server.IsConnected);
    }

    [Fact]
    public async Task Negative_length_header_closes_the_connection()
    {
        var name = TestPipes.NewName();
        var (server, raw) = await AcceptRawAsync(name, new IpcPipeOptions());
        await using var _ = server;
        using var __ = raw;
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Closed += cause => closed.TrySetResult(cause);

        await WriteAsync(raw, TestFrames.Header(-1));

        Assert.IsType<IpcProtocolException>(await closed.Task.Bounded());
    }

    [Fact]
    public async Task Frame_queue_byte_budget_back_pressures_without_losing_frames()
    {
        var name = TestPipes.NewName();
        var options = new IpcPipeOptions { MaxFrameBytes = SmallLimit, FrameQueueBytes = SmallLimit };
        var (server, raw) = await AcceptRawAsync(name, options);
        await using var _ = server;
        using var __ = raw;
        var events = new ConcurrentQueue<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        server.FrameReceived += frame =>
        {
            var message = FrameCodec.Default.Deserialize(frame);
            events.Enqueue(message.Operation);
            if (message.Operation == "block")
            {
                entered.TrySetResult();
                release.Wait(TestTimeouts.Limit);
            }
        };
        server.Closed += _ => events.Enqueue("closed");

        await WriteAsync(raw, FrameCodec.Default.Serialize(IpcMessage.Notification("block", Json.Parse($"\"{TestFrames.Padded(300)}\""))));
        await entered.Task.Bounded();
        for (var i = 1; i <= 4; i++)
        {
            await WriteAsync(raw, FrameCodec.Default.Serialize(IpcMessage.Notification($"f{i}", Json.Parse($"\"{TestFrames.Padded(300)}\""))));
        }

        release.Set();
        await TestAwait.UntilAsync(() => events.Count >= 5, "all frames to be delivered");

        Assert.Equal(["block", "f1", "f2", "f3", "f4"], events.ToArray());
        Assert.True(server.IsConnected);
    }

    [Fact]
    public async Task Frames_beyond_queue_capacity_back_pressure_instead_of_closing_the_connection()
    {
        var name = TestPipes.NewName();
        var options = new IpcPipeOptions { FrameQueueCapacity = 2 };
        var (server, raw) = await AcceptRawAsync(name, options);
        await using var _ = server;
        using var __ = raw;
        var events = new ConcurrentQueue<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        server.FrameReceived += frame =>
        {
            var message = FrameCodec.Default.Deserialize(frame);
            events.Enqueue(message.Operation);
            if (message.Operation == "block")
            {
                entered.TrySetResult();
                release.Wait(TestTimeouts.Limit);
            }
        };
        server.Closed += cause => closed.TrySetResult(cause);

        await WriteAsync(raw, FrameCodec.Default.Serialize(IpcMessage.Notification("block")));
        await entered.Task.Bounded();
        for (var i = 1; i <= 8; i++)
        {
            try
            {
                await WriteAsync(raw, FrameCodec.Default.Serialize(IpcMessage.Notification($"f{i}")));
            }
            catch (IOException)
            {
                break;
            }
        }

        await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        release.Set();
        await Task.WhenAny(closed.Task, TestAwait.UntilAsync(() => events.Count >= 9, "all frames to be delivered"));

        Assert.False(closed.Task.IsCompleted, "the connection closed when the queue reached capacity");
        Assert.Equal(["block", "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8"], events.ToArray());
    }

    [Fact]
    public async Task Frames_queued_before_disconnect_are_delivered_before_closed()
    {
        var name = TestPipes.NewName();
        var (server, raw) = await AcceptRawAsync(name, new IpcPipeOptions());
        await using var _ = server;
        var events = new ConcurrentQueue<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        server.FrameReceived += frame =>
        {
            var message = FrameCodec.Default.Deserialize(frame);
            events.Enqueue(message.Operation);
            if (message.Operation == "block")
            {
                entered.TrySetResult();
                release.Wait(TestTimeouts.Limit);
            }
        };
        server.Closed += _ =>
        {
            events.Enqueue("closed");
            closed.TrySetResult();
        };

        await WriteAsync(raw, FrameCodec.Default.Serialize(IpcMessage.Notification("block")));
        await WriteAsync(raw, FrameCodec.Default.Serialize(IpcMessage.Notification("after")));
        raw.Dispose();
        await entered.Task.Bounded();
        await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        release.Set();
        await TestAwait.UntilAsync(() => events.Count >= 3, "the queued frame and the close");

        Assert.Equal(["block", "after", "closed"], events.ToArray());
    }

    [Fact]
    public async Task Send_blocked_by_a_stalled_pipe_honours_the_request_deadline()
    {
        var name = TestPipes.NewName();
        const int bodyBytes = 3 * 1024 * 1024;
        var options = new IpcPipeOptions { MaxFrameBytes = 4 * 1024 * 1024, FrameQueueBytes = 4 * 1024 * 1024 };
        var serverTask = IpcPipe.CreateServer(name, options);
        await using var client = await IpcPipe.CreateClient(name, options).Bounded();
        await using var server = await serverTask.Bounded();
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.FrameReceived += frame =>
        {
            if (FrameCodec.Default.Deserialize(frame).Operation == "A")
            {
                entered.TrySetResult();
                release.Wait(TestTimeouts.Limit);
            }
        };
        await using var clientPeer = Peers.Create(client, TestTimeouts.Short);
        var big = Json.Parse($"\"{TestFrames.Padded(bodyBytes)}\"");

        try
        {
            await client.SendAsync(FrameCodec.Default.Serialize(IpcMessage.Notification("A", big)), CancellationToken.None).AsTask().Bounded();
            await entered.Task.Bounded();
            await client.SendAsync(FrameCodec.Default.Serialize(IpcMessage.Notification("B", big)), CancellationToken.None).AsTask().Bounded();

            var call = clientPeer.SendRequestAsync("R", big, CancellationToken.None);
            await Assert.ThrowsAsync<IpcTimeoutException>(() => call.Bounded());
        }
        finally
        {
            release.Set();
        }
    }

    private static async Task<(NamedPipeServerTransport Server, NamedPipeClientStream Raw)> AcceptRawAsync(string name, IpcPipeOptions options)
    {
        var serverTask = IpcPipe.CreateServer(name, options);
        var raw = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(10_000);
        var server = await serverTask.Bounded();
        return (server, raw);
    }

    private static async Task WriteAsync(NamedPipeClientStream raw, byte[] bytes)
    {
        await raw.WriteAsync(bytes);
        await raw.FlushAsync();
    }

    private static async Task WriteAsync(NamedPipeClientStream raw, ReadOnlyMemory<byte> bytes)
    {
        await raw.WriteAsync(bytes);
        await raw.FlushAsync();
    }
}
