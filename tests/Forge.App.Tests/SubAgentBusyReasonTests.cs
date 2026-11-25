using Forge.Abstractions;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Which sub-agent rows make a session busy. A row replayed from the store whose run end the messages cannot prove is
/// finished - its outcome is unknown, not running - but its status field keeps the queued default it is constructed with.
/// Counting that field alone made a reopened session look busy for good: archive and promote stayed greyed out with "a
/// sub-agent is running or queued" while nothing ran, and the same reason blocked its removal and every handover of its root.
/// </summary>
public sealed class SubAgentBusyReasonTests
{
    private static SessionInfo Child() =>
        new("child-session", "/repo/worktree-a", "Worker 2", DateTimeOffset.UnixEpoch)
        {
            ParentSessionId = "root",
            AgentKind = "Worker",
        };

    private static SubAgentViewModel Live() =>
        new(
            "root",
            "Worker-2",
            "child-session",
            "Worker",
            "Do the thing.",
            "test-model",
            stopRequested: null,
            parentAgentId: "root");

    [Fact]
    public void A_replayed_row_the_store_cannot_end_is_not_running()
    {
        var stored = SubAgentViewModel.FromStored("root", Child(), "root", status: null);

        // The trap the fix closes: the row is finished (unknown outcome) while its status is still the queued default.
        Assert.True(stored.IsFinished);
        Assert.Equal(SubAgentStatus.Queued, stored.Status);
        Assert.False(SessionWorkspaceViewModel.CountsAsRunning(stored));
    }

    [Fact]
    public void A_replayed_row_with_a_proven_end_is_not_running()
    {
        var stored = SubAgentViewModel.FromStored("root", Child(), "root", status: SubAgentStatus.Completed);

        Assert.False(SessionWorkspaceViewModel.CountsAsRunning(stored));
    }

    [Fact]
    public void A_row_whose_run_is_under_way_is_running()
    {
        Assert.True(SessionWorkspaceViewModel.CountsAsRunning(Live()));
    }

    [Fact]
    public void A_reused_row_is_running_once_its_next_run_starts()
    {
        var live = Live();
        live.Finish(SubAgentStatus.Completed, TimeSpan.FromSeconds(3), null, DateTimeOffset.UnixEpoch);
        Assert.False(SessionWorkspaceViewModel.CountsAsRunning(live));

        // The parent handed the same agent its next task: the tab continues, and the run's own events put it back to work.
        live.MarkResumed();
        live.MarkRunning(DateTimeOffset.UnixEpoch);
        Assert.True(SessionWorkspaceViewModel.CountsAsRunning(live));
    }
}
