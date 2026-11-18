using Avalonia;
using Avalonia.Controls;

namespace Forge.App.Views;

/// <summary>
/// Lays its children out as the stops of the reasoning dial: each child is centred on an anchor evenly spaced from the panel's
/// left edge to its right edge, which is where the thumb stops. The row is inset by the thumb's radius in XAML, so its left edge
/// is the first stop's centre and its right edge the last one's — one panel serves both the notches drawn on the track and the
/// labels under it, which is what keeps the two lined up.
/// <para>
/// <see cref="UniformGrid"/> cannot place them there: equal cells centre every child in the middle of its own cell, up to half a
/// cell away from the thumb's stop, with the ends inset by a full half cell (measured 8–10 px off on a five-step dial). Labels
/// are measured at their natural width — a name is never trimmed — and neighbours are pushed apart when they would overlap, so
/// the text stays whole; the labels that would collide at the ends simply reach further into the popup's padding.
/// </para>
/// </summary>
internal sealed class EffortTickPanel : Panel
{
    /// <summary>The smallest clear space left between two neighbouring labels.</summary>
    private const double MinGap = 4;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0.0;
        var height = 0.0;
        foreach (var child in Children)
        {
            child.Measure(Size.Infinity);
            width = Math.Max(width, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = Children.Count;
        if (count == 0)
        {
            return finalSize;
        }

        var last = count - 1;
        var lefts = new double[count];
        for (var index = 0; index < count; index++)
        {
            var width = Children[index].DesiredSize.Width;
            // One stop has no ends to span: it sits where the thumb's only position is, in the row's centre.
            var anchor = last == 0 ? finalSize.Width / 2 : finalSize.Width * index / last;
            // Never outside the row: a label that overhung it would widen the popup's content and run under its scroll bar
            // (measured: the flyout grew a horizontal bar that covered the labels). The end labels therefore give up their exact
            // centres — the notches on the track are what mark the stops — and the middle ones stay on theirs.
            var limit = Math.Max(0, finalSize.Width - width);
            lefts[index] = Math.Clamp(anchor - (width / 2), 0, limit);
        }

        // Whole names beat exact centres: a label that would run into its neighbour is pushed right instead of being trimmed.
        for (var index = 1; index < count; index++)
        {
            var previousRight = lefts[index - 1] + Children[index - 1].DesiredSize.Width + MinGap;
            if (lefts[index] < previousRight)
            {
                lefts[index] = previousRight;
            }
        }

        // A push can land the last label past the edge; slide the whole row back inside instead of letting it stick out.
        var overflow = lefts[last] + Children[last].DesiredSize.Width - finalSize.Width;
        if (overflow > 0)
        {
            for (var index = 0; index < count; index++)
            {
                lefts[index] = Math.Max(0, lefts[index] - overflow);
            }
        }

        for (var index = 0; index < count; index++)
        {
            var child = Children[index];
            var width = child.DesiredSize.Width;
            var height = Math.Min(child.DesiredSize.Height, finalSize.Height);
            child.Arrange(new Rect(lefts[index], (finalSize.Height - height) / 2, width, height));
        }

        return finalSize;
    }
}
