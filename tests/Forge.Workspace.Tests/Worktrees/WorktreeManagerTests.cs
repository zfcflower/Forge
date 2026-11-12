using Xunit;

namespace Forge.Workspace.Tests.Worktrees;

public sealed class WorktreeManagerTests
{
    private static WorktreeManager Manager(WorktreeOptions? options = null) => new(new GitCli(), options);

    private static (TempDirectory Sandbox, TestRepo Repo) Repository()
    {
        var sandbox = new TempDirectory();
        var repo = new TestRepo(sandbox);
        repo.CommitFile("README.md", "# repo\n", "initial");
        return (sandbox, repo);
    }

    [Fact]
    public async Task Create_places_worktree_in_default_location_on_a_new_branch()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();

            var session = await manager.CreateAsync(repo.Root, "Fix login flow");

            var expectedPath = Path.Combine(Path.GetDirectoryName(repo.Root)!, ".forge-worktrees", Path.GetFileName(repo.Root), "fix-login-flow");
            Assert.Equal(Path.GetFullPath(expectedPath), session.Path, ignoreCase: OperatingSystem.IsWindows());
            Assert.Equal("forge/fix-login-flow", session.Branch);
            Assert.Equal("fix-login-flow", session.Slug);
            Assert.Equal(repo.Head(), session.BaseCommit);
            Assert.True(Directory.Exists(session.Path));
            Assert.NotNull(await new GitCli().RevParseAsync(repo.Root, "refs/heads/forge/fix-login-flow"));
            Assert.Equal("forge/fix-login-flow", await new GitCli().GetCurrentBranchAsync(session.Path));
        }
    }

    [Fact]
    public async Task Create_gives_unique_slugs_for_repeated_titles()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();

            var first = await manager.CreateAsync(repo.Root, "same title");
            var second = await manager.CreateAsync(repo.Root, "same title");
            var third = await manager.CreateAsync(repo.Root, "same title");

            Assert.Equal(["forge/same-title", "forge/same-title-2", "forge/same-title-3"], new[] { first.Branch, second.Branch, third.Branch });
            Assert.Equal(3, new HashSet<string>(new[] { first.Path, second.Path, third.Path }, StringComparer.OrdinalIgnoreCase).Count);
        }
    }

    [Fact]
    public async Task Create_skips_a_slug_whose_branch_already_exists()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            repo.RunGit("branch", "forge/taken");

            var session = await Manager().CreateAsync(repo.Root, "taken");

            Assert.Equal("forge/taken-2", session.Branch);
            Assert.Equal("taken-2", session.Slug);
        }
    }

    [Fact]
    public async Task Create_honours_custom_branch_prefix_and_absolute_root_directory()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var root = sandbox.Combine("custom-root");
            var manager = Manager(new WorktreeOptions { BranchPrefix = "work/", RootDirectory = root });

            var session = await manager.CreateAsync(repo.Root, "feature");

            Assert.Equal("work/feature", session.Branch);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, Path.GetFileName(repo.Root), "feature")), session.Path, ignoreCase: OperatingSystem.IsWindows());
        }
    }

    [Fact]
    public void Relative_root_directory_resolves_against_the_repository_parent()
    {
        var sandbox = new TempDirectory();
        using (sandbox)
        {
            var repoPath = sandbox.Combine("project");
            var manager = Manager(new WorktreeOptions { RootDirectory = "wt-store" });

            var resolved = manager.ResolveWorktreePath(repoPath, "slot");

            Assert.Equal(Path.GetFullPath(Path.Combine(sandbox.Path, "wt-store", "project", "slot")), resolved, ignoreCase: OperatingSystem.IsWindows());
        }
    }

    [Fact]
    public void Default_location_is_outside_the_repository()
    {
        var sandbox = new TempDirectory();
        using (sandbox)
        {
            var repoPath = sandbox.Combine("project");
            var resolved = Manager().ResolveWorktreePath(repoPath, "slot");

            Assert.Equal(Path.GetFullPath(Path.Combine(sandbox.Path, ".forge-worktrees", "project", "slot")), resolved, ignoreCase: OperatingSystem.IsWindows());
            Assert.False(TestPaths.IsInside(resolved, repoPath));
        }
    }

    [Fact]
    public async Task Create_starts_from_configured_start_point()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var initial = repo.Head();
            repo.CommitFile("later.txt", "later\n", "later");
            repo.RunGit("tag", "v-start", initial);

            var session = await Manager(new WorktreeOptions { StartPoint = "v-start" }).CreateAsync(repo.Root, "from tag");

            Assert.Equal(initial, session.BaseCommit);
            Assert.Equal(initial, await new GitCli().RevParseAsync(session.Path, "HEAD"));
        }
    }

    [Fact]
    public async Task Create_with_unresolvable_start_point_throws_before_touching_disk()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager(new WorktreeOptions { StartPoint = "no-such-commit" });

            await Assert.ThrowsAsync<GitException>(() => manager.CreateAsync(repo.Root, "nothing"));

            Assert.Single(await new GitCli().ListWorktreesAsync(repo.Root));
        }
    }

    [Fact]
    public async Task Create_outside_a_repository_throws()
    {
        if (!GitProbe.IsAvailable) return;
        using var sandbox = new TempDirectory();

        await Assert.ThrowsAsync<GitException>(() => Manager().CreateAsync(sandbox.Path, "nowhere"));
    }

    [Fact]
    public async Task Remove_refuses_worktree_with_uncommitted_changes()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "dirty");
            File.WriteAllText(Path.Combine(session.Path, "scratch.txt"), "unsaved\n");

            var refusal = await Assert.ThrowsAsync<WorktreeRemovalRefusedException>(() => manager.RemoveAsync(repo.Root, session.Path));

            Assert.Contains(refusal.Reasons, static reason => reason.Contains("uncommitted", StringComparison.Ordinal));
            Assert.True(Directory.Exists(session.Path));
        }
    }

    [Fact]
    public async Task Remove_refuses_worktree_whose_commits_are_not_merged()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "unmerged");
            File.WriteAllText(Path.Combine(session.Path, "work.txt"), "work\n");
            await new GitCli().CommitAsync(session.Path, [], "work in progress");

            var refusal = await Assert.ThrowsAsync<WorktreeRemovalRefusedException>(() => manager.RemoveAsync(repo.Root, session.Path));

            Assert.Single(refusal.Reasons);
            Assert.Contains("not merged into 'main'", refusal.Reasons[0], StringComparison.Ordinal);
            Assert.True(Directory.Exists(session.Path));
        }
    }

    [Fact]
    public async Task Check_commits_from_the_repository_answers_like_the_worktree_check_when_git_cannot_run_inside_it()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var onBranch = await manager.CreateAsync(repo.Root, "on-branch");
            File.WriteAllText(Path.Combine(onBranch.Path, "work.txt"), "work\n");
            await new GitCli().CommitAsync(onBranch.Path, [], "work in progress");
            var detached = await manager.CreateAsync(repo.Root, "detached");
            GitFixture.RunChecked(detached.Path, "checkout", "-q", "--detach");
            File.WriteAllText(Path.Combine(detached.Path, "loose.txt"), "loose\n");
            GitFixture.RunChecked(detached.Path, "add", "-A");
            GitFixture.RunChecked(detached.Path, "commit", "-q", "-m", "loose commit");
            var expectedOnBranch = await manager.CheckRemovableAsync(repo.Root, onBranch.Path);
            var expectedDetached = await manager.CheckRemovableAsync(repo.Root, detached.Path);
            Assert.Equal(2, expectedDetached.Count);

            var list = await manager.ListAsync(repo.Root);
            var onBranchEntry = list.Single(w => Path.GetFileName(w.Path) == Path.GetFileName(onBranch.Path));
            var detachedEntry = list.Single(w => Path.GetFileName(w.Path) == Path.GetFileName(detached.Path));

            // A broken .git link: git cannot run inside either worktree any more, the commits are still in the repository.
            // (git hides the .git file on Windows, which a plain overwrite refuses: delete it first.)
            foreach (var worktree in new[] { onBranch.Path, detached.Path })
            {
                File.Delete(Path.Combine(worktree, ".git"));
                File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + Path.Combine(sandbox.Path, "nowhere") + "\n");
            }
            await Assert.ThrowsAsync<GitException>(() => manager.CheckRemovableAsync(repo.Root, onBranch.Path));

            Assert.Equal(
                expectedOnBranch,
                await manager.CheckCommitsAsync(repo.Root, onBranchEntry.HeadCommit!, onBranchEntry.IsDetached));
            Assert.Equal(
                expectedDetached,
                await manager.CheckCommitsAsync(repo.Root, detachedEntry.HeadCommit!, detachedEntry.IsDetached));
            Assert.Equal(
                ["1 commit not merged into 'main'"],
                await manager.CheckCommitsAsync(repo.Root, onBranchEntry.HeadCommit!, false, "main"));
            Assert.Empty(await manager.CheckCommitsAsync(repo.Root, repo.Head(), false));
        }
    }

    [Fact]
    public async Task Remove_accepts_worktree_whose_commits_are_merged_into_the_given_base()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "merged");
            File.WriteAllText(Path.Combine(session.Path, "work.txt"), "work\n");
            await new GitCli().CommitAsync(session.Path, [], "work done");

            await manager.RemoveAsync(repo.Root, session.Path, baseBranch: session.Branch);

            Assert.False(Directory.Exists(session.Path));
        }
    }

    [Fact]
    public async Task Remove_of_clean_session_deletes_directory_and_keeps_branch()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "clean");

            await manager.RemoveAsync(repo.Root, session.Path);

            Assert.False(Directory.Exists(session.Path));
            Assert.DoesNotContain(await manager.ListAsync(repo.Root), worktree => TestPaths.Same(worktree.Path, session.Path));
            Assert.NotNull(await new GitCli().RevParseAsync(repo.Root, "refs/heads/forge/clean"));
        }
    }

    [Fact]
    public async Task Forced_remove_discards_dirty_and_unmerged_work_but_not_the_branch()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "forced");
            File.WriteAllText(Path.Combine(session.Path, "lost.txt"), "gone soon\n");
            await new GitCli().CommitAsync(session.Path, [], "committed then dirty");
            File.WriteAllText(Path.Combine(session.Path, "dirty.txt"), "dirty\n");

            await manager.RemoveAsync(repo.Root, session.Path, force: true);

            Assert.False(Directory.Exists(session.Path));
            Assert.NotNull(await new GitCli().RevParseAsync(repo.Root, "refs/heads/forge/forced"));
        }
    }

    [Fact]
    public async Task Remove_resolves_relative_path_against_repository()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager(new WorktreeOptions { RootDirectory = sandbox.Combine("wts") });
            var session = await manager.CreateAsync(repo.Root, "relative");
            var relative = Path.GetRelativePath(repo.Root, session.Path);

            await manager.RemoveAsync(repo.Root, relative);

            Assert.False(Directory.Exists(session.Path));
        }
    }

    [Fact]
    public async Task Prune_drops_registrations_of_worktrees_deleted_by_hand()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "vanished");
            Directory.Delete(session.Path, recursive: true);
            Assert.Contains(await manager.ListAsync(repo.Root), worktree => worktree.IsPrunable);

            await manager.PruneAsync(repo.Root);

            Assert.DoesNotContain(await manager.ListAsync(repo.Root), worktree => TestPaths.Same(worktree.Path, session.Path));
            Assert.NotNull(await new GitCli().RevParseAsync(repo.Root, "refs/heads/forge/vanished"));
        }
    }

    [Fact]
    public async Task List_reports_the_main_worktree_and_created_sessions()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "listed");

            var listed = await manager.ListAsync(repo.Root);

            Assert.Contains(listed, worktree => TestPaths.Same(worktree.Path, repo.Root));
            Assert.Contains(listed, worktree => TestPaths.Same(worktree.Path, session.Path) && worktree.Branch == "forge/listed");
        }
    }

    [Fact]
    public void Slugify_reduces_titles_to_safe_ascii_and_bounds_length()
    {
        Assert.Equal("fix-the-login-flow", WorktreeManager.Slugify("Fix: the Login  flow!"));
        Assert.Equal("r-sum", WorktreeManager.Slugify("Résumé"));
        Assert.Equal("x", WorktreeManager.Slugify("__x__"));
        Assert.Equal("session", WorktreeManager.Slugify(string.Empty));
        Assert.Equal("session", WorktreeManager.Slugify("中文"));
        Assert.Equal("session", WorktreeManager.Slugify(null!));
        Assert.Equal("aaaaabcdef", WorktreeManager.Slugify(new string('a', 5) + "bcdefghij" + "klmnop", 10));
        Assert.True(WorktreeManager.Slugify(new string('z', 200)).Length <= 48);
    }

    [Fact]
    public void Slugify_rejects_non_positive_max_length()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorktreeManager.Slugify("anything", 0));
    }

    [Fact]
    public void Constructor_rejects_null_git()
    {
        Assert.Throws<ArgumentNullException>(() => new WorktreeManager(null!));
    }

    [Fact]
    public void Options_default_to_forge_prefix_and_bounded_attempts()
    {
        var options = new WorktreeOptions();

        Assert.Equal("forge/", options.BranchPrefix);
        Assert.Equal(48, options.MaxSlugLength);
        Assert.Equal(1000, options.MaxSlugAttempts);
        Assert.Null(options.RootDirectory);
        Assert.Null(options.StartPoint);
    }
}
