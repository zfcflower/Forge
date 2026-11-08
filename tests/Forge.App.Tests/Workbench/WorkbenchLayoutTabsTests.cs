using Forge.App.Workbench;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Pins the tab, floating and change-notification rules of <see cref="WorkbenchLayout"/> (forge-app spec, "工作台布局"
/// and "浮动窗口"; task-workbench-rewrite "新窗口"). Pure model tests: no window, no control tree.
/// </summary>
public sealed class WorkbenchLayoutTabsTests
{
    // Wide enough that Main never reaches its minimum, so width assertions see only the rule under test.
    private const double WideWindowWidth = 2000;
    private const string First = "first";
    private const string Second = "second";
    private const string Third = "third";
    private const string Fourth = "fourth";
    private const string SubAgentId = "1";
    private const double OtherSplitRatio = 0.6;
    private const double MovedX = 120;
    private const double MovedY = 80;
    private const double MovedWidth = 700;
    private const double MovedHeight = 500;

    [Fact]
    public void Reopening_an_existing_id_does_not_add_a_second_tab()
    {
        var layout = NewLayout();
        var original = Open(layout, First);
        Open(layout, Second);

        var reopened = Open(layout, First);

        Assert.Same(original, reopened);
        Assert.Equal(new[] { First, Second }, IdsOf(layout.MainTabs));
    }

    [Fact]
    public void Reopening_an_existing_id_rebinds_content_and_title()
    {
        var layout = NewLayout();
        var tab = layout.OpenTab(layout.MainTabs, First, TabKind.Session, "old content", "old title");

        layout.OpenTab(layout.MainTabs, First, TabKind.Session, "new content", "new title");

        Assert.Equal("new content", tab.Content);
        Assert.Equal("new title", tab.Title);
    }

    [Fact]
    public void Reopening_an_existing_id_activates_that_tab()
    {
        var layout = NewLayout();
        var first = Open(layout, First);
        Open(layout, Second);
        Assert.NotSame(first, layout.MainTabs.ActiveTab);

        Open(layout, First);

        Assert.Same(first, layout.MainTabs.ActiveTab);
    }

    [Fact]
    public void Closing_active_tab_activates_right_neighbour_first()
    {
        var layout = NewLayout();
        Open(layout, First);
        var middle = Open(layout, Second);
        var right = Open(layout, Third);
        layout.ActivateTab(middle);

        layout.CloseTab(middle);

        Assert.Same(right, layout.MainTabs.ActiveTab);
    }

    [Fact]
    public void Closing_last_active_tab_activates_left_neighbour()
    {
        var layout = NewLayout();
        Open(layout, First);
        Open(layout, Second);
        var last = Open(layout, Third);
        Assert.Same(last, layout.MainTabs.ActiveTab);

        layout.CloseTab(last);

        Assert.Equal(Second, layout.MainTabs.ActiveTab?.Id);
    }

    [Fact]
    public void Closing_active_tab_skips_floating_right_neighbour()
    {
        var layout = NewLayout();
        Open(layout, First);
        var active = Open(layout, Second);
        var floating = Open(layout, Third);
        var beyond = Open(layout, Fourth);
        layout.ActivateTab(active);
        layout.FloatTab(floating);

        layout.CloseTab(active);

        Assert.Same(beyond, layout.MainTabs.ActiveTab);
    }

    [Fact]
    public void Move_reorders_tabs_within_the_group()
    {
        var layout = NewLayout();
        Open(layout, First);
        Open(layout, Second);
        Open(layout, Third);

        layout.MoveTab(layout.MainTabs, 0, 2);

        Assert.Equal(new[] { Second, Third, First }, IdsOf(layout.MainTabs));
    }

    [Fact]
    public void Move_clamps_target_index_to_the_last_tab()
    {
        var layout = NewLayout();
        Open(layout, First);
        Open(layout, Second);
        Open(layout, Third);
        int beyondEnd = layout.MainTabs.Tabs.Count + 10;

        layout.MoveTab(layout.MainTabs, 0, beyondEnd);

        Assert.Equal(new[] { Second, Third, First }, IdsOf(layout.MainTabs));
    }

    [Fact]
    public void Move_clamps_negative_target_index_to_the_first_tab()
    {
        var layout = NewLayout();
        Open(layout, First);
        Open(layout, Second);
        Open(layout, Third);
        int beforeStart = -5;

        layout.MoveTab(layout.MainTabs, 2, beforeStart);

        Assert.Equal(new[] { Third, First, Second }, IdsOf(layout.MainTabs));
    }

    [Fact]
    public void Move_leaves_tabs_of_other_groups_untouched()
    {
        var layout = NewLayout();
        Open(layout, First);
        Open(layout, Second);
        var bottom = layout.OpenTab(layout.BottomTabs, WorkbenchIds.SubAgentTab(SubAgentId), TabKind.SubAgent, "bottom", "bottom");

        layout.MoveTab(layout.MainTabs, 0, 1);

        Assert.Same(bottom, Assert.Single(layout.BottomTabs.Tabs));
        Assert.Same(layout.BottomTabs, bottom.Group);
    }

    [Fact]
    public void Floating_the_active_tab_activates_its_right_neighbour()
    {
        var layout = NewLayout();
        Open(layout, First);
        var floating = Open(layout, Second);
        var right = Open(layout, Third);
        layout.ActivateTab(floating);

        layout.FloatTab(floating);

        Assert.Same(right, layout.MainTabs.ActiveTab);
    }

    [Fact]
    public void Floating_the_last_active_tab_activates_its_left_neighbour()
    {
        var layout = NewLayout();
        Open(layout, First);
        Open(layout, Second);
        var last = Open(layout, Third);
        Assert.Same(last, layout.MainTabs.ActiveTab);

        layout.FloatTab(last);

        Assert.Equal(Second, layout.MainTabs.ActiveTab?.Id);
    }

    [Fact]
    public void Active_tab_is_never_a_floating_tab()
    {
        var layout = NewLayout();
        Open(layout, First);
        var floating = Open(layout, Second);
        Open(layout, Third);
        layout.ActivateTab(floating);

        layout.FloatTab(floating);

        Assert.False(layout.MainTabs.ActiveTab is { IsFloating: true });
    }

    [Fact]
    public void Activating_a_floating_tab_leaves_the_active_tab_unchanged()
    {
        var layout = NewLayout();
        var active = Open(layout, First);
        var floating = Open(layout, Second);
        layout.ActivateTab(active);
        layout.FloatTab(floating);

        layout.ActivateTab(floating);

        Assert.Same(active, layout.MainTabs.ActiveTab);
    }

    [Fact]
    public void Floating_a_tab_keeps_it_in_its_group_collection()
    {
        var layout = NewLayout();
        Open(layout, First);
        var floating = Open(layout, Second);
        Open(layout, Third);

        layout.FloatTab(floating);

        Assert.Equal(new[] { First, Second, Third }, IdsOf(layout.MainTabs));
        Assert.True(floating.IsFloating);
    }

    [Fact]
    public void Dock_returns_tab_to_its_original_slot()
    {
        var layout = NewLayout();
        Open(layout, First);
        var floating = Open(layout, Second);
        Open(layout, Third);
        int originalSlot = layout.MainTabs.Tabs.IndexOf(floating);
        var item = layout.FloatTab(floating);

        layout.Dock(item);

        Assert.Equal(originalSlot, layout.MainTabs.Tabs.IndexOf(floating));
        Assert.False(floating.IsFloating);
        Assert.Empty(layout.Floating);
    }

    [Fact]
    public void Dock_activates_the_returned_tab()
    {
        var layout = NewLayout();
        Open(layout, First);
        var floating = Open(layout, Second);
        Open(layout, Third);
        var item = layout.FloatTab(floating);

        layout.Dock(item);

        Assert.Same(floating, layout.MainTabs.ActiveTab);
    }

    [Fact]
    public void Dock_does_nothing_while_the_layout_is_shutting_down()
    {
        var layout = NewLayout();
        var floating = Open(layout, First);
        var item = layout.FloatTab(floating);
        layout.IsShuttingDown = true;

        layout.Dock(item);

        Assert.Same(item, Assert.Single(layout.Floating));
        Assert.True(floating.IsFloating);
    }

    [Fact]
    public void Capture_keeps_the_floating_tab_while_the_layout_is_shutting_down()
    {
        var layout = NewLayout();
        var floating = Open(layout, First);
        var item = layout.FloatTab(floating);
        layout.IsShuttingDown = true;
        layout.Dock(item);

        var snapshot = layout.Capture();

        Assert.Equal(First, Assert.Single(snapshot.Floating).Id);
    }

    [Fact]
    public void Closing_a_floating_tab_removes_its_floating_item()
    {
        var layout = NewLayout();
        var floating = Open(layout, First);
        Open(layout, Second);
        layout.FloatTab(floating);

        layout.CloseTab(floating);

        Assert.Empty(layout.Floating);
    }

    [Fact]
    public void Floating_files_panel_takes_the_preview_group_out_of_the_inspector()
    {
        var layout = NewLayout();
        OpenPreview(layout);

        layout.FloatFilesPanel();

        Assert.False(layout.FilesPanel.IsVisible);
        Assert.False(layout.PreviewTabs.IsVisible);
    }

    [Fact]
    public void Opening_first_preview_tab_widens_inspector_by_preview_width()
    {
        var layout = NewLayout();
        double inspectorBefore = layout.Inspector.Width;
        double previewWidth = layout.PreviewWidth;

        OpenPreview(layout);

        Assert.Equal(inspectorBefore + previewWidth, layout.Inspector.Width);
    }

    [Fact]
    public void Preview_opened_while_files_float_does_not_widen_inspector()
    {
        var layout = NewLayout();
        double inspectorBefore = layout.Inspector.Width;
        layout.FloatFilesPanel();

        OpenPreview(layout);

        Assert.Equal(inspectorBefore, layout.Inspector.Width);
    }

    [Fact]
    public void Dock_brings_files_panel_and_preview_group_back_together()
    {
        var layout = NewLayout();
        OpenPreview(layout);
        var item = layout.FloatFilesPanel();

        layout.Dock(item);

        Assert.True(layout.FilesPanel.IsVisible);
        Assert.True(layout.PreviewTabs.IsVisible);
        Assert.Empty(layout.Floating);
    }

    [Fact]
    public void Dock_after_preview_opened_during_float_restores_borrowed_width()
    {
        var layout = NewLayout();
        double sideWidth = layout.Inspector.Width;
        var item = layout.FloatFilesPanel();
        OpenPreview(layout);

        layout.Dock(item);

        Assert.Equal(sideWidth + layout.PreviewWidth, layout.Inspector.Width);
    }

    [Fact]
    public void Closing_last_preview_tab_during_float_gives_width_back_on_dock()
    {
        var layout = NewLayout();
        double sideWidth = layout.Inspector.Width;
        var preview = OpenPreview(layout);
        var item = layout.FloatFilesPanel();
        layout.CloseTab(preview);

        layout.Dock(item);

        Assert.Equal(sideWidth, layout.Inspector.Width);
    }

    [Fact]
    public void Tab_closed_event_reports_the_closed_tab()
    {
        var layout = NewLayout();
        var closing = Open(layout, First);
        Open(layout, Second);
        var raised = new List<TabModel>();
        layout.TabClosed += (_, tab) => raised.Add(tab);

        layout.CloseTab(closing);

        Assert.Same(closing, Assert.Single(raised));
    }

    [Fact]
    public void Active_tab_changed_event_reports_the_newly_active_tab()
    {
        var layout = NewLayout();
        var target = Open(layout, First);
        Open(layout, Second);
        var raised = new List<TabModel>();
        layout.ActiveTabChanged += (_, tab) => raised.Add(tab);

        layout.ActivateTab(target);

        Assert.Same(target, Assert.Single(raised));
    }

    [Fact]
    public void Opening_a_tab_raises_layout_changed()
    {
        var layout = NewLayout();
        var changes = RecordLayoutChanges(layout);

        Open(layout, First);

        Assert.NotEmpty(changes);
    }

    [Fact]
    public void Closing_a_tab_raises_layout_changed()
    {
        var layout = NewLayout();
        var tab = Open(layout, First);
        Open(layout, Second);
        var changes = RecordLayoutChanges(layout);

        layout.CloseTab(tab);

        Assert.NotEmpty(changes);
    }

    [Fact]
    public void Moving_a_tab_raises_layout_changed()
    {
        var layout = NewLayout();
        Open(layout, First);
        Open(layout, Second);
        var changes = RecordLayoutChanges(layout);

        layout.MoveTab(layout.MainTabs, 0, 1);

        Assert.NotEmpty(changes);
    }

    [Fact]
    public void Floating_a_tab_raises_layout_changed()
    {
        var layout = NewLayout();
        var tab = Open(layout, First);
        Open(layout, Second);
        var changes = RecordLayoutChanges(layout);

        layout.FloatTab(tab);

        Assert.NotEmpty(changes);
    }

    [Fact]
    public void Floating_files_panel_raises_layout_changed()
    {
        var layout = NewLayout();
        var changes = RecordLayoutChanges(layout);

        layout.FloatFilesPanel();

        Assert.NotEmpty(changes);
    }

    [Fact]
    public void Docking_a_tab_raises_layout_changed()
    {
        var layout = NewLayout();
        var item = layout.FloatTab(Open(layout, First));
        var changes = RecordLayoutChanges(layout);

        layout.Dock(item);

        Assert.NotEmpty(changes);
    }

    [Fact]
    public void Changing_a_floating_window_geometry_raises_layout_changed()
    {
        var layout = NewLayout();
        var item = layout.FloatTab(Open(layout, First));
        var changes = RecordLayoutChanges(layout);

        item.Geometry = new FloatGeometry(MovedX, MovedY, MovedWidth, MovedHeight, IsMaximized: false);

        Assert.NotEmpty(changes);
    }

    [Fact]
    public void Switching_the_active_tab_raises_layout_changed()
    {
        var layout = NewLayout();
        var target = Open(layout, First);
        Open(layout, Second);
        var changes = RecordLayoutChanges(layout);

        layout.ActivateTab(target);

        Assert.NotEmpty(changes);
    }

    [Fact]
    public void Setting_preview_width_raises_layout_changed()
    {
        var layout = NewLayout();
        var changes = RecordLayoutChanges(layout);

        layout.PreviewWidth = WorkbenchMetrics.PreviewMinWidth;

        Assert.NotEmpty(changes);
    }

    [Fact]
    public void Setting_inspector_split_ratio_raises_layout_changed()
    {
        var layout = NewLayout();
        var changes = RecordLayoutChanges(layout);

        layout.InspectorSplitRatio = OtherSplitRatio;

        Assert.NotEmpty(changes);
    }

    [Fact]
    public void Toggling_a_drawer_does_not_raise_layout_changed()
    {
        var layout = NewLayout();
        layout.SetAutoHidden(RegionRole.Project, true);
        var changes = RecordLayoutChanges(layout);

        layout.ToggleDrawer(RegionRole.Project);

        Assert.Empty(changes);
    }

    [Fact]
    public void Covering_main_with_preview_does_not_raise_layout_changed()
    {
        var layout = NewLayout();
        OpenPreview(layout);
        var changes = RecordLayoutChanges(layout);

        layout.TogglePreviewCoverMain();

        Assert.Empty(changes);
    }

    private static WorkbenchLayout NewLayout()
    {
        var layout = new WorkbenchLayout("project", "files", "todo", "review", "specs");
        layout.SetWindowWidth(WideWindowWidth);
        return layout;
    }

    private static TabModel Open(WorkbenchLayout layout, string id) =>
        layout.OpenTab(layout.MainTabs, id, TabKind.Session, id, "title " + id);

    private static TabModel OpenPreview(WorkbenchLayout layout) =>
        layout.OpenTab(layout.PreviewTabs, WorkbenchIds.PreviewTab, TabKind.FilePreview, "preview", "preview");

    private static List<string> IdsOf(TabGroupModel group) => group.Tabs.Select(tab => tab.Id).ToList();

    private static List<EventArgs> RecordLayoutChanges(WorkbenchLayout layout)
    {
        var changes = new List<EventArgs>();
        layout.LayoutChanged += (_, args) => changes.Add(args);
        return changes;
    }
}
