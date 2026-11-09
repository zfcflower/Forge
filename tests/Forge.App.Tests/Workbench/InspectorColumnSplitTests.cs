using Avalonia.Controls;
using Avalonia.Headless;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// The Inspector column's stack: the files panel on top, then the Git panel and the tool group (TODO | review | spec tree).
/// Every line between them is draggable — before this, only the one under the files panel was, so the tool group the spec
/// tree lives in could not be made taller at all.
/// </summary>
[Collection("forge-app-ui")]
public sealed class InspectorColumnSplitTests
{
    private const double DragDistance = 50;
    private const double CollapseDragDistance = 900;
    private const double Tolerance = 1;
    private const string GitSplitterId = "panel-splitter-" + WorkbenchIds.GitPanel;

    [Fact]
    public async Task The_line_above_the_tool_group_is_a_splitter_across_the_column()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var geometry = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(RealLayout());
            var splitter = WorkbenchUi.Find<PanelSplitter>(scene.Window, GitSplitterId);
            var git = WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(WorkbenchIds.GitPanel));
            var tools = WorkbenchUi.Find<PanelChrome>(scene.Window, WorkbenchUi.PanelId(WorkbenchIds.InspectorTools));
            return (Splitter: splitter.Bounds, Git: git.Bounds, Tools: tools.Bounds);
        });

        Assert.Equal(WorkbenchMetrics.SplitterHitWidth, geometry.Splitter.Height, 0.5);
        Assert.Equal(geometry.Git.Width, geometry.Splitter.Width, 0.5);
        Assert.InRange(geometry.Splitter.Y, geometry.Git.Bottom - WorkbenchMetrics.SplitterHitWidth, geometry.Git.Bottom);
        Assert.InRange(geometry.Splitter.Bottom, geometry.Tools.Y, geometry.Tools.Y + WorkbenchMetrics.SplitterHitWidth);
    }

    [Fact]
    public async Task Dragging_it_up_makes_the_tool_group_taller_by_the_drag_distance()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var heights = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(RealLayout());
            var before = HeightOf(scene.Window, WorkbenchIds.InspectorTools);
            WorkbenchUi.DragSplitter(scene, GitSplitterId, 0, -DragDistance);
            return (Before: before, After: HeightOf(scene.Window, WorkbenchIds.InspectorTools));
        });

        Assert.Equal(heights.Before + DragDistance, heights.After, Tolerance);
    }

    [Fact]
    public async Task Dragging_it_up_takes_the_height_from_the_git_panel_above_it()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var heights = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(RealLayout());
            var before = HeightOf(scene.Window, WorkbenchIds.GitPanel);
            WorkbenchUi.DragSplitter(scene, GitSplitterId, 0, -DragDistance);
            return (Before: before, After: HeightOf(scene.Window, WorkbenchIds.GitPanel));
        });

        Assert.Equal(heights.Before - DragDistance, heights.After, Tolerance);
    }

    [Fact]
    public async Task Dragging_it_up_leaves_the_files_panel_alone()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var heights = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(RealLayout());
            var before = HeightOf(scene.Window, WorkbenchIds.FilesPanel);
            WorkbenchUi.DragSplitter(scene, GitSplitterId, 0, -DragDistance);
            return (Before: before, After: HeightOf(scene.Window, WorkbenchIds.FilesPanel));
        });

        Assert.Equal(heights.Before, heights.After, Tolerance);
    }

    [Fact]
    public async Task Dragging_up_past_the_minimum_stops_at_the_panel_minimum_height()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var heights = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(RealLayout());
            WorkbenchUi.DragSplitter(scene, GitSplitterId, 0, -CollapseDragDistance);
            return (Git: HeightOf(scene.Window, WorkbenchIds.GitPanel), Tools: HeightOf(scene.Window, WorkbenchIds.InspectorTools));
        });

        Assert.Equal(WorkbenchMetrics.PanelMinHeight, heights.Git, Tolerance);
        Assert.True(heights.Tools > WorkbenchMetrics.PanelMinHeight);
    }

    [Fact]
    public async Task Dragging_down_past_the_minimum_leaves_the_tool_group_its_minimum_height()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var heights = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(RealLayout());
            WorkbenchUi.DragSplitter(scene, GitSplitterId, 0, CollapseDragDistance);
            return (Git: HeightOf(scene.Window, WorkbenchIds.GitPanel), Tools: HeightOf(scene.Window, WorkbenchIds.InspectorTools));
        });

        Assert.Equal(WorkbenchMetrics.PanelMinHeight, heights.Tools, Tolerance);
        Assert.True(heights.Git > WorkbenchMetrics.PanelMinHeight);
    }

    [Fact]
    public async Task The_dragged_heights_come_back_after_a_restart()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var heights = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(RealLayout());
            WorkbenchUi.DragSplitter(scene, GitSplitterId, 0, -DragDistance);
            var captured = scene.Layout.Capture();

            var restored = RealLayout();
            restored.Apply(captured);
            var reopened = WorkbenchUi.Open(restored);
            return (Dragged: HeightOf(scene.Window, WorkbenchIds.InspectorTools), Reopened: HeightOf(reopened.Window, WorkbenchIds.InspectorTools));
        });

        Assert.Equal(heights.Dragged, heights.Reopened, Tolerance);
    }

    [Fact]
    public async Task A_column_without_a_git_panel_has_no_line_below_the_files_panel()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var found = await WorkbenchUi.QueryAsync(session, () =>
        {
            var scene = WorkbenchUi.Open(WorkbenchUi.NewLayout());
            return WorkbenchUi.TryFind<PanelSplitter>(scene.Window, GitSplitterId)?.Bounds ?? default;
        });

        Assert.Equal(0, found.Height);
    }

    /// <summary>The layout the application builds: Git under the files panel, the tool group under Git, general chats on the left.</summary>
    private static WorkbenchLayout RealLayout() =>
        new(new object(), new object(), new object(), new object(), new object(), new object(), new object());

    [Fact]
    public void A_stored_height_is_kept_for_a_panel_that_still_exists()
    {
        var layout = RealLayout();

        layout.Apply(new LayoutSnapshot { BelowHeights = { [WorkbenchIds.GitPanel] = 240 } });

        Assert.Equal(240, layout.BelowPanelHeight(WorkbenchIds.GitPanel));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-240.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void An_unusable_stored_height_falls_back_to_the_even_split(double height)
    {
        var layout = RealLayout();

        layout.Apply(new LayoutSnapshot { BelowHeights = { [WorkbenchIds.GitPanel] = height } });

        Assert.Equal(0, layout.BelowPanelHeight(WorkbenchIds.GitPanel));
    }

    [Fact]
    public void A_stored_height_for_a_panel_this_layout_does_not_have_is_dropped()
    {
        var layout = RealLayout();

        layout.Apply(new LayoutSnapshot { BelowHeights = { ["no-such-panel"] = 240 } });

        Assert.Equal(0, layout.BelowPanelHeight("no-such-panel"));
    }

    private static double HeightOf(Window window, string panelId) =>
        WorkbenchUi.Find<PanelChrome>(window, WorkbenchUi.PanelId(panelId)).Bounds.Height;
}
