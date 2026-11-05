using System.Diagnostics;
using Forge.Ipc;
using Xunit;

namespace Forge.HostClient.Tests;

public sealed class ExtensionHostClientLifecycleTests
{
    [Fact]
    public async Task New_client_is_stopped_with_no_tools_and_no_host_process()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);

        Assert.Equal(HostStatus.Stopped, client.Status);
        Assert.Empty(client.Tools);
        Assert.Empty(client.ToolsByPlugin);
        Assert.Null(client.HostProcessId);
        Assert.Null(client.HostExitCode);
    }

    [Fact]
    public async Task Start_connects_to_a_running_host_and_reports_Starting_then_Running()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));

        await client.StartConnectedAsync(host);
        var seen = await statuses.UntilAsync(events => events.Any(e => e.Status == HostStatus.Running));

        Assert.Equal(HostStatus.Running, client.Status);
        Assert.Equal([HostStatus.Starting, HostStatus.Running], seen.Select(e => e.Status));
        Assert.Equal($"Connecting to the Extension Host on pipe '{host.PipeName}'.", seen[0].Detail);
        Assert.Null(seen[1].Detail);
        Assert.Null(client.HostProcessId);
        // This client only connected to an already-running host, so it has no exit code to report.
        Assert.Null(client.HostExitCode);
    }

    [Fact]
    public async Task Start_while_running_is_rejected_and_keeps_the_connection()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync(CancellationToken.None));

        Assert.Equal(HostStatus.Running, client.Status);
        using var limit = TestLimit.Create();
        await client.RefreshPluginsAsync(limit.Token);
        Assert.Single(host.Requests("plugins.refresh"));
    }

    [Fact]
    public async Task Start_against_a_missing_host_times_out_and_reports_Failed()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host, startTimeout: TimeSpan.FromMilliseconds(300));
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<TimeoutException>(() => client.StartAsync(limit.Token));

        Assert.Equal(HostStatus.Failed, client.Status);
        var events = await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));
        var failure = events.Last(e => e.Status == HostStatus.Failed);
        Assert.NotNull(failure.Detail);
        Assert.Contains(host.PipeName, failure.Detail);
    }

    [Fact]
    public async Task Start_after_a_failed_start_recovers_on_the_same_instance()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host, startTimeout: TimeSpan.FromMilliseconds(500));
        using var limit = TestLimit.Create();
        await Assert.ThrowsAsync<TimeoutException>(() => client.StartAsync(limit.Token));
        Assert.Equal(HostStatus.Failed, client.Status);

        await client.StartConnectedAsync(host);

        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Start_with_an_invalid_pipe_name_throws_before_any_connection_attempt()
    {
        await using var client = new ExtensionHostClient(new HostConnection { PipeName = "bad/name" });
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<ArgumentException>(() => client.StartAsync(limit.Token));
    }

    [Fact]
    public async Task Caller_cancel_during_start_returns_to_Stopped_and_a_later_start_succeeds()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host, startTimeout: TimeSpan.FromSeconds(30));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.StartAsync(cancel.Token));

        Assert.Equal(HostStatus.Stopped, client.Status);
        await client.StartConnectedAsync(host);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Stop_before_start_is_a_no_op_that_raises_no_status_events()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        using var limit = TestLimit.Create();

        await client.StopAsync(limit.Token);

        Assert.Equal(HostStatus.Stopped, client.Status);
        Assert.Empty(statuses.Snapshot());
        Assert.Empty(host.Requests("host.stop"));
    }

    [Fact]
    public async Task Stop_sends_host_stop_and_reports_Stopped_without_a_failure()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        await client.StopAsync(limit.Token);

        Assert.Equal(HostStatus.Stopped, client.Status);
        Assert.Single(host.Requests("host.stop"));
        var events = statuses.Snapshot();
        Assert.DoesNotContain(events, e => e.Status == HostStatus.Failed);
        Assert.Equal((HostStatus.Stopped, (string?)null), events[^1]);
        Assert.Null(client.HostExitCode);
    }

    [Fact]
    public async Task Host_that_ignores_host_stop_is_torn_down_within_the_stop_timeout()
    {
        await using var host = new FakeHost();
        host.On("host.stop", FakeHost.NeverAnswer);
        var options = new ExtensionHostClientOptions { StopRequestTimeout = TimeSpan.FromMilliseconds(300) };
        await using var client = ClientFactory.Create(host, options: options);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var stopwatch = Stopwatch.StartNew();
        await client.StopAsync(limit.Token);

        Assert.Equal(HostStatus.Stopped, client.Status);
        Assert.Single(host.Requests("host.stop"));
        Assert.True(stopwatch.Elapsed < TestLimit.Span, "StopAsync must not wait for the host beyond the stop timeout.");
    }

    [Fact]
    public async Task Dispose_of_a_never_started_client_completes_and_is_idempotent()
    {
        await using var host = new FakeHost();
        var client = ClientFactory.Create(host);

        await client.DisposeAsync().AsTask().Bounded();
        await client.DisposeAsync().AsTask().Bounded();
    }

    [Fact]
    public async Task Dispose_of_a_running_client_stops_the_host_and_rejects_later_use()
    {
        await using var host = new FakeHost();
        var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);

        await client.DisposeAsync().AsTask().Bounded();

        Assert.Equal(HostStatus.Stopped, client.Status);
        Assert.Single(host.Requests("host.stop"));
        using var limit = TestLimit.Create();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.StartAsync(limit.Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.StopAsync(limit.Token));
        await client.DisposeAsync().AsTask().Bounded();
    }

    [Fact]
    public async Task Dispose_is_bounded_when_the_host_ignores_host_stop()
    {
        await using var host = new FakeHost();
        host.On("host.stop", FakeHost.NeverAnswer);
        var options = new ExtensionHostClientOptions { StopRequestTimeout = TimeSpan.FromMilliseconds(300) };
        var client = ClientFactory.Create(host, options: options);
        await client.StartConnectedAsync(host);

        await client.DisposeAsync().AsTask().Bounded();

        Assert.Equal(HostStatus.Stopped, client.Status);
    }

    [Fact]
    public async Task Host_that_becomes_ready_late_is_reported_as_Starting_then_Running_without_Failed()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host, startTimeout: TimeSpan.FromSeconds(10));
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        using var limit = TestLimit.Create();

        var starting = client.StartAsync(limit.Token);
        await statuses.UntilAsync(events => events.Any(e => e.Status == HostStatus.Starting));
        var accepting = host.AcceptAsync();
        await starting.Bounded();
        await accepting.Bounded();

        var sequence = statuses.Snapshot().Select(e => e.Status).ToArray();
        Assert.Equal([HostStatus.Starting, HostStatus.Running], sequence);
    }

    [Fact]
    public async Task Start_timeout_reports_the_timeout_as_the_failure_detail()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host, startTimeout: TimeSpan.FromMilliseconds(300));
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<TimeoutException>(() => client.StartAsync(limit.Token));

        var events = await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));
        var failure = events.Last(e => e.Status == HostStatus.Failed);
        Assert.NotNull(failure.Detail);
        Assert.Contains("did not accept a connection", failure.Detail);
    }

    [Fact]
    public async Task Start_timeout_names_a_host_that_is_still_running_instead_of_reporting_a_crash()
    {
        await using var host = new FakeHost();
        var directory = SleeperHost.NewDirectory();
        var connection = SleeperHost.Connection(host, directory) with { StartTimeout = TimeSpan.FromSeconds(2) };
        await using var client = new ExtensionHostClient(connection);
        using var limit = TestLimit.Create();
        try
        {
            var failure = await Assert.ThrowsAsync<TimeoutException>(() => client.StartAsync(limit.Token));

            Assert.Contains("did not accept a connection", failure.Message);
            Assert.Contains("is still running as process", failure.Message);
        }
        finally
        {
            SleeperHost.DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Start_names_a_host_that_exited_before_it_listened_with_its_exit_code()
    {
        await using var host = new FakeHost();
        var (executable, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", (IReadOnlyList<string>)["/c", "exit 3"])
            : ("/bin/sh", (IReadOnlyList<string>)["-c", "exit 3"]);
        var connection = new HostConnection
        {
            PipeName = host.PipeName,
            HostExecutablePath = executable,
            RuntimePath = string.Empty,
            HostArguments = arguments,
            StartTimeout = TimeSpan.FromSeconds(2),
        };
        await using var client = new ExtensionHostClient(connection);
        using var limit = TestLimit.Create();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync(limit.Token));

        Assert.Contains("exited before accepting the connection", failure.Message);
        Assert.Contains("exited with code 3", failure.Message);
    }
}
