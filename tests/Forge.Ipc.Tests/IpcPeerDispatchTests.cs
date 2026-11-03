using System.Collections.Concurrent;
using System.Text.Json;
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
            await peer.RespondAsync(message.Id, Json.Parse("""{ "ok" : true }"""), ct);
        };
        await peer.StartAsync(CancellationToken.None);

        await remote.SendMessageAsync(IpcMessage.Request(42, "ask", Json.Parse("""{"q":1}""")));
        var message = await seen.Task.Bounded();
        var reply = await remote.ReceiveMessageAsync();

        Assert.Equal(42, message.Id);
        Assert.Equal("ask", message.Operation);
        Assert.Equal("""{"q":1}""", message.Arguments!.Value.GetRawText());
        Assert.Equal(42, reply.Id);
        Assert.True(reply.IsResponse);
        Assert.Equal("""{ "ok" : true }""", reply.Result!.Value.GetRawText());
    }

    [Fact]
    public async Task Ping_is_answered_with_an_empty_object_and_never_reaches_handlers()
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

        await remote.SendMessageAsync(IpcMessage.Request(7, IpcOperations.Ping));
        var pong = await remote.ReceiveMessageAsync();
        await remote.SendMessageAsync(IpcMessage.Request(8, "after"));
        await after.Task.Bounded();

        Assert.Equal(7, pong.Id);
        Assert.True(pong.IsResponse);
        Assert.Equal("{}", pong.Result!.Value.GetRawText());
        Assert.Equal(["after"], surfaced.ToArray());
    }

    [Fact]
    public async Task Unhandled_server_request_is_answered_with_an_unhandled_error()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        await peer.StartAsync(CancellationToken.None);

        await remote.SendMessageAsync(IpcMessage.Request(7, "nobody"));
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

        await remote.SendMessageAsync(IpcMessage.Request(11, "explode"));
        var reply = await remote.ReceiveMessageAsync();

        Assert.Equal(11, reply.Id);
        Assert.Equal("handler_error", reply.Error!.Code);
        Assert.Equal("boom", reply.Error.Message);
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

        await remote.SendMessageAsync(IpcMessage.Request(13, "guarded"));
        var reply = await remote.ReceiveMessageAsync();

        Assert.Equal(13, reply.Id);
        Assert.Equal("denied", reply.Error!.Code);
        Assert.Null(reply.Error.Data);
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

        await remote.SendMessageAsync(IpcMessage.Request(42, "long"));
        await entered.Task.Bounded();
        await remote.SendMessageAsync(IpcMessage.Notification(IpcOperations.Cancel, Json.Parse("""{"id":42}""")));

        await cancelled.Task.Bounded();
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
            await remote.SendMessageAsync(IpcMessage.Request(i, "block"));
        }

        await allStarted.Task.Bounded();
        await remote.SendMessageAsync(IpcMessage.Request(1000, "queued"));
        await remote.SendMessageAsync(IpcMessage.Notification(IpcOperations.Cancel, IpcJsonTypes.FromId(1000)));
        await remote.SendMessageAsync(IpcMessage.Request(2000, IpcOperations.Ping));
        await remote.ReceiveUntilAsync(message => message.Id == 2000);

        release.TrySetResult();

        Assert.True(await queuedSawCancel.Task.Bounded());
    }

    [Fact]
    public async Task Remote_notification_is_surfaced_to_subscribers_with_its_argument_bytes()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var received = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.NotificationReceived += message => received.TrySetResult(message);
        await peer.StartAsync(CancellationToken.None);

        await remote.SendMessageAsync(IpcMessage.Notification("tick", Json.Parse("""{ "n" : 1 }""")));
        var message = await received.Task.Bounded();

        Assert.True(message.IsNotification);
        Assert.Equal(0, message.Id);
        Assert.Equal("tick", message.Operation);
        Assert.Equal("""{ "n" : 1 }""", message.Arguments!.Value.GetRawText());
    }

    [Fact]
    public async Task Send_notification_puts_a_notification_frame_on_the_wire()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        await peer.SendNotificationAsync("tick", Json.Parse("""{ "n" : 2 }"""), CancellationToken.None);
        var message = await remote.ReceiveMessageAsync();

        Assert.True(message.IsNotification);
        Assert.Equal("tick", message.Operation);
        Assert.Equal("""{ "n" : 2 }""", message.Arguments!.Value.GetRawText());
    }

    [Fact]
    public async Task Response_with_a_foreign_version_faults_its_request_once_and_the_connection_stays_usable()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var mismatches = new ConcurrentQueue<IpcVersionMismatchException>();
        peer.VersionMismatch += mismatches.Enqueue;

        var call = peer.SendRequestAsync("op", null, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendMessageAsync(IpcMessage.ResultMessage(request.Id, Json.Parse("{}")) with { Version = 2 });

        var ex = await Assert.ThrowsAsync<IpcVersionMismatchException>(() => call.Bounded());
        Assert.Equal(2, ex.Received);
        Assert.Equal(1, ex.Expected);
        var raised = Assert.Single(mismatches);
        Assert.Equal(2, raised.Received);

        var second = peer.SendRequestAsync("op", null, CancellationToken.None);
        var next = await remote.ReceiveMessageAsync();
        await remote.SendMessageAsync(IpcMessage.ResultMessage(next.Id, Json.Parse("""{"ok":true}""")));
        Assert.Equal("""{"ok":true}""", (await second.Bounded()).Result!.Value.GetRawText());
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

        await remote.SendMessageAsync(IpcMessage.Request(12, "work") with { Version = 3 });
        var reply = await remote.ReceiveMessageAsync();
        await TestAwait.UntilAsync(() => !mismatches.IsEmpty, "the version mismatch event");
        await remote.SendMessageAsync(IpcMessage.Request(13, "after"));
        await after.Task.Bounded();

        Assert.Equal(12, reply.Id);
        Assert.Equal("version_mismatch", reply.Error!.Code);
        Assert.Equal(["after"], handled.ToArray());
        Assert.Equal(3, Assert.Single(mismatches).Received);
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

        await remote.SendBodyAsync("not json");
        await remote.SendBodyAsync("null");
        await remote.SendBodyAsync("""{"v":1,"id":3,"op":"ok"}""");
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

        await remote.SendBodyAsync(string.Empty);
        await TestAwait.UntilAsync(() => errors.Count == 1, "the empty-frame protocol error");
        await remote.SendBodyAsync("""{"v":1,"id":5,"op":"ok"}""");
        var message = await ok.Task.Bounded();

        Assert.Equal("ok", message.Operation);
        Assert.Equal(5, message.Id);
        Assert.Contains("zero-length", Assert.Single(errors).Message, StringComparison.Ordinal);
        Assert.False(peer.IsClosed);
    }
}
