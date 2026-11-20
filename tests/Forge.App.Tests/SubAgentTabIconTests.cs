using Forge.App.ViewModels;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The bottom group's tab of a sub-agent wears the glyph of its level, the same one the project tree paints for that row:
/// the conversation-and-return bubble for an agent the session's own agent spawned, the lock for one that hangs under
/// another agent and cannot spawn again. A tab reads the level off the agent's parent link, where the main agent stands as
/// <c>"root"</c> — the link is never null for a live child, so a null check labelled every level the deepest one.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SubAgentTabIconTests
{
    [Fact]
    public Task A_tab_wears_the_glyph_of_its_agent_level() => SharedUi.RunAsync(() =>
    {
        // Spawned by the session's own agent: the first level, free to delegate.
        var firstLevel = TabHeaderViewFor(parentAgentId: "root");
        // Spawned by that agent: the last level, which cannot spawn another.
        var lastLevel = TabHeaderViewFor(parentAgentId: "agent-1");
        // A child replayed from the store without a parent row: still the session's own child.
        var replayed = TabHeaderViewFor(parentAgentId: null);

        Assert.Same(TreeIcons.ChatArrowBackDown, firstLevel.Icon);
        Assert.Same(TreeIcons.ChatArrowBackDown, replayed.Icon);
        Assert.Same(TreeIcons.ChatLock, lastLevel.Icon);
        return Task.CompletedTask;
    });

    private static TabHeaderView TabHeaderViewFor(string? parentAgentId)
    {
        var agent = new SubAgentViewModel(
            sessionId: "session-1",
            agentId: "agent-" + (parentAgentId ?? "replayed"),
            childSessionId: "child-session-1",
            kind: "Worker",
            task: "Say hi.",
            model: "test-model",
            stopRequested: null,
            parentAgentId: parentAgentId);
        return new TabHeaderView
        {
            Model = new TabModel("tab-" + agent.AgentId, TabKind.SubAgent, agent, "Worker · Say hi."),
        };
    }
}
