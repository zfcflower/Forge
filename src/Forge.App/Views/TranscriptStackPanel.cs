using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;

namespace Forge.App.Views;

/// <summary>
/// The transcript list's panel: Avalonia's virtualizing stack panel that keeps its first visible realized row at the place
/// it was arranged at, through every measure.
/// <para>
/// The stock panel (Avalonia 12.1.6) forgets where its first realized row starts whenever a realized row changes height
/// (a tool row opens, a thinking block opens, an image arrives) or rows are inserted or removed before it (a turn above
/// collapses), and guesses the row at the viewport again as <c>offset / average realized height</c>. With rows from one line
/// to a screen tall that guess is dozens of rows off: the list shows other rows at the same offset, and building that
/// screen of wrong rows costs as much as opening the transcript (measured ~140 ms per toggle on a 40-turn transcript; the
/// rows the user looked at are gone, so the scroll presenter's own anchoring has nothing to hold). Nothing public steers the
/// guess, so this panel uses the path the stock panel's own <c>ScrollIntoView</c> uses: for the length of one measure it
/// names the first visible row (index and container) as the element to start from, at its last arranged top. The two
/// fields are private to Avalonia and reached with <see cref="UnsafeAccessorAttribute"/> (bound at compile time, Native AOT
/// safe); the package is pinned, and should the fields be gone after an upgrade the panel behaves exactly like the stock
/// one (the content anchor still restores the view, at the cost of the extra screen).
/// </para>
/// <para>
/// Nothing else changes: the realized range, recycling, the estimate of the unrealized rows and the extent are the stock
/// panel's. When no realized row reaches into the viewport (a long jump of the scroll bar) the panel guesses as before, which
/// is right for a jump. A <c>ScrollIntoView</c> in progress keeps its own start.
/// </para>
/// </summary>
internal sealed class TranscriptStackPanel : VirtualizingStackPanel
{
    private static bool probed;
    private static bool available;

    private Rect viewport;

    public TranscriptStackPanel() => EffectiveViewportChanged += (_, e) => viewport = e.EffectiveViewport;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_scrollToIndex")]
    private static extern ref int ScrollToIndex(VirtualizingStackPanel panel);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_scrollToElement")]
    private static extern ref Control? ScrollToElement(VirtualizingStackPanel panel);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!Available(this) || viewport.Height <= 0 || viewport.Y <= 0)
        {
            return base.MeasureOverride(availableSize);
        }

        ref var index = ref ScrollToIndex(this);
        ref var element = ref ScrollToElement(this);
        if (index >= 0 || element is not null || FirstVisible() is not { } anchor)
        {
            return base.MeasureOverride(availableSize);
        }

        index = IndexFromContainer(anchor);
        element = anchor;
        try
        {
            return base.MeasureOverride(availableSize);
        }
        finally
        {
            // The measure found the row among the realized ones and left the fields alone; clear them so that nothing
            // after this measure (the next measure, ContainerFromIndex) mistakes the row for a scroll target.
            if (ReferenceEquals(element, anchor))
            {
                element = null;
                index = -1;
            }
        }
    }

    private static bool Available(VirtualizingStackPanel panel)
    {
        if (!probed)
        {
            probed = true;
            try
            {
                _ = ScrollToIndex(panel);
                _ = ScrollToElement(panel);
                available = true;
            }
            catch (Exception exception) when (exception is MissingFieldException or FieldAccessException or BadImageFormatException or TypeLoadException)
            {
                available = false;
            }
        }

        return available;
    }

    /// <summary>
    /// The realized row with the lowest index whose last arranged rectangle reaches into the viewport: the row the stock panel
    /// would start from if it still trusted its positions. The focused row the panel keeps alive outside its realized range
    /// is not among the realized containers.
    /// </summary>
    private Control? FirstVisible()
    {
        Control? best = null;
        var bestIndex = int.MaxValue;
        foreach (var container in GetRealizedContainers() ?? [])
        {
            var bounds = container.Bounds;
            if (bounds.Bottom > viewport.Top && bounds.Top < viewport.Bottom && IndexFromContainer(container) is var at && at >= 0 && at < bestIndex)
            {
                best = container;
                bestIndex = at;
            }
        }

        return best;
    }
}
