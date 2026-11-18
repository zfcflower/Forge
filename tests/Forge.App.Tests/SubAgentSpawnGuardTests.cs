using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Xunit;

namespace Forge.App.Tests;

public sealed class SubAgentSpawnGuardTests
{
    private sealed class NoComposer : IPromptComposer
    {
        public ComposedPrompt Compose(PromptInput input) => throw new NotSupportedException();
    }

    private sealed class Sink : IAgentEventSink
    {
        public List<AgentEvent> Events { get; } = [];

        public void Emit(AgentEvent value) => Events.Add(value);
    }

    private static SubAgentScheduler Scheduler(params AgentDefinition[] kinds) =>
        new(new AgentCatalog(kinds), new PromptLayers(string.Empty, null, null), new NoComposer(), null, new AgentOptions());

    private static AgentDefinition Kind(string name, string[]? tools) => new(name, "d", "p", new ModelSettings("m"), tools);

    private static Task<SubAgentOutcome> Spawn(SubAgentScheduler scheduler, string kind, int depth) =>
        scheduler.SpawnAsync(kind, "task", null, new ToolContext("s", "wd", depth), new Sink(), "main", CancellationToken.None);

    [Fact]
    public async Task Depth_two_cannot_spawn_but_depth_one_gets_past_the_depth_gate()
    {
        var scheduler = Scheduler(Kind("open", null));

        var refused = await Spawn(scheduler, "open", 2);
        Assert.True(refused.IsError);
        Assert.Contains("depth 2", refused.Text);

        // Depth 1 passes the depth gate and stops at the next check (this kind has no tools list).
        var next = await Spawn(scheduler, "open", 1);
        Assert.True(next.IsError);
        Assert.DoesNotContain("depth", next.Text);
        Assert.Contains("no usable `tools` list", next.Text);
    }

    [Fact]
    public async Task A_kind_without_a_usable_tools_list_is_refused_with_the_way_to_fix_it()
    {
        foreach (var tools in new string[]?[] { null, [] })
        {
            var outcome = await Spawn(Scheduler(Kind("loose", tools)), "loose", 0);

            Assert.True(outcome.IsError);
            Assert.Contains("no usable `tools` list", outcome.Text);
            Assert.Contains(".forge/agents", outcome.Text);
        }
    }
}
