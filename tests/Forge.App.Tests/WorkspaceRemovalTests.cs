using Forge.Abstractions;
using Forge.App.Lifecycle;
using Forge.Core.Storage;
using Forge.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Removing a workspace removes every session on it through the same purge as a session removal (7.3). The inheritance
/// guard refuses to purge a session while another session still inherits from it, so the flow has to go in rounds: an heir
/// first, the session it inherits from in the next pass. Without the rounds the refused session would silently stay behind
/// after the directory was already recycled.
/// </summary>
public sealed class WorkspaceRemovalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-tests-workspace-removal-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(directory, true);
        }
        catch (IOException)
        {
            // A pooled handle can outlive the test; the temp directory is the OS's problem then.
        }
    }

    [Fact]
    public async Task Removing_a_workspace_purges_an_heir_before_the_session_it_inherits_from()
    {
        var worktree = Path.Combine(directory, "worktree");
        System.IO.Directory.CreateDirectory(worktree);
        await using var store = new SqliteSessionStore(directory);
        var older = await store.CreateAsync(
            new SessionCreate(worktree, "older") { ProjectId = "p1", WorktreePath = worktree },
            CancellationToken.None);
        Assert.Null(await store.ArchiveAndPromoteAsync(older.Id, null, CancellationToken.None));
        var heir = await store.CreateAsync(
            new SessionCreate(worktree, "heir")
            {
                ProjectId = "p1",
                WorktreePath = worktree,
                InheritsFromSessionId = older.Id,
                InheritsUpToSeq = 0,
            },
            CancellationToken.None);
        Assert.Null(await store.ArchiveAndPromoteAsync(heir.Id, null, CancellationToken.None));

        // The older row goes first, so its purge is refused once: the heir is removed in that same round and the older
        // session in the next one. Without the rounds the workspace would end up with the refused session still there.
        await store.TouchAsync(older.Id, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        var project = new ProjectInfo("p1", "repo", Path.Combine(directory, "repo"), ProjectKind.Repository, 0, DateTimeOffset.UnixEpoch);
        var plan = new WorkspaceRemovalPlan(
            project,
            worktree,
            Branch: null,
            BaseBranch: null,
            Missing: false,
            Guard: [],
            Sessions: [older, heir],
            Active: null,
            UnmergedForks: []);
        var trashCalls = 0;
        var flow = new RemovalFlow(
            store,
            store,
            new UnremovableGit(worktree),
            (_, _) =>
            {
                Interlocked.Increment(ref trashCalls);
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask);

        var outcome = await flow.ExecuteWorkspaceRemovalAsync(plan, CancellationToken.None);

        Assert.Null(outcome.Error);
        Assert.Empty(outcome.Refused);
        Assert.Contains(older.Id, outcome.RemovedSessionIds);
        Assert.Contains(heir.Id, outcome.RemovedSessionIds);
        Assert.Null(await store.GetSessionAsync(older.Id, CancellationToken.None));
        Assert.Null(await store.GetSessionAsync(heir.Id, CancellationToken.None));
        Assert.Equal(1, trashCalls);
    }

    /// <summary>A git the removal guard finds nothing to refuse in: the worktree is registered and clean.</summary>
    private sealed class UnremovableGit(string worktree) : IRemovalGit
    {
        public Task<IReadOnlyList<string>> CheckRemovableAsync(string repositoryRoot, string directory, string? baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> CheckCommitsAsync(string repositoryRoot, string head, bool detached, string? baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task PruneAsync(string repositoryRoot, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> IsAncestorAsync(string repositoryRoot, string ancestor, string descendant, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<bool> BranchExistsAsync(string repositoryRoot, string branch, CancellationToken ct) => Task.FromResult(true);

        public Task AttachWorktreeAsync(string repositoryRoot, string directory, string branch, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string repositoryRoot, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GitWorktree>>(
                [new GitWorktree(worktree, "sha", "branch", IsDetached: false, IsLocked: false, LockReason: null, IsPrunable: false, PrunableReason: null, IsBare: false)]);
    }
}
