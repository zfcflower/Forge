using Forge.Ipc.Tests.Support;
using Xunit;

namespace Forge.Ipc.Tests;

public sealed class IpcPeerRequestTests
{
    [Fact]
    public async Task Response_completes_the_request_with_the_matching_payload()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        await peer.StartAsync(CancellationToken.None);

        var call = peer.RequestAsync("echo", new EchoArgs("hé", 1), CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendFrameAsync(TestFrames.Result(request.Id, new EchoResult("hé", 1, true)));
        var response = await call.Bounded();

        Assert.Equal("echo", request.Operation);
        Assert.Equal(new EchoArgs("hé", 1), request.Read<EchoArgs>());
        Assert.Equal(request.Id, response.Id);
        Assert.True(response.IsResponse);
        Assert.Equal(new EchoResult("hé", 1, true), response.Read<EchoResult>());
    }

    [Fact]
    public async Task Outbound_request_puts_header_and_typed_arguments_on_the_wire()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var call = peer.RequestAsync("echo", new EchoArgs("a", 5), CancellationToken.None);
        var body = await remote.ReceiveBodyAsync();
        var decoded = FrameCodec.Default.Decode(body);

        Assert.Equal("echo", decoded.Operation);
        Assert.True(decoded.Id > 0);
        Assert.Equal(IpcProtocol.Version, decoded.Version);
        Assert.False(decoded.IsResponse);
        Assert.Equal(new EchoArgs("a", 5), decoded.Read<EchoArgs>());

        await peer.StopAsync(CancellationToken.None);
        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => call.Bounded());
    }

    [Fact]
    public async Task Typed_request_returns_the_decoded_result()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var call = peer.RequestAsync<EchoArgs, EchoResult>("echo", new EchoArgs("x", 3), CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendFrameAsync(TestFrames.Result(request.Id, new EchoResult("x", 3, true)));

        Assert.Equal(new EchoResult("x", 3, true), await call.Bounded());
    }

    [Fact]
    public async Task Typed_request_decodes_primitive_results()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var name = peer.RequestAsync<IpcEmpty, string>("name", IpcEmpty.Instance, CancellationToken.None);
        var nameRequest = await remote.ReceiveMessageAsync();
        await remote.SendFrameAsync(TestFrames.Result(nameRequest.Id, "hi"));
        var count = peer.RequestAsync<IpcEmpty, int>("count", IpcEmpty.Instance, CancellationToken.None);
        var countRequest = await remote.ReceiveMessageAsync();
        await remote.SendFrameAsync(TestFrames.Result(countRequest.Id, 42));

        Assert.Equal("hi", await name.Bounded());
        Assert.Equal(42, await count.Bounded());
    }

    [Fact]
    public async Task Typed_request_with_a_result_of_the_wrong_type_throws_protocol_exception()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var call = peer.RequestAsync<IpcEmpty, EchoResult>("shape", IpcEmpty.Instance, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendFrameAsync(TestFrames.Result(request.Id, "not a record"));

        var ex = await Assert.ThrowsAsync<IpcProtocolException>(() => call.Bounded());
        Assert.Contains("'shape'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Typed_request_with_a_nil_result_throws_protocol_exception()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var call = peer.RequestAsync<IpcEmpty, EchoResult>("void", IpcEmpty.Instance, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendFrameAsync(FrameCodec.Default.EncodeToArray<EchoResult?>(IpcHeader.Result(request.Id), null));

        await Assert.ThrowsAsync<IpcProtocolException>(() => call.Bounded());
    }

    [Fact]
    public async Task Typed_request_answered_without_a_payload_throws_protocol_exception_but_untyped_succeeds()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var typed = peer.RequestAsync<IpcEmpty, EchoResult>("a", IpcEmpty.Instance, CancellationToken.None);
        var first = await remote.ReceiveMessageAsync();
        await remote.SendFrameAsync(TestFrames.HeaderOnly(IpcHeader.Result(first.Id)));
        var untyped = peer.RequestAsync("b", IpcEmpty.Instance, CancellationToken.None);
        var second = await remote.ReceiveMessageAsync();
        await remote.SendFrameAsync(TestFrames.HeaderOnly(IpcHeader.Result(second.Id)));

        await Assert.ThrowsAsync<IpcProtocolException>(() => typed.Bounded());
        Assert.False((await untyped.Bounded()).HasPayload);
    }

    [Fact]
    public async Task Error_response_throws_remote_exception_with_code_message_and_data()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var call = peer.RequestAsync("lookup", IpcEmpty.Instance, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendFrameAsync(TestFrames.Failure(request.Id, new IpcError("not_found", "no such key", """{"key":7}""")));

        var ex = await Assert.ThrowsAsync<IpcRemoteException>(() => call.Bounded());
        Assert.Equal("lookup", ex.Operation);
        Assert.Equal("not_found", ex.Code);
        Assert.Contains("no such key", ex.Message, StringComparison.Ordinal);
        Assert.Equal("""{"key":7}""", ex.DataJson);
    }

    [Fact]
    public async Task Typed_request_with_error_response_throws_remote_exception()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var call = peer.RequestAsync<IpcEmpty, int>("count", IpcEmpty.Instance, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendFrameAsync(TestFrames.Failure(request.Id, new IpcError("busy", "try later")));

        var ex = await Assert.ThrowsAsync<IpcRemoteException>(() => call.Bounded());
        Assert.Equal("busy", ex.Code);
        Assert.Null(ex.DataJson);
    }

    [Fact]
    public async Task Sixty_concurrent_requests_each_receive_their_own_result_when_answered_in_reverse()
    {
        const int count = 60;
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var calls = Enumerable.Range(0, count)
            .Select(i => peer.RequestAsync<EchoArgs, EchoResult>("echo", new EchoArgs("n", i), CancellationToken.None))
            .ToArray();
        var requests = new IpcMessage[count];
        for (var i = 0; i < count; i++)
        {
            requests[i] = await remote.ReceiveMessageAsync();
        }

        Assert.Equal(count, requests.Select(request => request.Id).Distinct().Count());
        foreach (var request in requests.Reverse())
        {
            var args = request.Read<EchoArgs>();
            await remote.SendFrameAsync(TestFrames.Result(request.Id, new EchoResult("echo", args.N, true)));
        }

        var results = await Task.WhenAll(calls).Bounded();
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(i, results[i].N);
        }
    }

    [Fact]
    public async Task Response_with_an_unknown_id_is_ignored_and_the_pending_request_still_completes()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var call = peer.RequestAsync<IpcEmpty, EchoResult>("op", IpcEmpty.Instance, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendFrameAsync(TestFrames.Result(request.Id + 1000, new EchoResult("wrong", 0, false)));
        await remote.SendFrameAsync(TestFrames.Failure(request.Id + 2000, new IpcError("x", "y")));
        await remote.SendFrameAsync(TestFrames.Result(request.Id, new EchoResult("right", 1, true)));

        Assert.Equal("right", (await call.Bounded()).Text);

        var second = peer.RequestAsync<IpcEmpty, EchoResult>("op", IpcEmpty.Instance, CancellationToken.None);
        var next = await remote.ReceiveMessageAsync();
        await remote.SendFrameAsync(TestFrames.Result(next.Id, new EchoResult("second", 2, true)));
        Assert.Equal("second", (await second.Bounded()).Text);
    }

    [Fact]
    public async Task Timeout_throws_timeout_exception_and_sends_cancel_carrying_the_request_id()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local, TestTimeouts.Short);

        var call = peer.RequestAsync("slow", IpcEmpty.Instance, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        var ex = await Assert.ThrowsAsync<IpcTimeoutException>(() => call.Bounded());
        var cancel = await remote.ReceiveMessageAsync();

        Assert.Equal("slow", ex.Operation);
        Assert.Equal(TestTimeouts.Short, ex.Timeout);
        Assert.True(cancel.IsNotification);
        Assert.Equal(IpcOperations.Cancel, cancel.Operation);
        Assert.Equal(request.Id, cancel.Read<long>());
    }

    [Fact]
    public async Task Connection_stays_usable_after_a_timeout_and_a_late_response_is_ignored()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local, TimeSpan.FromSeconds(1));

        var first = peer.RequestAsync("slow", IpcEmpty.Instance, CancellationToken.None);
        var late = await remote.ReceiveMessageAsync();
        await Assert.ThrowsAsync<IpcTimeoutException>(() => first.Bounded());
        var cancel = await remote.ReceiveMessageAsync();
        Assert.Equal(IpcOperations.Cancel, cancel.Operation);

        await remote.SendFrameAsync(TestFrames.Result(late.Id, new EchoResult("late", 0, false)));
        var second = peer.RequestAsync<IpcEmpty, EchoResult>("fast", IpcEmpty.Instance, CancellationToken.None);
        var fast = await remote.ReceiveUntilAsync(message => message.Operation == "fast");
        await remote.SendFrameAsync(TestFrames.Result(fast.Id, new EchoResult("fast", 1, true)));

        Assert.Equal("fast", (await second.Bounded()).Text);
        Assert.False(peer.IsClosed);
    }

    [Fact]
    public async Task Caller_cancellation_throws_operation_canceled_and_sends_cancel_carrying_the_request_id()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        using var cancellation = new CancellationTokenSource();

        var call = peer.RequestAsync("slow", IpcEmpty.Instance, cancellation.Token);
        var request = await remote.ReceiveMessageAsync();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.Bounded());
        var cancel = await remote.ReceiveMessageAsync();
        Assert.Equal(IpcOperations.Cancel, cancel.Operation);
        Assert.Equal(request.Id, cancel.Read<long>());
    }

    [Fact]
    public async Task Cancelled_ping_does_not_send_a_cancel_notification()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        using var cancellation = new CancellationTokenSource();

        var call = peer.RequestAsync(IpcOperations.Ping, IpcEmpty.Instance, cancellation.Token);
        await remote.ReceiveMessageAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.Bounded());
        var marker = peer.RequestAsync("marker", IpcEmpty.Instance, CancellationToken.None);
        var next = await remote.ReceiveMessageAsync();

        Assert.Equal("marker", next.Operation);
        await peer.StopAsync(CancellationToken.None);
        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => marker.Bounded());
    }

    [Fact]
    public async Task Whitespace_operation_name_is_rejected_before_sending()
    {
        var (local, _) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        await Assert.ThrowsAsync<ArgumentException>(async () => await peer.RequestAsync(" ", IpcEmpty.Instance, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await peer.NotifyAsync(" ", IpcEmpty.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task Max_outstanding_rejects_the_extra_request_without_sending_it_and_releases_on_cancel()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local, maxOutstanding: 2);
        using var firstCancel = new CancellationTokenSource();
        using var secondCancel = new CancellationTokenSource();

        var first = peer.RequestAsync("a", IpcEmpty.Instance, firstCancel.Token);
        var second = peer.RequestAsync("b", IpcEmpty.Instance, secondCancel.Token);
        Assert.Equal("a", (await remote.ReceiveMessageAsync()).Operation);
        Assert.Equal("b", (await remote.ReceiveMessageAsync()).Operation);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await peer.RequestAsync("c", IpcEmpty.Instance, CancellationToken.None));

        await firstCancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.Bounded());

        var fourth = peer.RequestAsync("d", IpcEmpty.Instance, CancellationToken.None);
        var next = await remote.ReceiveUntilAsync(message => !message.IsNotification);
        Assert.Equal("d", next.Operation);
        await remote.SendFrameAsync(TestFrames.Result(next.Id, IpcEmpty.Instance));
        await fourth.Bounded();

        await secondCancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.Bounded());
    }

    [Fact]
    public async Task Max_outstanding_slot_is_released_when_each_request_completes()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local, maxOutstanding: 1);

        for (var i = 0; i < 3; i++)
        {
            var call = peer.RequestAsync("step", IpcEmpty.Instance, CancellationToken.None);
            var request = await remote.ReceiveMessageAsync();
            await remote.SendFrameAsync(TestFrames.Result(request.Id, IpcEmpty.Instance));
            await call.Bounded();
        }
    }

    [Fact]
    public async Task Max_outstanding_slot_is_released_when_the_request_fails_remotely()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local, maxOutstanding: 1);

        var failing = peer.RequestAsync("bad", IpcEmpty.Instance, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendFrameAsync(TestFrames.Failure(request.Id, new IpcError("x", "y")));
        await Assert.ThrowsAsync<IpcRemoteException>(() => failing.Bounded());

        var next = peer.RequestAsync("good", IpcEmpty.Instance, CancellationToken.None);
        var second = await remote.ReceiveMessageAsync();
        await remote.SendFrameAsync(TestFrames.Result(second.Id, IpcEmpty.Instance));
        await next.Bounded();
    }
}
