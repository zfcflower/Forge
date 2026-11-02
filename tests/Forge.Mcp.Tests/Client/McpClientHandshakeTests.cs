using System.Text.Json;
using Forge.Mcp.Tests.Fixtures;
using Xunit;

namespace Forge.Mcp.Tests.Client;

public class McpClientHandshakeTests
{
    [Fact]
    public async Task ConnectAsync_sends_initialize_with_protocol_version_roots_capability_and_client_info()
    {
        var options = new McpClientOptions { ClientName = "forge-test", ClientVersion = "9.8.7", RequestTimeout = TimeSpan.FromSeconds(5) };
        var (client, transport) = await Clients.ConnectLoopbackAsync(options: options);
        await using var connection = client;

        using var initialize = JsonDocument.Parse(transport.Sent[0]);
        var root = initialize.RootElement;
        Assert.Equal(McpProtocol.Initialize, root.GetProperty("method").GetString());
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        var parameters = root.GetProperty("params");
        Assert.Equal(McpProtocol.Version, parameters.GetProperty("protocolVersion").GetString());
        Assert.False(parameters.GetProperty("capabilities").GetProperty("roots").GetProperty("listChanged").GetBoolean());
        Assert.Equal("forge-test", parameters.GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.Equal("9.8.7", parameters.GetProperty("clientInfo").GetProperty("version").GetString());
    }

    [Fact]
    public async Task ConnectAsync_sends_initialized_notification_after_the_initialize_response()
    {
        var (client, transport) = await Clients.ConnectLoopbackAsync();
        await using var connection = client;

        Assert.Equal(2, transport.Sent.Count);
        Assert.Equal(McpProtocol.Initialized, JsonRpc.MethodOf(transport.Sent[1]));
        using var notification = JsonDocument.Parse(transport.Sent[1]);
        Assert.False(notification.RootElement.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task ConnectAsync_publishes_protocol_version_to_transport_before_initialized_notification()
    {
        var (client, transport) = await Clients.ConnectLoopbackAsync();
        await using var connection = client;

        Assert.Equal(McpProtocol.Version, Assert.Single(transport.ProtocolVersions));
        Assert.Equal(McpProtocol.Version, client.ProtocolVersion);
    }

    [Fact]
    public async Task ConnectAsync_parses_server_info_instructions_and_full_capability_set()
    {
        const string result = """
            {"protocolVersion":"2025-06-18",
             "capabilities":{"tools":{"listChanged":true},"resources":{"subscribe":true,"listChanged":false},
                             "prompts":{},"logging":{},"completions":{}},
             "serverInfo":{"name":"docs","version":"2.0","title":"Docs server"},
             "instructions":"Use search first."}
            """;
        var server = new ScriptedServer().On(McpProtocol.Initialize, _ => result);
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        Assert.Equal(new McpServerInfo("docs", "2.0", "Docs server"), client.ServerInfo);
        Assert.Equal("Use search first.", client.Instructions);
        var capabilities = client.Capabilities;
        Assert.True(capabilities.Tools);
        Assert.True(capabilities.ToolsListChanged);
        Assert.True(capabilities.Resources);
        Assert.True(capabilities.ResourcesSubscribe);
        Assert.False(capabilities.ResourcesListChanged);
        Assert.True(capabilities.Prompts);
        Assert.False(capabilities.PromptsListChanged);
        Assert.True(capabilities.Logging);
        Assert.True(capabilities.Completions);
        Assert.True(client.IsInitialized);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Capability_flags_default_to_false_when_values_are_not_objects_or_true()
    {
        const string result = """{"protocolVersion":"2025-06-18","capabilities":{"tools":true,"resources":"yes","prompts":{"listChanged":"maybe"}}}""";
        var server = new ScriptedServer().On(McpProtocol.Initialize, _ => result);
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        Assert.True(client.Capabilities.Tools);
        Assert.False(client.Capabilities.ToolsListChanged);
        Assert.False(client.Capabilities.Resources);
        Assert.True(client.Capabilities.Prompts);
        Assert.False(client.Capabilities.PromptsListChanged);
        Assert.Null(client.ServerInfo);
    }

    [Fact]
    public async Task Missing_capabilities_object_yields_none()
    {
        var server = new ScriptedServer().On(McpProtocol.Initialize, _ => """{"protocolVersion":"2025-06-18"}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        Assert.Equal(McpServerCapabilities.None, client.Capabilities);
        Assert.Null(client.Instructions);
    }

    [Fact]
    public async Task Missing_server_protocol_version_falls_back_to_the_client_option()
    {
        var server = new ScriptedServer().On(McpProtocol.Initialize, _ => """{"capabilities":{}}""");
        var options = new McpClientOptions { ProtocolVersion = "2024-11-05", RequestTimeout = TimeSpan.FromSeconds(5) };
        var (client, transport) = await Clients.ConnectLoopbackAsync(server, options);
        await using var connection = client;

        Assert.Equal("2024-11-05", client.ProtocolVersion);
        Assert.Equal("2024-11-05", Assert.Single(transport.ProtocolVersions));
    }

    [Fact]
    public async Task Server_info_name_and_version_default_when_missing_inside_object()
    {
        var server = new ScriptedServer().On(McpProtocol.Initialize, _ => """{"protocolVersion":"2025-06-18","serverInfo":{}}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        Assert.Equal(new McpServerInfo("unknown", "0.0.0", null), client.ServerInfo);
    }

    [Fact]
    public async Task Second_ConnectAsync_on_an_initialized_client_does_not_reinitialize()
    {
        var (client, transport) = await Clients.ConnectLoopbackAsync();
        await using var connection = client;

        await client.ConnectAsync(CancellationToken.None);

        Assert.Equal(1, transport.StartCount);
        Assert.Equal(1, transport.Sent.Count(message => JsonRpc.MethodOf(message) == McpProtocol.Initialize));
    }

    [Fact]
    public async Task Initialize_error_response_throws_protocol_exception_and_leaves_client_uninitialized()
    {
        var server = new ScriptedServer().On(McpProtocol.Initialize, _ => throw new ScriptedRpcError(-32600, "unsupported protocol"));
        var transport = new LoopbackTransport { Responder = server.Respond };
        await using var client = new McpClient(transport, Clients.DefaultOptions());

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => client.ConnectAsync(CancellationToken.None));

        Assert.Equal(McpProtocol.Initialize, error.Method);
        Assert.Equal(-32600, error.Code);
        Assert.False(client.IsInitialized);
        Assert.DoesNotContain(transport.Sent, message => JsonRpc.MethodOf(message) == McpProtocol.Initialized);
    }

    [Fact]
    public async Task Initialize_non_object_result_is_a_protocol_error()
    {
        var transport = new LoopbackTransport
        {
            Responder = request => [JsonRpc.Result(JsonRpc.IdOf(request), """[1,2]""")],
        };
        await using var client = new McpClient(transport, Clients.DefaultOptions());

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => client.ConnectAsync(CancellationToken.None));

        Assert.Equal(McpErrorCodes.InvalidRequest, error.Code);
        Assert.Contains("no result object", error.Message);
    }

    [Fact]
    public async Task Reconnect_on_the_same_client_after_the_transport_dropped_succeeds()
    {
        var (client, transport) = await Clients.ConnectLoopbackAsync();
        await using var connection = client;
        transport.Drop();
        await Eventually.AssertAsync(() => !client.IsConnected, "client still connected");

        await client.ConnectAsync(CancellationToken.None);

        Assert.True(client.IsConnected);
        Assert.Equal(2, transport.StartCount);
    }

    [Fact]
    public async Task Ping_round_trips_and_completes()
    {
        var (client, transport) = await Clients.ConnectLoopbackAsync();
        await using var connection = client;

        await client.PingAsync(CancellationToken.None);

        Assert.Contains(transport.Sent, message => JsonRpc.MethodOf(message) == McpProtocol.Ping);
    }

    [Fact]
    public async Task Server_ping_request_is_answered_with_empty_result()
    {
        var (client, transport) = await Clients.ConnectLoopbackAsync();
        await using var connection = client;

        transport.Push(JsonRpc.Request("\"s-1\"", McpProtocol.Ping));

        Assert.True(await Eventually.Async(() => transport.Sent.Any(message => message.Contains("\"id\":\"s-1\""))));
        var answer = transport.Sent.Single(message => message.Contains("\"id\":\"s-1\""));
        using var document = JsonDocument.Parse(answer);
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("result").ValueKind);
        Assert.False(document.RootElement.TryGetProperty("method", out _));
    }

    [Fact]
    public async Task Server_roots_list_is_answered_from_client_options_including_optional_names()
    {
        var options = new McpClientOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(5),
            Roots = [new McpRoot("file:///work/app", "app"), new McpRoot("file:///work/lib")],
        };
        var (client, transport) = await Clients.ConnectLoopbackAsync(options: options);
        await using var connection = client;

        transport.Push(JsonRpc.Request("7", McpProtocol.RootsList));

        Assert.True(await Eventually.Async(() => transport.Sent.Any(message => message.Contains("\"id\":7,\"result\""))));
        using var document = JsonDocument.Parse(transport.Sent.Single(message => message.Contains("\"id\":7,\"result\"")));
        var roots = document.RootElement.GetProperty("result").GetProperty("roots");
        Assert.Equal(2, roots.GetArrayLength());
        Assert.Equal("file:///work/app", roots[0].GetProperty("uri").GetString());
        Assert.Equal("app", roots[0].GetProperty("name").GetString());
        Assert.False(roots[1].TryGetProperty("name", out _));
    }

    [Theory]
    [InlineData(McpProtocol.SamplingCreateMessage)]
    [InlineData(McpProtocol.ElicitationCreate)]
    [InlineData("roots/unknown")]
    public async Task Unsupported_server_requests_are_answered_with_method_not_found(string method)
    {
        var (client, transport) = await Clients.ConnectLoopbackAsync();
        await using var connection = client;

        transport.Push(JsonRpc.Request("\"req-9\"", method, "{}"));

        Assert.True(await Eventually.Async(() => transport.Sent.Any(message => message.Contains("\"id\":\"req-9\""))));
        using var document = JsonDocument.Parse(transport.Sent.Single(message => message.Contains("\"id\":\"req-9\"")));
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(McpErrorCodes.MethodNotFound, error.GetProperty("code").GetInt32());
        Assert.Contains(method, error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Server_notifications_raise_typed_change_events_and_the_generic_notification_hook()
    {
        var (client, transport) = await Clients.ConnectLoopbackAsync();
        await using var connection = client;
        var tools = 0;
        var resources = 0;
        var prompts = 0;
        var received = new List<McpNotification>();
        client.ToolsChanged += () => Interlocked.Increment(ref tools);
        client.ResourcesChanged += () => Interlocked.Increment(ref resources);
        client.PromptsChanged += () => Interlocked.Increment(ref prompts);
        client.NotificationReceived += received.Add;

        transport.Push(JsonRpc.Notification(McpProtocol.ToolsListChanged));
        transport.Push(JsonRpc.Notification(McpProtocol.ResourcesListChanged));
        transport.Push(JsonRpc.Notification(McpProtocol.PromptsListChanged));
        transport.Push(JsonRpc.Notification(McpProtocol.LogMessage, """{"level":"info","data":"hello"}"""));
        transport.Push(JsonRpc.Notification("notifications/unknown"));

        Assert.True(await Eventually.Async(() => received.Count == 5));
        Assert.Equal(1, tools);
        Assert.Equal(1, resources);
        Assert.Equal(1, prompts);
        Assert.Equal(McpProtocol.LogMessage, received[3].Method);
        Assert.Equal("""{"level":"info","data":"hello"}""", received[3].ParamsJson);
        Assert.Equal("notifications/unknown", received[4].Method);
    }

    [Fact]
    public async Task Notification_without_params_reports_an_empty_object()
    {
        var (client, transport) = await Clients.ConnectLoopbackAsync();
        await using var connection = client;
        var received = new List<McpNotification>();
        client.NotificationReceived += received.Add;

        transport.Push(JsonRpc.Notification("notifications/custom"));

        Assert.True(await Eventually.Async(() => received.Count == 1));
        Assert.Equal("{}", received[0].ParamsJson);
    }
}
