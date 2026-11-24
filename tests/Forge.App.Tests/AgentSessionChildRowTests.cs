using Forge.Abstractions;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The sub-agents under an agent session hang under that session's own row, and one that starts running is mounted there at
/// once. The defect this pins: an agent-session row's children were planned from the store only - the live roster was
/// consulted for root sessions alone - so a child the last full tree read had not seen yet stayed missing until some later
/// refresh, while a root session's child appeared on the spot.
/// </summary>
public sealed class AgentSessionChildRowTests
{
    [Fact]
    public void A_child_of_a_loaded_agent_session_is_mounted_from_its_live_roster()
    {
        var agent = new SubAgentViewModel(
            "agent-session",
            "Worker-2",
            "child-session",
            "Worker",
            "Map the call sites.",
            "test-model",
            stopRequested: null,
            parentAgentId: "root");
        var snapshot = new List<AgentNodeSnapshot>
        {
            new("root", null, "主 Agent", string.Empty, true, true),
            SessionWorkspaceViewModel.SnapshotOf(agent),
        };

        // A stale store row for the same session (the last full tree read) must not decide anything now: the roster does.
        var stored = new[] { Child("child-session", "Scout") }.ToLookup(static child => child.ParentSessionId!);

        var row = Assert.Single(ProjectTreeViewModel.PlanAgentSessionChildren(stored, "agent-session", running: [], snapshot));

        // The main agent of that session is the agent-session row itself, so only the child is lifted out.
        Assert.Equal(TreeNodeKind.SubAgent, row.Kind);
        Assert.Equal("agent:Worker-2", row.Key);
        Assert.Equal("Worker-2", row.Title);
        // The agent session is the depth-1 agent, so its children sit at the last depth the runtime allows.
        Assert.Equal(2, row.AgentLevel);
        // A row opens the tab of the session that owns the agent: here the agent session, not the root above it.
        Assert.Equal("agent-session", row.AgentSessionId);
    }

    [Fact]
    public void A_child_of_an_unloaded_agent_session_still_comes_from_the_store()
    {
        var stored = new[] { Child("child-session", "Worker") }
            .ToLookup(static child => child.ParentSessionId!);

        var row = Assert.Single(ProjectTreeViewModel.PlanAgentSessionChildren(
            stored, "agent-session", running: ["child-session"], snapshot: []));

        Assert.Equal(TreeNodeKind.SubAgent, row.Kind);
        Assert.Equal("agent:child-session", row.Key);
        Assert.Equal("Worker", row.Title);
        Assert.Equal(2, row.AgentLevel);
        Assert.True(row.IsRunning);
    }

    private static SessionInfo Child(string id, string agentKind) =>
        new(id, "/workspace", "Worker: Map the call sites.", DateTimeOffset.UnixEpoch)
        {
            ParentSessionId = "agent-session",
            AgentKind = agentKind,
            Task = "Map the call sites.",
        };
}
