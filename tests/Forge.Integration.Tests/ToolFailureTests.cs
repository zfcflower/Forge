using System.Diagnostics;
using Forge.HostClient;
using Xunit;

namespace Forge.Integration.Tests;

public sealed class ToolFailureTests
{
    [Fact]
    public async Task Throwing_tool_returns_error_result_and_the_worker_keeps_serving()
    {
        await using var fx = new E2eFixture();
        await fx.StartWithApprovedPluginAsync("alpha", "a", PluginSources.AllTools.Length);

        var pidBefore = await fx.WorkerPidAsync("alpha");

        var thrown = await fx.InvokeAsync("alpha", "throw", """{"text":"kaboom"}""");
        Assert.True(thrown.IsError);
        Assert.Contains("boom:kaboom", thrown.Content, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", thrown.Content, StringComparison.Ordinal);

        await fx.AssertToolContentAsync("alpha", "echo", """{"text":"after"}""", "a:after");
        Assert.Equal(pidBefore, await fx.WorkerPidAsync("alpha"));
        Assert.True(E2eFixture.IsRunning(pidBefore));
    }

    [Fact]
    public async Task Hung_tool_is_killed_at_the_host_deadline_and_the_next_call_runs_on_a_restarted_worker()
    {
        await using var fx = new E2eFixture(hostArguments: ["--tool-timeout", "2"]);
        await fx.StartWithApprovedPluginAsync("alpha", "a", PluginSources.AllTools.Length);

        var hungPid = await fx.WorkerPidAsync("alpha");

        var clock = Stopwatch.StartNew();
        var hung = await fx.InvokeAsync("alpha", "sleep", """{"ms":60000}""");
        clock.Stop();
        Assert.True(hung.IsError);
        Assert.Contains("exceeded", hung.Content, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the hung call took {clock.Elapsed}");

        await fx.WaitForExitAsync(hungPid, "the killed worker");

        await fx.AssertToolContentAsync("alpha", "echo", """{"text":"next"}""", "a:next");

        var restartedPid = await fx.WorkerPidAsync("alpha");
        Assert.NotEqual(hungPid, restartedPid);
        Assert.Equal(PluginSources.AllTools.Length, fx.ToolsOf("alpha").Count);
    }

    [Fact]
    public async Task Tool_slower_than_the_host_deadline_is_not_killed_when_it_finishes_in_time()
    {
        await using var fx = new E2eFixture(hostArguments: ["--tool-timeout", "10"]);
        await fx.StartWithApprovedPluginAsync("alpha", "a", PluginSources.AllTools.Length);

        var pid = await fx.WorkerPidAsync("alpha");
        await fx.AssertToolContentAsync("alpha", "sleep", """{"ms":200}""", "slept:200");
        Assert.Equal(pid, await fx.WorkerPidAsync("alpha"));
    }
}
