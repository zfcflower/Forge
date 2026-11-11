using Xunit;

namespace Forge.Workspace.Tests.Worktrees;

/// <summary>
/// The removal guards as a read-only check, the fork-source base branch, and the sparse worktree creation of the
/// session/worktree lifecycle work. Real temporary repositories.
/// </summary>
public sealed class WorktreeLifecycleTests
{
    private static WorktreeManager Manager() => new(new GitCli());

    private static (TempDirectory Sandbox, TestRepo Repo) Repository()
    {
        var sandbox = new TempDirectory();
        var repo = new TestRepo(sandbox);
        repo.Write("README.md", "# repo\n");
        repo.Commit("initial");
        return (sandbox, repo);
    }

    // ---- CheckRemovableAsync: one case per guard -------------------------------------------------------------

    [Fact]
    public async Task CheckRemovable_of_a_clean_merged_worktree_has_no_reasons()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "clean");

            var reasons = await manager.CheckRemovableAsync(repo.Root, session.Path);

            Assert.Empty(reasons);
        }
    }

    [Fact]
    public async Task CheckRemovable_reports_uncommitted_changes()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "dirty");
            File.WriteAllText(Path.Combine(session.Path, "scratch.txt"), "work\n");

            var reasons = await manager.CheckRemovableAsync(repo.Root, session.Path);

            var reason = Assert.Single(reasons);
            Assert.Contains("1 uncommitted change", reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CheckRemovable_reports_a_detached_head_that_no_ref_contains()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "detach");
            GitFixture.RunChecked(session.Path, "checkout", "-q", "--detach");
            File.WriteAllText(Path.Combine(session.Path, "lost.txt"), "x\n");
            GitFixture.RunChecked(session.Path, "add", "-A");
            GitFixture.RunChecked(session.Path, "commit", "-q", "-m", "orphan");

            var reasons = await manager.CheckRemovableAsync(repo.Root, session.Path, "main");

            Assert.Contains(reasons, reason => reason.Contains("HEAD is detached", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task CheckRemovable_reports_commits_not_merged_into_the_base()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "ahead");
            File.WriteAllText(Path.Combine(session.Path, "feature.txt"), "x\n");
            GitFixture.RunChecked(session.Path, "add", "-A");
            GitFixture.RunChecked(session.Path, "commit", "-q", "-m", "feature");

            var reasons = await manager.CheckRemovableAsync(repo.Root, session.Path);

            var reason = Assert.Single(reasons);
            Assert.Contains("1 commit not merged into 'main'", reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CheckRemovable_reports_when_no_base_branch_can_be_determined()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "nobase");
            // None of main/master/trunk exists any more and there is no origin, so the default branch is unknowable.
            repo.RunGit("branch", "-m", "main", "develop");

            var reasons = await manager.CheckRemovableAsync(repo.Root, session.Path);

            Assert.Contains(reasons, reason => reason.Contains("no base branch could be determined", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task CheckRemovable_does_not_touch_the_worktree()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "readonly");
            File.WriteAllText(Path.Combine(session.Path, "scratch.txt"), "work\n");

            _ = await manager.CheckRemovableAsync(repo.Root, session.Path);

            Assert.True(File.Exists(Path.Combine(session.Path, "scratch.txt")));
            Assert.Contains(await manager.ListAsync(repo.Root), worktree => PathsEqual(worktree.Path, session.Path));
        }
    }

    // ---- baseBranch = fork source -----------------------------------------------------------------------------

    [Fact]
    public async Task CheckRemovable_counts_commits_already_merged_into_the_fork_source_as_merged()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            // The "source" is a feature branch checked out in the main repository; main itself never receives the fork's work.
            repo.RunGit("checkout", "-q", "-b", "source");
            var fork = await manager.CreateAsync(repo.Root, "fork", "forge/fork", "source");
            File.WriteAllText(Path.Combine(fork.Path, "fork.txt"), "x\n");
            GitFixture.RunChecked(fork.Path, "add", "-A");
            GitFixture.RunChecked(fork.Path, "commit", "-q", "-m", "fork work");
            repo.RunGit("merge", "-q", "--no-ff", "-m", "merge fork", "forge/fork");

            var againstMain = await manager.CheckRemovableAsync(repo.Root, fork.Path, "main");
            var againstSource = await manager.CheckRemovableAsync(repo.Root, fork.Path, "source");

            Assert.Contains(againstMain, reason => reason.Contains("not merged into 'main'", StringComparison.Ordinal));
            Assert.Empty(againstSource);
        }
    }

    [Fact]
    public async Task CheckRemovable_still_counts_commits_the_fork_source_has_not_received()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            repo.RunGit("checkout", "-q", "-b", "source");
            var fork = await manager.CreateAsync(repo.Root, "fork", "forge/fork2", "source");
            File.WriteAllText(Path.Combine(fork.Path, "fork.txt"), "x\n");
            GitFixture.RunChecked(fork.Path, "add", "-A");
            GitFixture.RunChecked(fork.Path, "commit", "-q", "-m", "fork work");

            var reasons = await manager.CheckRemovableAsync(repo.Root, fork.Path, "source");

            Assert.Contains(reasons, reason => reason.Contains("1 commit not merged into 'source'", StringComparison.Ordinal));
        }
    }

    // ---- RemoveAsync behaviour unchanged ----------------------------------------------------------------------

    [Fact]
    public async Task Remove_throws_with_exactly_the_reasons_the_check_reports()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "both");
            File.WriteAllText(Path.Combine(session.Path, "a.txt"), "x\n");
            GitFixture.RunChecked(session.Path, "add", "-A");
            GitFixture.RunChecked(session.Path, "commit", "-q", "-m", "ahead");
            File.WriteAllText(Path.Combine(session.Path, "b.txt"), "y\n");

            var expected = await manager.CheckRemovableAsync(repo.Root, session.Path);
            var refusal = await Assert.ThrowsAsync<WorktreeRemovalRefusedException>(() => manager.RemoveAsync(repo.Root, session.Path));

            Assert.Equal(2, expected.Count);
            Assert.Equal(expected, refusal.Reasons);
            Assert.True(Directory.Exists(session.Path));
        }
    }

    [Fact]
    public async Task Remove_without_reasons_removes_the_directory_and_keeps_the_branch()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "gone");

            await manager.RemoveAsync(repo.Root, session.Path);

            Assert.False(Directory.Exists(session.Path));
            Assert.NotNull(await new GitCli().RevParseAsync(repo.Root, "refs/heads/" + session.Branch));
        }
    }

    [Fact]
    public async Task Remove_with_force_skips_the_check()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "forced");
            File.WriteAllText(Path.Combine(session.Path, "scratch.txt"), "work\n");

            await manager.RemoveAsync(repo.Root, session.Path, force: true);

            Assert.False(Directory.Exists(session.Path));
        }
    }

    [Fact]
    public async Task List_no_longer_has_a_worktree_whose_directory_was_removed_once_pruned()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "pruned");
            // Simulates the user's recycle bin taking the directory away behind git's back (no git involved).
            TempDirectory.DeleteTree(session.Path);
            Assert.False(Directory.Exists(session.Path));
            Assert.Contains(await manager.ListAsync(repo.Root), worktree => PathsEqual(worktree.Path, session.Path));

            await manager.PruneAsync(repo.Root);

            Assert.DoesNotContain(await manager.ListAsync(repo.Root), worktree => PathsEqual(worktree.Path, session.Path));
            Assert.NotNull(await new GitCli().RevParseAsync(repo.Root, "refs/heads/" + session.Branch));
        }
    }

    // ---- sparse worktrees -------------------------------------------------------------------------------------

    private static (TempDirectory Sandbox, TestRepo Repo) MultiDirectoryRepository()
    {
        var sandbox = new TempDirectory();
        var repo = new TestRepo(sandbox);
        repo.Write("top.txt", "top\n");
        repo.Write("alpha/a.txt", "a\n");
        repo.Write("beta/b.txt", "b\n");
        repo.Write("gamma/c.txt", "c\n");
        repo.Commit("initial");
        return (sandbox, repo);
    }

    [Fact]
    public async Task Sparse_create_materialises_only_the_selected_directories_and_leaves_the_main_checkout_alone()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = MultiDirectoryRepository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var git = new GitCli();

            var session = await manager.CreateAsync(
                repo.Root, "sparse", "forge/sparse", "HEAD", sparse: new SparseCheckout(["alpha", "beta"]));

            Assert.True(File.Exists(Path.Combine(session.Path, "alpha", "a.txt")));
            Assert.True(File.Exists(Path.Combine(session.Path, "beta", "b.txt")));
            Assert.True(File.Exists(Path.Combine(session.Path, "top.txt")));
            Assert.False(Directory.Exists(Path.Combine(session.Path, "gamma")));
            Assert.Empty(await git.GetStatusAsync(session.Path));
            Assert.Equal("forge/sparse", await git.GetCurrentBranchAsync(session.Path));

            // The main checkout: every file present, not sparse, nothing changed.
            Assert.True(File.Exists(repo.FullPath("gamma/c.txt")));
            Assert.Null(await git.GetSparseConfigAsync(repo.Root));
            Assert.Empty(await git.GetStatusAsync(repo.Root));
            Assert.Equal(string.Empty, GitFixture.Run(repo.Root, "config", "--local", "--get", "core.sparseCheckout").Output.Trim());
            Assert.Equal(string.Empty, GitFixture.Run(repo.Root, "config", "--local", "--get", "core.sparseCheckoutCone").Output.Trim());
        }
    }

    [Fact]
    public async Task Sparse_create_enables_per_worktree_config_and_keeps_the_pattern_state_in_the_worktree()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = MultiDirectoryRepository();
        using (sandbox)
        using (repo)
        {
            var session = await Manager().CreateAsync(
                repo.Root, "sparse", "forge/sparse", "HEAD", sparse: new SparseCheckout(["alpha"]));

            // Verified behaviour of git sparse-checkout set: it upgrades the repository to extensions.worktreeConfig and
            // writes core.sparseCheckout into the worktree's own config.worktree, not into the shared config.
            Assert.Equal("true", GitFixture.Run(repo.Root, "config", "--local", "--get", "extensions.worktreeConfig").Output.Trim());
            Assert.Equal("true", GitFixture.Run(session.Path, "config", "--worktree", "--get", "core.sparseCheckout").Output.Trim());
            Assert.Equal(1, GitFixture.Run(repo.Root, "config", "--local", "--get", "core.sparseCheckout").ExitCode);
        }
    }

    [Fact]
    public async Task Sparse_config_of_a_sparse_worktree_can_be_copied_to_a_second_worktree_identically()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = MultiDirectoryRepository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var git = new GitCli();
            var source = await manager.CreateAsync(
                repo.Root, "src", "forge/src", "HEAD", sparse: new SparseCheckout(["beta", "gamma"]));

            var config = await git.GetSparseConfigAsync(source.Path);
            Assert.NotNull(config);
            var copy = await manager.CreateAsync(repo.Root, "copy", "forge/copy", "HEAD", sparse: config);

            var copied = await git.GetSparseConfigAsync(copy.Path);
            Assert.NotNull(copied);
            Assert.Equal(config.Directories.Order(StringComparer.Ordinal), copied.Directories.Order(StringComparer.Ordinal));
            Assert.Equal(["beta", "gamma"], copied.Directories.Order(StringComparer.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(copy.Path, "alpha")));
            Assert.True(File.Exists(Path.Combine(copy.Path, "gamma", "c.txt")));
        }
    }

    [Fact]
    public async Task Sparse_create_with_an_unknown_directory_still_succeeds_and_keeps_only_root_files()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = MultiDirectoryRepository();
        using (sandbox)
        using (repo)
        {
            // Cone mode accepts a directory that does not exist in the commit; it just matches nothing.
            var session = await Manager().CreateAsync(
                repo.Root, "ghost", "forge/ghost", "HEAD", sparse: new SparseCheckout(["does-not-exist"]));

            Assert.True(File.Exists(Path.Combine(session.Path, "top.txt")));
            Assert.False(Directory.Exists(Path.Combine(session.Path, "alpha")));
        }
    }

    [Fact]
    public async Task Sparse_create_without_directories_fails_and_leaves_no_worktree_or_branch()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = MultiDirectoryRepository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();

            await Assert.ThrowsAnyAsync<ArgumentException>(() => manager.CreateAsync(
                repo.Root, "empty", "forge/empty", "HEAD", sparse: new SparseCheckout([])));

            var worktrees = await manager.ListAsync(repo.Root);
            Assert.Single(worktrees);
            Assert.Null(await new GitCli().RevParseAsync(repo.Root, "refs/heads/forge/empty"));
            Assert.False(Directory.Exists(manager.ResolveWorktreePath(repo.Root, "empty")));
        }
    }

    [Fact]
    public async Task Sparse_create_with_an_invalid_pattern_fails_and_cleans_the_worktree_up()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = MultiDirectoryRepository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();

            // A backslash-less but git-refused entry: cone mode rejects patterns with wildcard characters.
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => manager.CreateAsync(
                repo.Root, "bad", "forge/bad", "HEAD", sparse: new SparseCheckout(["al*pha"])));

            Assert.IsAssignableFrom<GitException>(failure);
            var worktrees = await manager.ListAsync(repo.Root);
            Assert.Single(worktrees);
            Assert.False(Directory.Exists(manager.ResolveWorktreePath(repo.Root, "bad")));
            Assert.Null(await new GitCli().RevParseAsync(repo.Root, "refs/heads/forge/bad"));
        }
    }

    // ---- Branch rollback (12.4): only the branch the failed creation itself made, only while unmoved, only -d ----

    [Fact]
    public async Task Rollback_deletes_the_branch_the_manager_just_created_while_it_is_unmoved()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "rollback");
            await manager.RemoveAsync(repo.Root, session.Path, force: false, baseBranch: session.BaseCommit);

            Assert.True(await manager.TryDeleteCreatedBranchAsync(session));

            Assert.Null(await new GitCli().RevParseAsync(repo.Root, "refs/heads/" + session.Branch));
        }
    }

    [Fact]
    public async Task Rollback_keeps_a_created_branch_that_moved_after_creation()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "moved");
            File.WriteAllText(Path.Combine(session.Path, "work.txt"), "work\n");
            GitFixture.RunChecked(session.Path, "add", "-A");
            GitFixture.RunChecked(session.Path, "commit", "-q", "-m", "work");
            var tip = GitFixture.RunChecked(session.Path, "rev-parse", "HEAD").Trim();

            Assert.False(await manager.TryDeleteCreatedBranchAsync(session));

            Assert.Equal(tip, await new GitCli().RevParseAsync(repo.Root, "refs/heads/" + session.Branch));
        }
    }

    [Fact]
    public async Task Rollback_never_deletes_a_pre_existing_branch_even_at_the_same_commit()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var head = repo.Head();
            repo.RunGit("branch", "keep");
            var manager = Manager();
            var handBuilt = new WorktreeSession(repo.Root, manager.ResolveWorktreePath(repo.Root, "keep"), "keep", "keep", head);

            Assert.False(await manager.TryDeleteCreatedBranchAsync(handBuilt));

            Assert.Equal(head, await new GitCli().RevParseAsync(repo.Root, "refs/heads/keep"));
        }
    }

    [Fact]
    public async Task Rollback_refuses_a_created_session_copied_onto_another_branch_name()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var head = repo.Head();
            repo.RunGit("branch", "keep");
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "copied");

            Assert.False(await manager.TryDeleteCreatedBranchAsync(session with { Branch = "keep" }));

            Assert.Equal(head, await new GitCli().RevParseAsync(repo.Root, "refs/heads/keep"));
        }
    }

    [Fact]
    public async Task A_failed_create_on_an_existing_branch_name_keeps_that_branch()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = MultiDirectoryRepository();
        using (sandbox)
        using (repo)
        {
            var head = repo.Head();
            repo.RunGit("branch", "forge/taken");
            var manager = Manager();

            await Assert.ThrowsAnyAsync<Exception>(() => manager.CreateAsync(
                repo.Root, "taken", "forge/taken", "HEAD", sparse: new SparseCheckout(["al*pha"])));

            Assert.Equal(head, await new GitCli().RevParseAsync(repo.Root, "refs/heads/forge/taken"));
            Assert.Single(await manager.ListAsync(repo.Root));
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
