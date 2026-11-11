using Forge.App.Composition;
using Xunit;

namespace Forge.App.Tests.Composition;

/// <summary>
/// The workspace states of TASK-session-worktree-lifecycle section 2: a refresh's background <c>MERGE_HEAD</c> read is
/// written only when nothing happened in the workspace since the read began, so a stale "no merge" cannot wipe the
/// conflict a merge just flagged, and a stale "in a merge" cannot bring back a conflict an abort just cleared.
/// </summary>
public sealed class WorkspaceStatesTests
{
    private const string Workspace = "D:/repo/work";

    [Fact]
    public void A_read_with_nothing_in_between_is_written()
    {
        var states = new WorkspaceStates();
        var epoch = states.Epoch(Workspace);

        Assert.True(states.TrySetConflictedFromRead(Workspace, true, epoch));
        Assert.True(states.IsConflicted(Workspace));
        Assert.True(states.TrySetConflictedFromRead(Workspace, false, states.Epoch(Workspace)));
        Assert.False(states.IsConflicted(Workspace));
    }

    [Fact]
    public void A_read_that_began_before_a_merge_hit_a_conflict_does_not_clear_it()
    {
        var states = new WorkspaceStates();
        var epoch = states.Epoch(Workspace);
        Assert.True(states.TryBeginOperation(Workspace));
        states.SetConflicted(Workspace, true);
        states.EndOperation(Workspace);

        Assert.False(states.TrySetConflictedFromRead(Workspace, false, epoch));
        Assert.True(states.IsConflicted(Workspace));
    }

    [Fact]
    public void A_read_that_began_before_an_abort_does_not_bring_the_conflict_back()
    {
        var states = new WorkspaceStates();
        states.SetConflicted(Workspace, true);
        var epoch = states.Epoch(Workspace);
        Assert.True(states.TryBeginOperation(Workspace));
        states.SetConflicted(Workspace, false);
        states.EndOperation(Workspace);

        Assert.False(states.TrySetConflictedFromRead(Workspace, true, epoch));
        Assert.False(states.IsConflicted(Workspace));
    }

    [Fact]
    public void A_read_while_an_operation_runs_is_dropped()
    {
        var states = new WorkspaceStates();
        Assert.True(states.TryBeginOperation(Workspace));
        var epoch = states.Epoch(Workspace);

        Assert.False(states.TrySetConflictedFromRead(Workspace, true, epoch));
        Assert.False(states.IsConflicted(Workspace));
    }

    [Fact]
    public void A_path_variant_is_the_same_workspace()
    {
        var states = new WorkspaceStates();
        var epoch = states.Epoch(Workspace + "/");
        Assert.True(states.TryBeginOperation(Workspace));
        states.EndOperation(Workspace);

        Assert.False(states.TrySetConflictedFromRead(Workspace + "/", true, epoch));
        Assert.True(states.TryBeginOperation(Workspace));
        Assert.False(states.TryBeginOperation(Workspace + "/"));
    }
}
