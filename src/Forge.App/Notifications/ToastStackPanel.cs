using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Notifications;

/// <summary>Where each toast goes: a pure function of the heights, so the stacking arithmetic is testable on its own.</summary>
internal static class ToastStackLayout
{
    /// <summary>Top offset of each item, in the order given.</summary>
    public static double[] Offsets(IReadOnlyList<double> heights, double spacing)
    {
        var offsets = new double[heights.Count];
        var top = 0d;
        for (var i = 0; i < offsets.Length; i++)
        {
            offsets[i] = top;
            top += heights[i];
            if (i + 1 < offsets.Length)
            {
                top += spacing;
            }
        }

        return offsets;
    }
}

/// <summary>
/// The toast stack: the children (the item containers of the overlay's <see cref="ItemsControl"/>) are stacked vertically,
/// <see cref="Spacing"/> apart, top-aligned and stretched to the panel's width. Because toasts arrive newest first and leave
/// from anywhere in the stack, a layout pass changes the position of every toast below the one that came or went, so the
/// panel slides each moved child from where it was to where it now belongs instead of letting it jump:
/// <list type="bullet">
/// <item>every moved child gets a <see cref="TranslateTransform"/> in <see cref="Visual.RenderTransformProperty"/>;</item>
/// <item>a child whose target moved is placed at its old visual position (the difference between the two targets plus
/// whatever is left of a running slide) and an <see cref="Animation"/> takes that offset to zero over
/// <see cref="ReflowDuration"/> with an ease-out, which is the window the center waits before it removes a leaving item;</item>
/// <item>a child that shows up for the first time has no previous position and is not animated.</item>
/// </list>
/// The animation runs on the child (the framework animates a <see cref="Visual"/>, not a transform object), so it writes
/// <see cref="OffsetProperty"/> and the changed offset is mirrored onto the child's transform; that keeps the offset
/// readable while a slide is in flight, which is what an interrupted slide continues from.
/// <para>A child that <see cref="ItemsControl"/> takes away (containers are recycled) is stopped and stripped of its
/// transform in the next arrange, so nothing of one item survives into the next. 0 to <c>5</c> children are the only states
/// the shell asks for, and every one of them is handled.</para>
/// </summary>
internal sealed class ToastStackPanel : Panel
{
    /// <summary>Vertical gap between two toasts.</summary>
    internal const double Spacing = 8;

    /// <summary>How long a toast takes to slide to the place its neighbours left it.</summary>
    internal static readonly TimeSpan ReflowDuration = TimeSpan.FromMilliseconds(180);

    /// <summary>The slide offset of one toast, in pixels: the reflow animation writes it, the child's transform shows it.</summary>
    private static readonly AttachedProperty<double> OffsetProperty =
        AvaloniaProperty.RegisterAttached<ToastStackPanel, Control, double>("Offset");

    /// <summary>How far a toast starts from its place when it first appears, in pixels.</summary>
    private const double EnterOffset = 6;

    /// <summary>How far a toast drops while it fades out, in pixels.</summary>
    private const double LeaveOffset = 6;

    /// <summary>Below this the target did not really move; a re-arrange of unchanged children must not restart a slide.</summary>
    private const double Tolerance = 0.01;

    private readonly Dictionary<Control, Child> states = [];

    static ToastStackPanel()
    {
        OffsetProperty.Changed.AddClassHandler<Control>(
            static (child, change) => Mirror(child, change.GetNewValue<double>()));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var bounded = double.IsFinite(availableSize.Width);
        var width = bounded ? availableSize.Width : 0d;
        var height = 0d;
        var widest = 0d;
        var count = 0;

        foreach (var child in Children)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            height += child.DesiredSize.Height;
            widest = Math.Max(widest, child.DesiredSize.Width);
            count++;
        }

        if (count > 1)
        {
            height += Spacing * (count - 1);
        }

        return new Size(bounded ? availableSize.Width : widest, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children;
        var heights = new double[children.Count];
        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] = children[i].DesiredSize.Height;
        }

        var offsets = ToastStackLayout.Offsets(heights, Spacing);
        for (var i = 0; i < heights.Length; i++)
        {
            var child = children[i];
            child.Arrange(new Rect(0, offsets[i], finalSize.Width, heights[i]));
            Place(child, offsets[i]);
        }

        Prune();
        return finalSize;
    }

    /// <summary>Slides a child whose target moved; a child seen for the first time settles in from a few pixels above.</summary>
    private void Place(Control child, double top)
    {
        if (!states.TryGetValue(child, out var state))
        {
            state = new Child(top);
            states.Add(child, state);
            StartSlide(child, state, -EnterOffset, 0, top);
            return;
        }

        var transform = state.Transform;

        // Where the child is drawn right now: the last target plus whatever is left of a slide still in flight.
        var from = (transform?.Y ?? 0) + state.Top - top;
        state.Top = top;

        if (Math.Abs(from) < Tolerance)
        {
            return;
        }

        StartSlide(child, state, from, 0, top);
    }

    /// <summary>
    /// Slides the toast of a notification that is leaving: the card drops <see cref="LeaveOffset"/> pixels while the styles
    /// fade it out, and the removal that follows lets the stack close the gap. Called by the overlay when the item's
    /// <c>IsLeaving</c> flips, because the panel cannot see a view model flag.
    /// </summary>
    internal void SlideAway(Control child)
    {
        if (!states.TryGetValue(child, out var state))
        {
            return;
        }

        StartSlide(child, state, state.Transform?.Y ?? 0, LeaveOffset, state.Top);
    }

    /// <summary>Animates a child's visual offset from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private void StartSlide(Control child, Child state, double from, double to, double top)
    {
        state.Slide?.Cancel();
        state.Slide?.Dispose();
        state.Slide = null;

        if (state.Transform is not { } transform)
        {
            transform = new TranslateTransform();
            state.Transform = transform;
            child.RenderTransform = transform;
        }

        // Before the first tick of the animation the toast is drawn away from its place, not already on it.
        transform.Y = from;
        state.Top = top;

        var slide = new CancellationTokenSource();
        state.Slide = slide;
        _ = SlideAsync(state, child, from, to, slide);
    }

    private static async Task SlideAsync(Child state, Control child, double from, double to, CancellationTokenSource slide)
    {
        try
        {
            await CreateSlide(from, to).RunAsync(child, slide.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer position replaced this one; the slide that replaced it owns the child from here on.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A reflow that cannot run (no animation clock in this host) must not take the toast stack down with it; the
            // offset is reset by the finally below either way.
            AppLogs.Toast.ZLogWarning($"the reflow animation failed: {exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(state.Slide, slide))
            {
                state.Slide = null;
                // The animation's value is gone with it, so the offset is put back to where the slide ended: the toast sits
                // exactly on its new position (or stays where it dropped while leaving, until it is taken away).
                Mirror(child, to);
            }

            slide.Dispose();
        }
    }

    /// <summary>One slide: the toast moves from <paramref name="from"/> to <paramref name="to"/>, easing out.</summary>
    private static Animation CreateSlide(double from, double to) => new()
    {
        Duration = ReflowDuration,
        Easing = new CubicEaseOut(),
        Children =
        {
            new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(OffsetProperty, from) } },
            new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(OffsetProperty, to) } },
        },
    };

    /// <summary>Puts the animated offset on the child's transform; nothing else moves the transform.</summary>
    private static void Mirror(Control child, double offset)
    {
        if (child.RenderTransform is TranslateTransform transform)
        {
            transform.Y = offset;
        }
    }

    /// <summary>Drops the state (and stops the slide) of the children the item host has taken away.</summary>
    private void Prune()
    {
        if (states.Count == 0)
        {
            return;
        }

        List<Control>? gone = null;
        foreach (var child in states.Keys)
        {
            if (!Children.Contains(child))
            {
                (gone ??= []).Add(child);
            }
        }

        if (gone is null)
        {
            return;
        }

        foreach (var child in gone)
        {
            if (!states.Remove(child, out var state))
            {
                continue;
            }

            state.Slide?.Cancel();
            state.Slide?.Dispose();
            state.Slide = null;
            child.RenderTransform = null;
        }
    }

    /// <summary>Where the panel last put one toast, and the slide the toast is on its way to its next place with.</summary>
    private sealed class Child(double top)
    {
        /// <summary>The last arranged top edge, in the panel's own coordinates.</summary>
        public double Top { get; set; } = top;

        /// <summary>Created on the first move; a toast that never moved keeps none.</summary>
        public TranslateTransform? Transform { get; set; }

        /// <summary>The slide in flight; cancelled when a newer target arrives or the child goes away.</summary>
        public CancellationTokenSource? Slide { get; set; }
    }
}
