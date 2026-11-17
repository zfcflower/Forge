using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

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

    /// <summary>When a key last went down inside the list (<see cref="Environment.TickCount64"/>).</summary>
    private long keyAt = -KeyWindow;

    /// <summary>How long after a key a request to scroll something inside a row into view is taken to be the key's.</summary>
    private const long KeyWindow = 1000;

    public TranscriptStackPanel()
    {
        EffectiveViewportChanged += (_, e) => viewport = e.EffectiveViewport;
        AddHandler(KeyDownEvent, (_, _) => keyAt = Environment.TickCount64, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(RequestBringIntoViewEvent, OnRequestBringIntoView, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    /// <summary>
    /// A click inside a row must not scroll the list. The text of a row (a tool's output, a reply) asks to be brought into view
    /// as a whole when it takes focus, and a tall block then drags the list to its own top or bottom under the pointer; the
    /// focus can arrive before the press is routed, so the press cannot be what is checked. A press on something that is not
    /// focusable (a label, a gap, a caption) focuses the row itself, and the scroll viewer's focus handler then asks for the
    /// whole row: a row taller than what shows has its bottom aligned, so the list jumps to the end of an open row. Only a
    /// request that is not a focus change is let through for a row (<c>ScrollIntoView</c>, the sticky message, the follower),
    /// and one from inside a row only, like a focus change on a row, while the keyboard drives, so Tab or the arrow keys
    /// through a row's controls still scroll to the focused one.
    /// </summary>
    private void OnRequestBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (Environment.TickCount64 - keyAt < KeyWindow || e.TargetObject is not Visual target)
        {
            return;
        }

        if (!ReferenceEquals(target.GetVisualParent(), this) || target is Avalonia.Input.InputElement { IsFocused: true })
        {
            e.Handled = true;
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_scrollToIndex")]
    private static extern ref int ScrollToIndex(VirtualizingStackPanel panel);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_scrollToElement")]
    private static extern ref Control? ScrollToElement(VirtualizingStackPanel panel);

    /// <summary>How many arranges put row 0 back at the top of the content (diagnostics for the bench).</summary>
    internal static int Reconciled;

    protected override Size MeasureOverride(Size availableSize) => MeasurePinned(availableSize);

    /// <summary>
    /// The stock panel fills the viewport backwards from its anchor row down to row 0 and stops there, whatever position
    /// the anchor was estimated at: after a long jump of the scroll bar the anchor's position is an estimate from average
    /// row heights, so row 0 can end up hundreds of DIP below (a blank band above the first row, seen dragging the thumb
    /// toward the top of a long transcript) or above (its top cut off, out of reach) the start of the content. Row 0 starts
    /// at 0 by definition, so when it was arranged elsewhere every realized row is moved by the difference and the panel
    /// measures once more: that measure starts from the first visible row where it now stands (the pin above), fills the
    /// viewport below it, and the extent follows from the corrected position.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (FirstRealizedIndex == 0 && ContainerFromIndex(0) is { } first && Math.Abs(first.Bounds.Top) > 0.5)
        {
            var shift = first.Bounds.Top;
            foreach (var container in GetRealizedContainers() ?? [])
            {
                var bounds = container.Bounds;
                container.Arrange(new Rect(bounds.X, bounds.Y - shift, bounds.Width, bounds.Height));
            }

            Reconciled++;
            InvalidateMeasure();
        }

        return size;
    }

    private Size MeasurePinned(Size availableSize)
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

    /// <summary>Whether the pinned Avalonia still has the two private fields the panel steers by (the bench fails when it has not).</summary>
    internal static bool CanSteer() => Available(new TranscriptStackPanel());

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
