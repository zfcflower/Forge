using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Forge.App.Views;

/// <summary>
/// Makes the vertical scroll bar's thumb of a virtualizing list follow the mouse. The list's extent is an estimate (the
/// unrealized rows count as the average of the realized ones) and changes with every row that scrolls in, and the thumb's
/// length and place are derived from it: dragged slowly across a long transcript, the thumb grew and shrank by several
/// pixels a step and ran up to 25 px away from the pointer. While the thumb is held, its geometry (maximum, viewport, value)
/// is therefore pinned to what it was when it was grabbed, and the list is scrolled by the thumb's value as before: the same
/// scale as the thumb, so the content moves in step with the mouse and never against it. The last stretch of the track is
/// the end of the content however the estimate moved meanwhile, so dragging to the bottom always lands on the bottom. When the
/// thumb is released the pinned values are dropped and the bar shows the list's own geometry again.
/// </summary>
internal sealed class ScrollThumbDrag
{
    /// <summary>The share of the track from which the thumb stands for the end of the content.</summary>
    private const double EndShare = 0.995;

    private readonly ScrollViewer scroller;
    private ScrollBar? pinned;

    private ScrollThumbDrag(ScrollViewer scroller)
    {
        this.scroller = scroller;
        scroller.AddHandler(Thumb.DragStartedEvent, OnDragStarted, RoutingStrategies.Bubble, handledEventsToo: true);
        scroller.AddHandler(Thumb.DragCompletedEvent, OnDragCompleted, RoutingStrategies.Bubble, handledEventsToo: true);
        scroller.DetachedFromVisualTree += (_, _) => Release();
    }

    public static ScrollThumbDrag Attach(ScrollViewer scroller) => new(scroller);

    /// <summary>Stops listening (the scroller is replaced when the list's template is applied again).</summary>
    public void Detach()
    {
        Release();
        scroller.RemoveHandler(Thumb.DragStartedEvent, OnDragStarted);
        scroller.RemoveHandler(Thumb.DragCompletedEvent, OnDragCompleted);
    }

    private void OnDragStarted(object? sender, VectorEventArgs e)
    {
        Release();
        // Only this scroll viewer's own bar drives this list: a nested viewer inside a row (a tool body's output) raises the
        // same events, and dragging its thumb used to write that small value into the transcript's offset — the list jumped
        // to the top while the pointer was on an inner scroll bar.
        if (e.Source is not Visual visual
            || visual.FindAncestorOfType<ScrollBar>(includeSelf: true) is not { Orientation: Orientation.Vertical } bar
            || !ReferenceEquals(bar.FindAncestorOfType<ScrollViewer>(), scroller)
            || bar.Maximum <= bar.Minimum)
        {
            return;
        }

        // Local values rank above the template's bindings to the scroll viewer, so they hold until they are cleared.
        bar.SetValue(RangeBase.MaximumProperty, bar.Maximum);
        bar.SetValue(ScrollBar.ViewportSizeProperty, bar.ViewportSize);
        bar.SetValue(RangeBase.ValueProperty, bar.Value);
        bar.PropertyChanged += OnBarPropertyChanged;
        pinned = bar;
    }

    private void OnDragCompleted(object? sender, VectorEventArgs e) => Release();

    private void OnBarPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != RangeBase.ValueProperty || sender is not ScrollBar bar || bar.Maximum <= bar.Minimum)
        {
            return;
        }

        // The pinned value is the scroll offset on the scale the list had when the thumb was grabbed; the panel clamps it to the
        // extent it has now. The bottom stretch ignores that scale: the extent has probably grown or shrunk since.
        var share = (bar.Value - bar.Minimum) / (bar.Maximum - bar.Minimum);
        var offset = share >= EndShare ? Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height) : bar.Value;
        scroller.Offset = new Vector(scroller.Offset.X, offset);
    }

    private void Release()
    {
        if (pinned is not { } bar)
        {
            return;
        }

        pinned = null;
        bar.PropertyChanged -= OnBarPropertyChanged;
        bar.ClearValue(RangeBase.ValueProperty);
        bar.ClearValue(RangeBase.MaximumProperty);
        bar.ClearValue(ScrollBar.ViewportSizeProperty);
    }
}
