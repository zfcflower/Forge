using System.Text;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// The <see cref="GitCli"/> plumbing the screenshot step is built on (<c>GitCli.Objects.cs</c>): blobs, trees and parentless
/// commits that no branch points to, a lease-protected push of a bare commit, a detached worktree, and the path-limited
/// helpers used by the cleanup. Everything runs against real repositories.
/// </summary>
public sealed class ScreenshotGitObjectsTests
{
    private const string AssetsRef = "refs/heads/assets/forge-feature";

    private static async Task<string> OrphanAsync(ScreenshotFixture fixture, string tag, string name = "a.png")
    {
        var file = Path.Combine(fixture.Markers, tag + ".png");
        File.WriteAllBytes(file, ScreenshotTestData.Png(tag));
        var blob = await fixture.Git.HashObjectAsync(fixture.Work, file);
        var tree = await fixture.Git.MakeTreeAsync(fixture.Work, [new GitTreeEntry(name, blob)]);
        return await fixture.Git.CommitTreeAsync(fixture.Work, tree, "assets " + tag);
    }

    [Fact]
    public async Task HashObject_stores_the_bytes_exactly_without_converting_line_endings()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        File.WriteAllText(Path.Combine(fixture.Work, ".gitattributes"), "* text eol=crlf\n*.png text eol=crlf\n");
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x0A, 0x0D, 0x0A, 0xFF];
        var file = Path.Combine(fixture.Markers, "binary.png");
        File.WriteAllBytes(file, bytes);

        var id = await fixture.Git.HashObjectAsync(fixture.Work, file);

        Assert.Equal(40, id.Length);
        Assert.Equal(id, IsolatedGit.RunChecked(fixture.Work, "hash-object", "--no-filters", "--", file).Trim());
        Assert.Equal("blob", IsolatedGit.RunChecked(fixture.Work, "cat-file", "-t", id).Trim());
        Assert.Equal(bytes.Length.ToString(), IsolatedGit.RunChecked(fixture.Work, "cat-file", "-s", id).Trim());
    }

    [Fact]
    public async Task HashObject_reads_a_file_outside_the_repository_and_one_whose_name_looks_like_an_option()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        File.WriteAllBytes(Path.Combine(fixture.Work, "-weird.png"), ScreenshotTestData.Png("weird"));

        var id = await fixture.Git.HashObjectAsync(fixture.Work, "-weird.png");

        Assert.Equal("blob", IsolatedGit.RunChecked(fixture.Work, "cat-file", "-t", id).Trim());
    }

    [Fact]
    public async Task HashObject_fails_for_a_missing_file_and_rejects_a_blank_path()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();

        await Assert.ThrowsAsync<GitException>(() => fixture.Git.HashObjectAsync(fixture.Work, Path.Combine(fixture.Markers, "missing.png")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Git.HashObjectAsync(fixture.Work, " "));
    }

    [Fact]
    public async Task MakeTree_writes_a_flat_tree_of_regular_files()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var first = await fixture.Git.HashObjectAsync(fixture.Work, WritePng(fixture, "one"));
        var second = await fixture.Git.HashObjectAsync(fixture.Work, WritePng(fixture, "two"));

        var tree = await fixture.Git.MakeTreeAsync(fixture.Work, [new GitTreeEntry("home-before.png", second), new GitTreeEntry("home-after.png", first)]);

        var listing = IsolatedGit.RunChecked(fixture.Work, "ls-tree", tree);
        Assert.Equal(
            $"100644 blob {first}\thome-after.png\n100644 blob {second}\thome-before.png\n",
            listing);
    }

    [Fact]
    public async Task MakeTree_of_nothing_is_the_empty_tree()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();

        var tree = await fixture.Git.MakeTreeAsync(fixture.Work, []);

        Assert.Equal("4b825dc642cb6eb9a060e54bf8d69288fbee4904", tree);
    }

    [Theory]
    [InlineData("a/b.png")]
    [InlineData("a\\b.png")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(".git")]
    [InlineData("")]
    [InlineData("tab\tname.png")]
    [InlineData("new\nline.png")]
    [InlineData("nul\0name.png")]
    public async Task MakeTree_refuses_names_that_are_not_plain_file_names(string name)
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var blob = await fixture.Git.HashObjectAsync(fixture.Work, WritePng(fixture, "one"));

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Git.MakeTreeAsync(fixture.Work, [new GitTreeEntry(name, blob)]));
    }

    [Fact]
    public async Task MakeTree_refuses_duplicate_names_and_ids_that_are_not_objects()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var blob = await fixture.Git.HashObjectAsync(fixture.Work, WritePng(fixture, "one"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Git.MakeTreeAsync(fixture.Work, [new GitTreeEntry("a.png", blob), new GitTreeEntry("a.png", blob)]));
        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Git.MakeTreeAsync(fixture.Work, [new GitTreeEntry("a.png", "not-an-object-id")]));
        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Git.MakeTreeAsync(fixture.Work, [new GitTreeEntry("a.png", "--help")]));
    }

    [Fact]
    public async Task MakeTree_fails_with_a_git_error_for_an_object_that_does_not_exist()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();

        await Assert.ThrowsAsync<GitException>(
            () => fixture.Git.MakeTreeAsync(fixture.Work, [new GitTreeEntry("a.png", new string('1', 40))]));
    }

    [Fact]
    public async Task CommitTree_makes_a_parentless_commit_and_moves_no_ref()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var refsBefore = IsolatedGit.RunChecked(fixture.Work, "for-each-ref", "--format=%(refname) %(objectname)");
        var headBefore = IsolatedGit.Head(fixture.Work);
        var tree = await fixture.Git.MakeTreeAsync(fixture.Work, []);

        var commit = await fixture.Git.CommitTreeAsync(fixture.Work, tree, "Screenshots for forge/feature\n\n- home");

        var raw = IsolatedGit.RunChecked(fixture.Work, "cat-file", "-p", commit);
        Assert.DoesNotContain("\nparent ", "\n" + raw, StringComparison.Ordinal);
        Assert.StartsWith("tree " + tree, raw, StringComparison.Ordinal);
        Assert.Equal("Screenshots for forge/feature\n\n- home\n", IsolatedGit.RunChecked(fixture.Work, "log", "-1", "--format=%B", commit).TrimEnd('\n') + "\n");
        Assert.Equal(refsBefore, IsolatedGit.RunChecked(fixture.Work, "for-each-ref", "--format=%(refname) %(objectname)"));
        Assert.Equal(headBefore, IsolatedGit.Head(fixture.Work));
    }

    [Fact]
    public async Task CommitTree_with_a_parent_records_it()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var tree = await fixture.Git.MakeTreeAsync(fixture.Work, []);
        var parent = IsolatedGit.Head(fixture.Work);

        var commit = await fixture.Git.CommitTreeAsync(fixture.Work, tree, "child", parent);

        Assert.Equal(parent, IsolatedGit.RunChecked(fixture.Work, "rev-parse", commit + "^").Trim());
    }

    [Fact]
    public async Task CommitTree_falls_back_to_a_neutral_identity_when_git_has_none()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        IsolatedGit.RunChecked(fixture.Work, "config", "--local", "user.name", string.Empty);
        IsolatedGit.RunChecked(fixture.Work, "config", "--local", "user.email", string.Empty);
        var tree = await fixture.Git.MakeTreeAsync(fixture.Work, []);

        var commit = await fixture.Git.CommitTreeAsync(fixture.Work, tree, "no identity here");

        Assert.Equal(
            "Forge <forge@localhost> Forge <forge@localhost>",
            IsolatedGit.RunChecked(fixture.Work, "log", "-1", "--format=%an <%ae> %cn <%ce>", commit).Trim());
    }

    [Fact]
    public async Task CommitTree_rejects_a_blank_message_and_an_option_instead_of_a_tree()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var tree = await fixture.Git.MakeTreeAsync(fixture.Work, []);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Git.CommitTreeAsync(fixture.Work, tree, "  "));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Git.CommitTreeAsync(fixture.Work, "--help", "message"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Git.CommitTreeAsync(fixture.Work, tree, "message", "--help"));
    }

    [Fact]
    public async Task PushCommit_creates_a_ref_on_the_remote_without_creating_one_locally()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var commit = await OrphanAsync(fixture, "first");
        var branchesBefore = IsolatedGit.BranchList(fixture.Work);

        var result = await fixture.Git.PushCommitAsync(fixture.Work, "origin", commit, AssetsRef, string.Empty);

        Assert.True(result.Success, result.Message);
        Assert.Equal(commit, fixture.OriginTip("assets/forge-feature"));
        Assert.Equal(branchesBefore, IsolatedGit.BranchList(fixture.Work));
        Assert.Null(fixture.OriginTip("forge/feature"));
    }

    [Fact]
    public async Task PushCommit_replaces_the_ref_when_the_lease_matches_even_though_the_history_is_unrelated()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var first = await OrphanAsync(fixture, "first");
        Assert.True((await fixture.Git.PushCommitAsync(fixture.Work, "origin", first, AssetsRef, string.Empty)).Success);
        var second = await OrphanAsync(fixture, "second");

        var result = await fixture.Git.PushCommitAsync(fixture.Work, "origin", second, AssetsRef, first);

        Assert.True(result.Success, result.Message);
        Assert.Equal(second, fixture.OriginTip("assets/forge-feature"));
    }

    [Fact]
    public async Task PushCommit_refuses_to_overwrite_a_ref_that_moved_since_it_was_read()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var first = await OrphanAsync(fixture, "first");
        var intruder = await OrphanAsync(fixture, "intruder");
        Assert.True((await fixture.Git.PushCommitAsync(fixture.Work, "origin", first, AssetsRef, string.Empty)).Success);
        Assert.True((await fixture.Git.PushCommitAsync(fixture.Work, "origin", intruder, AssetsRef, first)).Success);
        var mine = await OrphanAsync(fixture, "mine");

        var stale = await fixture.Git.PushCommitAsync(fixture.Work, "origin", mine, AssetsRef, first);

        Assert.False(stale.Success);
        Assert.Equal(GitRemoteFailureKind.NonFastForward, stale.Failure);
        Assert.Equal(intruder, fixture.OriginTip("assets/forge-feature"));
    }

    [Fact]
    public async Task PushCommit_with_an_empty_lease_refuses_when_the_ref_already_exists()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var first = await OrphanAsync(fixture, "first");
        var second = await OrphanAsync(fixture, "second");
        Assert.True((await fixture.Git.PushCommitAsync(fixture.Work, "origin", first, AssetsRef, string.Empty)).Success);

        var result = await fixture.Git.PushCommitAsync(fixture.Work, "origin", second, AssetsRef, string.Empty);

        Assert.False(result.Success);
        Assert.Equal(first, fixture.OriginTip("assets/forge-feature"));
    }

    [Fact]
    public async Task PushCommit_pushes_nothing_but_the_one_ref()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var commit = await OrphanAsync(fixture, "first");

        await fixture.Git.PushCommitAsync(fixture.Work, "origin", commit, AssetsRef, string.Empty);

        var refs = IsolatedGit.RunChecked(fixture.Origin, "for-each-ref", "--format=%(refname)")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["refs/heads/assets/forge-feature", "refs/heads/main"], refs.Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("not-a-hash", "refs/heads/assets/x", "")]
    [InlineData("abc1234", "refs/heads/assets/x", "")]
    [InlineData("<commit>", "refs/tags/assets-x", "")]
    [InlineData("<commit>", "assets/x", "")]
    [InlineData("<commit>", "refs/heads/", "")]
    [InlineData("<commit>", "refs/heads/a..b", "")]
    [InlineData("<commit>", "refs/heads/a b", "")]
    [InlineData("<commit>", "refs/heads/assets/x", "not-a-hash")]
    public async Task PushCommit_validates_its_input_before_talking_to_the_remote(string commit, string destination, string expected)
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var real = await OrphanAsync(fixture, "first");

        var result = await fixture.Git.PushCommitAsync(fixture.Work, "origin", commit == "<commit>" ? real : commit, destination, expected);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Message);
        Assert.Null(fixture.OriginTip("assets/x"));
        Assert.Null(fixture.OriginTip("assets-x"));
    }

    [Fact]
    public async Task PushCommit_rejects_a_remote_name_that_looks_like_an_option()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var commit = await OrphanAsync(fixture, "first");

        var result = await fixture.Git.PushCommitAsync(fixture.Work, "--receive-pack=evil", commit, AssetsRef, string.Empty);

        Assert.False(result.Success);
        Assert.Null(fixture.OriginTip("assets/forge-feature"));
    }

    [Fact]
    public async Task PushCommit_reports_a_rejection_by_the_remote_and_pushes_nothing()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.RejectPushes();
        var commit = await OrphanAsync(fixture, "first");

        var result = await fixture.Git.PushCommitAsync(fixture.Work, "origin", commit, AssetsRef, string.Empty);

        Assert.False(result.Success);
        Assert.NotEqual(GitRemoteFailureKind.None, result.Failure);
        Assert.Null(fixture.OriginTip("assets/forge-feature"));
    }

    [Fact]
    public async Task AddDetachedWorktree_checks_a_commit_out_without_touching_any_branch()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var main = IsolatedGit.RunChecked(fixture.Work, "rev-parse", "origin/main").Trim();
        var branchesBefore = IsolatedGit.BranchList(fixture.Work);
        var path = Path.Combine(fixture.Markers, "base-checkout");

        await fixture.Git.AddDetachedWorktreeAsync(fixture.Work, path, main);

        try
        {
            Assert.Equal(main, IsolatedGit.Head(path));
            Assert.Equal(string.Empty, IsolatedGit.CurrentBranch(path));
            Assert.True(File.Exists(Path.Combine(path, "removed.png")), "the base still has the image the branch deleted");
            Assert.Equal(branchesBefore, IsolatedGit.BranchList(fixture.Work));
            Assert.Equal(2, fixture.Worktrees().Count);
        }
        finally
        {
            await fixture.Git.RemoveWorktreeAsync(fixture.Work, path, force: true, CancellationToken.None);
            await fixture.Git.PruneWorktreesAsync(fixture.Work, CancellationToken.None);
        }

        Assert.False(Directory.Exists(path));
        fixture.AssertNoWorktree();
    }

    [Fact]
    public async Task AddDetachedWorktree_does_not_read_a_path_that_looks_like_an_option_as_one()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var main = IsolatedGit.RunChecked(fixture.Work, "rev-parse", "origin/main").Trim();

        await fixture.Git.AddDetachedWorktreeAsync(fixture.Work, "-detached-checkout", main);

        try
        {
            Assert.True(Directory.Exists(Path.Combine(fixture.Work, "-detached-checkout")));
        }
        finally
        {
            await fixture.Git.RemoveWorktreeAsync(fixture.Work, "-detached-checkout", force: true, CancellationToken.None);
            await fixture.Git.PruneWorktreesAsync(fixture.Work, CancellationToken.None);
        }
    }

    [Fact]
    public async Task AddDetachedWorktree_fails_for_a_commit_that_does_not_exist_and_rejects_an_option_as_commit()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var path = Path.Combine(fixture.Markers, "never");

        await Assert.ThrowsAsync<GitException>(() => fixture.Git.AddDetachedWorktreeAsync(fixture.Work, path, new string('1', 40)));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Git.AddDetachedWorktreeAsync(fixture.Work, path, "--force"));
        Assert.False(Directory.Exists(path));
        fixture.AssertNoWorktree();
    }

    [Fact]
    public async Task ListTrackedFiles_matches_paths_literally_and_returns_forward_slash_paths_from_the_index()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Write("tools/a[1].txt", "bracket");
        fixture.Write("tools/a1.txt", "plain");
        fixture.Write("tools/sub/deep.txt", "deep");
        fixture.Write("untracked-later.txt", "x");
        IsolatedGit.RunChecked(fixture.Work, "add", "--", "tools");
        IsolatedGit.RunChecked(fixture.Work, "commit", "-q", "-m", "tools");

        var bracket = await fixture.Git.ListTrackedFilesAsync(fixture.Work, ["tools/a[1].txt"]);
        var directory = await fixture.Git.ListTrackedFilesAsync(fixture.Work, ["tools"]);
        var glob = await fixture.Git.ListTrackedFilesAsync(fixture.Work, ["tools/*.txt"]);
        var untracked = await fixture.Git.ListTrackedFilesAsync(fixture.Work, ["untracked-later.txt", "does/not/exist"]);
        var none = await fixture.Git.ListTrackedFilesAsync(fixture.Work, []);

        Assert.Equal(["tools/a[1].txt"], bracket);
        Assert.Equal(["tools/a1.txt", "tools/a[1].txt", "tools/sub/deep.txt"], directory.Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(glob);
        Assert.Empty(untracked);
        Assert.Empty(none);
    }

    [Fact]
    public async Task CommitPaths_commits_exactly_the_given_paths_and_keeps_other_staged_work_staged()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Write("tools/gone.txt", "gone");
        fixture.Write("tools/changed.txt", "one");
        fixture.CommitAll("add tools");
        var before = IsolatedGit.Head(fixture.Work);
        File.Delete(Path.Combine(fixture.Work, "tools", "gone.txt"));
        fixture.Write("tools/changed.txt", "two");
        fixture.Write("tools/new.txt", "new");
        fixture.Write("unrelated.txt", "staged for something else");
        IsolatedGit.RunChecked(fixture.Work, "add", "--", "unrelated.txt");

        var commit = await fixture.Git.CommitPathsAsync(fixture.Work, ["tools"], "chore: tidy tools");

        Assert.Equal(IsolatedGit.Head(fixture.Work), commit.Id);
        Assert.Equal(before, IsolatedGit.RunChecked(fixture.Work, "rev-parse", "HEAD~1").Trim());
        Assert.Equal("chore: tidy tools", IsolatedGit.RunChecked(fixture.Work, "log", "-1", "--format=%s").Trim());
        var changes = IsolatedGit.RunChecked(fixture.Work, "diff-tree", "--no-commit-id", "--name-status", "-r", "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["A\ttools/new.txt", "D\ttools/gone.txt", "M\ttools/changed.txt"], changes);
        Assert.Equal("unrelated.txt\n", IsolatedGit.RunChecked(fixture.Work, "diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task CommitPaths_treats_glob_characters_in_a_path_literally()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Write("tools/[x].txt", "bracket");
        fixture.Write("tools/x.txt", "plain");
        fixture.CommitAll("add tools");
        File.Delete(Path.Combine(fixture.Work, "tools", "[x].txt"));
        File.Delete(Path.Combine(fixture.Work, "tools", "x.txt"));

        await fixture.Git.CommitPathsAsync(fixture.Work, ["tools/[x].txt"], "remove the bracket file");

        Assert.Equal("D\ttools/[x].txt\n", IsolatedGit.RunChecked(fixture.Work, "diff-tree", "--no-commit-id", "--name-status", "-r", "HEAD"));
        Assert.Equal(" D tools/x.txt\n", IsolatedGit.RunChecked(fixture.Work, "status", "--porcelain"));
    }

    [Fact]
    public async Task CommitPaths_fails_when_there_is_nothing_to_commit_and_rejects_bad_arguments()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var head = IsolatedGit.Head(fixture.Work);

        await Assert.ThrowsAsync<GitException>(() => fixture.Git.CommitPathsAsync(fixture.Work, ["feature.txt"], "nothing changed"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Git.CommitPathsAsync(fixture.Work, [], "message"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Git.CommitPathsAsync(fixture.Work, ["feature.txt"], " "));
        Assert.Equal(head, IsolatedGit.Head(fixture.Work));
    }

    [Fact]
    public async Task GetConfigValue_returns_the_value_or_null()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        IsolatedGit.RunChecked(fixture.Work, "config", "--local", "forge.test.blank", string.Empty);

        Assert.Equal(ScreenshotFixture.PublicAddress, await fixture.Git.GetConfigValueAsync(fixture.Work, "remote.origin.url"));
        Assert.Null(await fixture.Git.GetConfigValueAsync(fixture.Work, "remote.origin.pushurl"));
        Assert.Null(await fixture.Git.GetConfigValueAsync(fixture.Work, "forge.test.blank"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Git.GetConfigValueAsync(fixture.Work, "--list"));
    }

    [Fact]
    public async Task Plumbing_works_with_a_cancelled_token_by_throwing_and_changing_nothing()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        var tree = await fixture.Git.MakeTreeAsync(fixture.Work, []);
        var refsBefore = IsolatedGit.RunChecked(fixture.Work, "for-each-ref");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Git.CommitTreeAsync(fixture.Work, tree, "cancelled", null, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Git.MakeTreeAsync(fixture.Work, [], cancellation.Token));
        Assert.Equal(refsBefore, IsolatedGit.RunChecked(fixture.Work, "for-each-ref"));
    }

    private static string WritePng(ScreenshotFixture fixture, string tag)
    {
        var path = Path.Combine(fixture.Markers, tag + ".png");
        File.WriteAllBytes(path, ScreenshotTestData.Png(tag));
        return path;
    }
}
