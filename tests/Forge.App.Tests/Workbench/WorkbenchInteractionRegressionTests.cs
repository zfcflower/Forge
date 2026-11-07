using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests.Workbench;

[Collection("forge-app-ui")]
public sealed class WorkbenchInteractionRegressionTests
{
    private const int Precision = 3;
    private const double Wide = 1800;
    private const double Bar = PanelChrome.TitleBarHeight;
    private const double Gap = WorkbenchMetrics.PanelSplitterWidth;

    [Theory]
    [InlineData(false, 50)]
    [InlineData(false, -50)]
    [InlineData(false, 150)]
    [InlineData(false, -250)]
    [InlineData(true, 50)]
    [InlineData(true, -50)]
    [InlineData(true, 150)]
    [InlineData(true, -250)]
    public Task Preview_splitter_drag_keeps_inspector_width_and_moves_the_files_edge(bool previewRight, double dx) => Ui(() =>
    {
        var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout(), Wide);
        WorkbenchUi.OpenPreviewTab(scene);
        if (previewRight)
        {
            scene.Layout.SwapRegions(RegionRole.Project, RegionRole.Inspector);
            WorkbenchUi.Settle(scene.Window);
        }

        var region = WorkbenchUi.Region(scene.Window, RegionRole.Inspector);
        var total = region.Bounds.Width;
        var main = WorkbenchUi.Region(scene.Window, RegionRole.Main).Bounds.Width;
        var before = Chrome(scene, "preview").Bounds.Width;
        var expected = Math.Clamp(
            before + (previewRight ? -dx : dx),
            WorkbenchMetrics.PreviewMinWidth,
            total - WorkbenchMetrics.SideMinWidth - Gap);
        var splitter = WorkbenchUi.Find<Control>(scene.Window, "panel-splitter-preview");
        var from = WorkbenchUi.PointIn(splitter, new Point(1, 100), scene.Window);
        scene.Window.MouseDown(from, MouseButton.Left);
        scene.Window.MouseMove(WorkbenchUi.Offset(from, dx / 2, 0));
        WorkbenchUi.Settle(scene.Window);
        Assert.Equal(total, region.Bounds.Width, Precision);
        var to = WorkbenchUi.Offset(from, dx, 0);
        scene.Window.MouseMove(to);
        scene.Window.MouseUp(to, MouseButton.Left);
        WorkbenchUi.Settle(scene.Window);
        var preview = Chrome(scene, "preview").Bounds;
        var files = Chrome(scene, "files").Bounds;
        Assert.Equal(total, region.Bounds.Width, Precision);
        Assert.Equal(total, scene.Layout.Inspector.Width, Precision);
        Assert.Equal(main, WorkbenchUi.Region(scene.Window, RegionRole.Main).Bounds.Width, Precision);
        Assert.Equal(expected, preview.Width, Precision);
        Assert.Equal(total - Gap - expected, files.Width, Precision);
        Assert.Equal(expected, scene.Layout.PreviewWidth, Precision);
        Assert.Equal(previewRight ? files.Right + Gap : preview.Right + Gap, previewRight ? preview.X : files.X, Precision);
    });

    [Fact]
    public void ResizePreviewColumn_clamps_ignores_non_finite_and_keeps_inspector_width()
    {
        var layout = WorkbenchUi.NewLayout();
        layout.OpenTab(layout.PreviewTabs, WorkbenchIds.PreviewTab, TabKind.FilePreview, new object(), "file");
        var inspector = layout.Inspector.Width;
        layout.ResizePreviewColumn(double.MaxValue);
        Assert.Equal(inspector - WorkbenchMetrics.SideMinWidth - Gap, layout.PreviewWidth, Precision);
        layout.ResizePreviewColumn(1);
        Assert.Equal(WorkbenchMetrics.PreviewMinWidth, layout.PreviewWidth, Precision);
        layout.ResizePreviewColumn(double.NaN);
        Assert.Equal(WorkbenchMetrics.PreviewMinWidth, layout.PreviewWidth, Precision);
        Assert.Equal(inspector, layout.Inspector.Width, Precision);
    }

    [Fact]
    public Task Hover_and_select_never_move_labels_tabs_or_neighbours_and_close_is_clickable() => Ui(() =>
    {
        var layout = WorkbenchUi.NewLayout();
        var scene = WorkbenchUi.Open(layout, Wide);
        foreach (var id in new[] { "a", "b", "c" })
        {
            WorkbenchUi.OpenSessionTab(layout, id);
        }

        WorkbenchUi.Settle(scene.Window);
        var before = TabGeometry(scene);
        var close = Find(scene, "tab-" + WorkbenchIds.SessionTab("a") + "-close");
        Assert.Equal(0, close.Opacity);
        foreach (var id in new[] { "a", "b", "c", "a" })
        {
            scene.Window.MouseMove(WorkbenchUi.CentreOf(WorkbenchUi.Header(scene, id), scene.Window));
            WorkbenchUi.Settle(scene.Window);
            Assert.True(WorkbenchUi.Header(scene, id).IsPointerOver);
            AssertSame(before, TabGeometry(scene));
        }

        Assert.Equal(1, close.Opacity);
        Assert.True(close.IsHitTestVisible);
        WorkbenchUi.ClickTab(scene, "b");
        scene.Window.MouseMove(WorkbenchUi.CentreOf(WorkbenchUi.Region(scene.Window, RegionRole.Inspector), scene.Window));
        WorkbenchUi.Settle(scene.Window);
        AssertSame(before, TabGeometry(scene));
        Assert.Equal(0, close.Opacity);
        WorkbenchUi.ClickCloseOfTab(scene, "a");
        Assert.Equal(new[] { "session:b", "session:c" }, WorkbenchUi.TabIdsOf(layout.MainTabs));
    });

    [Theory]
    [InlineData("project", "projects")]
    [InlineData("main", "bottom")]
    [InlineData("inspector", "files")]
    [InlineData("inspector+preview", "preview")]
    public Task Maximize_shows_only_its_panel_and_the_same_button_restores(string scenario, string target) => Ui(() =>
    {
        var (scene, role) = Build(scenario);
        var ids = IdsOf(scenario);
        var region = WorkbenchUi.Region(scene.Window, role);
        var before = ids.ToDictionary(id => id, id => Abs(Chrome(scene, id), scene.Window));
        WorkbenchUi.ClickMaximize(scene, target);
        Assert.Equal(region.Bounds.Size, Chrome(scene, target).Bounds.Size);
        foreach (var id in ids)
        {
            Assert.Equal(id == target, Chrome(scene, id).IsEffectivelyVisible);
        }

        WorkbenchUi.ClickMaximize(scene, target);
        Assert.Null(scene.Layout.GetRegion(role).MaximizedPanel);
        foreach (var id in ids)
        {
            AssertRect(before[id], Abs(Chrome(scene, id), scene.Window));
        }
    });

    [Theory]
    [InlineData("project", "projects")]
    [InlineData("project", "git-panel")]
    [InlineData("main", "main")]
    [InlineData("main", "bottom")]
    [InlineData("inspector", "files")]
    [InlineData("inspector", "inspector-tools")]
    public Task Minimize_leaves_a_title_bar_gives_space_to_the_peer_and_restores(string scenario, string target) => Ui(() =>
    {
        var (scene, role) = Build(scenario);
        var ids = IdsOf(scenario);
        var peer = ids.Single(id => id != target);
        var region = WorkbenchUi.Region(scene.Window, role);
        var before = ids.ToDictionary(id => id, id => Abs(Chrome(scene, id), scene.Window));
        ClickMinimize(scene, target);
        var shrunk = Chrome(scene, target);
        var grown = Chrome(scene, peer);
        Assert.Equal(Bar, shrunk.Bounds.Height, Precision);
        Assert.Equal(region.Bounds.Width, shrunk.Bounds.Width, Precision);
        Assert.False(Content(shrunk).IsEffectivelyVisible);
        Assert.Equal(region.Bounds.Height - Bar - Gap, grown.Bounds.Height, Precision);
        Assert.False(Find(scene, "panel-" + peer + "-minimize").IsEffectivelyVisible);
        Assert.True(Find(scene, "panel-" + target + "-minimize").IsEffectivelyVisible);
        ClickMinimize(scene, target);
        foreach (var id in ids)
        {
            AssertRect(before[id], Abs(Chrome(scene, id), scene.Window));
        }
    });

    [Fact]
    public Task Minimized_preview_becomes_a_top_bar_over_files_and_restores() => Ui(() =>
    {
        var (scene, role) = Build("inspector+preview");
        var region = Abs(WorkbenchUi.Region(scene.Window, role), scene.Window);
        var before = IdsOf("inspector+preview").ToDictionary(id => id, id => Abs(Chrome(scene, id), scene.Window));
        ClickMinimize(scene, "preview");
        var preview = Abs(Chrome(scene, "preview"), scene.Window);
        var files = Abs(Chrome(scene, "files"), scene.Window);
        Assert.Equal(Bar, preview.Height, Precision);
        Assert.Equal(region.Width, preview.Width, Precision);
        Assert.Equal(region.Width, files.Width, Precision);
        Assert.InRange(files.Y - preview.Bottom, 0, Gap + 0.001);
        Assert.Equal(region.Bottom, Abs(Chrome(scene, "inspector-tools"), scene.Window).Bottom, Precision);
        ClickMinimize(scene, "preview");
        foreach (var (id, rect) in before)
        {
            AssertRect(rect, Abs(Chrome(scene, id), scene.Window));
        }
    });

    [Fact]
    public Task Closing_the_last_peer_while_minimized_unminimizes_and_the_survivor_fills_the_region() => Ui(() =>
    {
        var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout(), Wide);
        WorkbenchUi.OpenBottomTab(scene);
        WorkbenchUi.OpenPreviewTab(scene);
        var main = WorkbenchUi.Region(scene.Window, RegionRole.Main);
        var inspector = WorkbenchUi.Region(scene.Window, RegionRole.Inspector);
        ClickMinimize(scene, "bottom");
        ClickMinimize(scene, "preview");
        scene.Layout.CloseTab(scene.Layout.FindTab(WorkbenchIds.SubAgentTab("agent-1"))!);
        scene.Layout.CloseTab(scene.Layout.FindTab(WorkbenchIds.PreviewTab)!);
        WorkbenchUi.Settle(scene.Window);
        Assert.False(scene.Layout.BottomTabs.IsMinimized);
        Assert.False(scene.Layout.PreviewTabs.IsMinimized);
        Assert.False(scene.Layout.MainTabs.CanMinimize);
        Assert.False(Find(scene, "panel-main-minimize").IsEffectivelyVisible);
        Assert.Equal(main.Bounds.Height, Chrome(scene, "main").Bounds.Height, Precision);
        Assert.Equal(inspector.Bounds.Width, Chrome(scene, "files").Bounds.Width, Precision);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Sidebar_title_bars_differ_from_content_on_both_sides_in_both_themes(bool dark, bool swapped) => Ui(() =>
    {
        var o = new object();
        var scene = WorkbenchUi.Open(new WorkbenchLayout(o, o, o, o, o, o), Wide);
        WorkbenchUi.OpenPreviewTab(scene);
        if (swapped)
        {
            scene.Layout.SwapRegions(RegionRole.Project, RegionRole.Inspector);
        }

        scene.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        WorkbenchUi.Settle(scene.Window);
        var titles = new List<Color>();
        foreach (var id in new[] { "projects", "git-panel", "files", "inspector-tools", "preview" })
        {
            var chrome = Chrome(scene, id);
            var bar = chrome.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_TitleBar");
            var title = Assert.IsAssignableFrom<ISolidColorBrush>(bar.Background).Color;
            Assert.NotEqual(Assert.IsAssignableFrom<ISolidColorBrush>(chrome.Background).Color, title);
            titles.Add(title);
        }

        Assert.Single(titles.Distinct());
    });

    [Fact]
    public Task Maximizing_a_peer_clears_minimized_and_restore_brings_back_two_normal_panels() => Ui(() =>
    {
        var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout(), Wide);
        var before = Abs(Chrome(scene, "files"), scene.Window);
        ClickMinimize(scene, "files");
        Assert.True(scene.Layout.FilesPanel.IsMinimized);
        WorkbenchUi.ClickMaximize(scene, "inspector-tools");
        Assert.False(scene.Layout.FilesPanel.IsMinimized);
        Assert.False(Chrome(scene, "files").IsEffectivelyVisible);
        WorkbenchUi.ClickMaximize(scene, "inspector-tools");
        AssertRect(before, Abs(Chrome(scene, "files"), scene.Window));
    });

    [Fact]
    public Task Minimizing_a_maximized_panel_leaves_maximize_and_shows_bar_plus_peer() => Ui(() =>
    {
        var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout(), Wide);
        var region = WorkbenchUi.Region(scene.Window, RegionRole.Inspector);
        WorkbenchUi.ClickMaximize(scene, "files");
        ClickMinimize(scene, "files");
        Assert.Null(scene.Layout.Inspector.MaximizedPanel);
        Assert.Equal(Bar, Chrome(scene, "files").Bounds.Height, Precision);
        Assert.Equal(region.Bounds.Height - Bar - Gap, Chrome(scene, "inspector-tools").Bounds.Height, Precision);
    });

    [Fact]
    public Task Floating_files_while_tools_is_minimized_unminimizes_tools_and_docking_restores_both() => Ui(() =>
    {
        var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout(), Wide);
        var region = WorkbenchUi.Region(scene.Window, RegionRole.Inspector);
        ClickMinimize(scene, "inspector-tools");
        var item = scene.Layout.FloatFilesPanel();
        WorkbenchUi.Settle(scene.Window);
        Assert.False(scene.Layout.InspectorTools.IsMinimized);
        Assert.False(scene.Layout.InspectorTools.CanMinimize);
        Assert.False(Find(scene, "panel-inspector-tools-minimize").IsEffectivelyVisible);
        Assert.Equal(region.Bounds.Height, Chrome(scene, "inspector-tools").Bounds.Height, Precision);
        scene.Layout.Dock(item);
        WorkbenchUi.Settle(scene.Window);
        Assert.True(Chrome(scene, "files").Bounds.Height > Bar);
        Assert.True(Chrome(scene, "inspector-tools").Bounds.Height > Bar);
        Assert.False(scene.Layout.FilesPanel.IsMinimized || scene.Layout.InspectorTools.IsMinimized);
    });

    [Fact]
    public Task Last_expanded_peer_has_no_minimize_button_and_cannot_be_minimized() => Ui(() =>
    {
        var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout(), Wide);
        var region = WorkbenchUi.Region(scene.Window, RegionRole.Inspector);
        ClickMinimize(scene, "files");
        Assert.False(scene.Layout.InspectorTools.CanMinimize);
        scene.Layout.ToggleMinimize(scene.Layout.InspectorTools);
        WorkbenchUi.Settle(scene.Window);
        Assert.False(scene.Layout.InspectorTools.IsMinimized);
        Assert.Equal(region.Bounds.Height - Bar - Gap, Chrome(scene, "inspector-tools").Bounds.Height, Precision);
        var project = WorkbenchUi.Region(scene.Window, RegionRole.Project);
        Assert.False(Find(scene, "panel-projects-minimize").IsEffectivelyVisible);
        Assert.Equal(project.Bounds.Height, Chrome(scene, "projects").Bounds.Height, Precision);
    });

    [Fact]
    public Task Collapsing_bottom_while_main_is_minimized_restores_main_above_the_strip() => Ui(() =>
    {
        var (scene, role) = Build("main");
        var region = WorkbenchUi.Region(scene.Window, role);
        ClickMinimize(scene, "main");
        Assert.True(scene.Layout.MainTabs.IsMinimized);
        WorkbenchUi.Click(scene.Window, WorkbenchUi.CentreOf(Find(scene, "panel-bottom-pin"), scene.Window));
        WorkbenchUi.Settle(scene.Window);
        Assert.True(scene.Layout.IsBottomCollapsed);
        Assert.False(scene.Layout.MainTabs.IsMinimized);
        Assert.Equal(region.Bounds.Height - WorkbenchMetrics.AutoHideStripWidth, Chrome(scene, "main").Bounds.Height, Precision);
    });

    private static async Task Ui(Action body)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await WorkbenchUi.RunAsync(session, body);
    }

    private static (Scene Scene, RegionRole Role) Build(string scenario)
    {
        if (scenario == "project")
        {
            var o = new object();
            return (WorkbenchUi.Open(new WorkbenchLayout(o, o, o, o, o, o), Wide), RegionRole.Project);
        }

        var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout(), Wide);
        if (scenario == "main")
        {
            WorkbenchUi.OpenBottomTab(scene);
            return (scene, RegionRole.Main);
        }

        if (scenario == "inspector+preview")
        {
            WorkbenchUi.OpenPreviewTab(scene);
        }

        return (scene, RegionRole.Inspector);
    }

    private static string[] IdsOf(string scenario) => scenario switch
    {
        "project" => new[] { "projects", "git-panel" },
        "main" => new[] { "main", "bottom" },
        "inspector+preview" => new[] { "preview", "files", "inspector-tools" },
        _ => new[] { "files", "inspector-tools" },
    };

    private static PanelChrome Chrome(Scene scene, string id) => WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(id));
    private static Control Find(Scene scene, string automationId) => WorkbenchUi.Find<Control>(scene.Window, automationId);
    private static ContentControl Content(PanelChrome chrome) =>
        chrome.GetVisualDescendants().OfType<ContentControl>().First(c => c.Name == "PART_Content");
    private static void ClickMinimize(Scene scene, string panel) =>
        WorkbenchUi.Click(scene.Window, WorkbenchUi.CentreOf(Find(scene, "panel-" + panel + "-minimize"), scene.Window));
    private static Rect Abs(Visual visual, Visual space) => new(visual.TranslatePoint(default, space) ?? default, visual.Bounds.Size);

    private static void AssertRect(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, Precision);
        Assert.Equal(expected.Y, actual.Y, Precision);
        Assert.Equal(expected.Width, actual.Width, Precision);
        Assert.Equal(expected.Height, actual.Height, Precision);
    }

    private static Dictionary<string, Rect> TabGeometry(Scene scene)
    {
        var map = new Dictionary<string, Rect>();
        foreach (var id in new[] { "a", "b", "c" })
        {
            var header = WorkbenchUi.Header(scene, id);
            var label = header.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Label");
            map[id + ":tab"] = Abs(header, scene.Window);
            map[id + ":label"] = Abs(label, scene.Window);
            map[id + ":close"] = Abs(Find(scene, "tab-" + WorkbenchIds.SessionTab(id) + "-close"), scene.Window);
        }

        return map;
    }

    private static void AssertSame(Dictionary<string, Rect> expected, Dictionary<string, Rect> actual)
    {
        foreach (var (key, rect) in expected)
        {
            AssertRect(rect, actual[key]);
        }
    }
}
