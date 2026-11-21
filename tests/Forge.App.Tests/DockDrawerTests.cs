using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The dock strip floats one panel, not its whole region: the tab names the panel the user asked for, so the drawer shows
/// that panel alone. Three things go wrong silently when it does not: the drawer stacks every panel of the region, its panel
/// body paints over the 1 px outline the drawer has on the side facing Main, and every indicator bar in the strip reads the
/// same, so the strip never says which panel is on show. The outline and the content inset are on opposite sides of the
/// drawer, so the geometry is checked on a left- and a right-edge region.
/// </summary>
[Collection("forge-app-ui")]
public sealed class DockDrawerTests
{
    [Fact]
    public Task A_left_edge_strip_floats_only_the_panel_it_names() => SharedUi.RunAsync(() =>
    {
        using var layout = NewLayout();
        var (window, host) = Show(layout, RegionRole.Project);
        var strip = StripOf(host, RegionRole.Project);
        var presenter = PresenterOf(host, RegionRole.Project);

        Click(strip, WorkbenchIds.ProjectPanel);
        SharedUi.Settle(window);

        Assert.Same(layout.ProjectPanel, layout.DrawerPanel);
        Assert.Equal(WorkbenchIds.ProjectPanel, strip.ShownPanelId);
        Assert.Same(layout.ProjectPanel, OnlyLaidOutPanel(presenter));

        // Another tab of the same region switches the panel instead of stacking both.
        Click(strip, WorkbenchIds.GeneralPanel);
        SharedUi.Settle(window);
        Assert.Same(layout.GeneralPanel, layout.DrawerPanel);
        Assert.Equal(WorkbenchIds.GeneralPanel, strip.ShownPanelId);
        Assert.Same(layout.GeneralPanel, OnlyLaidOutPanel(presenter));

        // Esc and the tab itself both dismiss it.
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert.Null(layout.DrawerPanel);
        SharedUi.Settle(window);
        Assert.Null(strip.ShownPanelId);
        Assert.Empty(LaidOutPanels(presenter));

        Click(strip, WorkbenchIds.GeneralPanel);
        SharedUi.Settle(window);
        Assert.Same(layout.GeneralPanel, layout.DrawerPanel);

        Click(strip, WorkbenchIds.GeneralPanel);
        SharedUi.Settle(window);
        Assert.Null(layout.DrawerPanel);
        Assert.Empty(LaidOutPanels(presenter));

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_left_edge_drawer_keeps_its_outline_and_marks_the_shown_bar() => SharedUi.RunAsync(() =>
    {
        using var layout = NewLayout();
        var (window, host) = Show(layout, RegionRole.Project);
        var strip = StripOf(host, RegionRole.Project);
        Click(strip, WorkbenchIds.ProjectPanel);
        SharedUi.Settle(window);

        // The 1 px outline faces Main, i.e. the right side of a left-edge drawer, and the panel body stops before it.
        var surface = DrawerSurface(host);
        Assert.Equal(new Thickness(0, 0, 1, 0), surface.BorderThickness);
        Assert.Equal(surface.Bounds.Right - 1, PresenterOf(host, RegionRole.Project).Bounds.Right);

        // The strip's own 3 px bars follow the drawer: only the shown panel's bar is the accent.
        var bars = IndicatorBars(strip);
        Assert.Equal(2, bars.Length);
        Assert.Same(Resource(window, "BrandActionBrush"), bars[0].Background);
        Assert.Same(Resource(window, "DockIndicatorIdleBrush"), bars[1].Background);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_right_edge_strip_puts_the_outline_between_strip_and_panel() => SharedUi.RunAsync(() =>
    {
        using var layout = NewLayout();
        var (window, host) = Show(layout, RegionRole.Inspector);
        Click(StripOf(host, RegionRole.Inspector), WorkbenchIds.FilesPanel);
        SharedUi.Settle(window);

        Assert.Same(layout.FilesPanel, layout.DrawerPanel);
        var presenter = PresenterOf(host, RegionRole.Inspector);
        Assert.Same(layout.FilesPanel, OnlyLaidOutPanel(presenter));

        // Mirrored: the outline is on the drawer's left, so the body starts one pixel in and ends on the surface's edge.
        var surface = DrawerSurface(host);
        Assert.Equal(new Thickness(1, 0, 0, 0), surface.BorderThickness);
        Assert.Equal(surface.Bounds.X + 1, presenter.Bounds.X);
        Assert.Equal(surface.Bounds.Right, presenter.Bounds.Right);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public void A_minimized_panel_is_restored_when_its_tab_floats_it()
    {
        using var layout = NewLayout();
        layout.SetAutoHidden(RegionRole.Project, true);
        var general = layout.GeneralPanel!;
        layout.ToggleMinimize(general);
        Assert.True(general.IsMinimized);

        // A folded panel would leave the drawer a bare 28 px title bar, so the tab restores it first.
        layout.ToggleDrawer(general);
        Assert.False(general.IsMinimized);
        Assert.Same(general, layout.DrawerPanel);
    }

    [Fact]
    public Task A_drawer_opens_at_the_region_default_width() => SharedUi.RunAsync(() =>
    {
        using var layout = NewLayout();
        var (window, host) = Place(layout);

        // A region dragged wider before it was auto-hidden must not open a wider drawer.
        layout.BeginSplitterDrag(0);
        layout.UpdateSplitterDrag(WorkbenchMetrics.DefaultProjectWidth / 2);
        layout.EndSplitterDrag();
        SharedUi.Settle(window);
        Assert.True(layout.GetRegion(RegionRole.Project).Width > WorkbenchMetrics.DefaultProjectWidth);

        layout.SetAutoHidden(RegionRole.Project, true);
        SharedUi.Settle(window);
        Click(StripOf(host, RegionRole.Project), WorkbenchIds.ProjectPanel);
        SharedUi.Settle(window);
        Assert.Equal(WorkbenchMetrics.DefaultProjectWidth, DrawerSurface(host).Bounds.Width);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_preview_drawer_opens_at_the_preview_column_width() => SharedUi.RunAsync(() =>
    {
        using var layout = NewLayout();
        layout.OpenTab(layout.PreviewTabs, "file:a.cs", TabKind.FilePreview, new object(), "a.cs");
        var (window, host) = Show(layout, RegionRole.Inspector);

        Click(StripOf(host, RegionRole.Inspector), WorkbenchIds.PreviewTabs);
        SharedUi.Settle(window);

        // A drawer shows one column, not the region: the preview keeps the width it has while the region is pinned, even
        // though the region also reserves room for the files column beside it.
        Assert.Same(layout.PreviewTabs, layout.DrawerPanel);
        Assert.Equal(Math.Max(WorkbenchMetrics.PreviewMinWidth, layout.PreviewWidth), DrawerSurface(host).Bounds.Width);
        Assert.NotEqual(layout.GetRegion(RegionRole.Inspector).Width, DrawerSurface(host).Bounds.Width);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Covering_main_from_the_drawer_takes_main_s_area() => SharedUi.RunAsync(() =>
    {
        using var layout = NewLayout();
        layout.OpenTab(layout.PreviewTabs, "file:a.cs", TabKind.FilePreview, new object(), "a.cs");
        var (window, host) = Show(layout, RegionRole.Inspector);
        Click(StripOf(host, RegionRole.Inspector), WorkbenchIds.PreviewTabs);
        SharedUi.Settle(window);

        var main = PresenterOf(host, RegionRole.Main).Bounds;
        layout.TogglePreviewCoverMain();
        SharedUi.Settle(window);

        // The same union the pinned inspector covers: from Main's left edge to the strip.
        var surface = DrawerSurface(host);
        Assert.Equal(main.X, surface.Bounds.X);
        Assert.Equal(host.Bounds.Width - WorkbenchMetrics.AutoHideStripWidth, surface.Bounds.Right);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_files_drawer_takes_the_preview_beside_it_once_it_has_a_tab() => SharedUi.RunAsync(() =>
    {
        using var layout = NewLayout();
        var (window, host) = Show(layout, RegionRole.Inspector);
        var presenter = PresenterOf(host, RegionRole.Inspector);

        Click(StripOf(host, RegionRole.Inspector), WorkbenchIds.FilesPanel);
        SharedUi.Settle(window);
        Assert.Equal(WorkbenchMetrics.DefaultInspectorWidth, DrawerSurface(host).Bounds.Width);
        Assert.Same(layout.FilesPanel, OnlyLaidOutPanel(presenter));

        // The two are pinned together: opening a file in the files drawer opens the preview column on its left.
        layout.OpenTab(layout.PreviewTabs, "file:a.cs", TabKind.FilePreview, new object(), "a.cs");
        SharedUi.Settle(window);

        var previewWidth = Math.Max(WorkbenchMetrics.PreviewMinWidth, layout.PreviewWidth);
        Assert.Equal(WorkbenchMetrics.DefaultInspectorWidth + previewWidth + WorkbenchMetrics.PanelSplitterWidth, DrawerSurface(host).Bounds.Width);
        var preview = ChromeOf(presenter, layout.PreviewTabs);
        var files = ChromeOf(presenter, layout.FilesPanel);
        Assert.Equal(previewWidth, preview.Bounds.Width);
        Assert.Equal(preview.Bounds.Right + WorkbenchMetrics.PanelSplitterWidth, files.Bounds.X);
        // Both are drawer content: neither may fold to its title bar inside the drawer.
        Assert.False(preview.MinimizeVisible);
        Assert.False(files.MinimizeVisible);
        // The preview keeps its cover button here too, and pressing it widens the drawer over Main.
        Assert.True(preview.CoverVisible);
        var filesWidth = files.Bounds.Width;
        layout.TogglePreviewCoverMain();
        SharedUi.Settle(window);
        Assert.Equal(PresenterOf(host, RegionRole.Main).Bounds.X, DrawerSurface(host).Bounds.X);
        Assert.Equal(filesWidth, files.Bounds.Width);
        // It covers Main only up to the files column: the preview's title-bar tools must never end up under the file tree.
        Assert.Equal(files.Bounds.X - WorkbenchMetrics.PanelSplitterWidth, preview.Bounds.Right);
        Assert.True(preview.Bounds.Right < DrawerSurface(host).Bounds.Width);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_files_drawer_s_line_moves_the_preview_not_the_drawer() => SharedUi.RunAsync(() =>
    {
        using var layout = NewLayout();
        layout.OpenTab(layout.PreviewTabs, "file:a.cs", TabKind.FilePreview, new object(), "a.cs");
        var (window, host) = Show(layout, RegionRole.Inspector);
        var presenter = PresenterOf(host, RegionRole.Inspector);
        Click(StripOf(host, RegionRole.Inspector), WorkbenchIds.FilesPanel);
        SharedUi.Settle(window);

        var surfaceWidth = DrawerSurface(host).Bounds.Width;
        var preview = ChromeOf(presenter, layout.PreviewTabs).Bounds.Width;
        var files = ChromeOf(presenter, layout.FilesPanel).Bounds.Width;
        var splitter = presenter.GetVisualDescendants().OfType<PanelSplitter>().Single(s => s.Kind == PanelSplitterKind.Preview);
        var from = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Vector(60, 0));
        window.MouseUp(from + new Vector(60, 0), MouseButton.Left);
        SharedUi.Settle(window);

        // Dragging the line resizes the preview column; the files column gives up what the preview takes, and the drawer itself
        // keeps the width it opened at.
        Assert.Equal(preview + 60, ChromeOf(presenter, layout.PreviewTabs).Bounds.Width);
        Assert.Equal(files - 60, ChromeOf(presenter, layout.FilesPanel).Bounds.Width);
        Assert.Equal(surfaceWidth, DrawerSurface(host).Bounds.Width);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task An_open_drawer_keeps_the_line_between_the_pinned_regions() => SharedUi.RunAsync(() =>
    {
        using var layout = NewLayout();
        var (window, host) = Place(layout);
        layout.SetAutoHidden(RegionRole.Project, true);
        SharedUi.Settle(window);

        // Only Main and the Inspector are pinned now, so exactly one region line is on screen.
        var splitter = host.Children.OfType<RegionSplitter>().Single(s => s.Bounds.Width > 0);
        Assert.Equal(WorkbenchMetrics.SplitterHitWidth, splitter.Bounds.Width);
        var line = splitter.Bounds;

        Click(StripOf(host, RegionRole.Project), WorkbenchIds.ProjectPanel);
        SharedUi.Settle(window);

        // The drawer lies over the auto-hidden side, not over this gap: the line stays where it was and stays draggable.
        Assert.NotNull(layout.DrawerPanel);
        Assert.Equal(line, splitter.Bounds);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_drawer_panel_offers_no_minimize_button() => SharedUi.RunAsync(() =>
    {
        using var layout = NewLayout();
        var (window, host) = Show(layout, RegionRole.Project);
        var presenter = PresenterOf(host, RegionRole.Project);
        var strip = StripOf(host, RegionRole.Project);

        Click(strip, WorkbenchIds.ProjectPanel);
        SharedUi.Settle(window);

        // The drawer holds one panel and has no splitter to pin it back: no folding it to a bare title bar there.
        Assert.True(ChromeOf(presenter, layout.ProjectPanel).MinimizeVisible is false);

        Click(strip, WorkbenchIds.ProjectPanel);
        SharedUi.Settle(window);
        Assert.True(ChromeOf(presenter, layout.ProjectPanel).MinimizeVisible);

        window.Close();
        return Task.CompletedTask;
    });

    private static WorkbenchLayout NewLayout() =>
        new(new object(), new object(), new object(), new object(), new object(), generalContent: new object());

    /// <summary>Puts the workspace in a window and lays it out once, every region docked.</summary>
    private static (Window Window, WorkbenchHost Host) Place(WorkbenchLayout layout)
    {
        var host = new WorkbenchHost { Layout = layout };
        var window = new Window { Width = 1200, Height = 800, Content = host };
        window.Show();
        SharedUi.Settle(window);
        return (window, host);
    }

    /// <summary>Places the workspace and auto-hides <paramref name="autoHide"/> so its strip is on screen.</summary>
    private static (Window Window, WorkbenchHost Host) Show(WorkbenchLayout layout, RegionRole autoHide)
    {
        var (window, host) = Place(layout);
        layout.SetAutoHidden(autoHide, true);
        SharedUi.Settle(window);
        return (window, host);
    }

    private static AutoHideStrip StripOf(WorkbenchHost host, RegionRole role) =>
        host.Children.OfType<AutoHideStrip>().Single(strip => strip.Role == role);

    private static RegionPresenter PresenterOf(WorkbenchHost host, RegionRole role) =>
        host.Children.OfType<RegionPresenter>().Single(presenter => presenter.Role == role);

    private static PanelChrome ChromeOf(RegionPresenter presenter, PanelModel panel) =>
        presenter.Children.OfType<PanelChrome>().Single(chrome => ReferenceEquals(chrome.Model, panel));

    private static Border DrawerSurface(WorkbenchHost host) =>
        host.Children.OfType<Border>().Single(border => AutomationProperties.GetAutomationId(border)?.StartsWith("drawer-", StringComparison.Ordinal) == true);

    private static PanelModel OnlyLaidOutPanel(RegionPresenter presenter)
    {
        var laidOut = LaidOutPanels(presenter);
        Assert.Single(laidOut);
        return laidOut[0].Model!;
    }

    private static List<PanelChrome> LaidOutPanels(RegionPresenter presenter) =>
        presenter.Children.OfType<PanelChrome>().Where(chrome => chrome.Bounds.Width > 0).ToList();

    private static Border[] IndicatorBars(AutoHideStrip strip) =>
        strip.GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Width == WorkbenchMetrics.DockIndicatorWidth)
            .ToArray();

    /// <summary>Presses a strip tab the way a pointer does: the button's own click, which the strip subscribed to.</summary>
    private static void Click(AutoHideStrip strip, string panelId)
    {
        var id = "autohide-" + strip.Role + "-" + panelId;
        var button = strip.GetVisualDescendants()
            .OfType<AutoHideButton>()
            .Single(candidate => AutomationProperties.GetAutomationId(candidate) == id);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>The named token of the window's current theme; a missing key fails here instead of comparing two nulls.</summary>
    private static object Resource(Visual anchor, string key)
    {
        Assert.True(anchor.TryFindResource(key, anchor.ActualThemeVariant, out var value), "missing resource: " + key);
        Assert.NotNull(value);
        return value;
    }
}
