using System.Diagnostics;
using Forge.Ipc;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.Integration.Tests;

/// <summary>Host shutdown while a plugin worker is stalled in its startup load: prompt exit, worker gone, queued call failed.</summary>
public sealed class HostCleanupFailureTests
{
    /// <summary>Bound on the host's exit after <c>host.stop</c>; a stalled start must be aborted well inside it.</summary>
    private static readonly TimeSpan ExitLimit = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan BarrierLimit = TestBudgets.Condition;

    [Theory]
    [InlineData(nameof(FakeWorkerMode.NeverConnect), FakeScriptWorker.ReadyFile)]
    [InlineData(nameof(FakeWorkerMode.ConnectHold), FakeScriptWorker.RequestSeenFile)]
    public async Task Host_stop_exits_and_kills_a_plugin_worker_stalled_in_its_startup_load(string mode, string barrierFile)
    {
        var fakeDirectory = Path.Combine(Path.GetTempPath(), "forge-cleanup-fake-" + Guid.NewGuid().ToString("N")[..10]);
        var root = PluginQueueBudgetHost.NewRoot("cleanup");
        try
        {
            var fakeWorker = FakeScriptWorker.Build(fakeDirectory, Enum.Parse<FakeWorkerMode>(mode));
            using var budget = new CancellationTokenSource(TestBudgets.Safety(TimeSpan.FromSeconds(150), TimeSpan.FromMinutes(10)));
            var ct = budget.Token;

            await using (var approving = PluginQueueBudgetHost.Start(["--tool-timeout", "120"], tag: "cleanup-approve", root: root))
            {
                approving.WritePlugin("gate", PluginQueueBudgetSources.Gated("gate"));
                await using var peer = await approving.ConnectAsync(ct);
                await PluginQueueBudgetWire.ApproveAsync(peer, "gate", ct);
                await StopAsync(peer, ct, approving);
                var approvingExit = await ExitAsync(approving, ct);
                Assert.True(
                    approvingExit == 0,
                    $"the host that approved 'gate' exited with {approvingExit}; the second phase would start from an " +
                    $"unapproved plugin. {Describe(approving)}");
            }

            await using var host = PluginQueueBudgetHost.Start(
                ["--worker", fakeWorker, "--dotnet", "dotnet", "--tool-timeout", "120"],
                tag: "cleanup",
                root: root);
            await using var stalled = await host.ConnectAsync(ct);

            // Barrier: the fake is up and the host is inside its startup refresh load.
            await PluginQueueBudgetWait.UntilAsync(
                () => File.Exists(Path.Combine(fakeDirectory, barrierFile)), BarrierLimit, ct, "the fake worker to reach its barrier");
            var workerPid = FakeScriptWorker.ReadPid(fakeDirectory);
            Assert.True(
                E2eFixture.IsRunning(workerPid),
                $"the fake worker (pid {workerPid}) exited before the stop, so the load it stalls was never in flight. " +
                Describe(host));

            var queued = stalled.RequestAsync(
                Sdk.ExtensionProtocol.PluginExecute,
                PluginQueueBudgetWire.Execute(
                    host.Root, "gate", PluginQueueBudgetSources.MarkTool, new { marker = Path.Combine(host.Root, "ran.txt") },
                    "cleanup-run-1", "cleanup-run-1", [], 120_000),
                ct);

            await StopAsync(stalled, ct, host);
            var exitCode = await ExitAsync(host, ct);

            Assert.True(
                exitCode == 0,
                $"the host exited with {exitCode} after host.stop had to kill a worker stalled in its start. {Describe(host)}");
            Assert.False(
                E2eFixture.IsRunning(workerPid),
                $"the host exited while its plugin worker (pid {workerPid}) was still running; the worker must be gone before the host is. " +
                Describe(host));
            Assert.False(
                File.Exists(Path.Combine(host.Root, "ran.txt")),
                $"the plugin handler ran on a worker that never loaded. {Describe(host)}");

            await AssertFailedWithoutResultAsync(queued, ct, host);
        }
        finally
        {
            ReleaseFake(fakeDirectory);
            RawHost.TryDeleteDirectory(root);
            RawHost.TryDeleteDirectory(fakeDirectory);
        }
    }

    private static async Task StopAsync(IpcPeer peer, CancellationToken ct, PluginQueueBudgetHost host)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var answer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            answer.CancelAfter(BarrierLimit);
            await peer.RequestAsync(Sdk.ExtensionProtocol.HostStop, IpcEmpty.Instance, answer.Token);
        }
        catch (IpcConnectionClosedException)
        {
            // The host may close its pipe before answering host.stop; the exit is what is asserted.
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"host.stop was neither answered nor followed by a closed pipe within {BarrierLimit.TotalSeconds:0} s " +
                $"({clock.Elapsed.TotalSeconds:0.###} s waited). {Describe(host)}");
        }
    }

    private static async Task<int> ExitAsync(PluginQueueBudgetHost host, CancellationToken ct)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(ExitLimit);
        try
        {
            return await host.WaitForExitAsync(bound.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The Extension Host did not exit within {ExitLimit.TotalSeconds:0} s of host.stop while a plugin worker was " +
                $"starting; this machine's slowest observed process start took " +
                $"{TestBudgets.ObservedHostStart.TotalSeconds:0.###} s. {Describe(host)}");
        }
    }

    /// <summary>The host's own stdout/stderr, which names a cleanup failure the exit code only reports as non-zero.</summary>
    private static string Describe(PluginQueueBudgetHost host) =>
        host.Output.Count == 0 ? "The host wrote nothing to stdout or stderr." : "Host output: " + string.Join(" | ", host.Output);

    private static async Task AssertFailedWithoutResultAsync(Task<IpcMessage> pending, CancellationToken ct, PluginQueueBudgetHost host)
    {
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(BarrierLimit);
            var message = await pending.WaitAsync(bound.Token);
            Assert.Fail($"the call waiting on the stopped worker succeeded: '{message.Operation}' answered request {message.Id}.");
        }
        catch (Exception exception) when (exception is IpcConnectionClosedException or IpcRemoteException or IOException)
        {
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"the call waiting on the stopped worker never ended. {Describe(host)}");
        }
    }

    private static void ReleaseFake(string fakeDirectory)
    {
        try
        {
            File.WriteAllText(Path.Combine(fakeDirectory, "release.txt"), "release");
        }
        catch (IOException)
        {
        }
    }
}
