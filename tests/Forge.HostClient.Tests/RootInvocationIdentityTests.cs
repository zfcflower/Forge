using System.Collections.Concurrent;
using Forge.Abstractions;
using Forge.Ipc;
using Forge.Ipc.Protocol;
using Xunit;
using Sdk = Forge.Plugin.Sdk;
using static Forge.HostClient.Tests.ToolFixtures;

namespace Forge.HostClient.Tests;

/// <summary>Root invocation identity (<c>context.rootRunId</c>): the main program assigns the top-level
/// run's own id as its root, nested executions keep the parent's root with a fresh run id, siblings
/// share one root, and independent executions never share one. A worker-supplied root on the wire is
/// ignored: every context on the wire is built from the trusted pending record.</summary>
public sealed class RootInvocationIdentityTests
{
    [Fact]
    public async Task Top_level_plugin_execute_carries_its_own_runId_as_rootRunId()
    {
        await using var host = new FakeHost();
        host.Answer("plugin.execute", new Sdk.ToolResult("ok"));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var proxy = await BoundRoots.RegisterPluginToolAsync(client, host, "alpha", "entry");

        var result = await proxy.InvokeAsync(
            new ToolCall("root-1", "entry", "{}"),
            new ToolContext("sess-root", BoundRoots.AbsoluteDir("root-top")),
            limit.Token);

        Assert.False(result.IsError);
        var context = BoundRoots.ContextOf(Assert.Single(host.Requests("plugin.execute")));
        AssertRootIsSelf(context);
    }

    [Fact]
    public async Task Top_level_code_execute_carries_its_own_runId_as_rootRunId()
    {
        await using var host = new FakeHost();
        host.Answer("code.execute", new Sdk.CodeExecutionResult("ok"));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var proxy = client.CreateRunCodeTool(new TestRegistry());

        var result = await proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1+1\"}"),
            new ToolContext("sess-rc", BoundRoots.AbsoluteDir("root-code")),
            limit.Token);

        Assert.False(result.IsError);
        var context = BoundRoots.ContextOf(Assert.Single(host.Requests("code.execute")));
        AssertRootIsSelf(context);
    }

    [Fact]
    public async Task Top_level_command_carries_its_own_runId_as_rootRunId()
    {
        await using var host = new FakeHost();
        host.Answer("plugin.command", new Sdk.CommandOutput("hello", "next", false));
        await using var client = await ClientFactory.StartAsync(host, approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var command = await RegisterCommandAsync(host, client, "alpha", "hi");

        var result = await command.ExecuteAsync(
            "args", new CommandContext("sess-cmd", BoundRoots.AbsoluteDir("root-cmd")), limit.Token);

        Assert.False(result.IsError);
        var context = BoundRoots.ContextOf(Assert.Single(host.Requests("plugin.command")));
        AssertRootIsSelf(context);
    }

    [Fact]
    public async Task Top_level_prompt_carries_its_own_runId_as_rootRunId()
    {
        await using var host = new FakeHost();
        host.Answer("plugin.prompt", new Sdk.PromptOutput("t"));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var contributor = await RegisterPromptAsync(host, client, "alpha", "tone");

        var text = await client.GetPluginPromptAsync(
            contributor,
            new ToolContext("sess-pr", BoundRoots.AbsoluteDir("root-prompt")),
            limit.Token);

        Assert.Equal("t", text);
        var context = BoundRoots.ContextOf(Assert.Single(host.Requests("plugin.prompt")));
        AssertRootIsSelf(context);
    }

    [Fact]
    public async Task Nested_plugin_call_keeps_the_parent_root_with_a_fresh_runId()
    {
        var betaSource = new TaskCompletionSource<ExtensionHostTool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new StubTool("bridge", ToolSideEffect.ReadOnly, async (call, context, ct) =>
        {
            var beta = await betaSource.Task.ConfigureAwait(false);
            return await beta.InvokeAsync(new ToolCall("nested-1", "work", "{}"), context, ct).ConfigureAwait(false);
        });
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: bridge.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var setup = await StartChainAsync(host, client, betaSource, limit.Token);

        var callback = host.CallClientAsync(
            "host.tool_call", BoundRoots.BoundPayload(setup.RunA, "alpha", "bridge", "{}"), limit.Token);
        var nested = await setup.NestedArrived.Task.Bounded();
        var nestedContext = BoundRoots.ContextOf(nested);
        var nestedRunId = nestedContext.RunId;

        Assert.NotEqual(setup.RunA, nestedRunId);
        Assert.Equal(setup.RunA, setup.RootA);
        Assert.Equal(setup.RootA, nestedContext.RootRunId);
        Assert.Equal(["alpha"], nestedContext.PluginChain);

        setup.HoldB.Content = "work-done";
        setup.HoldB.Gate.TrySetResult();
        Assert.Equal(("work-done", false), BoundRoots.OutputOf(await callback.Bounded()));
        setup.HoldA.Gate.TrySetResult();
        Assert.False((await setup.RootTask.Bounded()).IsError);
    }

    [Fact]
    public async Task Sibling_nested_calls_share_one_root_with_distinct_runIds()
    {
        var betaSource = new TaskCompletionSource<ExtensionHostTool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new StubTool("bridge", ToolSideEffect.ReadOnly, async (call, context, ct) =>
        {
            var beta = await betaSource.Task.ConfigureAwait(false);
            var first = await beta.InvokeAsync(new ToolCall("nested-1", "work", "{}"), context, ct).ConfigureAwait(false);
            var second = await beta.InvokeAsync(new ToolCall("nested-2", "work", "{}"), context, ct).ConfigureAwait(false);
            return new ToolResult(first.Content + "+" + second.Content);
        });
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: bridge.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var nestedCalls = new ConcurrentQueue<Sdk.ExtensionCallContext>();
        var bothNested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdA = new InFlightRoot();
        host.On("plugin.execute", async (message, ct) =>
        {
            var plugin = message.Read<PluginExecuteRequest>().PluginId;
            if (!string.Equals(plugin, "beta", StringComparison.Ordinal))
            {
                holdA.Arrived.TrySetResult(message);
                await holdA.Gate.Task.WaitAsync(ct).ConfigureAwait(false);
                return holdA.Reply(message.Operation);
            }

            nestedCalls.Enqueue(BoundRoots.ContextOf(message));
            if (nestedCalls.Count >= 2)
            {
                bothNested.TrySetResult();
            }

            return HostReply.Of(new Sdk.ToolResult("work"));
        });
        var alphaProxy = await BoundRoots.RegisterPluginToolAsync(client, host, "alpha", "entry");
        var betaProxy = await BoundRoots.RegisterPluginToolAsync(client, host, "beta", "work");
        betaSource.TrySetResult(betaProxy);
        var directory = BoundRoots.AbsoluteDir("siblings");
        var rootTask = alphaProxy.InvokeAsync(
            new ToolCall("root-1", "entry", "{}"),
            new ToolContext("sess-sib", directory, 0),
            limit.Token).AsTask();
        var runA = BoundRoots.RunIdOf(await holdA.Arrived.Task.Bounded());

        var callback = await host.CallClientAsync(
            "host.tool_call", BoundRoots.BoundPayload(runA, "alpha", "bridge", "{}"), limit.Token);
        await bothNested.Task.Bounded();

        var pair = nestedCalls.ToArray();
        Assert.Equal(2, pair.Length);
        var firstContext = pair[0];
        var secondContext = pair[1];
        Assert.NotEqual(firstContext.RunId, secondContext.RunId);
        Assert.NotEqual(runA, firstContext.RunId);
        Assert.NotEqual(runA, secondContext.RunId);
        Assert.Equal(runA, firstContext.RootRunId);
        Assert.Equal(runA, secondContext.RootRunId);

        Assert.Equal(("work+work", false), BoundRoots.OutputOf(callback));
        holdA.Gate.TrySetResult();
        Assert.False((await rootTask.Bounded()).IsError);
    }

    [Fact]
    public async Task Independent_same_plugin_roots_have_different_roots()
    {
        await using var host = new FakeHost();
        var seen = new ConcurrentQueue<Sdk.ExtensionCallContext>();
        host.On("plugin.execute", (message, _) =>
        {
            seen.Enqueue(BoundRoots.ContextOf(message));
            return Task.FromResult(new Sdk.ToolResult("ok"));
        });
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var proxy = await BoundRoots.RegisterPluginToolAsync(client, host, "alpha", "entry");
        var directory = BoundRoots.AbsoluteDir("independent");

        await proxy.InvokeAsync(
            new ToolCall("r-1", "entry", "{}"), new ToolContext("sess-i", directory, 0), limit.Token);
        await proxy.InvokeAsync(
            new ToolCall("r-2", "entry", "{}"), new ToolContext("sess-i", directory, 0), limit.Token);

        var pair = seen.ToArray();
        Assert.Equal(2, pair.Length);
        var first = pair[0];
        var second = pair[1];
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.NotEqual(first.RootRunId, second.RootRunId);
        AssertRootIsSelf(first);
        AssertRootIsSelf(second);
    }

    [Fact]
    public async Task Independent_scripts_have_different_roots()
    {
        await using var host = new FakeHost();
        var seen = new ConcurrentQueue<Sdk.ExtensionCallContext>();
        host.On("code.execute", (message, _) =>
        {
            seen.Enqueue(BoundRoots.ContextOf(message));
            return Task.FromResult(new Sdk.CodeExecutionResult("ok"));
        });
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var proxy = client.CreateRunCodeTool(new TestRegistry());
        var directory = BoundRoots.AbsoluteDir("independent-scripts");

        await proxy.InvokeAsync(
            new ToolCall("r-1", "run_code", "{\"code\":\"1\"}"),
            new ToolContext("sess-s", directory, 0), limit.Token);
        await proxy.InvokeAsync(
            new ToolCall("r-2", "run_code", "{\"code\":\"2\"}"),
            new ToolContext("sess-s", directory, 0), limit.Token);

        var pair = seen.ToArray();
        Assert.Equal(2, pair.Length);
        var first = pair[0];
        var second = pair[1];
        Assert.NotEqual(first.RootRunId, second.RootRunId);
        AssertRootIsSelf(first);
        AssertRootIsSelf(second);
    }

    [Fact]
    public async Task Forged_rootRunId_in_the_callback_is_ignored()
    {
        var betaSource = new TaskCompletionSource<ExtensionHostTool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new StubTool("bridge", ToolSideEffect.ReadOnly, async (call, context, ct) =>
        {
            var beta = await betaSource.Task.ConfigureAwait(false);
            return await beta.InvokeAsync(new ToolCall("nested-1", "work", "{}"), context, ct).ConfigureAwait(false);
        });
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: bridge.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var setup = await StartChainAsync(host, client, betaSource, limit.Token);

        var callback = host.CallClientAsync("host.tool_call", ForgedRootPayload(setup.RunA), limit.Token);
        var nested = await setup.NestedArrived.Task.Bounded();
        var nestedContext = BoundRoots.ContextOf(nested);

        Assert.NotEqual("forged", nestedContext.RunId);
        Assert.NotEqual("forged-root", nestedContext.RootRunId);
        Assert.Equal(setup.RootA, nestedContext.RootRunId);
        Assert.NotEqual(setup.RunA, nestedContext.RunId);

        setup.HoldB.Content = "work-done";
        setup.HoldB.Gate.TrySetResult();
        Assert.Equal(("work-done", false), BoundRoots.OutputOf(await callback.Bounded()));
        setup.HoldA.Gate.TrySetResult();
        Assert.False((await setup.RootTask.Bounded()).IsError);
    }

    private static void AssertRootIsSelf(Sdk.ExtensionCallContext context)
    {
        Assert.False(string.IsNullOrWhiteSpace(context.RootRunId));
        Assert.Equal(context.RunId, context.RootRunId);
    }

    /// <summary>A bound <c>bridge</c> callback for <paramref name="runId"/> followed by a worker-supplied root id and a
    /// whole forged context (run "forged", root "forged-root") after the four documented elements.</summary>
    private static RawPayload ForgedRootPayload(string runId)
    {
        var bound = IpcSerializer.Serialize(HostWire.Forward(runId, "alpha", "bridge", "{}"));
        Assert.Equal(0x94, bound[0]);
        var forgedContext = new Sdk.ExtensionCallContext("forged", "FORGED", "C:/forged", 9, [])
        {
            RootRunId = "forged-root",
        };
        var payload = new List<byte>(bound) { [0] = 0x96 };
        payload.AddRange(IpcSerializer.Serialize("forged-root"));
        payload.AddRange(IpcSerializer.Serialize(forgedContext));
        return new RawPayload(payload.ToArray());
    }

    private sealed record ChainSetup(
        string RunA, string RootA, InFlightRoot HoldA, InFlightRoot HoldB,
        Task<ToolResult> RootTask, TaskCompletionSource<IpcMessage> NestedArrived);

    private static async Task<ChainSetup> StartChainAsync(
        FakeHost host, ExtensionHostClient client,
        TaskCompletionSource<ExtensionHostTool> betaSource, CancellationToken ct)
    {
        var directory = BoundRoots.AbsoluteDir("rootchain");
        var holdA = new InFlightRoot();
        var holdB = new InFlightRoot { Content = "work-done" };
        var nestedArrived = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On("plugin.execute", async (message, ct2) =>
        {
            var plugin = message.Read<PluginExecuteRequest>().PluginId;
            if (string.Equals(plugin, "beta", StringComparison.Ordinal))
            {
                nestedArrived.TrySetResult(message);
                await holdB.Gate.Task.WaitAsync(ct2).ConfigureAwait(false);
                return holdB.Reply(message.Operation);
            }

            holdA.Arrived.TrySetResult(message);
            await holdA.Gate.Task.WaitAsync(ct2).ConfigureAwait(false);
            return holdA.Reply(message.Operation);
        });
        var alphaProxy = await BoundRoots.RegisterPluginToolAsync(client, host, "alpha", "entry");
        var betaProxy = await BoundRoots.RegisterPluginToolAsync(client, host, "beta", "work");
        betaSource.TrySetResult(betaProxy);
        var rootTask = alphaProxy.InvokeAsync(
            new ToolCall("root-1", "entry", "{}"),
            new ToolContext("sess-chain", directory, 0),
            ct).AsTask();
        var rootRequest = await holdA.Arrived.Task.Bounded();
        var rootContext = BoundRoots.ContextOf(rootRequest);
        return new ChainSetup(
            BoundRoots.RunIdOf(rootRequest),
            rootContext.RootRunId!,
            holdA, holdB, rootTask, nestedArrived);
    }

    private static async Task<ExtensionHostCommand> RegisterCommandAsync(
        FakeHost host, ExtensionHostClient client, string pluginId, string name)
    {
        await host.NotifyAndSyncAsync(
            Sdk.ExtensionProtocol.ContributionsRegister,
            HostWire.Contributions(
                pluginId, new Sdk.PluginContributionSet { Commands = [new Sdk.CommandDeclaration(name, "d", "")] }));
        return (ExtensionHostCommand)client.Commands.Single(c => c.Definition.Name == name);
    }

}
