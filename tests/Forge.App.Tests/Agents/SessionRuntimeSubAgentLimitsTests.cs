using System.Collections.Concurrent;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// The <c>agents</c> section's sub-agent dials reach a live session: the cap from <see cref="AgentRuntimeServices.SubAgents"/>
/// is in force on the host the runtime keeps, a changed answer moves it on the next turn without rebuilding that host, and
/// switching spawning off takes <c>spawn_agent</c> out of the published tool set while the steer, follow-up and stop tools
/// stay. The cap is observed through the tree it bounds: children are held at the model until the test releases them, so
/// "admitted" and "queued" are read from signals instead of from elapsed time.
/// </summary>
public sealed class SessionRuntimeSubAgentLimitsTests : IDisposable
{
    private const string Kind = "Scout";
    private const int DefaultCap = 4;

    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-sub-agent-limits-" + Guid.NewGuid().ToString("N"));
    private readonly ChildGates gates = new();
    private readonly ConcurrentQueue<AgentEvent> childEvents = new();
    private readonly SemaphoreSlim queued = new(0);
    private SubAgentLimits limits = SubAgentLimits.Default;

    public SessionRuntimeSubAgentLimitsTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is harmless.
        }
    }

    /// <summary>
    /// Assembles the runtime the way the composition root does: the sub-agent dials come from settings and the tools store is
    /// wired (an empty switch list), because the session re-applies the stored switches whenever the sub-agent switch moves.
    /// </summary>
    private SessionRuntimeRig NewRig(bool withSubAgents = true) => new(
        root,
        services =>
        {
            if (withSubAgents)
            {
                services.SubAgents = () => limits;
            }

            services.DisabledTools = () => [];
        });

    /// <summary>Holds every child request at the model until <see cref="ChildGates.Release"/> is called for its task.</summary>
    private void HoldChildren(SessionRuntimeRig rig) =>
        rig.Provider.BeforeReply = (request, _) =>
        {
            var task = LastUserText(request);
            return task.StartsWith("child-", StringComparison.Ordinal) ? gates.HoldAsync(task) : Task.CompletedTask;
        };

    private List<Task<ChildAgentResult>> Spawn(SessionRuntimeRig rig, params string[] tasks) =>
        [.. tasks.Select(task => rig.Runtime.SpawnChildAsync(Kind, task, OnChildEvent, CancellationToken.None))];

    private void OnChildEvent(AgentEvent value)
    {
        childEvents.Enqueue(value);
        if (value is AgentSubAgentQueued)
        {
            queued.Release();
        }
    }

    /// <summary>Waits until a child reports that it is waiting for a slot.</summary>
    private async Task QueuedAsync() =>
        Assert.True(await queued.WaitAsync(TimeSpan.FromSeconds(60)), "no child was queued behind the cap");

    private static string[] ToolNames(ChatRequest request) => [.. request.Tools.Select(static tool => tool.Name)];

    private static string LastUserText(ChatRequest request)
    {
        for (var index = request.Messages.Count - 1; index >= 0; index--)
        {
            if (request.Messages[index].Role != MessageRole.User)
            {
                continue;
            }

            foreach (var block in request.Messages[index].Content)
            {
                if (block is TextBlock text)
                {
                    return text.Text;
                }
            }
        }

        return string.Empty;
    }

    [Fact]
    public async Task The_configured_cap_bounds_the_tree_from_the_first_turn()
    {
        limits = new SubAgentLimits(2, Enabled: true);
        await using var rig = NewRig();
        HoldChildren(rig);
        try
        {
            await rig.TurnAsync();
            Assert.Contains(SpawnAgentTool.Name, ToolNames(rig.Provider.Requests[^1]));

            var spawned = Spawn(rig, "child-1", "child-2", "child-3");

            // Two children reach the model while the third is held back, so the cap is exactly the configured two.
            var running = new List<string> { await gates.NextArrivalAsync(), await gates.NextArrivalAsync() };
            await QueuedAsync();
            foreach (var task in running)
            {
                gates.Release(task);
            }

            gates.Release(await gates.NextArrivalAsync());

            var results = await Task.WhenAll(spawned).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.All(results, static result => Assert.False(result.IsError, result.Text));
            Assert.Single(childEvents.OfType<AgentSubAgentQueued>());
            Assert.Empty(childEvents.OfType<AgentFailed>());
            Assert.Equal(3, childEvents.OfType<AgentSubAgentFinished>().Count());
        }
        finally
        {
            gates.ReleaseAll("child-1", "child-2", "child-3");
        }
    }

    [Fact]
    public async Task A_changed_cap_moves_on_the_next_turn_without_rebuilding_the_host()
    {
        limits = new SubAgentLimits(1, Enabled: true);
        await using var rig = NewRig();
        HoldChildren(rig);
        try
        {
            await rig.TurnAsync();
            var published = rig.Runtime.PublishedTools;
            Assert.NotNull(published);

            var spawned = Spawn(rig, "child-1", "child-2");
            var first = await gates.NextArrivalAsync();
            await QueuedAsync();

            limits = new SubAgentLimits(3, Enabled: true);

            // The raise is applied before the root's own request, so the queued child starts at once and the turn still runs.
            await rig.TurnAsync();
            var woken = await gates.NextArrivalAsync();
            Assert.NotEqual(first, woken);

            // The same host served the turn: only the cap moved, so the published tool set (and the prompt cache) is intact.
            Assert.Same(published, rig.Runtime.PublishedTools);

            gates.Release(first);
            gates.Release(woken);
            var results = await Task.WhenAll(spawned).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.All(results, static result => Assert.False(result.IsError, result.Text));
            Assert.Single(childEvents.OfType<AgentSubAgentQueued>());
        }
        finally
        {
            gates.ReleaseAll("child-1", "child-2");
        }
    }

    [Fact]
    public async Task Switching_spawning_off_takes_spawn_agent_out_and_keeps_the_control_tools()
    {
        limits = new SubAgentLimits(2, Enabled: false);
        await using var rig = NewRig();

        await rig.TurnAsync();

        var offered = ToolNames(rig.Provider.Requests[^1]);
        Assert.DoesNotContain(SpawnAgentTool.Name, offered);
        Assert.Contains(SubAgentSteerTool.Name, offered);
        Assert.Contains(SubAgentFollowUpTool.Name, offered);
        Assert.Contains(SubAgentStopTool.Name, offered);

        // The registry the session publishes matches what the model was offered, so a child started before the switch can
        // still be steered and stopped while nothing new can be spawned.
        var published = rig.Runtime.PublishedTools;
        Assert.NotNull(published);
        Assert.DoesNotContain(SpawnAgentTool.Name, published.Definitions.Select(static definition => definition.Name));
    }

    [Fact]
    public async Task Switching_spawning_back_on_restores_spawn_agent_on_the_next_turn()
    {
        limits = new SubAgentLimits(2, Enabled: false);
        await using var rig = NewRig();
        await rig.TurnAsync();
        Assert.DoesNotContain(SpawnAgentTool.Name, ToolNames(rig.Provider.Requests[^1]));

        limits = new SubAgentLimits(2, Enabled: true);

        await rig.TurnAsync();
        Assert.Contains(SpawnAgentTool.Name, ToolNames(rig.Provider.Requests[^1]));
        Assert.Contains(SpawnAgentTool.Name, rig.Runtime.PublishedTools!.Definitions.Select(static definition => definition.Name));
    }

    [Fact]
    public async Task Switching_spawning_back_on_restores_spawn_agent_without_a_tools_store()
    {
        // A runtime assembled without the tools store (a unit test shape) has no stored switches to re-apply, so the
        // sub-agent switch alone has to set the disabled set in both directions; restoring it was the defect this pins.
        limits = new SubAgentLimits(2, Enabled: false);
        await using var rig = new SessionRuntimeRig(root, services => services.SubAgents = () => limits);
        await rig.TurnAsync();
        Assert.DoesNotContain(SpawnAgentTool.Name, ToolNames(rig.Provider.Requests[^1]));

        limits = new SubAgentLimits(2, Enabled: true);

        await rig.TurnAsync();
        Assert.Contains(SpawnAgentTool.Name, ToolNames(rig.Provider.Requests[^1]));
        Assert.Contains(SpawnAgentTool.Name, rig.Runtime.PublishedTools!.Definitions.Select(static definition => definition.Name));
    }

    [Fact]
    public async Task Without_a_factory_the_default_cap_of_four_and_spawn_agent_are_kept()
    {
        await using var rig = NewRig(withSubAgents: false);
        HoldChildren(rig);
        try
        {
            await rig.TurnAsync();
            Assert.Contains(SpawnAgentTool.Name, ToolNames(rig.Provider.Requests[^1]));

            var spawned = Spawn(rig, "child-1", "child-2", "child-3", "child-4", "child-5");

            // Four children reach the model before anything is released and the fifth waits: without the factory the cap is
            // the historical four, and spawning is on.
            var running = new List<string>();
            for (var index = 0; index < DefaultCap; index++)
            {
                running.Add(await gates.NextArrivalAsync());
            }

            await QueuedAsync();
            foreach (var task in running)
            {
                gates.Release(task);
            }

            gates.Release(await gates.NextArrivalAsync());

            var results = await Task.WhenAll(spawned).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.All(results, static result => Assert.False(result.IsError, result.Text));
            Assert.Single(childEvents.OfType<AgentSubAgentQueued>());
            Assert.Equal(5, childEvents.OfType<AgentSubAgentFinished>().Count());
        }
        finally
        {
            gates.ReleaseAll("child-1", "child-2", "child-3", "child-4", "child-5");
        }
    }

    [Fact]
    public async Task An_unchanged_switch_publishes_no_new_tool_set()
    {
        limits = new SubAgentLimits(2, Enabled: true);
        await using var rig = NewRig();
        await rig.TurnAsync();
        var published = rig.Runtime.PublishedTools;
        Assert.NotNull(published);

        await rig.TurnAsync();

        // The registry instance only changes when a tool set was published, so the second turn reused the first one and the
        // session's prompt cache still hits; the requests show the same tool definitions.
        Assert.Same(published, rig.Runtime.PublishedTools);
        var requests = rig.Provider.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Equal(ToolNames(requests[0]), ToolNames(requests[1]));
    }

    [Fact]
    public async Task A_steady_off_switch_publishes_no_new_tool_set_on_a_later_turn()
    {
        limits = new SubAgentLimits(2, Enabled: false);
        await using var rig = NewRig();
        await rig.TurnAsync();
        var published = rig.Runtime.PublishedTools;
        Assert.NotNull(published);

        await rig.TurnAsync();

        // Re-applying the same switch must not republish the tool set either, or every turn of a session with spawning off
        // would break its prompt cache.
        Assert.Same(published, rig.Runtime.PublishedTools);
    }

    /// <summary>
    /// Holds child requests at the model. Arrivals tell a test which children were admitted (a queued child never arrives),
    /// so the releases are exact and no step has to wait a fixed time to learn that something did not happen.
    /// </summary>
    private sealed class ChildGates
    {
        private readonly Lock gate = new();
        private readonly Queue<string> pending = new();
        private readonly SemaphoreSlim signals = new(0);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> released = new(StringComparer.Ordinal);

        /// <summary>Provider-side gate: records the arrival, then waits for the test to release this task.</summary>
        public async Task HoldAsync(string task)
        {
            lock (gate)
            {
                pending.Enqueue(task);
            }

            signals.Release();
            await Signal(task).Task.ConfigureAwait(false);
        }

        /// <summary>The task text of the next child request that reached the model.</summary>
        public async Task<string> NextArrivalAsync()
        {
            Assert.True(await signals.WaitAsync(TimeSpan.FromSeconds(60)), "no child request reached the model");
            lock (gate)
            {
                return pending.Dequeue();
            }
        }

        public void Release(string task) => Signal(task).TrySetResult();

        /// <summary>Releases every named task, so a request that arrives later is answered instead of hanging the rig.</summary>
        public void ReleaseAll(params string[] tasks)
        {
            foreach (var task in tasks)
            {
                Release(task);
            }
        }

        private TaskCompletionSource Signal(string task) =>
            released.GetOrAdd(task, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
