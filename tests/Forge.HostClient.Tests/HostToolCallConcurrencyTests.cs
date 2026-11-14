using System.Collections.Concurrent;
using Forge.Abstractions;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary>Per-run callback gates, cross-run isolation, result truncation and run-lifetime expiry.
/// All coordination uses <see cref="TaskCompletionSource"/> barriers; no sleeps.</summary>
public sealed class HostToolCallConcurrencyTests
{
    private const string Operation = "host.tool_call";

    [Fact]
    public async Task The_per_run_gate_caps_concurrent_callbacks_at_one()
    {
        var execution = new Sdk.CodeExecutionOptions { MaxConcurrentToolCalls = 1, MaxInnerResultChars = 10_000 };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrent = 0;
        var peak = 0;
        var tool = new StubTool("work", ToolSideEffect.ReadOnly, async (_, _, _) =>
        {
            var running = Interlocked.Increment(ref concurrent);
            while (true)
            {
                var seen = Volatile.Read(ref peak);
                if (running <= seen || Interlocked.CompareExchange(ref peak, running, seen) == seen)
                {
                    break;
                }
            }

            entered.TrySetResult();
            await releaseTool.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref concurrent);
            return new ToolResult("done");
        });
        var registry = new TestRegistry([tool]);
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var setup = await BoundRoots.StartScriptRootAsync(host, client, registry, limit.Token, execution);

        using var callbackBudget = TestLimit.Create();
        var first = host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(setup.RunId, "$run_code", "work", "{}"), callbackBudget.Token);
        await entered.Task.Bounded();
        var second = host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(setup.RunId, "$run_code", "work", "{}"), callbackBudget.Token);

        Assert.False(second.IsCompleted, "The second callback must wait at the per-run gate of max 1.");
        releaseTool.TrySetResult();
        Assert.Equal(("done", false), BoundRoots.OutputOf(await first.Bounded()));
        Assert.Equal(("done", false), BoundRoots.OutputOf(await second.Bounded()));
        await setup.FinishAsync();

        Assert.Equal(1, Volatile.Read(ref peak));
        Assert.Equal(2, tool.InvocationCount);
    }

    [Fact]
    public async Task Independent_roots_do_not_share_the_callback_cap()
    {
        var execution = new Sdk.CodeExecutionOptions { MaxConcurrentToolCalls = 1, MaxInnerResultChars = 10_000 };
        var entered = new ConcurrentQueue<string>();
        var bothInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTools = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new StubTool("work", ToolSideEffect.ReadOnly, async (call, _, _) =>
        {
            entered.Enqueue(call.ArgumentsJson);
            if (entered.Count == 2)
            {
                bothInside.TrySetResult();
            }

            await releaseTools.Task.ConfigureAwait(false);
            return new ToolResult("done");
        });
        var registry = new TestRegistry([tool]);
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();

        var first = await BoundRoots.StartScriptRootAsync(host, client, registry, limit.Token, execution, "1+1");
        var second = await BoundRoots.StartScriptRootAsync(host, client, registry, limit.Token, execution, "2+2");
        Assert.NotEqual(first.RunId, second.RunId);

        using var callbackBudget = TestLimit.Create();
        var callbackA = host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(first.RunId, "$run_code", "work", "{\"tag\":\"a\"}"), callbackBudget.Token);
        var callbackB = host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(second.RunId, "$run_code", "work", "{\"tag\":\"b\"}"), callbackBudget.Token);

        await bothInside.Task.Bounded();
        releaseTools.TrySetResult();
        Assert.Equal(("done", false), BoundRoots.OutputOf(await callbackA.Bounded()));
        Assert.Equal(("done", false), BoundRoots.OutputOf(await callbackB.Bounded()));
        await first.FinishAsync();
        await second.FinishAsync();

        Assert.Equal(["{\"tag\":\"a\"}", "{\"tag\":\"b\"}"], entered.OrderBy(entry => entry, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Callback_content_is_bounded_and_marked()
    {
        var execution = new Sdk.CodeExecutionOptions { MaxConcurrentToolCalls = 8, MaxInnerResultChars = 50 };
        var big = new string('x', 200);
        var tool = new StubTool("work", ToolSideEffect.ReadOnly, (_, _, _) =>
            ValueTask.FromResult(new ToolResult(big)));
        var registry = new TestRegistry([tool]);
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var setup = await BoundRoots.StartScriptRootAsync(host, client, registry, limit.Token, execution);

        var response = await setup.CallAsync(host, "work", "{}", limit.Token, pluginId: "$run_code");

        var (content, isError) = BoundRoots.OutputOf(response);
        Assert.False(isError);
        Assert.Equal(new string('x', 50) + "\n[... tool result truncated: kept 50 of 200 chars]", content);
    }

    [Fact]
    public async Task Outer_code_output_is_bounded_and_marked()
    {
        var execution = new Sdk.CodeExecutionOptions { MaxResultChars = 100, MaxInnerResultChars = 10_000 };
        var big = new string('y', 500);
        var registry = new TestRegistry();
        await using var harness = await HostClientHarness.StartAsync();
        var hold = new InFlightRoot { Content = big };
        BoundRoots.ParkRoot(harness.Host, "code.execute", hold);
        var proxy = harness.Client.CreateRunCodeTool(registry, new RunCodeProxyOptions { Execution = execution });

        var invocation = proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1+1\"}"),
            new ToolContext("sess", BoundRoots.AbsoluteDir("trunc")),
            harness.Limit.Token).AsTask();
        hold.Release();
        var result = await invocation.Bounded();

        Assert.False(result.IsError);
        Assert.Equal(new string('y', 100) + "\n[... result truncated: kept 100 of 500 chars]", result.Content);
    }

    [Fact]
    public async Task Canceled_run_cannot_start_a_tool_after_approval()
    {
        var tool = new StubTool("echo", ToolSideEffect.Write);
        var enteredApproval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApproval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new TestRegistry([tool]);
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(
            host,
            resolver: _ => null,
            approval: async (_, _) =>
            {
                enteredApproval.TrySetResult();
                await releaseApproval.Task.ConfigureAwait(false);
                return ApprovalDecision.Allow;
            });
        using var limit = TestLimit.Create();
        var hold = new InFlightRoot { Content = "script-ok" };
        BoundRoots.ParkRoot(host, "code.execute", hold);
        var proxy = client.CreateRunCodeTool(registry);
        var rootTask = proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1+1\"}"),
            new ToolContext("sess", BoundRoots.AbsoluteDir("expiry")),
            limit.Token).AsTask();
        var runId = BoundRoots.RunIdOf(await hold.Arrived.Task.Bounded());

        var pending = host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(runId, "$run_code", "echo", "{}"), limit.Token);
        await enteredApproval.Task.Bounded();
        hold.Gate.TrySetResult();
        await rootTask.Bounded();
        releaseApproval.TrySetResult();

        var content = BoundRoots.RefusedContent(await pending.Bounded());
        Assert.Contains("already ended", content);
        Assert.Equal(0, tool.InvocationCount);
    }
}
