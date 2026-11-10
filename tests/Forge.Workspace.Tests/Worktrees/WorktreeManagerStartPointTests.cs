using Forge.Workspace.Tests.Git;
using Xunit;

namespace Forge.Workspace.Tests.Worktrees;

/// <summary>
/// Covers <see cref="WorktreeManager.CreateAsync(string, string, string?, string, CancellationToken)"/> —
/// the overload that starts the new branch at an explicit start point (a local branch, a
/// remote-tracking ref or any revision) instead of at HEAD. The start point is resolved to its
/// commit and the worktree is added from that commit, so the new branch never picks up an
/// upstream from a remote-tracking ref it started at. Name handling matches the HEAD-starting
/// overload: an invalid name throws <see cref="ArgumentException"/>, an existing branch throws
/// <see cref="GitException"/> naming it, a blank name falls back to the title overload, and the
/// directory slug comes from the part after the last <c>/</c>.
/// </summary>
public sealed class WorktreeManagerStartPointTests
{
    private static (TempDirectory Sandbox, TestRepo Repo) Repository()
    {
        var sandbox = new TempDirectory();
        var repo = new TestRepo(sandbox);
        repo.Write("README.md", "# repo\n");
        repo.Commit("initial");
        return (sandbox, repo);
    }

    private static IReadOnlyList<string> BranchLines(TestRepo repo) =>
        repo.RunGit("branch", "--list", "--format=%(refname:short)")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .ToList();

    private static IReadOnlyList<string> WorktreeDirs(string repositoryRoot)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var container = Path.Combine(parent!, ".forge-worktrees", Path.GetFileName(repositoryRoot));
        if (!Directory.Exists(container))
        {
            return [];
        }

        return Directory.GetDirectories(container).OrderBy(static d => d, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task Start_point_at_another_local_branch_starts_there_instead_of_head()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            repo.Write("base.txt", "base\n");
            var baseCommit = repo.Commit("base");
            repo.RunGit("branch", "base/branch");
            repo.Write("later.txt", "later\n");
            var head = repo.Commit("later");
            Assert.NotEqual(baseCommit, head);
            var manager = new WorktreeManager(new GitCli());

            var session = await manager.CreateAsync(repo.Root, "From base", "feature/from-base", "base/branch");

            Assert.Equal("feature/from-base", session.Branch);
            Assert.Equal(baseCommit, session.BaseCommit);
            var sessionHead = GitFixture.RunChecked(session.Path, "rev-parse", "HEAD").Trim();
            Assert.Equal(baseCommit, sessionHead);
            Assert.NotEqual(head, sessionHead);
            Assert.Contains("feature/from-base", BranchLines(repo), StringComparer.Ordinal);
            Assert.Equal("feature/from-base", GitFixture.RunChecked(session.Path, "branch", "--show-current").Trim());
        }
    }

    [Fact]
    public async Task Start_point_at_a_remote_tracking_ref_starts_there_with_no_upstream()
    {
        if (!GitProbe.IsAvailable) return;
        using var sandbox = new RemoteSandbox();
        var repo = sandbox.Clone("alice");
        sandbox.CommitFile(repo, "base.txt", "base\n", "seed");
        IsolatedGit.RunChecked(repo, "push", "-q", "-u", "origin", "main");
        var remoteTip = IsolatedGit.RunChecked(repo, "rev-parse", "origin/main").Trim();
        sandbox.CommitFile(repo, "local.txt", "local\n", "local work");
        var localHead = IsolatedGit.Head(repo);
        Assert.NotEqual(remoteTip, localHead);

        var session = await new WorktreeManager(new GitCli()).CreateAsync(repo, "Remote start", "feature/from-remote", "origin/main");

        Assert.Equal("feature/from-remote", session.Branch);
        Assert.Equal(remoteTip, session.BaseCommit);
        var sessionHead = GitFixture.RunChecked(session.Path, "rev-parse", "HEAD").Trim();
        Assert.Equal(remoteTip, sessionHead);
        Assert.NotEqual(localHead, sessionHead);

        // The point of resolving the start point to a commit: the new branch must not
        // track origin/main just because it started there.
        var upstream = GitFixture.RunChecked(repo, "for-each-ref", "--format=%(upstream:short)", "refs/heads/feature/from-remote");
        Assert.True(string.IsNullOrWhiteSpace(upstream), $"expected no upstream but got '{upstream}'");
        var (exitCode, _, _) = GitFixture.Run(repo, "rev-parse", "--abbrev-ref", "feature/from-remote@{u}");
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task Start_point_at_a_commit_sha_starts_there()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            repo.Write("first.txt", "first\n");
            var first = repo.Commit("first");
            repo.Write("second.txt", "second\n");
            repo.Commit("second");

            var session = await new WorktreeManager(new GitCli()).CreateAsync(repo.Root, "At sha", "feature/at-sha", first);

            Assert.Equal("feature/at-sha", session.Branch);
            Assert.Equal(first, session.BaseCommit);
            Assert.Equal(first, GitFixture.RunChecked(session.Path, "rev-parse", "HEAD").Trim());
            Assert.Equal("feature/at-sha", GitFixture.RunChecked(session.Path, "branch", "--show-current").Trim());
        }
    }

    [Fact]
    public async Task Start_point_at_an_annotated_tag_reports_the_commit_it_tags()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            repo.Write("tagged.txt", "tagged\n");
            var tagged = repo.Commit("tagged");
            repo.RunGit("tag", "-a", "v1", "-m", "release");
            repo.Write("later.txt", "later\n");
            repo.Commit("later");

            var session = await new WorktreeManager(new GitCli()).CreateAsync(repo.Root, "At tag", "feature/at-tag", "v1");

            // An annotated tag resolves to its own tag object without `^{commit}`: the worktree would still land on the
            // tagged commit, but the recorded base commit would be the tag object instead of a commit.
            Assert.Equal(tagged, session.BaseCommit);
            Assert.Equal(tagged, GitFixture.RunChecked(session.Path, "rev-parse", "HEAD").Trim());
        }
    }

    [Fact]
    public async Task Unresolvable_start_point_throws_and_leaves_no_worktree_behind()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var exception = await Assert.ThrowsAsync<GitException>(
                () => new WorktreeManager(new GitCli()).CreateAsync(repo.Root, "Nope", "feature/never", "no-such-ref-xyz"));

            Assert.Contains("no-such-ref-xyz", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("feature/never", BranchLines(repo), StringComparer.Ordinal);
            Assert.Equal([], WorktreeDirs(repo.Root));
            Assert.Single(await new GitCli().ListWorktreesAsync(repo.Root));
        }
    }

    [Fact]
    public async Task Existing_branch_name_throws_naming_it_and_leaves_no_directory()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            repo.RunGit("branch", "taken");
            var before = WorktreeDirs(repo.Root);

            var exception = await Assert.ThrowsAsync<GitException>(
                () => new WorktreeManager(new GitCli()).CreateAsync(repo.Root, "Title", "taken", "main"));

            Assert.Contains("taken", exception.Message, StringComparison.Ordinal);
            Assert.Equal(before, WorktreeDirs(repo.Root));
        }
    }

    [Fact]
    public async Task Invalid_branch_name_is_rejected_before_touching_anything()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var before = WorktreeDirs(repo.Root);

            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => new WorktreeManager(new GitCli()).CreateAsync(repo.Root, "Title", "bad..name", "main"));

            Assert.Equal("branchName", exception.ParamName);
            Assert.DoesNotContain("bad..name", BranchLines(repo), StringComparer.Ordinal);
            Assert.Equal(before, WorktreeDirs(repo.Root));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_branch_name_falls_back_to_the_title_overload_ignoring_the_start_point(string? branchName)
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            repo.Write("old.txt", "old\n");
            var old = repo.Commit("old");
            repo.RunGit("tag", "v-old", old);
            repo.Write("new.txt", "new\n");
            var head = repo.Commit("new");
            Assert.NotEqual(old, head);

            var session = await new WorktreeManager(new GitCli()).CreateAsync(repo.Root, "My Title Here", branchName, "v-old");

            Assert.StartsWith("forge/", session.Branch, StringComparison.Ordinal);
            Assert.NotEqual("v-old", session.Branch);
            Assert.Equal(head, session.BaseCommit);
            Assert.True(Directory.Exists(session.Path));
        }
    }

    [Fact]
    public async Task Directory_slug_comes_from_the_last_segment_of_the_branch_name()
    {
        if (!GitProbe.IsAvailable) return;
        var (sandbox, repo) = Repository();
        using (sandbox)
        using (repo)
        {
            var session = await new WorktreeManager(new GitCli()).CreateAsync(repo.Root, "Title", "team/alpha/tool", "main");

            Assert.Equal("team/alpha/tool", session.Branch);
            Assert.Equal("tool", session.Slug);
            Assert.Equal("tool", Path.GetFileName(session.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            Assert.True(Directory.Exists(session.Path));
        }
    }
}
