using System.Collections.Concurrent;
using Forge.Ipc.Tests.Support;
using Xunit;

namespace Forge.Ipc.Tests;

public sealed class IpcPeerDispatchTests
{
    [Fact]
    public async Task Server_request_is_surfaced_with_its_id_and_respond_preserves_it()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var seen = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.RequestReceived += async (message, ct) =>
        {
            seen.TrySetResult(message);
            await peer.RespondAsync(message.Id, new EchoResult("ok", 1, true), ct);
        };
        await peer.StartAsync(CancellationToken.None);

        await remote.SendFrameAsync(TestFrames.Request(42, "ask", new EchoArgs("q", 1)));
        var message = await seen.Task.Bounded();
        var reply = await remote.ReceiveMessageAsync();

        Assert.Equal(42, message.Id);
        Assert.Equal("ask", message.Operation);
        Assert.Equal(new EchoArgs("q", 1), message.Read<EchoArgs>());
        Assert.Equal(42, reply.Id);
        Assert.True(reply.IsResponse);
        Assert.Equal(new EchoResult("ok", 1, true), reply.Read<EchoResult>());
    }

    [Fact]
    public async Task Ping_is_answered_with_ipc_empty_and_never_reaches_handlers()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var surfaced = new ConcurrentQueue<string>();
        var after = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.RequestReceived += (message, _) =>
        {
            surfaced.Enqueue(message.Operation);
            if (message.Operation == "after")
            {
                after.TrySetResult();
            }

            return Task.CompletedTask;
        };
        await peer.StartAsync(CancellationToken.None);

        await remote.SendFrameAsync(TestFrames.Request(7, IpcOperations.Ping, IpcEmpty.Instance));
        var pong = await remote.ReceiveMessageAsync();
        await remote.SendFrameAsync(TestFrames.Request(8, "after", IpcEmpty.Instance));
        await after.Task.Bounded();

        Assert.Equal(7, pong.Id);
        Assert.True(pong.IsResponse);
        Assert.Equal(IpcEmpty.Instance, pong.Read<IpcEmpty>());
        Assert.Equal(["after"], surfaced.ToArray());
    }

    [Fact]
    public async Task Ping_without_a_payload_is_still_answered()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        await peer.StartAsync(CancellationToken.None);

        await remote.SendFrameAsync(TestFrames.HeaderOnly(IpcHeader.Request(7, IpcOperations.Ping)));
        var pong = await remote.ReceiveMessageAsync();

        Assert.Equal(7, pong.Id);
        Assert.Equal(IpcEmpty.Instance, pong.Read<IpcEmpty>());
    }

    [Fact]
    public async Task Local_ping_request_returns_ipc_empty_from_the_remote_peer()
    {
        var (a, b) = await LoopbackTransport.ConnectAsync();
        await using var left = Peers.Create(a);
        await using var right = Peers.Create(b);
        await left.StartAsync(CancellationToken.None);
        await right.StartAsync(CancellationToken.None);

        var pong = await left.RequestAsync<IpcEmpty, IpcEmpty>(IpcOperations.Ping, IpcEmpty.Instance, CancellationToken.None).Bounded();

        Assert.Equal(IpcEmpty.Instance, pong);
    }

    [Fact]
    public async Task Unhandled_server_request_is_answered_with_an_unhandled_error()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        await peer.StartAsync(CancellationToken.None);

        await remote.SendFrameAsync(TestFrames.Request(7, "nobody", IpcEmpty.Instance));
        var reply = await remote.ReceiveMessageAsync();

        Assert.Equal(7, reply.Id);
        Assert.Equal("unhandled", reply.Error!.Code);
        Assert.Contains("'nobody'", reply.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handler_exception_is_reported_as_handler_error_with_its_message()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        peer.RequestReceived += (_, _) => throw new InvalidOperationException("boom");
        await peer.StartAsync(CancellationToken.None);

        await remote.SendFrameAsync(TestFrames.Request(11, "explode", IpcEmpty.Instance));
        var reply = await remote.ReceiveMessageAsync();

        Assert.Equal(11, reply.Id);
        Assert.Equal("handler_error", reply.Error!.Code);
        Assert.Equal("boom", reply.Error.Message);
    }

    [Fact]
    public async Task Request_with_a_payload_of_the_wrong_type_surfaces_as_handler_error_and_the_peer_survives()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        peer.RequestReceived += async (message, ct) =>
        {
            var args = message.Read<EchoArgs>();
            await peer.RespondAsync(message.Id, new EchoResult(args.Text, args.N, true), ct);
        };
        await peer.StartAsync(CancellationToken.None);

        await remote.SendFrameAsync(TestFrames.Request(1, "echo", "wrong type"));
        var bad = await remote.ReceiveMessageAsync();
        await remote.SendFrameAsync(TestFrames.Request(2, "echo", new EchoArgs("fine", 2)));
        var good = await remote.ReceiveMessageAsync();

        Assert.Equal("handler_error", bad.Error!.Code);
        Assert.Contains("EchoArgs", bad.Error.Message, StringComparison.Ordinal);
        Assert.Equal(new EchoResult("fine", 2, true), good.Read<EchoResult>());
        Assert.False(peer.IsClosed);
    }

    [Fact]
    public async Task Handler_can_reply_with_a_typed_error_that_carries_no_data()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        peer.RequestReceived += async (message, ct) =>
        {
            await peer.RespondErrorAsync(message.Id, new IpcError("denied", "not allowed"), ct);
        };
        await peer.StartAsync(CancellationToken.None);

        await remote.SendFrameAsync(TestFrames.Request(13, "guarded", IpcEmpty.Instance));
        var reply = await remote.ReceiveMessageAsync();

        Assert.Equal(13, reply.Id);
        Assert.Equal("denied", reply.Error!.Code);
        Assert.Null(reply.Error.Data);
        Assert.False(reply.HasPayload);
    }

    [Fact]
    public async Task Cancel_notification_cancels_the_token_of_a_running_server_handler()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.RequestReceived += async (_, ct) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        };
        await peer.StartAsync(CancellationToken.None);

        await remote.SendFrameAsync(TestFrames.Request(42, "long", IpcEmpty.Instance));
        await entered.Task.Bounded();
        await remote.SendFrameAsync(TestFrames.Notification(IpcOperations.Cancel, 42L));

        await cancelled.Task.Bounded();
    }

    [Fact]
    public async Task Cancel_with_a_malformed_payload_is_ignored_and_the_handler_keeps_running()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawCancel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.RequestReceived += async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task;
            sawCancel.TrySetResult(ct.IsCancellationRequested);
        };
        await peer.StartAsync(CancellationToken.None);

        await remote.SendFrameAsync(TestFrames.Request(42, "long", IpcEmpty.Instance));
        await entered.Task.Bounded();
        await remote.SendFrameAsync(TestFrames.Notification(IpcOperations.Cancel, "not an id"));
        await remote.SendFrameAsync(TestFrames.Request(43, IpcOperations.Ping, IpcEmpty.Instance));
        await remote.ReceiveUntilAsync(message => message.Id == 43);
        release.TrySetResult();

        Assert.False(await sawCancel.Task.Bounded());
        Assert.False(peer.IsClosed);
    }

    [Fact]
    public async Task Cancel_for_a_request_queued_behind_the_handler_gate_reaches_its_handler()
    {
        const int gate = 64;
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedSawCancel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.RequestReceived += async (message, ct) =>
        {
            if (message.Operation == "queued")
            {
                queuedSawCancel.TrySetResult(ct.IsCancellationRequested);
                return;
            }

            if (Interlocked.Increment(ref started) == gate)
            {
                allStarted.TrySetResult();
            }

            await release.Task;
        };
        await peer.StartAsync(CancellationToken.None);

        for (var i = 1; i <= gate; i++)
        {
            await remote.SendFrameAsync(TestFrames.Request(i, "block", IpcEmpty.Instance));
        }

        await allStarted.Task.Bounded();
        await remote.SendFrameAsync(TestFrames.Request(1000, "queued", IpcEmpty.Instance));
        await remote.SendFrameAsync(TestFrames.Notification(IpcOperations.Cancel, 1000L));
        await remote.SendFrameAsync(TestFrames.Request(2000, IpcOperations.Ping, IpcEmpty.Instance));
        await remote.ReceiveUntilAsync(message => message.Id == 2000);

        release.TrySetResult();

        Assert.True(await queuedSawCancel.Task.Bounded());
    }

    [Fact]
    public async Task Remote_notification_is_surfaced_to_subscribers_with_its_typed_payload()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var received = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.NotificationReceived += message => received.TrySetResult(message);
        await peer.StartAsync(CancellationToken.None);

        await remote.SendFrameAsync(TestFrames.Notification("tick", new EchoArgs("t", 1)));
        var message = await received.Task.Bounded();

        Assert.True(message.IsNotification);
        Assert.Equal(0, message.Id);
        Assert.Equal("tick", message.Operation);
        Assert.Equal(new EchoArgs("t", 1), message.Read<EchoArgs>());
    }

    [Fact]
    public async Task Cancel_notification_is_also_surfaced_to_notification_subscribers()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var received = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.NotificationReceived += message => received.TrySetResult(message);
        await peer.StartAsync(CancellationToken.None);

        await remote.SendFrameAsync(TestFrames.Notification(IpcOperations.Cancel, 77L));
        var message = await received.Task.Bounded();

        Assert.Equal(IpcOperations.Cancel, message.Operation);
        Assert.Equal(77L, message.Read<long>());
    }

    [Fact]
    public async Task Notify_puts_a_notification_frame_on_the_wire()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        await peer.NotifyAsync("tick", new EchoArgs("n", 2), CancellationToken.None);
        var message = await remote.ReceiveMessageAsync();

        Assert.True(message.IsNotification);
        Assert.Equal("tick", message.Operation);
        Assert.Equal(IpcProtocol.Version, message.Version);
        Assert.Equal(new EchoArgs("n", 2), message.Read<EchoArgs>());
    }

    [Fact]
    public async Task Throwing_notification_subscriber_does_not_kill_the_peer()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.NotificationReceived += message =>
        {
            if (message.Operation == "boom")
            {
                throw new InvalidOperationException("subscriber failed");
            }

            second.TrySetResult();
        };
        await peer.StartAsync(CancellationToken.None);

        await remote.SendFrameAsync(TestFrames.Notification("boom", IpcEmpty.Instance));
        await remote.SendFrameAsync(TestFrames.Notification("fine", IpcEmpty.Instance));
        await second.Task.Bounded();

        Assert.False(peer.IsClosed);
    }

    [Fact]
    public async Task Response_with_a_foreign_version_faults_its_request_once_and_the_connection_stays_usable()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var mismatches = new ConcurrentQueue<IpcVersionMismatchException>();
        peer.VersionMismatch += mismatches.Enqueue;

        var call = peer.RequestAsync("op", IpcEmpty.Instance, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        var foreign = IpcHeader.Result(request.Id) with { Version = IpcProtocol.Version + 1 };
        await remote.SendFrameAsync(FrameCodec.Default.EncodeToArray(foreign, IpcEmpty.Instance));

        var ex = await Assert.ThrowsAsync<IpcVersionMismatchException>(() => call.Bounded());
        Assert.Equal(IpcProtocol.Version + 1, ex.Received);
        Assert.Equal(IpcProtocol.Version, ex.Expected);
        Assert.Equal(IpcProtocol.Version + 1, Assert.Single(mismatches).Received);

        var second = peer.RequestAsync<IpcEmpty, EchoResult>("op", IpcEmpty.Instance, CancellationToken.None);
        var next = await remote.ReceiveMessageAsync();
        await remote.SendFrameAsync(TestFrames.Result(next.Id, new EchoResult("x", 1, true)));
        Assert.True((await second.Bounded()).Ok);
        Assert.Single(mismatches);
    }

    [Fact]
    public async Task Request_with_a_foreign_version_is_refused_with_version_mismatch_and_not_dispatched()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var mismatches = new ConcurrentQueue<IpcVersionMismatchException>();
        peer.VersionMismatch += mismatches.Enqueue;
        var handled = new ConcurrentQueue<string>();
        var after = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.RequestReceived += (message, _) =>
        {
            handled.Enqueue(message.Operation);
            if (message.Operation == "after")
            {
                after.TrySetResult();
            }

            return Task.CompletedTask;
        };
        await peer.StartAsync(CancellationToken.None);

        var foreign = IpcHeader.Request(12, "work") with { Version = 1 };
        await remote.SendFrameAsync(FrameCodec.Default.EncodeToArray(foreign, IpcEmpty.Instance));
        var reply = await remote.ReceiveMessageAsync();
        await TestAwait.UntilAsync(() => !mismatches.IsEmpty, "the version mismatch event");
        await remote.SendFrameAsync(TestFrames.Request(13, "after", IpcEmpty.Instance));
        await after.Task.Bounded();

        Assert.Equal(12, reply.Id);
        Assert.Equal("version_mismatch", reply.Error!.Code);
        Assert.Equal(["after"], handled.ToArray());
        Assert.Equal(1, Assert.Single(mismatches).Received);
    }

    [Fact]
    public async Task Notification_with_a_foreign_version_is_dropped_without_a_reply()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var mismatches = new ConcurrentQueue<IpcVersionMismatchException>();
        peer.VersionMismatch += mismatches.Enqueue;
        var notifications = new ConcurrentQueue<string>();
        var after = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.NotificationReceived += message =>
        {
            notifications.Enqueue(message.Operation);
            after.TrySetResult();
        };
        await peer.StartAsync(CancellationToken.None);

        var foreign = IpcHeader.Notification("old") with { Version = 1 };
        await remote.SendFrameAsync(FrameCodec.Default.EncodeToArray(foreign, IpcEmpty.Instance));
        await remote.SendFrameAsync(TestFrames.Notification("new", IpcEmpty.Instance));
        await after.Task.Bounded();

        Assert.Equal(["new"], notifications.ToArray());
        Assert.Single(mismatches);
    }

    [Fact]
    public async Task Malformed_frames_raise_a_protocol_error_and_the_next_valid_request_is_dispatched()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var errors = new ConcurrentQueue<IpcProtocolException>();
        peer.ProtocolError += errors.Enqueue;
        var ok = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.RequestReceived += (message, _) =>
        {
            ok.TrySetResult(message);
            return Task.CompletedTask;
        };
        await peer.StartAsync(CancellationToken.None);

        var truncated = TestFrames.HeaderOnly(IpcHeader.Request(9, "cut"))[TestFrames.HeaderBytes..^3];
        await remote.SendBodyAsync([0xC1, 0xC1, 0xC1]);
        await remote.SendBodyAsync(truncated);
        await remote.SendFrameAsync(TestFrames.Request(3, "ok", IpcEmpty.Instance));
        var message = await ok.Task.Bounded();

        Assert.Equal("ok", message.Operation);
        Assert.Equal(3, message.Id);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.IsType<IpcProtocolException>(error));
        Assert.False(peer.IsClosed);
    }

    [Fact]
    public async Task Empty_frame_raises_a_protocol_error_and_the_next_valid_request_is_dispatched()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var errors = new ConcurrentQueue<IpcProtocolException>();
        peer.ProtocolError += errors.Enqueue;
        var ok = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.RequestReceived += (message, _) =>
        {
            ok.TrySetResult(message);
            return Task.CompletedTask;
        };
        await peer.StartAsync(CancellationToken.None);

        await remote.SendBodyAsync([]);
        await TestAwait.UntilAsync(() => errors.Count == 1, "the empty-frame protocol error");
        await remote.SendFrameAsync(TestFrames.Request(5, "ok", IpcEmpty.Instance));
        var message = await ok.Task.Bounded();

        Assert.Equal("ok", message.Operation);
        Assert.Equal(5, message.Id);
        Assert.Contains("zero-length", Assert.Single(errors).Message, StringComparison.Ordinal);
        Assert.False(peer.IsClosed);
    }
}
