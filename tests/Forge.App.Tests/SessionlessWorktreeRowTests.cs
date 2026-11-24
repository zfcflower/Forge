using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A registered worktree with no Forge session gets a row of its own (7.3), and that row carries the same checkout reads
/// as a workspace with a session: HEAD, a running merge / rebase and the sparse flag. Without them the row could never
/// show 〔稀疏〕 or disable its entries, while its own "+" is the only way to start a session there.
/// </summary>
public sealed class SessionlessWorktreeRowTests
{
    [Fact]
    public void A_worktree_without_a_session_carries_the_read_checkout_state()
    {
        var project = new ProjectInfo("p1", "repo", "/repo", ProjectKind.Repository, 0, DateTimeOffset.UnixEpoch);
        var worktree = new GitWorktree("/repo/wt", "sha", "forge/session-1", false, false, null, false, null, false);
        var reads = new MenuGitReads(
            new Dictionary<string, WorkspaceGitState>(StringComparer.OrdinalIgnoreCase)
            {
                ["/repo/wt"] = new WorkspaceGitState(GitHeadKind.Branch, MergeOrRebase: true, IsSparse: true),
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, IReadOnlyList<GitWorktree>?>(StringComparer.OrdinalIgnoreCase)
            {
                ["/repo"] = [worktree],
            });

        var row = Assert.Single(ProjectTreeViewModel.PlanWorktreesWithoutSessions(project, reads, [], defaultBranch: null));

        Assert.Equal(TreeNodeKind.Worktree, row.Kind);
        Assert.Equal("/repo/wt", row.WorktreePath);
        Assert.NotNull(row.GitState);
        Assert.True(row.GitState.IsSparse);
        Assert.True(row.GitState.MergeOrRebase);
        Assert.Equal(GitHeadKind.Branch, row.GitState.Head);
    }
}
