using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;
using Xunit;

namespace Forge.HostClient.Tests;

/// <summary>Call-chain recursion: plugin A→B is allowed with the trusted parent chain, B→A is refused
/// locally, and any nested <c>run_code</c> is refused before it reaches the resolver, approval or IPC.</summary>
public sealed class HostToolCallRecursionTests
{
    private const string Operation = "host.tool_call";

    [Fact]
    public async Task Plugin_A_to_B_is_allowed_with_the_trusted_session_chain_depth_and_fresh_ids()
    {
        var betaSource = new TaskCompletionSource<ExtensionHostTool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new StubTool("bridge", ToolSideEffect.ReadOnly, async (call, context, ct) =>
        {
            var beta = await betaSource.Task.ConfigureAwait(false);
            return await beta.InvokeAsync(new ToolCall("nested-1", "work", "{}"), context, ct).ConfigureAwait(false);
        });
        var leaf = new StubTool("leaf", ToolSideEffect.ReadOnly, (_, _, _) =>
            ValueTask.FromResult(new ToolResult("leaf-ok")));
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name switch
            {
                "bridge" => bridge,
                "leaf" => leaf,
                _ => null,
            },
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var setup = await StartChainAsync(host, client, bridge, betaSource, limit.Token);

        var callbackA = host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(setup.RunA, "alpha", "bridge", "{}"), limit.Token);
        var nestedRequest = await setup.NestedArrived.Task.Bounded();
        var runB = BoundRoots.RunIdOf(nestedRequest);
        Assert.NotEqual(setup.RunA, runB);

        var wire = nestedRequest.Arguments!.Value;
        Assert.Equal("beta", wire.GetProperty("pluginId").GetString());
        Assert.Equal("work", wire.GetProperty("tool").GetString());
        var context = wire.GetProperty("context");
        Assert.Equal(runB, context.GetProperty("runId").GetString());
        Assert.Equal("sess-chain", context.GetProperty("sessionId").GetString());
        Assert.Equal(setup.Directory, context.GetProperty("workingDirectory").GetString());
        Assert.Equal(1, context.GetProperty("depth").GetInt32());
        Assert.Equal(new[] { "alpha" }, context.GetProperty("pluginChain").EnumerateArray()
            .Select(element => element.GetString()!).ToArray());
        Assert.Equal("sess-chain", wire.GetProperty("sessionId").GetString());
        Assert.Equal(setup.Directory, wire.GetProperty("workingDir").GetString());
        Assert.Equal(1, wire.GetProperty("depth").GetInt32());

        var callbackB = await host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(runB, "beta", "leaf", "{}"), limit.Token);
        Assert.Equal(("leaf-ok", false), BoundRoots.OutputOf(callbackB));

        setup.HoldB.ResultJson = """{"content":"work-done","isError":false}""";
        setup.HoldB.Gate.TrySetResult();
        Assert.Equal(("work-done", false), BoundRoots.OutputOf(await callbackA.Bounded()));
        setup.HoldA.Gate.TrySetResult();
        var rootResult = await setup.RootTask.Bounded();
        Assert.False(rootResult.IsError);

        var invocation = Assert.Single(leaf.Invocations);
        Assert.Equal(new ToolContext("sess-chain", setup.Directory, 2), invocation.Context);
    }

    [Fact]
    public async Task Plugin_B_back_to_A_is_refused_locally_without_IPC()
    {
        var betaSource = new TaskCompletionSource<ExtensionHostTool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ExtensionHostTool? alphaProxy = null;
        var leaf = new StubTool("leaf", ToolSideEffect.ReadOnly, async (call, context, ct) =>
            await alphaProxy!.InvokeAsync(new ToolCall("cycle-1", "entry", "{}"), context, ct).ConfigureAwait(false));
        var bridge = new StubTool("bridge", ToolSideEffect.ReadOnly, async (call, context, ct) =>
        {
            var beta = await betaSource.Task.ConfigureAwait(false);
            return await beta.InvokeAsync(new ToolCall("nested-1", "work", "{}"), context, ct).ConfigureAwait(false);
        });
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name switch
            {
                "bridge" => bridge,
                "leaf" => leaf,
                _ => null,
            },
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var setup = await StartChainAsync(host, client, bridge, betaSource, limit.Token);
        alphaProxy = setup.AlphaProxy;

        var callbackA = host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(setup.RunA, "alpha", "bridge", "{}"), limit.Token);
        var runB = BoundRoots.RunIdOf(await setup.NestedArrived.Task.Bounded());
        var before = host.Requests("plugin.execute").Count;

        var callbackB = await host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(runB, "beta", "leaf", "{}"), limit.Token);

        var (content, isError) = BoundRoots.OutputOf(callbackB);
        Assert.True(isError);
        Assert.Contains("already on the call chain", content);
        Assert.Contains("'alpha'", content);
        Assert.Equal(before, host.Requests("plugin.execute").Count);
        setup.HoldB.Gate.TrySetResult();
        Assert.Equal(("work-done", false), BoundRoots.OutputOf(await callbackA.Bounded()));
        setup.HoldA.Gate.TrySetResult();
        await setup.RootTask.Bounded();
    }

    [Fact]
    public async Task Plugin_callback_to_run_code_is_refused_without_IPC()
    {
        await using var host = new FakeHost();
        var registry = new TestRegistry([new StubTool("leaf")]);
        ExtensionHostClient? clientRef = null;
        var bridge = new StubTool("bridge", ToolSideEffect.ReadOnly, (call, context, ct) =>
        {
            var proxy = clientRef!.CreateRunCodeTool(registry);
            return proxy.InvokeAsync(
                new ToolCall("nested-rc", "run_code", "{\"code\":\"1+1\"}"), context, ct);
        });
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "bridge" ? bridge : null,
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        clientRef = client;
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var betaSource = new TaskCompletionSource<ExtensionHostTool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var setup = await StartChainAsync(host, client, bridge, betaSource, limit.Token);

        var response = await host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(setup.RunA, "alpha", "bridge", "{}"), limit.Token);
        setup.HoldA.Gate.TrySetResult();
        await setup.RootTask.Bounded();

        var (content, isError) = BoundRoots.OutputOf(response);
        Assert.True(isError);
        Assert.Contains("run_code cannot be invoked", content);
        Assert.Empty(host.Requests("code.execute"));
    }

    [Fact]
    public async Task Script_callback_to_run_code_through_the_proxy_is_refused_without_IPC()
    {
        await using var host = new FakeHost();
        var registry = new TestRegistry();
        ExtensionHostClient? clientRef = null;
        var bridge = new StubTool("bridge", ToolSideEffect.ReadOnly, (call, context, ct) =>
        {
            var proxy = clientRef!.CreateRunCodeTool(registry);
            return proxy.InvokeAsync(
                new ToolCall("nested-rc", "run_code", "{\"code\":\"1+1\"}"), context, ct);
        });
        registry.Add(bridge);
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "bridge" ? bridge : null,
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        clientRef = client;
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var setup = await StartScriptRootAsync(host, client, registry, limit.Token);

        var response = await host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(setup.RunId, "$run_code", "bridge", "{}"), limit.Token);
        setup.Hold.Gate.TrySetResult();
        await setup.RootTask.Bounded();

        var (content, isError) = BoundRoots.OutputOf(response);
        Assert.True(isError);
        Assert.Contains("run_code cannot be invoked from inside another extension invocation", content);
        Assert.Single(host.Requests("code.execute"));
    }

    [Fact]
    public async Task Script_callback_naming_run_code_on_the_wire_is_refused_before_resolver_and_approval()
    {
        var resolved = 0;
        var approved = 0;
        await using var host = new FakeHost();
        var registry = new TestRegistry([new StubTool("bridge")]);
        await using var client = ClientFactory.Create(
            host,
            resolver: _ =>
            {
                Interlocked.Increment(ref resolved);
                return null;
            },
            approval: (_, _) =>
            {
                Interlocked.Increment(ref approved);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var setup = await StartScriptRootAsync(host, client, registry, limit.Token);

        var response = await host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(setup.RunId, "$run_code", "run_code", "{\"code\":\"1+1\"}"), limit.Token);
        setup.Hold.Gate.TrySetResult();
        await setup.RootTask.Bounded();

        var (content, isError) = BoundRoots.OutputOf(response);
        Assert.True(isError);
        Assert.Contains("run_code cannot be invoked from inside another extension invocation", content);
        Assert.Equal(0, resolved);
        Assert.Equal(0, approved);
    }

    private sealed record ChainSetup(
        string RunA, string Directory, InFlightRoot HoldA, InFlightRoot HoldB, Task<ToolResult> RootTask,
        TaskCompletionSource<IpcMessage> NestedArrived, ExtensionHostTool AlphaProxy, ExtensionHostTool BetaProxy);

    private sealed record ScriptSetup(string RunId, InFlightRoot Hold, Task<ToolResult> RootTask);

    private static async Task<ChainSetup> StartChainAsync(
        FakeHost host, ExtensionHostClient client, StubTool bridge,
        TaskCompletionSource<ExtensionHostTool> betaSource, CancellationToken ct)
    {
        var directory = BoundRoots.AbsoluteDir("chain");
        var holdA = new InFlightRoot();
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
        var runA = BoundRoots.RunIdOf(await holdA.Arrived.Task.Bounded());
        return new ChainSetup(runA, directory, holdA, holdB, rootTask, nestedArrived, alphaProxy, betaProxy);
    }

    private static async Task<ScriptSetup> StartScriptRootAsync(
        FakeHost host, ExtensionHostClient client, TestRegistry registry, CancellationToken ct)
    {
        var hold = new InFlightRoot { ResultJson = """{"content":"script-ok","isError":false}""" };
        BoundRoots.ParkRoot(host, "code.execute", hold);
        var proxy = client.CreateRunCodeTool(registry);
        var rootTask = proxy.InvokeAsync(
            new ToolCall("root-rc", "run_code", "{\"code\":\"1+1\"}"),
            new ToolContext("sess-script", BoundRoots.AbsoluteDir("script"), 0),
            ct).AsTask();
        var runId = BoundRoots.RunIdOf(await hold.Arrived.Task.Bounded());
        return new ScriptSetup(runId, hold, rootTask);
    }
}
