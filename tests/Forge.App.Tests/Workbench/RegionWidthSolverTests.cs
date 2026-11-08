using Forge.App.Workbench;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Pins <see cref="RegionWidthSolver"/> against the "区域宽度分配" rules of the forge-app spec. Drag: the squeezed side
/// shrinks Main first and then the other regions nearest-first; a side region collapses only when pushed more than
/// <see cref="WorkbenchMetrics.CollapseOvershoot"/> × its minimum past it; Main never collapses; the uncollapsed widths
/// keep their start sum; a drag is recomputed from the start widths. Fit: surplus goes to Main; a shortfall shrinks
/// Main, then the side regions, then hides them in shrink order; protected regions never hide; both edge strips are
/// always reserved and gaps are counted once per gap. Also pins that a drag call allocates nothing.
/// </summary>
public sealed class RegionWidthSolverTests
{
    private const int ProjectIndex = 0;
    private const int MainIndex = 1;
    private const int InspectorIndex = 2;
    private const int RegionCount = 3;

    private const int LeftSplitter = 0;
    private const int RightSplitter = 1;
    private const int NoProtectedRegion = -1;

    private const double ProjectStart = WorkbenchMetrics.DefaultProjectWidth;
    private const double MainStart = 500;
    private const double InspectorStart = WorkbenchMetrics.DefaultInspectorWidth;
    private const double SideMin = WorkbenchMetrics.SideMinWidth;
    private const double MainMin = WorkbenchMetrics.MainMinWidth;
    private const double Total = ProjectStart + MainStart + InspectorStart;

    private const double ProjectSlack = ProjectStart - SideMin;
    private const double InspectorSlack = InspectorStart - SideMin;
    private const double MainSlack = MainStart - MainMin;
    private const double Overshoot = SideMin * WorkbenchMetrics.CollapseOvershoot;
    private const double OnePixel = 1;

    private const double Splitter = WorkbenchMetrics.RegionSplitterWidth;
    private const double Strip = WorkbenchMetrics.AutoHideStripWidth;

    // Docked regions have only the splitters between them; strips are reserved only for auto-hidden regions.
    private const double DockedChrome = 2 * Splitter;

    // Narrow enough that the inspector must hide, wide enough that the project then still fits beside Main.
    private const double HideOneWindow = 660;

    private const double HugeDelta = 1e6;
    /// <summary>
    /// One drag notch that the width solver can absorb without clamping in the cases under test. It has to stay inside a
    /// window: above <c>ProjectSlack</c> (or the case that expects the project to hit its minimum stops before it) and below
    /// <c>InspectorSlack</c> (or a case that expects the inspector to shrink by the drag distance clamps instead).
    /// </summary>
    private const double SmallDrag = 70;
    private const double MediumDrag = 200;
    private const double PartialShortfall = 50;
    private const double UndersizedMain = 300;
    private const double UndersizedMainTight = 100;
    private const double OversizedInspector = 210;

    private const int PixelDigits = 6;
    private const int DragCalls = 1000;

    [Fact]
    public void Drag_right_on_left_splitter_takes_space_from_main_only_while_main_is_above_minimum()
    {
        const double delta = SmallDrag;
        Assert.True(delta < MainSlack);

        var (widths, collapsed) = RunDrag(DefaultStart(), DefaultMins(), MainIndex, LeftSplitter, delta);

        Assert.Equal(ProjectStart + delta, widths[ProjectIndex], PixelDigits);
        Assert.Equal(MainStart - delta, widths[MainIndex], PixelDigits);
        Assert.Equal(InspectorStart, widths[InspectorIndex], PixelDigits);
        Assert.False(collapsed[InspectorIndex]);
    }

    [Fact]
    public void Drag_right_on_left_splitter_shrinks_inspector_only_after_main_reaches_minimum()
    {
        const double delta = MediumDrag;
        Assert.True(delta > MainSlack);

        var (widths, collapsed) = RunDrag(DefaultStart(), DefaultMins(), MainIndex, LeftSplitter, delta);

        Assert.Equal(MainMin, widths[MainIndex], PixelDigits);
        Assert.Equal(InspectorStart - (delta - MainSlack), widths[InspectorIndex], PixelDigits);
        Assert.Equal(ProjectStart + delta, widths[ProjectIndex], PixelDigits);
        Assert.False(collapsed[InspectorIndex]);
    }

    [Fact]
    public void Drag_left_on_left_splitter_shrinks_project_and_grows_main()
    {
        const double delta = -ProjectSlack;

        var (widths, collapsed) = RunDrag(DefaultStart(), DefaultMins(), MainIndex, LeftSplitter, delta);

        Assert.Equal(SideMin, widths[ProjectIndex], PixelDigits);
        Assert.Equal(MainStart + ProjectSlack, widths[MainIndex], PixelDigits);
        Assert.Equal(InspectorStart, widths[InspectorIndex], PixelDigits);
        Assert.False(collapsed[ProjectIndex]);
    }

    [Fact]
    public void Drag_left_on_right_splitter_shrinks_main_before_project()
    {
        const double delta = -MediumDrag;

        var (widths, collapsed) = RunDrag(DefaultStart(), DefaultMins(), MainIndex, RightSplitter, delta);

        Assert.Equal(MainMin, widths[MainIndex], PixelDigits);
        Assert.Equal(ProjectStart - (MediumDrag - MainSlack), widths[ProjectIndex], PixelDigits);
        Assert.Equal(InspectorStart + MediumDrag, widths[InspectorIndex], PixelDigits);
        Assert.False(collapsed[ProjectIndex]);
    }

    [Fact]
    public void Drag_right_on_right_splitter_shrinks_inspector_and_grows_main()
    {
        const double delta = SmallDrag;

        var (widths, collapsed) = RunDrag(DefaultStart(), DefaultMins(), MainIndex, RightSplitter, delta);

        Assert.Equal(InspectorStart - delta, widths[InspectorIndex], PixelDigits);
        Assert.Equal(MainStart + delta, widths[MainIndex], PixelDigits);
        Assert.Equal(ProjectStart, widths[ProjectIndex], PixelDigits);
        Assert.False(collapsed[InspectorIndex]);
    }

    [Fact]
    public void Drag_left_on_left_splitter_collapses_project_when_pushed_past_overshoot_threshold()
    {
        const double delta = -(ProjectSlack + Overshoot + OnePixel);

        var (_, collapsed) = RunDrag(DefaultStart(), DefaultMins(), MainIndex, LeftSplitter, delta);

        Assert.True(collapsed[ProjectIndex]);
        Assert.False(collapsed[MainIndex]);
        Assert.False(collapsed[InspectorIndex]);
    }

    [Fact]
    public void Drag_left_on_left_splitter_stops_at_minimum_when_exactly_at_overshoot_threshold()
    {
        const double delta = -(ProjectSlack + Overshoot);

        var (widths, collapsed) = RunDrag(DefaultStart(), DefaultMins(), MainIndex, LeftSplitter, delta);

        Assert.False(collapsed[ProjectIndex]);
        Assert.Equal(SideMin, widths[ProjectIndex], PixelDigits);
        Assert.Equal(MainStart + ProjectSlack, widths[MainIndex], PixelDigits);
    }

    [Fact]
    public void Drag_collapsed_region_keeps_its_start_width()
    {
        const double delta = -(ProjectSlack + Overshoot + OnePixel);

        var (widths, _) = RunDrag(DefaultStart(), DefaultMins(), MainIndex, LeftSplitter, delta);

        Assert.Equal(ProjectStart, widths[ProjectIndex], PixelDigits);
    }

    [Fact]
    public void Drag_right_on_right_splitter_collapses_inspector_when_pushed_past_overshoot_threshold()
    {
        const double delta = InspectorSlack + Overshoot + OnePixel;

        var (widths, collapsed) = RunDrag(DefaultStart(), DefaultMins(), MainIndex, RightSplitter, delta);

        Assert.True(collapsed[InspectorIndex]);
        Assert.Equal(InspectorStart, widths[InspectorIndex], PixelDigits);
        Assert.Equal(Total - ProjectStart, widths[MainIndex], PixelDigits);
    }

    [Fact]
    public void Drag_right_on_right_splitter_stops_at_minimum_when_exactly_at_overshoot_threshold()
    {
        const double delta = InspectorSlack + Overshoot;

        var (widths, collapsed) = RunDrag(DefaultStart(), DefaultMins(), MainIndex, RightSplitter, delta);

        Assert.False(collapsed[InspectorIndex]);
        Assert.Equal(SideMin, widths[InspectorIndex], PixelDigits);
        Assert.Equal(Total - ProjectStart - SideMin, widths[MainIndex], PixelDigits);
    }

    [Theory]
    [InlineData(LeftSplitter, SmallDrag)]
    [InlineData(LeftSplitter, -SmallDrag)]
    [InlineData(LeftSplitter, MediumDrag)]
    [InlineData(LeftSplitter, -(ProjectSlack + Overshoot + OnePixel))]
    [InlineData(RightSplitter, -SmallDrag)]
    [InlineData(RightSplitter, InspectorSlack + Overshoot + OnePixel)]
    [InlineData(LeftSplitter, HugeDelta)]
    [InlineData(RightSplitter, -HugeDelta)]
    public void Drag_keeps_the_sum_of_uncollapsed_widths_equal_to_the_start_sum(int splitter, double delta)
    {
        var (widths, collapsed) = RunDrag(DefaultStart(), DefaultMins(), MainIndex, splitter, delta);

        double uncollapsedSum = 0;
        for (int i = 0; i < RegionCount; i++)
        {
            if (!collapsed[i])
            {
                uncollapsedSum += widths[i];
            }
        }

        Assert.Equal(Total, uncollapsedSum, PixelDigits);
    }

    [Theory]
    [InlineData(LeftSplitter, HugeDelta)]
    [InlineData(LeftSplitter, -HugeDelta)]
    [InlineData(RightSplitter, HugeDelta)]
    [InlineData(RightSplitter, -HugeDelta)]
    public void Drag_never_collapses_main(int splitter, double delta)
    {
        var (_, collapsed) = RunDrag(DefaultStart(), DefaultMins(), MainIndex, splitter, delta);

        Assert.False(collapsed[MainIndex]);
    }

    [Fact]
    public void Drag_huge_right_on_left_splitter_collapses_inspector_and_leaves_main_at_minimum()
    {
        var (widths, collapsed) = RunDrag(DefaultStart(), DefaultMins(), MainIndex, LeftSplitter, HugeDelta);

        Assert.True(collapsed[InspectorIndex]);
        Assert.Equal(MainMin, widths[MainIndex], PixelDigits);
        Assert.Equal(Total - MainMin, widths[ProjectIndex], PixelDigits);
    }

    [Fact]
    public void Drag_huge_left_on_left_splitter_collapses_project_and_main_takes_the_rest()
    {
        var (widths, collapsed) = RunDrag(DefaultStart(), DefaultMins(), MainIndex, LeftSplitter, -HugeDelta);

        Assert.True(collapsed[ProjectIndex]);
        Assert.Equal(ProjectStart, widths[ProjectIndex], PixelDigits);
        Assert.Equal(Total - InspectorStart, widths[MainIndex], PixelDigits);
        Assert.Equal(InspectorStart, widths[InspectorIndex], PixelDigits);
    }

    [Fact]
    public void Drag_huge_left_on_right_splitter_collapses_project_and_inspector_takes_the_rest()
    {
        var (widths, collapsed) = RunDrag(DefaultStart(), DefaultMins(), MainIndex, RightSplitter, -HugeDelta);

        Assert.True(collapsed[ProjectIndex]);
        Assert.Equal(MainMin, widths[MainIndex], PixelDigits);
        Assert.Equal(Total - MainMin, widths[InspectorIndex], PixelDigits);
    }

    [Fact]
    public void Drag_zero_delta_keeps_every_start_width_and_collapses_nothing()
    {
        var (widths, collapsed) = RunDrag(DefaultStart(), DefaultMins(), MainIndex, LeftSplitter, 0);

        Assert.Equal(ProjectStart, widths[ProjectIndex], PixelDigits);
        Assert.Equal(MainStart, widths[MainIndex], PixelDigits);
        Assert.Equal(InspectorStart, widths[InspectorIndex], PixelDigits);
        Assert.All(collapsed, c => Assert.False(c));
    }

    [Fact]
    public void Drag_recomputes_from_start_so_a_small_drag_after_a_collapsing_one_does_not_collapse()
    {
        double[] start = DefaultStart();
        double[] min = DefaultMins();
        double[] widths = new double[RegionCount];
        bool[] collapsed = new bool[RegionCount];

        RegionWidthSolver.Drag(start, min, MainIndex, LeftSplitter, -HugeDelta, widths, collapsed);
        Assert.True(collapsed[ProjectIndex]);

        RegionWidthSolver.Drag(start, min, MainIndex, LeftSplitter, -ProjectSlack, widths, collapsed);

        Assert.False(collapsed[ProjectIndex]);
        Assert.Equal(SideMin, widths[ProjectIndex], PixelDigits);
        Assert.Equal(MainStart + ProjectSlack, widths[MainIndex], PixelDigits);
    }

    [Fact]
    public void Drag_right_on_left_splitter_in_project_inspector_main_order_shrinks_main_before_the_nearer_inspector()
    {
        const double delta = SmallDrag;
        double[] start = [ProjectStart, InspectorStart, MainStart];
        double[] min = [SideMin, SideMin, MainMin];

        var (widths, collapsed) = RunDrag(start, min, 2, LeftSplitter, delta);

        Assert.Equal(ProjectStart + delta, widths[0], PixelDigits);
        Assert.Equal(InspectorStart, widths[1], PixelDigits);
        Assert.Equal(MainStart - delta, widths[2], PixelDigits);
        Assert.False(collapsed[1]);
    }

    [Fact]
    public void Drag_left_on_right_splitter_in_project_inspector_main_order_shrinks_nearer_inspector_before_project()
    {
        const double delta = -SmallDrag;
        double[] start = [ProjectStart, InspectorStart, MainStart];
        double[] min = [SideMin, SideMin, MainMin];

        var (widths, collapsed) = RunDrag(start, min, 2, RightSplitter, delta);

        Assert.Equal(InspectorStart - SmallDrag, widths[1], PixelDigits);
        Assert.Equal(ProjectStart, widths[0], PixelDigits);
        Assert.Equal(MainStart + SmallDrag, widths[2], PixelDigits);
        Assert.False(collapsed[0]);
    }

    [Fact]
    public void Drag_right_on_left_splitter_in_main_project_inspector_order_shrinks_nearer_project_before_inspector()
    {
        const double delta = SmallDrag;
        double[] start = [MainStart, ProjectStart, InspectorStart];
        double[] min = [MainMin, SideMin, SideMin];

        var (widths, collapsed) = RunDrag(start, min, 0, LeftSplitter, delta);

        Assert.Equal(MainStart + delta, widths[0], PixelDigits);
        Assert.Equal(SideMin, widths[1], PixelDigits);
        Assert.Equal(InspectorStart - (delta - ProjectSlack), widths[2], PixelDigits);
        Assert.False(collapsed[2]);
    }

    [Fact]
    public void Drag_left_on_left_splitter_in_main_project_inspector_order_stops_main_at_minimum_and_drops_the_rest()
    {
        const double delta = -MediumDrag;
        double[] start = [MainStart, ProjectStart, InspectorStart];
        double[] min = [MainMin, SideMin, SideMin];

        var (widths, collapsed) = RunDrag(start, min, 0, LeftSplitter, delta);

        Assert.Equal(MainMin, widths[0], PixelDigits);
        Assert.Equal(ProjectStart + MainSlack, widths[1], PixelDigits);
        Assert.Equal(InspectorStart, widths[2], PixelDigits);
        Assert.False(collapsed[0]);
    }

    [Fact]
    public void Drag_left_on_right_splitter_in_main_project_inspector_order_shrinks_main_then_project()
    {
        const double delta = -MediumDrag;
        double[] start = [MainStart, ProjectStart, InspectorStart];
        double[] min = [MainMin, SideMin, SideMin];

        var (widths, collapsed) = RunDrag(start, min, 0, RightSplitter, delta);

        Assert.Equal(MainMin, widths[0], PixelDigits);
        Assert.Equal(SideMin, widths[1], PixelDigits);
        Assert.Equal(InspectorStart + MediumDrag, widths[2], PixelDigits);
        Assert.False(collapsed[1]);
    }

    [Fact]
    public void Drag_allocates_nothing_per_call()
    {
        double[] start = DefaultStart();
        double[] min = DefaultMins();
        double[] widths = new double[RegionCount];
        bool[] collapsed = new bool[RegionCount];

        RegionWidthSolver.Drag(start, min, MainIndex, LeftSplitter, HugeDelta, widths, collapsed);
        _ = GC.GetAllocatedBytesForCurrentThread();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < DragCalls; i++)
        {
            int splitter = i % 2 == 0 ? LeftSplitter : RightSplitter;
            double delta = i % 4 < 2 ? i : -i;
            RegionWidthSolver.Drag(start, min, MainIndex, splitter, delta, widths, collapsed);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void Fit_widening_gives_surplus_to_main_and_leaves_sides_alone()
    {
        const double surplus = SmallDrag;
        const double window = Total + DockedChrome + surplus;

        var (widths, hidden) = FitDefault(window, DefaultStart(), NoneHidden());

        Assert.Equal(ProjectStart, widths[ProjectIndex], PixelDigits);
        Assert.Equal(InspectorStart, widths[InspectorIndex], PixelDigits);
        Assert.Equal(MainStart + surplus, widths[MainIndex], PixelDigits);
        Assert.All(hidden, h => Assert.False(h));
    }

    [Fact]
    public void Fit_narrowing_shrinks_main_before_any_side_region()
    {
        const double shortfall = SmallDrag;
        const double window = Total + DockedChrome - shortfall;

        var (widths, hidden) = FitDefault(window, DefaultStart(), NoneHidden());

        Assert.Equal(ProjectStart, widths[ProjectIndex], PixelDigits);
        Assert.Equal(InspectorStart, widths[InspectorIndex], PixelDigits);
        Assert.Equal(MainStart - shortfall, widths[MainIndex], PixelDigits);
        Assert.All(hidden, h => Assert.False(h));
    }

    [Fact]
    public void Fit_narrowing_shrinks_inspector_before_project_once_main_is_at_minimum()
    {
        const double shortfall = MainSlack + PartialShortfall;
        const double window = Total + DockedChrome - shortfall;

        var (widths, hidden) = FitDefault(window, DefaultStart(), NoneHidden());

        Assert.Equal(ProjectStart, widths[ProjectIndex], PixelDigits);
        Assert.Equal(InspectorStart - PartialShortfall, widths[InspectorIndex], PixelDigits);
        Assert.Equal(MainMin, widths[MainIndex], PixelDigits);
        Assert.All(hidden, h => Assert.False(h));
    }

    [Fact]
    public void Fit_narrowing_shrinks_project_once_inspector_is_at_minimum()
    {
        const double shortfall = MainSlack + InspectorSlack + PartialShortfall;
        const double window = Total + DockedChrome - shortfall;

        var (widths, hidden) = FitDefault(window, DefaultStart(), NoneHidden());

        Assert.Equal(ProjectStart - PartialShortfall, widths[ProjectIndex], PixelDigits);
        Assert.Equal(SideMin, widths[InspectorIndex], PixelDigits);
        Assert.Equal(MainMin, widths[MainIndex], PixelDigits);
        Assert.All(hidden, h => Assert.False(h));
    }

    [Fact]
    public void Fit_narrowing_hides_inspector_before_project_when_shrinking_is_not_enough()
    {
        const double window = HideOneWindow;

        var (widths, hidden) = FitDefault(window, DefaultStart(), NoneHidden());

        Assert.True(hidden[InspectorIndex]);
        Assert.False(hidden[ProjectIndex]);
        Assert.False(hidden[MainIndex]);
        Assert.Equal(window - Splitter - Strip - SideMin, widths[MainIndex], PixelDigits);
    }

    [Fact]
    public void Fit_narrowing_shrinks_project_to_minimum_before_hiding_inspector()
    {
        const double window = 600;

        var (widths, _) = FitDefault(window, DefaultStart(), NoneHidden());

        Assert.Equal(SideMin, widths[ProjectIndex], PixelDigits);
    }

    [Fact]
    public void Fit_hide_order_follows_shrink_order_when_project_comes_first()
    {
        const double window = HideOneWindow;
        int[] shrinkOrder = [ProjectIndex, InspectorIndex];

        var (_, hidden) = FitWith(window, DefaultStart(), DefaultMins(), NoneHidden(), DefaultEdges(), MainIndex, shrinkOrder, NoProtectedRegion);

        Assert.True(hidden[ProjectIndex]);
        Assert.False(hidden[InspectorIndex]);
    }

    [Fact]
    public void Fit_narrowing_hides_both_sides_when_still_too_narrow_and_main_keeps_the_space_between_strips()
    {
        const double window = 500;

        var (widths, hidden) = FitDefault(window, DefaultStart(), NoneHidden());

        Assert.True(hidden[ProjectIndex]);
        Assert.True(hidden[InspectorIndex]);
        Assert.Equal(window - 2 * Strip, widths[MainIndex], PixelDigits);
    }

    [Fact]
    public void Fit_region_hidden_by_fit_keeps_the_width_it_had_when_last_docked()
    {
        const double window = 500;

        var (widths, _) = FitDefault(window, DefaultStart(), NoneHidden());

        Assert.Equal(SideMin, widths[ProjectIndex], PixelDigits);
        Assert.Equal(SideMin, widths[InspectorIndex], PixelDigits);
    }

    [Fact]
    public void Fit_already_hidden_inspector_keeps_its_width_and_frees_one_gap_and_one_strip_for_main()
    {
        const double window = 1000;
        bool[] hidden = [false, false, true];

        var (widths, outHidden) = FitDefault(window, DefaultStart(), hidden);

        Assert.True(outHidden[InspectorIndex]);
        Assert.Equal(InspectorStart, widths[InspectorIndex], PixelDigits);
        Assert.Equal(window - Splitter - Strip - ProjectStart, widths[MainIndex], PixelDigits);
    }

    [Fact]
    public void Fit_side_regions_hidden_on_the_same_edge_share_one_strip()
    {
        const double window = 800;
        double[] widths = [MainStart, ProjectStart, InspectorStart];
        double[] min = [MainMin, SideMin, SideMin];
        bool[] hidden = [false, true, true];
        RegionEdge[] edges = [RegionEdge.Left, RegionEdge.Right, RegionEdge.Right];

        var (outWidths, outHidden) = FitWith(window, widths, min, hidden, edges, 0, [2, 1], NoProtectedRegion);

        Assert.True(outHidden[1]);
        Assert.True(outHidden[2]);
        Assert.Equal(window - Strip, outWidths[0], PixelDigits);
    }

    [Fact]
    public void Fit_protected_side_region_shrinks_but_is_not_hidden()
    {
        const double window = 500;

        var (widths, hidden) = FitWith(window, DefaultStart(), DefaultMins(), NoneHidden(), DefaultEdges(), MainIndex, [InspectorIndex, ProjectIndex], ProjectIndex);

        Assert.False(hidden[ProjectIndex]);
        Assert.Equal(SideMin, widths[ProjectIndex], PixelDigits);
        Assert.True(hidden[InspectorIndex]);
    }

    [Fact]
    public void Fit_unprotected_side_region_is_hidden_in_the_same_situation()
    {
        const double window = 500;

        var (_, hidden) = FitWith(window, DefaultStart(), DefaultMins(), NoneHidden(), DefaultEdges(), MainIndex, [InspectorIndex, ProjectIndex], NoProtectedRegion);

        Assert.True(hidden[ProjectIndex]);
    }

    [Fact]
    public void Fit_main_below_minimum_is_filled_from_oversized_inspector_without_hiding()
    {
        const double window = ProjectStart + UndersizedMain + InspectorStart + DockedChrome;
        double[] widths = [ProjectStart, UndersizedMain, InspectorStart];

        var (outWidths, hidden) = FitDefault(window, widths, NoneHidden());

        Assert.Equal(ProjectStart, outWidths[ProjectIndex], PixelDigits);
        Assert.Equal(MainMin, outWidths[MainIndex], PixelDigits);
        Assert.Equal(InspectorStart - (MainMin - UndersizedMain), outWidths[InspectorIndex], PixelDigits);
        Assert.All(hidden, h => Assert.False(h));
    }

    [Fact]
    public void Fit_main_below_minimum_shrinks_sides_to_minimum_then_hides_inspector()
    {
        const double window = HideOneWindow;
        double[] widths = [ProjectStart, UndersizedMainTight, OversizedInspector];

        var (outWidths, hidden) = FitDefault(window, widths, NoneHidden());

        Assert.True(hidden[InspectorIndex]);
        Assert.False(hidden[ProjectIndex]);
        Assert.Equal(SideMin, outWidths[ProjectIndex], PixelDigits);
        Assert.Equal(window - Splitter - Strip - SideMin, outWidths[MainIndex], PixelDigits);
    }

    [Fact]
    public void Fit_window_narrower_than_sum_of_minimums_hides_both_sides_and_main_takes_what_is_left()
    {
        const double window = 300;

        var (widths, hidden) = FitDefault(window, DefaultStart(), NoneHidden());

        Assert.True(hidden[ProjectIndex]);
        Assert.True(hidden[InspectorIndex]);
        Assert.Equal(window - 2 * Strip, widths[MainIndex], PixelDigits);
    }

    [Fact]
    public void Fit_window_narrower_than_the_edge_strips_clamps_main_to_zero()
    {
        const double window = 10;

        var (widths, _) = FitDefault(window, DefaultStart(), NoneHidden());

        Assert.Equal(0.0, widths[MainIndex], PixelDigits);
    }

    private static double[] DefaultStart() => [ProjectStart, MainStart, InspectorStart];

    private static double[] DefaultMins() => [SideMin, MainMin, SideMin];

    private static bool[] NoneHidden() => [false, false, false];

    private static RegionEdge[] DefaultEdges() => [RegionEdge.Left, RegionEdge.Left, RegionEdge.Right];

    private static (double[] Widths, bool[] Collapsed) RunDrag(
        double[] start,
        double[] min,
        int mainIndex,
        int splitterIndex,
        double delta)
    {
        var widths = new double[start.Length];
        var collapsed = new bool[start.Length];
        RegionWidthSolver.Drag(start, min, mainIndex, splitterIndex, delta, widths, collapsed);
        return (widths, collapsed);
    }

    private static (double[] Widths, bool[] Hidden) FitDefault(double windowWidth, double[] widths, bool[] hidden) =>
        FitWith(windowWidth, widths, DefaultMins(), hidden, DefaultEdges(), MainIndex, [InspectorIndex, ProjectIndex], NoProtectedRegion);

    private static (double[] Widths, bool[] Hidden) FitWith(
        double windowWidth,
        double[] widths,
        double[] minWidths,
        bool[] hidden,
        RegionEdge[] edges,
        int mainIndex,
        int[] shrinkOrder,
        int protectedIndex)
    {
        var outWidths = new double[widths.Length];
        var outHidden = new bool[widths.Length];
        RegionWidthSolver.Fit(widths, minWidths, hidden, edges, mainIndex, shrinkOrder, protectedIndex, windowWidth, outWidths, outHidden);
        return (outWidths, outHidden);
    }
}
