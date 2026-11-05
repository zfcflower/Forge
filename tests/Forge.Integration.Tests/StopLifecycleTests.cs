using Forge.HostClient;
using Xunit;

namespace Forge.Integration.Tests;

public sealed class StopLifecycleTests
{
    [Fact]
    public async Task Stop_ends_host_and_worker_processes_and_releases_the_pipe()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");

        var workerPid = await fx.WorkerPidAsync("alpha");
        var hostPid = fx.Client.HostProcessId ?? throw new InvalidOperationException("The client did not start a host.");
        Assert.True(E2eFixture.IsRunning(hostPid));
        Assert.True(E2eFixture.IsRunning(workerPid));

        await fx.Client.StopAsync(fx.Ct);

        Assert.Equal(HostStatus.Stopped, fx.Client.Status);
        Assert.Empty(fx.Client.Tools);
        Assert.Empty(fx.Client.ToolsByPlugin);
        await fx.WaitForExitAsync(hostPid, "the host");
        await fx.WaitForExitAsync(workerPid, "the worker");
        await fx.WaitUntilAsync(() => !E2eFixture.PipeAcceptsConnections(fx.PipeName), "the host pipe to stop accepting connections");
    }

    [Fact]
    public async Task Client_started_again_after_a_clean_stop_gets_a_fresh_host_and_republished_tools()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");
        var firstHost = fx.Client.HostProcessId ?? throw new InvalidOperationException("The client did not start a host.");

        await fx.Client.StopAsync(fx.Ct);
        Assert.Empty(fx.Client.Tools);

        await fx.Client.StartAsync(fx.Ct);
        Assert.Equal(HostStatus.Running, fx.Client.Status);
        Assert.NotEqual(firstHost, fx.Client.HostProcessId);
        Assert.Null(fx.Client.HostExitCode);
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools after restart");

        var echo = await fx.InvokeAsync("alpha", "echo", """{"text":"again"}""");
        Assert.False(echo.IsError, echo.Content);
        Assert.Equal("a:again", echo.Content);
    }

    [Fact]
    public async Task Stop_before_start_is_a_no_op()
    {
        await using var fx = new E2eFixture();
        await fx.Client.StopAsync(fx.Ct);
        Assert.Equal(HostStatus.Stopped, fx.Client.Status);
        Assert.Null(fx.Client.HostProcessId);
    }

    [Fact]
    public async Task Stop_request_lets_the_host_exit_on_its_own_with_code_zero()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");
        Assert.Null(fx.Client.HostExitCode);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await fx.Client.StopAsync(fx.Ct);
        clock.Stop();

        Assert.Equal(HostStatus.Stopped, fx.Client.Status);
        // Exit code 0 proves the host ran its own shutdown after host.stop instead of being killed by the client. The
        // stop time is reported because a client that ran out of its exit window kills the host, which is the other way
        // this assertion fails.
        Assert.True(
            fx.Client.HostExitCode == 0,
            $"the host did not run its own shutdown: host.stop answered in {clock.Elapsed.TotalSeconds:0.###} s with " +
            $"exit code {fx.Client.HostExitCode?.ToString() ?? "none"}; the client kills a host that does not exit " +
            $"within {TestBudgets.Condition.TotalSeconds:0} s.");
    }
}
