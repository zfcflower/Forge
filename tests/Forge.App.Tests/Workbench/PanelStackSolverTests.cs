using Forge.App.Workbench;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Unit tests of <see cref="PanelStackSolver"/>, the arithmetic behind every draggable line of a panel stack: how a region's
/// height is divided between its panels and where the line between two neighbours lands after a drag.
/// </summary>
public sealed class PanelStackSolverTests
{
    private const double Min = WorkbenchMetrics.PanelMinHeight;
    private const double Tolerance = 0.001;

    [Fact]
    public void Equal_weights_split_the_space_evenly()
    {
        double[] weights = [1, 1, 1];
        var heights = new double[3];

        PanelStackSolver.Distribute(603, weights, Min, heights);

        Assert.Equal([201, 201, 201], heights);
    }

    [Fact]
    public void Weights_divide_the_space_in_their_proportions()
    {
        double[] weights = [240, 160];
        var heights = new double[2];

        PanelStackSolver.Distribute(400, weights, Min, heights);

        Assert.Equal(240, heights[0], Tolerance);
        Assert.Equal(160, heights[1], Tolerance);
    }

    [Fact]
    public void A_weightless_slot_splits_the_space_evenly_instead_of_collapsing()
    {
        double[] weights = [0, 0];
        var heights = new double[2];

        PanelStackSolver.Distribute(400, weights, Min, heights);

        Assert.Equal(200, heights[0], Tolerance);
        Assert.Equal(200, heights[1], Tolerance);
    }

    [Fact]
    public void A_slot_under_the_minimum_is_lifted_and_the_slack_comes_from_the_others()
    {
        // 1:20 weights put the first slot far below the minimum; lifting it leaves the second with the rest.
        double[] weights = [1, 20];
        var heights = new double[2];

        PanelStackSolver.Distribute(840, weights, Min, heights);

        Assert.Equal(Min, heights[0], Tolerance);
        Assert.Equal(840 - Min, heights[1], Tolerance);
    }

    [Fact]
    public void Lifting_several_slots_at_once_keeps_the_rest_proportional()
    {
        double[] weights = [1, 1, 10];
        var heights = new double[3];

        PanelStackSolver.Distribute(600, weights, Min, heights);

        Assert.Equal(Min, heights[0], Tolerance);
        Assert.Equal(Min, heights[1], Tolerance);
        Assert.Equal(600 - (2 * Min), heights[2], Tolerance);
    }

    [Fact]
    public void Space_that_cannot_hold_every_minimum_degrades_to_a_proportional_split()
    {
        // Three slots of 80 px need 240 px; 200 px cannot, so nothing is pinned and nothing overflows the region.
        double[] weights = [1, 1, 1];
        var heights = new double[3];

        PanelStackSolver.Distribute(200, weights, Min, heights);

        Assert.Equal(200, heights[0] + heights[1] + heights[2], Tolerance);
        Assert.All(heights, height => Assert.True(height < Min));
    }

    [Fact]
    public void The_slots_always_fill_exactly_the_space_they_are_given()
    {
        double[] weights = [3, 1, 7, 2];
        var heights = new double[4];

        PanelStackSolver.Distribute(517, weights, Min, heights);

        Assert.Equal(517, heights.Sum(), Tolerance);
    }

    [Fact]
    public void A_drag_moves_the_line_by_the_pointer_travel()
    {
        var upper = PanelStackSolver.ResizePair(240, 160, 30, Min);

        Assert.Equal(270, upper, Tolerance);
    }

    [Fact]
    public void A_drag_stops_at_the_minimum_height_of_either_neighbour()
    {
        Assert.Equal(Min, PanelStackSolver.ResizePair(240, 160, -1000, Min), Tolerance);
        Assert.Equal(400 - Min, PanelStackSolver.ResizePair(240, 160, 1000, Min), Tolerance);
    }

    [Fact]
    public void A_pair_too_short_for_two_minimums_does_not_move()
    {
        Assert.Equal(100, PanelStackSolver.ResizePair(100, 50, 40, Min), Tolerance);
    }
}
