using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Mcp.Tests.Fixtures;
using Xunit;

namespace Forge.Mcp.Tests.Client;

public class McpClientRequestTests
{
    [Fact]
    public async Task Concurrent_requests_are_correlated_by_id_when_replies_arrive_out_of_order()
    {
        const int count = 12;
        var server = new ScriptedServer();
        var pending = new ConcurrentQueue<string>();
        var transport = new LoopbackTransport();
        transport.Responder = request =>
        {
            if (JsonRpc.MethodOf(request.GetRawText()) != McpProtocol.ToolsCall)
            {
                return server.Respond(request);
            }

            pending.Enqueue(JsonRpc.IdOf(request) + "|" + request.GetProperty("params").GetProperty("arguments").GetProperty("n").GetString());
            if (pending.Count < count)
            {
                return [];
            }

            var replies = pending.ToArray().Reverse().Select(entry =>
            {
                var parts = entry.Split('|');
                return JsonRpc.Result(parts[0], $$"""{"content":[{"type":"text","text":"{{parts[1]}}"}]}""");
            });
            pending.Clear();
            return replies;
        };
        await using var client = new McpClient(transport, Clients.DefaultOptions());
        await client.ConnectAsync(CancellationToken.None);

        var calls = Enumerable.Range(0, count)
            .Select(i => client.CallToolAsync("echo", $$"""{"n":"{{i}}"}""", CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(calls);

        Assert.Equal(Enumerable.Range(0, count).Select(i => i.ToString()), results.Select(result => result.Text));
    }

    [Fact]
    public async Task Responses_with_unknown_or_missing_ids_are_ignored_and_later_requests_still_complete()
    {
        var (client, transport) = await Clients.ConnectLoopbackAsync();
        await using var connection = client;

        transport.Push(JsonRpc.Result("999", "{}"));
        transport.Push("""{"jsonrpc":"2.0","result":{}}""");
        transport.Push("not json at all");
        transport.Push("[1,2,3]");
        transport.Push("\"just a string\"");

        await client.PingAsync(CancellationToken.None);

        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task String_id_response_does_not_complete_the_numeric_request_with_the_same_digits()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, root => null);
        var transport = new LoopbackTransport { Responder = server.Respond };
        await using var client = new McpClient(transport, Clients.DefaultOptions());
        await client.ConnectAsync(CancellationToken.None);

        var pendingCall = client.CallToolAsync("echo", null, CancellationToken.None);
        await Eventually.AssertAsync(() => transport.TrackedRequestIds.Count >= 2, "tools/call was not sent");
        var id = transport.TrackedRequestIds[^1];
        transport.Push(JsonRpc.Result($"\"{id}\"", """{"content":[{"type":"text","text":"string-id"}]}"""));
        await client.PingAsync(CancellationToken.None);

        Assert.False(pendingCall.IsCompleted);

        transport.Push(JsonRpc.Result(id.ToString(), """{"content":[{"type":"text","text":"numeric-id"}]}"""));

        var result = await pendingCall;
        Assert.Equal("numeric-id", result.Text);
    }

    [Fact]
    public async Task Error_response_becomes_protocol_exception_with_method_code_and_raw_data()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => throw new ScriptedRpcError(-32602, "bad arguments", """{"field":"text"}"""));
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("echo", "{}", CancellationToken.None));

        Assert.Equal(McpProtocol.ToolsCall, error.Method);
        Assert.Equal(-32602, error.Code);
        Assert.Equal("""{"field":"text"}""", error.DataJson);
        Assert.False(error.IsMethodNotFound);
        Assert.Equal("tools/call failed with JSON-RPC error -32602: bad arguments", error.Message);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Method_not_found_error_is_flagged_and_error_without_code_defaults_to_internal_error()
    {
        var server = new ScriptedServer();
        var transport = new LoopbackTransport();
        transport.Responder = root =>
        {
            if (root.TryGetProperty("method", out var method) && method.GetString() == McpProtocol.ToolsCall)
            {
                return [$"{{\"jsonrpc\":\"2.0\",\"id\":{JsonRpc.IdOf(root)},\"error\":{{\"message\":\"boom\"}}}}"];
            }

            return server.Respond(root);
        };
        await using var client = new McpClient(transport, Clients.DefaultOptions());
        await client.ConnectAsync(CancellationToken.None);

        var missing = await Assert.ThrowsAsync<McpProtocolException>(() => client.ListResourcesAsync(CancellationToken.None));
        var internalError = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("echo", null, CancellationToken.None));

        Assert.True(missing.IsMethodNotFound);
        Assert.Equal(McpErrorCodes.InternalError, internalError.Code);
        Assert.Equal("tools/call failed with JSON-RPC error -32603: boom", internalError.Message);
        Assert.Null(internalError.DataJson);
    }

    [Fact]
    public async Task Timeout_throws_timeout_exception_and_sends_cancellation_for_that_request()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => null);
        var transport = new LoopbackTransport { Responder = server.Respond };
        await using var client = new McpClient(transport, Clients.DefaultOptions(TimeSpan.FromMilliseconds(150)));
        await client.ConnectAsync(CancellationToken.None);

        var timeout = await Assert.ThrowsAsync<McpTimeoutException>(() => client.CallToolAsync("hang", null, CancellationToken.None));

        Assert.Equal(McpProtocol.ToolsCall, timeout.Method);
        Assert.Equal(TimeSpan.FromMilliseconds(150), timeout.Timeout);
        var callId = transport.TrackedRequestIds[^1];
        await Eventually.AssertAsync(() => transport.Sent.Any(message => JsonRpc.MethodOf(message) == McpProtocol.Cancelled), "no cancellation sent");
        using var document = JsonDocument.Parse(transport.Sent.Single(message => JsonRpc.MethodOf(message) == McpProtocol.Cancelled));
        var parameters = document.RootElement.GetProperty("params");
        Assert.Equal(callId, parameters.GetProperty("requestId").GetInt64());
        Assert.Equal("request timed out", parameters.GetProperty("reason").GetString());
        Assert.Contains(callId, transport.SettledRequestIds);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Late_response_after_timeout_is_ignored()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => null);
        var transport = new LoopbackTransport { Responder = server.Respond };
        await using var client = new McpClient(transport, Clients.DefaultOptions(TimeSpan.FromMilliseconds(100)));
        await client.ConnectAsync(CancellationToken.None);
        await Assert.ThrowsAsync<McpTimeoutException>(() => client.CallToolAsync("hang", null, CancellationToken.None));

        transport.Push(JsonRpc.Result(transport.TrackedRequestIds[^1].ToString(), """{"content":[]}"""));

        await client.PingAsync(CancellationToken.None);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Caller_cancellation_cancels_the_wait_and_sends_cancellation_with_client_reason()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => null);
        var transport = new LoopbackTransport { Responder = server.Respond };
        await using var client = new McpClient(transport, Clients.DefaultOptions(TimeSpan.FromSeconds(30)));
        await client.ConnectAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var call = client.CallToolAsync("hang", null, cts.Token);
        await Eventually.AssertAsync(() => transport.TrackedRequestIds.Count >= 2, "tools/call was not sent");
        var callId = transport.TrackedRequestIds[^1];
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await Eventually.AssertAsync(() => transport.Sent.Any(message => JsonRpc.MethodOf(message) == McpProtocol.Cancelled), "no cancellation sent");
        using var document = JsonDocument.Parse(transport.Sent.Single(message => JsonRpc.MethodOf(message) == McpProtocol.Cancelled));
        Assert.Equal(callId, document.RootElement.GetProperty("params").GetProperty("requestId").GetInt64());
        Assert.Equal("cancelled by client", document.RootElement.GetProperty("params").GetProperty("reason").GetString());
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Already_cancelled_token_fails_the_request_before_waiting_for_a_reply()
    {
        var transport = new LoopbackTransport { Responder = new ScriptedServer().Respond };
        await using var client = new McpClient(transport, Clients.DefaultOptions());
        await client.ConnectAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PingAsync(cts.Token));
    }

    [Fact]
    public async Task Pending_requests_fault_with_connection_closed_when_the_transport_closes()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => null).On(McpProtocol.ToolsList, _ => null);
        var transport = new LoopbackTransport { Responder = server.Respond };
        await using var client = new McpClient(transport, Clients.DefaultOptions(TimeSpan.FromSeconds(30)));
        Exception? closedWith = new InvalidOperationException("not raised");
        var closedRaised = new TaskCompletionSource();
        client.Closed += error =>
        {
            closedWith = error;
            closedRaised.TrySetResult();
        };
        await client.ConnectAsync(CancellationToken.None);

        var first = client.CallToolAsync("hang", null, CancellationToken.None);
        var second = client.ListToolsAsync(CancellationToken.None);
        await Eventually.AssertAsync(() => transport.TrackedRequestIds.Count >= 3, "requests were not sent");
        transport.Drop(new McpTransportException("socket reset"));

        var firstError = await Assert.ThrowsAsync<McpConnectionClosedException>(() => first);
        var secondError = await Assert.ThrowsAsync<McpConnectionClosedException>(() => second);
        Assert.IsType<McpTransportException>(firstError.InnerException);
        Assert.Contains("tools/call", firstError.Message);
        Assert.Contains("tools/list", secondError.Message);
        await closedRaised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("socket reset", closedWith?.Message);
        Assert.False(client.IsConnected);
        Assert.False(client.IsInitialized);
    }

    [Fact]
    public async Task Requests_after_the_transport_closed_fail_fast_without_being_sent()
    {
        var (client, transport) = await Clients.ConnectLoopbackAsync();
        await using var connection = client;
        var sentBefore = transport.Sent.Count;
        transport.Drop();
        await Eventually.AssertAsync(() => !client.IsConnected, "client still connected");

        var error = await Assert.ThrowsAsync<McpConnectionClosedException>(() => client.PingAsync(CancellationToken.None));

        Assert.Contains(McpProtocol.Ping, error.Message);
        Assert.Equal(sentBefore, transport.Sent.Count);
    }

    [Fact]
    public async Task Send_failure_propagates_and_the_pending_entry_is_released()
    {
        var (client, transport) = await Clients.ConnectLoopbackAsync();
        await using var connection = client;
        transport.SendFailure = new McpTransportException("pipe broken");
        var settledBefore = transport.SettledRequestIds.Count;

        var error = await Assert.ThrowsAsync<McpTransportException>(() => client.PingAsync(CancellationToken.None));

        Assert.Equal("pipe broken", error.Message);
        Assert.True(transport.SettledRequestIds.Count > settledBefore);
    }

    [Fact]
    public async Task Every_answered_request_is_reported_as_settled_on_the_transport()
    {
        var (client, transport) = await Clients.ConnectLoopbackAsync();
        await using var connection = client;

        await client.PingAsync(CancellationToken.None);
        await client.ListToolsAsync(CancellationToken.None);

        Assert.Equal(transport.TrackedRequestIds.OrderBy(id => id), transport.SettledRequestIds.Distinct().OrderBy(id => id));
    }

    [Fact]
    public async Task Disposing_the_client_disposes_the_transport_unless_the_caller_keeps_it()
    {
        var owned = await Clients.ConnectLoopbackAsync();
        await owned.Client.DisposeAsync();
        Assert.Equal(1, owned.Transport.DisposeCount);

        var shared = await Clients.ConnectLoopbackAsync(options: new McpClientOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(5),
            DisposeTransport = false,
        });
        await shared.Client.DisposeAsync();
        Assert.Equal(0, shared.Transport.DisposeCount);
        await shared.Transport.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_is_idempotent()
    {
        var (client, transport) = await Clients.ConnectLoopbackAsync();

        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task Closing_the_client_stops_the_transport_and_marks_the_client_closed()
    {
        var (client, transport) = await Clients.ConnectLoopbackAsync();
        await using var connection = client;
        var closedEvents = 0;
        client.Closed += _ => Interlocked.Increment(ref closedEvents);

        await client.CloseAsync(CancellationToken.None);

        Assert.Equal(1, transport.StopCount);
        Assert.False(client.IsConnected);
        Assert.False(client.IsInitialized);
        Assert.Equal(1, closedEvents);
    }
}
