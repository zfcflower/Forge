using Xunit;

namespace Forge.Workspace.Tests.Git;

[CollectionDefinition("ForgeSnapshotTempFiles")]
public sealed class ForgeSnapshotTempFilesCollection
{
}

/// <summary>Pins GitCli.SnapshotTreeAsync: the temporary-index snapshot leaves the user's index,
/// HEAD, branch, refs, stash and status untouched, cleans up after itself (including on failure),
/// and reports the tree git itself would report.</summary>
[Collection("ForgeSnapshotTempFiles")]
public sealed class GitCliSnapshotTests
{
    private static GitCli Cli() => new();

    private static string IndexPath(string root) =>
        GitFixture.RunChecked(root, "rev-parse", "--path-format=absolute", "--git-path", "index").Trim();

    private static HashSet<string> SnapshotTempFiles() =>
        Directory.GetFiles(Path.GetTempPath(), "forge-snapshot-*").ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task Snapshot_leaves_index_bytes_mtime_head_branch_refs_stash_and_status_untouched()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "line1\nline2\n");
        repo.Commit("base");

        // Park one change in the stash so `git stash list` has content to protect.
        repo.Write("a.txt", "stashed change\n");
        repo.RunGit("stash");
        Assert.NotEmpty(GitFixture.RunChecked(repo.Root, "stash", "list"));

        // Now dirty the tree three ways: unstaged modification, staged new file, untracked file.
        repo.Write("a.txt", "working change\n");
        repo.Write("staged.txt", "staged\n");
        repo.RunGit("add", "staged.txt");
        repo.Write("untracked.txt", "untracked\n");

        var cli = Cli();
        var index = IndexPath(repo.Root);
        var head = await cli.RevParseAsync(repo.Root, "HEAD");
        var branch = await cli.GetCurrentBranchAsync(repo.Root);
        var refs = GitFixture.RunChecked(repo.Root, "for-each-ref");
        var stash = GitFixture.RunChecked(repo.Root, "stash", "list");
        var status = GitFixture.RunChecked(repo.Root, "status", "--porcelain");

        // Captured last: `git status` itself refreshes racily-clean index entries
        // (same bytes, new mtime), so the mtime baseline must postdate it.
        var indexBytes = File.ReadAllBytes(index);
        var indexMtime = File.GetLastWriteTimeUtc(index);

        var tree = await cli.SnapshotTreeAsync(repo.Root);

        Assert.NotEmpty(tree);
        Assert.Equal(indexBytes, File.ReadAllBytes(index));
        Assert.Equal(indexMtime, File.GetLastWriteTimeUtc(index));
        Assert.Equal(head, await cli.RevParseAsync(repo.Root, "HEAD"));
        Assert.Equal(branch, await cli.GetCurrentBranchAsync(repo.Root));
        Assert.Equal(refs, GitFixture.RunChecked(repo.Root, "for-each-ref"));
        Assert.Equal(stash, GitFixture.RunChecked(repo.Root, "stash", "list"));
        Assert.Equal(status, GitFixture.RunChecked(repo.Root, "status", "--porcelain"));
    }

    [Fact]
    public async Task Snapshot_removes_its_temp_index_files()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("base");
        repo.Write("dirty.txt", "dirty\n");

        var before = SnapshotTempFiles();
        await Cli().SnapshotTreeAsync(repo.Root);
        var after = SnapshotTempFiles();
        after.ExceptWith(before);
        Assert.Empty(after);
    }

    [Fact]
    public async Task Snapshot_cleans_up_temp_files_when_git_fails_midway()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Write(".gitattributes", "*.bin filter=fail\n");
        repo.Commit("base");
        repo.RunGit("config", "--local", "filter.fail.clean", "git non-existent-command-xyz");
        repo.RunGit("config", "--local", "filter.fail.required", "true");
        repo.WriteBytes("f.bin", [1, 2, 3]);

        var index = IndexPath(repo.Root);
        var indexBytes = File.ReadAllBytes(index);
        var before = SnapshotTempFiles();

        await Assert.ThrowsAsync<GitException>(() => Cli().SnapshotTreeAsync(repo.Root));

        var after = SnapshotTempFiles();
        after.ExceptWith(before);
        Assert.Empty(after);
        Assert.Equal(indexBytes, File.ReadAllBytes(index));
    }

    [Fact]
    public async Task Snapshot_includes_untracked_but_excludes_ignored_files()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Write(".gitignore", "*.ignored\n");
        repo.Commit("base");

        var cli = Cli();
        var first = await cli.SnapshotTreeAsync(repo.Root);
        repo.Write("new.txt", "new file\n");
        repo.Write("skip.ignored", "ignored\n");
        var second = await cli.SnapshotTreeAsync(repo.Root);

        Assert.NotEqual(first, second);
        var changes = await cli.GetTreeChangesAsync(repo.Root, first, second);
        var change = Assert.Single(changes);
        Assert.Equal("new.txt", change.Path);
        Assert.Equal(GitEntryStatus.Added, change.IndexStatus);

        var text = await cli.GetTreeDiffTextAsync(repo.Root, first, second);
        Assert.Contains("new.txt", text, StringComparison.Ordinal);
        Assert.DoesNotContain("skip.ignored", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_of_an_unchanged_tree_returns_the_same_hash()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("base");
        repo.Write("dirty.txt", "dirty\n");

        var cli = Cli();
        Assert.Equal(await cli.SnapshotTreeAsync(repo.Root), await cli.SnapshotTreeAsync(repo.Root));
    }

    [Fact]
    public async Task Snapshot_of_a_clean_tree_matches_head_tree()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("base");

        var tree = await Cli().SnapshotTreeAsync(repo.Root);
        Assert.Equal(GitFixture.RunChecked(repo.Root, "rev-parse", "HEAD^{tree}").Trim(), tree);
    }

    [Fact]
    public async Task Snapshot_inside_a_linked_worktree_matches_and_leaves_the_main_index_alone()
    {
        if (!GitProbe.IsAvailable) return;
        using var sandbox = new TempDirectory();
        using var repo = new TestRepo(sandbox);
        repo.Write("a.txt", "a\n");
        repo.Commit("base");

        var linked = sandbox.Combine("linked");
        GitFixture.RunChecked(repo.Root, "worktree", "add", linked);
        try
        {
            var cli = Cli();
            var mainIndexBytes = File.ReadAllBytes(IndexPath(repo.Root));
            var tree = await cli.SnapshotTreeAsync(linked);
            Assert.Equal(GitFixture.RunChecked(linked, "rev-parse", "HEAD^{tree}").Trim(), tree);
            Assert.Equal(await cli.SnapshotTreeAsync(repo.Root), tree);
            Assert.Equal(mainIndexBytes, File.ReadAllBytes(IndexPath(repo.Root)));
        }
        finally
        {
            GitFixture.RunChecked(repo.Root, "worktree", "remove", "--force", linked);
        }
    }

    [Fact]
    public async Task Snapshot_without_a_real_index_file_still_works()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("base");

        var cli = Cli();
        var before = await cli.SnapshotTreeAsync(repo.Root);
        var index = IndexPath(repo.Root);
        File.Delete(index);

        var after = await cli.SnapshotTreeAsync(repo.Root);
        Assert.Equal(before, after);
        Assert.False(File.Exists(index));
    }

    [Fact]
    public async Task Snapshot_of_a_non_repo_throws_GitException()
    {
        if (!GitProbe.IsAvailable) return;
        using var directory = new TempDirectory();

        await Assert.ThrowsAsync<GitException>(() => Cli().SnapshotTreeAsync(directory.Path));
    }

    [Fact]
    public async Task Tree_arguments_with_a_leading_dash_are_rejected()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("a.txt", "a\n");
        repo.Commit("base");

        var cli = Cli();
        var tree = await cli.SnapshotTreeAsync(repo.Root);
        await Assert.ThrowsAsync<ArgumentException>(() => cli.GetTreeDiffTextAsync(repo.Root, "-foo", tree));
        await Assert.ThrowsAsync<ArgumentException>(() => cli.GetTreeDiffAsync(repo.Root, tree, "-bar"));
        await Assert.ThrowsAsync<ArgumentException>(() => cli.GetTreeChangesAsync(repo.Root, "--upload-pack=x", tree));
    }
}
