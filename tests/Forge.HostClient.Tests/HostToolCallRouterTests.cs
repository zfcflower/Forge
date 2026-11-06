using System.Collections.Concurrent;
using Forge.Abstractions;
using Xunit;

namespace Forge.HostClient.Tests;

/// <summary>The session-aware router seam: tool resolution and approval use the session the run was registered with
/// (the Host-validated context), never a session the worker claims in the message.</summary>
public sealed class HostToolCallRouterTests
{
    private const string Operation = "host.tool_call";

    private sealed class RecordingRouter(StubTool? tool, ApprovalDecision decision) : IHostToolCallRouter
    {
        public ConcurrentQueue<(string Session, string Tool)> Resolved { get; } = new();

        public ConcurrentQueue<(string Session, ApprovalRequest Request)> Approved { get; } = new();

        public ITool? Resolve(string sessionId, string toolName)
        {
            Resolved.Enqueue((sessionId, toolName));
            return tool is not null && tool.Name == toolName ? tool : null;
        }

        public ValueTask<ApprovalDecision> ApproveAsync(string sessionId, ApprovalRequest request, CancellationToken ct)
        {
            Approved.Enqueue((sessionId, request));
            return ValueTask.FromResult(decision);
        }
    }

    private static ExtensionHostClient Create(FakeHost host, IHostToolCallRouter router) =>
        new(ClientFactory.Connection(host), router, null);

    private static async Task<(string RunId, InFlightRoot Hold, Task<ToolResult> RootTask)> StartRootAsync(
        FakeHost host, ExtensionHostClient client, string sessionId, CancellationToken ct)
    {
        var root = new InFlightRoot();
        BoundRoots.ParkRoot(host, "plugin.execute", root);
        var proxy = await BoundRoots.RegisterPluginToolAsync(client, host, "alpha", "entry");
        var context = new Forge.Abstractions.ToolContext(sessionId, BoundRoots.AbsoluteDir("root"));
        var rootTask = await BoundRoots.StartPluginRootAsync(proxy, context, root, ct);
        var runId = BoundRoots.RunIdOf(await root.Arrived.Task.Bounded());
        return (runId, root, rootTask);
    }

    [Fact]
    public async Task The_router_receives_the_session_the_run_was_registered_with_not_a_forged_one()
    {
        var tool = new StubTool("echo", ToolSideEffect.Write);
        var router = new RecordingRouter(tool, ApprovalDecision.Allow);
        await using var host = new FakeHost();
        await using var client = Create(host, router);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var (runId, hold, rootTask) = await StartRootAsync(host, client, "sess-real", limit.Token);

        var response = await host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(runId, "alpha", "echo", "{}", forgeLegacy: true), limit.Token);
        hold.Release();
        await rootTask.Bounded();

        Assert.False(BoundRoots.OutputOf(response).IsError);
        Assert.Equal(("sess-real", "echo"), Assert.Single(router.Resolved));
        var approved = Assert.Single(router.Approved);
        Assert.Equal("sess-real", approved.Session);
        Assert.Equal("echo", approved.Request.ToolName);
        Assert.Equal("sess-real", Assert.Single(tool.Invocations).Context.SessionId);
    }

    [Fact]
    public async Task A_router_that_resolves_nothing_reports_an_unknown_tool_and_never_asks_approval()
    {
        var router = new RecordingRouter(null, ApprovalDecision.Allow);
        await using var host = new FakeHost();
        await using var client = Create(host, router);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var (runId, hold, rootTask) = await StartRootAsync(host, client, "sess-closed", limit.Token);

        var response = await host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(runId, "alpha", "echo", "{}"), limit.Token);
        hold.Release();
        await rootTask.Bounded();

        Assert.Equal(("Unknown tool 'echo'.", true), BoundRoots.OutputOf(response));
        Assert.Empty(router.Approved);
    }

    [Fact]
    public async Task A_router_denial_means_the_tool_is_not_invoked()
    {
        var tool = new StubTool("echo", ToolSideEffect.Write);
        var router = new RecordingRouter(tool, ApprovalDecision.Deny);
        await using var host = new FakeHost();
        await using var client = Create(host, router);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var (runId, hold, rootTask) = await StartRootAsync(host, client, "sess-a", limit.Token);

        var response = await host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(runId, "alpha", "echo", "{}"), limit.Token);
        hold.Release();
        await rootTask.Bounded();

        var (content, isError) = BoundRoots.OutputOf(response);
        Assert.True(isError);
        Assert.Contains("denied", content);
        Assert.Equal(0, tool.InvocationCount);
    }
}
