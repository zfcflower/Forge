using System.Text.Json;
using Forge.Ipc.Tests.Support;
using Xunit;

namespace Forge.Ipc.Tests;

public sealed class IpcPeerRequestTests
{
    private sealed record UnregisteredPayload(int Value);

    [Fact]
    public async Task Response_completes_the_request_with_the_matching_result_bytes()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        await peer.StartAsync(CancellationToken.None);

        var call = peer.SendRequestAsync("echo", Json.Parse("""{ "n" : 1, "s" : "\u00e9" }"""), CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendMessageAsync(IpcMessage.ResultMessage(request.Id, Json.Parse("""{ "ok" : true, "n" : 1.50 }""")));
        var response = await call.Bounded();

        Assert.Equal("echo", request.Operation);
        Assert.Equal("""{ "n" : 1, "s" : "\u00e9" }""", request.Arguments!.Value.GetRawText());
        Assert.Equal(request.Id, response.Id);
        Assert.Equal("""{ "ok" : true, "n" : 1.50 }""", response.Result!.Value.GetRawText());
    }

    [Fact]
    public async Task Outbound_request_puts_operation_and_argument_bytes_on_the_wire()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var call = peer.SendRequestAsync("echo", Json.Parse("""{ "n" : 1.50 }"""), CancellationToken.None);
        var body = await remote.ReceiveBodyAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal("echo", document.RootElement.GetProperty("op").GetString());
        Assert.True(document.RootElement.GetProperty("id").GetInt64() > 0);
        Assert.Equal(1, document.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("""{ "n" : 1.50 }""", document.RootElement.GetProperty("args").GetRawText());

        await peer.StopAsync(CancellationToken.None);
        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => call.Bounded());
    }

    [Fact]
    public async Task Typed_request_deserializes_a_string_result()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var call = peer.SendRequestAsync<string>("name", null, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendMessageAsync(IpcMessage.ResultMessage(request.Id, Json.Parse("\"hi\"")));

        Assert.Equal("hi", await call.Bounded());
    }

    [Fact]
    public async Task Typed_request_deserializes_an_int_result()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var call = peer.SendRequestAsync<int>("count", null, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendMessageAsync(IpcMessage.ResultMessage(request.Id, Json.Parse("42")));

        Assert.Equal(42, await call.Bounded());
    }

    [Fact]
    public async Task Typed_request_with_an_unregistered_result_type_fails_after_the_response_arrives()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var call = peer.SendRequestAsync<UnregisteredPayload>("shape", null, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendMessageAsync(IpcMessage.ResultMessage(request.Id, Json.Parse("""{"Value":1}""")));

        var ex = await Assert.ThrowsAsync<IpcProtocolException>(() => call.Bounded());
        Assert.Contains("is not registered", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Typed_request_with_a_mismatched_result_shape_throws_protocol_exception()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var call = peer.SendRequestAsync<int>("count", null, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendMessageAsync(IpcMessage.ResultMessage(request.Id, Json.Parse("\"not a number\"")));

        await Assert.ThrowsAsync<IpcProtocolException>(() => call.Bounded());
    }

    [Fact]
    public async Task Error_response_throws_remote_exception_with_code_message_and_data()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var call = peer.SendRequestAsync("lookup", null, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendMessageAsync(IpcMessage.ErrorMessage(request.Id, new IpcError("not_found", "no such key", """{"key":7}""")));

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

        var call = peer.SendRequestAsync<int>("count", null, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendMessageAsync(IpcMessage.ErrorMessage(request.Id, new IpcError("busy", "try later")));

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
            .Select(i => peer.SendRequestAsync("echo", Json.Parse($$"""{"n":{{i}}}"""), CancellationToken.None))
            .ToArray();
        var requests = new IpcMessage[count];
        for (var i = 0; i < count; i++)
        {
            requests[i] = await remote.ReceiveMessageAsync();
        }

        Assert.Equal(count, requests.Select(request => request.Id).Distinct().Count());
        foreach (var request in requests.Reverse())
        {
            var n = request.Arguments!.Value.GetProperty("n").GetInt32();
            await remote.SendMessageAsync(IpcMessage.ResultMessage(request.Id, Json.Parse($$"""{"echo":{{n}}}""")));
        }

        var responses = await Task.WhenAll(calls).Bounded();
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(i, responses[i].Result!.Value.GetProperty("echo").GetInt32());
        }
    }

    [Fact]
    public async Task Response_with_an_unknown_id_is_ignored_and_the_pending_request_still_completes()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var call = peer.SendRequestAsync("op", null, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendMessageAsync(IpcMessage.ResultMessage(request.Id + 1000, Json.Parse("""{"wrong":true}""")));
        await remote.SendMessageAsync(IpcMessage.ErrorMessage(request.Id + 2000, new IpcError("x", "y")));
        await remote.SendMessageAsync(IpcMessage.ResultMessage(request.Id, Json.Parse("""{"right":true}""")));

        var response = await call.Bounded();
        Assert.Equal("""{"right":true}""", response.Result!.Value.GetRawText());

        var second = peer.SendRequestAsync("op", null, CancellationToken.None);
        var next = await remote.ReceiveMessageAsync();
        await remote.SendMessageAsync(IpcMessage.ResultMessage(next.Id, Json.Parse("""{"second":true}""")));
        Assert.Equal("""{"second":true}""", (await second.Bounded()).Result!.Value.GetRawText());
    }

    [Fact]
    public async Task Response_with_json_null_result_completes_the_request()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local, TimeSpan.FromSeconds(2));

        var call = peer.SendRequestAsync("void", null, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        await remote.SendMessageAsync(IpcMessage.ResultMessage(request.Id, Json.Parse("null")));
        var response = await call.Bounded();

        Assert.True(response.IsResponse);
        Assert.Equal("null", response.Result!.Value.GetRawText());
    }

    [Fact]
    public async Task Timeout_throws_timeout_exception_and_sends_cancel_carrying_the_request_id()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local, TestTimeouts.Short);

        var call = peer.SendRequestAsync("slow", null, CancellationToken.None);
        var request = await remote.ReceiveMessageAsync();
        var ex = await Assert.ThrowsAsync<IpcTimeoutException>(() => call.Bounded());
        var cancel = await remote.ReceiveMessageAsync();

        Assert.Equal("slow", ex.Operation);
        Assert.Equal(TestTimeouts.Short, ex.Timeout);
        Assert.True(cancel.IsNotification);
        Assert.Equal("cancel", cancel.Operation);
        Assert.Equal(request.Id, cancel.Arguments!.Value.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Connection_stays_usable_after_a_timeout_and_a_late_response_is_ignored()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local, TimeSpan.FromSeconds(1));

        var first = peer.SendRequestAsync("slow", null, CancellationToken.None);
        var late = await remote.ReceiveMessageAsync();
        await Assert.ThrowsAsync<IpcTimeoutException>(() => first.Bounded());
        var cancel = await remote.ReceiveMessageAsync();
        Assert.Equal("cancel", cancel.Operation);

        await remote.SendMessageAsync(IpcMessage.ResultMessage(late.Id, Json.Parse("""{"late":true}""")));
        var second = peer.SendRequestAsync("fast", null, CancellationToken.None);
        var fast = await remote.ReceiveUntilAsync(message => message.Operation == "fast");
        await remote.SendMessageAsync(IpcMessage.ResultMessage(fast.Id, Json.Parse("""{"fast":true}""")));

        Assert.Equal("""{"fast":true}""", (await second.Bounded()).Result!.Value.GetRawText());
        Assert.False(peer.IsClosed);
    }

    [Fact]
    public async Task Caller_cancellation_throws_operation_canceled_and_sends_cancel_carrying_the_request_id()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        using var cancellation = new CancellationTokenSource();

        var call = peer.SendRequestAsync("slow", null, cancellation.Token);
        var request = await remote.ReceiveMessageAsync();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.Bounded());
        var cancel = await remote.ReceiveMessageAsync();
        Assert.Equal("cancel", cancel.Operation);
        Assert.Equal(request.Id, cancel.Arguments!.Value.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Whitespace_operation_name_is_rejected_before_sending()
    {
        var (local, _) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        await Assert.ThrowsAsync<ArgumentException>(async () => await peer.SendRequestAsync(" ", null, CancellationToken.None));
    }

    [Fact]
    public async Task Max_outstanding_rejects_the_extra_request_without_sending_it_and_releases_on_cancel()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local, maxOutstanding: 2);
        using var firstCancel = new CancellationTokenSource();
        using var secondCancel = new CancellationTokenSource();

        var first = peer.SendRequestAsync("a", null, firstCancel.Token);
        var second = peer.SendRequestAsync("b", null, secondCancel.Token);
        Assert.Equal("a", (await remote.ReceiveMessageAsync()).Operation);
        Assert.Equal("b", (await remote.ReceiveMessageAsync()).Operation);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await peer.SendRequestAsync("c", null, CancellationToken.None));

        await firstCancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.Bounded());

        var fourth = peer.SendRequestAsync("d", null, CancellationToken.None);
        var next = await remote.ReceiveUntilAsync(message => !message.IsNotification);
        Assert.Equal("d", next.Operation);
        await remote.SendMessageAsync(IpcMessage.ResultMessage(next.Id, Json.Parse("{}")));
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
            var call = peer.SendRequestAsync("step", null, CancellationToken.None);
            var request = await remote.ReceiveMessageAsync();
            await remote.SendMessageAsync(IpcMessage.ResultMessage(request.Id, Json.Parse("""{"step":1}""")));
            await call.Bounded();
        }
    }
}
