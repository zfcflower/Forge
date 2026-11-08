using Avalonia;
using Forge.App.Review;
using Xunit;

namespace Forge.App.Tests.Review;

/// <summary>
/// Pins the icon's placement rule: bottom right of the anchor, flipped to the left when that would leave the viewport,
/// and always clipped inside it.
/// </summary>
public class ReviewIconPlacementTests
{
    private static readonly Rect Viewport = new(0, 0, 800, 600);

    [Fact]
    public void The_icon_sits_at_the_bottom_right_of_the_anchor()
    {
        var placed = ReviewIconPlacement.Find(Viewport, new Point(200, 100));

        Assert.Equal(200 + ReviewIconPlacement.Gap, placed.X);
        Assert.Equal(100 + ReviewIconPlacement.Gap, placed.Y);
    }

    [Fact]
    public void An_anchor_near_the_right_edge_flips_the_icon_to_its_left()
    {
        var anchor = new Point(Viewport.Right - 20, 100);

        var placed = ReviewIconPlacement.Find(Viewport, anchor);

        Assert.Equal(anchor.X - ReviewIconPlacement.IconSize - ReviewIconPlacement.Gap, placed.X);
        Assert.InRange(placed.X + ReviewIconPlacement.IconSize, Viewport.Left, Viewport.Right);
    }

    [Fact]
    public void An_anchor_beyond_the_right_edge_still_keeps_the_icon_fully_inside()
    {
        var placed = ReviewIconPlacement.Find(Viewport, new Point(5000, 100));

        Assert.Equal(Viewport.Right - ReviewIconPlacement.Edge - ReviewIconPlacement.IconSize, placed.X);
        Assert.InRange(placed.X, Viewport.Left, Viewport.Right);
    }

    [Fact]
    public void An_anchor_above_or_below_the_viewport_keeps_the_icon_inside()
    {
        var top = ReviewIconPlacement.Find(Viewport, new Point(100, -400));
        var bottom = ReviewIconPlacement.Find(Viewport, new Point(100, 4000));

        Assert.Equal(Viewport.Top + ReviewIconPlacement.Edge, top.Y);
        Assert.Equal(Viewport.Bottom - ReviewIconPlacement.Edge - ReviewIconPlacement.IconSize, bottom.Y);
    }

    [Fact]
    public void A_viewport_smaller_than_the_icon_does_not_produce_an_inverted_range()
    {
        var placed = ReviewIconPlacement.Find(new Rect(0, 0, 20, 20), new Point(10, 10));

        Assert.Equal(ReviewIconPlacement.Edge, placed.X);
        Assert.Equal(ReviewIconPlacement.Edge, placed.Y);
    }
}
