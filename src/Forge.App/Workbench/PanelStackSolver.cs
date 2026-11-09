namespace Forge.App.Workbench;

/// <summary>
/// Divides the height of one panel stack (the panels of a region stacked above each other) between its slots, and moves the
/// line between two neighbouring slots. Pure and allocation-free: the presenter passes its reusable buffers in, so dragging
/// or arranging allocates nothing. <see cref="WorkbenchMetrics.PanelMinHeight"/> is the floor every slot keeps while the
/// stack has room for it.
/// </summary>
internal static class PanelStackSolver
{
    /// <summary>Slots one call handles without allocating; a region never stacks more panels than this.</summary>
    public const int MaxSlots = 8;

    /// <summary>
    /// Fills <paramref name="heights"/> with the share of <paramref name="space"/> each slot takes in proportion to its
    /// weight, lifting every slot that would fall under <paramref name="minHeight"/> up to it and taking that height from the
    /// slots that still have slack. A weight of zero (or a set of weights that sums to zero) means "no opinion": those slots
    /// split the space evenly. When the space cannot hold every minimum, the slots take proportional shares instead, so the
    /// stack degrades to a proportional split rather than overflowing its region.
    /// </summary>
    public static void Distribute(double space, ReadOnlySpan<double> weights, double minHeight, Span<double> heights)
    {
        var count = weights.Length;
        if (count == 0 || heights.Length < count)
        {
            return;
        }

        space = Math.Max(0, space);
        if (count > MaxSlots || space < count * minHeight)
        {
            ShareAll(space, weights, heights, count);
            return;
        }

        Span<bool> pinned = stackalloc bool[MaxSlots];
        for (var i = 0; i < count; i++)
        {
            pinned[i] = false;
        }

        var remaining = space;
        var free = count;
        while (free > 0)
        {
            ShareFree(remaining, weights, heights, count, pinned);
            var smallest = -1;
            for (var i = 0; i < count; i++)
            {
                if (!pinned[i] && (smallest < 0 || heights[i] < heights[smallest]))
                {
                    smallest = i;
                }
            }

            if (smallest < 0 || heights[smallest] >= minHeight)
            {
                return;
            }

            pinned[smallest] = true;
            heights[smallest] = minHeight;
            remaining = Math.Max(0, remaining - minHeight);
            free--;
        }
    }

    /// <summary>
    /// Where the line between two slots lands after a drag of <paramref name="delta"/>: the pair keeps its combined height and
    /// neither side goes under <paramref name="minHeight"/>. Returns <paramref name="upperHeight"/> unchanged when the pair is
    /// already too short to hold two minimums.
    /// </summary>
    public static double ResizePair(double upperHeight, double lowerHeight, double delta, double minHeight)
    {
        var total = upperHeight + lowerHeight;
        if (!double.IsFinite(total) || total < 2 * minHeight)
        {
            return upperHeight;
        }

        return Math.Clamp(upperHeight + delta, minHeight, total - minHeight);
    }

    /// <summary>Weights of the slots that are still free, summed; a zero count means the space splits evenly.</summary>
    private static void ShareAll(double remaining, ReadOnlySpan<double> weights, Span<double> heights, int count)
    {
        var sum = 0.0;
        for (var i = 0; i < count; i++)
        {
            sum += Positive(weights[i]);
        }

        for (var i = 0; i < count; i++)
        {
            heights[i] = sum > 0 ? remaining * Positive(weights[i]) / sum : remaining / count;
        }
    }

    /// <summary>As <see cref="ShareAll"/>, skipping the slots already pinned at the minimum height.</summary>
    private static void ShareFree(
        double remaining,
        ReadOnlySpan<double> weights,
        Span<double> heights,
        int count,
        ReadOnlySpan<bool> pinned)
    {
        var sum = 0.0;
        var free = 0;
        for (var i = 0; i < count; i++)
        {
            if (pinned[i])
            {
                continue;
            }

            sum += Positive(weights[i]);
            free++;
        }

        for (var i = 0; i < count; i++)
        {
            if (pinned[i])
            {
                continue;
            }

            heights[i] = sum > 0 ? remaining * Positive(weights[i]) / sum : remaining / free;
        }
    }

    private static double Positive(double weight) => double.IsFinite(weight) && weight > 0 ? weight : 0;
}
