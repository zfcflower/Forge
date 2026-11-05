using Xunit;

namespace Forge.Workspace.Tests.Worktrees;

public sealed class WorktreeManagerPinTests
{
    private static WorktreeManager Manager(WorktreeOptions? options = null) => new(new GitCli(), options);

    private static (TempDirectory Sandbox, TestRepo Repo) Repository()
    {
        var sandbox = new TempDirectory();
        var repo = new TestRepo(sandbox);
        repo.Write("README.md", "# repo\n");
        repo.Commit("initial");
        return (sandbox, repo);
    }

    [Fact]
    public void Default_options_keep_the_nested_repository_layout()
    {
        var manager = Manager();
        Assert.Equal("forge/", manager.Options.BranchPrefix);

        var sandbox = new TempDirectory();
        using (sandbox)
        {
            var resolved = manager.ResolveWorktreePath(sandbox.Combine("repo"), "slot");
            var parent = Path.GetDirectoryName(Path.GetFullPath(sandbox.Combine("repo")))!;
            Assert.Equal(
                Path.GetFullPath(Path.Combine(parent, ".forge-worktrees", "repo", "slot")),
                resolved,
                ignoreCase: OperatingSystem.IsWindows());
        }
    }

    [Fact]
    public async Task Create_with_explicit_absolute_root_directory_places_branch_there()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var root = sandbox.Combine("explicit-root");
            var manager = Manager(new WorktreeOptions { RootDirectory = root, BranchPrefix = "forge/" });

            var session = await manager.CreateAsync(repo.Root, "My feature");

            Assert.Equal(Path.GetFullPath(Path.Combine(root, Path.GetFileName(repo.Root), "my-feature")), session.Path);
            Assert.Equal("forge/my-feature", session.Branch);
            Assert.True(Directory.Exists(session.Path));
            Assert.Equal("forge/my-feature", await new GitCli().GetCurrentBranchAsync(session.Path));
        }
    }

    [Fact]
    public async Task Create_defaults_stay_nested_beside_nothing_new()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var session = await Manager().CreateAsync(repo.Root, "nested check");

            var parent = Path.GetDirectoryName(repo.Root)!;
            Assert.Equal(
                Path.GetFullPath(Path.Combine(parent, ".forge-worktrees", Path.GetFileName(repo.Root), "nested-check")),
                session.Path,
                ignoreCase: OperatingSystem.IsWindows());
        }
    }

    [Fact]
    public void Slugify_handles_edges()
    {
        Assert.Equal("session", WorktreeManager.Slugify(string.Empty));
        Assert.Equal("session", WorktreeManager.Slugify("!!!"));
        Assert.Equal("hello-world", WorktreeManager.Slugify("Hello, World!"));
        Assert.Equal("a-b", WorktreeManager.Slugify("  A   B  "));
        Assert.True(WorktreeManager.Slugify(new string('x', 200)).Length <= 48);
        Assert.Equal("caf", WorktreeManager.Slugify("Caf\u00e9", 3));
    }

    [Fact]
    public async Task Create_in_a_non_repo_throws_GitException()
    {
        if (!GitProbe.IsAvailable) return;
        using var directory = new TempDirectory();

        await Assert.ThrowsAsync<GitException>(() => Manager().CreateAsync(directory.Path, "title"));
    }

    [Fact]
    public async Task Remove_refuses_dirty_worktrees_and_force_removes_them()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var session = await manager.CreateAsync(repo.Root, "dirty work");
            File.WriteAllText(Path.Combine(session.Path, "dirty.txt"), "dirty\n");

            var refused = await Assert.ThrowsAsync<WorktreeRemovalRefusedException>(() =>
                manager.RemoveAsync(repo.Root, session.Path));
            Assert.Contains("uncommitted", refused.Message, StringComparison.OrdinalIgnoreCase);

            await manager.RemoveAsync(repo.Root, session.Path, force: true);
            Assert.False(Directory.Exists(session.Path));
        }
    }

    [Fact]
    public async Task Concurrent_creates_get_unique_branches_and_paths()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager();
            var sessions = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => manager.CreateAsync(repo.Root, $"parallel title {i}")))
                .WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(4, sessions.Select(s => s.Branch).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(4, sessions.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(sessions, s => Assert.StartsWith("forge/", s.Branch, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Concurrent_same_title_creates_race_documented()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var sessions = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Manager().CreateAsync(repo.Root, "same title")))
                .WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(4, sessions.Select(s => s.Branch).Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public async Task Sibling_of_repository_layout_places_worktrees_beside_the_repo()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var manager = Manager(new WorktreeOptions { Layout = WorktreeLayout.SiblingOfRepository });
            var session = await manager.CreateAsync(repo.Root, "sibling check");

            var parent = Path.GetDirectoryName(Path.GetFullPath(repo.Root))!;
            Assert.Equal(
                Path.GetFullPath(Path.Combine(parent, Path.GetFileName(repo.Root) + ".worktrees", "sibling-check")),
                session.Path,
                ignoreCase: OperatingSystem.IsWindows());
            Assert.Equal("forge/sibling-check", session.Branch);
            Assert.True(Directory.Exists(session.Path));
        }
    }
}
