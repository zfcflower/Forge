using System.Collections.Concurrent;
using Forge.Plugin.Sdk;
using Forge.ScriptRuntime;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>Inner-tool tests for the single-use executor, driven directly over the wire: the session serves the
/// executor's <c>host.tool_call</c> requests and the test asserts what the script observed. Nothing here touches
/// internal APIs.</summary>
public sealed class SingleUseExecutorToolTests
{
    private static readonly TimeSpan StepLimit = TimeSpan.FromSeconds(60);
    private static readonly CodeExecutionOptions Options = new();
    private const int RunTimeoutMilliseconds = 15000;

    [Fact]
    public async Task InnerToolCall_LeavesThroughTheBoundFacade_AndReturnsItsReply()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        var runId = SingleUseExecutorArtifacts.NewRunId();
        var seen = new ConcurrentQueue<BoundToolCallRequest>();
        var compiled = await SingleUseExecutorArtifacts.CompileValidAsync(
            "return await Tools.Call(\"echo\", new { text = \"hi\" });", Options, ct);
        var tools = new[] { new ToolDeclaration("echo", "Echoes text.", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly) };
        await using var session = await SingleUseExecutorSession.StartAsync(ct);
        session.ToolResponder = call =>
        {
            seen.Enqueue(call);
            return new ToolResultPayload("echo:hi");
        };
        await SingleUseExecutorArtifacts.LoadAsync(session, compiled, ct);
        var result = await SingleUseExecutorArtifacts.RunOnceAsync(
            session,
            SingleUseExecutorArtifacts.RunRequest(runId, tools, Options, RunTimeoutMilliseconds),
            ct);
        Assert.False(result.IsError, result.Content);
        Assert.Equal("echo:hi", result.Content);
        var callback = Assert.Single(seen);
        Assert.Equal("echo", callback.Name);
        Assert.Equal(runId, callback.RunId);
        Assert.Equal("hi", callback.Arguments.GetProperty("text").GetString());
    }

    [Fact]
    public async Task RunCodeSelfCall_IsDeniedWithoutRecursion()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        var compiled = await SingleUseExecutorArtifacts.CompileValidAsync(
            "try { await Tools.Call(\"run_code\", new {}); return \"SHOULD-NOT\"; } " +
            "catch (ToolCallException ex) { return \"self=\" + ex.Denied; }",
            Options,
            ct);
        await using var session = await SingleUseExecutorSession.StartAsync(ct);
        await SingleUseExecutorArtifacts.LoadAsync(session, compiled, ct);
        var result = await SingleUseExecutorArtifacts.RunOnceAsync(
            session,
            SingleUseExecutorArtifacts.RunRequest(SingleUseExecutorArtifacts.NewRunId(), [], Options, RunTimeoutMilliseconds),
            ct);
        Assert.False(result.IsError, result.Content);
        Assert.Equal("self=True", result.Content);
    }
}
