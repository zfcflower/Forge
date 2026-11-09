using Avalonia.Headless;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

[Collection("forge-app-ui")]
public sealed class ChangedTreeTests
{
    private static FileDiffViewModel File(string path, FileDiffStatus status = FileDiffStatus.Modified, bool binary = false)
    {
        var lines = new List<DiffLine> { new(DiffLineKind.Added, 0, 1, "x"), new(DiffLineKind.Removed, 1, 0, "y") };
        var diff = new FileDiff(path, path, status, binary, binary ? [] : [new DiffHunk(1, 1, 1, 1, lines)]);
        return new FileDiffViewModel(diff);
    }

    private static ChangedFileTree Tree(params FileDiffViewModel[] files)
    {
        var tree = new ChangedFileTree(static _ => { });
        tree.Replace(files);
        return tree;
    }

    [Fact]
    public void Single_folder_chains_are_compacted_and_folders_sort_before_files()
    {
        var tree = Tree(File("zeta.txt"), File("src/app/core/b.cs"), File("src/app/core/a.cs"), File("docs/readme.md"), File("src/top.cs"));

        var names = tree.Rows.Select(static row => row.Name).ToArray();
        Assert.Equal(["docs", "readme.md", "src", "app/core", "a.cs", "b.cs", "top.cs", "zeta.txt"], names);
        var compacted = tree.Rows.Single(static row => row.Name == "app/core");
        Assert.Equal("src/app/core", compacted.Path);
        Assert.True(compacted.IsDirectory);
        Assert.Equal(1, compacted.Depth);
    }

    [Fact]
    public void File_rows_expose_counts_badge_and_added_state()
    {
        var tree = Tree(File("a.txt", FileDiffStatus.Added), File("img.png", FileDiffStatus.Modified, binary: true));

        var added = tree.Rows.Single(static row => row.Name == "a.txt");
        Assert.True(added.IsAdded);
        Assert.True(added.ShowCounts);
        Assert.Equal("+1", added.AddedText);
        Assert.Equal("−1", added.RemovedText);
        var binary = tree.Rows.Single(static row => row.Name == "img.png");
        Assert.False(binary.ShowCounts);
        Assert.True(binary.ShowBadgeOnly);
        Assert.Equal("M", binary.Badge);
    }

    [Fact]
    public void Collapse_all_hides_children_and_expand_all_restores_them_and_survives_rebuild()
    {
        var files = new[] { File("a/x.txt"), File("a/y.txt"), File("b/c/z.txt"), File("b/w.txt") };
        var tree = Tree(files);
        var full = tree.Rows.Count;
        Assert.False(tree.IsAllCollapsed);

        tree.SetAllExpanded(false);
        Assert.True(tree.IsAllCollapsed);
        Assert.All(tree.Rows, static row => Assert.True(row.IsDirectory && row.Depth == 0));
        Assert.Equal(2, tree.Rows.Count);

        tree.Replace(files);
        Assert.True(tree.IsAllCollapsed);

        tree.SetAllExpanded(true);
        Assert.Equal(full, tree.Rows.Count);
        Assert.False(tree.IsAllCollapsed);
    }

    [Fact]
    public void Folder_toggle_splices_rows_and_file_toggle_opens_the_file()
    {
        FileDiffViewModel? opened = null;
        var tree = new ChangedFileTree(file => opened = file);
        var file = File("d/f.txt");
        tree.Replace([file]);

        var folder = tree.Rows[0];
        folder.ToggleCommand.Execute(null);
        Assert.Single(tree.Rows);
        folder.ToggleCommand.Execute(null);
        Assert.Equal(2, tree.Rows.Count);

        tree.Rows[1].ToggleCommand.Execute(null);
        Assert.Same(file, opened);

        tree.SetSelected(file);
        Assert.True(tree.Rows[1].IsSelectedFile);
        tree.SetSelected(null);
        Assert.False(tree.Rows[1].IsSelectedFile);
    }
}
