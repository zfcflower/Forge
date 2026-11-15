using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Mcp.Tests.Fixtures;
using Xunit;

namespace Forge.Mcp.Tests.Client;

public sealed class McpClientProgressAndSubscribeTests
{
    private static async Task<(McpClient Client, LoopbackTransport Transport)> ConnectAsync(ScriptedServer server)
    {
        var transport = new LoopbackTransport();
        transport.Responder = server.Respond;
        var client = new McpClient(transport, Clients.DefaultOptions());
        await client.ConnectAsync(CancellationToken.None);
        return (client, transport);
    }

    [Fact]
    public async Task Subscribe_and_unsubscribe_send_the_requests_and_track_the_uri()
    {
        var server = new ScriptedServer()
            .On(McpProtocol.ResourcesSubscribe, _ => JsonRpc.EmptyObject)
            .On(McpProtocol.ResourcesUnsubscribe, _ => JsonRpc.EmptyObject);
        var (client, _) = await ConnectAsync(server);

        await client.SubscribeAsync("res://x", CancellationToken.None);
        Assert.True(client.IsSubscribed("res://x"));
        await client.UnsubscribeAsync("res://x", CancellationToken.None);
        Assert.False(client.IsSubscribed("res://x"));

        Assert.Contains(McpProtocol.ResourcesSubscribe, server.RequestMethods);
        Assert.Contains(McpProtocol.ResourcesUnsubscribe, server.RequestMethods);
    }

    [Fact]
    public async Task Unsubscribe_is_idempotent_and_safe_without_a_prior_subscribe()
    {
        var server = new ScriptedServer()
            .On(McpProtocol.ResourcesSubscribe, _ => JsonRpc.EmptyObject)
            .On(McpProtocol.ResourcesUnsubscribe, _ => JsonRpc.EmptyObject);
        var (client, _) = await ConnectAsync(server);

        await client.UnsubscribeAsync("res://never-subscribed", CancellationToken.None);
        Assert.False(client.IsSubscribed("res://never-subscribed"));
        await client.SubscribeAsync("res://x", CancellationToken.None);
        await client.UnsubscribeAsync("res://x", CancellationToken.None);
        await client.UnsubscribeAsync("res://x", CancellationToken.None);

        Assert.False(client.IsSubscribed("res://x"));
        Assert.Equal(3, server.RequestMethods.Count(method => method == McpProtocol.ResourcesUnsubscribe));
    }

    [Fact]
    public async Task Resources_updated_notification_raises_the_resource_updated_event()
    {
        var (client, transport) = await ConnectAsync(new ScriptedServer());
        var updated = new ConcurrentQueue<string>();
        client.ResourceUpdated += change => updated.Enqueue(change.Uri);

        transport.Push(JsonRpc.Notification(McpProtocol.ResourcesUpdated, """{"uri":"res://x"}"""));

        Assert.True(await Eventually.Async(() => updated.Count == 1, TimeSpan.FromSeconds(10)));
        Assert.Equal("res://x", updated.Single());
    }

    [Fact]
    public async Task An_update_for_a_uri_nobody_subscribed_to_is_reported_without_claiming_a_subscription()
    {
        var (client, transport) = await ConnectAsync(new ScriptedServer());
        var updated = new ConcurrentQueue<string>();
        client.ResourceUpdated += change => updated.Enqueue(change.Uri);

        transport.Push(JsonRpc.Notification(McpProtocol.ResourcesUpdated, """{"uri":"res://unknown"}"""));
        // A payload with no uri is protocol noise: it must not raise an empty update.
        transport.Push(JsonRpc.Notification(McpProtocol.ResourcesUpdated, "{}"));
        transport.Push(JsonRpc.Notification(McpProtocol.ResourcesUpdated, """{"uri":"res://sentinel"}"""));

        Assert.True(await Eventually.Async(() => updated.Count == 2, TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { "res://unknown", "res://sentinel" }, updated.ToArray());
        Assert.False(client.IsSubscribed("res://unknown"));
    }

    [Fact]
    public async Task Progress_notification_routes_to_the_call_that_owns_the_token()
    {
        var transport = new LoopbackTransport();
        transport.Responder = root =>
        {
            if (JsonRpc.MethodOf(root.GetRawText()) == McpProtocol.ToolsCall)
            {
                var token = root.GetProperty("params").GetProperty("_meta").GetProperty("progressToken").GetString();
                transport.Push(JsonRpc.Notification(McpProtocol.Progress,
                    $$"""{"progressToken":"{{token}}","progress":5,"total":10,"message":"half"}"""));
                return [JsonRpc.Result(JsonRpc.IdOf(root), """{"content":[{"type":"text","text":"ok"}]}""")];
            }

            return new ScriptedServer().Respond(root);
        };
        var client = new McpClient(transport, Clients.DefaultOptions());
        await client.ConnectAsync(CancellationToken.None);

        var seen = new ConcurrentQueue<string>();
        var result = await client.CallToolAsync("echo", null, CancellationToken.None, progress: seen.Enqueue);

        Assert.Equal("ok", result.Text);
        Assert.Equal("[5/10] half", Assert.Single(seen));
    }

    [Theory]
    // The payload body only: the test inserts the progressToken in front of it.
    [InlineData("\"progress\":5,\"total\":10,\"message\":\"half\"", "[5/10] half")]
    [InlineData("\"progress\":5,\"total\":10,\"message\":\"\"", "[5/10]")]
    [InlineData("\"progress\":5,\"total\":10", "[5/10]")]
    [InlineData("\"progress\":3,\"message\":\"working\"", "working")]
    [InlineData("\"progress\":3", "")]
    [InlineData("\"progress\":2.5,\"total\":10,\"message\":\"quarter\"", "[2.5/10] quarter")]
    public async Task Progress_is_one_snapshot_with_or_without_a_total(string parameters, string expected)
    {
        var transport = new LoopbackTransport();
        string? token = null;
        string? id = null;
        var seen = new ConcurrentQueue<string>();
        transport.Responder = root =>
        {
            if (JsonRpc.MethodOf(root.GetRawText()) != McpProtocol.ToolsCall)
            {
                return new ScriptedServer().Respond(root);
            }

            token = root.GetProperty("params").GetProperty("_meta").GetProperty("progressToken").GetString();
            id = JsonRpc.IdOf(root);
            return [];
        };
        var client = new McpClient(transport, Clients.DefaultOptions());
        await client.ConnectAsync(CancellationToken.None);

        var call = client.CallToolAsync("echo", null, CancellationToken.None, progress: seen.Enqueue);
        Assert.True(await Eventually.Async(() => token is not null && id is not null, TimeSpan.FromSeconds(10)));
        var pending = token!;
        transport.Push(JsonRpc.Notification(McpProtocol.Progress,
            $$"""{"progressToken":"{{pending}}",{{parameters}}}"""));
        Assert.True(await Eventually.Async(() => seen.Count == 1, TimeSpan.FromSeconds(10)));
        transport.Push(JsonRpc.Result(id!, """{"content":[{"type":"text","text":"ok"}]}"""));

        Assert.Equal("ok", (await call).Text);
        Assert.Equal(expected, Assert.Single(seen));
    }

    [Fact]
    public async Task Progress_without_a_matching_token_falls_through_to_the_general_event()
    {
        var (client, transport) = await ConnectAsync(new ScriptedServer());
        var general = new ConcurrentQueue<string>();
        var routed = new ConcurrentQueue<string>();
        client.NotificationReceived += notification => general.Enqueue(notification.Method);

        transport.Push(JsonRpc.Notification(McpProtocol.Progress, """{"progress":3,"message":"untracked"}"""));
        transport.Push(JsonRpc.Notification(McpProtocol.Progress, """{"progressToken":"no-such-token","progress":7}"""));

        Assert.True(await Eventually.Async(() => general.Count == 2, TimeSpan.FromSeconds(10)));
        Assert.All(general, method => Assert.Equal(McpProtocol.Progress, method));
        Assert.Empty(routed);
    }

    [Fact]
    public async Task Progress_for_a_settled_call_falls_through_to_the_general_event()
    {
        var transport = new LoopbackTransport();
        var scripted = new ScriptedServer();
        string? token = null;
        transport.Responder = root =>
        {
            if (JsonRpc.MethodOf(root.GetRawText()) != McpProtocol.ToolsCall)
            {
                return scripted.Respond(root);
            }

            token = root.GetProperty("params").GetProperty("_meta").GetProperty("progressToken").GetString();
            return [JsonRpc.Result(JsonRpc.IdOf(root), """{"content":[{"type":"text","text":"ok"}]}""")];
        };
        var client = new McpClient(transport, Clients.DefaultOptions());
        await client.ConnectAsync(CancellationToken.None);
        var routed = new ConcurrentQueue<string>();
        var general = new ConcurrentQueue<string>();
        client.NotificationReceived += notification => general.Enqueue(notification.Method);

        var result = await client.CallToolAsync("echo", null, CancellationToken.None, progress: routed.Enqueue);
        var settled = token!;
        transport.Push(JsonRpc.Notification(McpProtocol.Progress,
            $$"""{"progressToken":"{{settled}}","progress":9,"total":10}"""));

        Assert.True(await Eventually.Async(() => general.Count == 1, TimeSpan.FromSeconds(10)));
        Assert.Equal("ok", result.Text);
        Assert.Empty(routed);
    }

    [Fact]
    public async Task Two_calls_on_one_connection_keep_their_progress_apart()
    {
        var transport = new LoopbackTransport();
        var scripted = new ScriptedServer();
        var pending = new ConcurrentDictionary<string, (string Id, string Token)>(StringComparer.Ordinal);
        transport.Responder = root =>
        {
            if (JsonRpc.MethodOf(root.GetRawText()) != McpProtocol.ToolsCall)
            {
                return scripted.Respond(root);
            }

            var name = root.GetProperty("params").GetProperty("name").GetString()!;
            var token = root.GetProperty("params").GetProperty("_meta").GetProperty("progressToken").GetString()!;
            pending[name] = (JsonRpc.IdOf(root), token);
            return [];
        };
        var client = new McpClient(transport, Clients.DefaultOptions());
        await client.ConnectAsync(CancellationToken.None);

        var first = new ConcurrentQueue<string>();
        var second = new ConcurrentQueue<string>();
        var firstCall = client.CallToolAsync("first", null, CancellationToken.None, first.Enqueue);
        var secondCall = client.CallToolAsync("second", null, CancellationToken.None, second.Enqueue);
        Assert.True(await Eventually.Async(() => pending.Count == 2, TimeSpan.FromSeconds(10)));
        var firstToken = pending["first"].Token;
        var secondToken = pending["second"].Token;
        Assert.NotEqual(firstToken, secondToken);

        transport.Push(JsonRpc.Notification(McpProtocol.Progress,
            $$"""{"progressToken":"{{secondToken}}","progress":2,"total":4,"message":"second"}"""));
        transport.Push(JsonRpc.Notification(McpProtocol.Progress,
            $$"""{"progressToken":"{{firstToken}}","progress":1,"total":8,"message":"first"}"""));
        Assert.True(await Eventually.Async(() => first.Count == 1 && second.Count == 1, TimeSpan.FromSeconds(10)));
        transport.Push(JsonRpc.Result(pending["first"].Id, """{"content":[{"type":"text","text":"first-ok"}]}"""));
        transport.Push(JsonRpc.Result(pending["second"].Id, """{"content":[{"type":"text","text":"second-ok"}]}"""));

        Assert.Equal("first-ok", (await firstCall).Text);
        Assert.Equal("second-ok", (await secondCall).Text);
        Assert.Equal("[1/8] first", Assert.Single(first));
        Assert.Equal("[2/4] second", Assert.Single(second));
    }

    [Fact]
    public async Task Two_connections_number_their_progress_tokens_independently()
    {
        var transportA = new LoopbackTransport("a");
        var transportB = new LoopbackTransport("b");
        var pendingA = new ConcurrentDictionary<string, (string Id, string Token)>(StringComparer.Ordinal);
        var pendingB = new ConcurrentDictionary<string, (string Id, string Token)>(StringComparer.Ordinal);
        transportA.Responder = Scripted(pendingA);
        transportB.Responder = Scripted(pendingB);
        var clientA = new McpClient(transportA, Clients.DefaultOptions());
        var clientB = new McpClient(transportB, Clients.DefaultOptions());
        await clientA.ConnectAsync(CancellationToken.None);
        await clientB.ConnectAsync(CancellationToken.None);

        var seenA = new ConcurrentQueue<string>();
        var seenB = new ConcurrentQueue<string>();
        var callA = clientA.CallToolAsync("echo", null, CancellationToken.None, seenA.Enqueue);
        var callB = clientB.CallToolAsync("echo", null, CancellationToken.None, seenB.Enqueue);
        Assert.True(await Eventually.Async(() => pendingA.Count == 1 && pendingB.Count == 1, TimeSpan.FromSeconds(10)));
        var tokenA = pendingA["echo"].Token;
        var tokenB = pendingB["echo"].Token;
        // Each connection numbers its own tokens from p1: the collision is exactly what this test is about.
        Assert.Equal(tokenA, tokenB);

        transportA.Push(JsonRpc.Notification(McpProtocol.Progress,
            $$"""{"progressToken":"{{tokenA}}","progress":1,"total":2,"message":"a"}"""));
        Assert.True(await Eventually.Async(() => seenA.Count == 1, TimeSpan.FromSeconds(10)));
        Assert.Empty(seenB);

        transportA.Push(JsonRpc.Result(pendingA["echo"].Id, """{"content":[{"type":"text","text":"ok"}]}"""));
        transportB.Push(JsonRpc.Result(pendingB["echo"].Id, """{"content":[{"type":"text","text":"ok"}]}"""));
        await callA;
        await callB;

        Assert.Equal("[1/2] a", Assert.Single(seenA));
        Assert.Empty(seenB);
    }

    [Fact]
    public async Task A_burst_of_progress_notifications_leaves_each_call_a_snapshot_and_the_last_one_final()
    {
        const int burst = 200;
        var transport = new LoopbackTransport();
        var scripted = new ScriptedServer();
        string? token = null;
        string? id = null;
        transport.Responder = root =>
        {
            if (JsonRpc.MethodOf(root.GetRawText()) != McpProtocol.ToolsCall)
            {
                return scripted.Respond(root);
            }

            token = root.GetProperty("params").GetProperty("_meta").GetProperty("progressToken").GetString();
            id = JsonRpc.IdOf(root);
            return [];
        };
        var client = new McpClient(transport, Clients.DefaultOptions());
        await client.ConnectAsync(CancellationToken.None);
        var seen = new ConcurrentQueue<string>();

        var call = client.CallToolAsync("echo", null, CancellationToken.None, seen.Enqueue);
        Assert.True(await Eventually.Async(() => token is not null && id is not null, TimeSpan.FromSeconds(10)));
        var pending = token!;
        for (var index = 1; index <= burst; index++)
        {
            transport.Push(JsonRpc.Notification(McpProtocol.Progress,
                $$"""{"progressToken":"{{pending}}","progress":{{index}},"total":200,"message":"step {{index}}"}"""));
        }

        Assert.True(await Eventually.Async(() => seen.Count == burst, TimeSpan.FromSeconds(20)));
        transport.Push(JsonRpc.Result(id!, """{"content":[{"type":"text","text":"done"}]}"""));
        Assert.Equal("done", (await call).Text);

        var lines = seen.ToArray();
        Assert.Equal(burst, lines.Length);
        // Every callback is one complete snapshot (never two prefixes glued together) and the last one is the final state.
        Assert.All(lines, line => Assert.Matches(@"^\[\d+/200\] step \d+$", line));
        Assert.Equal("[1/200] step 1", lines[0]);
        Assert.Equal("[200/200] step 200", lines[^1]);
    }

    private static Func<JsonElement, IEnumerable<string>> Scripted(
        ConcurrentDictionary<string, (string Id, string Token)> pending)
    {
        var scripted = new ScriptedServer();
        return root =>
        {
            if (JsonRpc.MethodOf(root.GetRawText()) != McpProtocol.ToolsCall)
            {
                return scripted.Respond(root);
            }

            var name = root.GetProperty("params").GetProperty("name").GetString()!;
            var token = root.GetProperty("params").GetProperty("_meta").GetProperty("progressToken").GetString()!;
            pending[name] = (JsonRpc.IdOf(root), token);
            return [];
        };
    }
}
