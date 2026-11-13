using Forge.Abstractions;
using Forge.Ipc;
using Forge.Ipc.Protocol;
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
        await using var client = await ClientFactory.StartAsync(
            host,
            resolver: name => name switch
            {
                "bridge" => bridge,
                "leaf" => leaf,
                _ => null,
            },
            approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var setup = await StartChainAsync(host, client, bridge, betaSource, limit.Token);

        var callbackA = host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(setup.RunA, "alpha", "bridge", "{}"), limit.Token);
        var nestedRequest = await setup.NestedArrived.Task.Bounded();
        var runB = BoundRoots.RunIdOf(nestedRequest);
        Assert.NotEqual(setup.RunA, runB);

        var wire = nestedRequest.Read<PluginExecuteRequest>();
        Assert.Equal("beta", wire.PluginId);
        Assert.Equal("work", wire.Tool);
        var context = wire.Context!;
        Assert.Equal(runB, context.RunId);
        Assert.Equal("sess-chain", context.SessionId);
        Assert.Equal(setup.Directory, context.WorkingDirectory);
        Assert.Equal(1, context.Depth);
        Assert.Equal(new[] { "alpha" }, context.PluginChain);

        var callbackB = await host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(runB, "beta", "leaf", "{}"), limit.Token);
        Assert.Equal(("leaf-ok", false), BoundRoots.OutputOf(callbackB));

        setup.HoldB.Content = "work-done";
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
        await using var client = await ClientFactory.StartAsync(
            host,
            resolver: name => name switch
            {
                "bridge" => bridge,
                "leaf" => leaf,
                _ => null,
            },
            approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var setup = await StartChainAsync(host, client, bridge, betaSource, limit.Token);
        alphaProxy = setup.AlphaProxy;

        var callbackA = host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(setup.RunA, "alpha", "bridge", "{}"), limit.Token);
        var runB = BoundRoots.RunIdOf(await setup.NestedArrived.Task.Bounded());
        var before = host.Requests("plugin.execute").Count;

        var callbackB = await host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(runB, "beta", "leaf", "{}"), limit.Token);

        var content = BoundRoots.RefusedContent(callbackB);
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
            resolver: bridge.Resolver(),
            approval: Approvals.AllowAll);
        clientRef = client;
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var betaSource = new TaskCompletionSource<ExtensionHostTool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var setup = await StartChainAsync(host, client, bridge, betaSource, limit.Token);

        var response = await host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(setup.RunA, "alpha", "bridge", "{}"), limit.Token);
        setup.HoldA.Gate.TrySetResult();
        await setup.RootTask.Bounded();

        var content = BoundRoots.RefusedContent(response);
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
            resolver: bridge.Resolver(),
            approval: Approvals.AllowAll);
        clientRef = client;
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var setup = await BoundRoots.StartScriptRootAsync(host, client, registry, limit.Token);

        var response = await setup.CallAsync(host, "bridge", "{}", limit.Token, pluginId: "$run_code");

        var content = BoundRoots.RefusedContent(response);
        Assert.Contains("run_code cannot be invoked from inside another extension invocation", content);
        Assert.Single(host.Requests("code.execute"));
    }

    [Fact]
    public async Task Script_callback_naming_run_code_on_the_wire_is_refused_before_resolver_and_approval()
    {
        var resolver = new ResolverSpy();
        var approval = new ApprovalSpy();
        await using var host = new FakeHost();
        var registry = new TestRegistry([new StubTool("bridge")]);
        await using var client = await ClientFactory.StartAsync(host, resolver: resolver.Resolve, approval: approval.Approve);
        using var limit = TestLimit.Create();
        var setup = await BoundRoots.StartScriptRootAsync(host, client, registry, limit.Token);

        var response = await setup.CallAsync(host, "run_code", "{\"code\":\"1+1\"}", limit.Token, pluginId: "$run_code");

        var content = BoundRoots.RefusedContent(response);
        Assert.Contains("run_code cannot be invoked from inside another extension invocation", content);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, approval.Calls);
    }

    private sealed record ChainSetup(
        string RunA, string Directory, InFlightRoot HoldA, InFlightRoot HoldB, Task<ToolResult> RootTask,
        TaskCompletionSource<IpcMessage> NestedArrived, ExtensionHostTool AlphaProxy, ExtensionHostTool BetaProxy);

    private static async Task<ChainSetup> StartChainAsync(
        FakeHost host, ExtensionHostClient client, StubTool bridge,
        TaskCompletionSource<ExtensionHostTool> betaSource, CancellationToken ct)
    {
        var directory = BoundRoots.AbsoluteDir("chain");
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
        var runA = BoundRoots.RunIdOf(await holdA.Arrived.Task.Bounded());
        return new ChainSetup(runA, directory, holdA, holdB, rootTask, nestedArrived, alphaProxy, betaProxy);
    }
}
