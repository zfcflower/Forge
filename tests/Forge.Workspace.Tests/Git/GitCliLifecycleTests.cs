using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>Merge options, ancestry, branch rename, upstream and sparse reads used by the session/worktree lifecycle.</summary>
public sealed class GitCliLifecycleTests
{
    private static GitCli Cli() => new();

    private static TestRepo Diverged(out TempDirectory sandbox, bool conflicting)
    {
        sandbox = new TempDirectory();
        var repo = new TestRepo(sandbox);
        repo.Write("shared.txt", "base\n");
        repo.Commit("base");
        repo.RunGit("checkout", "-q", "-b", "side");
        repo.Write(conflicting ? "shared.txt" : "side.txt", "side\n");
        repo.Commit("side work");
        repo.RunGit("checkout", "-q", "main");
        if (conflicting)
        {
            repo.Write("shared.txt", "main\n");
            repo.Commit("main work");
        }

        return repo;
    }

    // ---- merge --no-ff ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Merge_with_no_fast_forward_creates_a_merge_commit_even_when_a_fast_forward_is_possible()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = Diverged(out var sandbox, conflicting: false);
        using (sandbox)
        {
            var before = repo.Head();

            var result = await Cli().MergeAsync(repo.Root, "side", noFastForward: true);

            Assert.True(result.Success, result.Message);
            var parents = repo.RunGit("rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ');
            Assert.Equal(3, parents.Length); // commit + two parents
            Assert.Equal(before, parents[1]);
            Assert.True(await Cli().IsAncestorAsync(repo.Root, "side", "HEAD"));
        }
    }

    [Fact]
    public async Task Merge_without_the_option_still_fast_forwards()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = Diverged(out var sandbox, conflicting: false);
        using (sandbox)
        {
            var side = repo.RunGit("rev-parse", "side").Trim();

            var result = await Cli().MergeAsync(repo.Root, "side");

            Assert.True(result.Success, result.Message);
            Assert.Equal(side, repo.Head());
        }
    }

    [Fact]
    public async Task Merge_with_no_fast_forward_when_already_up_to_date_reports_it_and_adds_no_commit()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = Diverged(out var sandbox, conflicting: false);
        using (sandbox)
        {
            Assert.True((await Cli().MergeAsync(repo.Root, "side", noFastForward: true)).Success);
            var head = repo.Head();

            var again = await Cli().MergeAsync(repo.Root, "side", noFastForward: true);

            Assert.True(again.Success);
            Assert.Contains("Already up to date", again.Message, StringComparison.Ordinal);
            Assert.Equal(head, repo.Head());
        }
    }

    [Fact]
    public async Task Merge_conflict_is_detected_as_in_progress_and_abort_restores_the_tree()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = Diverged(out var sandbox, conflicting: true);
        using (sandbox)
        {
            var before = repo.Head();
            Assert.False(await Cli().HasMergeInProgressAsync(repo.Root));

            var merge = await Cli().MergeAsync(repo.Root, "side", noFastForward: true);

            Assert.False(merge.Success);
            Assert.Equal(GitRemoteFailureKind.Conflict, merge.Failure);
            Assert.True(await Cli().HasMergeInProgressAsync(repo.Root));
            Assert.False(await Cli().HasRebaseInProgressAsync(repo.Root));
            Assert.Contains("<<<<<<<", File.ReadAllText(repo.FullPath("shared.txt")), StringComparison.Ordinal);

            await Cli().AbortMergeAsync(repo.Root);

            Assert.False(await Cli().HasMergeInProgressAsync(repo.Root));
            Assert.Equal(before, repo.Head());
            Assert.Equal("main\n", File.ReadAllText(repo.FullPath("shared.txt")).ReplaceLineEndings("\n"));
            Assert.Empty(await Cli().GetStatusAsync(repo.Root));
        }
    }

    [Fact]
    public async Task Abort_without_a_merge_in_progress_throws()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = Diverged(out var sandbox, conflicting: false);
        using (sandbox)
        {
            await Assert.ThrowsAsync<GitException>(() => Cli().AbortMergeAsync(repo.Root));
        }
    }

    [Fact]
    public async Task Merge_refused_because_of_local_changes_reports_gits_file_list_and_changes_nothing()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = Diverged(out var sandbox, conflicting: false);
        using (sandbox)
        {
            // side changes shared.txt too, so an uncommitted edit of it would be overwritten by a fast-forward-less merge.
            repo.RunGit("checkout", "-q", "side");
            repo.Write("shared.txt", "side edit\n");
            repo.Commit("side edits shared");
            repo.RunGit("checkout", "-q", "main");
            repo.Write("shared.txt", "uncommitted local edit\n");
            var before = repo.Head();

            var result = await Cli().MergeAsync(repo.Root, "side", noFastForward: true);

            Assert.False(result.Success);
            Assert.Contains("shared.txt", result.StandardError, StringComparison.Ordinal);
            Assert.Contains("shared.txt", result.Message, StringComparison.Ordinal);
            Assert.Equal(before, repo.Head());
            Assert.False(await Cli().HasMergeInProgressAsync(repo.Root));
            Assert.Equal("uncommitted local edit\n", File.ReadAllText(repo.FullPath("shared.txt")).ReplaceLineEndings("\n"));
        }
    }

    [Fact]
    public async Task Merge_rejects_an_option_looking_revision()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = Diverged(out var sandbox, conflicting: false);
        using (sandbox)
        {
            var result = await Cli().MergeAsync(repo.Root, "--abort", noFastForward: true);

            Assert.False(result.Success);
        }
    }

    // ---- rebase in progress -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_stopped_rebase_is_reported_as_rebase_in_progress_not_merge()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = Diverged(out var sandbox, conflicting: true);
        using (sandbox)
        {
            repo.RunGit("checkout", "-q", "side");
            var rebase = GitFixture.Run(repo.Root, "rebase", "main");
            Assert.NotEqual(0, rebase.ExitCode);

            Assert.True(await Cli().HasRebaseInProgressAsync(repo.Root));
            Assert.False(await Cli().HasMergeInProgressAsync(repo.Root));

            GitFixture.RunChecked(repo.Root, "rebase", "--abort");
            Assert.False(await Cli().HasRebaseInProgressAsync(repo.Root));
        }
    }

    [Fact]
    public async Task Merge_in_progress_is_read_per_worktree()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = Diverged(out var sandbox, conflicting: true);
        using (sandbox)
        {
            var worktreePath = Path.Combine(sandbox.Path, "wt");
            repo.RunGit("worktree", "add", "-q", "-b", "wt-branch", worktreePath, "main");

            Assert.False((await Cli().MergeAsync(worktreePath, "side", noFastForward: true)).Success);

            Assert.True(await Cli().HasMergeInProgressAsync(worktreePath));
            Assert.False(await Cli().HasMergeInProgressAsync(repo.Root));
        }
    }

    // ---- ListCommitsAsync / IsAncestorAsync --------------------------------------------------------------------

    [Fact]
    public async Task ListCommits_returns_the_commits_of_a_range_newest_first()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "1\n");
        var baseCommit = repo.Commit("base");
        repo.Write("a.txt", "2\n");
        repo.Commit("second");
        repo.Write("a.txt", "3\n");
        repo.Commit("third");

        var commits = await Cli().ListCommitsAsync(repo.Root, baseCommit + "..HEAD");

        Assert.Equal(["third", "second"], commits.Select(commit => commit.Subject));
    }

    [Fact]
    public async Task ListCommits_of_an_empty_range_is_empty_and_of_an_unknown_range_throws()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "1\n");
        repo.Commit("base");

        Assert.Empty(await Cli().ListCommitsAsync(repo.Root, "HEAD..HEAD"));
        await Assert.ThrowsAsync<GitException>(() => Cli().ListCommitsAsync(repo.Root, "nope..HEAD"));
    }

    [Fact]
    public async Task IsAncestor_is_true_for_an_older_commit_false_for_a_diverged_one_and_throws_for_garbage()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = Diverged(out var sandbox, conflicting: true);
        using (sandbox)
        {
            var baseCommit = repo.RunGit("merge-base", "main", "side").Trim();

            Assert.True(await Cli().IsAncestorAsync(repo.Root, baseCommit, "main"));
            Assert.True(await Cli().IsAncestorAsync(repo.Root, "main", "main"));
            Assert.False(await Cli().IsAncestorAsync(repo.Root, "side", "main"));
            Assert.False(await Cli().IsAncestorAsync(repo.Root, "main", baseCommit));
            await Assert.ThrowsAsync<GitException>(() => Cli().IsAncestorAsync(repo.Root, "no-such-ref", "main"));
            await Assert.ThrowsAsync<ArgumentException>(() => Cli().IsAncestorAsync(repo.Root, "--help", "main"));
        }
    }

    // ---- RenameBranchAsync --------------------------------------------------------------------------------------

    [Fact]
    public async Task RenameBranch_moves_the_branch_and_keeps_its_commits()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = Diverged(out var sandbox, conflicting: false);
        using (sandbox)
        {
            var tip = repo.RunGit("rev-parse", "side").Trim();

            await Cli().RenameBranchAsync(repo.Root, "side", "renamed/side");

            Assert.Equal(tip, await Cli().RevParseAsync(repo.Root, "refs/heads/renamed/side"));
            Assert.Null(await Cli().RevParseAsync(repo.Root, "refs/heads/side"));
        }
    }

    [Fact]
    public async Task RenameBranch_renames_the_branch_checked_out_in_a_linked_worktree()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = Diverged(out var sandbox, conflicting: false);
        using (sandbox)
        {
            var worktreePath = Path.Combine(sandbox.Path, "wt");
            repo.RunGit("worktree", "add", "-q", "-b", "wt-branch", worktreePath, "main");

            await Cli().RenameBranchAsync(repo.Root, "wt-branch", "wt-renamed");

            Assert.Equal("wt-renamed", await Cli().GetCurrentBranchAsync(worktreePath));
        }
    }

    [Fact]
    public async Task RenameBranch_to_an_existing_name_throws_and_leaves_both_branches()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = Diverged(out var sandbox, conflicting: false);
        using (sandbox)
        {
            var side = repo.RunGit("rev-parse", "side").Trim();
            var main = repo.Head();

            var failure = await Assert.ThrowsAsync<GitException>(() => Cli().RenameBranchAsync(repo.Root, "side", "main"));

            Assert.Contains("already exists", failure.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(side, await Cli().RevParseAsync(repo.Root, "refs/heads/side"));
            Assert.Equal(main, await Cli().RevParseAsync(repo.Root, "refs/heads/main"));
        }
    }

    [Fact]
    public async Task RenameBranch_of_an_unknown_branch_or_to_an_invalid_name_throws()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = Diverged(out var sandbox, conflicting: false);
        using (sandbox)
        {
            await Assert.ThrowsAsync<GitException>(() => Cli().RenameBranchAsync(repo.Root, "missing", "other"));
            await Assert.ThrowsAsync<GitException>(() => Cli().RenameBranchAsync(repo.Root, "side", "bad name with ~tilde"));
            await Assert.ThrowsAsync<ArgumentException>(() => Cli().RenameBranchAsync(repo.Root, "side", "-D"));
            Assert.NotNull(await Cli().RevParseAsync(repo.Root, "refs/heads/side"));
        }
    }

    // ---- GetUpstreamRemoteAsync ---------------------------------------------------------------------------------

    [Fact]
    public async Task UpstreamRemote_is_null_without_a_configured_upstream_and_the_remote_name_with_one()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = Diverged(out var sandbox, conflicting: false);
        using (sandbox)
        {
            Assert.Null(await Cli().GetUpstreamRemoteAsync(repo.Root, "side"));

            // Config only: the remote does not need to exist or be reachable.
            repo.RunGit("config", "branch.side.remote", "origin");
            repo.RunGit("config", "branch.side.merge", "refs/heads/side");

            Assert.Equal("origin", await Cli().GetUpstreamRemoteAsync(repo.Root, "side"));
            Assert.Null(await Cli().GetUpstreamRemoteAsync(repo.Root, "main"));
        }
    }

    [Fact]
    public async Task UpstreamRemote_of_a_branch_with_a_slash_in_its_name_is_found()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = Diverged(out var sandbox, conflicting: false);
        using (sandbox)
        {
            repo.RunGit("branch", "forge/feature");
            repo.RunGit("config", "branch.forge/feature.remote", "upstream");

            Assert.Equal("upstream", await Cli().GetUpstreamRemoteAsync(repo.Root, "forge/feature"));
        }
    }

    // ---- sparse reads --------------------------------------------------------------------------------------------

    [Fact]
    public async Task SparseConfig_is_null_for_an_ordinary_checkout()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = Diverged(out var sandbox, conflicting: false);
        using (sandbox)
        {
            Assert.Null(await Cli().GetSparseConfigAsync(repo.Root));
        }
    }

    [Fact]
    public async Task SparseConfig_lists_the_cone_directories_of_a_sparse_checkout()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("alpha/a.txt", "a\n");
        repo.Write("beta/b.txt", "b\n");
        repo.Write("top.txt", "t\n");
        repo.Commit("initial");
        repo.RunGit("sparse-checkout", "set", "--cone", "beta");

        var config = await Cli().GetSparseConfigAsync(repo.Root);

        Assert.NotNull(config);
        Assert.Equal(["beta"], config.Directories);
    }

    [Fact]
    public async Task SparseConfig_is_null_again_once_sparse_checkout_is_disabled()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("alpha/a.txt", "a\n");
        repo.Commit("initial");
        repo.RunGit("sparse-checkout", "set", "--cone", "alpha");
        repo.RunGit("sparse-checkout", "disable");

        Assert.Null(await Cli().GetSparseConfigAsync(repo.Root));
    }

    [Fact]
    public async Task TopLevelDirectories_lists_only_directories_of_the_revision()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("alpha/a.txt", "a\n");
        repo.Write("beta/nested/b.txt", "b\n");
        repo.Write("top.txt", "t\n");
        repo.Write("with space/s.txt", "s\n");
        repo.Commit("initial");

        var directories = await Cli().ListTopLevelDirectoriesAsync(repo.Root, "HEAD");

        Assert.Equal(["alpha", "beta", "with space"], directories.Order(StringComparer.Ordinal));
        await Assert.ThrowsAsync<GitException>(() => Cli().ListTopLevelDirectoriesAsync(repo.Root, "no-such-rev"));
    }
}
