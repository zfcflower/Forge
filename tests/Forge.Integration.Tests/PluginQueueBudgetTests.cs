using System.Text.Json;
using Forge.Abstractions;
using Forge.HostClient;
using Forge.Ipc;
using Sdk = Forge.Plugin.Sdk;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>Queue-budget expiry, caller cancel, revoke and host stop for calls waiting on a worker slot.</summary>
public sealed class PluginQueueBudgetTests
{
    private const string QueueTimeoutCode = "plugin_queue_timeout";

    /// <summary>Upper bound on how long a queued call may take to answer when it should have failed already.</summary>
    private static readonly TimeSpan AnswerLimit = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound on the holder's own answer once the test releases it.</summary>
    private static readonly TimeSpan HolderLimit = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan MarkerLimit = TimeSpan.FromSeconds(30);

    private static readonly string[] NestedChain = ["$run_code"];

    [Fact]
    public async Task Nested_same_root_sibling_that_waits_past_its_budget_expires_in_the_queue_before_dispatch()
    {
        await RunQueueExpiryAsync(
            NestedChain,
            holdRun: "budget-nested-hold",
            holdRoot: "budget-nested-root",
            waitRun: "budget-nested-wait",
            waitRoot: "budget-nested-root");
    }

    [Fact]
    public async Task Top_level_call_that_waits_past_its_budget_for_a_worker_slot_expires_with_plugin_queue_timeout()
    {
        await RunQueueExpiryAsync(
            [],
            holdRun: "budget-top-hold",
            holdRoot: "budget-top-hold",
            waitRun: "budget-top-wait",
            waitRoot: "budget-top-wait");
    }

    [Fact]
    public async Task Command_behind_a_busy_worker_slot_expires_in_the_queue_before_dispatch()
    {
        await using var host = PluginQueueBudgetHost.Start(["--concurrency", "1", "--tool-timeout", "120"]);
        host.WritePlugin("gate", PluginQueueBudgetSources.Gated("gate"));
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = budget.Token;
        await using var peer = await host.ConnectAsync(ct);
        await PluginQueueBudgetWire.ApproveAsync(peer, "gate", ct);

        var workerPid = await PidAsync(peer, host.Root, "budget-command-hold", ct);
        var holdMarker = Path.Combine(host.Root, "holder-started.txt");
        var release = Path.Combine(host.Root, "release.txt");
        var waitMarker = Path.Combine(host.Root, "waiter-ran.txt");

        var holder = peer.SendRequestAsync(
            Sdk.ExtensionProtocol.PluginExecute,
            PluginQueueBudgetWire.Execute(
                host.Root, "gate", PluginQueueBudgetSources.HoldTool,
                new { marker = holdMarker, release },
                "budget-command-hold", "budget-command-hold", [], 60_000),
            ct);
        await PluginQueueBudgetWait.UntilAsync(() => File.Exists(holdMarker), MarkerLimit, ct, "the holder to occupy the worker slot");

        IpcRemoteException? refusal = null;
        using (var bound = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            bound.CancelAfter(AnswerLimit);
            try
            {
                await peer.SendRequestAsync(
                    Sdk.ExtensionProtocol.PluginCommand,
                    PluginQueueBudgetWire.ExecuteCommand(
                        host.Root, "gate", "markcmd",
                        JsonSerializer.Serialize(new { marker = waitMarker }),
                        "budget-command-wait", "budget-command-wait", [], 1500),
                    bound.Token);
            }
            catch (IpcRemoteException remote)
            {
                refusal = remote;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"the queued command was not answered within {AnswerLimit.TotalSeconds:0} s although its 1.5 s budget had run out.");
            }
        }

        Assert.NotNull(refusal);
        Assert.Equal(QueueTimeoutCode, refusal!.Code);
        Assert.False(holder.IsCompleted, "the holder answered before the queued command's budget expired");
        Assert.False(File.Exists(waitMarker), "the queued command's handler ran although its budget expired in the queue");
        Assert.True(E2eFixture.IsRunning(workerPid), $"the worker (pid {workerPid}) was killed by the queued command's expiry");

        File.WriteAllText(release, "release");
        var held = PluginQueueBudgetWire.ReadToolResult(await holder.WaitAsync(HolderLimit, ct));
        Assert.False(held.IsError, held.Content);
        Assert.Equal(workerPid, await PidAsync(peer, host.Root, "budget-command-after", ct));

        var later = await peer.SendRequestAsync(
            Sdk.ExtensionProtocol.PluginCommand,
            PluginQueueBudgetWire.ExecuteCommand(
                host.Root, "gate", "markcmd",
                JsonSerializer.Serialize(new { marker = Path.Combine(host.Root, "later-ran.txt") }),
                "budget-command-later", "budget-command-later", [], 60_000),
            ct);
        var (output, isError) = PluginQueueBudgetWire.ReadCommandResult(later);
        Assert.False(isError, output);
        Assert.Equal("marked", output);
    }

    [Fact]
    public async Task Caller_cancel_of_a_queued_sibling_returns_promptly_and_the_holder_completes()
    {
        await using var fx = new E2eFixture(hostArguments: ["--concurrency", "1"]);
        var holdMarker = Path.Combine(fx.Root, "holder-started.txt");
        var waitMarker = Path.Combine(fx.Root, "waiter-ran.txt");
        var release = Path.Combine(fx.Root, "release.txt");
        await StartGatedAsync(fx);
        var pidBefore = await fx.WorkerPidAsync("gate");

        var holder = fx.InvokeAsync("gate", PluginQueueBudgetSources.HoldTool, JsonSerializer.Serialize(new { marker = holdMarker, release }));
        await PluginQueueBudgetWait.UntilAsync(() => File.Exists(holdMarker), MarkerLimit, fx.Ct, "the holder to occupy the only slot");

        using var withdraw = CancellationTokenSource.CreateLinkedTokenSource(fx.Ct);
        var waiter = fx.ToolOf("gate", PluginQueueBudgetSources.MarkTool).InvokeAsync(
            new ToolCall(Guid.NewGuid().ToString("N"), PluginQueueBudgetSources.MarkTool, JsonSerializer.Serialize(new { marker = waitMarker })),
            new ToolContext("queue-budget", fx.Root),
            withdraw.Token).AsTask();
        withdraw.Cancel();

        var ending = await EndAsync(waiter, fx.Ct);
        AssertDidNotSucceed(ending, "the withdrawn sibling");
        Assert.False(File.Exists(waitMarker), "the withdrawn sibling ran while the holder still held the only slot");
        Assert.False(holder.IsCompleted, "the holder ended when a sibling was withdrawn");

        File.WriteAllText(release, "release");
        var held = await holder.WaitAsync(HolderLimit, fx.Ct);
        Assert.False(held.IsError, held.Content);
        Assert.Equal("held:released", held.Content);

        var later = await fx.InvokeAsync("gate", PluginQueueBudgetSources.EchoTool, """{"text":"after"}""");
        Assert.False(later.IsError, later.Content);
        Assert.Equal("g:after", later.Content);
        Assert.Equal(pidBefore, await fx.WorkerPidAsync("gate"));
    }

    [Fact]
    public async Task Revoking_the_plugin_drains_a_queued_sibling_without_hanging()
    {
        await using var fx = new E2eFixture(hostArguments: ["--concurrency", "1"]);
        var holdMarker = Path.Combine(fx.Root, "holder-started.txt");
        var waitMarker = Path.Combine(fx.Root, "waiter-ran.txt");
        await StartGatedAsync(fx);

        var holder = fx.InvokeAsync("gate", PluginQueueBudgetSources.HoldTool, JsonSerializer.Serialize(new { marker = holdMarker, release = "never" }));
        await PluginQueueBudgetWait.UntilAsync(() => File.Exists(holdMarker), MarkerLimit, fx.Ct, "the holder to occupy the only slot");
        var waiter = fx.InvokeAsync("gate", PluginQueueBudgetSources.MarkTool, JsonSerializer.Serialize(new { marker = waitMarker }));

        await fx.Client.RevokePluginAsync("gate", "1.0.0", fx.Ct).WaitAsync(AnswerLimit, fx.Ct);

        AssertDidNotSucceed(await EndAsync(waiter, fx.Ct), "the sibling queued behind a revoked plugin");
        AssertDidNotSucceed(await EndAsync(holder, fx.Ct), "the holder of a revoked plugin");
        Assert.False(File.Exists(waitMarker), "the queued sibling's handler ran after the revoke");
    }

    [Fact]
    public async Task Host_stop_drains_a_queued_sibling_without_hanging()
    {
        await using var fx = new E2eFixture(hostArguments: ["--concurrency", "1"]);
        var holdMarker = Path.Combine(fx.Root, "holder-started.txt");
        var waitMarker = Path.Combine(fx.Root, "waiter-ran.txt");
        await StartGatedAsync(fx);

        var holder = fx.InvokeAsync("gate", PluginQueueBudgetSources.HoldTool, JsonSerializer.Serialize(new { marker = holdMarker, release = "never" }));
        await PluginQueueBudgetWait.UntilAsync(() => File.Exists(holdMarker), MarkerLimit, fx.Ct, "the holder to occupy the only slot");
        var waiter = fx.InvokeAsync("gate", PluginQueueBudgetSources.MarkTool, JsonSerializer.Serialize(new { marker = waitMarker }));

        await fx.Client.StopAsync(fx.Ct).WaitAsync(AnswerLimit, fx.Ct);
        Assert.Equal(HostStatus.Stopped, fx.Client.Status);

        AssertDidNotSucceed(await EndAsync(waiter, fx.Ct), "the sibling queued behind a stopped host");
        AssertDidNotSucceed(await EndAsync(holder, fx.Ct), "the holder of a stopped host");
        Assert.False(File.Exists(waitMarker), "the queued sibling's handler ran after the host stopped");
    }

    /// <summary>The holder keeps the only slot; the sibling's 1.5 s budget expires in the queue before the holder is released.</summary>
    private static async Task RunQueueExpiryAsync(
        IReadOnlyList<string> chain,
        string holdRun,
        string holdRoot,
        string waitRun,
        string waitRoot)
    {
        await using var host = PluginQueueBudgetHost.Start(["--concurrency", "1", "--tool-timeout", "120"]);
        host.WritePlugin("gate", PluginQueueBudgetSources.Gated("gate"));
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = budget.Token;
        await using var peer = await host.ConnectAsync(ct);
        await PluginQueueBudgetWire.ApproveAsync(peer, "gate", ct);

        var workerPid = await PidAsync(peer, host.Root, "pid-before-" + holdRun, ct);
        var holdMarker = Path.Combine(host.Root, "holder-started.txt");
        var release = Path.Combine(host.Root, "release.txt");
        var waitMarker = Path.Combine(host.Root, "waiter-ran.txt");

        var holder = peer.SendRequestAsync(
            Sdk.ExtensionProtocol.PluginExecute,
            PluginQueueBudgetWire.Execute(
                host.Root, "gate", PluginQueueBudgetSources.HoldTool,
                new { marker = holdMarker, release },
                holdRun, holdRoot, chain, 60_000),
            ct);
        await PluginQueueBudgetWait.UntilAsync(() => File.Exists(holdMarker), MarkerLimit, ct, "the holder to occupy the worker slot");

        var waiterError = await AwaitQueueRefusalAsync(peer, host.Root, waitRun, waitRoot, chain, waitMarker, ct);

        Assert.Equal(QueueTimeoutCode, waiterError.Code);
        Assert.False(holder.IsCompleted, "the holder answered before the queued sibling's budget expired; the expiry did not happen while queued");
        Assert.False(File.Exists(waitMarker), "the queued sibling's handler ran although its budget expired in the queue");
        Assert.True(E2eFixture.IsRunning(workerPid), $"the worker (pid {workerPid}) that runs the holder was killed by the queued sibling's expiry");

        File.WriteAllText(release, "release");
        var held = PluginQueueBudgetWire.ReadToolResult(await holder.WaitAsync(HolderLimit, ct));
        Assert.False(held.IsError, held.Content);
        Assert.Equal("held:released", held.Content);
        Assert.Equal(workerPid, await PidAsync(peer, host.Root, "pid-after-" + holdRun, ct));

        var later = await peer.SendRequestAsync(
            Sdk.ExtensionProtocol.PluginExecute,
            PluginQueueBudgetWire.Execute(
                host.Root, "gate", PluginQueueBudgetSources.EchoTool,
                new { text = "after" },
                "budget-later-" + waitRun, "budget-later-" + waitRun, [], 60_000),
            ct);
        var (content, isError) = PluginQueueBudgetWire.ReadToolResult(later);
        Assert.False(isError, content);
        Assert.Equal("g:after", content);
        Assert.False(File.Exists(waitMarker), "the expired sibling ran once the slot freed; an expired call must never dispatch");
    }

    /// <summary>Sends a sibling with a 1.5 s budget and returns the refusal it must receive.</summary>
    private static async Task<IpcRemoteException> AwaitQueueRefusalAsync(
        IpcPeer peer,
        string root,
        string runId,
        string rootRunId,
        IReadOnlyList<string> chain,
        string marker,
        CancellationToken ct)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(AnswerLimit);
        try
        {
            await peer.SendRequestAsync(
                Sdk.ExtensionProtocol.PluginExecute,
                PluginQueueBudgetWire.Execute(root, "gate", PluginQueueBudgetSources.MarkTool, new { marker }, runId, rootRunId, chain, 1500),
                bound.Token);
        }
        catch (IpcRemoteException remote)
        {
            return remote;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"the queued sibling was not answered within {AnswerLimit.TotalSeconds:0} s although its 1.5 s budget had run out.");
        }

        throw new InvalidOperationException("the queued sibling succeeded instead of being refused for its budget");
    }

    private static async Task<int> PidAsync(IpcPeer peer, string root, string runId, CancellationToken ct)
    {
        var response = await peer.SendRequestAsync(
            Sdk.ExtensionProtocol.PluginExecute,
            PluginQueueBudgetWire.Execute(root, "gate", PluginQueueBudgetSources.PidTool, new { }, runId, runId, [], 60_000),
            ct);
        var (content, isError) = PluginQueueBudgetWire.ReadToolResult(response);
        Assert.False(isError, content);
        return int.Parse(content);
    }

    private static async Task StartGatedAsync(E2eFixture fx)
    {
        fx.WritePlugin("gate", PluginQueueBudgetSources.Gated("gate"));
        await fx.StartAsync();
        await fx.ApproveAsync("gate");
        await fx.WaitUntilAsync(() => fx.ToolsOf("gate").Count == 4, "the tools of gate");
    }

    private static async Task<(ToolResult? Result, Exception? Fault)> EndAsync(Task<ToolResult> call, CancellationToken ct)
    {
        try
        {
            return (await call.WaitAsync(AnswerLimit, ct), null);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"A call did not end within {AnswerLimit.TotalSeconds:0} s after the event that should have ended it.");
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    /// <summary>A withdrawn, expired or drained call ends as an error or a cancellation.</summary>
    private static void AssertDidNotSucceed((ToolResult? Result, Exception? Fault) ending, string what)
    {
        if (ending.Fault is not null)
        {
            Assert.True(
                ending.Fault is OperationCanceledException or IpcRemoteException or IOException,
                $"{what} failed with an unexpected {ending.Fault.GetType().FullName}: {ending.Fault.Message}");
            return;
        }

        Assert.True(ending.Result is { IsError: true }, $"{what} succeeded: '{ending.Result?.Content}'");
    }
}
