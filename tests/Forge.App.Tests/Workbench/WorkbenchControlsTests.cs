using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Headless UI tests of the workbench controls under <c>Workbench/Controls</c>, driven by real pointer and key events:
/// region and panel geometry, title-bar buttons by capability, auto-hide strips and drawers, maximize, splitter drags,
/// region swap, tab strips and floating windows. Each test opens its own window over its own <see cref="WorkbenchLayout"/>
/// and does its arranging, pointer input and reading inside one UI dispatch, so the assertions see laid-out state.
/// </summary>
[Collection("forge-app-ui")]
public sealed class WorkbenchControlsTests
{
    private const double DragDistance = 50;
    private const double ShortDragDistance = 30;
    private const double CollapseDragDistance = 600;
    private const double NoMovement = 0;
    private const int NoFloatingItems = 0;
    private const double NoStripOffset = 0;
    private const double NarrowWindowWidth = 1040;
    private const double WideWorkbenchWidth = 1600;
    private const int OneFloatingItem = 1;
    private const int OriginalTabIndex = 0;
    private const int Precision = 3;
    private const string ProjectPanelId = "projects";
    private const string FilesPanelId = "files";
    private const string ToolsPanelId = "inspector-tools";
    private const string PreviewPanelId = "preview";
    private const string MainPanelId = "main";
    private const string BottomPanelId = "bottom";
    private const string FirstTabId = "one";
    private const string SecondTabId = "two";
    private const string ThirdTabId = "three";

    // ---------------------------------------------------------------- regions and splitters

    [Fact]
    public async Task Three_regions_render_in_default_order()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var roles = await WorkbenchUi.QueryAsync(session, () =>
            WorkbenchUi.RegionsByPosition(WorkbenchUi.Open(WorkbenchUi.NewLayout()).Window));

        Assert.Equal([RegionRole.Project, RegionRole.Main, RegionRole.Inspector], roles);
    }

    [Fact]
    public async Task Docked_regions_are_separated_by_the_one_pixel_region_gap()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var gaps = await WorkbenchUi.QueryAsync(session, () =>
        {
            var window = WorkbenchUi.Open(WorkbenchUi.NewLayout()).Window;
            var project = WorkbenchUi.Region(window, RegionRole.Project);
            var main = WorkbenchUi.Region(window, RegionRole.Main);
            var inspector = WorkbenchUi.Region(window, RegionRole.Inspector);
            return (WorkbenchUi.GapBetween(project, main), WorkbenchUi.GapBetween(main, inspector));
        });

        Assert.Equal((WorkbenchMetrics.RegionSplitterWidth, WorkbenchMetrics.RegionSplitterWidth), gaps);
    }

    [Fact]
    public async Task Regions_gaps_and_edge_strips_fill_the_window_width_so_the_splitter_hit_areas_take_no_layout_space()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var total = await WorkbenchUi.QueryAsync(session, () =>
            WorkbenchUi.TotalDockedWidth(WorkbenchUi.Open(WorkbenchUi.NewLayout()).Window));

        Assert.Equal(WorkbenchUi.WindowWidth, total);
    }

    [Fact]
    public async Task Region_splitter_hit_area_is_six_pixels_wide()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var hitWidth = await WorkbenchUi.QueryAsync(session, () =>
        {
            var window = WorkbenchUi.Open(WorkbenchUi.NewLayout()).Window;
            return WorkbenchUi.Find<RegionSplitter>(window, WorkbenchUi.RegionSplitterId(0)).Bounds.Width;
        });

        Assert.Equal(WorkbenchMetrics.SplitterHitWidth, hitWidth);
    }

    [Fact]
    public async Task Region_splitter_line_is_one_pixel_wide()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var lineWidth = await WorkbenchUi.QueryAsync(session, () =>
        {
            var window = WorkbenchUi.Open(WorkbenchUi.NewLayout()).Window;
            return WorkbenchUi.LineOf(WorkbenchUi.Find<RegionSplitter>(window, WorkbenchUi.RegionSplitterId(0))).Bounds.Width;
        });

        Assert.Equal(WorkbenchMetrics.RegionSplitterWidth, lineWidth);
    }

    [Fact]
    public async Task Panels_inside_a_region_are_separated_by_a_one_pixel_gap()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var gap = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenBottomTab(scene);
            return WorkbenchUi.GapBetween(
                WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(MainPanelId)),
                WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(BottomPanelId)),
                vertical: true);
        });

        Assert.Equal(WorkbenchMetrics.PanelSplitterWidth, gap);
    }

    [Fact]
    public async Task Panel_splitter_hit_area_is_six_pixels_high()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var hitHeight = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenBottomTab(scene);
            return WorkbenchUi.Find<PanelSplitter>(scene.Window, WorkbenchUi.BottomSplitterId).Bounds.Height;
        });

        Assert.Equal(WorkbenchMetrics.SplitterHitWidth, hitHeight);
    }

    [Fact]
    public async Task Panel_splitter_line_is_one_pixel_high()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var lineHeight = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenBottomTab(scene);
            return WorkbenchUi.LineOf(WorkbenchUi.Find<PanelSplitter>(scene.Window, WorkbenchUi.BottomSplitterId)).Bounds.Height;
        });

        Assert.Equal(WorkbenchMetrics.PanelSplitterWidth, lineHeight);
    }

    // ---------------------------------------------------------------- title-bar and tab typography

    [Fact]
    public async Task Panel_titles_are_bold_at_the_emphasis_size()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var title = await WorkbenchUi.QueryAsync(session, () =>
        {
            var window = WorkbenchUi.Open(WorkbenchUi.NewLayout()).Window;
            var chrome = WorkbenchUi.Find<PanelChrome>(window, WorkbenchUi.PanelId(ProjectPanelId));
            var block = LabelOf(chrome, "PART_Title");
            return (Weight: block.FontWeight, Size: block.FontSize, Emphasis: WorkbenchUi.FontSizeResource("FontSizeMd"));
        });

        Assert.True(title.Emphasis is not null, "FontSizeMd must be in the application resources");
        // Bold is the only emphasis above regular that both the Latin and the CJK faces of the interface chain carry
        // (Segoe UI has a 600 face, Microsoft YaHei does not), so the two scripts cannot drift to different weights.
        Assert.Equal(FontWeight.Bold, title.Weight);
        // The panel title reads the emphasis token of the appearance ramp, so it follows the interface font size.
        Assert.Equal(title.Emphasis, title.Size);
    }

    [Fact]
    public async Task A_panel_title_bar_is_as_high_as_the_title_bar_constant()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var height = await WorkbenchUi.QueryAsync(session, () =>
        {
            var window = WorkbenchUi.Open(WorkbenchUi.NewLayout()).Window;
            var chrome = WorkbenchUi.Find<PanelChrome>(window, WorkbenchUi.PanelId(ProjectPanelId));
            return chrome.GetVisualDescendants().OfType<Border>().Single(static border => border.Name == "PART_TitleBar").Bounds.Height;
        });

        Assert.Equal(PanelChrome.TitleBarHeight, height);
    }

    [Fact]
    public async Task Tab_labels_are_regular_at_the_body_size()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var tab = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenSessionTab(scene.Layout, FirstTabId);
            WorkbenchUi.Settle(scene.Window);
            var header = WorkbenchUi.Header(scene, FirstTabId);
            var label = LabelOf(header, "PART_Label");
            return (Height: header.Bounds.Height, Weight: label.FontWeight, Size: label.FontSize, Body: WorkbenchUi.FontSizeResource("FontSizeBase"));
        });

        Assert.True(tab.Body is not null, "FontSizeBase must be in the application resources");
        // A tab keeps the body size but no weight: the title bar carries the emphasis, the tick labels read as plain
        // text, and 400 is a weight both the Latin and the CJK faces really have.
        Assert.Equal(FontWeight.Normal, tab.Weight);
        // A tab label takes the body token, one step below the panel title's emphasis, so the strip reads tighter than
        // the title it sits on.
        Assert.Equal(tab.Body, tab.Size);
        // The theme's TabHeaderView height; the tab sits on the 28 px title bar's bottom line without being clipped.
        Assert.Equal(25, tab.Height);
    }

    private static TextBlock LabelOf(Visual root, string name) =>
        root.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == name);

    [Fact]
    public async Task A_floating_panel_title_is_bold_at_the_emphasis_size()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var title = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenSessionTab(scene.Layout, FirstTabId);
            WorkbenchUi.FloatTab(scene, FirstTabId);
            var floating = WorkbenchUi.FloatingWindow(scene.Window, WorkbenchIds.SessionTab(FirstTabId))
                ?? throw new InvalidOperationException("The tab did not float.");
            WorkbenchUi.Settle(floating);
            var block = LabelOf(floating, "PART_Title");
            return (Weight: block.FontWeight, Size: block.FontSize, Emphasis: WorkbenchUi.FontSizeResource("FontSizeMd"));
        });

        Assert.True(title.Emphasis is not null, "FontSizeMd must be in the application resources");
        // A floated panel draws its own title bar (FloatingPanelWindow) out of the same chrome: the same size and the
        // same bold title, so a panel looks the same docked and floating.
        Assert.Equal(FontWeight.Bold, title.Weight);
        Assert.Equal(title.Emphasis, title.Size);
    }

    // ---------------------------------------------------------------- title-bar buttons by capability

    [Fact]
    public async Task Pin_button_is_shown_on_a_side_panel()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var visible = await WorkbenchUi.QueryAsync(session, () =>
            WorkbenchUi.Find<Control>(WorkbenchUi.Open(WorkbenchUi.NewLayout()).Window, WorkbenchUi.PinId(ProjectPanelId)).IsVisible);

        Assert.True(visible);
    }

    [Fact]
    public async Task Pin_button_is_hidden_on_the_main_tab_group()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var visible = await WorkbenchUi.QueryAsync(session, () =>
            WorkbenchUi.Find<Control>(WorkbenchUi.Open(WorkbenchUi.NewLayout()).Window, WorkbenchUi.PinId(MainPanelId)).IsVisible);

        Assert.False(visible);
    }

    [Fact]
    public async Task Panel_title_bars_have_no_maximize_button()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        // The inspector shows two panels, which is the case that used to put a maximize button on both title bars.
        var found = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            return WorkbenchUi.TryFind<Control>(scene.Window, "panel-" + FilesPanelId + "-maximize") is not null
                || WorkbenchUi.TryFind<Control>(scene.Window, "panel-" + ToolsPanelId + "-maximize") is not null;
        });

        Assert.False(found);
    }

    [Fact]
    public async Task Float_button_is_hidden_on_the_project_panel()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var visible = await WorkbenchUi.QueryAsync(session, () =>
            WorkbenchUi.Find<Control>(WorkbenchUi.Open(WorkbenchUi.NewLayout()).Window, WorkbenchUi.FloatPanelId(ProjectPanelId)).IsVisible);

        Assert.False(visible);
    }

    [Fact]
    public async Task Float_button_is_shown_on_a_tab_group_with_an_active_tab()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var visible = await WorkbenchUi.QueryAsync(session, () =>
            WorkbenchUi.Find<Control>(WorkbenchUi.Open(WorkbenchUi.NewLayout()).Window, WorkbenchUi.FloatPanelId(ToolsPanelId)).IsVisible);

        Assert.True(visible);
    }

    [Fact]
    public async Task Float_button_is_shown_on_the_files_panel()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var visible = await WorkbenchUi.QueryAsync(session, () =>
            WorkbenchUi.Find<Control>(WorkbenchUi.Open(WorkbenchUi.NewLayout()).Window, WorkbenchUi.FloatPanelId(FilesPanelId)).IsVisible);

        Assert.True(visible);
    }

    [Fact]
    public async Task Cover_button_is_hidden_while_the_preview_group_is_closed()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var visible = await WorkbenchUi.QueryAsync(session, () =>
            WorkbenchUi.Find<Control>(WorkbenchUi.Open(WorkbenchUi.NewLayout()).Window, WorkbenchUi.CoverId(PreviewPanelId)).IsVisible);

        Assert.False(visible);
    }

    [Fact]
    public async Task Cover_button_appears_once_the_preview_group_opens()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var visible = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenPreviewTab(scene);
            return WorkbenchUi.Find<Control>(scene.Window, WorkbenchUi.CoverId(PreviewPanelId)).IsVisible;
        });

        Assert.True(visible);
    }

    [Fact]
    public async Task Close_button_is_hidden_while_the_preview_group_is_closed()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var visible = await WorkbenchUi.QueryAsync(session, () =>
            WorkbenchUi.Find<Control>(WorkbenchUi.Open(WorkbenchUi.NewLayout()).Window, WorkbenchUi.CloseId(PreviewPanelId)).IsVisible);

        Assert.False(visible);
    }

    [Fact]
    public async Task Close_button_appears_once_the_preview_group_opens()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var visible = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenPreviewTab(scene);
            return WorkbenchUi.Find<Control>(scene.Window, WorkbenchUi.CloseId(PreviewPanelId)).IsVisible;
        });

        Assert.True(visible);
    }

    [Fact]
    public async Task Close_button_closes_the_preview_group_and_returns_its_width_to_main()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var state = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout(), WideWorkbenchWidth);
            WorkbenchUi.OpenPreviewTab(scene);
            scene.Layout.OpenTab(scene.Layout.PreviewTabs, WorkbenchIds.ChangesTab, TabKind.Changes, new object(), "changes");
            WorkbenchUi.Settle(scene.Window);
            double mainBefore = scene.Layout.Main.Width;
            double inspectorBefore = scene.Layout.Inspector.Width;

            WorkbenchUi.Click(scene.Window, WorkbenchUi.CentreOf(
                WorkbenchUi.Find<Control>(scene.Window, WorkbenchUi.CloseId(PreviewPanelId)), scene.Window));

            return (
                Tabs: scene.Layout.PreviewTabs.Tabs.Count,
                Visible: scene.Layout.PreviewTabs.IsVisible,
                Inspector: scene.Layout.Inspector.Width,
                Main: scene.Layout.Main.Width,
                ButtonVisible: WorkbenchUi.Find<Control>(scene.Window, WorkbenchUi.CloseId(PreviewPanelId)).IsVisible,
                mainBefore,
                inspectorBefore);
        });

        Assert.Equal(NoFloatingItems, state.Tabs);
        Assert.False(state.Visible);
        Assert.False(state.ButtonVisible);
        Assert.Equal(state.inspectorBefore - WorkbenchMetrics.DefaultPreviewWidth, state.Inspector, Precision);
        Assert.Equal(state.mainBefore + WorkbenchMetrics.DefaultPreviewWidth, state.Main, Precision);
    }

    // ---------------------------------------------------------------- preview column splitter

    [Fact]
    public async Task Preview_splitter_hit_band_straddles_the_preview_files_gap_when_the_preview_is_left_of_files()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var straddles = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenPreviewTab(scene);
            var splitter = WorkbenchUi.Find<Control>(scene.Window, "panel-splitter-preview");
            var preview = WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(PreviewPanelId));
            return (
                StraddlesGap: splitter.Bounds.X < preview.Bounds.Right && splitter.Bounds.Right > preview.Bounds.Right,
                MovedOffLeftEdge: splitter.Bounds.X > preview.Bounds.X);
        });

        Assert.True(straddles.StraddlesGap);
        Assert.True(straddles.MovedOffLeftEdge);
    }

    [Fact]
    public async Task Preview_splitter_hit_band_straddles_the_preview_files_gap_when_the_preview_is_right_of_files()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var straddles = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenPreviewTab(scene);
            scene.Layout.SwapRegions(RegionRole.Project, RegionRole.Inspector);
            WorkbenchUi.Settle(scene.Window);
            var splitter = WorkbenchUi.Find<Control>(scene.Window, "panel-splitter-preview");
            var preview = WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(PreviewPanelId));
            return (
                StraddlesGap: splitter.Bounds.X < preview.Bounds.X && splitter.Bounds.Right > preview.Bounds.X,
                LeftOfPreview: splitter.Bounds.X < preview.Bounds.X);
        });

        Assert.True(straddles.StraddlesGap);
        Assert.True(straddles.LeftOfPreview);
    }

    [Fact]
    public async Task Dragging_the_preview_splitter_right_widens_the_preview_when_it_is_left_of_files()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var widths = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout(), WideWorkbenchWidth);
            WorkbenchUi.OpenPreviewTab(scene);
            double before = scene.Layout.PreviewWidth;

            WorkbenchUi.DragPreviewSplitter(scene, DragDistance);

            return (
                before,
                after: scene.Layout.PreviewWidth,
                inspector: scene.Layout.Inspector.Width,
                chrome: WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(PreviewPanelId)).Bounds.Width);
        });

        Assert.Equal(widths.before + DragDistance, widths.after, Precision);
        Assert.Equal(widths.after, widths.chrome, Precision);
        Assert.Equal(WorkbenchMetrics.DefaultInspectorWidth + WorkbenchMetrics.DefaultPreviewWidth, widths.inspector, Precision);
    }

    [Fact]
    public async Task Dragging_the_preview_splitter_right_narrows_the_preview_when_it_is_right_of_files()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var widths = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout(), WideWorkbenchWidth);
            WorkbenchUi.OpenPreviewTab(scene);
            scene.Layout.SwapRegions(RegionRole.Project, RegionRole.Inspector);
            WorkbenchUi.Settle(scene.Window);
            double before = scene.Layout.PreviewWidth;

            WorkbenchUi.DragPreviewSplitter(scene, DragDistance);

            return (
                before,
                after: scene.Layout.PreviewWidth,
                inspector: scene.Layout.Inspector.Width,
                chrome: WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(PreviewPanelId)).Bounds.Width);
        });

        Assert.Equal(widths.before - DragDistance, widths.after, Precision);
        Assert.Equal(widths.after, widths.chrome, Precision);
        Assert.Equal(WorkbenchMetrics.DefaultInspectorWidth + WorkbenchMetrics.DefaultPreviewWidth, widths.inspector, Precision);
    }

    // ---------------------------------------------------------------- covering Main

    [Fact]
    public async Task Cover_spreads_the_preview_over_main_and_its_own_column()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var cover = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenPreviewTab(scene);
            WorkbenchUi.Click(scene.Window, WorkbenchUi.CentreOf(
                WorkbenchUi.Find<Control>(scene.Window, WorkbenchUi.CoverId(PreviewPanelId)), scene.Window));

            var inspector = WorkbenchUi.Region(scene.Window, RegionRole.Inspector);
            var main = WorkbenchUi.Region(scene.Window, RegionRole.Main);
            var preview = WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(PreviewPanelId));
            var files = WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(FilesPanelId));
            var origin = inspector.TranslatePoint(new Point(0, 0), scene.Window) ?? throw new InvalidOperationException("presenter not in window");
            var coverWindow = new Rect(origin + inspector.PreviewCoverRect.Position, inspector.PreviewCoverRect.Size);
            var filesWindow = new Rect(origin + files.Bounds.Position, files.Bounds.Size);
            return (
                ChromeFillsCover: preview.Bounds == inspector.PreviewCoverRect,
                CoversMain: coverWindow.Contains(main.Bounds),
                StartsAtMain: Math.Abs(coverWindow.Left - main.Bounds.X) < 0.01,
                EndsAtFilesColumn: Math.Abs(filesWindow.X - coverWindow.Right - WorkbenchMetrics.PanelSplitterWidth) < 0.01,
                FilesStillVisible: files.Bounds.Width > 0);
        });

        Assert.True(cover.ChromeFillsCover);
        Assert.True(cover.CoversMain);
        Assert.True(cover.StartsAtMain);
        Assert.True(cover.EndsAtFilesColumn);
        Assert.True(cover.FilesStillVisible);
    }

    [Fact]
    public async Task Cover_spreads_the_preview_over_main_and_its_own_column_when_it_is_right_of_files()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var cover = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenPreviewTab(scene);
            scene.Layout.SwapRegions(RegionRole.Project, RegionRole.Inspector);
            WorkbenchUi.Settle(scene.Window);
            WorkbenchUi.Click(scene.Window, WorkbenchUi.CentreOf(
                WorkbenchUi.Find<Control>(scene.Window, WorkbenchUi.CoverId(PreviewPanelId)), scene.Window));

            var inspector = WorkbenchUi.Region(scene.Window, RegionRole.Inspector);
            var main = WorkbenchUi.Region(scene.Window, RegionRole.Main);
            var preview = WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(PreviewPanelId));
            var files = WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(FilesPanelId));
            var origin = inspector.TranslatePoint(new Point(0, 0), scene.Window) ?? throw new InvalidOperationException("presenter not in window");
            var coverWindow = new Rect(origin + inspector.PreviewCoverRect.Position, inspector.PreviewCoverRect.Size);
            var filesWindow = new Rect(origin + files.Bounds.Position, files.Bounds.Size);
            return (
                ChromeFillsCover: preview.Bounds == inspector.PreviewCoverRect,
                CoversMain: coverWindow.Contains(main.Bounds),
                EndsAtMain: Math.Abs(coverWindow.Right - main.Bounds.Right) < 0.01,
                StartsAtFilesColumn: Math.Abs(coverWindow.Left - filesWindow.Right - WorkbenchMetrics.PanelSplitterWidth) < 0.01,
                FilesStillVisible: files.Bounds.Width > 0);
        });

        Assert.True(cover.ChromeFillsCover);
        Assert.True(cover.CoversMain);
        Assert.True(cover.EndsAtMain);
        Assert.True(cover.StartsAtFilesColumn);
        Assert.True(cover.FilesStillVisible);
    }

    [Fact]
    public async Task Clicking_cover_again_returns_the_preview_to_its_column()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var restored = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenPreviewTab(scene);
            var coverButton = WorkbenchUi.Find<Control>(scene.Window, WorkbenchUi.CoverId(PreviewPanelId));
            WorkbenchUi.Click(scene.Window, WorkbenchUi.CentreOf(coverButton, scene.Window));
            WorkbenchUi.Click(scene.Window, WorkbenchUi.CentreOf(coverButton, scene.Window));

            var inspector = WorkbenchUi.Region(scene.Window, RegionRole.Inspector);
            var preview = WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(PreviewPanelId));
            var files = WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(FilesPanelId));
            return (
                CoverCleared: inspector.PreviewCoverRect == default,
                BackToColumn: preview.Bounds.Width < inspector.RegionRect.Width && files.Bounds.X > preview.Bounds.Right,
                InspectorBackToRegion: inspector.Bounds == new Rect(WorkbenchUi.WindowWidth - inspector.RegionRect.Width, 0, inspector.RegionRect.Width, WorkbenchUi.WindowHeight));
        });

        Assert.True(restored.CoverCleared);
        Assert.True(restored.BackToColumn);
        Assert.True(restored.InspectorBackToRegion);
    }

    // ---------------------------------------------------------------- auto-hide, strips and drawers

    [Fact]
    public async Task Pinning_a_side_panel_auto_hides_its_region()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var hidden = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.ClickPin(scene, ProjectPanelId);
            return scene.Layout.Project.IsAutoHidden;
        });

        Assert.True(hidden);
    }

    [Fact]
    public async Task Auto_hidden_region_leaves_a_strip_of_strip_width_on_its_window_edge()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var strip = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.ClickPin(scene, ProjectPanelId);
            return WorkbenchUi.Find<AutoHideStrip>(scene.Window, WorkbenchUi.StripId(RegionRole.Project)).Bounds;
        });

        Assert.Equal(new Rect(0, 0, WorkbenchMetrics.AutoHideStripWidth, WorkbenchUi.WindowHeight), strip);
    }

    [Fact]
    public async Task Docked_region_has_no_edge_strip_until_it_is_auto_hidden()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var bounds = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            var strip = WorkbenchUi.Find<AutoHideStrip>(scene.Window, WorkbenchUi.StripId(RegionRole.Project));
            var docked = strip.Bounds;
            WorkbenchUi.ClickPin(scene, ProjectPanelId);
            return (Docked: docked, Hidden: strip.Bounds);
        });

        Assert.Equal(default, bounds.Docked);
        Assert.Equal(WorkbenchMetrics.AutoHideStripWidth, bounds.Hidden.Width);
    }

    [Fact]
    public async Task Strip_indicator_uses_accent_when_closed_and_idle_color_when_open()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var colors = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.ClickPin(scene, ProjectPanelId);
            var strip = WorkbenchUi.Find<AutoHideStrip>(scene.Window, WorkbenchUi.StripId(RegionRole.Project));
            string closed = IndicatorColor(strip);
            WorkbenchUi.ClickStripButton(scene, RegionRole.Project, ProjectPanelId);
            string open = IndicatorColor(strip);
            return (closed, open);
        });

        Assert.Equal("#FF3A8F5C", colors.closed);
        Assert.Equal("#FFC8C8C8", colors.open);
    }

    private static string IndicatorColor(AutoHideStrip strip) =>
        strip.GetVisualDescendants().OfType<Border>()
            .Single(border => Math.Abs(border.Bounds.Width - WorkbenchMetrics.DockIndicatorWidth) < 0.1)
            .Background is SolidColorBrush brush
                ? brush.Color.ToString().ToUpperInvariant()
                : throw new InvalidOperationException("The strip indicator has no solid color.");

    [Fact]
    public async Task Strip_button_opens_the_drawer_over_main()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var open = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.ClickPin(scene, ProjectPanelId);
            WorkbenchUi.ClickStripButton(scene, RegionRole.Project, ProjectPanelId);
            return scene.Layout.OpenDrawer;
        });

        Assert.Equal(RegionRole.Project, open);
    }

    [Fact]
    public async Task Escape_closes_the_open_drawer()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var open = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.ClickPin(scene, ProjectPanelId);
            WorkbenchUi.ClickStripButton(scene, RegionRole.Project, ProjectPanelId);
            WorkbenchUi.PressEscape(scene);
            return scene.Layout.OpenDrawer;
        });

        Assert.Null(open);
    }

    [Fact]
    public async Task Press_outside_the_drawer_closes_it()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var open = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.ClickPin(scene, ProjectPanelId);
            WorkbenchUi.ClickStripButton(scene, RegionRole.Project, ProjectPanelId);
            WorkbenchUi.ClickMainCentre(scene);
            return scene.Layout.OpenDrawer;
        });

        Assert.Null(open);
    }

    [Fact]
    public async Task Pinning_from_the_drawer_docks_the_region_again()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var hidden = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.ClickPin(scene, ProjectPanelId);
            WorkbenchUi.ClickStripButton(scene, RegionRole.Project, ProjectPanelId);
            WorkbenchUi.ClickPin(scene, ProjectPanelId);
            return scene.Layout.Project.IsAutoHidden;
        });

        Assert.False(hidden);
    }

    [Fact]
    public async Task Two_auto_hidden_regions_on_the_same_edge_sit_side_by_side()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var offsets = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.SwapDrag(scene, FilesPanelId, RegionRole.Main, cancel: false);
            WorkbenchUi.ClickPin(scene, ProjectPanelId);
            WorkbenchUi.ClickPin(scene, FilesPanelId);
            return (
                Project: WorkbenchUi.Find<AutoHideStrip>(scene.Window, WorkbenchUi.StripId(RegionRole.Project)).Bounds.X,
                Inspector: WorkbenchUi.Find<AutoHideStrip>(scene.Window, WorkbenchUi.StripId(RegionRole.Inspector)).Bounds.X);
        });

        Assert.Equal((NoStripOffset, WorkbenchMetrics.AutoHideStripWidth), offsets);
    }

    [Fact]
    public async Task Auto_hide_strip_follows_its_region_to_the_new_edge_after_a_swap()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var stripX = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.SwapDrag(scene, ProjectPanelId, RegionRole.Inspector, cancel: false);
            WorkbenchUi.ClickPin(scene, ProjectPanelId);
            return WorkbenchUi.Find<AutoHideStrip>(scene.Window, WorkbenchUi.StripId(RegionRole.Project)).Bounds.X;
        });

        Assert.Equal(WorkbenchUi.WindowWidth - WorkbenchMetrics.AutoHideStripWidth, stripX);
    }

    [Fact]
    public async Task Open_drawer_overlays_main_without_resizing_it()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var overlay = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.ClickPin(scene, ProjectPanelId);
            var main = WorkbenchUi.Region(scene.Window, RegionRole.Main).Bounds;
            WorkbenchUi.ClickStripButton(scene, RegionRole.Project, ProjectPanelId);
            var mainAfter = WorkbenchUi.Region(scene.Window, RegionRole.Main).Bounds;
            var drawer = WorkbenchUi.Region(scene.Window, RegionRole.Project).Bounds;
            return (MainBefore: main, MainAfter: mainAfter, Drawer: drawer);
        });

        Assert.Equal(overlay.MainBefore, overlay.MainAfter);
        Assert.True(overlay.Drawer.X < overlay.MainBefore.Right && overlay.MainBefore.X < overlay.Drawer.Right);
    }

    [Fact]
    public async Task Empty_bottom_group_gives_its_space_back_to_main()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var mainHeight = await WorkbenchUi.QueryAsync(session, () =>
            WorkbenchUi.Find<PanelChrome>(WorkbenchUi.Open(WorkbenchUi.NewLayout()).Window, WorkbenchUi.PanelId(MainPanelId)).Bounds.Height);

        Assert.Equal(WorkbenchUi.WindowHeight, mainHeight);
    }

    [Fact]
    public async Task Minimizing_the_bottom_group_leaves_its_title_bar_on_the_bottom_edge_of_main()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var titleBar = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenBottomTab(scene);
            // The session's sub-agent toggle and the title bar's own restore button drive this one state; what shrinks is
            // the body, so the tabs and tools that get it back stay on screen.
            scene.Layout.IsBottomMinimized = true;
            WorkbenchUi.Settle(scene.Window);
            return WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(WorkbenchIds.BottomTabs)).Bounds;
        });

        Assert.Equal(PanelChrome.TitleBarHeight, titleBar.Height, 0.5);
        Assert.Equal(WorkbenchUi.WindowHeight, titleBar.Bottom, 0.5);
    }

    [Fact]
    public async Task A_stacked_region_draws_a_separator_above_the_lower_panel_title_bar()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var geometry = await WorkbenchUi.QueryAsync(session, () =>
        {
            var o = new object();
            var scene = WorkbenchUi.Open(new WorkbenchLayout(o, o, o, o, o, o));
            scene.Window.RequestedThemeVariant = ThemeVariant.Light;
            WorkbenchUi.Settle(scene.Window);
            var presenter = WorkbenchUi.Region(scene.Window, RegionRole.Project);
            var project = WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(ProjectPanelId));
            var git = WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId("git-panel"));
            var gapTop = project.Bounds.Y + project.Bounds.Height;
            var line = presenter.GetVisualChildren()
                .OfType<Border>()
                .FirstOrDefault(border => border.Bounds.Width > 0
                    && Math.Abs(border.Bounds.Height - WorkbenchMetrics.PanelSplitterWidth) < Precision
                    && Math.Abs(border.Bounds.Y - gapTop) < Precision);
            return (Gap: git.Bounds.Y - gapTop, LineY: line?.Bounds.Y,
                LineColour: line is null ? (Color?)null : Assert.IsAssignableFrom<ISolidColorBrush>(line.Background).Color);
        });

        // The gap used to be empty, so the Git panel's title bar had no top line while the inspector's had one.
        Assert.Equal(WorkbenchMetrics.PanelSplitterWidth, geometry.Gap, Precision);
        Assert.Equal(Color.Parse("#D4D4D8"), geometry.LineColour);
    }

    [Fact]
    public async Task The_bottom_group_title_bar_has_no_pin()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        // The pin's "collapse into a strip" is what 缩小/还原 already does for the tabs the user wants to keep in place.
        var visible = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenBottomTab(scene);
            return WorkbenchUi.Find<Control>(scene.Window, WorkbenchUi.PinId(BottomPanelId)).IsEffectivelyVisible;
        });

        Assert.False(visible);
    }

    [Fact]
    public async Task The_pin_shows_its_state_solid_when_docked_and_outline_when_auto_hidden()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var state = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            var pin = WorkbenchUi.Find<PanelIconButton>(scene.Window, WorkbenchUi.PinId(ProjectPanelId));
            var docked = (pin.Icon, Tip: ToolTip.GetTip(pin) as string);
            WorkbenchUi.ClickPin(scene, ProjectPanelId);
            var autoHidden = (pin.Icon, Tip: ToolTip.GetTip(pin) as string);
            return (Docked: docked, AutoHidden: autoHidden,
                SolidIcon: WorkbenchIcons.Find(scene.Window, "IconPinSolid"),
                OutlineIcon: WorkbenchIcons.Find(scene.Window, "IconPin"));
        });

        // Two states of one button, told apart by the glyph itself: the filled pin while docked, the regular one while
        // the region is auto-hidden.
        Assert.Same(state.SolidIcon, state.Docked.Icon);
        Assert.Same(state.OutlineIcon, state.AutoHidden.Icon);
        Assert.Equal(ShellStrings.AutoHide, state.Docked.Tip);
        Assert.Equal(ShellStrings.AutoHide, state.AutoHidden.Tip);
    }

    [Fact]
    public async Task Hovering_an_icon_button_paints_it_with_the_button_brush()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var colours = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            scene.Window.RequestedThemeVariant = ThemeVariant.Light;
            WorkbenchUi.Settle(scene.Window);
            var pin = WorkbenchUi.Find<Control>(scene.Window, WorkbenchUi.PinId(ProjectPanelId));
            scene.Window.MouseMove(WorkbenchUi.CentreOf(pin, scene.Window));
            WorkbenchUi.Settle(scene.Window);
            var background = pin.GetVisualDescendants().OfType<Border>().First(border => border.Name == "PART_Background");
            return Assert.IsAssignableFrom<ISolidColorBrush>(background.Background).Color;
        });

        Assert.Equal(Color.Parse("#D4D4D8"), colours);
    }

    [Fact]
    public async Task A_tool_tab_without_a_close_box_centres_its_label()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var label = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            var header = WorkbenchUi.Find<TabHeaderView>(scene.Window, "tab-" + WorkbenchIds.SpecsTab);
            var text = header.GetVisualDescendants().OfType<TextBlock>().First(block => block.Name == "PART_Label");
            var origin = text.TranslatePoint(default, header) ?? default;
            return (Closable: header.IsClosable,
                Offset: origin.X + (text.Bounds.Width / 2) - (header.Bounds.Width / 2),
                Width: header.Bounds.Width);
        });

        Assert.False(label.Closable);
        Assert.True(label.Width > 0);
        Assert.InRange(label.Offset, -1, 1);
    }

    // ---------------------------------------------------------------- splitter drags

    [Fact]
    public async Task Narrowing_the_window_shrinks_main_before_the_side_regions()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var widths = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            scene.Window.Width = NarrowWindowWidth;
            WorkbenchUi.Settle(scene.Window);
            return (Project: scene.Layout.Project.Width, Main: scene.Layout.Main.Width, Inspector: scene.Layout.Inspector.Width);
        });

        var expectedMain = NarrowWindowWidth - (2 * WorkbenchMetrics.RegionSplitterWidth) - WorkbenchMetrics.DefaultProjectWidth - WorkbenchMetrics.DefaultInspectorWidth;
        Assert.Equal((WorkbenchMetrics.DefaultProjectWidth, expectedMain, WorkbenchMetrics.DefaultInspectorWidth), widths);
    }

    [Fact]
    public async Task Dragging_the_left_splitter_right_grows_the_project_region_by_the_drag_distance()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var widths = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            var before = scene.Layout.Project.Width;
            WorkbenchUi.DragSplitter(scene, WorkbenchUi.RegionSplitterId(0), DragDistance, 0);
            return (Before: before, After: scene.Layout.Project.Width);
        });

        Assert.Equal(widths.Before + DragDistance, widths.After);
    }

    [Fact]
    public async Task Dragging_the_left_splitter_right_takes_the_width_from_main()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var widths = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            var before = scene.Layout.Main.Width;
            WorkbenchUi.DragSplitter(scene, WorkbenchUi.RegionSplitterId(0), DragDistance, 0);
            return (Before: before, After: scene.Layout.Main.Width);
        });

        Assert.Equal(widths.Before - DragDistance, widths.After);
    }

    [Fact]
    public async Task Dragging_the_left_splitter_right_leaves_the_inspector_alone_while_main_can_shrink()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var widths = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            var before = scene.Layout.Inspector.Width;
            WorkbenchUi.DragSplitter(scene, WorkbenchUi.RegionSplitterId(0), DragDistance, 0);
            return (Before: before, After: scene.Layout.Inspector.Width);
        });

        Assert.Equal(widths.Before, widths.After);
    }

    [Fact]
    public async Task Dragging_the_right_splitter_left_grows_the_inspector_region()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var widths = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            var before = scene.Layout.Inspector.Width;
            WorkbenchUi.DragSplitter(scene, WorkbenchUi.RegionSplitterId(1), -DragDistance, 0);
            return (Before: before, After: scene.Layout.Inspector.Width);
        });

        Assert.Equal(widths.Before + DragDistance, widths.After);
    }

    [Fact]
    public async Task Dragging_a_side_splitter_past_the_minimum_auto_hides_that_side_region()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var hidden = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.DragSplitter(scene, WorkbenchUi.RegionSplitterId(0), -CollapseDragDistance, 0);
            return scene.Layout.Project.IsAutoHidden;
        });

        Assert.True(hidden);
    }

    [Fact]
    public async Task Dragging_a_side_splitter_short_of_the_minimum_keeps_the_region_docked_at_its_dragged_width()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var widths = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            var before = scene.Layout.Project.Width;
            WorkbenchUi.DragSplitter(scene, WorkbenchUi.RegionSplitterId(0), -ShortDragDistance, 0);
            return (Before: before, After: scene.Layout.Project.Width);
        });

        Assert.Equal(widths.Before - ShortDragDistance, widths.After);
    }

    [Fact]
    public async Task Dragging_the_bottom_splitter_up_grows_the_bottom_group()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var heights = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenBottomTab(scene);
            var before = scene.Layout.BottomHeight;
            WorkbenchUi.DragSplitter(scene, WorkbenchUi.BottomSplitterId, 0, -DragDistance);
            return (Before: before, After: scene.Layout.BottomHeight);
        });

        Assert.Equal(heights.Before + DragDistance, heights.After);
    }

    // ---------------------------------------------------------------- region swap

    [Fact]
    public async Task Dragging_a_title_bar_across_another_region_swaps_the_two_regions()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var order = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.SwapDrag(scene, ProjectPanelId, RegionRole.Main, cancel: false);
            return WorkbenchUi.OrderOf(scene.Layout);
        });

        Assert.Equal([RegionRole.Main, RegionRole.Project, RegionRole.Inspector], order);
    }

    [Fact]
    public async Task Escape_cancels_a_region_swap_that_is_in_progress()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var order = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.SwapDrag(scene, ProjectPanelId, RegionRole.Main, cancel: true);
            return WorkbenchUi.OrderOf(scene.Layout);
        });

        Assert.Equal([RegionRole.Project, RegionRole.Main, RegionRole.Inspector], order);
    }

    [Fact]
    public async Task Swapped_regions_keep_their_own_widths()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var widths = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            var before = scene.Layout.Project.Width;
            WorkbenchUi.SwapDrag(scene, ProjectPanelId, RegionRole.Main, cancel: false);
            return (Before: before, After: scene.Layout.Project.Width);
        });

        Assert.Equal(widths.Before, widths.After);
    }

    [Fact]
    public async Task Auto_hidden_region_cannot_start_a_swap_from_its_drawer()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var order = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.ClickPin(scene, ProjectPanelId);
            WorkbenchUi.ClickStripButton(scene, RegionRole.Project, ProjectPanelId);
            WorkbenchUi.SwapDrag(scene, ProjectPanelId, RegionRole.Main, cancel: false);
            return WorkbenchUi.OrderOf(scene.Layout);
        });

        Assert.Equal([RegionRole.Project, RegionRole.Main, RegionRole.Inspector], order);
    }

    [Fact]
    public async Task Drawer_opens_on_the_edge_of_its_new_position_after_a_swap()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var drawerRight = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.SwapDrag(scene, ProjectPanelId, RegionRole.Inspector, cancel: false);
            WorkbenchUi.ClickPin(scene, ProjectPanelId);
            WorkbenchUi.ClickStripButton(scene, RegionRole.Project, ProjectPanelId);
            return WorkbenchUi.Find<Border>(scene.Window, "drawer-Project").Bounds.Right;
        });

        // The drawer opens beside the edge strip, which stays visible.
        Assert.Equal(WorkbenchUi.WindowWidth - WorkbenchMetrics.AutoHideStripWidth, drawerRight);
    }

    // ---------------------------------------------------------------- tabs

    [Fact]
    public async Task Clicking_a_tab_activates_it()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var active = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenSessionTab(scene.Layout, FirstTabId);
            WorkbenchUi.OpenSessionTab(scene.Layout, SecondTabId);
            WorkbenchUi.ClickTab(scene, FirstTabId);
            return scene.Layout.MainTabs.ActiveTab?.Id;
        });

        Assert.Equal(WorkbenchIds.SessionTab(FirstTabId), active);
    }

    [Fact]
    public async Task Close_button_on_a_tab_closes_that_tab()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var remaining = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenSessionTab(scene.Layout, FirstTabId);
            WorkbenchUi.OpenSessionTab(scene.Layout, SecondTabId);
            WorkbenchUi.ClickCloseOfTab(scene, FirstTabId);
            return WorkbenchUi.TabIdsOf(scene.Layout.MainTabs);
        });

        Assert.Equal([WorkbenchIds.SessionTab(SecondTabId)], remaining);
    }

    [Fact]
    public async Task Middle_click_on_a_tab_closes_it()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var remaining = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenSessionTab(scene.Layout, FirstTabId);
            WorkbenchUi.OpenSessionTab(scene.Layout, SecondTabId);
            WorkbenchUi.MiddleClickTab(scene, FirstTabId);
            return WorkbenchUi.TabIdsOf(scene.Layout.MainTabs);
        });

        Assert.Equal([WorkbenchIds.SessionTab(SecondTabId)], remaining);
    }

    [Fact]
    public async Task Dragging_a_tab_along_its_strip_reorders_the_group()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var order = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenSessionTab(scene.Layout, FirstTabId);
            WorkbenchUi.OpenSessionTab(scene.Layout, SecondTabId);
            WorkbenchUi.OpenSessionTab(scene.Layout, ThirdTabId);
            WorkbenchUi.DragTabAlongStrip(scene, FirstTabId, SecondTabId);
            return WorkbenchUi.TabIdsOf(scene.Layout.MainTabs);
        });

        Assert.Equal([WorkbenchIds.SessionTab(SecondTabId), WorkbenchIds.SessionTab(FirstTabId), WorkbenchIds.SessionTab(ThirdTabId)], order);
    }

    [Fact]
    public async Task Dragging_a_tab_out_of_the_strip_floats_it()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var floating = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenSessionTab(scene.Layout, FirstTabId);
            WorkbenchUi.OpenSessionTab(scene.Layout, SecondTabId);
            WorkbenchUi.DragTabOutOfStrip(scene, FirstTabId);
            return scene.Layout.Floating.Select(static item => item.Id).ToList();
        });

        Assert.Equal([WorkbenchIds.SessionTab(FirstTabId)], floating);
    }

    [Fact]
    public async Task Each_tab_header_has_its_own_context_menu_instance()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var distinct = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenSessionTab(scene.Layout, FirstTabId);
            WorkbenchUi.OpenSessionTab(scene.Layout, SecondTabId);
            return WorkbenchUi.HasDistinctContextMenus(scene, FirstTabId, SecondTabId);
        });

        Assert.True(distinct);
    }

    // ---------------------------------------------------------------- floating windows

    [Fact]
    public async Task Floating_a_tab_opens_its_own_window()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var opened = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenSessionTab(scene.Layout, FirstTabId);
            WorkbenchUi.FloatTab(scene, FirstTabId);
            return WorkbenchUi.FloatingWindow(scene.Window, WorkbenchIds.SessionTab(FirstTabId))?.IsVisible == true;
        });

        Assert.True(opened);
    }

    [Fact]
    public async Task Removing_a_floating_item_closes_its_window()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var stillOpen = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenSessionTab(scene.Layout, FirstTabId);
            WorkbenchUi.FloatTab(scene, FirstTabId);
            WorkbenchUi.DockFloating(scene, WorkbenchIds.SessionTab(FirstTabId));
            return WorkbenchUi.FloatingWindow(scene.Window, WorkbenchIds.SessionTab(FirstTabId)) is not null;
        });

        Assert.False(stillOpen);
    }

    [Fact]
    public async Task Closing_a_floating_window_docks_its_tab_back_into_its_slot()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var state = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenSessionTab(scene.Layout, FirstTabId);
            WorkbenchUi.OpenSessionTab(scene.Layout, SecondTabId);
            WorkbenchUi.FloatTab(scene, FirstTabId);
            WorkbenchUi.CloseFloatingWindow(scene, WorkbenchIds.SessionTab(FirstTabId));
            var tab = WorkbenchUi.Tab(scene.Layout, FirstTabId);
            return (
                Floating: scene.Layout.Floating.Count,
                IsFloating: tab.IsFloating,
                Index: scene.Layout.MainTabs.Tabs.IndexOf(tab),
                Active: scene.Layout.MainTabs.ActiveTab?.Id);
        });

        Assert.Equal((NoFloatingItems, false, OriginalTabIndex, WorkbenchIds.SessionTab(FirstTabId)), state);
    }

    [Fact]
    public async Task Dock_button_in_a_floating_window_docks_its_tab_back()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var floating = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenSessionTab(scene.Layout, FirstTabId);
            WorkbenchUi.FloatTab(scene, FirstTabId);
            WorkbenchUi.ClickFloatingDock(scene, WorkbenchIds.SessionTab(FirstTabId));
            return scene.Layout.Floating.Count;
        });

        Assert.Equal(NoFloatingItems, floating);
    }

    [Fact]
    public async Task Closing_the_window_during_shutdown_keeps_its_tab_floating()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var state = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenSessionTab(scene.Layout, FirstTabId);
            WorkbenchUi.OpenSessionTab(scene.Layout, SecondTabId);
            WorkbenchUi.FloatTab(scene, FirstTabId);
            scene.Layout.IsShuttingDown = true;
            WorkbenchUi.CloseFloatingWindow(scene, WorkbenchIds.SessionTab(FirstTabId));
            return (Floating: scene.Layout.Floating.Count, IsFloating: WorkbenchUi.Tab(scene.Layout, FirstTabId).IsFloating);
        });

        Assert.Equal((OneFloatingItem, true), state);
    }

    [Fact]
    public async Task Closing_a_floating_tab_closes_its_window()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var stillOpen = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenSessionTab(scene.Layout, FirstTabId);
            WorkbenchUi.OpenSessionTab(scene.Layout, SecondTabId);
            WorkbenchUi.FloatTab(scene, FirstTabId);
            scene.Layout.CloseTab(WorkbenchUi.Tab(scene.Layout, FirstTabId));
            Dispatcher.UIThread.RunJobs();
            return WorkbenchUi.FloatingWindow(scene.Window, WorkbenchIds.SessionTab(FirstTabId)) is not null;
        });

        Assert.False(stillOpen);
    }

    [Fact]
    public async Task Floating_the_files_panel_takes_the_preview_group_into_its_window()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var carriesPreview = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenPreviewTab(scene);
            scene.Layout.FloatFilesPanel();
            Dispatcher.UIThread.RunJobs();
            var floating = WorkbenchUi.FloatingWindow(scene.Window, FilesPanelId)
                ?? throw new InvalidOperationException("The files panel did not open its window.");
            return floating.GetVisualDescendants().OfType<PanelTabStrip>().Any(strip => ReferenceEquals(strip.Group, scene.Layout.PreviewTabs));
        });

        Assert.True(carriesPreview);
    }

    [Fact]
    public async Task Floating_tab_window_opens_at_its_default_size()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var size = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenSessionTab(scene.Layout, FirstTabId);
            WorkbenchUi.FloatTab(scene, FirstTabId);
            var floating = WorkbenchUi.FloatingWindow(scene.Window, WorkbenchIds.SessionTab(FirstTabId))
                ?? throw new InvalidOperationException("The tab did not open its window.");
            return (floating.Width, floating.Height);
        });

        Assert.Equal((WorkbenchMetrics.FloatingTabWidth, WorkbenchMetrics.FloatingTabHeight), size);
    }

    [Fact]
    public async Task Floating_files_window_opens_at_its_default_size()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var size = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            scene.Layout.FloatFilesPanel();
            Dispatcher.UIThread.RunJobs();
            var floating = WorkbenchUi.FloatingWindow(scene.Window, FilesPanelId)
                ?? throw new InvalidOperationException("The files panel did not open its window.");
            return (floating.Width, floating.Height);
        });

        Assert.Equal((WorkbenchMetrics.FloatingFilesWidth, WorkbenchMetrics.FloatingFilesHeight), size);
    }

    [Fact]
    public async Task Floating_files_panel_opens_its_window()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var opened = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            scene.Layout.FloatFilesPanel();
            Dispatcher.UIThread.RunJobs();
            return WorkbenchUi.FloatingWindow(scene.Window, FilesPanelId)?.IsVisible == true;
        });

        Assert.True(opened);
    }
}

/// <summary>
/// Smoke tests of the real shell window: <see cref="MainWindow"/> over the real <see cref="MainWindowViewModel"/>, built
/// from <see cref="AppComposition"/> with every data root pointed at a per-test temp directory (never the user's data).
/// </summary>
[Collection("forge-app-ui")]
public sealed class MainWindowSmokeTests
{
    private const double CompactHeightLimit = 28;
    private const double MinimumVisibleHeight = 1;

    [Fact]
    public async Task Main_window_exposes_the_workbench_host_and_its_three_regions()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await using var shell = await ShellFixture.OpenAsync(session);

        var found = await WorkbenchUi.QueryAsync(session, () => ShellFixture.FoundWorkbenchIds(shell.Window));

        Assert.Equal(
            ["workbench-host", "region-Project", "region-Main", "region-Inspector"],
            found);
    }

    [Fact]
    public async Task Finished_subagents_menu_is_at_most_28_pixels_high_with_a_finished_sub_agent()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await using var shell = await ShellFixture.OpenAsync(session);
        await WorkbenchUi.RunAsync(session, () => shell.ShowFinishedSubAgent());

        var height = await WorkbenchUi.QueryAsync(session, () => WorkbenchUi.Find<Button>(shell.Window, "finished-subagents-menu").Bounds.Height);

        Assert.InRange(height, MinimumVisibleHeight, CompactHeightLimit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Window_header_uses_the_same_chrome_colour_as_a_panel_title_bar(bool dark)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await using var shell = await ShellFixture.OpenAsync(session);

        var colours = await WorkbenchUi.QueryAsync(session, () =>
        {
            shell.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            WorkbenchUi.Settle(shell.Window);
            var header = shell.Window.GetVisualDescendants().OfType<Border>().First(border => border.Name == "TitleBar");
            var chrome = WorkbenchUi.Find<PanelChrome>(shell.Window, WorkbenchUi.PanelId("main"));
            var bar = chrome.GetVisualDescendants().OfType<Border>().First(border => border.Name == "PART_TitleBar");
            return (Header: Colour(header), Bar: Colour(bar));
        });

        Assert.Equal(colours.Bar, colours.Header);
    }

    [Fact]
    public async Task Window_header_replaces_the_search_box_with_the_magnifier_and_the_host_state()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await using var shell = await ShellFixture.OpenAsync(session);

        var header = await WorkbenchUi.QueryAsync(session, () =>
        {
            var terminal = WorkbenchUi.Find<Button>(shell.Window, "terminal-open");
            var settings = WorkbenchUi.Find<Button>(shell.Window, "open-settings");
            var host = WorkbenchUi.Find<StackPanel>(shell.Window, "host-state");
            var titleRow = shell.Window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "TitleBar");
            var dots = shell.Window.GetVisualDescendants().OfType<Ellipse>().Where(e => e.Classes.Contains("status-dot")).ToList();
            return (
                HasTerminal: terminal.IsEffectivelyVisible,
                TerminalLeftOfSettings: terminal.Bounds.X < settings.Bounds.X,
                SameRow: ReferenceEquals(terminal.Parent, settings.Parent) && titleRow.IsVisualAncestorOf(terminal),
                SearchBox: WorkbenchUi.TryFind<TextBox>(shell.Window, "shell-search") is not null,
                Magnifier: WorkbenchUi.TryFind<FluentIcon>(shell.Window, "shell-search") is not null,
                HostLabel: host.IsEffectivelyVisible ? TextOf(host) : null,
                RightAligned: (host.TranslatePoint(default, titleRow) is { } origin)
                    && origin.X + host.Bounds.Width > titleRow.Bounds.Width / 2,
                DotColours: dots.Select(dot => Assert.IsAssignableFrom<ISolidColorBrush>(dot.Fill).Color).Distinct().Count());
        });

        Assert.True(header.HasTerminal);
        Assert.True(header.TerminalLeftOfSettings);
        Assert.True(header.SameRow);
        Assert.False(header.SearchBox);
        Assert.True(header.Magnifier);
        Assert.Equal(Loc.Instance[ShellStrings.HostNameKey], header.HostLabel);
        Assert.True(header.RightAligned);
        // The header dot and the status bar dot both read the same host state, so they must paint the same colour.
        Assert.Equal(1, header.DotColours);
    }

    private static string TextOf(StackPanel panel) =>
        string.Concat(panel.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text));

    private static Color Colour(Border border) => Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color;
}

/// <summary>Shared UI-thread helpers: opening windows, finding automation ids and driving pointer and key events.</summary>
internal static class WorkbenchUi
{
    public const double WindowWidth = 1200;
    public const double WindowHeight = 800;

    /// <summary>One pixel inside a tab's leading edge: a pointer there lies in that tab's slot.</summary>
    public const double InsideEdge = 1;

    /// <summary>How far past the float-out distance a tab is dropped, so the drop is unambiguous.</summary>
    public const double FloatOutMargin = 10;

    private static readonly TimeSpan StepBudget = TimeSpan.FromSeconds(5);
    private const int SettlePasses = 2;

    /// <summary>Every dispatch expires after the step budget, so a stuck UI step fails instead of hanging the run.</summary>
    public static CancellationToken Budget() => new CancellationTokenSource(StepBudget).Token;

    public static Task RunAsync(HeadlessUnitTestSession session, Action action) =>
        session.Dispatch(() => { action(); return 0; }, Budget());

    public static Task<T> QueryAsync<T>(HeadlessUnitTestSession session, Func<T> query) =>
        session.Dispatch(query, Budget());

    public static WorkbenchLayout NewLayout() => new(new object(), new object(), new object(), new object(), new object());

    /// <summary>A window with the host as its content, shown and laid out once.</summary>
    public static Scene Open(WorkbenchLayout layout, double width = WindowWidth)
    {
        var host = new WorkbenchHost { Layout = layout };
        var window = new Window { Width = width, Height = WindowHeight, Content = host };
        window.Show();
        Settle(window);
        return new Scene(window, host, layout);
    }

    /// <summary>
    /// Drains the dispatcher and renders, twice: a layout change can post follow-up work (the drawer's z-order, a pending
    /// arrange) that only exists after the first pass. Headless rendering runs only when this is called, so nothing waits.
    /// </summary>
    public static void Settle(Window window)
    {
        for (var pass = 0; pass < SettlePasses; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    public static string RegionSplitterId(int index) => "region-splitter-" + index;

    public const string BottomSplitterId = "panel-splitter-bottom";

    public static string PanelId(string panel) => "panel-" + panel;

    public static string PinId(string panel) => "panel-" + panel + "-pin";

    public static string FloatPanelId(string panel) => "panel-" + panel + "-float";

    public static string CoverId(string panel) => "panel-" + panel + "-cover";

    public static string CloseId(string panel) => "panel-" + panel + "-close";

    public static string StripId(RegionRole role) => "autohide-" + role;

    public static T? TryFind<T>(Visual root, string automationId) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == automationId);

    public static T Find<T>(Visual root, string automationId) where T : Visual =>
        TryFind<T>(root, automationId)
        ?? throw new InvalidOperationException("No " + typeof(T).Name + " with AutomationId '" + automationId + "'.");

    public static RegionPresenter Region(Window window, RegionRole role) => Find<RegionPresenter>(window, "region-" + role);

    public static IReadOnlyList<RegionRole> RegionsByPosition(Window window) =>
        window.GetVisualDescendants().OfType<RegionPresenter>()
            .Where(static presenter => presenter.Bounds.Width > 0)
            .OrderBy(static presenter => presenter.Bounds.X)
            .Select(static presenter => presenter.Role)
            .ToList();

    public static double TotalDockedWidth(Window window)
    {
        var regions = window.GetVisualDescendants().OfType<RegionPresenter>().Where(static presenter => presenter.Bounds.Width > 0).ToList();
        var widths = regions.Sum(static presenter => presenter.Bounds.Width);
        var gaps = Math.Max(0, regions.Count - 1) * WorkbenchMetrics.RegionSplitterWidth;
        return widths + gaps;
    }

    /// <summary>The visible line inside a splitter's hit band (the template's <c>PART_Line</c> border).</summary>
    public static Border LineOf(Control splitter) =>
        splitter.GetVisualDescendants().OfType<Border>().Single(static border => border.Name == "PART_Line");

    /// <summary>
    /// A size token of the running application's resources (<c>Tokens.axaml</c> or the appearance applier's child), so a
    /// test compares a control's font size against the token it reads instead of a copied number. UI thread only.
    /// </summary>
    public static double? FontSizeResource(string key) =>
        Application.Current!.Resources.TryGetResource(key, null, out var value) ? value as double? : null;

    /// <summary>Distance between two controls: horizontal by default, vertical when <paramref name="vertical"/> is set.</summary>
    public static double GapBetween(Visual first, Visual second, bool vertical = false) => vertical
        ? second.Bounds.Y - first.Bounds.Bottom
        : second.Bounds.X - first.Bounds.Right;

    public static Point CentreOf(Visual target, Visual space) =>
        target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), space)
        ?? throw new InvalidOperationException("Control is not in the same visual tree as the target space.");

    public static Point PointIn(Visual target, Point local, Visual space) =>
        target.TranslatePoint(local, space)
        ?? throw new InvalidOperationException("Control is not in the same visual tree as the target space.");

    public static Point Offset(Point point, double dx, double dy) => new(point.X + dx, point.Y + dy);

    public static void Click(Window window, Point at)
    {
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Settle(window);
    }

    public static void ClickPin(Scene scene, string panel) =>
        Click(scene.Window, CentreOf(Find<Control>(scene.Window, PinId(panel)), scene.Window));

    public static void ClickStripButton(Scene scene, RegionRole role, string panel) =>
        Click(scene.Window, CentreOf(Find<AutoHideButton>(scene.Window, "autohide-" + role + "-" + panel), scene.Window));

    public static void ClickMainCentre(Scene scene) =>
        Click(scene.Window, CentreOf(Region(scene.Window, RegionRole.Main), scene.Window));

    public static void PressEscape(Scene scene)
    {
        scene.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle(scene.Window);
    }

    /// <summary>A real left-button drag from the centre of a splitter by the given offsets.</summary>
    public static void DragSplitter(Scene scene, string splitterId, double dx, double dy)
    {
        var splitter = Find<Control>(scene.Window, splitterId);
        var from = CentreOf(splitter, scene.Window);
        var to = Offset(from, dx, dy);
        scene.Window.MouseDown(from, MouseButton.Left);
        scene.Window.MouseMove(to);
        scene.Window.MouseUp(to, MouseButton.Left);
        Settle(scene.Window);
    }

    /// <summary>
    /// Drags the preview splitter from a point inside its hit band that is clear of the files/tools splitter corner: the
    /// two bands overlap in a few pixels around the files panel's bottom edge, where the later splitter wins the hit test.
    /// </summary>
    public static void DragPreviewSplitter(Scene scene, double dx)
    {
        var splitter = Find<Control>(scene.Window, "panel-splitter-preview");
        var from = PointIn(splitter, new Point(1, 100), scene.Window);
        var to = Offset(from, dx, 0);
        scene.Window.MouseDown(from, MouseButton.Left);
        scene.Window.MouseMove(to);
        scene.Window.MouseUp(to, MouseButton.Left);
        Settle(scene.Window);
    }

    /// <summary>Drags a panel title bar's blank space across to the centre of <paramref name="target"/>; optionally cancels with Escape.</summary>
    public static void SwapDrag(Scene scene, string panel, RegionRole target, bool cancel)
    {
        var chrome = Find<PanelChrome>(scene.Window, PanelId(panel));
        var from = PointIn(chrome, new Point(chrome.Bounds.Width / 2, PanelChrome.TitleBarHeight / 2), scene.Window);
        var to = CentreOf(Region(scene.Window, target), scene.Window);
        scene.Window.MouseDown(from, MouseButton.Left);
        scene.Window.MouseMove(to);
        if (cancel)
        {
            PressEscape(scene);
        }

        scene.Window.MouseUp(to, MouseButton.Left);
        Settle(scene.Window);
    }

    public static IReadOnlyList<RegionRole> OrderOf(WorkbenchLayout layout) =>
        layout.Order.Select(static region => region.Role).ToList();

    public static void OpenBottomTab(Scene scene)
    {
        scene.Layout.OpenTab(scene.Layout.BottomTabs, WorkbenchIds.SubAgentTab("agent-1"), TabKind.SubAgent, new object(), "agent-1");
        Settle(scene.Window);
    }

    public static void OpenPreviewTab(Scene scene)
    {
        scene.Layout.OpenTab(scene.Layout.PreviewTabs, WorkbenchIds.PreviewTab, TabKind.FilePreview, new object(), "file");
        Settle(scene.Window);
    }

    public static TabModel OpenSessionTab(WorkbenchLayout layout, string id) =>
        layout.OpenTab(layout.MainTabs, WorkbenchIds.SessionTab(id), TabKind.Session, new object(), "Session " + id);

    public static TabModel Tab(WorkbenchLayout layout, string id) =>
        layout.FindTab(WorkbenchIds.SessionTab(id)) ?? throw new InvalidOperationException("No tab " + id + ".");

    public static IReadOnlyList<string> TabIdsOf(TabGroupModel group) => group.Tabs.Select(static tab => tab.Id).ToList();

    public static TabHeaderView Header(Scene scene, string id) =>
        Find<TabHeaderView>(scene.Window, "tab-" + WorkbenchIds.SessionTab(id));

    public static void ClickTab(Scene scene, string id)
    {
        Settle(scene.Window);
        Click(scene.Window, CentreOf(Header(scene, id), scene.Window));
    }

    public static void ClickCloseOfTab(Scene scene, string id)
    {
        Settle(scene.Window);
        var header = Header(scene, id);
        scene.Window.MouseMove(CentreOf(header, scene.Window));
        Settle(scene.Window);
        var close = Find<Control>(scene.Window, "tab-" + WorkbenchIds.SessionTab(id) + "-close");
        Click(scene.Window, CentreOf(close, scene.Window));
    }

    public static void MiddleClickTab(Scene scene, string id)
    {
        Settle(scene.Window);
        var at = CentreOf(Header(scene, id), scene.Window);
        scene.Window.MouseDown(at, MouseButton.Middle);
        scene.Window.MouseUp(at, MouseButton.Middle);
        Settle(scene.Window);
    }

    /// <summary>Presses on the first tab and moves onto the leading edge of the second, which reorders them.</summary>
    public static void DragTabAlongStrip(Scene scene, string draggedId, string overId)
    {
        Settle(scene.Window);
        var dragged = Header(scene, draggedId);
        var over = Header(scene, overId);
        var from = CentreOf(dragged, scene.Window);
        var to = PointIn(over, new Point(InsideEdge, over.Bounds.Height / 2), scene.Window);
        scene.Window.MouseDown(from, MouseButton.Left);
        scene.Window.MouseMove(to);
        scene.Window.MouseUp(to, MouseButton.Left);
        Settle(scene.Window);
    }

    /// <summary>Presses on a tab and drops it well below its strip, past the float-out distance.</summary>
    public static void DragTabOutOfStrip(Scene scene, string id)
    {
        Settle(scene.Window);
        var header = Header(scene, id);
        var strip = header.FindAncestorOfType<PanelTabStrip>() ?? throw new InvalidOperationException("Header is not inside a tab strip.");
        var from = CentreOf(header, scene.Window);
        var to = PointIn(strip, new Point(strip.Bounds.Width / 2, strip.Bounds.Height + WorkbenchMetrics.TabFloatOutDistance + FloatOutMargin), scene.Window);
        scene.Window.MouseDown(from, MouseButton.Left);
        scene.Window.MouseMove(to);
        scene.Window.MouseUp(to, MouseButton.Left);
        Settle(scene.Window);
    }

    public static bool HasDistinctContextMenus(Scene scene, string first, string second)
    {
        var a = Header(scene, first).ContextMenu;
        var b = Header(scene, second).ContextMenu;
        return a is not null && b is not null && !ReferenceEquals(a, b);
    }

    public static void FloatTab(Scene scene, string id)
    {
        scene.Layout.FloatTab(Tab(scene.Layout, id));
        Settle(scene.Window);
    }

    public static Window? FloatingWindow(Window owner, string itemId)
    {
        var expected = "floating-" + itemId;
        return owner.OwnedWindows.FirstOrDefault(window => AutomationProperties.GetAutomationId(window) == expected);
    }

    public static void DockFloating(Scene scene, string itemId)
    {
        var item = scene.Layout.Floating.Single(entry => entry.Id == itemId);
        scene.Layout.Dock(item);
        Settle(scene.Window);
    }

    /// <summary>Closes a floating window the way the title-bar close box does: the window's own close path.</summary>
    public static void CloseFloatingWindow(Scene scene, string itemId)
    {
        var window = FloatingWindow(scene.Window, itemId) ?? throw new InvalidOperationException("No floating window for " + itemId + ".");
        window.Close();
        Settle(scene.Window);
    }

    public static void ClickFloatingDock(Scene scene, string itemId)
    {
        var window = FloatingWindow(scene.Window, itemId) ?? throw new InvalidOperationException("No floating window for " + itemId + ".");
        Settle(window);
        Click(window, CentreOf(Find<Control>(window, "floating-" + itemId + "-dock"), window));
    }
}

/// <summary>A workbench host in a window, with the layout it renders; the window is shown and laid out.</summary>
internal sealed record Scene(Window Window, WorkbenchHost Host, WorkbenchLayout Layout);

/// <summary>
/// The real shell: an <see cref="AppComposition"/> rooted in a temp directory (the data, general-workspace and workspace
/// variables are set for its lifetime and restored afterwards), its view model, and the <see cref="MainWindow"/> shown over it.
/// </summary>
internal sealed class ShellFixture : IAsyncDisposable
{
    private const string DataVariable = "FORGE_DATA_DIR";
    private const string GeneralVariable = "FORGE_GENERAL_WORKSPACE";
    private const string WorkspaceVariable = "FORGE_WORKSPACE";

    private readonly string root;
    private readonly string? savedData;
    private readonly string? savedGeneral;
    private readonly string? savedWorkspace;
    private readonly HeadlessUnitTestSession session;
    private readonly AppComposition composition;
    private readonly MainWindowViewModel viewModel;

    private ShellFixture(
        HeadlessUnitTestSession session,
        string root,
        (string? Data, string? General, string? Workspace) saved,
        AppComposition composition,
        MainWindowViewModel viewModel,
        MainWindow window)
    {
        this.session = session;
        this.root = root;
        savedData = saved.Data;
        savedGeneral = saved.General;
        savedWorkspace = saved.Workspace;
        this.composition = composition;
        this.viewModel = viewModel;
        Window = window;
    }

    public MainWindow Window { get; }

    public static async Task<ShellFixture> OpenAsync(HeadlessUnitTestSession session)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-shell-smoke-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var saved = (
            Data: Environment.GetEnvironmentVariable(DataVariable),
            General: Environment.GetEnvironmentVariable(GeneralVariable),
            Workspace: Environment.GetEnvironmentVariable(WorkspaceVariable));
        Environment.SetEnvironmentVariable(DataVariable, Path.Combine(root, "data"));
        Environment.SetEnvironmentVariable(GeneralVariable, Path.Combine(root, "general"));
        Environment.SetEnvironmentVariable(WorkspaceVariable, workspace);

        try
        {
            var (composition, viewModel, window) = await WorkbenchUi.QueryAsync(session, () =>
            {
                var composed = AppComposition.Create();
                var model = new MainWindowViewModel(composed);
                var shell = new MainWindow { DataContext = model };
                shell.Show();
                WorkbenchUi.Settle(shell);
                return (composed, model, shell);
            });
            return new ShellFixture(session, root, saved, composition, viewModel, window);
        }
        catch (Exception)
        {
            RestoreEnvironment(saved);
            DeleteRoot(root);
            throw;
        }
    }

    public static IReadOnlyList<string> FoundWorkbenchIds(MainWindow window) =>
        new[] { "workbench-host", "region-Project", "region-Main", "region-Inspector" }
            .Where(id => WorkbenchUi.TryFind<Visual>(window, id) is not null)
            .ToList();

    /// <summary>
    /// Puts one finished sub-agent into the bottom group's finished menu and keeps the group in the layout for it, the
    /// state the view model reaches once a session has finished sub-agents.
    /// </summary>
    public void ShowFinishedSubAgent()
    {
        var menu = viewModel.Workbench.BottomTabs.HeaderTools.OfType<FinishedSubAgentsViewModel>().Single();
        var finished = new SubAgentViewModel("session-1", "agent-1", "child-1", "worker", "task", "model", null);
        menu.Bind([finished], static _ => { });
        viewModel.Workbench.BottomTabs.KeepsVisibleWhenEmpty = true;
        WorkbenchUi.Settle(Window);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await WorkbenchUi.RunAsync(session, () => Window.Close());
            await WorkbenchUi.RunAsync(session, () => viewModel.Dispose());
            await composition.DisposeAsync();
        }
        finally
        {
            RestoreEnvironment((savedData, savedGeneral, savedWorkspace));
            DeleteRoot(root);
        }
    }

    private static void RestoreEnvironment((string? Data, string? General, string? Workspace) saved)
    {
        Environment.SetEnvironmentVariable(DataVariable, saved.Data);
        Environment.SetEnvironmentVariable(GeneralVariable, saved.General);
        Environment.SetEnvironmentVariable(WorkspaceVariable, saved.Workspace);
    }

    private static void DeleteRoot(string root)
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
