using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;
using Xunit;
using static Forge.HostClient.Tests.ToolFixtures;

namespace Forge.HostClient.Tests;

/// <summary>Root invocation identity (<c>context.rootRunId</c>): the main program assigns the top-level
/// run's own id as its root, nested executions keep the parent's root with a fresh run id, siblings
/// share one root, and independent executions never share one. A worker-supplied root on the wire is
/// ignored: every context on the wire is built from the trusted pending record.</summary>
public sealed class RootInvocationIdentityTests
{
    [Fact]
    public async Task Top_level_plugin_execute_carries_its_own_runId_as_camelCase_rootRunId()
    {
        await using var host = new FakeHost();
        host.Answer("plugin.execute", """{"content":"ok","isError":false}""");
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var proxy = await BoundRoots.RegisterPluginToolAsync(client, host, "alpha", "entry");

        var result = await proxy.InvokeAsync(
            new ToolCall("root-1", "entry", "{}"),
            new ToolContext("sess-root", BoundRoots.AbsoluteDir("root-top")),
            limit.Token);

        Assert.False(result.IsError);
        var context = Assert.Single(host.Requests("plugin.execute")).Arguments!.Value.GetProperty("context");
        AssertRootIsSelf(context);
    }

    [Fact]
    public async Task Top_level_code_execute_carries_its_own_runId_as_rootRunId()
    {
        await using var host = new FakeHost();
        host.Answer("code.execute", """{"content":"ok","isError":false}""");
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var proxy = client.CreateRunCodeTool(new TestRegistry());

        var result = await proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1+1\"}"),
            new ToolContext("sess-rc", BoundRoots.AbsoluteDir("root-code")),
            limit.Token);

        Assert.False(result.IsError);
        var context = Assert.Single(host.Requests("code.execute")).Arguments!.Value.GetProperty("context");
        AssertRootIsSelf(context);
    }

    [Fact]
    public async Task Top_level_command_carries_its_own_runId_as_rootRunId()
    {
        await using var host = new FakeHost();
        host.Answer("plugin.command", """{"output":"hello","promptToSend":"next","isError":false}""");
        await using var client = await ClientFactory.StartAsync(host, approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var command = await RegisterCommandAsync(host, client, "alpha", "hi");

        var result = await command.ExecuteAsync(
            "args", new CommandContext("sess-cmd", BoundRoots.AbsoluteDir("root-cmd")), limit.Token);

        Assert.False(result.IsError);
        var context = Assert.Single(host.Requests("plugin.command")).Arguments!.Value.GetProperty("context");
        AssertRootIsSelf(context);
    }

    [Fact]
    public async Task Top_level_prompt_carries_its_own_runId_as_rootRunId()
    {
        await using var host = new FakeHost();
        host.Answer("plugin.prompt", """{"text":"t"}""");
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();
        var contributor = await RegisterPromptAsync(host, client, "alpha", "tone");

        var text = await client.GetPluginPromptAsync(
            contributor,
            new ToolContext("sess-pr", BoundRoots.AbsoluteDir("root-prompt")),
            limit.Token);

        Assert.Equal("t", text);
        var context = Assert.Single(host.Requests("plugin.prompt")).Arguments!.Value.GetProperty("context");
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
        var nestedArgs = nested.Arguments!.Value;
        var nestedContext = nestedArgs.GetProperty("context");
        var nestedRunId = nestedContext.GetProperty("runId").GetString()!;

        Assert.NotEqual(setup.RunA, nestedRunId);
        Assert.Equal(setup.RunA, setup.RootA);
        Assert.Equal(setup.RootA, nestedContext.GetProperty("rootRunId").GetString());
        Assert.Equal(["alpha"], nestedContext.GetProperty("pluginChain").EnumerateArray()
            .Select(element => element.GetString()!).ToArray());

        setup.HoldB.ResultJson = """{"content":"work-done","isError":false}""";
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
        var nestedCalls = new ConcurrentQueue<JsonElement>();
        var bothNested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdA = new InFlightRoot { ResultJson = """{"content":"root-ok","isError":false}""" };
        host.On("plugin.execute", async (message, ct) =>
        {
            var plugin = message.Arguments!.Value.GetProperty("pluginId").GetString();
            if (!string.Equals(plugin, "beta", StringComparison.Ordinal))
            {
                holdA.Arrived.TrySetResult(message);
                await holdA.Gate.Task.WaitAsync(ct).ConfigureAwait(false);
                return Json.Element(holdA.ResultJson);
            }

            nestedCalls.Enqueue(message.Arguments!.Value.Clone());
            if (nestedCalls.Count >= 2)
            {
                bothNested.TrySetResult();
            }

            return Json.Element("""{"content":"work","isError":false}""");
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
        var firstContext = pair[0].GetProperty("context");
        var secondContext = pair[1].GetProperty("context");
        Assert.NotEqual(
            firstContext.GetProperty("runId").GetString(),
            secondContext.GetProperty("runId").GetString());
        Assert.NotEqual(runA, firstContext.GetProperty("runId").GetString());
        Assert.NotEqual(runA, secondContext.GetProperty("runId").GetString());
        Assert.Equal(runA, firstContext.GetProperty("rootRunId").GetString());
        Assert.Equal(runA, secondContext.GetProperty("rootRunId").GetString());

        Assert.Equal(("work+work", false), BoundRoots.OutputOf(callback));
        holdA.Gate.TrySetResult();
        Assert.False((await rootTask.Bounded()).IsError);
    }

    [Fact]
    public async Task Independent_same_plugin_roots_have_different_roots()
    {
        await using var host = new FakeHost();
        var seen = new ConcurrentQueue<JsonElement>();
        host.On("plugin.execute", (message, _) =>
        {
            seen.Enqueue(message.Arguments!.Value.Clone());
            return Task.FromResult(Json.Element("""{"content":"ok","isError":false}"""));
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
        var first = pair[0].GetProperty("context");
        var second = pair[1].GetProperty("context");
        Assert.NotEqual(
            first.GetProperty("runId").GetString(), second.GetProperty("runId").GetString());
        Assert.NotEqual(
            first.GetProperty("rootRunId").GetString(), second.GetProperty("rootRunId").GetString());
        AssertRootIsSelf(first);
        AssertRootIsSelf(second);
    }

    [Fact]
    public async Task Independent_scripts_have_different_roots()
    {
        await using var host = new FakeHost();
        var seen = new ConcurrentQueue<JsonElement>();
        host.On("code.execute", (message, _) =>
        {
            seen.Enqueue(message.Arguments!.Value.Clone());
            return Task.FromResult(Json.Element("""{"content":"ok","isError":false}"""));
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
        var first = pair[0].GetProperty("context");
        var second = pair[1].GetProperty("context");
        Assert.NotEqual(
            first.GetProperty("rootRunId").GetString(), second.GetProperty("rootRunId").GetString());
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

        var plain = BoundRoots.BoundPayload(setup.RunA, "alpha", "bridge", "{}");
        var forged = plain.Substring(0, plain.Length - 1)
            + ",\"rootRunId\":\"forged-root\",\"context\":{\"runId\":\"forged\",\"sessionId\":\"FORGED\","
            + "\"workingDirectory\":\"C:/forged\",\"depth\":9,\"pluginChain\":[],\"rootRunId\":\"forged-root\"}}";
        var callback = host.CallClientAsync("host.tool_call", forged, limit.Token);
        var nested = await setup.NestedArrived.Task.Bounded();
        var nestedContext = nested.Arguments!.Value.GetProperty("context");

        Assert.NotEqual("forged", nestedContext.GetProperty("runId").GetString());
        Assert.NotEqual("forged-root", nestedContext.GetProperty("rootRunId").GetString());
        Assert.Equal(setup.RootA, nestedContext.GetProperty("rootRunId").GetString());
        Assert.NotEqual(setup.RunA, nestedContext.GetProperty("runId").GetString());

        setup.HoldB.ResultJson = """{"content":"work-done","isError":false}""";
        setup.HoldB.Gate.TrySetResult();
        Assert.Equal(("work-done", false), BoundRoots.OutputOf(await callback.Bounded()));
        setup.HoldA.Gate.TrySetResult();
        Assert.False((await setup.RootTask.Bounded()).IsError);
    }

    private static void AssertRootIsSelf(JsonElement context)
    {
        var names = context.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Contains("rootRunId", names);
        Assert.DoesNotContain("RootRunId", names);
        Assert.Equal(
            context.GetProperty("runId").GetString(),
            context.GetProperty("rootRunId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(context.GetProperty("rootRunId").GetString()));
    }

    private sealed record ChainSetup(
        string RunA, string RootA, InFlightRoot HoldA, InFlightRoot HoldB,
        Task<ToolResult> RootTask, TaskCompletionSource<IpcMessage> NestedArrived);

    private static async Task<ChainSetup> StartChainAsync(
        FakeHost host, ExtensionHostClient client,
        TaskCompletionSource<ExtensionHostTool> betaSource, CancellationToken ct)
    {
        var directory = BoundRoots.AbsoluteDir("rootchain");
        var holdA = new InFlightRoot { ResultJson = """{"content":"root-ok","isError":false}""" };
        var holdB = new InFlightRoot { ResultJson = """{"content":"work-done","isError":false}""" };
        var nestedArrived = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On("plugin.execute", async (message, ct2) =>
        {
            var plugin = message.Arguments!.Value.GetProperty("pluginId").GetString();
            if (string.Equals(plugin, "beta", StringComparison.Ordinal))
            {
                nestedArrived.TrySetResult(message);
                await holdB.Gate.Task.WaitAsync(ct2).ConfigureAwait(false);
                return Json.Element(holdB.ResultJson);
            }

            holdA.Arrived.TrySetResult(message);
            await holdA.Gate.Task.WaitAsync(ct2).ConfigureAwait(false);
            return Json.Element(holdA.ResultJson);
        });
        var alphaProxy = await BoundRoots.RegisterPluginToolAsync(client, host, "alpha", "entry");
        var betaProxy = await BoundRoots.RegisterPluginToolAsync(client, host, "beta", "work");
        betaSource.TrySetResult(betaProxy);
        var rootTask = alphaProxy.InvokeAsync(
            new ToolCall("root-1", "entry", "{}"),
            new ToolContext("sess-chain", directory, 0),
            ct).AsTask();
        var rootRequest = await holdA.Arrived.Task.Bounded();
        var rootContext = rootRequest.Arguments!.Value.GetProperty("context");
        return new ChainSetup(
            BoundRoots.RunIdOf(rootRequest),
            rootContext.GetProperty("rootRunId").GetString()!,
            holdA, holdB, rootTask, nestedArrived);
    }

    private static async Task<ExtensionHostCommand> RegisterCommandAsync(
        FakeHost host, ExtensionHostClient client, string pluginId, string name)
    {
        await host.NotifyAndSyncAsync("contributions.register",
            "{\"pluginId\":\"" + pluginId + "\",\"contributions\":{\"commands\":["
            + "{\"name\":\"" + name + "\",\"description\":\"d\",\"argumentHint\":\"\"}]}}");
        return (ExtensionHostCommand)client.Commands.Single(c => c.Definition.Name == name);
    }

}
