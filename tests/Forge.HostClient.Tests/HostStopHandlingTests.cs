using System.Diagnostics;
using Xunit;

namespace Forge.HostClient.Tests;

public sealed class HostStopHandlingTests
{
    [Fact]
    public async Task Acknowledged_stop_stops_cleanly_and_a_repeat_stop_sends_nothing()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        await client.StartConnectedAsync(host);
        await ToolFixtures.RegisterEchoAsync(client, host);
        var changes = new EventLog<int>();
        client.ToolsChanged += () => changes.Add(1);
        using var limit = TestLimit.Create();

        await client.StopAsync(limit.Token);

        Assert.Equal(HostStatus.Stopped, client.Status);
        Assert.Empty(client.Tools);
        Assert.Empty(client.ToolsByPlugin);
        Assert.Single(host.Requests("host.stop"));
        Assert.DoesNotContain(statuses.Snapshot(), entry => entry.Status == HostStatus.Failed);
        Assert.Equal((HostStatus.Stopped, (string?)null), statuses.Snapshot()[^1]);
        Assert.Single(changes.Snapshot());

        await client.StopAsync(limit.Token);

        Assert.Single(host.Requests("host.stop"));
        Assert.Equal(HostStatus.Stopped, client.Status);
    }

    [Fact]
    public async Task Unanswered_stop_still_sends_exactly_one_request_and_clears_the_tools()
    {
        await using var host = new FakeHost();
        host.On("host.stop", FakeHost.NeverAnswer);
        var options = new ExtensionHostClientOptions { StopRequestTimeout = TimeSpan.FromMilliseconds(300) };
        await using var client = ClientFactory.Create(host, options: options);
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        await client.StartConnectedAsync(host);
        await ToolFixtures.RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        await client.StopAsync(limit.Token).Bounded();

        Assert.Equal(HostStatus.Stopped, client.Status);
        Assert.Empty(client.Tools);
        Assert.Empty(client.ToolsByPlugin);
        Assert.Single(host.Requests("host.stop"));
        Assert.DoesNotContain(statuses.Snapshot(), entry => entry.Status == HostStatus.Failed);
        Assert.Equal((HostStatus.Stopped, (string?)null), statuses.Snapshot()[^1]);
    }

    [Fact]
    public async Task Acknowledged_stop_waits_the_exit_timeout_before_killing_a_host_that_stays_alive()
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-hc-stop-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);
        await using var host = new FakeHost();
        var options = new ExtensionHostClientOptions { ProcessExitTimeout = TimeSpan.FromSeconds(1) };
        await using var client = new ExtensionHostClient(SleeperConnection(host, directory), options: options);
        try
        {
            var accepting = host.AcceptAsync();
            using var limit = TestLimit.Create();
            await client.StartAsync(limit.Token);
            await accepting.Bounded();
            var pid = client.HostProcessId ?? throw new InvalidOperationException("The client did not start a host process.");
            Assert.True(IsRunning(pid));

            var clock = Stopwatch.StartNew();
            await client.StopAsync(limit.Token);
            clock.Stop();
            await WaitForExitAsync(pid);

            Assert.Equal(HostStatus.Stopped, client.Status);
            Assert.Single(host.Requests("host.stop"));
            Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(500),
                $"StopAsync returned after {clock.Elapsed} without waiting the exit timeout for the host to exit on its own.");
        }
        finally
        {
            await client.DisposeAsync();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Unanswered_stop_kills_the_host_without_waiting_for_self_exit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-hc-stop-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);
        await using var host = new FakeHost();
        host.On("host.stop", FakeHost.NeverAnswer);
        var options = new ExtensionHostClientOptions
        {
            StopRequestTimeout = TimeSpan.FromMilliseconds(300),
            ProcessExitTimeout = TimeSpan.FromSeconds(30),
        };
        await using var client = new ExtensionHostClient(SleeperConnection(host, directory), options: options);
        try
        {
            var accepting = host.AcceptAsync();
            using var limit = TestLimit.Create();
            await client.StartAsync(limit.Token);
            await accepting.Bounded();
            var pid = client.HostProcessId ?? throw new InvalidOperationException("The client did not start a host process.");
            Assert.True(IsRunning(pid));

            var clock = Stopwatch.StartNew();
            await client.StopAsync(limit.Token).Bounded();
            clock.Stop();
            await WaitForExitAsync(pid);

            Assert.Equal(HostStatus.Stopped, client.Status);
            Assert.Single(host.Requests("host.stop"));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(12),
                $"StopAsync took {clock.Elapsed} although the host never answered host.stop.");
        }
        finally
        {
            await client.DisposeAsync();
            DeleteDirectory(directory);
        }
    }

    private static HostConnection SleeperConnection(FakeHost host, string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(directory, "sleeper.cmd");
            File.WriteAllText(script, "@echo off\r\nping -n 60 127.0.0.1 > NUL\r\n");
            return new HostConnection
            {
                PipeName = host.PipeName,
                HostExecutablePath = "cmd.exe",
                RuntimePath = string.Empty,
                HostArguments = ["/c", script],
                StartTimeout = TimeSpan.FromSeconds(10),
            };
        }

        var shell = Path.Combine(directory, "sleeper.sh");
        File.WriteAllText(shell, "#!/bin/sh\nsleep 60\n");
        File.SetUnixFileMode(shell, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new HostConnection
        {
            PipeName = host.PipeName,
            HostExecutablePath = shell,
            RuntimePath = string.Empty,
            StartTimeout = TimeSpan.FromSeconds(10),
        };
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitForExitAsync(int processId)
    {
        using var limit = TestLimit.Create();
        while (IsRunning(processId))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), limit.Token);
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
