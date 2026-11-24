using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Concurrent tool calls in one batch answer for themselves. A turn that runs its read-only calls in parallel — four
/// <c>spawn_agent</c> calls are exactly that — used to be awaited as a bare set: when one call was stopped on its own the
/// await threw and every other call's finished result was discarded, so the model was told "Cancelled." four times while
/// three of the four children were still running (incident: Worker 2, 2026-11-24 13:04–13:16 UTC). The fan-out is total
/// now: every call still starts at once and the batch still waits for all of them, but each outcome is recorded against
/// its own call as it is known.
/// </summary>
public sealed class ToolBatchOutcomeTests
{
    [Fact]
    public async Task One_stopped_call_keeps_its_siblings_results()
    {
        var calls = new[] { Call("a"), Call("b"), Call("c"), Call("d") };
        var results = new ContentBlock?[calls.Length];

        await AgentLoop.RunCallsAsync(calls, results, 0, calls.Length, call => call.Id switch
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
        var calls = new[] { Call("a"), Call("b"), Call("c"), Call("d") };
        var results = new ContentBlock?[calls.Length];
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await AgentLoop.RunCallsAsync(calls, results, 0, calls.Length, async call =>
        {
            if (Interlocked.Increment(ref started) == calls.Length)
            {
                allStarted.TrySetResult();
            }

            // Every call waits for the last one to start: only a genuinely concurrent fan-out gets past this. The wait is
            // bounded so a regression back to serial execution fails in seconds instead of stalling the test run.
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return new ToolResultBlock(call.Id, "done " + call.Id);
        });

        Assert.Equal(4, started);
        Assert.Equal(["done a", "done b", "done c", "done d"], results.Select(block => ((ToolResultBlock)block!).Content));
    }

    [Fact]
    public async Task An_outcome_is_recorded_as_its_call_finishes()
    {
        var calls = new[] { Call("slow"), Call("fast") };
        var results = new ContentBlock?[calls.Length];
        var slow = new TaskCompletionSource<ContentBlock>(TaskCreationOptions.RunContinuationsAsynchronously);

        var batch = AgentLoop.RunCallsAsync(calls, results, 0, calls.Length, call =>
            call.Id == "slow" ? slow.Task : Task.FromResult<ContentBlock>(new ToolResultBlock("fast", "done fast")));
        await Task.Yield();

        // The fast call is on record while the batch is still waiting for the slow one: a cancelled turn cannot lose what
        // already finished, and the slow call's slot stays empty until it does.
        Assert.Equal("done fast", Assert.IsType<ToolResultBlock>(results[1]).Content);
        Assert.Null(results[0]);
        Assert.False(batch.IsCompleted);

        slow.SetResult(new ToolResultBlock("slow", "done slow"));
        await batch;
        Assert.Equal("done slow", Assert.IsType<ToolResultBlock>(results[0]).Content);
    }

    private static ToolCallBlock Call(string id) => new(id, "spawn_agent", "{}");
}
