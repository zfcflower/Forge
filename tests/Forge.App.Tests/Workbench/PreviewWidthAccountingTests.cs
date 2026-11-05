using Forge.App.Workbench;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Pins the width accounting of the preview column (forge-app spec, "区域宽度分配" and "布局快照自管"): the Inspector's
/// width is the width of its own column plus, while the preview group is docked-visible, the borrowed
/// <see cref="WorkbenchLayout.PreviewWidth"/>; Main absorbs the difference. The snapshot stores the Inspector without
/// the preview column, so a restore that reopens a preview tab does not count the column twice.
///
/// Pure model tests: no window, no control tree.
/// </summary>
public sealed class PreviewWidthAccountingTests
{
    // Wide enough that Main never reaches its minimum, so a width change is only the rule under test.
    private const double WideWindowWidth = 2000;
    private const int Precision = 6;

    [Fact]
    public void Floating_the_last_docked_preview_tab_gives_PreviewWidth_back()
    {
        var layout = NewLayout();
        var tab = OpenPreview(layout, WorkbenchIds.PreviewTab);
        double borrowed = layout.Inspector.Width;

        layout.FloatTab(tab);

        Assert.False(layout.PreviewTabs.IsVisible);
        Assert.Equal(borrowed - layout.PreviewWidth, layout.Inspector.Width, Precision);
    }

    [Fact]
    public void Floating_the_last_docked_preview_tab_widens_main_by_PreviewWidth()
    {
        var layout = NewLayout();
        var tab = OpenPreview(layout, WorkbenchIds.PreviewTab);
        double mainBefore = layout.Main.Width;

        layout.FloatTab(tab);

        Assert.Equal(mainBefore + layout.PreviewWidth, layout.Main.Width, Precision);
    }

    [Fact]
    public void Docking_the_floating_preview_tab_borrows_PreviewWidth_again()
    {
        var layout = NewLayout();
        var tab = OpenPreview(layout, WorkbenchIds.PreviewTab);
        double dockedWidth = layout.Inspector.Width;
        var item = layout.FloatTab(tab);

        layout.Dock(item);

        Assert.True(layout.PreviewTabs.IsVisible);
        Assert.Equal(dockedWidth, layout.Inspector.Width, Precision);
        Assert.Equal(dockedWidth - layout.PreviewWidth, BaseInspectorWidth(layout), Precision);
    }

    [Fact]
    public void Floating_one_of_two_preview_tabs_keeps_the_preview_column()
    {
        var layout = NewLayout();
        var file = OpenPreview(layout, WorkbenchIds.PreviewTab);
        OpenPreview(layout, WorkbenchIds.ChangesTab);
        double withColumn = layout.Inspector.Width;

        layout.FloatTab(file);

        Assert.True(layout.PreviewTabs.IsVisible);
        Assert.Equal(withColumn, layout.Inspector.Width, Precision);
    }

    [Fact]
    public void Floating_files_panel_gives_PreviewWidth_back_and_docking_it_borrows_again()
    {
        var layout = NewLayout();
        OpenPreview(layout, WorkbenchIds.PreviewTab);
        double baseWidth = BaseInspectorWidth(layout);
        double withColumn = layout.Inspector.Width;

        var item = layout.FloatFilesPanel();
        Assert.Equal(baseWidth, layout.Inspector.Width, Precision);
        Assert.Equal(WideWindowWidth - MainChrome(layout), layout.Main.Width + layout.Inspector.Width + layout.Project.Width, Precision);

        layout.Dock(item);

        Assert.Equal(withColumn, layout.Inspector.Width, Precision);
    }

    [Fact]
    public void Inspector_width_stays_base_plus_the_visible_preview_column_through_float_and_dock_cycles()
    {
        var layout = NewLayout();
        double baseWidth = BaseInspectorWidth(layout);
        Assert.Equal(WorkbenchMetrics.DefaultInspectorWidth, baseWidth, Precision);
        var expected = new List<(double Inspector, double Main, double StoredBase)>();
        var observed = new List<(double Inspector, double Main, double StoredBase)>();

        // Each step records the width the rule demands and the width the layout produced, in the same order, so a
        // failure points at the transition that broke the invariant.
        void Record()
        {
            bool docked = layout.PreviewTabs.IsVisible;
            double inspector = baseWidth + (docked ? layout.PreviewWidth : 0);
            expected.Add((inspector, WideWindowWidth - MainChrome(layout) - layout.Project.Width - inspector, baseWidth));
            observed.Add((layout.Inspector.Width, layout.Main.Width, BaseInspectorWidth(layout)));
        }

        Record();
        var file = OpenPreview(layout, WorkbenchIds.PreviewTab);
        Record();
        var changes = OpenPreview(layout, WorkbenchIds.ChangesTab);
        Record();
        var floatingFile = layout.FloatTab(file);
        Record();
        var floatingChanges = layout.FloatTab(changes);
        Record();
        var floatingFiles = layout.FloatFilesPanel();
        Record();
        layout.Dock(floatingFiles);
        Record();
        layout.Dock(floatingFile);
        Record();
        layout.Dock(floatingChanges);
        Record();
        layout.FloatFilesPanel();
        Record();
        layout.FloatTab(changes);
        Record();
        while (layout.Floating.Count > 0)
        {
            layout.Dock(layout.Floating[0]);
            Record();
        }

        Assert.Equal(expected, observed);
    }

    [Fact]
    public void Restoring_a_stored_inspector_width_then_reopening_the_preview_tab_borrows_the_column_once()
    {
        // The snapshot stores the Inspector without the preview column; the reopen after Apply adds it exactly once.
        const double storedInspectorWidth = 320;
        var layout = new WorkbenchLayout(new object(), new object(), new object(), new object(), new object());
        layout.Apply(new LayoutSnapshot
        {
            Regions = [new RegionSnapshot { Role = nameof(RegionRole.Inspector), Width = storedInspectorWidth }],
        });
        Assert.Equal(storedInspectorWidth, layout.Inspector.Width, Precision);

        OpenPreview(layout, WorkbenchIds.PreviewTab);

        Assert.Equal(storedInspectorWidth + layout.PreviewWidth, layout.Inspector.Width, Precision);
        Assert.Equal(storedInspectorWidth, BaseInspectorWidth(layout), Precision);
    }

    private static WorkbenchLayout NewLayout()
    {
        var layout = new WorkbenchLayout(new object(), new object(), new object(), new object(), new object());
        layout.SetWindowWidth(WideWindowWidth);
        return layout;
    }

    private static TabModel OpenPreview(WorkbenchLayout layout, string id) =>
        layout.OpenTab(layout.PreviewTabs, id, TabKind.FilePreview, new object(), "title " + id);

    /// <summary>Width of the Inspector without the preview column, as the snapshot stores it.</summary>
    private static double BaseInspectorWidth(WorkbenchLayout layout) =>
        layout.Capture().Regions.Single(static region => region.Role == nameof(RegionRole.Inspector)).Width;

    private static double MainChrome(WorkbenchLayout layout) =>
        (layout.Order.Count - 1) * WorkbenchMetrics.RegionSplitterWidth;
}
