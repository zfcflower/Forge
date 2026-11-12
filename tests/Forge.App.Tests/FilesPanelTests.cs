using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.ViewModels;
using Forge.App.Views;
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
        var session = SharedUi.Session;
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
        var session = SharedUi.Session;
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
        var session = SharedUi.Session;
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
        var session = SharedUi.Session;
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
        var session = SharedUi.Session;
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

    [Fact]
    public async Task Tab_badges_count_the_whole_index_and_the_change_list_and_show_zero_without_a_session()
    {
        var session = SharedUi.Session;
        var root = NewRoot();
        Write(root, "sub/a.txt");
        Write(root, "sub/b.txt");
        Write(root, "top.txt");

        var counts = await session.Dispatch(
            async () =>
            {
                var panel = new FilesPanelViewModel(new GitCli(), _ => { }, _ => { }, _ => { });
                panel.Attach(new PanelContext("s1", root, null));
                await panel.RefreshAsync(CancellationToken.None);
                var attached = (
                    panel.AllCount,
                    panel.ChangedCount,
                    Changes: panel.Changes.Count,
                    Rows: panel.Rows.Count,
                    panel.AllTabName,
                    panel.ChangedTabName);

                panel.Attach(PanelContext.Empty);
                await panel.RefreshAsync(CancellationToken.None);
                return (Attached: attached, Empty: (panel.AllCount, panel.ChangedCount, panel.AllTabName));
            },
            CancellationToken.None);

        // "全部" counts the index, not the visible rows: "sub" is still collapsed, so one row stands for three files.
        Assert.Equal(3, counts.Attached.AllCount);
        Assert.Equal(2, counts.Attached.Rows);
        Assert.Equal(counts.Attached.Changes, counts.Attached.ChangedCount);

        // The badge's number has to reach assistive technology, which reads the accessible name instead of the content.
        Assert.Contains("3", counts.Attached.AllTabName, StringComparison.Ordinal);
        Assert.Contains(
            counts.Attached.ChangedCount.ToString(CultureInfo.InvariantCulture),
            counts.Attached.ChangedTabName,
            StringComparison.Ordinal);

        // No session still shows both badges, as 0, and both names stay well formed.
        Assert.Equal((0, 0), (counts.Empty.AllCount, counts.Empty.ChangedCount));
        Assert.Contains("0", counts.Empty.AllTabName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Toolbar_badges_render_the_counts_and_the_summary_line_is_gone()
    {
        var session = SharedUi.Session;
        var root = NewRoot();
        Write(root, "a.txt");
        Write(root, "b.txt");

        var toolbar = await session.Dispatch(
            async () =>
            {
                var panel = new FilesPanelViewModel(new GitCli(), _ => { }, _ => { }, _ => { });
                panel.Attach(new PanelContext("s1", root, null));
                await panel.RefreshAsync(CancellationToken.None);

                var view = new FilesToolView { DataContext = panel };
                var window = SharedUi.Show(view, 275, 320);
                SharedUi.Settle(window);

                var all = Badge(window, "files-all-count");
                var changed = Badge(window, "files-changed-count");
                var texts = view.GetVisualDescendants().OfType<TextBlock>()
                    .Where(static block => block.IsEffectivelyVisible)
                    .Select(static block => block.Text ?? string.Empty)
                    .ToList();
                // The digits sit wholly inside the pill and centred on it (a fixed line height once clipped and shifted them).
                foreach (var badge in new[] { all, changed })
                {
                    var pill = Assert.IsType<Border>(badge.Parent);
                    var glyphs = badge.TextLayout.Height;
                    var inner = pill.Bounds.Height - pill.Padding.Top - pill.Padding.Bottom;
                    Assert.True(glyphs <= inner + 0.5, $"badge text height {glyphs} exceeds the pill's content height {inner}");
                    Assert.True(badge.TextLayout.WidthIncludingTrailingWhitespace <= badge.Bounds.Width + 0.5, "badge text is clipped horizontally");
                    var origin = badge.TranslatePoint(default, pill)!.Value;
                    var centreX = origin.X + badge.Bounds.Width / 2;
                    var centreY = origin.Y + badge.Bounds.Height / 2;
                    Assert.InRange(centreX, pill.Bounds.Width / 2 - 0.75, pill.Bounds.Width / 2 + 0.75);
                    Assert.InRange(centreY, pill.Bounds.Height / 2 - 0.75, pill.Bounds.Height / 2 + 0.75);
                }

                return (All: all.Text, Changed: changed.Text, Pill: all.Parent is Border border && border.Classes.Contains("count-badge"), Texts: texts);
            },
            CancellationToken.None);

        Assert.Equal("2", toolbar.All);
        Assert.Equal("0", toolbar.Changed);
        Assert.True(toolbar.Pill, "the count is drawn as a count-badge pill, not as bare text.");

        // The old summary after the buttons read "0 个改动 · 348 个文件"; no text on the toolbar may invent that line again.
        Assert.DoesNotContain(toolbar.Texts, static text => text.Contains('\u00b7'));
    }

    private static TextBlock Badge(Visual root, string automationId) =>
        root.GetVisualDescendants().OfType<TextBlock>()
            .First(block => AutomationProperties.GetAutomationId(block) == automationId);

    private string NewRoot()
    {
        var root = TestTempFiles.NewDirectory("forge-files-");
        roots.Add(root);
        return root;
    }

    private static void Write(string root, string relativePath) => TestTempFiles.WriteFile(root, relativePath, "x");
}
