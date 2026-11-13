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

        await host.DropAndAwaitFailedAsync(statuses);

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
        await using var client = await host.StartTrackedAsync(out var statuses);
        await host.NotifyClientAsync("tools.register", Register("alpha", "a1"));
        await host.NotifyClientAsync("tools.register", Register("beta", "b1"));
        await host.SyncAsync();
        Assert.Equal(2, client.Tools.Count);

        await host.DropAndAwaitFailedAsync(statuses);
        var changes = client.TrackToolsChanged();
        using var limit = TestLimit.Create();

        await client.ReconnectAsync(host, limit.Token);

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
        await using var client = await host.StartTrackedAsync(out var statuses, startTimeout: TimeSpan.FromSeconds(10));
        await host.NotifyAndSyncAsync("tools.register", Register("alpha", "a1"));
        await host.DropAndAwaitFailedAsync(statuses);
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
        var statuses = client.TrackStatus();
        try
        {
            using var limit = TestLimit.Create();
            await client.ReconnectAsync(host, limit.Token);
            var firstHost = client.HostProcessId ??
                throw new InvalidOperationException("The client did not start a host process.");
            Assert.True(SleeperHost.IsRunning(firstHost));

            await host.DropAndAwaitFailedAsync(statuses);
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
        await using var client = await host.StartTrackedAsync(out var statuses);
        await host.NotifyAndSyncAsync("tools.register", Register("alpha", "a1"));
        await host.DropAndAwaitFailedAsync(statuses);
        var failuresAfterTheCrash = statuses.Snapshot().Count(entry => entry.Status == HostStatus.Failed);
        using var limit = TestLimit.Create();

        await client.ReconnectAsync(host, limit.Token);

        Assert.Equal(HostStatus.Running, client.Status);
        Assert.Equal(failuresAfterTheCrash, statuses.Snapshot().Count(entry => entry.Status == HostStatus.Failed));
        await client.RefreshPluginsAsync(limit.Token);
        Assert.Single(host.Requests("plugins.refresh"));
        await host.NotifyAndSyncAsync("tools.register", Register("gamma", "g1"));
        Assert.Equal("g1", Assert.Single(client.Tools).Definition.Name);
    }

    [Fact]
    public async Task A_start_that_times_out_reports_exactly_one_Failed_status()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host, startTimeout: TimeSpan.FromMilliseconds(300));
        var statuses = client.TrackStatus();
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<TimeoutException>(() => client.StartAsync(limit.Token));

        Assert.Equal(HostStatus.Failed, client.Status);
        var events = await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));
        Assert.Single(events, entry => entry.Status == HostStatus.Failed);
        Assert.Equal(HostStatus.Failed, events[^1].Status);
    }

#if DEBUG
    /// <summary>A host that drops the connection inside the connect-to-Running window leaves the client Failed and the
    /// start failed; the same instance starts again against a healthy host.</summary>
    [Fact]
    public async Task A_connection_that_closes_before_the_Running_commit_fails_the_start_and_stays_startable()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        var statuses = client.TrackStatus();
        using var limit = TestLimit.Create();
        client.AfterConnectedHook = async () =>
        {
            await host.WhenAcceptedAsync(1).WaitAsync(TestLimit.Span).ConfigureAwait(false);
            await host.DropConnectionAsync().ConfigureAwait(false);
            await statuses.UntilAsync(seen => seen.Any(entry => entry.Status == HostStatus.Failed))
                .ConfigureAwait(false);
        };

        var accepting = host.AcceptAsync();
        var starting = client.StartAsync(limit.Token);
        await accepting.Bounded();
        var failed = await Assert.ThrowsAsync<IpcConnectionClosedException>(() => starting);

        Assert.Contains(host.PipeName, failed.Message, StringComparison.Ordinal);
        Assert.Equal(HostStatus.Failed, client.Status);
        var events = statuses.Snapshot();
        Assert.Single(events, entry => entry.Status == HostStatus.Failed);
        Assert.Equal(HostStatus.Failed, events[^1].Status);
        Assert.DoesNotContain(events, entry => entry.Status == HostStatus.Running);

        client.AfterConnectedHook = null;
        var acceptingAgain = host.AcceptAsync();
        await client.StartAsync(limit.Token).Bounded();
        await acceptingAgain.Bounded();

        Assert.Equal(HostStatus.Running, client.Status);
        await client.RefreshPluginsAsync(limit.Token);
        Assert.Single(host.Requests("plugins.refresh"));
    }
#endif

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
