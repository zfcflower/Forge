using Xunit;

namespace Forge.Workspace.Tests.Worktrees;

/// <summary>Pins WorktreeOptions.Layout = SiblingOfRepository: worktrees land in
/// &lt;repoParent&gt;/&lt;repoName&gt;.worktrees/&lt;slug&gt; on branch forge/&lt;slug&gt;.</summary>
public sealed class WorktreeManagerLayoutTests
{
    private static WorktreeManager SiblingManager(WorktreeOptions? options = null) =>
        new(new GitCli(), options ?? new WorktreeOptions { Layout = WorktreeLayout.SiblingOfRepository });

    private static (TempDirectory Sandbox, TestRepo Repo) Repository()
    {
        var sandbox = new TempDirectory();
        var repo = new TestRepo(sandbox);
        repo.CommitFile("README.md", "# repo\n", "initial");
        return (sandbox, repo);
    }

    [Fact]
    public void Sibling_layout_resolves_beside_the_repository()
    {
        var manager = SiblingManager();
        var sandbox = new TempDirectory();
        using (sandbox)
        {
            var repositoryRoot = sandbox.Combine("repo");
            var resolved = manager.ResolveWorktreePath(repositoryRoot, "my-feature");
            var parent = Path.GetDirectoryName(Path.GetFullPath(repositoryRoot))!;
            Assert.Equal(
                Path.GetFullPath(Path.Combine(parent, "repo.worktrees", "my-feature")),
                resolved,
                ignoreCase: OperatingSystem.IsWindows());
        }
    }

    [Fact]
    public async Task Sibling_layout_creates_worktree_beside_the_repo_on_a_forge_branch()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var session = await SiblingManager().CreateAsync(repo.Root, "My Feature");

            var parent = Path.GetDirectoryName(Path.GetFullPath(repo.Root))!;
            Assert.Equal(
                Path.GetFullPath(Path.Combine(parent, Path.GetFileName(repo.Root) + ".worktrees", "my-feature")),
                session.Path,
                ignoreCase: OperatingSystem.IsWindows());
            Assert.Equal("forge/my-feature", session.Branch);
            Assert.Equal("my-feature", session.Slug);
            Assert.True(Directory.Exists(session.Path));
            Assert.Equal("forge/my-feature", await new GitCli().GetCurrentBranchAsync(session.Path));
        }
    }

    [Fact]
    public async Task Sibling_layout_with_explicit_root_directory_nests_repo_folders_there()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var root = sandbox.Combine("explicit-roots");
            var manager = SiblingManager(new WorktreeOptions
            {
                Layout = WorktreeLayout.SiblingOfRepository,
                RootDirectory = root,
            });

            var session = await manager.CreateAsync(repo.Root, "My Feature");

            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, Path.GetFileName(repo.Root) + ".worktrees", "my-feature")),
                session.Path,
                ignoreCase: OperatingSystem.IsWindows());
            Assert.Equal("forge/my-feature", session.Branch);
            Assert.True(Directory.Exists(session.Path));
        }
    }

    [Fact]
    public void Default_layout_stays_nested_by_repository()
    {
        var manager = new WorktreeManager(new GitCli());
        Assert.Equal(WorktreeLayout.NestedByRepository, manager.Options.Layout);

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
}
