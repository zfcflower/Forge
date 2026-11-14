using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;
using static Forge.Core.Tests.Agents.SubAgentTestSupport;

namespace Forge.Core.Tests.Agents;

/// <summary>Pins the shared sub-agent control surface: unknown/finished results, queued-task
/// merging, tool visibility by depth, and how control outcomes surface in tool results.</summary>
public sealed class SubAgentControlSurfacePinTests
{
    [Fact]
    public void Unknown_ids_report_NotFound()
    {
        var host = AgentTestHost.Create(
            new FakeProvider((request, index) => Turn.Say("done")),
            [FakeTool.Echo("read")],
            options: Options(),
            agents: [Scout]);

        Assert.Equal(SubAgentControlResult.NotFound, host.SubAgentControl.Steer("Scout-404", "x"));
        Assert.Equal(SubAgentControlResult.NotFound, host.SubAgentControl.FollowUp("Scout-404", "x"));
        Assert.Equal(SubAgentControlResult.NotFound, host.SubAgentControl.Stop("Scout-404", "x"));
    }

    [Fact]
    public async Task Finished_children_report_AlreadyFinished()
    {
        var provider = ParentSpawnsScout("inspect");
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout]);
        var (_, events) = await AgentTestHost.RunRootAsync(host, "session-cs-" + Guid.NewGuid().ToString("n"), timeout: TimeSpan.FromSeconds(15));
        Assert.Empty(events.OfType<AgentFailed>());
        var childId = Assert.Single(host.SubAgents.Instances).Id;

        Assert.Equal(SubAgentControlResult.AlreadyFinished, host.SubAgentControl.Steer(childId, "late"));
        Assert.Equal(SubAgentControlResult.AlreadyFinished, host.SubAgentControl.FollowUp(childId, "late"));
        Assert.Equal(SubAgentControlResult.AlreadyFinished, host.SubAgentControl.Stop(childId, "late"));
    }

    [Fact]
    public async Task Queued_steer_and_followUp_are_appended_to_the_task()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(async (request, index, ct) =>
        {
            if (Turn.LastUserText(request) == "first")
            {
                firstEntered.TrySetResult();
                await firstRelease.Task.WaitAsync(ct);
                return Turn.Say("first done");
            }

            if (Turn.LastUserText(request).Contains("second", StringComparison.Ordinal))
            {
                var full = string.Join("\n", request.Messages.Where(m => m.Role == MessageRole.User).Select(Turn.Text));
                Assert.Contains("steered-while-queued", full, StringComparison.Ordinal);
                Assert.Contains("followed-while-queued", full, StringComparison.Ordinal);
                return Turn.Say("second done");
            }

            return Turn.Say("unexpected");
        });
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(maxParallel: 1), agents: [Scout]);
        var context = new ToolContext("sess-qappend-" + Guid.NewGuid().ToString("n"), Workspace);
        var sink = new CollectingSink();

        var firstTask = host.SubAgents.SpawnAsync("Scout", "first", null, context, sink, "root", CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondTask = host.SubAgents.SpawnAsync("Scout", "second", null, context, sink, "root", CancellationToken.None);
        string? secondId = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (secondId is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
            if (host.SubAgents.Instances.Count == 2)
            {
                var running = sink.Running.SingleOrDefault();
                secondId = host.SubAgents.Instances.Select(i => i.Id).FirstOrDefault(id => id != running);
            }
        }

        Assert.NotNull(secondId);
        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.Steer(secondId, "steered-while-queued"));
        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.FollowUp(secondId, "followed-while-queued"));

        firstRelease.TrySetResult();
        Assert.Equal("first done", (await firstTask.WaitAsync(TimeSpan.FromSeconds(15))).Text);
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(second.IsError);
        Assert.Contains("second done", second.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parent_sees_control_tools_beside_spawn_and_deepest_agent_sees_none()
    {
        IReadOnlyList<string>? rootTools = null;
        IReadOnlyList<string>? deepTools = null;
        var provider = new FakeProvider((request, index) =>
        {
            var names = request.Tools.Select(t => t.Name).ToArray();
            if (request.SystemPrompt.Contains("scout role", StringComparison.Ordinal))
            {
                if (!names.Contains(SpawnAgentTool.Name))
                {
                    deepTools = names;
                }

                return ToolRounds(request) == 0
                    ? Turn.Call(("call", "spawn_agent", Spawn("Scout", "down")))
                    : Turn.Say("leaf");
            }

            rootTools ??= names;
            return ToolRounds(request) == 0
                ? Turn.Call(("call", "spawn_agent", Spawn("Scout", "down")))
                : Turn.Say("leaf");
        });
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout]);
        var (_, events) = await AgentTestHost.RunRootAsync(host, "session-depth-" + Guid.NewGuid().ToString("n"), timeout: TimeSpan.FromSeconds(20));
        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Equal(3, host.SubAgents.Instances.Count);

        Assert.NotNull(rootTools);
        Assert.Contains(SpawnAgentTool.Name, rootTools);
        Assert.Contains(SubAgentSteerTool.Name, rootTools);
        Assert.Contains(SubAgentFollowUpTool.Name, rootTools);
        Assert.Contains(SubAgentStopTool.Name, rootTools);

        Assert.NotNull(deepTools);
        Assert.DoesNotContain(SpawnAgentTool.Name, deepTools);
        Assert.DoesNotContain(SubAgentSteerTool.Name, deepTools);
        Assert.DoesNotContain(SubAgentFollowUpTool.Name, deepTools);
        Assert.DoesNotContain(SubAgentStopTool.Name, deepTools);
    }

    [Fact]
    public async Task Control_tool_results_report_NotFound_for_unknown_ids()
    {
        var provider = new FakeProvider((request, index) => ToolRounds(request) switch
        {
            0 => Turn.Call(("c1", "subagent_steer", """{"id":"Scout-404","instruction":"go"}""")),
            1 => Turn.Call(("c2", "subagent_followup", """{"id":"Scout-404","instruction":"go"}""")),
            2 => Turn.Call(("c3", "subagent_stop", """{"id":"Scout-404"}""")),
            _ => Turn.Say("parent done"),
        });
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout]);
        var (root, events) = await AgentTestHost.RunRootAsync(host, "session-notfound-" + Guid.NewGuid().ToString("n"), timeout: TimeSpan.FromSeconds(15));
        Assert.Empty(events.OfType<AgentFailed>());
        var results = root.History.SelectMany(m => m.Content.OfType<ToolResultBlock>()).ToArray();
        Assert.Equal(3, results.Length);
        Assert.All(results, r => Assert.True(r.IsError));
        Assert.All(results, r => Assert.Contains("not found", r.Content, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Control_tool_results_report_AlreadyFinished_after_the_child_ends()
    {
        var provider = new FakeProvider((request, index) =>
        {
            var last = Turn.LastUserText(request);
            if (last == "short task")
            {
                return Turn.Say("child report");
            }

            return ToolRounds(request) switch
            {
                0 => Turn.Call(("call-1", "spawn_agent", Spawn("Scout", "short task"))),
                1 => Turn.Call(("c2", "subagent_steer", """{"id":"Scout-1","instruction":"too late"}""")),
                _ => Turn.Say("parent done"),
            };
        });
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("read")], options: Options(), agents: [Scout]);
        var (root, events) = await AgentTestHost.RunRootAsync(host, "session-already-" + Guid.NewGuid().ToString("n"), timeout: TimeSpan.FromSeconds(15));
        Assert.Empty(events.OfType<AgentFailed>());
        var results = root.History.SelectMany(m => m.Content.OfType<ToolResultBlock>()).ToArray();
        Assert.Equal(2, results.Length);
        Assert.False(results[0].IsError);
        Assert.True(results[1].IsError);
        Assert.Contains("already finished", results[1].Content, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CollectingSink : IAgentEventSink
    {
        private readonly List<AgentEvent> events = new();
        public void Emit(AgentEvent value)
        {
            lock (events)
            {
                events.Add(value);
            }
        }

        public IReadOnlyList<string> Running
        {
            get
            {
                lock (events)
                {
                    return events.OfType<AgentSubAgentRunning>().Select(e => e.AgentId).ToArray();
                }
            }
        }
    }
}
