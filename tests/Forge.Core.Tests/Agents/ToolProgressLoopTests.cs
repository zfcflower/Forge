using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// Tool progress through the real agent loop: every call gets its own <see cref="ToolContext.Progress"/> that emits
/// <see cref="AgentToolProgress"/> for that call between its started and finished events, calls running in parallel never
/// report on each other's call, a silent tool produces no progress events, a tool that fails after reporting still finishes
/// with an error, and progress is never written to the session history or the store.
/// </summary>
public class ToolProgressLoopTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private static FakeTool Reporting(string name, ToolSideEffect sideEffect, params string[] tails) =>
        new(name, sideEffect, (_, context, _) =>
        {
            foreach (var tail in tails)
            {
                context.Progress!(tail);
            }

            return new ValueTask<ToolResult>(new ToolResult("final:" + name));
        });

    private static int IndexOf(List<AgentEvent> events, Func<AgentEvent, bool> match) => events.FindIndex(new Predicate<AgentEvent>(match));

    [Fact]
    public async Task Progress_events_arrive_between_started_and_finished_in_order_with_the_right_agent_and_call()
    {
        var provider = AgentTestScripts.OneToolCallThenSay("shell", callId: "call-1", arguments: """{"x":1}""");
        var host = AgentTestHost.Create(provider, [Reporting("shell", ToolSideEffect.ReadOnly, "one", "one two", "one two three")]);

        var (root, events) = await AgentTestHost.RunRootAsync(host, timeout: Limit);

        Assert.Empty(events.OfType<AgentFailed>());
        var started = IndexOf(events, static e => e is AgentToolStarted);
        var finished = IndexOf(events, static e => e is AgentToolFinished);
        var progress = events.OfType<AgentToolProgress>().ToArray();
        Assert.Equal(["one", "one two", "one two three"], progress.Select(e => e.Tail).ToArray());
        Assert.All(progress, e =>
        {
            Assert.Equal(root.Id, e.AgentId);
            Assert.Equal(new ToolCall("call-1", "shell", """{"x":1}"""), e.Call);
        });
        var positions = events.Select((e, index) => (e, index)).Where(pair => pair.e is AgentToolProgress).Select(pair => pair.index).ToArray();
        Assert.All(positions, position => Assert.InRange(position, started + 1, finished - 1));
        Assert.Equal(positions.Order().ToArray(), positions);
        var done = Assert.Single(events.OfType<AgentToolFinished>());
        Assert.Equal("final:shell", done.Result.Content);
        Assert.Equal(root.Id, Assert.Single(events.OfType<AgentToolStarted>()).AgentId);
    }

    [Fact]
    public async Task Two_parallel_calls_each_report_on_their_own_call()
    {
        // Each tool waits until the other one is running before it reports, so the two report interleaved; sequential
        // execution would never get past the rendezvous and the bounded run would fail.
        var arrivedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolA = new FakeTool("read-a", ToolSideEffect.ReadOnly, async (_, context, _) =>
        {
            arrivedA.TrySetResult();
            await arrivedB.Task.WaitAsync(Limit);
            context.Progress!("a1");
            await Task.Yield();
            context.Progress!("a2");
            return new ToolResult("ra");
        });
        var toolB = new FakeTool("read-b", ToolSideEffect.ReadOnly, async (_, context, _) =>
        {
            arrivedB.TrySetResult();
            await arrivedA.Task.WaitAsync(Limit);
            context.Progress!("b1");
            await Task.Yield();
            context.Progress!("b2");
            return new ToolResult("rb");
        });
        var provider = new FakeProvider((_, index) => index == 0 ? Turn.Call(("call-a", "read-a", "{}"), ("call-b", "read-b", "{}")) : Turn.Say("done"));
        var host = AgentTestHost.Create(provider, [toolA, toolB]);

        var (_, events) = await AgentTestHost.RunRootAsync(host, timeout: TimeSpan.FromSeconds(30));

        Assert.Empty(events.OfType<AgentFailed>());
        var progress = events.OfType<AgentToolProgress>().ToArray();
        Assert.Equal(["a1", "a2"], progress.Where(e => e.Call.Id == "call-a").Select(e => e.Tail).ToArray());
        Assert.Equal(["b1", "b2"], progress.Where(e => e.Call.Id == "call-b").Select(e => e.Tail).ToArray());
        Assert.All(progress.Where(e => e.Call.Id == "call-a"), e => Assert.Equal("read-a", e.Call.Name));
        Assert.All(progress.Where(e => e.Call.Id == "call-b"), e => Assert.Equal("read-b", e.Call.Name));
        Assert.Equal(4, progress.Length);
        foreach (var id in new[] { "call-a", "call-b" })
        {
            var start = IndexOf(events, e => e is AgentToolStarted s && s.Call.Id == id);
            var finish = IndexOf(events, e => e is AgentToolFinished f && f.Call.Id == id);
            Assert.All(
                events.Select((e, index) => (e, index)).Where(pair => pair.e is AgentToolProgress p && p.Call.Id == id),
                pair => Assert.InRange(pair.index, start + 1, finish - 1));
        }
    }

    [Fact]
    public async Task A_tool_that_never_reports_produces_no_progress_events()
    {
        var provider = AgentTestScripts.OneToolCallThenSay("quiet");
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("quiet")]);

        var (_, events) = await AgentTestHost.RunRootAsync(host, timeout: Limit);

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Empty(events.OfType<AgentToolProgress>());
        Assert.Single(events.OfType<AgentToolStarted>());
        Assert.Single(events.OfType<AgentToolFinished>());
    }

    [Fact]
    public async Task A_tool_that_throws_after_reporting_still_finishes_with_an_error_and_the_loop_continues()
    {
        var tool = new FakeTool("boom", ToolSideEffect.ReadOnly, (_, context, _) =>
        {
            context.Progress!("partial output");
            throw new InvalidOperationException("tool exploded");
        });
        var provider = AgentTestScripts.OneToolCallThenSay("boom", finalText: "recovered");
        var host = AgentTestHost.Create(provider, [tool]);

        var (root, events) = await AgentTestHost.RunRootAsync(host, timeout: Limit);

        Assert.Empty(events.OfType<AgentFailed>());
        var started = IndexOf(events, static e => e is AgentToolStarted);
        var progress = IndexOf(events, static e => e is AgentToolProgress);
        var finished = IndexOf(events, static e => e is AgentToolFinished);
        Assert.True(started >= 0 && started < progress && progress < finished);
        Assert.Equal("partial output", ((AgentToolProgress)events[progress]).Tail);
        var result = ((AgentToolFinished)events[finished]).Result;
        Assert.True(result.IsError);
        Assert.Contains("tool exploded", result.Content, StringComparison.Ordinal);
        Assert.Equal("recovered", Turn.Text(root.History[^1]));
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task A_tool_that_reports_and_then_is_cancelled_does_not_emit_progress_after_the_run_ended()
    {
        using var cancellation = new CancellationTokenSource();
        var tool = new FakeTool("hang", ToolSideEffect.ReadOnly, async (_, context, ct) =>
        {
            context.Progress!("working");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ToolResult("never");
        });
        var provider = AgentTestScripts.OneToolCallThenSay("hang");
        var host = AgentTestHost.Create(provider, [tool]);
        var root = host.CreateRoot("session", Path.GetTempPath());

        var events = new List<AgentEvent>();
        var run = Task.Run(async () =>
        {
            await foreach (var value in host.RunAsync(root, "go", cancellation.Token))
            {
                events.Add(value);
                if (value is AgentToolProgress)
                {
                    await cancellation.CancelAsync();
                }
            }
        });
        await run.WaitAsync(Limit);

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Equal("working", Assert.Single(events.OfType<AgentToolProgress>()).Tail);
    }

    [Fact]
    public async Task Progress_is_not_written_to_the_history_or_the_store()
    {
        var store = new InMemorySessionStore();
        var session = await store.CreateSessionAsync(Path.GetTempPath(), "progress", CancellationToken.None);
        const string Secret = "live-tail-marker-7f3a";
        var provider = AgentTestScripts.OneToolCallThenSay("shell");
        var host = AgentTestHost.Create(provider, [Reporting("shell", ToolSideEffect.ReadOnly, Secret, Secret + " more")], store: store);
        var root = host.CreateRoot(session.Id, Path.GetTempPath());

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go")).WaitAsync(Limit);

        Assert.Equal(2, events.OfType<AgentToolProgress>().Count());
        Assert.Equal(
            new[] { MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.Assistant },
            root.History.Select(message => message.Role).ToArray());
        Assert.DoesNotContain(root.History, message => ContainsMarker(message, Secret));
        var stored = store.Appended(session.Id);
        Assert.Equal(root.History.Count, stored.Count);
        Assert.DoesNotContain(stored, message => ContainsMarker(message, Secret));
        var toolResult = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.Equal("final:shell", toolResult.Content);
    }

    private static bool ContainsMarker(ChatMessage message, string marker) =>
        message.Content.Any(block => block switch
        {
            TextBlock text => text.Text.Contains(marker, StringComparison.Ordinal),
            ToolResultBlock result => result.Content.Contains(marker, StringComparison.Ordinal),
            ToolCallBlock call => call.ArgumentsJson.Contains(marker, StringComparison.Ordinal),
            _ => false,
        });
}
