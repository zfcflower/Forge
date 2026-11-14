using Forge.Abstractions;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary>
/// <c>run_code</c> under live budgets and a live registry (wave W14): the concurrency gate, both truncation bounds and the
/// per-call clamp follow the provider's <i>current</i> answer at the moment a script starts, and an inner call is resolved
/// against the registry at call time, so a name the registry no longer admits (a narrowed list, a switched-off tool, a
/// name that was never a candidate) is refused as unknown without reaching approval or the tool.
/// </summary>
public sealed class RunCodeLiveBudgetTests
{
    private const string Operation = "host.tool_call";

    /// <summary>Starts a <c>run_code</c> root through a proxy built over <paramref name="options"/>, after the caller may have
    /// moved the provider's value: creation validates the provider once, the invocation reads it again.</summary>
    private static async Task<ActiveRoot> StartRootAsync(
        FakeHost host, ExtensionHostClient client, IToolRegistry registry, RunCodeProxyOptions options, CancellationToken ct, Action? beforeInvoke = null)
    {
        var hold = new InFlightRoot { Content = "script-ok" };
        BoundRoots.ParkRoot(host, "code.execute", hold);
        var proxy = client.CreateRunCodeTool(registry, options);
        beforeInvoke?.Invoke();
        var rootTask = proxy.InvokeAsync(
            new ToolCall("root-rc", "run_code", "{\"code\":\"1+1\"}"),
            new ToolContext("sess-live", BoundRoots.AbsoluteDir("live"), 0),
            ct).AsTask();
        var runId = BoundRoots.RunIdOf(await BoundRoots.WaitArrivedAsync(hold));
        return new ActiveRoot(runId, hold, rootTask);
    }

    [Fact]
    public async Task The_gate_uses_the_provider_value_read_when_the_script_starts()
    {
        var current = new Sdk.CodeExecutionOptions { MaxConcurrentToolCalls = 8 };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new StubTool("work", ToolSideEffect.ReadOnly, async (_, _, _) =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return new ToolResult("done");
        });
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();

        // Eight at creation (the options are valid), one by the time the script starts.
        var setup = await StartRootAsync(
            host,
            client,
            new TestRegistry([tool]),
            new RunCodeProxyOptions { ExecutionProvider = () => current },
            limit.Token,
            beforeInvoke: () => current = new Sdk.CodeExecutionOptions { MaxConcurrentToolCalls = 1 });

        using var callbackBudget = TestLimit.Create();
        var first = host.CallClientAsync(Operation, BoundRoots.BoundPayload(setup.RunId, "$run_code", "work", "{}"), callbackBudget.Token);
        await entered.Task.Bounded();
        var second = host.CallClientAsync(Operation, BoundRoots.BoundPayload(setup.RunId, "$run_code", "work", "{}"), callbackBudget.Token);

        // A gate sized from the creation-time value (8) would have let the second call into the tool as well.
        Assert.False(second.IsCompleted, "the second callback must wait at the gate of the current value, 1");
        release.TrySetResult();
        Assert.Equal(("done", false), BoundRoots.OutputOf(await first.Bounded()));
        Assert.Equal(("done", false), BoundRoots.OutputOf(await second.Bounded()));
        await setup.FinishAsync();
        Assert.Equal(2, tool.InvocationCount);
    }

    [Fact]
    public async Task A_provider_that_later_answers_zero_is_clamped_to_one_instead_of_hanging_the_call()
    {
        var current = new Sdk.CodeExecutionOptions { MaxConcurrentToolCalls = 8 };
        var tool = new StubTool("work", ToolSideEffect.ReadOnly);
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();

        var setup = await StartRootAsync(
            host,
            client,
            new TestRegistry([tool]),
            new RunCodeProxyOptions { ExecutionProvider = () => current },
            limit.Token,
            beforeInvoke: () => current = new Sdk.CodeExecutionOptions { MaxConcurrentToolCalls = 0, MaxInnerResultChars = -5 });

        // A zero-slot semaphore would park this call until the script's timeout; clamped to one it simply runs.
        using var callbackBudget = TestLimit.Create();
        var response = await host
            .CallClientAsync(Operation, BoundRoots.BoundPayload(setup.RunId, "$run_code", "work", "{}"), callbackBudget.Token)
            .Bounded();
        await setup.FinishAsync();

        Assert.False(BoundRoots.OutputOf(response).IsError);
        Assert.Equal(1, tool.InvocationCount);
    }

    [Fact]
    public async Task Both_truncation_bounds_use_the_provider_value_read_when_the_script_starts()
    {
        var current = new Sdk.CodeExecutionOptions { MaxInnerResultChars = 10_000, MaxResultChars = 10_000 };
        var tool = new StubTool("work", ToolSideEffect.ReadOnly, (_, _, _) =>
            ValueTask.FromResult(new ToolResult(new string('x', 200))));
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();

        var setup = await StartRootAsync(
            host,
            client,
            new TestRegistry([tool]),
            new RunCodeProxyOptions { ExecutionProvider = () => current },
            limit.Token,
            beforeInvoke: () => current = new Sdk.CodeExecutionOptions { MaxInnerResultChars = 30, MaxResultChars = 4 });

        using var callbackBudget = TestLimit.Create();
        var inner = BoundRoots.OutputOf(await host
            .CallClientAsync(Operation, BoundRoots.BoundPayload(setup.RunId, "$run_code", "work", "{}"), callbackBudget.Token)
            .Bounded());
        var final = await setup.FinishAsync();

        Assert.Equal(new string('x', 30) + "\n[... tool result truncated: kept 30 of 200 chars]", inner.Content);
        Assert.Equal("scri\n[... result truncated: kept 4 of 9 chars]", final.Content);
    }

    [Fact]
    public async Task A_name_the_registry_no_longer_admits_is_refused_as_unknown_before_approval_or_the_tool()
    {
        var kept = new StubTool("read");
        var narrowed = new StubTool("grep");
        var approvals = 0;
        var registry = new TestRegistry([kept, narrowed]);
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(
            host,
            resolver: _ => null,
            approval: (_, _) =>
            {
                Interlocked.Increment(ref approvals);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        using var limit = TestLimit.Create();
        var setup = await BoundRoots.StartScriptRootAsync(host, client, registry, limit.Token);

        // The script's declarations were taken with both tools; the list narrows while it runs.
        registry.Remove("grep");

        using var callbackBudget = TestLimit.Create();
        var hidden = await host
            .CallClientAsync(Operation, BoundRoots.BoundPayload(setup.RunId, "$run_code", "grep", "{}"), callbackBudget.Token)
            .Bounded();
        var visible = await host
            .CallClientAsync(Operation, BoundRoots.BoundPayload(setup.RunId, "$run_code", "read", "{}"), callbackBudget.Token)
            .Bounded();
        await setup.FinishAsync();

        Assert.Equal("Unknown tool 'grep'.", BoundRoots.RefusedContent(hidden));
        Assert.Equal(("ran read", false), BoundRoots.OutputOf(visible));
        Assert.Equal(0, narrowed.InvocationCount);
        Assert.Equal(1, approvals);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("write")]
    [InlineData("git_status")]
    [InlineData("no_such_tool")]
    public async Task A_tool_outside_the_script_registry_is_unknown_even_when_the_session_has_it(string name)
    {
        // The session-wide resolver knows every tool; a script's run resolves against its own registry only.
        var outside = new StubTool(name, ToolSideEffect.Execute);
        var inside = new StubTool("read");
        var approvals = 0;
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(
            host,
            resolver: toolName => toolName == name ? outside : inside,
            approval: (_, _) =>
            {
                Interlocked.Increment(ref approvals);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        using var limit = TestLimit.Create();
        var setup = await BoundRoots.StartScriptRootAsync(host, client, new TestRegistry([inside]), limit.Token);

        var response = await setup.CallAsync(host, name, "{}", limit.Token, pluginId: "$run_code");

        Assert.Equal($"Unknown tool '{name}'.", BoundRoots.RefusedContent(response));
        Assert.Equal(0, outside.InvocationCount);
        Assert.Equal(0, approvals);
    }
}
