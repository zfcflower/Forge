using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// The single-call cancel (wave W8 follow-up): every tool call runs on its own token linked to the turn's, so
/// <see cref="AgentHost.CancelToolCall"/> stops exactly that call - the loop answers it with a "cancelled by the user"
/// tool error and the model goes on - while a turn cancel still takes every call down. A call that finishes while the stop
/// is pressed never throws and never leaks its token source, a call that ignores the token keeps its real result, and a
/// stopped <c>spawn_agent</c> call stops its child.
/// </summary>
public class ToolCallCancelTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    /// <summary>A tool that runs until its own token is cancelled, recording that it saw the cancel.</summary>
    private static FakeTool Hanging(string name, TaskCompletionSource? observed = null) =>
        new(name, ToolSideEffect.ReadOnly, async (_, _, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                observed?.TrySetResult();
                throw;
            }

            return new ToolResult("never");
        });

    /// <summary>Runs the root and lets <paramref name="onEvent"/> react to every event as it arrives (on the reader's thread).</summary>
    private static async Task<List<AgentEvent>> RunAsync(
        AgentHost host,
        AgentInstance root,
        Action<AgentEvent> onEvent,
        CancellationToken ct = default)
    {
        var events = new List<AgentEvent>();
        var run = Task.Run(async () =>
        {
            try
            {
                await foreach (var value in host.RunAsync(root, "go", ct))
                {
                    events.Add(value);
                    onEvent(value);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        });
        await run.WaitAsync(Limit);
        return events;
    }

    private static ToolResult ResultOf(List<AgentEvent> events, string callId) =>
        events.OfType<AgentToolFinished>().Single(finished => finished.Call.Id == callId).Result;

    [Fact]
    public async Task Stopping_one_call_answers_it_as_cancelled_and_the_turn_goes_on()
    {
        var provider = AgentTestScripts.OneToolCallThenSay("hang", finalText: "carried on");
        var host = AgentTestHost.Create(provider, [Hanging("hang")]);
        var root = host.CreateRoot("session", Path.GetTempPath());
        var accepted = false;

        var events = await RunAsync(host, root, value =>
        {
            if (value is AgentToolStarted started)
            {
                accepted = host.CancelToolCall(started.AgentId, started.Call.Id);
            }
        });

        Assert.True(accepted);
        Assert.Empty(events.OfType<AgentFailed>());
        var result = ResultOf(events, "call-1");
        Assert.True(result.IsError);
        Assert.Equal(AgentLoop.CancelledByUserResult, result.Content);
        // The model was asked again with the cancelled result and answered: the turn was not cancelled.
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal("carried on", Turn.Text(root.History[^1]));
        var toolResult = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.True(toolResult.IsError);
        Assert.Equal(AgentLoop.CancelledByUserResult, toolResult.Content);
        Assert.Equal(0, host.RunningToolCallCount);
    }

    [Fact]
    public async Task Stopping_one_call_of_a_parallel_batch_leaves_the_other_call_running_to_its_result()
    {
        var stoppedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bCancelled = false;
        var toolB = new FakeTool("read-b", ToolSideEffect.ReadOnly, async (_, _, ct) =>
        {
            // B outlives A's stop, so a stop that leaked into B's token would be seen here.
            await stoppedA.Task.WaitAsync(Limit, CancellationToken.None);
            await Task.Delay(50, CancellationToken.None);
            bCancelled = ct.IsCancellationRequested;
            return new ToolResult("rb");
        });
        var provider = new FakeProvider((_, index) => index == 0
            ? Turn.Call(("call-a", "read-a", "{}"), ("call-b", "read-b", "{}"))
            : Turn.Say("done"));
        var host = AgentTestHost.Create(provider, [Hanging("read-a", stoppedA), toolB]);
        var root = host.CreateRoot("session", Path.GetTempPath());

        var events = await RunAsync(host, root, value =>
        {
            if (value is AgentToolStarted { Call.Id: "call-a" } started)
            {
                Assert.True(host.CancelToolCall(started.AgentId, "call-a"));
            }
        });

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Equal(AgentLoop.CancelledByUserResult, ResultOf(events, "call-a").Content);
        var b = ResultOf(events, "call-b");
        Assert.False(b.IsError);
        Assert.Equal("rb", b.Content);
        Assert.False(bCancelled);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(0, host.RunningToolCallCount);
    }

    [Fact]
    public async Task A_turn_cancel_still_cancels_every_running_call()
    {
        var sawA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider((_, index) => index == 0
            ? Turn.Call(("call-a", "read-a", "{}"), ("call-b", "read-b", "{}"))
            : Turn.Say("never asked"));
        var host = AgentTestHost.Create(provider, [Hanging("read-a", sawA), Hanging("read-b", sawB)]);
        var root = host.CreateRoot("session", Path.GetTempPath());
        using var turn = new CancellationTokenSource();
        var started = 0;

        var events = await RunAsync(
            host,
            root,
            value =>
            {
                if (value is AgentToolStarted && ++started == 2)
                {
                    turn.Cancel();
                }
            },
            turn.Token);

        await Task.WhenAll(sawA.Task, sawB.Task).WaitAsync(Limit);
        // The turn ended: the model was never asked again, and no call was answered as a user stop.
        Assert.Single(provider.Requests);
        Assert.DoesNotContain(
            events.OfType<AgentToolFinished>(),
            finished => finished.Result.Content == AgentLoop.CancelledByUserResult);
        Assert.Equal(0, host.RunningToolCallCount);
    }

    [Fact]
    public async Task A_stop_racing_the_calls_own_finish_never_throws_and_leaks_no_token_source()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var tool = new FakeTool("quick", ToolSideEffect.ReadOnly, async (_, _, ct) =>
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                return new ToolResult("ok");
            });
            var provider = AgentTestScripts.OneToolCallThenSay("quick");
            var host = AgentTestHost.Create(provider, [tool]);
            var root = host.CreateRoot("session", Path.GetTempPath());
            Task<bool>? stop = null;

            var events = await RunAsync(host, root, value =>
            {
                if (value is AgentToolStarted started)
                {
                    // From another thread, as the UI's press would land: before, during or after the call's own return.
                    stop = Task.Run(() => host.CancelToolCall(started.AgentId, started.Call.Id));
                }
            });

            Assert.NotNull(stop);
            await stop!.WaitAsync(Limit);
            Assert.Empty(events.OfType<AgentFailed>());
            var result = ResultOf(events, "call-1");
            Assert.True(
                result.Content is "ok" or AgentLoop.CancelledByUserResult,
                $"iteration {iteration}: unexpected result '{result.Content}'");
            Assert.Equal(2, provider.Requests.Count);
            Assert.Equal(0, host.RunningToolCallCount);

            // Once the call is gone a stop has nothing to cancel.
            Assert.False(host.CancelToolCall(root.Id, "call-1"));
        }
    }

    [Fact]
    public async Task A_call_that_ignores_its_token_keeps_its_real_result()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new FakeTool("stubborn", ToolSideEffect.ReadOnly, async (_, _, _) =>
        {
            await release.Task.WaitAsync(Limit);
            return new ToolResult("finished anyway");
        });
        var provider = AgentTestScripts.OneToolCallThenSay("stubborn");
        var host = AgentTestHost.Create(provider, [tool]);
        var root = host.CreateRoot("session", Path.GetTempPath());
        var accepted = false;
        var again = true;

        var events = await RunAsync(host, root, value =>
        {
            if (value is AgentToolStarted started)
            {
                accepted = host.CancelToolCall(started.AgentId, started.Call.Id);
                // A second press on a call already stopping is refused: there is nothing more to cancel.
                again = host.CancelToolCall(started.AgentId, started.Call.Id);
                release.TrySetResult();
            }
        });

        Assert.True(accepted);
        Assert.False(again);
        var result = ResultOf(events, "call-1");
        Assert.False(result.IsError);
        Assert.Equal("finished anyway", result.Content);
        Assert.Equal(0, host.RunningToolCallCount);
    }

    [Theory]
    [InlineData("root", "no-such-call")]
    [InlineData("no-such-agent", "call-1")]
    [InlineData("", "call-1")]
    [InlineData("root", "")]
    [InlineData(null, null)]
    public void A_stop_for_a_call_that_is_not_running_is_refused(string? agentId, string? callId)
    {
        var host = AgentTestHost.Create(AgentTestScripts.OneToolCallThenSay("x"), [FakeTool.Echo("x")]);

        Assert.False(host.CancelToolCall(agentId!, callId!));
        Assert.Equal(0, host.RunningToolCallCount);
    }

    [Fact]
    public async Task Stopping_a_spawn_agent_call_stops_the_child_and_the_parent_goes_on()
    {
        var scout = new AgentDefinition("Scout", "scout kind", "scout role", new ModelSettings("scout-model"), ["read"]);
        var childAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(async (request, _, ct) =>
        {
            if (request.Messages.Count == 1 && Turn.LastUserText(request) == "long task")
            {
                childAsked.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    childCancelled.TrySetResult();
                    throw;
                }
            }

            return request.Messages.Any(message => message.Role == MessageRole.Tool)
                ? Turn.Say("parent done")
                : Turn.Call(("call-1", "spawn_agent", """{"kind":"Scout","task":"long task"}"""));
        });
        var host = AgentTestHost.Create(
            provider,
            [FakeTool.Echo("read")],
            options: new AgentOptions { MaxParallelSubAgents = 2, MaxSubAgentDepth = 2, MaxTurns = 10 },
            agents: [scout]);
        var root = host.CreateRoot("session", Path.GetTempPath());
        Task<bool>? stop = null;

        var events = await RunAsync(host, root, value =>
        {
            if (value is AgentSubAgentStarted started)
            {
                // Stop only once the child is really mid-request, so the test proves the child's own work is cancelled
                // (a stop that lands earlier ends the spawn before the child ever asks its model).
                stop = Task.Run(async () =>
                {
                    await childAsked.Task.WaitAsync(Limit);
                    return host.CancelToolCall(started.AgentId, "call-1");
                });
            }
        });

        Assert.NotNull(stop);
        Assert.True(await stop!.WaitAsync(Limit));
        await childCancelled.Task.WaitAsync(Limit);
        var finished = Assert.Single(events.OfType<AgentSubAgentFinished>());
        Assert.Equal(SubAgentStatus.Stopped, finished.Status);
        var result = ResultOf(events, "call-1");
        Assert.True(result.IsError);
        Assert.Equal(AgentLoop.CancelledByUserResult, result.Content);
        Assert.Equal("parent done", Turn.Text(root.History[^1]));
        Assert.Equal(0, host.RunningToolCallCount);
    }
}
