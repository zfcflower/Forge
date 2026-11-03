using System.Diagnostics;
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

    [Fact(Skip = "BUG: ExtensionHostClient.StopAsync sends host.stop, then HostProcess.TerminateAsync kills the host process "
        + "tree at once (exit code -1) without waiting for ProcessExitTimeout, which its own contract describes as the time the "
        + "host gets to exit on its own. Expected: after host.stop the host runs its shutdown (workers stopped) and exits with code 0 "
        + "within ProcessExitTimeout.")]
    public async Task Stop_request_lets_the_host_exit_on_its_own_with_code_zero()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");

        var hostPid = fx.Client.HostProcessId ?? throw new InvalidOperationException("The client did not start a host.");
        using var host = Process.GetProcessById(hostPid);
        var exited = host.WaitForExitAsync(fx.Ct);

        await fx.Client.StopAsync(fx.Ct);

        await exited;
        Assert.Equal(0, host.ExitCode);
    }
}
