using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The line between the preview column and the files column is one draggable <see cref="PanelSplitter"/> in both hosts. Two
/// things break it silently: the line has to take the side the preview column sits on (a swapped region order puts the
/// preview to the right of the files column, and the drag then subtracts), and the floating files window has to give it a
/// real splitter at all - it used to paint a 1 px border, which is why that window's gap could not be dragged.
/// </summary>
[Collection("forge-app-ui")]
public sealed class PreviewSplitterTests
{
    [Fact]
    public Task The_floating_files_window_drags_its_preview_splitter() => SharedUi.RunAsync(() =>
    {
        using var layout = NewLayout();
        layout.OpenTab(layout.PreviewTabs, "file:a.cs", TabKind.FilePreview, new object(), "a.cs");
        var (window, _) = Place(layout);

        layout.FloatFilesPanel();
        SharedUi.Settle(window);
        var floating = window.OwnedWindows.Single();
        var splitter = floating.GetVisualDescendants().OfType<PanelSplitter>().Single();
        Assert.Equal(PanelSplitterKind.Preview, splitter.Kind);
        Assert.True(splitter.IsVertical);

        // The 6 px band straddles the 1 px line, so the columns still meet across a single pixel line.
        var grid = (Grid)splitter.Parent!;
        var preview = grid.Children.OfType<Border>().Single();
        var files = grid.Children.OfType<ContentControl>().Single();
        Assert.Equal(WorkbenchMetrics.PanelSplitterWidth, files.Bounds.X - preview.Bounds.Right);

        // A 60 px drag to the right widens the preview column by 60: the preview sits on the left in this window.
        var start = layout.PreviewWidth;
        var from = CenterOf(splitter, floating);
        floating.MouseDown(from, MouseButton.Left);
        floating.MouseMove(from + new Vector(60, 0));
        floating.MouseUp(from + new Vector(60, 0), MouseButton.Left);
        SharedUi.Settle(floating);

        Assert.Equal(start + 60, layout.PreviewWidth);

        floating.Close();
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_docked_line_drags_the_way_its_column_is_placed() => SharedUi.RunAsync(() =>
    {
        using var layout = NewLayout();
        layout.OpenTab(layout.PreviewTabs, "file:a.cs", TabKind.FilePreview, new object(), "a.cs");
        var (window, host) = Place(layout);

        // Swap the project and inspector regions: the files column now sits left of the preview, so the same rightward drag
        // narrows the preview column instead of widening it.
        layout.SwapRegions(RegionRole.Project, RegionRole.Inspector);
        SharedUi.Settle(window);
        Assert.False(layout.IsPreviewLeftOfFiles);

        var presenter = host.Children.OfType<RegionPresenter>().Single(p => p.Role == RegionRole.Inspector);
        var splitter = presenter.GetVisualDescendants().OfType<PanelSplitter>().Single(s => s.Kind == PanelSplitterKind.Preview);
        Assert.False(splitter.PreviewOnLeft);

        // The drag starts from the column that is on screen, which the region width may have clamped below the stored width.
        var arrangedStart = presenter.Children
            .OfType<PanelChrome>()
            .Single(chrome => ReferenceEquals(chrome.Model, layout.PreviewTabs))
            .Bounds.Width;
        var from = CenterOf(splitter, window);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Vector(40, 0));
        window.MouseUp(from + new Vector(40, 0), MouseButton.Left);
        SharedUi.Settle(window);

        Assert.Equal(arrangedStart - 40, layout.PreviewWidth);

        window.Close();
        return Task.CompletedTask;
    });

    private static WorkbenchLayout NewLayout() =>
        new(new object(), new object(), new object(), new object(), new object());

    private static (Window Window, WorkbenchHost Host) Place(WorkbenchLayout layout)
    {
        var host = new WorkbenchHost { Layout = layout };
        var window = new Window { Width = 1200, Height = 800, Content = host };
        window.Show();
        SharedUi.Settle(window);
        return (window, host);
    }

    private static Point CenterOf(Visual splitter, Visual host) =>
        splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), host)!.Value;
}
