using Xunit;

namespace Forge.HostClient.Tests;

public sealed class ExtensionHostClientRecoveryTests
{
    [Fact]
    public async Task Recovery_keeps_a_registration_from_the_connect_to_Running_window_and_drops_what_the_new_host_omits()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        await client.StartConnectedAsync(host);
        await host.NotifyClientAsync("tools.register", Register("alpha", "a1"));
        await host.NotifyClientAsync("tools.register", Register("beta", "b1"));
        await host.SyncAsync();
        Assert.Equal(new[] { "a1", "b1" }, client.Tools.Select(tool => tool.Definition.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        await host.DropConnectionAsync();
        await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));

        host.NotifyOnAccept("tools.register", Register("gamma", "g1"));
        var accepting = host.AcceptAsync();
        using var limit = TestLimit.Create();
        await client.StartAsync(limit.Token).Bounded();
        await accepting.Bounded();
        await host.SyncAsync();

        Assert.Equal(HostStatus.Running, client.Status);
        Assert.Equal("g1", Assert.Single(client.Tools).Definition.Name);
        Assert.Equal(new[] { "gamma" }, client.ToolsByPlugin.Keys.ToArray());
    }

    [Fact]
    public async Task Recovery_transition_drops_every_stale_entry_with_exactly_one_ToolsChanged()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        await client.StartConnectedAsync(host);
        await host.NotifyClientAsync("tools.register", Register("alpha", "a1"));
        await host.NotifyClientAsync("tools.register", Register("beta", "b1"));
        await host.SyncAsync();
        Assert.Equal(2, client.Tools.Count);

        await host.DropConnectionAsync();
        await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));
        var changes = new EventLog<int>();
        client.ToolsChanged += () => changes.Add(1);
        using var limit = TestLimit.Create();

        var accepting = host.AcceptAsync();
        await client.StartAsync(limit.Token).Bounded();
        await accepting.Bounded();

        Assert.Equal(HostStatus.Running, client.Status);
        Assert.Empty(client.Tools);
        Assert.Empty(client.ToolsByPlugin);
        Assert.Single(changes.Snapshot());
        Assert.Equal((HostStatus.Running, (string?)null), statuses.Snapshot()[^1]);
    }

    [Fact]
    public async Task Stop_during_recovery_with_a_window_registration_still_leaves_the_client_stopped_with_no_tools()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host, startTimeout: TimeSpan.FromSeconds(10));
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        await client.StartConnectedAsync(host);
        await host.NotifyClientAsync("tools.register", Register("alpha", "a1"));
        await host.SyncAsync();
        await host.DropConnectionAsync();
        await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));
        host.NotifyOnAccept("tools.register", Register("gamma", "g1"));
        using var limit = TestLimit.Create();

        var recovering = client.StartAsync(limit.Token);
        await statuses.UntilAsync(seen => seen.Count(e => e.Status == HostStatus.Starting) == 2);
        var stopping = client.StopAsync(limit.Token);
        var accepting = host.AcceptAsync();
        await recovering.Bounded();
        await accepting.Bounded();
        await stopping.Bounded();

        Assert.Equal(HostStatus.Stopped, client.Status);
        Assert.Empty(client.Tools);
        Assert.Empty(client.ToolsByPlugin);
        Assert.Equal(HostStatus.Stopped, statuses.Snapshot()[^1].Status);
    }

    private static string Register(string pluginId, string tool) =>
        $$"""{"pluginId":"{{pluginId}}","tools":[{"name":"{{tool}}","description":"d","inputSchemaJson":"{}","sideEffect":0}]}""";
}
