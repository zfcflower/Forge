using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Forge.Mcp.Tests.Fixtures;
using Xunit;

namespace Forge.Mcp.Tests.Http;

public class StreamableHttpTransportTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static StreamableHttpTransport Transport(FakeMcpHttpServer server, bool openServerStream = false, IReadOnlyDictionary<string, string>? headers = null,
        int maxRetries = 3, TimeSpan? retryDelay = null) =>
        new(new McpHttpOptions
        {
            Endpoint = server.Endpoint,
            Headers = headers,
            OpenServerStream = openServerStream,
            HttpClient = DirectHttp.Client,
            MaxServerStreamRetries = maxRetries,
            ServerStreamRetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(50),
        });

    private static McpClient Client(StreamableHttpTransport transport) => new(transport, Clients.DefaultOptions(TimeSpan.FromSeconds(10)));

    private static McpClient SharedClient(StreamableHttpTransport transport) => new(transport, new McpClientOptions
    {
        RequestTimeout = TimeSpan.FromSeconds(10),
        DisposeTransport = false,
    });

    [Fact]
    public async Task Json_round_trip_keeps_the_session_and_protocol_headers_and_sends_custom_headers()
    {
        await using var server = new FakeMcpHttpServer();
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer t0ken" };
        var transport = Transport(server, headers: headers);
        await using var client = Client(transport);

        await client.ConnectAsync(CancellationToken.None);
        var tools = await client.ListToolsAsync(CancellationToken.None);
        var result = await client.CallToolAsync("echo", null, CancellationToken.None);

        Assert.Equal(["echo"], tools.Select(tool => tool.Name));
        Assert.Equal("ok", result.Text);
        Assert.Equal("session-1", transport.SessionId);
        Assert.Equal(McpProtocol.Version, client.ProtocolVersion);

        var requests = server.Requests;
        var initialize = requests.First(request => JsonRpcMethodOf(request) == McpProtocol.Initialize);
        Assert.Null(initialize.Header(McpProtocol.SessionIdHeader));
        Assert.Null(initialize.Header(McpProtocol.ProtocolVersionHeader));
        Assert.Equal("Bearer t0ken", initialize.Header("Authorization"));
        var listed = requests.First(request => JsonRpcMethodOf(request) == McpProtocol.ToolsList);
        Assert.Equal("session-1", listed.Header(McpProtocol.SessionIdHeader));
        Assert.Equal(McpProtocol.Version, listed.Header(McpProtocol.ProtocolVersionHeader));
        var initialized = requests.First(request => JsonRpcMethodOf(request) == McpProtocol.Initialized);
        Assert.Equal("session-1", initialized.Header(McpProtocol.SessionIdHeader));
        Assert.All(requests, request => Assert.Equal("Bearer t0ken", request.Header("Authorization")));
    }

    [Fact]
    public async Task Post_requests_accept_both_json_and_event_streams()
    {
        await using var server = new FakeMcpHttpServer();
        await using var client = Client(Transport(server));

        await client.ConnectAsync(CancellationToken.None);

        var accept = server.Requests.Where(request => request.Method == "POST").Select(request => request.Header("Accept") ?? string.Empty).ToList();
        Assert.All(accept, value => Assert.Contains("application/json", value));
        Assert.All(accept, value => Assert.Contains("text/event-stream", value));
    }

    [Fact]
    public async Task Concurrent_json_calls_each_receive_their_own_answer()
    {
        await using var server = new FakeMcpHttpServer();
        server.Rpc.On(McpProtocol.ToolsCall, root => $$"""{"content":[{"type":"text","text":"reply-{{JsonRpc.ParamsOf(root).GetProperty("arguments").GetProperty("n").GetString()}}"}]}""");
        await using var client = Client(Transport(server));
        await client.ConnectAsync(CancellationToken.None);

        var calls = Enumerable.Range(0, 8).Select(i => client.CallToolAsync("echo", $$"""{"n":"{{i}}"}""", CancellationToken.None)).ToArray();
        var results = await Task.WhenAll(calls);

        Assert.Equal(Enumerable.Range(0, 8).Select(i => "reply-" + i), results.Select(result => result.Text));
    }

    [Fact]
    public async Task Sse_answers_with_crlf_and_multi_line_data_are_reassembled()
    {
        await using var server = new FakeMcpHttpServer { UseSse = true, CrLf = true, SplitSseData = true };
        var transport = Transport(server);
        await using var client = Client(transport);

        await client.ConnectAsync(CancellationToken.None);
        var tools = await client.ListToolsAsync(CancellationToken.None);

        Assert.Equal("fake", client.ServerInfo!.Name);
        Assert.Equal(["echo"], tools.Select(tool => tool.Name));
        Assert.Equal("session-1", transport.SessionId);
    }

    [Fact]
    public async Task Sse_stream_can_carry_notifications_before_the_answer_and_ignores_comments_and_empty_events()
    {
        await using var server = new FakeMcpHttpServer();
        server.Override = request =>
        {
            if (request.Method != "POST" || !request.Body.Contains("\"tools/list\"", StringComparison.Ordinal))
            {
                return null;
            }

            using var document = System.Text.Json.JsonDocument.Parse(request.Body);
            var answer = server.Rpc.Respond(document.RootElement)[0];
            return new HttpReply(200,
                ": keep-alive comment\r\n\r\n" +
                "event: message\r\n" +
                FakeMcpHttpServer.SseEvent(JsonRpc.Notification(McpProtocol.ToolsListChanged), null, crlf: true) +
                "id: 17\r\nevent: message\r\n" +
                FakeMcpHttpServer.SseEvent(answer, null, splitData: true, crlf: true) +
                "event: ping\r\n\r\n",
                "text/event-stream");
        };
        await using var client = Client(Transport(server));
        await client.ConnectAsync(CancellationToken.None);
        var changed = 0;
        client.ToolsChanged += () => Interlocked.Increment(ref changed);

        var tools = await client.ListToolsAsync(CancellationToken.None);

        Assert.Equal(["echo"], tools.Select(tool => tool.Name));
        Assert.Equal(1, Volatile.Read(ref changed));
    }

    [Fact]
    public async Task Sse_stream_that_ends_before_answering_faults_the_pending_request()
    {
        await using var server = new FakeMcpHttpServer();
        server.Override = request => request.Method == "POST" && request.Body.Contains("\"tools/list\"", StringComparison.Ordinal)
            ? new HttpReply(200, FakeMcpHttpServer.SseEvent(JsonRpc.Notification(McpProtocol.LogMessage, """{"level":"info"}""")), "text/event-stream")
            : null;
        await using var client = Client(Transport(server));
        await client.ConnectAsync(CancellationToken.None);
        Exception? closedWith = null;
        client.Closed += error => closedWith = error;

        await Assert.ThrowsAsync<McpConnectionClosedException>(() => client.ListToolsAsync(CancellationToken.None));

        Assert.False(client.IsConnected);
        Assert.Contains("closed the stream before answering", closedWith?.Message ?? string.Empty);
    }

    [Fact]
    public async Task Batch_array_answers_are_dispatched_one_element_at_a_time()
    {
        await using var server = new FakeMcpHttpServer();
        var changed = 0;
        server.Override = request =>
        {
            if (request.Method != "POST" || !request.Body.Contains("\"tools/call\"", StringComparison.Ordinal))
            {
                return null;
            }

            var id = System.Text.Json.JsonDocument.Parse(request.Body).RootElement.GetProperty("id").GetRawText();
            return new HttpReply(200,
                $"[{JsonRpc.Notification(McpProtocol.ToolsListChanged)},{JsonRpc.Result(id, """{"content":[{"type":"text","text":"batched"}]}""")}]");
        };
        await using var client = Client(Transport(server));
        await client.ConnectAsync(CancellationToken.None);
        client.ToolsChanged += () => Interlocked.Increment(ref changed);

        var result = await client.CallToolAsync("echo", null, CancellationToken.None);

        Assert.Equal("batched", result.Text);
        Assert.True(await Eventually.Async(() => Volatile.Read(ref changed) == 1, Wait));
    }

    [Fact]
    public async Task Initialize_answer_over_sse_carries_the_event_id_that_later_resumes_the_server_stream()
    {
        await using var server = new FakeMcpHttpServer { GetStatus = 405 };
        server.Override = request =>
        {
            if (request.Method != "POST" || !request.Body.Contains("\"initialize\"", StringComparison.Ordinal))
            {
                return null;
            }

            var root = System.Text.Json.JsonDocument.Parse(request.Body).RootElement;
            var answer = server.Rpc.Respond(root)[0];
            return new HttpReply(200, FakeMcpHttpServer.SseEvent(answer, "7"), "text/event-stream",
                new Dictionary<string, string> { [McpProtocol.SessionIdHeader] = server.SessionId });
        };
        await using var client = Client(Transport(server, openServerStream: true));

        await client.ConnectAsync(CancellationToken.None);

        Assert.True(await Eventually.Async(() => server.CountRequests("GET") >= 1, Wait));
        var get = server.Requests.First(request => request.Method == "GET");
        Assert.Equal("7", get.Header("Last-Event-ID"));
        Assert.Equal("text/event-stream", get.Header("Accept"));
        Assert.DoesNotContain("application/json", get.Header("Accept") ?? string.Empty);
    }

    [Fact]
    public async Task Server_stream_405_is_treated_as_no_push_channel_without_retries_or_closing()
    {
        await using var server = new FakeMcpHttpServer { GetStatus = 405 };
        await using var client = Client(Transport(server, openServerStream: true, retryDelay: TimeSpan.FromMilliseconds(20)));

        await client.ConnectAsync(CancellationToken.None);
        await Task.Delay(300);

        Assert.Equal(1, server.CountRequests("GET"));
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Server_stream_pushes_notifications_and_resumes_with_last_event_id_after_the_stream_ends()
    {
        await using var server = new FakeMcpHttpServer
        {
            GetStatus = 200,
            GetBody = FakeMcpHttpServer.SseEvent(JsonRpc.Notification(McpProtocol.ToolsListChanged), "5", crlf: true),
        };
        await using var client = Client(Transport(server, openServerStream: true, retryDelay: TimeSpan.FromMilliseconds(40)));
        var changed = 0;
        client.ToolsChanged += () => Interlocked.Increment(ref changed);

        await client.ConnectAsync(CancellationToken.None);

        Assert.True(await Eventually.Async(() => Volatile.Read(ref changed) >= 1, Wait));
        Assert.True(await Eventually.Async(() => server.CountRequests("GET") >= 2, Wait));
        var resumed = server.Requests.Where(request => request.Method == "GET").Skip(1).First();
        Assert.Equal("5", resumed.Header("Last-Event-ID"));
        Assert.Equal("text/event-stream", resumed.Header("Accept"));
        Assert.Equal(McpProtocol.Version, resumed.Header(McpProtocol.ProtocolVersionHeader));
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Server_stream_failing_beyond_the_retry_budget_closes_the_transport_with_an_error()
    {
        await using var server = new FakeMcpHttpServer();
        server.Override = request => request.Method == "GET" ? new HttpReply(500, "boom", "text/plain") : null;
        var transport = Transport(server, openServerStream: true, maxRetries: 2, retryDelay: TimeSpan.FromMilliseconds(20));
        await using var client = Client(transport);
        Exception? closedWith = null;
        var closed = new TaskCompletionSource();
        client.Closed += error =>
        {
            closedWith = error;
            closed.TrySetResult();
        };

        await client.ConnectAsync(CancellationToken.None);
        await closed.Task.WaitAsync(Wait);

        Assert.IsType<McpTransportException>(closedWith);
        Assert.Contains("server stream", closedWith.Message);
        Assert.Equal(3, server.CountRequests("GET"));
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task Expired_session_404_raises_session_expired_and_closes_the_transport()
    {
        await using var server = new FakeMcpHttpServer();
        var transport = Transport(server);
        await using var client = Client(transport);
        await client.ConnectAsync(CancellationToken.None);
        Exception? closedWith = null;
        client.Closed += error => closedWith = error;
        server.IsExpired = true;

        var expired = await Assert.ThrowsAsync<McpSessionExpiredException>(() => client.ListToolsAsync(CancellationToken.None));

        Assert.Contains("expired", expired.Message);
        Assert.IsType<McpSessionExpiredException>(closedWith);
        Assert.False(transport.IsConnected);
        Assert.Null(transport.SessionId);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Re_initialization_on_a_new_client_after_expiry_negotiates_a_fresh_session()
    {
        await using var server = new FakeMcpHttpServer();
        var transport = Transport(server);
        await using (var first = SharedClient(transport))
        {
            await first.ConnectAsync(CancellationToken.None);
            server.IsExpired = true;
            await Assert.ThrowsAsync<McpSessionExpiredException>(() => first.ListToolsAsync(CancellationToken.None));
        }

        server.IsExpired = false;
        server.SessionId = "session-2";
        await using var second = SharedClient(transport);
        await second.ConnectAsync(CancellationToken.None);
        var tools = await second.ListToolsAsync(CancellationToken.None);

        Assert.Equal("session-2", transport.SessionId);
        Assert.Equal(["echo"], tools.Select(tool => tool.Name));
        Assert.Equal("session-2", server.Requests.Last(request => JsonRpcMethodOf(request) == McpProtocol.ToolsList).Header(McpProtocol.SessionIdHeader));
    }

    [Fact]
    public async Task Not_found_without_a_session_is_a_plain_transport_error()
    {
        await using var server = new FakeMcpHttpServer();
        server.Override = _ => new HttpReply(404, "no such endpoint", "text/plain");
        await using var client = Client(Transport(server));

        var error = await Assert.ThrowsAsync<McpTransportException>(() => client.ConnectAsync(CancellationToken.None));

        Assert.IsNotType<McpSessionExpiredException>(error);
        Assert.Contains("HTTP 404", error.Message);
        Assert.Contains("no such endpoint", error.Message);
    }

    [Fact]
    public async Task Error_status_reports_a_bounded_body_snippet()
    {
        await using var server = new FakeMcpHttpServer();
        server.Override = _ => new HttpReply(500, new string('x', 2000), "text/plain");
        await using var client = Client(Transport(server));

        var error = await Assert.ThrowsAsync<McpTransportException>(() => client.ConnectAsync(CancellationToken.None));

        Assert.Contains("returned HTTP 500", error.Message);
        Assert.True(error.Message.Length < 800, $"message length {error.Message.Length}");
    }

    [Fact]
    public async Task Secrets_from_headers_never_appear_in_error_messages()
    {
        const string secret = "SECRET-HEADER-VALUE-42";
        await using var server = new FakeMcpHttpServer();
        server.Override = _ => new HttpReply(401, "denied", "text/plain");
        var transport = Transport(server, headers: new Dictionary<string, string> { ["Authorization"] = "Bearer " + secret, ["X-Api-Key"] = secret });
        await using var client = Client(transport);

        var error = await Assert.ThrowsAsync<McpTransportException>(() => client.ConnectAsync(CancellationToken.None));

        Assert.Contains("HTTP 401", error.Message);
        Assert.DoesNotContain(secret, error.Message);
        Assert.DoesNotContain(secret, error.ToString());
    }

    [Fact]
    public async Task Unreachable_endpoint_is_a_transport_error_without_header_values()
    {
        const string secret = "unreachable-secret-7";
        var port = FreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp/");
        var transport = new StreamableHttpTransport(new McpHttpOptions
        {
            Endpoint = endpoint,
            Headers = new Dictionary<string, string> { ["Authorization"] = secret },
            OpenServerStream = false,
            HttpClient = DirectHttp.Client,
        });
        await using var client = Client(transport);

        var error = await Assert.ThrowsAsync<McpTransportException>(() => client.ConnectAsync(CancellationToken.None));

        Assert.Contains("failed", error.Message);
        Assert.DoesNotContain(secret, error.Message);
        Assert.False(client.IsInitialized);
    }

    [Fact]
    public async Task Close_ends_the_session_with_a_single_delete()
    {
        await using var server = new FakeMcpHttpServer();
        var transport = Transport(server);
        await using var client = Client(transport);
        await client.ConnectAsync(CancellationToken.None);
        var closedEvents = 0;
        client.Closed += _ => Interlocked.Increment(ref closedEvents);

        await client.CloseAsync(CancellationToken.None);

        Assert.Equal(1, server.CountRequests("DELETE"));
        Assert.False(transport.IsConnected);
        Assert.False(client.IsConnected);
        Assert.Equal(1, closedEvents);
        Assert.Null(transport.SessionId);
    }

    [Fact]
    public async Task Close_sends_the_session_header_on_delete()
    {
        await using var server = new FakeMcpHttpServer();
        await using var client = Client(Transport(server));
        await client.ConnectAsync(CancellationToken.None);

        await client.CloseAsync(CancellationToken.None);

        var delete = server.Requests.Single(request => request.Method == "DELETE");
        Assert.Equal("session-1", delete.Header(McpProtocol.SessionIdHeader));
    }

    [Fact]
    public async Task Disposed_transport_rejects_further_starts_and_sends()
    {
        await using var server = new FakeMcpHttpServer();
        var transport = Transport(server);
        await using (var client = Client(transport))
        {
            await client.ConnectAsync(CancellationToken.None);
        }

        Assert.False(transport.IsConnected);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.StartAsync(CancellationToken.None));
        await Assert.ThrowsAsync<McpConnectionClosedException>(async () =>
            await transport.SendAsync(new byte[] { (byte)'{', (byte)'}' }, CancellationToken.None));
    }

    [Fact]
    public async Task Notifications_are_acknowledged_with_202_and_produce_no_messages()
    {
        await using var server = new FakeMcpHttpServer();
        var messages = new ConcurrentQueue<string>();
        var transport = Transport(server);
        await using var client = Client(transport);
        await client.ConnectAsync(CancellationToken.None);
        client.NotificationReceived += notification => messages.Enqueue(notification.Method);

        await client.SendNotificationAsync("notifications/roots/list_changed", null, CancellationToken.None);

        Assert.Equal(1, server.CountRequests("POST", "notifications/roots/list_changed"));
        Assert.Empty(messages);
        Assert.True(client.IsConnected);
    }

    private static string? JsonRpcMethodOf(RecordedHttpRequest request) =>
        request.Body.Length == 0 ? null : JsonRpc.MethodOf(request.Body);

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
