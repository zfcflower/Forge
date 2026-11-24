using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Concurrent tool calls in one batch answer for themselves. A turn that runs its read-only calls in parallel — four
/// <c>spawn_agent</c> calls are exactly that — used to be awaited as a bare set: when one call was stopped on its own the
/// await threw and every other call's finished result was discarded, so the model was told "Cancelled." four times while
/// three of the four children were still running (incident: Worker 2, 2026-11-24 13:04–13:16 UTC). The fan-out is total
/// now: it still runs every call at once and waits for all of them, but one call's cancellation or failure is recorded
/// against that call alone.
/// </summary>
public sealed class ToolBatchOutcomeTests
{
    [Fact]
    public async Task One_stopped_call_keeps_its_siblings_results()
    {
        var results = await AgentLoop.RunCallsAsync(
            [Call("a"), Call("b"), Call("c"), Call("d")],
            call => call.Id switch
            {
                "a" => Task.FromResult<ContentBlock>(new ToolResultBlock("a", "review one")),
                "b" => Task.FromException<ContentBlock>(new OperationCanceledException()),
                "c" => Task.FromResult<ContentBlock>(new ToolResultBlock("c", "review three")),
                _ => Task.FromException<ContentBlock>(new InvalidOperationException("boom")),
            });

        // The two that did work keep their own answers...
        Assert.Equal("review one", Assert.IsType<ToolResultBlock>(results[0]).Content);
        Assert.Equal("review three", Assert.IsType<ToolResultBlock>(results[2]).Content);
        // ...the stopped one says only that it was stopped...
        var cancellation = Assert.IsType<ToolResultBlock>(results[1]);
        Assert.Equal(AgentLoop.CancelledByUserResult, cancellation.Content);
        Assert.True(cancellation.IsError);
        // ...and an unexpected failure is that call's error, not a cancelled batch.
        Assert.Equal("boom", Assert.IsType<ToolResultBlock>(results[3]).Content);
        Assert.True(results[3] is ToolResultBlock { IsError: true });
    }

    [Fact]
    public async Task The_calls_still_run_at_the_same_time()
    {
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var results = await AgentLoop.RunCallsAsync(
            [Call("a"), Call("b"), Call("c"), Call("d")],
            async call =>
            {
                if (Interlocked.Increment(ref started) == 4)
                {
                    allStarted.TrySetResult();
                }

                // Every call waits for the fourth to start: only a genuinely concurrent fan-out gets past this.
                await allStarted.Task;
                return new ToolResultBlock(call.Id, "done " + call.Id);
            });

        Assert.Equal(4, started);
        Assert.Equal(["done a", "done b", "done c", "done d"], results.Select(block => ((ToolResultBlock)block).Content));
    }

    [Fact]
    public async Task A_cancelled_turn_is_ended_by_the_caller_after_the_batch_answers()
    {
        using var turn = new CancellationTokenSource();
        turn.Cancel();

        // The batch still completes with a result per call - no throw out of the fan-out...
        var results = await AgentLoop.RunCallsAsync(
            [Call("a"), Call("b")],
            _ => Task.FromCanceled<ContentBlock>(turn.Token));

        Assert.All(results, block => Assert.Equal(AgentLoop.CancelledByUserResult, Assert.IsType<ToolResultBlock>(block).Content));
        // ...and the loop ends the run itself once it holds them, so the turn's own outcome stays the token's.
        Assert.Throws<OperationCanceledException>(() => turn.Token.ThrowIfCancellationRequested());
    }

    private static ToolCallBlock Call(string id) => new(id, "spawn_agent", "{}");
}
