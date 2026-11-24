using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The agent rows under a session: each one is named, never handed the task it was spawned for. The live roster names a row
/// with the id the model itself addresses (<c>Reviewer-3</c>); a restarted window has no roster, so the stored kind is the
/// only name left - the same word a restored sub-agent tab shows. A row whose title is a whole prompt turns into a tall block
/// of text instead of a line.
/// </summary>
public sealed class AgentRowLabelTests
{
    private const string LongTask =
        "Read-only review of a just-implemented fix.\n\n## Background\nThe colour resolver fell back to the wrong token.";

    [Fact]
    public void A_live_agent_row_is_named_by_the_id_the_model_addresses()
    {
        var agent = new SubAgentViewModel(
            "root-session", "Reviewer-3", "child-session", "Reviewer", LongTask, "test-model", stopRequested: null);

        var row = Assert.Single(ProjectTreeViewModel.PlanAgents([SessionWorkspaceViewModel.SnapshotOf(agent)], "root-session"));

        Assert.Equal("Reviewer-3", row.Title);
        Assert.DoesNotContain("Read-only review", row.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stored_agent_row_is_named_by_its_kind_and_never_by_its_task()
    {
        var children = new[]
        {
            Child("reviewer-session", agentKind: "Reviewer"),
            Child("no-kind-session", agentKind: null),
        }.ToLookup(child => child.ParentSessionId!);

        var rows = ProjectTreeViewModel.PlanStoredAgents(children, "root-session", "root-session", running: [], level: 1);

        Assert.Equal(["Reviewer", SessionStrings.SubAgentsTitle], rows.Select(row => row.Title));
        Assert.All(rows, row => Assert.DoesNotContain("Read-only review", row.Title, StringComparison.Ordinal));
    }

    /// <summary>A stored child row as the store keeps it: a long task, and a title built from the front of that task.</summary>
    private static SessionInfo Child(string id, string? agentKind) =>
        new(id, "/workspace", "Reviewer: " + LongTask, DateTimeOffset.UnixEpoch)
        {
            ParentSessionId = "root-session",
            AgentKind = agentKind,
            Task = LongTask,
        };
}
