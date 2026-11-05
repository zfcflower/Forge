using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.Composition;
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
    private const double NarrowWindowWidth = 1000;
    private const int OneFloatingItem = 1;
    private const int OriginalTabIndex = 0;
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
    public async Task Docked_regions_are_separated_by_the_two_pixel_region_gap()
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
    public async Task Regions_and_gaps_fill_the_window_width_so_the_splitter_hit_areas_take_no_layout_space()
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
    public async Task Region_splitter_line_is_two_pixels_wide()
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
    public async Task Maximize_button_is_hidden_while_its_region_shows_one_panel()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var visible = await WorkbenchUi.QueryAsync(session, () =>
            WorkbenchUi.Find<Control>(WorkbenchUi.Open(WorkbenchUi.NewLayout()).Window, WorkbenchUi.MaximizeId(ProjectPanelId)).IsVisible);

        Assert.False(visible);
    }

    [Fact]
    public async Task Maximize_button_is_shown_once_its_region_shows_two_panels()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var visible = await WorkbenchUi.QueryAsync(session, () =>
            WorkbenchUi.Find<Control>(WorkbenchUi.Open(WorkbenchUi.NewLayout()).Window, WorkbenchUi.MaximizeId(FilesPanelId)).IsVisible);

        Assert.True(visible);
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
    public async Task Collapsing_the_bottom_group_leaves_a_strip_on_the_bottom_edge_of_main()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var strip = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.OpenBottomTab(scene);
            WorkbenchUi.ClickPin(scene, BottomPanelId);
            return WorkbenchUi.Find<BottomCollapsedStrip>(scene.Window, "autohide-bottom").Bounds;
        });

        Assert.Equal((WorkbenchMetrics.AutoHideStripWidth, WorkbenchUi.WindowHeight), (strip.Height, strip.Bottom));
    }

    // ---------------------------------------------------------------- maximize

    [Fact]
    public async Task Maximize_gives_the_panel_the_whole_region()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var sizes = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.ClickMaximize(scene, FilesPanelId);
            return (
                Region: WorkbenchUi.Region(scene.Window, RegionRole.Inspector).Bounds.Size,
                Panel: WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(FilesPanelId)).Bounds.Size);
        });

        Assert.Equal(sizes.Region, sizes.Panel);
    }

    [Fact]
    public async Task Maximize_hides_the_other_panels_of_the_region()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var toolsHeight = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.ClickMaximize(scene, FilesPanelId);
            return WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(ToolsPanelId)).Bounds.Height;
        });

        Assert.Equal(NoMovement, toolsHeight);
    }

    [Fact]
    public async Task Clicking_maximize_again_restores_the_other_panels()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var toolsHeight = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            WorkbenchUi.ClickMaximize(scene, FilesPanelId);
            WorkbenchUi.ClickMaximize(scene, FilesPanelId);
            return WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(ToolsPanelId)).Bounds.Height;
        });

        Assert.True(toolsHeight > NoMovement);
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

        Assert.Equal(WorkbenchUi.WindowWidth, drawerRight);
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
    public static Scene Open(WorkbenchLayout layout)
    {
        var host = new WorkbenchHost { Layout = layout };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = host };
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

    public static string MaximizeId(string panel) => "panel-" + panel + "-maximize";

    public static string FloatPanelId(string panel) => "panel-" + panel + "-float";

    public static string CoverId(string panel) => "panel-" + panel + "-cover";

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

    public static void ClickMaximize(Scene scene, string panel) =>
        Click(scene.Window, CentreOf(Find<Control>(scene.Window, MaximizeId(panel)), scene.Window));

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
