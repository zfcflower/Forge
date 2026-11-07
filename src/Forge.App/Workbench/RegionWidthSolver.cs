namespace Forge.App.Workbench;

/// <summary>
/// Pure width arithmetic of the three regions; see the "区域宽度分配" section of the forge-app spec. Every span is
/// indexed by visual position (left to right). Nothing here allocates, so a drag can call it on every pointer move.
/// </summary>
internal static class RegionWidthSolver
{
    private const double Tolerance = 1e-6;

    /// <summary>
    /// Recomputes the widths for a region-splitter drag, always from the widths at drag start so dragging back undoes.
    /// <list type="bullet">
    /// <item>Only docked regions take part: <paramref name="startWidths"/> lists them in visual order and splitter
    /// <paramref name="splitterIndex"/> sits between region <c>splitterIndex</c> and <c>splitterIndex + 1</c>.</item>
    /// <item><paramref name="delta"/> &gt; 0 drags right: the region left of the splitter grows; &lt; 0 the right one grows.</item>
    /// <item>On the shrinking side, Main shrinks to its minimum first, then the other regions nearest-first, each to its
    /// minimum. A side region pushed more than <see cref="WorkbenchMetrics.CollapseOvershoot"/> × its minimum past it
    /// collapses: <paramref name="collapsed"/> is set and its width is left at its start width (the width it returns
    /// with); the growing region then takes all the space the others leave.</item>
    /// <item>The sum of the widths of regions that are not collapsed always equals the sum of
    /// <paramref name="startWidths"/>; demand that no region can give is dropped. Main never collapses.</item>
    /// </list>
    /// </summary>
    public static void Drag(
        ReadOnlySpan<double> startWidths,
        ReadOnlySpan<double> minWidths,
        int mainIndex,
        int splitterIndex,
        double delta,
        Span<double> widths,
        Span<bool> collapsed)
    {
        int count = startWidths.Length;
        for (int i = 0; i < count; i++)
        {
            widths[i] = startWidths[i];
            collapsed[i] = false;
        }

        if (count < 2
            || splitterIndex < 0
            || splitterIndex >= count - 1
            || minWidths.Length < count
            || widths.Length < count
            || collapsed.Length < count
            || mainIndex < 0
            || mainIndex >= count
            || !double.IsFinite(delta))
        {
            return;
        }

        double need = Math.Abs(delta);
        if (need <= Tolerance)
        {
            return;
        }

        bool growsLeft = delta > 0;
        int growing = growsLeft ? splitterIndex : splitterIndex + 1;

        bool mainShrinks = growsLeft ? mainIndex > splitterIndex : mainIndex <= splitterIndex;
        if (mainShrinks)
        {
            ShrinkToMinimum(widths, minWidths, mainIndex, ref need);
        }

        if (growsLeft)
        {
            for (int i = splitterIndex + 1; i < count && need > Tolerance; i++)
            {
                if (i == mainIndex)
                {
                    continue;
                }

                CollapseOrShrink(widths, minWidths, i, ref need, collapsed);
            }
        }
        else
        {
            for (int i = splitterIndex; i >= 0 && need > Tolerance; i--)
            {
                if (i == mainIndex)
                {
                    continue;
                }

                CollapseOrShrink(widths, minWidths, i, ref need, collapsed);
            }
        }

        double total = 0;
        for (int i = 0; i < count; i++)
        {
            total += startWidths[i];
        }

        double others = 0;
        for (int i = 0; i < count; i++)
        {
            if (i != growing && !collapsed[i])
            {
                others += widths[i];
            }
        }

        widths[growing] = Math.Max(0, total - others);
    }

    /// <summary>
    /// Fits the regions into the window after a window resize, a pin/unpin, a swap, a collapse or a preview-width
    /// change. Spans cover all three regions in visual order.
    /// <list type="bullet">
    /// <item><paramref name="hidden"/> marks regions the user already auto-hid; they take no width and are kept as is.</item>
    /// <item>The space to fill is <paramref name="windowWidth"/> minus one <see cref="WorkbenchMetrics.RegionSplitterWidth"/>
    /// per gap between docked regions and one <see cref="WorkbenchMetrics.AutoHideStripWidth"/> per window edge that
    /// carries at least one auto-hidden side region (<paramref name="edges"/> gives each side region's edge; Main's entry is ignored).</item>
    /// <item>Surplus goes to Main. A shortfall shrinks Main to its minimum, then the side regions in
    /// <paramref name="shrinkOrder"/> (visual indices, Inspector first) to their minimum, then hides them in the same
    /// order until it fits; hiding recomputes the chrome. <paramref name="protectedIndex"/> (or -1) shrinks but never
    /// hides — the region the user just pinned. If nothing more can hide, Main takes what is left, never below 0.</item>
    /// <item>Main also ends at its minimum width when the total fits but the side regions would squeeze it below that:
    /// the same shrink-then-hide pass runs, so the space comes out of the side regions instead of Main.</item>
    /// <item><paramref name="outHidden"/> is the union of <paramref name="hidden"/> and the regions hidden here; a hidden
    /// region's <paramref name="outWidths"/> entry keeps its last docked width.</item>
    /// </list>
    /// </summary>
    public static void Fit(
        ReadOnlySpan<double> widths,
        ReadOnlySpan<double> minWidths,
        ReadOnlySpan<bool> hidden,
        ReadOnlySpan<RegionEdge> edges,
        int mainIndex,
        ReadOnlySpan<int> shrinkOrder,
        int protectedIndex,
        double windowWidth,
        Span<double> outWidths,
        Span<bool> outHidden)
    {
        int count = widths.Length;
        for (int i = 0; i < count; i++)
        {
            outWidths[i] = widths[i];
            outHidden[i] = hidden[i];
        }

        if (count == 0
            || mainIndex < 0
            || mainIndex >= count
            || minWidths.Length < count
            || outWidths.Length < count
            || outHidden.Length < count)
        {
            return;
        }

        double window = double.IsFinite(windowWidth) && windowWidth > 0 ? windowWidth : 0;
        double available = AvailableWidth(outHidden, edges, mainIndex, window);
        double need = DockedWidth(outWidths, outHidden) - available;

        if (need > Tolerance)
        {
            ShrinkToMinimum(outWidths, minWidths, mainIndex, ref need);

            for (int k = 0; k < shrinkOrder.Length && need > Tolerance; k++)
            {
                int index = shrinkOrder[k];
                if (index < 0 || index >= count || index == mainIndex || outHidden[index])
                {
                    continue;
                }

                ShrinkToMinimum(outWidths, minWidths, index, ref need);
            }

            for (int k = 0; k < shrinkOrder.Length && need > Tolerance; k++)
            {
                int index = shrinkOrder[k];
                if (index < 0 || index >= count || index == mainIndex || index == protectedIndex || outHidden[index])
                {
                    continue;
                }

                outHidden[index] = true;
                available = AvailableWidth(outHidden, edges, mainIndex, window);
                need = DockedWidth(outWidths, outHidden) - available;
            }
        }

        double others = 0;
        for (int i = 0; i < count; i++)
        {
            if (i != mainIndex && !outHidden[i])
            {
                others += outWidths[i];
            }
        }

        double mainMinimum = minWidths[mainIndex];
        double mainWidth = available - others;
        if (mainWidth + Tolerance < mainMinimum)
        {
            double deficit = mainMinimum - mainWidth;

            for (int k = 0; k < shrinkOrder.Length && deficit > Tolerance; k++)
            {
                int index = shrinkOrder[k];
                if (index < 0 || index >= count || index == mainIndex || outHidden[index])
                {
                    continue;
                }

                ShrinkToMinimum(outWidths, minWidths, index, ref deficit);
            }

            others = DockedWidthExceptMain(outWidths, outHidden, mainIndex);
            deficit = mainMinimum - (available - others);

            for (int k = 0; k < shrinkOrder.Length && deficit > Tolerance; k++)
            {
                int index = shrinkOrder[k];
                if (index < 0 || index >= count || index == mainIndex || index == protectedIndex || outHidden[index])
                {
                    continue;
                }

                outHidden[index] = true;
                available = AvailableWidth(outHidden, edges, mainIndex, window);
                others = DockedWidthExceptMain(outWidths, outHidden, mainIndex);
                deficit = mainMinimum - (available - others);
            }
        }

        outWidths[mainIndex] = Math.Max(0, available - others);
    }

    /// <summary>Width of the docked side regions, Main left out.</summary>
    private static double DockedWidthExceptMain(ReadOnlySpan<double> widths, ReadOnlySpan<bool> hidden, int mainIndex)
    {
        double total = 0;
        for (int i = 0; i < widths.Length; i++)
        {
            if (i != mainIndex && !hidden[i])
            {
                total += widths[i];
            }
        }

        return total;
    }

    private static void CollapseOrShrink(
        Span<double> widths,
        ReadOnlySpan<double> minWidths,
        int index,
        ref double need,
        Span<bool> collapsed)
    {
        double minimum = minWidths[index];
        double available = Math.Max(0, widths[index] - minimum);

        if (need > available + (WorkbenchMetrics.CollapseOvershoot * minimum))
        {
            collapsed[index] = true;
            need -= widths[index];
            return;
        }

        double give = Math.Min(available, need);
        widths[index] -= give;
        need -= give;
    }

    private static void ShrinkToMinimum(
        Span<double> widths,
        ReadOnlySpan<double> minWidths,
        int index,
        ref double need)
    {
        double available = Math.Max(0, widths[index] - minWidths[index]);
        double give = Math.Min(available, need);
        widths[index] -= give;
        need -= give;
    }

    private static double AvailableWidth(
        ReadOnlySpan<bool> hidden,
        ReadOnlySpan<RegionEdge> edges,
        int mainIndex,
        double windowWidth)
    {
        int docked = 0;
        bool leftStrip = false;
        bool rightStrip = false;

        for (int i = 0; i < hidden.Length; i++)
        {
            if (!hidden[i])
            {
                docked++;
            }

            if (i == mainIndex)
            {
                continue;
            }

            if (!hidden[i])
            {
                continue;
            }

            RegionEdge edge = i < edges.Length ? edges[i] : RegionEdge.Left;
            if (edge == RegionEdge.Right)
            {
                rightStrip = true;
            }
            else
            {
                leftStrip = true;
            }
        }

        double chrome = docked > 1 ? (docked - 1) * WorkbenchMetrics.RegionSplitterWidth : 0;
        if (leftStrip)
        {
            chrome += WorkbenchMetrics.AutoHideStripWidth;
        }

        if (rightStrip)
        {
            chrome += WorkbenchMetrics.AutoHideStripWidth;
        }

        return windowWidth - chrome;
    }

    private static double DockedWidth(ReadOnlySpan<double> widths, ReadOnlySpan<bool> hidden)
    {
        double total = 0;
        for (int i = 0; i < widths.Length; i++)
        {
            if (!hidden[i])
            {
                total += widths[i];
            }
        }

        return total;
    }
}
