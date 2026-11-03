using Forge.Ipc;
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
        var window = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runningTransitions = 0;
        client.StatusChanged += (status, detail) =>
        {
            if (status == HostStatus.Running && Interlocked.Increment(ref runningTransitions) == 2)
            {
                if (HoldsUntilTheWindowRegistrationLanded(host))
                {
                    window.TrySetResult();
                }
                else
                {
                    window.TrySetException(new TimeoutException(
                        "The on-accept registration did not reach the client before the Running transition finished."));
                }
            }

            statuses.Add((status, detail));
        };
        await client.StartConnectedAsync(host);
        await host.NotifyClientAsync("tools.register", Register("alpha", "a1"));
        await host.NotifyClientAsync("tools.register", Register("beta", "b1"));
        await host.SyncAsync();
        Assert.Equal(
            new[] { "a1", "b1" },
            client.Tools.Select(tool => tool.Definition.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        await host.DropConnectionAsync();
        await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));

        host.NotifyOnAccept("tools.register", Register("gamma", "g1"));
        var accepting = host.AcceptAsync();
        using var limit = TestLimit.Create();
        await client.StartAsync(limit.Token).Bounded();
        await accepting.Bounded();
        await window.Task.Bounded();

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

    [Fact]
    public async Task Recovery_ends_the_previous_generations_host_process_and_StopAsync_leaves_no_process_behind()
    {
        var directory = SleeperHost.NewDirectory();
        await using var host = new FakeHost();
        var options = new ExtensionHostClientOptions { ProcessExitTimeout = TimeSpan.FromSeconds(1) };
        await using var client = new ExtensionHostClient(SleeperHost.Connection(host, directory), options: options);
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        try
        {
            using var limit = TestLimit.Create();
            var accepting = host.AcceptAsync();
            await client.StartAsync(limit.Token).Bounded();
            await accepting.Bounded();
            var firstHost = client.HostProcessId ??
                throw new InvalidOperationException("The client did not start a host process.");
            Assert.True(SleeperHost.IsRunning(firstHost));

            await host.DropConnectionAsync();
            await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));
            Assert.True(
                SleeperHost.IsRunning(firstHost),
                "The first host process outlived its connection; recovery has to end it.");

            var acceptingAgain = host.AcceptAsync();
            await client.StartAsync(limit.Token).Bounded();
            await acceptingAgain.Bounded();
            var secondHost = client.HostProcessId ??
                throw new InvalidOperationException("The client did not start a host process.");
            Assert.NotEqual(firstHost, secondHost);
            await SleeperHost.WaitForExitAsync(firstHost);
            Assert.False(SleeperHost.IsRunning(firstHost));
            Assert.True(SleeperHost.IsRunning(secondHost));

            await client.StopAsync(limit.Token).Bounded();
            await SleeperHost.WaitForExitAsync(secondHost);

            Assert.False(SleeperHost.IsRunning(firstHost));
            Assert.False(SleeperHost.IsRunning(secondHost));
            Assert.Null(client.HostProcessId);
            Assert.Equal(HostStatus.Stopped, client.Status);
            Assert.Single(host.Requests("host.stop"));
        }
        finally
        {
            await client.DisposeAsync();
            SleeperHost.DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Recovery_keeps_the_new_connection_usable_and_reports_no_Failed_after_it_starts_again()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        await client.StartConnectedAsync(host);
        await host.NotifyClientAsync("tools.register", Register("alpha", "a1"));
        await host.SyncAsync();
        await host.DropConnectionAsync();
        await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));
        var failuresAfterTheCrash = statuses.Snapshot().Count(entry => entry.Status == HostStatus.Failed);
        using var limit = TestLimit.Create();

        var accepting = host.AcceptAsync();
        await client.StartAsync(limit.Token).Bounded();
        await accepting.Bounded();

        Assert.Equal(HostStatus.Running, client.Status);
        Assert.Equal(failuresAfterTheCrash, statuses.Snapshot().Count(entry => entry.Status == HostStatus.Failed));
        await client.RefreshPluginsAsync(limit.Token);
        Assert.Single(host.Requests("plugins.refresh"));
        await host.NotifyClientAsync("tools.register", Register("gamma", "g1"));
        await host.SyncAsync();
        Assert.Equal("g1", Assert.Single(client.Tools).Definition.Name);
    }

    [Fact]
    public async Task A_start_that_times_out_reports_exactly_one_Failed_status()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host, startTimeout: TimeSpan.FromMilliseconds(300));
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<TimeoutException>(() => client.StartAsync(limit.Token));

        Assert.Equal(HostStatus.Failed, client.Status);
        var events = await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));
        Assert.Single(events, entry => entry.Status == HostStatus.Failed);
        Assert.Equal(HostStatus.Failed, events[^1].Status);
    }

    /// <summary>Blocks the caller until the host has accepted the recovering connection, sent its on-accept
    /// registration, and the client has processed it.</summary>
    private static bool HoldsUntilTheWindowRegistrationLanded(FakeHost host)
    {
        try
        {
            return host.WhenAcceptedAsync(2).Wait(TestLimit.Span) && ClientProcessedTheRegistration(host);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ClientProcessedTheRegistration(FakeHost host)
    {
        using var limit = TestLimit.Create();
        try
        {
            host.CallClientAsync("sync", null, limit.Token).GetAwaiter().GetResult();
            return false;
        }
        catch (IpcRemoteException error)
        {
            return string.Equals(error.Code, "unsupported", StringComparison.Ordinal);
        }
    }

    private static string Register(string pluginId, string tool) =>
        $$"""{"pluginId":"{{pluginId}}","tools":[{"name":"{{tool}}","description":"d","inputSchemaJson":"{}","sideEffect":0}]}""";
}
