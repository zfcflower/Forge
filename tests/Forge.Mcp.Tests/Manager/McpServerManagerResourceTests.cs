using System.Collections.Concurrent;
using Forge.Mcp.Tests.Fixtures;
using Xunit;

namespace Forge.Mcp.Tests.Manager;

/// <summary>
/// The resource-subscription surface of <see cref="McpServerManager"/>: the capability gate, update forwarding, and the fact
/// that a subscription belongs to one session — the manager keeps the set the user asked for and replays it onto every new
/// session, so a reconnect does not silently stop the update notifications.
/// </summary>
public sealed class McpServerManagerResourceTests
{
    private const string SubscribeCapability =
        """{"protocolVersion":"2025-06-18","capabilities":{"resources":{"subscribe":true,"listChanged":true}},"serverInfo":{"name":"fake","version":"1.2.3"}}""";

    private static ScriptedServer WithSubscriptions() =>
        new ScriptedServer()
            .On(McpProtocol.Initialize, _ => SubscribeCapability)
            .On(McpProtocol.ResourcesSubscribe, _ => JsonRpc.EmptyObject)
            .On(McpProtocol.ResourcesUnsubscribe, _ => JsonRpc.EmptyObject);

    [Fact]
    public async Task Subscribing_to_a_server_without_the_capability_fails_with_a_readable_error()
    {
        await using var fixture = new ManagerFixture(ManagerFixture.Of(ManagerFixture.Stdio("srv")));

        var exception = await Assert.ThrowsAsync<McpException>(
            () => fixture.Manager.SubscribeAsync("srv", "res://x", CancellationToken.None));

        Assert.Contains("srv", exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not support resource subscriptions", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(McpProtocol.ResourcesSubscribe, string.Join('\n', fixture.Transports[0].Sent));
    }

    [Fact]
    public async Task Subscribe_unsubscribe_and_updates_reach_the_manager_with_the_server_name()
    {
        await using var fixture = new ManagerFixture(ManagerFixture.Of(ManagerFixture.Stdio("srv")), _ => WithSubscriptions());
        var updates = new ConcurrentQueue<(string Server, string Uri)>();
        fixture.Manager.ResourceUpdated += (name, update) => updates.Enqueue((name, update.Uri));

        await fixture.Manager.SubscribeAsync("srv", "res://x", CancellationToken.None);
        var transport = Assert.Single(fixture.Transports);
        Assert.Contains(transport.Sent, payload => payload.Contains(McpProtocol.ResourcesSubscribe, StringComparison.Ordinal) &&
                                                   payload.Contains("res://x", StringComparison.Ordinal));

        transport.Push(JsonRpc.Notification(McpProtocol.ResourcesUpdated, """{"uri":"res://x"}"""));
        transport.Push(JsonRpc.Notification(McpProtocol.ResourcesUpdated, """{"uri":"res://not-subscribed"}"""));
        Assert.True(await Eventually.Async(() => updates.Count == 2, TimeSpan.FromSeconds(10)));
        Assert.Equal(
            new[] { ("srv", "res://x"), ("srv", "res://not-subscribed") },
            updates.ToArray());

        // An unsubscribe for a uri that was never subscribed is a no-op, and unsubscribing twice does not throw.
        await fixture.Manager.UnsubscribeAsync("srv", "res://never", CancellationToken.None);
        await fixture.Manager.UnsubscribeAsync("srv", "res://x", CancellationToken.None);
        await fixture.Manager.UnsubscribeAsync("srv", "res://x", CancellationToken.None);
        Assert.True(fixture.Manager.GetState("srv").IsReady);
    }

    private static int SubscribeRequests(LoopbackTransport transport) =>
        transport.Sent.Count(payload => payload.Contains(McpProtocol.ResourcesSubscribe, StringComparison.Ordinal));

    [Fact]
    public async Task A_reconnect_replays_the_subscriptions_the_manager_keeps()
    {
        await using var fixture = new ManagerFixture(
            ManagerFixture.Of(ManagerFixture.Stdio("srv")),
            _ => WithSubscriptions(),
            initialReconnectDelay: TimeSpan.FromMilliseconds(20));
        await fixture.Manager.StartAsync("srv", CancellationToken.None);
        await fixture.Manager.SubscribeAsync("srv", "res://x", CancellationToken.None);
        var transport = Assert.Single(fixture.Transports);
        Assert.Equal(1, SubscribeRequests(transport));
        Assert.True(fixture.Manager.IsSubscribed("srv", "res://x"));

        transport.Drop(new McpTransportException("connection lost"));
        Assert.True(await Eventually.Async(() => transport.StartCount == 2, TimeSpan.FromSeconds(10)));
        Assert.True(await Eventually.Async(() => fixture.Manager.GetState("srv").IsReady, TimeSpan.FromSeconds(10)));

        // A subscription belongs to the MCP session that sent it, so the reconnected client starts with none. The manager keeps
        // the set the user asked for and replays it, so a dropped connection never leaves the resource browser subscribed only in
        // its own memory while the server sends no more updates.
        Assert.True(await Eventually.Async(() => SubscribeRequests(transport) == 2, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task An_unsubscribed_resource_is_dropped_from_the_set_a_reconnect_replays()
    {
        await using var fixture = new ManagerFixture(ManagerFixture.Of(ManagerFixture.Stdio("srv")), _ => WithSubscriptions());
        await fixture.Manager.StartAsync("srv", CancellationToken.None);
        await fixture.Manager.SubscribeAsync("srv", "res://x", CancellationToken.None);
        await fixture.Manager.SubscribeAsync("srv", "res://y", CancellationToken.None);
        Assert.Equal(["res://x", "res://y"], fixture.Manager.SubscriptionsOf("srv").Order(StringComparer.Ordinal));

        await fixture.Manager.UnsubscribeAsync("srv", "res://y", CancellationToken.None);

        Assert.Equal(["res://x"], fixture.Manager.SubscriptionsOf("srv"));
        Assert.False(fixture.Manager.IsSubscribed("srv", "res://y"));
    }
}
