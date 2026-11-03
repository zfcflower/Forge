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
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");

        var pidBefore = await fx.WorkerPidAsync("alpha");

        var thrown = await fx.InvokeAsync("alpha", "throw", """{"text":"kaboom"}""");
        Assert.True(thrown.IsError);
        Assert.Contains("boom:kaboom", thrown.Content, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", thrown.Content, StringComparison.Ordinal);

        var echo = await fx.InvokeAsync("alpha", "echo", """{"text":"after"}""");
        Assert.False(echo.IsError, echo.Content);
        Assert.Equal("a:after", echo.Content);
        Assert.Equal(pidBefore, await fx.WorkerPidAsync("alpha"));
        Assert.True(E2eFixture.IsRunning(pidBefore));
    }

    [Fact]
    public async Task Hung_tool_is_killed_at_the_host_deadline_and_the_next_call_runs_on_a_restarted_worker()
    {
        await using var fx = new E2eFixture(hostArguments: ["--tool-timeout", "2"]);
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");

        var hungPid = await fx.WorkerPidAsync("alpha");

        var clock = Stopwatch.StartNew();
        var hung = await fx.InvokeAsync("alpha", "sleep", """{"ms":60000}""");
        clock.Stop();
        Assert.True(hung.IsError);
        Assert.Contains("exceeded", hung.Content, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the hung call took {clock.Elapsed}");

        await fx.WaitForExitAsync(hungPid, "the killed worker");

        var echo = await fx.InvokeAsync("alpha", "echo", """{"text":"next"}""");
        Assert.False(echo.IsError, echo.Content);
        Assert.Equal("a:next", echo.Content);

        var restartedPid = await fx.WorkerPidAsync("alpha");
        Assert.NotEqual(hungPid, restartedPid);
        Assert.Equal(PluginSources.AllTools.Length, fx.ToolsOf("alpha").Count);
    }

    [Fact]
    public async Task Tool_slower_than_the_host_deadline_is_not_killed_when_it_finishes_in_time()
    {
        await using var fx = new E2eFixture(hostArguments: ["--tool-timeout", "10"]);
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");

        var pid = await fx.WorkerPidAsync("alpha");
        var slept = await fx.InvokeAsync("alpha", "sleep", """{"ms":200}""");
        Assert.False(slept.IsError, slept.Content);
        Assert.Equal("slept:200", slept.Content);
        Assert.Equal(pid, await fx.WorkerPidAsync("alpha"));
    }
}
