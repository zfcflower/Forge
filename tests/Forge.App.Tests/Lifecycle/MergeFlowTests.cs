using System.Text;
using Forge.Abstractions;
using Forge.App.Lifecycle;
using Forge.App.Localization;
using Forge.App.Tests.Git;
using Forge.Core.Storage;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Lifecycle;

/// <summary>
/// Merge back / sync (TASK-session-worktree-lifecycle 6.6) against real git repositories: the preview finds the
/// workspace that has the source branch checked out through <c>git worktree list</c>, lists the commits a merge brings
/// in and detects "already merged" / "up to date"; a merge back with no such workspace degrades (no ancestor guessing)
/// to pushing the fork branch; the merge is <c>--no-ff</c> in the right direction; a conflict leaves the target in the
/// merge and abort restores it; git's refusal to overwrite local changes is relayed with the file it names.
/// </summary>
public sealed class MergeFlowTests : IDisposable
{
    private readonly GitTestSandbox sandbox = GitTestSandbox.Create();
    private readonly MergeFlow flow = new(new MergeGit(new GitCli()));
    private readonly string repo;
    private readonly string forkDirectory;
    private readonly string baseSha;

    public MergeFlowTests()
    {
        repo = sandbox.CloneTracked("repo");
        forkDirectory = Path.Combine(sandbox.Root, "fork-wt");
        baseSha = Git(repo, "rev-parse", "HEAD");
        Git(repo, "worktree", "add", "-q", "-b", "fork", forkDirectory, "main");
    }

    private ForkRelation Relation => new(
        new ProjectInfo("proj-1", "repo", repo, ProjectKind.Repository, 0, DateTimeOffset.UtcNow),
        forkDirectory,
        "fork",
        "main",
        baseSha);

    public void Dispose() => sandbox.Dispose();

    [Fact]
    public async Task The_merge_back_preview_targets_the_workspace_on_the_source_branch_and_lists_the_fork_commits()
    {
        sandbox.CommitFile(forkDirectory, "a.txt", "a\n", "fork one");
        sandbox.CommitFile(forkDirectory, "b.txt", "b\n", "fork two");

        var preview = await flow.PreviewAsync(Relation, MergeDirection.MergeBack, default);

        Assert.Null(preview.Error);
        Assert.True(preview.CanMerge);
        Assert.False(preview.AlreadyDone);
        Assert.False(preview.TargetMissing);
        AssertSamePath(repo, preview.TargetDirectory);
        Assert.Equal("fork", preview.Revision);
        Assert.Equal("main", preview.TargetBranch);
        Assert.Equal(["fork two", "fork one"], preview.Commits.Select(static commit => commit.Subject));
    }

    [Fact]
    public async Task Merging_back_creates_a_merge_commit_on_the_source_branch_and_is_then_already_merged()
    {
        sandbox.CommitFile(forkDirectory, "a.txt", "a\n", "fork one");
        var preview = await flow.PreviewAsync(Relation, MergeDirection.MergeBack, default);

        var outcome = await flow.ExecuteAsync(preview);

        Assert.Equal(MergeOutcomeKind.Merged, outcome.Kind);
        Assert.Equal(ForkStrings.Merged("fork", "main"), outcome.Message);
        Assert.True(File.Exists(Path.Combine(repo, "a.txt")));

        // --no-ff: a merge commit with two parents even though a fast-forward was possible.
        Assert.Equal(3, Git(repo, "rev-list", "--parents", "-n", "1", "main").Split(' ').Length);
        Assert.Equal("fork", Git(forkDirectory, "rev-parse", "--abbrev-ref", "HEAD"));

        var again = await flow.PreviewAsync(Relation, MergeDirection.MergeBack, default);
        Assert.True(again.AlreadyDone);
        Assert.False(again.CanMerge);
        Assert.Empty(again.Commits);
        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.ExecuteAsync(again));
    }

    [Fact]
    public async Task A_fork_without_new_commits_is_already_merged()
    {
        var preview = await flow.PreviewAsync(Relation, MergeDirection.MergeBack, default);

        Assert.Null(preview.Error);
        Assert.True(preview.AlreadyDone);
        Assert.False(preview.CanMerge);
    }

    [Fact]
    public async Task A_source_branch_nobody_has_checked_out_degrades_the_merge_back_without_an_error()
    {
        sandbox.CommitFile(forkDirectory, "a.txt", "a\n", "fork one");
        Git(repo, "checkout", "-q", "-b", "elsewhere");

        var preview = await flow.PreviewAsync(Relation, MergeDirection.MergeBack, default);

        Assert.Null(preview.Error);
        Assert.True(preview.TargetMissing);
        Assert.False(preview.CanMerge);
        Assert.Null(preview.TargetDirectory);
        Assert.Equal("fork", preview.ForkBranch);
        Assert.Equal("main", preview.SourceBranch);
    }

    [Fact]
    public async Task A_renamed_source_branch_degrades_the_merge_back_and_guesses_no_other_target()
    {
        sandbox.CommitFile(forkDirectory, "a.txt", "a\n", "fork one");
        Git(repo, "branch", "-m", "main", "trunk");

        var preview = await flow.PreviewAsync(Relation, MergeDirection.MergeBack, default);

        // "trunk" contains fork_base_sha, but the target is never derived from ancestry (section 9).
        Assert.Null(preview.Error);
        Assert.True(preview.TargetMissing);
        Assert.Null(preview.TargetDirectory);
    }

    [Fact]
    public async Task The_preview_follows_a_fork_branch_renamed_in_its_workspace()
    {
        sandbox.CommitFile(forkDirectory, "a.txt", "a\n", "fork one");
        Git(forkDirectory, "branch", "-m", "fork", "renamed");

        var preview = await flow.PreviewAsync(Relation, MergeDirection.MergeBack, default);

        Assert.Null(preview.Error);
        Assert.Equal("renamed", preview.ForkBranch);
        Assert.Equal("renamed", preview.Revision);
        Assert.Single(preview.Commits);
    }

    [Fact]
    public async Task A_fork_branch_that_no_longer_exists_cannot_be_planned()
    {
        Git(repo, "worktree", "remove", "--force", forkDirectory);
        Git(repo, "branch", "-D", "fork");

        var back = await flow.PreviewAsync(Relation, MergeDirection.MergeBack, default);
        var sync = await flow.PreviewAsync(Relation, MergeDirection.Sync, default);

        Assert.Equal(ForkStrings.ForkMissing("fork"), back.Error);
        Assert.False(back.CanMerge);
        Assert.False(back.TargetMissing);
        Assert.Equal(ForkStrings.ForkMissing("fork"), sync.Error);
    }

    [Fact]
    public async Task A_conflict_leaves_the_target_in_the_merge_and_abort_restores_it()
    {
        sandbox.CommitFile(forkDirectory, "file.txt", "fork side\n", "fork edit");
        sandbox.CommitFile(repo, "file.txt", "main side\n", "main edit");
        var preview = await flow.PreviewAsync(Relation, MergeDirection.MergeBack, default);
        Assert.True(preview.CanMerge);
        var git = new GitCli();

        var outcome = await flow.ExecuteAsync(preview);

        Assert.Equal(MergeOutcomeKind.Conflict, outcome.Kind);
        Assert.Equal(ForkStrings.Conflict(preview.TargetDirectory!), outcome.Message);
        Assert.True(await git.HasMergeInProgressAsync(repo));
        Assert.False(await git.HasMergeInProgressAsync(forkDirectory));

        var failure = await flow.AbortAsync(repo);

        Assert.Null(failure);
        Assert.False(await git.HasMergeInProgressAsync(repo));
        Assert.Equal("main side\n", File.ReadAllText(Path.Combine(repo, "file.txt")));
        Assert.Equal(string.Empty, Git(repo, "status", "--porcelain"));
    }

    [Fact]
    public async Task Aborting_where_no_merge_is_in_progress_reports_the_reason()
    {
        var failure = await flow.AbortAsync(repo);

        Assert.NotNull(failure);
        Assert.NotEqual(string.Empty, failure);
    }

    [Fact]
    public async Task Local_changes_the_merge_would_overwrite_are_refused_with_the_file_named_and_nothing_changes()
    {
        sandbox.CommitFile(forkDirectory, "file.txt", "fork side\n", "fork edit");
        File.WriteAllText(Path.Combine(repo, "file.txt"), "uncommitted\n", new UTF8Encoding(false));
        var head = Git(repo, "rev-parse", "HEAD");
        var preview = await flow.PreviewAsync(Relation, MergeDirection.MergeBack, default);

        var outcome = await flow.ExecuteAsync(preview);

        Assert.Equal(MergeOutcomeKind.Failed, outcome.Kind);
        Assert.Contains("file.txt", outcome.Message, StringComparison.Ordinal);
        Assert.False(await new GitCli().HasMergeInProgressAsync(repo));
        Assert.Equal(head, Git(repo, "rev-parse", "HEAD"));
        Assert.Equal("uncommitted\n", File.ReadAllText(Path.Combine(repo, "file.txt")));
    }

    [Fact]
    public async Task A_target_with_unrelated_local_changes_is_merged_and_keeps_them()
    {
        sandbox.CommitFile(forkDirectory, "a.txt", "a\n", "fork one");
        File.WriteAllText(Path.Combine(repo, "file.txt"), "uncommitted\n", new UTF8Encoding(false));
        var preview = await flow.PreviewAsync(Relation, MergeDirection.MergeBack, default);

        var outcome = await flow.ExecuteAsync(preview);

        Assert.Equal(MergeOutcomeKind.Merged, outcome.Kind);
        Assert.True(File.Exists(Path.Combine(repo, "a.txt")));
        Assert.Equal("uncommitted\n", File.ReadAllText(Path.Combine(repo, "file.txt")));
    }

    [Fact]
    public async Task Sync_merges_the_source_branch_into_the_fork_workspace_and_is_then_up_to_date()
    {
        sandbox.CommitFile(repo, "m.txt", "m\n", "main one");
        sandbox.CommitFile(forkDirectory, "a.txt", "a\n", "fork one");

        var preview = await flow.PreviewAsync(Relation, MergeDirection.Sync, default);

        Assert.Null(preview.Error);
        Assert.True(preview.CanMerge);
        AssertSamePath(forkDirectory, preview.TargetDirectory);
        Assert.Equal("main", preview.Revision);
        Assert.Equal("fork", preview.TargetBranch);
        Assert.Equal(["main one"], preview.Commits.Select(static commit => commit.Subject));
        var mainHead = Git(repo, "rev-parse", "main");

        var outcome = await flow.ExecuteAsync(preview);

        Assert.Equal(MergeOutcomeKind.Merged, outcome.Kind);
        Assert.Equal(ForkStrings.Merged("main", "fork"), outcome.Message);
        Assert.True(File.Exists(Path.Combine(forkDirectory, "m.txt")));
        Assert.Equal(3, Git(forkDirectory, "rev-list", "--parents", "-n", "1", "fork").Split(' ').Length);

        // The source branch itself did not move.
        Assert.Equal(mainHead, Git(repo, "rev-parse", "main"));
        Assert.False(File.Exists(Path.Combine(repo, "a.txt")));

        var again = await flow.PreviewAsync(Relation, MergeDirection.Sync, default);
        Assert.True(again.AlreadyDone);
        Assert.False(again.CanMerge);
    }

    [Fact]
    public async Task Sync_does_not_need_the_source_branch_checked_out_anywhere()
    {
        sandbox.CommitFile(repo, "m.txt", "m\n", "main one");
        Git(repo, "checkout", "-q", "-b", "elsewhere");

        var preview = await flow.PreviewAsync(Relation, MergeDirection.Sync, default);

        Assert.Null(preview.Error);
        Assert.True(preview.CanMerge);
        AssertSamePath(forkDirectory, preview.TargetDirectory);
    }

    [Fact]
    public async Task Sync_from_a_source_branch_that_is_gone_is_an_error()
    {
        Git(repo, "branch", "-m", "main", "trunk");

        var preview = await flow.PreviewAsync(Relation, MergeDirection.Sync, default);

        Assert.Equal(ForkStrings.SourceMissing("main"), preview.Error);
        Assert.False(preview.CanMerge);
        Assert.False(preview.TargetMissing);
    }

    [Fact]
    public async Task A_repository_git_cannot_read_fails_the_preview_with_the_reason()
    {
        var missing = Path.Combine(sandbox.Root, "nowhere");
        var relation = Relation with { Project = Relation.Project with { RootPath = missing } };

        var preview = await flow.PreviewAsync(relation, MergeDirection.MergeBack, default);

        Assert.NotNull(preview.Error);
        Assert.False(preview.CanMerge);
        Assert.False(preview.TargetMissing);
    }

    [Fact]
    public async Task Pushing_the_fork_branch_publishes_it_with_an_upstream()
    {
        sandbox.CommitFile(forkDirectory, "a.txt", "a\n", "fork one");

        var failure = await flow.PushForkAsync(Relation, "fork", default);

        Assert.Null(failure);
        Assert.Contains("refs/heads/fork", Git(repo, "ls-remote", "--heads", "origin", "fork"), StringComparison.Ordinal);
        Assert.Equal("origin/fork", Git(forkDirectory, "rev-parse", "--abbrev-ref", "fork@{upstream}"));
    }

    [Fact]
    public async Task A_failed_push_is_reported_with_the_reason()
    {
        Git(repo, "remote", "set-url", "origin", Path.Combine(sandbox.Root, "missing.git"));

        var failure = await flow.PushForkAsync(Relation, "fork", default);

        Assert.NotNull(failure);
        Assert.StartsWith(ForkStrings.PushFailed(string.Empty), failure, StringComparison.Ordinal);
        Assert.NotEqual(ForkStrings.PushFailed(string.Empty), failure);
    }

    [Fact]
    public async Task A_target_switched_to_another_branch_after_the_preview_merges_nothing()
    {
        sandbox.CommitFile(forkDirectory, "a.txt", "a\n", "fork one");
        var preview = await flow.PreviewAsync(Relation, MergeDirection.MergeBack, default);
        Git(repo, "switch", "-q", "-c", "other");
        var otherHead = Git(repo, "rev-parse", "HEAD");

        var outcome = await flow.ExecuteAsync(preview);

        Assert.Equal(MergeOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(ForkStrings.TargetMoved(preview.TargetDirectory!, "main"), outcome.Message);
        Assert.Equal(otherHead, Git(repo, "rev-parse", "HEAD"));
        Assert.Equal(baseSha, Git(repo, "rev-parse", "main"));
        Assert.False(File.Exists(Path.Combine(repo, "a.txt")));
    }

    [Fact]
    public async Task A_sync_whose_fork_workspace_switched_branch_merges_nothing()
    {
        sandbox.CommitFile(repo, "up.txt", "up\n", "upstream");
        var preview = await flow.PreviewAsync(Relation, MergeDirection.Sync, default);
        Git(forkDirectory, "switch", "-q", "-c", "elsewhere");

        var outcome = await flow.ExecuteAsync(preview);

        Assert.Equal(MergeOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(ForkStrings.TargetMoved(preview.TargetDirectory!, "fork"), outcome.Message);
        Assert.False(File.Exists(Path.Combine(forkDirectory, "up.txt")));
    }

    private static string Git(string directory, params string[] arguments) =>
        GitTestShell.RunChecked(directory, arguments).Trim();

    private static void AssertSamePath(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(OccupancyKey.Compute(null, expected), OccupancyKey.Compute(null, actual));
    }
}
