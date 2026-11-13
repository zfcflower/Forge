using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>Pins the tree-to-tree diff surface: name/status changes (with rename sources),
/// parsed hunks and unified text between two SnapshotTreeAsync hashes.</summary>
[Collection("ForgeSnapshotTempFiles")]
public sealed class GitTreeDiffTests
{
    private static GitCli Cli() => new();

    [Fact]
    public async Task TreeChanges_reports_added_modified_and_deleted()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.Write("keep.txt", "keep\n");
        repo.Write("change.txt", "before\n");
        repo.CommitFile("gone.txt", "gone\n", "base");

        var cli = Cli();
        var first = await cli.SnapshotTreeAsync(repo.Root);
        repo.Write("change.txt", "after\n");
        File.Delete(repo.FullPath("gone.txt"));
        repo.Write("added.txt", "added\n");
        var second = await cli.SnapshotTreeAsync(repo.Root);

        var changes = (await cli.GetTreeChangesAsync(repo.Root, first, second))
            .OrderBy(static c => c.Path, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(3, changes.Count);
        Assert.Equal("added.txt", changes[0].Path);
        Assert.Equal(GitEntryStatus.Added, changes[0].IndexStatus);
        Assert.Equal("change.txt", changes[1].Path);
        Assert.Equal(GitEntryStatus.Modified, changes[1].IndexStatus);
        Assert.Equal("gone.txt", changes[2].Path);
        Assert.Equal(GitEntryStatus.Deleted, changes[2].IndexStatus);
    }

    [Fact]
    public async Task TreeChanges_reports_renames_with_the_original_path()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.CommitFile("old.txt", "line1\nline2\nline3\nline4\n", "base");

        var cli = Cli();
        var first = await cli.SnapshotTreeAsync(repo.Root);
        File.Move(repo.FullPath("old.txt"), repo.FullPath("new.txt"));
        var second = await cli.SnapshotTreeAsync(repo.Root);

        var change = Assert.Single(await cli.GetTreeChangesAsync(repo.Root, first, second));
        Assert.Equal(GitStatusKind.Renamed, change.Kind);
        Assert.Equal("new.txt", change.Path);
        Assert.Equal("old.txt", change.OriginalPath);
        Assert.Equal(GitEntryStatus.Renamed, change.IndexStatus);
    }

    [Fact]
    public async Task TreeDiff_returns_hunks_with_numbered_lines()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.CommitFile("a.txt", "one\ntwo\nthree\nfour\n", "base");

        var cli = Cli();
        var first = await cli.SnapshotTreeAsync(repo.Root);
        repo.Write("a.txt", "one\nTWO\nthree\nfour\nfive\n");
        var second = await cli.SnapshotTreeAsync(repo.Root);

        var file = Assert.Single(await cli.GetTreeDiffAsync(repo.Root, first, second));
        Assert.Equal("a.txt", file.Path);
        Assert.Equal(FileDiffStatus.Modified, file.Status);
        Assert.NotEmpty(file.Hunks);
        Assert.True(file.AddedLineCount > 0);
        Assert.True(file.RemovedLineCount > 0);
        var hunk = file.Hunks[0];
        Assert.True(hunk.OldStart >= 1);
        Assert.True(hunk.NewStart >= 1);
        Assert.All(hunk.Lines, line =>
        {
            if (line.Kind == DiffLineKind.Added) Assert.True(line.NewLineNumber > 0);
            if (line.Kind == DiffLineKind.Removed) Assert.True(line.OldLineNumber > 0);
        });
        Assert.Contains(hunk.Lines, line => line.Kind == DiffLineKind.Added && line.Text.Contains("TWO", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TreeDiffText_is_non_empty_for_differing_trees()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.CommitFile("a.txt", "one\n", "base");

        var cli = Cli();
        var first = await cli.SnapshotTreeAsync(repo.Root);
        repo.Write("a.txt", "two\n");
        var second = await cli.SnapshotTreeAsync(repo.Root);

        var text = await cli.GetTreeDiffTextAsync(repo.Root, first, second);
        Assert.NotEmpty(text);
        Assert.Contains("a.txt", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_tree_diffs_are_empty()
    {
        if (!GitProbe.IsAvailable) return;
        using var repo = new TestRepo();
        repo.CommitFile("a.txt", "one\n", "base");

        var cli = Cli();
        var tree = await cli.SnapshotTreeAsync(repo.Root);
        Assert.Empty(await cli.GetTreeChangesAsync(repo.Root, tree, tree));
        Assert.Empty(await cli.GetTreeDiffAsync(repo.Root, tree, tree));
        Assert.Empty(await cli.GetTreeDiffTextAsync(repo.Root, tree, tree));
    }
}
