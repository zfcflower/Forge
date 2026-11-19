using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.App.ViewModels;

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
    private static bool extentProbed;
    private static bool extentAvailable;

    /// <summary>The height a row last had when it was laid out, by row (weak: a removed row takes its entry with it).</summary>
    private readonly ConditionalWeakTable<object, HeightBox> heights = new();

    /// <summary>The sum and count of <see cref="heights"/> per row type, for rows nobody has seen yet.</summary>
    private readonly Dictionary<Type, TypeHeights> byType = [];

    private double lastWidth = double.NaN;
    private double lastExtent = double.NaN;

    /// <summary>What the rows below the realized ones are priced by changed since the last measure (a type seen for the first time, a row removed): that measure answered with the old figures.</summary>
    private bool repriced;

    private sealed class HeightBox(double height)
    {
        public double Height = height;
    }

    private sealed class TypeHeights
    {
        public double Sum;
        public int Count;
    }

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

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_lastEstimatedElementSizeU")]
    private static extern ref double LastEstimatedElementSize(VirtualizingStackPanel panel);

    /// <summary>How many arranges put row 0 back at the top of the content (diagnostics for the bench).</summary>
    internal static int Reconciled;

    protected override Size MeasureOverride(Size availableSize) => CorrectExtent(MeasurePinned(availableSize));

    /// <summary>
    /// The stock panel's extent is the end of the realized rows plus every row below them times one number, the average of the
    /// rows it has realized right now. Rows run from one line to a screen, so that number swings with whatever kinds of rows are
    /// on screen, and the scroll bar's thumb (its length and place both come from the extent) swings with it. Here the rows below
    /// the realized range count with what is known about them: the height the row itself had when it was last laid out, and for a
    /// row never seen the average of the rows of its type that were. The end of the realized rows is taken from what the stock
    /// panel returned (it is the part of its figure that is not an estimate); nothing else about the panel's measure changes.
    /// The cache is dropped when the width changes (text wraps again) and when the rows are reset.
    /// </summary>
    private Size CorrectExtent(Size size)
    {
        if (!ExtentAvailable(this) || Orientation != Avalonia.Layout.Orientation.Vertical || Items is not { Count: > 0 } items)
        {
            return size;
        }

        var last = LastRealizedIndex;
        var remaining = items.Count - last - 1;
        if (last < 0 || remaining <= 0 || byType.Count == 0 || size.Height == lastExtent ||
            (ScrollToIndex(this) >= 0 && ScrollToElement(this) is not null))
        {
            // Nothing below to estimate, nothing known yet, or a measure the stock panel answered without realizing (waiting for a
            // scroll to land: its figure is the one this method returned last, or is based on the scroll target).
            return size;
        }

        var average = LastEstimatedElementSize(this);
        var realizedEnd = size.Height - (remaining * average);
        var below = 0d;
        for (var i = last + 1; i < items.Count; i++)
        {
            below += KnownHeight(items[i], average);
        }

        lastExtent = realizedEnd + below;
        return new Size(size.Width, lastExtent);
    }

    /// <summary>
    /// What an unrealized item is worth. An item is a round, and a round is priced by its rows: every row already laid out
    /// contributes its measured height, and a row nobody has seen yet the average of the rows of its type. That is the same
    /// knowledge the flat row table had (one height per row), only summed over the round's own rows instead of averaged over
    /// whatever happens to be on screen — so the thumb's place and length stay where the rows put them.
    /// </summary>
    private double KnownHeight(object? item, double fallback)
    {
        if (item is null)
        {
            return fallback;
        }

        if (item is TranscriptRoundViewModel round)
        {
            var total = 0d;
            foreach (var row in round.Rows)
            {
                total += KnownRowHeight(row, fallback);
            }

            return total;
        }

        return KnownRowHeight(item, fallback);
    }

    private double KnownRowHeight(object row, double fallback)
    {
        if (heights.TryGetValue(row, out var box))
        {
            return box.Height;
        }

        return byType.TryGetValue(row.GetType(), out var type) && type.Count > 0 ? type.Sum / type.Count : fallback;
    }

    private void Remember(object item, double height)
    {
        if (heights.TryGetValue(item, out var box))
        {
            Of(item).Sum += height - box.Height;
            box.Height = height;
            return;
        }

        heights.Add(item, new HeightBox(height));
        var type = Of(item);
        type.Sum += height;
        type.Count++;
    }

    private TypeHeights Of(object item)
    {
        var key = item.GetType();
        if (!byType.TryGetValue(key, out var type))
        {
            byType[key] = type = new TypeHeights();
            repriced = true;
        }

        return type;
    }

    private void Forget(object? item)
    {
        if (item is not null && heights.TryGetValue(item, out var box))
        {
            var type = Of(item);
            type.Sum -= box.Height;
            type.Count--;
            repriced = true;
            heights.Remove(item);
        }
    }

    private void ForgetAll()
    {
        heights.Clear();
        byType.Clear();
        lastExtent = double.NaN;
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(items, e);
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ForgetAll();
        }
        else if (e.OldItems is { } removed)
        {
            foreach (var item in removed)
            {
                Forget(item);
            }
        }
    }

    /// <summary>Records the height of every realized row (the end of an arrange, when the sizes are final).</summary>
    private void Sample(Size finalSize)
    {
        if (!double.IsNaN(lastWidth) && Math.Abs(lastWidth - finalSize.Width) > 0.5)
        {
            ForgetAll();
        }

        lastWidth = finalSize.Width;
        if (Items is not { } items)
        {
            return;
        }

        for (var i = FirstRealizedIndex; i >= 0 && i <= LastRealizedIndex && i < items.Count; i++)
        {
            if (items[i] is not { } item || ContainerFromIndex(i) is not { IsMeasureValid: true } container)
            {
                continue;
            }

            // A realized row reports its own height; a round also reports every row it hosts, which is what prices the rounds
            // nobody has realized yet.
            if (item is not TranscriptRoundViewModel round)
            {
                Remember(item, container.DesiredSize.Height);
                continue;
            }

            {
                var rows = container.GetVisualDescendants().OfType<ItemsControl>()
                    .FirstOrDefault(c => ReferenceEquals(c.ItemsSource, round.Rows));
                if (rows is not null)
                {
                    for (var r = 0; r < round.Rows.Count; r++)
                    {
                        if (rows.ContainerFromIndex(r) is { IsMeasureValid: true } rowContainer)
                        {
                            Remember(round.Rows[r], rowContainer.DesiredSize.Height);
                        }
                    }
                }
            }
        }
    }

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
        if (ExtentAvailable(this))
        {
            Sample(finalSize);
            if (repriced)
            {
                // Once per change of the pricing (a new row type, a removed row, a cache drop): measure again so the extent counts the real
                // height now instead of at the next scroll.
                repriced = false;
                InvalidateMeasure();
            }
        }

        if (FirstRealizedIndex == 0 && ContainerFromIndex(0) is { } first && Math.Abs(first.Bounds.Top) > 0.5)
        {
            var shift = first.Bounds.Top;
            var bottom = 0d;
            foreach (var container in GetRealizedContainers() ?? [])
            {
                bottom = Math.Max(bottom, container.Bounds.Bottom);
            }

            if (viewport.Height > 0 && Items is { } all && bottom - shift < viewport.Y)
            {
                // Row 0 was reached from an estimated anchor that was off by more than the viewport is from the top (a drag to the top
                // of a long transcript walks rows whose estimated places drifted by the estimate's error): moving the rows to 0 would
                // leave the viewport with nothing in it. The offset is what the user put the thumb at, so drop the realized rows and
                // let the next measure pick its anchor from the offset again.
                base.OnItemsChanged(all, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
                Reconciled++;
                InvalidateMeasure();
                return size;
            }

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

    /// <summary>Whether the pinned Avalonia still has the three private fields the panel steers and prices by (the bench fails when it has not).</summary>
    internal static bool CanSteer() => Available(new TranscriptStackPanel()) && ExtentAvailable(new TranscriptStackPanel());

    private static bool ExtentAvailable(VirtualizingStackPanel panel)
    {
        if (!extentProbed)
        {
            extentProbed = true;
            try
            {
                _ = LastEstimatedElementSize(panel);
                _ = ScrollToIndex(panel);
                _ = ScrollToElement(panel);
                extentAvailable = true;
            }
            catch (Exception exception) when (exception is MissingFieldException or FieldAccessException or BadImageFormatException or TypeLoadException)
            {
                extentAvailable = false;
            }
        }

        return extentAvailable;
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
