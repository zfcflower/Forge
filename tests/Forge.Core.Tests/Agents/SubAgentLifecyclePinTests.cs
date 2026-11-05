using System.Collections.Concurrent;
using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>Pins the per-child lifecycle ordering of the sub-agent scheduler and loop.</summary>
public sealed class SubAgentLifecyclePinTests
{
    private static string Workspace => Path.GetTempPath();

    private static readonly AgentDefinition Scout =
        new("Scout", "scout kind", "scout role", new ModelSettings("scout-model"), ["read"]);

    private static AgentOptions Options(int maxParallel = 4, int maxDepth = 3, int maxTurns = 10) =>
        new() { MaxParallelSubAgents = maxParallel, MaxSubAgentDepth = maxDepth, MaxTurns = maxTurns };

    private static string Spawn(string kind, string task) =>
        $$"""{"kind":"{{kind}}","task":"{{task}}"}""";

    private static string AgentIdOf(string content)
    {
        const string marker = "[agent_id: ";
        var start = content.LastIndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No agent id in '{content}'.");
        start += marker.Length;
        return content[start..content.IndexOf(']', start)];
    }

    private static int IndexOf<T>(IReadOnlyList<AgentEvent> events, Func<T, bool> match) where T : AgentEvent
    {
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is T typed && match(typed))
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public async Task Child_lifecycle_is_Started_then_Running_then_exactly_one_Finished()
    {
        var provider = new FakeProvider((request, index) =>
            request.Messages.Count == 1 && Turn.LastUserText(request) == "inspect"
                ? Turn.Say("child report")
                : index == 0
                    ? Turn.Call(("call-1", "spawn_agent", Spawn("Scout", "inspect")))
                    : Turn.Say("parent done"));
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout]);
        var root = host.CreateRoot("session-lifecycle-" + Guid.NewGuid().ToString("n"), Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go")).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Empty(events.OfType<AgentFailed>());
        var started = Assert.Single(events.OfType<AgentSubAgentStarted>());
        Assert.Equal("root", started.AgentId);
        Assert.Equal("Scout-1", started.ChildId);
        Assert.Equal("inspect", started.Task);
        Assert.Equal("scout-model", started.Model);
        Assert.Equal(started.ChildSessionId, host.SubAgents.Instances.Single().SessionId);

        var running = Assert.Single(events.OfType<AgentSubAgentRunning>());
        Assert.Equal("Scout-1", running.AgentId);

        var finished = Assert.Single(events.OfType<AgentSubAgentFinished>());
        Assert.Equal("Scout-1", finished.AgentId);
        Assert.Equal(SubAgentStatus.Completed, finished.Status);

        Assert.True(
            IndexOf<AgentSubAgentStarted>(events, _ => true) < IndexOf<AgentSubAgentRunning>(events, _ => true),
            "Started must precede Running.");
        Assert.True(
            IndexOf<AgentSubAgentRunning>(events, _ => true) < IndexOf<AgentSubAgentFinished>(events, _ => true),
            "Running must precede Finished.");
        Assert.Empty(events.OfType<AgentSubAgentQueued>());
    }

    [Fact]
    public async Task Queued_is_emitted_only_when_slots_are_exhausted()
    {
        // MaxParallel = 1 with two parallel spawns: exactly one child must wait.
        var provider = new FakeProvider(async (request, index, ct) =>
        {
            if (request.Messages.Count == 1 && Turn.LastUserText(request) == "go")
            {
                return Turn.Call(
                    ("call-1", "spawn_agent", Spawn("Scout", "task one")),
                    ("call-2", "spawn_agent", Spawn("Scout", "task two")));
            }

            if (Turn.LastUserText(request).StartsWith("task", StringComparison.Ordinal))
            {
                await Task.Delay(150, ct);
                return Turn.Say("child done");
            }

            return Turn.Say("parent done");
        });
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(maxParallel: 1), agents: [Scout]);
        var root = host.CreateRoot("session-queued-" + Guid.NewGuid().ToString("n"), Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go")).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Equal(2, host.SubAgents.Instances.Count);
        var queued = Assert.Single(events.OfType<AgentSubAgentQueued>());
        var startedIds = events.OfType<AgentSubAgentStarted>().Select(e => e.ChildId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(queued.AgentId, startedIds);
        Assert.Equal(2, events.OfType<AgentSubAgentRunning>().Count());
        Assert.Equal(2, events.OfType<AgentSubAgentFinished>().Count());
        foreach (var instance in host.SubAgents.Instances)
        {
            Assert.Equal(1, events.OfType<AgentSubAgentFinished>().Count(f => f.AgentId == instance.Id));
        }
    }

    [Fact]
    public async Task No_Queued_event_when_a_slot_is_free()
    {
        var provider = new FakeProvider((request, index) =>
            request.Messages.Count == 1 && Turn.LastUserText(request) == "go"
                ? Turn.Call(("call-1", "spawn_agent", Spawn("Scout", "solo")))
                : Turn.LastUserText(request) == "solo"
                    ? Turn.Say("child done")
                    : Turn.Say("parent done"));
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(maxParallel: 4), agents: [Scout]);
        var root = host.CreateRoot("session-free-" + Guid.NewGuid().ToString("n"), Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go")).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Empty(events.OfType<AgentSubAgentQueued>());
        Assert.Single(events.OfType<AgentSubAgentRunning>());
        Assert.Single(events.OfType<AgentSubAgentFinished>());
    }

    [Fact]
    public async Task Finished_precedes_the_parent_spawn_tool_result()
    {
        var provider = new FakeProvider(async (request, index, ct) =>
        {
            if (request.Messages.Count == 1 && Turn.LastUserText(request) == "go")
            {
                return Turn.Call(
                    ("call-1", "spawn_agent", Spawn("Scout", "task one")),
                    ("call-2", "spawn_agent", Spawn("Scout", "task two")));
            }

            if (Turn.LastUserText(request).StartsWith("task", StringComparison.Ordinal))
            {
                await Task.Delay(50, ct);
                return Turn.Say("child done");
            }

            return Turn.Say("parent done");
        });
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(maxParallel: 4), agents: [Scout]);
        var root = host.CreateRoot("session-order-" + Guid.NewGuid().ToString("n"), Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go")).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Empty(events.OfType<AgentFailed>());
        var eventList = events.ToList();
        foreach (var finished in eventList.OfType<AgentSubAgentFinished>())
        {
            var finishedIndex = eventList.IndexOf(finished);
            var toolIndex = IndexOf<AgentToolFinished>(
                eventList,
                t => t.Call.Name == "spawn_agent" && t.Result.Content.Contains(finished.AgentId, StringComparison.Ordinal));
            Assert.True(toolIndex >= 0, $"No parent tool result found for {finished.AgentId}.");
            Assert.True(finishedIndex < toolIndex, $"Finished for {finished.AgentId} must precede the parent spawn tool result.");
        }
    }

    [Fact]
    public async Task Finished_usage_is_this_run_delta_not_the_lifetime_total()
    {
        var provider = new FakeProvider((request, index) =>
        {
            var last = Turn.LastUserText(request);
            if (last == "first task")
            {
                return Turn.Say("alpha", new TokenUsage(100, 50));
            }

            if (last == "second task")
            {
                return Turn.Say("beta", new TokenUsage(7, 3));
            }

            var rounds = request.Messages.Count(m => m.Role == MessageRole.Tool);
            return rounds switch
            {
                0 => Turn.Call(("call-1", "spawn_agent", $$"""{"kind":"Scout","task":"first task"}""")),
                1 => Turn.Call(("call-2", "spawn_agent", $$"""{"kind":"Scout","task":"second task","resume_id":"Scout-1"}""")),
                _ => Turn.Say("parent done"),
            };
        });
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout]);
        var root = host.CreateRoot("session-usage-" + Guid.NewGuid().ToString("n"), Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go")).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Empty(events.OfType<AgentFailed>());
        var finished = events.OfType<AgentSubAgentFinished>().ToArray();
        Assert.Equal(2, finished.Length);
        Assert.All(finished, f => Assert.Equal("Scout-1", f.AgentId));
        Assert.Equal(new TokenUsage(100, 50), finished[0].Usage);
        Assert.Equal(new TokenUsage(7, 3), finished[1].Usage);
        Assert.Equal(new TokenUsage(107, 53), host.SubAgents.Instances.Single().Usage);
    }

    [Fact]
    public async Task Finished_elapsed_includes_queue_time()
    {
        var provider = new FakeProvider(async (request, index, ct) =>
        {
            if (request.Messages.Count == 1 && Turn.LastUserText(request) == "go")
            {
                return Turn.Call(
                    ("call-1", "spawn_agent", Spawn("Scout", "task one")),
                    ("call-2", "spawn_agent", Spawn("Scout", "task two")));
            }

            if (Turn.LastUserText(request).StartsWith("task", StringComparison.Ordinal))
            {
                await Task.Delay(200, ct);
                return Turn.Say("child done");
            }

            return Turn.Say("parent done");
        });
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(maxParallel: 1), agents: [Scout]);
        var root = host.CreateRoot("session-elapsed-" + Guid.NewGuid().ToString("n"), Workspace);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go")).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Empty(events.OfType<AgentFailed>());
        var finished = events.OfType<AgentSubAgentFinished>().OrderBy(f => f.At).ToArray();
        Assert.Equal(2, finished.Length);
        Assert.All(finished, f => Assert.True(f.Elapsed > TimeSpan.Zero));
        // The queued child waits for the first child (~200ms) plus its own run.
        var queued = events.OfType<AgentSubAgentQueued>().ToArray();
        Assert.Single(queued);
        var queuedChild = finished.Single(f => f.AgentId == queued[0].AgentId);
        var otherChild = finished.Single(f => f.AgentId != queued[0].AgentId);
        Assert.True(
            queuedChild.Elapsed >= otherChild.Elapsed,
            $"Queued child elapsed {queuedChild.Elapsed} should cover the first child run {otherChild.Elapsed}.");
        Assert.True(queuedChild.Elapsed >= TimeSpan.FromMilliseconds(150));
    }
}
