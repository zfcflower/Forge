using Avalonia.Headless;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

[CollectionDefinition("forge-app-ui", DisableParallelization = true)]
public sealed class HeadlessCollection : ICollectionFixture<SharedHeadlessFixture>
{
}

/// <summary>
/// Pins <see cref="FilesPanelViewModel"/> refresh semantics: a stale (lower-generation) refresh never
/// overwrites a newer result, expanded folders survive a refresh, and the flat
/// <see cref="FilesPanelViewModel.Rows"/> list always holds exactly the visible nodes. Skipped-folder and
/// <c>.gitignore</c> rules are covered too.
///
/// Headless note: the panel refreshes fire-and-forget on <c>Attach</c> and on watcher ticks, which do not
/// reliably drain on a headless UI loop, so every step below performs an explicit, awaited
/// <c>RefreshAsync</c> on the dispatcher instead of polling for the background refresh.
/// </summary>
[Collection("forge-app-ui")]
public sealed class FilesPanelTests : IDisposable
{
    private readonly List<string> roots = [];

    public void Dispose()
    {
        foreach (var root in roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task Stale_refresh_never_overwrites_a_newer_result()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var big = NewRoot();
        for (var i = 0; i < 1000; i++)
        {
            File.WriteAllText(Path.Combine(big, $"b-{i:0000}.txt"), "x");
        }

        var tiny = NewRoot();
        File.WriteAllText(Path.Combine(tiny, "alpha.txt"), "x");

        var rows = await session.Dispatch(
            async () =>
            {
                var panel = new FilesPanelViewModel(new GitCli(), _ => { }, _ => { }, _ => { });
                panel.Attach(new PanelContext("s-big", big, null));
                var slow = panel.RefreshAsync(CancellationToken.None);
                panel.Attach(new PanelContext("s-tiny", tiny, null));
                var fast = panel.RefreshAsync(CancellationToken.None);
                await Task.WhenAll(slow, fast);
                return panel.Rows.Select(static row => row.RelativePath).ToList();
            },
            CancellationToken.None);

        // The slow refresh over the big directory (older generation) must have discarded itself;
        // the rows show the tiny directory even though the slow build finished last.
        var single = Assert.Single(rows);
        Assert.Equal("alpha.txt", single);
    }

    [Fact]
    public async Task Expanded_folders_survive_a_refresh()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var root = NewRoot();
        Write(root, "sub/a.txt");
        Write(root, "sub/b.txt");
        Write(root, "root.txt");

        var rows = await session.Dispatch(
            async () =>
            {
                var panel = new FilesPanelViewModel(new GitCli(), _ => { }, _ => { }, _ => { });
                panel.Attach(new PanelContext("s1", root, null));
                await panel.RefreshAsync(CancellationToken.None);
                panel.Rows.Single(row => row.RelativePath == "sub").IsExpanded = true;
                await panel.RefreshAsync(CancellationToken.None);
                return panel.Rows.Select(static row => (row.RelativePath, row.IsExpanded, row.Depth)).ToList();
            },
            CancellationToken.None);

        Assert.Equal(4, rows.Count);
        Assert.Contains(("sub", true, 0), rows);
        Assert.Contains(("sub/a.txt", false, 1), rows);
        Assert.Contains(("sub/b.txt", false, 1), rows);
    }

    [Fact]
    public async Task Row_count_equals_visible_nodes_with_depth_steps_of_at_most_one()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var root = NewRoot();
        Write(root, "d1/f1.txt");
        Write(root, "d1/f2.txt");
        Write(root, "d2/f3.txt");
        Write(root, "top.txt");

        var rows = await session.Dispatch(
            async () =>
            {
                var panel = new FilesPanelViewModel(new GitCli(), _ => { }, _ => { }, _ => { });
                panel.Attach(new PanelContext("s1", root, null));
                await panel.RefreshAsync(CancellationToken.None);
                var collapsed = panel.Rows.Count;
                panel.Rows.Single(row => row.RelativePath == "d1").IsExpanded = true;
                var oneExpanded = panel.Rows.Count;
                panel.Rows.Single(row => row.RelativePath == "d2").IsExpanded = true;
                var bothExpanded = panel.Rows.Count;
                return (Collapsed: collapsed, OneExpanded: oneExpanded, BothExpanded: bothExpanded,
                    Rows: panel.Rows.Select(static row => (row.RelativePath, row.Depth)).ToList());
            },
            CancellationToken.None);

        Assert.Equal(3, rows.Collapsed);
        Assert.Equal(5, rows.OneExpanded);
        Assert.Equal(6, rows.BothExpanded);
        Assert.Equal(0, rows.Rows[0].Depth);
        for (var i = 1; i < rows.Rows.Count; i++)
        {
            Assert.True(
                rows.Rows[i].Depth <= rows.Rows[i - 1].Depth + 1,
                $"Row {rows.Rows[i].RelativePath} jumps from depth {rows.Rows[i - 1].Depth} to {rows.Rows[i].Depth}.");
        }

        var byPath = rows.Rows.ToDictionary(static row => row.RelativePath, static row => row.Depth, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(byPath["d1"] + 1, byPath["d1/f1.txt"]);
        Assert.Equal(byPath["d1"] + 1, byPath["d1/f2.txt"]);
        Assert.Equal(byPath["d2"] + 1, byPath["d2/f3.txt"]);
    }

    [Fact]
    public async Task Walk_skips_tool_folders_in_the_panel_tree()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var root = NewRoot();
        foreach (var skipped in new[] { "bin", "obj", ".git", "node_modules", ".vs", ".idea", ".forge" })
        {
            Write(root, skipped + "/tool.dat");
        }

        Write(root, "src/ok.txt");
        Write(root, "README.md");

        var rows = await session.Dispatch(
            async () =>
            {
                var panel = new FilesPanelViewModel(new GitCli(), _ => { }, _ => { }, _ => { });
                panel.Attach(new PanelContext("s1", root, null));
                await panel.RefreshAsync(CancellationToken.None);
                var names = panel.Rows.Select(static row => row.Name).ToList();
                panel.Rows.Single(row => row.RelativePath == "src").IsExpanded = true;
                var child = panel.Rows.SingleOrDefault(static row => row.RelativePath == "src/ok.txt");
                return (Names: names, Child: child?.RelativePath);
            },
            CancellationToken.None);

        Assert.Equal(2, rows.Names.Count);
        Assert.Contains("src", rows.Names);
        Assert.Contains("README.md", rows.Names);
        Assert.DoesNotContain("bin", rows.Names);
        Assert.DoesNotContain("obj", rows.Names);
        Assert.DoesNotContain(".git", rows.Names);
        Assert.DoesNotContain("node_modules", rows.Names);
        Assert.DoesNotContain(".vs", rows.Names);
        Assert.DoesNotContain(".idea", rows.Names);
        Assert.DoesNotContain(".forge", rows.Names);
        Assert.Equal("src/ok.txt", rows.Child);
    }

    [Fact]
    public async Task Git_repo_panel_tree_honours_gitignore()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var root = NewRoot();
        var git = new GitCli();
        Assert.True((await git.RunAsync(root, ["init"], CancellationToken.None)).Success);
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "ignored/\n*.log\n");
        Write(root, "kept/keep.txt");
        Write(root, "ignored/skip.txt");
        Write(root, "debug.log");
        Write(root, "top.txt");

        var rows = await session.Dispatch(
            async () =>
            {
                var panel = new FilesPanelViewModel(new GitCli(), _ => { }, _ => { }, _ => { });
                panel.Attach(new PanelContext("s1", root, null));
                await panel.RefreshAsync(CancellationToken.None);
                var names = panel.Rows.Select(static row => row.RelativePath).ToList();
                panel.Rows.Single(row => row.RelativePath == "kept").IsExpanded = true;
                var child = panel.Rows.SingleOrDefault(static row => row.RelativePath == "kept/keep.txt");
                return (Names: names, Child: child?.RelativePath);
            },
            CancellationToken.None);

        Assert.Contains("kept", rows.Names);
        Assert.Contains("top.txt", rows.Names);
        Assert.DoesNotContain("ignored", rows.Names);
        Assert.DoesNotContain("debug.log", rows.Names);
        Assert.Equal("kept/keep.txt", rows.Child);
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        roots.Add(root);
        return root;
    }

    private static void Write(string root, string relativePath)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
    }
}
