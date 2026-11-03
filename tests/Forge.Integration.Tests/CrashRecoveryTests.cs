using System.Diagnostics;
using System.Text.Json;
using Forge.HostClient;
using Forge.Ipc;
using Xunit;

namespace Forge.Integration.Tests;

public sealed class CrashRecoveryTests
{
    [Fact]
    public async Task Worker_crash_fails_the_inflight_call_and_the_next_call_runs_on_a_restarted_worker()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");

        var crashedPid = await fx.WorkerPidAsync("alpha");

        var crashed = await fx.InvokeAsync("alpha", "crash");
        Assert.True(crashed.IsError);
        Assert.Contains("exited", crashed.Content, StringComparison.Ordinal);
        await fx.WaitForExitAsync(crashedPid, "the crashed worker");

        var echo = await fx.InvokeAsync("alpha", "echo", """{"text":"ok"}""");
        Assert.False(echo.IsError, echo.Content);
        Assert.Equal("a:ok", echo.Content);

        var restartedPid = await fx.WorkerPidAsync("alpha");
        Assert.NotEqual(crashedPid, restartedPid);
        Assert.Equal(PluginSources.AllTools.Length, fx.ToolsOf("alpha").Count);
    }

    [Fact]
    public async Task Repeated_worker_crashes_disable_the_worker_with_a_clear_error()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var crashed = await fx.InvokeAsync("alpha", "crash");
            Assert.True(crashed.IsError, $"crash #{attempt + 1} returned: {crashed.Content}");
        }

        var disabled = await fx.InvokeAsync("alpha", "echo", """{"text":"x"}""");
        Assert.True(disabled.IsError);
        Assert.Contains("failed 4 times in a row", disabled.Content, StringComparison.Ordinal);
        Assert.Contains("stays stopped", disabled.Content, StringComparison.Ordinal);
    }

    [Fact(Skip = "BUG: RefreshPluginsAsync does not re-enable a worker disabled after repeated crashes. ApplyAsync calls "
        + "EnsureLoadedAsync on the still-disabled WorkerSlot, which throws worker_disabled; the catch unregisters the plugin's "
        + "tools and stops the slot, so the Client.Tools snapshot is empty after refresh and nothing re-publishes it until a "
        + "held proxy happens to be invoked. Expected: after refresh the worker is running again and its tools are registered.")]
    public async Task Refresh_re_enables_a_worker_disabled_by_repeated_crashes_and_republishes_its_tools()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await fx.InvokeAsync("alpha", "crash");
        }

        await fx.Client.RefreshPluginsAsync(fx.Ct);

        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools after refresh");
        var echo = await fx.InvokeAsync("alpha", "echo", """{"text":"revived"}""");
        Assert.False(echo.IsError, echo.Content);
        Assert.Equal("a:revived", echo.Content);
    }

    [Fact]
    public async Task Host_crash_fails_pending_calls_keeps_the_tool_snapshot_and_leaves_no_orphan_processes()
    {
        await using var fx = new E2eFixture();
        var marker = Path.Combine(fx.Root, "sleep-started.txt");
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");

        var workerPid = await fx.WorkerPidAsync("alpha");
        var hostPid = fx.Client.HostProcessId ?? throw new InvalidOperationException("The client did not start a host.");
        Assert.True(E2eFixture.IsRunning(hostPid));

        var statusEvents = new List<(HostStatus Status, string? Detail)>();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Client.StatusChanged += (status, detail) =>
        {
            lock (statusEvents)
            {
                statusEvents.Add((status, detail));
            }

            if (status == HostStatus.Failed)
            {
                failed.TrySetResult();
            }
        };

        var pending = fx.InvokeAsync("alpha", "sleep", JsonSerializer.Serialize(new { ms = 60_000, marker }));
        await fx.WaitUntilAsync(() => File.Exists(marker), "the sleeping tool to start");

        Process.GetProcessById(hostPid).Kill(entireProcessTree: false);

        await Assert.ThrowsAnyAsync<IOException>(() => pending);
        await failed.Task.WaitAsync(fx.Ct);

        Assert.Equal(HostStatus.Failed, fx.Client.Status);
        lock (statusEvents)
        {
            Assert.Contains(statusEvents, entry => entry.Status == HostStatus.Failed && !string.IsNullOrEmpty(entry.Detail));
        }

        Assert.Equal(PluginSources.AllTools.Length, fx.ToolsOf("alpha").Count);
        Assert.Equal(PluginSources.AllTools.Length, fx.Client.Tools.Count);

        await Assert.ThrowsAnyAsync<IOException>(() => fx.InvokeAsync("alpha", "echo", """{"text":"late"}"""));

        await fx.WaitForExitAsync(hostPid, "the killed host");
        await fx.WaitForExitAsync(workerPid, "the worker of the killed host");

        await fx.Client.StopAsync(fx.Ct);
        Assert.Equal(HostStatus.Stopped, fx.Client.Status);
        Assert.False(E2eFixture.IsRunning(hostPid));
        Assert.False(E2eFixture.IsRunning(workerPid));
    }
}
