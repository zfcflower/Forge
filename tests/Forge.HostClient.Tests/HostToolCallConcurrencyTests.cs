using System.Collections.Concurrent;
using System.Text.Json;
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
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "work" ? tool : null,
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var setup = await StartScriptRootAsync(host, client, registry, execution, limit.Token);

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
        setup.Hold.Gate.TrySetResult();
        await setup.RootTask.Bounded();

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
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "work" ? tool : null,
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var first = await StartScriptRootAsync(host, client, registry, execution, limit.Token, "1+1");
        var second = await StartScriptRootAsync(host, client, registry, execution, limit.Token, "2+2");
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
        first.Hold.Gate.TrySetResult();
        second.Hold.Gate.TrySetResult();
        await first.RootTask.Bounded();
        await second.RootTask.Bounded();

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
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "work" ? tool : null,
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var setup = await StartScriptRootAsync(host, client, registry, execution, limit.Token);

        var response = await host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(setup.RunId, "$run_code", "work", "{}"), limit.Token);
        setup.Hold.Gate.TrySetResult();
        await setup.RootTask.Bounded();

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
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var hold = new InFlightRoot
        {
            ResultJson = "{\"content\":" + JsonSerializer.Serialize(big) + ",\"isError\":false}",
        };
        BoundRoots.ParkRoot(host, "code.execute", hold);
        var proxy = client.CreateRunCodeTool(registry, new RunCodeProxyOptions { Execution = execution });

        var invocation = proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1+1\"}"),
            new ToolContext("sess", BoundRoots.AbsoluteDir("trunc")),
            limit.Token).AsTask();
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
        await using var client = ClientFactory.Create(
            host,
            resolver: _ => null,
            approval: async (_, _) =>
            {
                enteredApproval.TrySetResult();
                await releaseApproval.Task.ConfigureAwait(false);
                return ApprovalDecision.Allow;
            });
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var hold = new InFlightRoot { ResultJson = """{"content":"script-ok","isError":false}""" };
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

        var (content, isError) = BoundRoots.OutputOf(await pending.Bounded());
        Assert.True(isError);
        Assert.Contains("already ended", content);
        Assert.Equal(0, tool.InvocationCount);
    }

    private sealed record ScriptSetup(string RunId, InFlightRoot Hold, Task<ToolResult> RootTask);

    private static async Task<ScriptSetup> StartScriptRootAsync(
        FakeHost host, ExtensionHostClient client, TestRegistry registry, Sdk.CodeExecutionOptions execution,
        CancellationToken ct, string code = "1+1")
    {
        var hold = new InFlightRoot { ResultJson = """{"content":"script-ok","isError":false}""" };
        BoundRoots.ParkRoot(host, "code.execute", hold);
        var proxy = client.CreateRunCodeTool(registry, new RunCodeProxyOptions { Execution = execution });
        var rootTask = proxy.InvokeAsync(
            new ToolCall("root-rc", "run_code", "{\"code\":" + JsonSerializer.Serialize(code) + "}"),
            new ToolContext("sess-script", BoundRoots.AbsoluteDir("script"), 0),
            ct).AsTask();
        var runId = BoundRoots.RunIdOf(await hold.Arrived.Task.Bounded());
        return new ScriptSetup(runId, hold, rootTask);
    }
}
