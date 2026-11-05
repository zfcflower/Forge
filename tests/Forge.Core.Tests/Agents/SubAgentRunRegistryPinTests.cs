using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// Pins the <see cref="SubAgentScheduler"/> run registry: finished runs stay addressable as
/// <see cref="SubAgentControlResult.AlreadyFinished"/>, unknown ids are
/// <see cref="SubAgentControlResult.NotFound"/>, and resuming a finished id makes it
/// controllable again. The registry is observed through behaviour only: the live-run map
/// has no public or test-visible size API, so <c>Instances</c> (all spawns ever) is the
/// only count asserted and liveness is proven by control results.
/// </summary>
public sealed class SubAgentRunRegistryPinTests
{
    private static readonly AgentDefinition Scout =
        new("Scout", "scout kind", "scout role", new ModelSettings("scout-model"), ["read"]);

    private sealed class CollectingSink : IAgentEventSink
    {
        private readonly Lock gate = new();
        private readonly List<AgentEvent> events = new();

        public void Emit(AgentEvent value)
        {
            lock (gate)
            {
                events.Add(value);
            }
        }

        public bool HasRunning(string agentId)
        {
            lock (gate)
            {
                return events.OfType<AgentSubAgentRunning>().Any(e => e.AgentId == agentId);
            }
        }
    }

    private static AgentHost CreateHost(IChatProvider provider) =>
        AgentTestHost.Create(
            provider,
            [FakeTool.Echo("read")],
            options: new AgentOptions { MaxParallelSubAgents = 4, MaxSubAgentDepth = 3, MaxTurns = 10 },
            agents: [Scout]);

    private static ToolContext Context(string sessionId) => new(sessionId, Path.GetTempPath());

    [Fact]
    public async Task Hundred_sequential_spawns_leave_every_finished_id_AlreadyFinished_and_unknown_NotFound()
    {
        var host = CreateHost(new FakeProvider((request, index) => Turn.Say("done")));
        var sink = new CollectingSink();
        var sessionId = "sess-registry-" + Guid.NewGuid().ToString("n");

        var outcomes = new List<SubAgentOutcome>(100);
        for (var i = 0; i < 100; i++)
        {
            var outcome = await host.SubAgents
                .SpawnAsync("Scout", "task " + i, null, Context(sessionId), sink, "root", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(outcome.IsError, outcome.Text);
            outcomes.Add(outcome);
        }

        // All 100 spawns registered as instances (public count), ids unique.
        Assert.Equal(100, host.SubAgents.Instances.Count);
        Assert.Equal(100, outcomes.Select(o => o.AgentId).Distinct(StringComparer.Ordinal).Count());

        // Every finished id is still known, but no longer controllable.
        foreach (var outcome in outcomes)
        {
            Assert.Equal(SubAgentControlResult.AlreadyFinished, host.SubAgentControl.Steer(outcome.AgentId, "late"));
            Assert.Equal(SubAgentControlResult.AlreadyFinished, host.SubAgentControl.FollowUp(outcome.AgentId, "late"));
            Assert.Equal(SubAgentControlResult.AlreadyFinished, host.SubAgentControl.Stop(outcome.AgentId, "late"));
        }

        // Unknown ids (and blank ids) are NotFound, never AlreadyFinished.
        Assert.Equal(SubAgentControlResult.NotFound, host.SubAgentControl.Steer("Scout-424242", "x"));
        Assert.Equal(SubAgentControlResult.NotFound, host.SubAgentControl.FollowUp("Scout-424242", "x"));
        Assert.Equal(SubAgentControlResult.NotFound, host.SubAgentControl.Stop("Scout-424242", "x"));
        Assert.Equal(SubAgentControlResult.NotFound, host.SubAgentControl.Steer(string.Empty, "x"));
    }

    [Fact]
    public async Task Resume_of_a_finished_id_is_controllable_while_running_then_AlreadyFinished()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedOnce = 0;
        var provider = new FakeProvider(async (request, index, ct) =>
        {
            if (Turn.LastUserText(request) == "resumable"
                && Interlocked.CompareExchange(ref blockedOnce, 1, 0) == 0)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct).ConfigureAwait(false);
                return Turn.Say("resumed done");
            }

            return Turn.Say("done");
        });
        var host = CreateHost(provider);
        var sink = new CollectingSink();
        var sessionId = "sess-resume-" + Guid.NewGuid().ToString("n");

        var first = await host.SubAgents
            .SpawnAsync("Scout", "first run", null, Context(sessionId), sink, "root", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(first.IsError, first.Text);
        var id = first.AgentId;
        Assert.Equal(SubAgentControlResult.AlreadyFinished, host.SubAgentControl.Steer(id, "late"));

        // Resume the finished id: while the resumed run is in flight the id is controllable again.
        var resumeTask = host.SubAgents.SpawnAsync(
            "Scout", "resumable", id, Context(sessionId), sink, "root", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!sink.HasRunning(id) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(sink.HasRunning(id), "Resumed run never reached Running.");
        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.Steer(id, "redirect"));
        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.FollowUp(id, "extra"));
        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.Stop(id, "enough"));

        release.TrySetResult();
        var resumed = await resumeTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(resumed.IsError, resumed.Text);
        Assert.Equal(id, resumed.AgentId);

        // After the resumed run terminates the id is finished again, not dropped from the registry.
        Assert.Equal(SubAgentControlResult.AlreadyFinished, host.SubAgentControl.Steer(id, "late"));
        Assert.Equal(SubAgentControlResult.AlreadyFinished, host.SubAgentControl.FollowUp(id, "late"));
        Assert.Equal(SubAgentControlResult.AlreadyFinished, host.SubAgentControl.Stop(id, "late"));
        Assert.Contains(host.SubAgents.Instances, i => i.Id == id);
    }
}
