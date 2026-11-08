using System.Collections.Specialized;
using Forge.App.Workbench;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Pure-model tests of <see cref="WorkbenchLayout"/> for regions and panels: default topology, region swapping,
/// auto-hide and drawers, splitter drags, maximize, the preview group, and the bottom group. No UI is started;
/// the panel content objects are plain <see cref="object"/> instances.
/// </summary>
public sealed class WorkbenchLayoutRegionTests
{
    private const double WideWindowWidth = 1600;
    private const double TightWindowWidth = 800;
    private const double NarrowWindowWidth = 700;
    private const double WindowWidthBeforePreviewArrange = 1100;
    private const double OversizedPreviewWidth = 10000;
    private const double ChangedBottomHeight = 300;
    private const double BelowMinimumPreviewWidth = 0;
    private const double DragDelta = 100;
    private const double CollapseDragDelta = 200;
    private const double SpillDelta = 50;
    private const int LeftSplitter = 0;
    private const int RightSplitter = 1;
    private const int DragMoveCount = 200;

    private static WorkbenchLayout NewLayout() => new(new object(), new object(), new object(), new object(), new object());

    private static WorkbenchLayout NewLayoutAt(double windowWidth)
    {
        var layout = NewLayout();
        layout.SetWindowWidth(windowWidth);
        return layout;
    }

    private static TabModel OpenPreview(WorkbenchLayout layout, string id = WorkbenchIds.PreviewTab) =>
        layout.OpenTab(layout.PreviewTabs, id, TabKind.FilePreview, new object(), "file");

    private static TabModel OpenBottomTab(WorkbenchLayout layout) =>
        layout.OpenTab(layout.BottomTabs, WorkbenchIds.SubAgentTab("agent-1"), TabKind.SubAgent, new object(), "sub-agent");

    private static List<RegionRole> RolesOf(WorkbenchLayout layout) => layout.Order.Select(static region => region.Role).ToList();

    // ---------------------------------------------------------------- default topology

    [Fact]
    public void Order_is_project_main_inspector_by_default()
    {
        var layout = NewLayout();

        Assert.Equal([RegionRole.Project, RegionRole.Main, RegionRole.Inspector], RolesOf(layout));
    }

    [Fact]
    public void Project_region_holds_only_the_project_panel()
    {
        var layout = NewLayout();

        Assert.Equal([WorkbenchIds.ProjectPanel], layout.Project.Panels.Select(static panel => panel.Id));
    }

    [Fact]
    public void Main_region_holds_main_tabs_then_bottom_group()
    {
        var layout = NewLayout();

        Assert.Equal([WorkbenchIds.MainTabs, WorkbenchIds.BottomTabs], layout.Main.Panels.Select(static panel => panel.Id));
    }

    [Fact]
    public void Inspector_region_holds_preview_then_files_then_inspector_tools()
    {
        var layout = NewLayout();

        Assert.Equal(
            [WorkbenchIds.PreviewTabs, WorkbenchIds.FilesPanel, WorkbenchIds.InspectorTools],
            layout.Inspector.Panels.Select(static panel => panel.Id));
    }

    [Fact]
    public void InspectorTools_holds_todo_review_and_specs_tabs_in_order()
    {
        var layout = NewLayout();

        Assert.Equal(
            [WorkbenchIds.TodoTab, WorkbenchIds.ReviewTab, WorkbenchIds.SpecsTab],
            layout.InspectorTools.Tabs.Select(static tab => tab.Id));
    }

    [Fact]
    public void InspectorTools_preset_tabs_are_not_closable()
    {
        var layout = NewLayout();

        Assert.All(layout.InspectorTools.Tabs, static tab => Assert.False(tab.IsClosable));
    }

    [Fact]
    public void InspectorTools_starts_on_the_todo_tab()
    {
        var layout = NewLayout();

        Assert.Equal(WorkbenchIds.TodoTab, layout.InspectorTools.ActiveTab?.Id);
    }

    [Fact]
    public void Preview_group_is_hidden_by_default()
    {
        var layout = NewLayout();

        Assert.False(layout.PreviewTabs.IsVisible);
    }

    [Fact]
    public void EdgeOf_project_is_left_by_default()
    {
        var layout = NewLayout();

        Assert.Equal(RegionEdge.Left, layout.EdgeOf(RegionRole.Project));
    }

    [Fact]
    public void IsPreviewLeftOfFiles_is_true_in_the_default_order()
    {
        var layout = NewLayout();

        Assert.True(layout.IsPreviewLeftOfFiles);
    }

    [Fact]
    public void First_SetWindowWidth_gives_main_at_least_its_minimum_width()
    {
        var layout = NewLayout();

        layout.SetWindowWidth(TightWindowWidth);

        Assert.True(layout.Main.Width >= WorkbenchMetrics.MainMinWidth);
    }

    // ---------------------------------------------------------------- swapping regions

    [Fact]
    public void Swap_raises_Move_collection_changes_and_never_Reset()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        var actions = new List<NotifyCollectionChangedAction>();
        layout.Order.CollectionChanged += (_, e) => actions.Add(e.Action);

        layout.SwapRegions(RegionRole.Project, RegionRole.Main);

        Assert.NotEmpty(actions);
        Assert.All(actions, static action => Assert.Equal(NotifyCollectionChangedAction.Move, action));
    }

    [Fact]
    public void Swap_reorders_the_two_regions_and_returns_true()
    {
        var layout = NewLayoutAt(WideWindowWidth);

        bool swapped = layout.SwapRegions(RegionRole.Project, RegionRole.Inspector);

        Assert.True(swapped);
        Assert.Equal([RegionRole.Inspector, RegionRole.Main, RegionRole.Project], RolesOf(layout));
    }

    [Fact]
    public void Swap_keeps_each_region_width_with_the_region()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        layout.BeginSplitterDrag(LeftSplitter);
        layout.UpdateSplitterDrag(DragDelta);
        layout.EndSplitterDrag();
        double projectWidth = layout.Project.Width;

        layout.SwapRegions(RegionRole.Project, RegionRole.Inspector);

        Assert.Equal(projectWidth, layout.Order[^1].Width);
    }

    [Fact]
    public void Swap_is_refused_when_an_involved_region_is_auto_hidden()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        layout.SetAutoHidden(RegionRole.Project, true);

        bool swapped = layout.SwapRegions(RegionRole.Project, RegionRole.Main);

        Assert.False(swapped);
        Assert.Equal([RegionRole.Project, RegionRole.Main, RegionRole.Inspector], RolesOf(layout));
    }

    [Fact]
    public void Swap_of_two_visible_regions_is_not_blocked_by_a_hidden_third_region()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        layout.SetAutoHidden(RegionRole.Project, true);

        bool swapped = layout.SwapRegions(RegionRole.Main, RegionRole.Inspector);

        Assert.True(swapped);
    }

    [Fact]
    public void EdgeOf_follows_the_region_after_a_swap()
    {
        var layout = NewLayoutAt(WideWindowWidth);

        layout.SwapRegions(RegionRole.Project, RegionRole.Inspector);

        Assert.Equal(RegionEdge.Right, layout.EdgeOf(RegionRole.Project));
    }

    [Fact]
    public void IsPreviewLeftOfFiles_follows_the_swap_and_turns_false()
    {
        var layout = NewLayoutAt(WideWindowWidth);

        layout.SwapRegions(RegionRole.Project, RegionRole.Inspector);

        Assert.False(layout.IsPreviewLeftOfFiles);
    }

    [Fact]
    public void EdgeOf_rejects_Main_because_it_has_no_auto_hide_edge()
    {
        var layout = NewLayout();

        Assert.Throws<ArgumentException>(() => layout.EdgeOf(RegionRole.Main));
    }

    // ---------------------------------------------------------------- auto-hide and drawers

    [Fact]
    public void SetAutoHidden_true_hides_a_side_region()
    {
        var layout = NewLayoutAt(WideWindowWidth);

        layout.SetAutoHidden(RegionRole.Project, true);

        Assert.True(layout.Project.IsAutoHidden);
    }

    [Fact]
    public void SetAutoHidden_is_ignored_for_Main()
    {
        var layout = NewLayoutAt(WideWindowWidth);

        layout.SetAutoHidden(RegionRole.Main, true);

        Assert.False(layout.Main.IsAutoHidden);
    }

    [Fact]
    public void ToggleDrawer_does_nothing_for_a_docked_region()
    {
        var layout = NewLayoutAt(WideWindowWidth);

        layout.ToggleDrawer(RegionRole.Project);

        Assert.Null(layout.OpenDrawer);
    }

    [Fact]
    public void ToggleDrawer_does_nothing_for_Main()
    {
        var layout = NewLayoutAt(WideWindowWidth);

        layout.ToggleDrawer(RegionRole.Main);

        Assert.Null(layout.OpenDrawer);
    }

    [Fact]
    public void ToggleDrawer_opens_the_drawer_of_an_auto_hidden_region()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        layout.SetAutoHidden(RegionRole.Project, true);

        layout.ToggleDrawer(RegionRole.Project);

        Assert.Equal(RegionRole.Project, layout.OpenDrawer);
    }

    [Fact]
    public void Opening_one_drawer_closes_the_other()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        layout.SetAutoHidden(RegionRole.Project, true);
        layout.SetAutoHidden(RegionRole.Inspector, true);
        layout.ToggleDrawer(RegionRole.Project);

        layout.ToggleDrawer(RegionRole.Inspector);

        Assert.Equal(RegionRole.Inspector, layout.OpenDrawer);
    }

    [Fact]
    public void Pinning_back_closes_the_open_drawer()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        layout.SetAutoHidden(RegionRole.Project, true);
        layout.ToggleDrawer(RegionRole.Project);

        layout.SetAutoHidden(RegionRole.Project, false);

        Assert.Null(layout.OpenDrawer);
    }

    [Fact]
    public void Pinned_region_is_not_hidden_by_the_refit_that_follows_pinning()
    {
        // Too narrow for three docked regions: the first fit hides the inspector (the first shrink target).
        var layout = NewLayoutAt(NarrowWindowWidth);
        Assert.True(layout.Inspector.IsAutoHidden);

        layout.SetAutoHidden(RegionRole.Inspector, false);

        Assert.False(layout.Inspector.IsAutoHidden);
    }

    [Fact]
    public void Refit_keeps_the_drawer_open_over_an_auto_hidden_region()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        layout.SetAutoHidden(RegionRole.Project, true);
        layout.ToggleDrawer(RegionRole.Project);

        layout.SetWindowWidth(WideWindowWidth + DragDelta);

        Assert.Equal(RegionRole.Project, layout.OpenDrawer);
    }

    [Fact]
    public void Pinning_back_closes_the_drawer_even_before_the_first_arrange()
    {
        var layout = NewLayout();
        layout.SetAutoHidden(RegionRole.Project, true);
        layout.ToggleDrawer(RegionRole.Project);

        layout.SetAutoHidden(RegionRole.Project, false);

        Assert.Null(layout.OpenDrawer);
    }

    // ---------------------------------------------------------------- splitter drags

    [Fact]
    public void Drag_right_on_left_splitter_shrinks_main_before_inspector()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        double mainStart = layout.Main.Width;
        double inspectorStart = layout.Inspector.Width;

        layout.BeginSplitterDrag(LeftSplitter);
        layout.UpdateSplitterDrag(DragDelta);

        Assert.Equal(mainStart - DragDelta, layout.Main.Width);
        Assert.Equal(inspectorStart, layout.Inspector.Width);
    }

    [Fact]
    public void Drag_right_on_left_splitter_takes_the_overflow_from_inspector_after_main_reaches_its_minimum()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        double mainRoom = layout.Main.Width - WorkbenchMetrics.MainMinWidth;
        double inspectorStart = layout.Inspector.Width;

        layout.BeginSplitterDrag(LeftSplitter);
        layout.UpdateSplitterDrag(mainRoom + SpillDelta);

        Assert.Equal(WorkbenchMetrics.MainMinWidth, layout.Main.Width);
        Assert.Equal(inspectorStart - SpillDelta, layout.Inspector.Width);
    }

    [Fact]
    public void Drag_left_on_right_splitter_shrinks_main_before_project()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        double mainStart = layout.Main.Width;
        double projectStart = layout.Project.Width;

        layout.BeginSplitterDrag(RightSplitter);
        layout.UpdateSplitterDrag(-DragDelta);

        Assert.Equal(mainStart - DragDelta, layout.Main.Width);
        Assert.Equal(projectStart, layout.Project.Width);
    }

    [Fact]
    public void Drag_writes_region_widths_back_to_the_models_on_end()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        double projectStart = layout.Project.Width;
        double mainStart = layout.Main.Width;

        layout.BeginSplitterDrag(LeftSplitter);
        layout.UpdateSplitterDrag(DragDelta);
        layout.EndSplitterDrag();

        Assert.Equal(projectStart + DragDelta, layout.Project.Width);
        Assert.Equal(mainStart - DragDelta, layout.Main.Width);
    }

    [Fact]
    public void Updates_during_a_drag_do_not_raise_LayoutChanged()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        int raised = 0;
        layout.LayoutChanged += (_, _) => raised++;

        layout.BeginSplitterDrag(LeftSplitter);
        layout.UpdateSplitterDrag(DragDelta);
        layout.UpdateSplitterDrag(DragDelta * 2);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void EndSplitterDrag_raises_LayoutChanged_exactly_once()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        int raised = 0;
        layout.LayoutChanged += (_, _) => raised++;
        layout.BeginSplitterDrag(LeftSplitter);
        layout.UpdateSplitterDrag(DragDelta);
        layout.UpdateSplitterDrag(DragDelta * 2);

        layout.EndSplitterDrag();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void EndPanelSplitterDrag_raises_LayoutChanged()
    {
        // A panel splitter (bottom height, preview width, inspector ratio) writes through the layout setters while it
        // moves; ending the drag must still raise once, so the final state is snapshotted even when the last moves were
        // clamped to an unchanged value.
        var layout = NewLayoutAt(WideWindowWidth);
        int raised = 0;
        layout.LayoutChanged += (_, _) => raised++;

        layout.EndPanelSplitterDrag();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Dragging_a_side_past_its_minimum_auto_hides_it()
    {
        var layout = NewLayoutAt(WideWindowWidth);

        layout.BeginSplitterDrag(LeftSplitter);
        layout.UpdateSplitterDrag(-CollapseDragDelta);

        Assert.True(layout.Project.IsAutoHidden);
    }

    [Fact]
    public void Dragging_back_undoes_the_collapse_of_a_side()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        layout.BeginSplitterDrag(LeftSplitter);
        layout.UpdateSplitterDrag(-CollapseDragDelta);
        Assert.True(layout.Project.IsAutoHidden);

        layout.UpdateSplitterDrag(-DragDelta);

        Assert.False(layout.Project.IsAutoHidden);
    }

    [Fact]
    public void UpdateSplitterDrag_does_not_allocate_once_warm()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        layout.BeginSplitterDrag(LeftSplitter);
        layout.UpdateSplitterDrag(DragDelta);
        layout.UpdateSplitterDrag(-DragDelta);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int move = 0; move < DragMoveCount; move++)
        {
            layout.UpdateSplitterDrag(move % 2 == 0 ? DragDelta : -DragDelta);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated);
    }

    // ---------------------------------------------------------------- preview group

    [Fact]
    public void Opening_first_preview_tab_widens_inspector_by_PreviewWidth()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        double inspectorBefore = layout.Inspector.Width;

        OpenPreview(layout);

        Assert.Equal(inspectorBefore + layout.PreviewWidth, layout.Inspector.Width);
    }

    [Fact]
    public void Opening_first_preview_tab_takes_the_same_width_from_main()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        double mainBefore = layout.Main.Width;

        OpenPreview(layout);

        Assert.Equal(mainBefore - layout.PreviewWidth, layout.Main.Width);
    }

    [Fact]
    public void Opening_a_second_preview_tab_does_not_borrow_again()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        OpenPreview(layout);
        double inspectorAfterFirst = layout.Inspector.Width;

        OpenPreview(layout, WorkbenchIds.PinnedFileTab("b.cs"));

        Assert.Equal(inspectorAfterFirst, layout.Inspector.Width);
    }

    [Fact]
    public void Closing_last_preview_tab_returns_the_width_to_main()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        double mainBefore = layout.Main.Width;
        double inspectorBefore = layout.Inspector.Width;
        TabModel preview = OpenPreview(layout);

        layout.CloseTab(preview);

        Assert.Equal(mainBefore, layout.Main.Width);
        Assert.Equal(inspectorBefore, layout.Inspector.Width);
    }

    [Fact]
    public void Closing_last_preview_tab_hides_the_preview_group()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        TabModel preview = OpenPreview(layout);

        layout.CloseTab(preview);

        Assert.False(layout.PreviewTabs.IsVisible);
    }

    [Fact]
    public void PreviewWidth_setter_clamps_up_to_PreviewMinWidth()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        OpenPreview(layout);

        layout.PreviewWidth = BelowMinimumPreviewWidth;

        Assert.Equal(WorkbenchMetrics.PreviewMinWidth, layout.PreviewWidth);
    }

    [Fact]
    public void PreviewWidth_setter_clamps_down_to_inspector_width_minus_side_minimum_and_splitter()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        OpenPreview(layout);
        double inspectorWidth = layout.Inspector.Width;
        double largestPreview = inspectorWidth - WorkbenchMetrics.SideMinWidth - WorkbenchMetrics.PanelSplitterWidth;

        layout.PreviewWidth = OversizedPreviewWidth;

        Assert.Equal(largestPreview, layout.PreviewWidth);
    }

    [Fact]
    public void TogglePreviewCoverMain_is_ignored_while_the_preview_group_is_hidden()
    {
        var layout = NewLayoutAt(WideWindowWidth);

        layout.TogglePreviewCoverMain();

        Assert.False(layout.IsPreviewCoveringMain);
    }

    [Fact]
    public void TogglePreviewCoverMain_flips_the_cover_while_the_preview_group_is_visible()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        OpenPreview(layout);

        layout.TogglePreviewCoverMain();

        Assert.True(layout.IsPreviewCoveringMain);
    }

    [Fact]
    public void Closing_the_preview_group_clears_the_cover()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        TabModel preview = OpenPreview(layout);
        layout.TogglePreviewCoverMain();

        layout.CloseTab(preview);

        Assert.False(layout.IsPreviewCoveringMain);
    }

    [Fact]
    public void TogglePreviewCoverMain_does_not_raise_LayoutChanged()
    {
        var layout = NewLayoutAt(WideWindowWidth);
        OpenPreview(layout);
        int raised = 0;
        layout.LayoutChanged += (_, _) => raised++;

        layout.TogglePreviewCoverMain();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Preview_opened_before_the_first_arrange_leaves_main_at_least_its_minimum()
    {
        var layout = NewLayout();
        OpenPreview(layout);

        layout.SetWindowWidth(WindowWidthBeforePreviewArrange);

        Assert.True(layout.Main.Width >= WorkbenchMetrics.MainMinWidth);
    }

    // ---------------------------------------------------------------- bottom group

    [Fact]
    public void Bottom_group_is_hidden_without_tabs()
    {
        var layout = NewLayout();

        Assert.False(layout.BottomTabs.IsVisible);
    }

    [Fact]
    public void Bottom_group_reports_no_content_without_tabs()
    {
        var layout = NewLayout();

        Assert.False(layout.BottomHasContent);
    }

    [Fact]
    public void Bottom_group_stays_visible_when_empty_and_KeepsVisibleWhenEmpty_is_set()
    {
        var layout = NewLayout();

        layout.BottomTabs.KeepsVisibleWhenEmpty = true;

        Assert.True(layout.BottomTabs.IsVisible);
    }

    [Fact]
    public void Collapsed_bottom_group_is_hidden()
    {
        var layout = NewLayout();
        OpenBottomTab(layout);

        layout.IsBottomCollapsed = true;

        Assert.False(layout.BottomTabs.IsVisible);
    }

    [Fact]
    public void Collapsed_bottom_group_still_reports_content()
    {
        var layout = NewLayout();
        OpenBottomTab(layout);

        layout.IsBottomCollapsed = true;

        Assert.True(layout.BottomHasContent);
    }

    [Fact]
    public void BottomHeight_setter_raises_LayoutChanged()
    {
        var layout = NewLayout();
        int raised = 0;
        layout.LayoutChanged += (_, _) => raised++;

        layout.BottomHeight = ChangedBottomHeight;

        Assert.Equal(1, raised);
    }

    [Fact]
    public void IsBottomCollapsed_setter_raises_LayoutChanged()
    {
        var layout = NewLayout();
        int raised = 0;
        layout.LayoutChanged += (_, _) => raised++;

        layout.IsBottomCollapsed = true;

        Assert.Equal(1, raised);
    }
}
