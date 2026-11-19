using Avalonia;
using Avalonia.Controls;

namespace Forge.App.Views;

/// <summary>
/// Attached behaviour for the inner viewers whose content streams while the user watches — the expanded thinking block.
/// It keeps the content pinned to the bottom while it grows, exactly the way the transcript follows the reply, and hands
/// control back the moment the user scrolls up; scrolling back to the bottom resumes the follow.
/// <para>
/// Usage: <c>views:StreamFollow.Follow="True"</c> on a <see cref="ScrollViewer"/>. The behaviour is one
/// <see cref="ScrollViewer.ScrollChanged"/> subscription and nothing per frame; a row that scrolls away unsubscribes
/// with the control itself. A viewer reused from the row pool resets to following when its data context changes.
/// </para>
/// </summary>
public static class StreamFollow
{
    /// <summary>How far from the bottom (in DIP) a position may sit and still count as "at the bottom": a wheel notch is
    /// several times this, so a real scroll away always unfollows while rounding never does.</summary>
    private const double BottomSlack = 4;

    public static readonly AttachedProperty<bool> FollowProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("Follow", typeof(StreamFollow));

    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, State?>("State", typeof(StreamFollow));

    private sealed class State
    {
        public bool Following = true;
    }

    private sealed class FollowObserver : IObserver<AvaloniaPropertyChangedEventArgs<bool>>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(AvaloniaPropertyChangedEventArgs<bool> change) => OnFollowChanged(change);
    }

    static StreamFollow() => FollowProperty.Changed.Subscribe(new FollowObserver());

    public static void SetFollow(ScrollViewer element, bool value) => element.SetValue(FollowProperty, value);

    public static bool GetFollow(ScrollViewer element) => element.GetValue(FollowProperty);

    private static void OnFollowChanged(AvaloniaPropertyChangedEventArgs<bool> change)
    {
        if (change.Sender is not ScrollViewer viewer)
        {
            return;
        }

        if (!change.NewValue.Value)
        {
            viewer.ScrollChanged -= OnScrollChanged;
            viewer.DataContextChanged -= OnDataContextChanged;
            viewer.SetValue(StateProperty, null);
            return;
        }

        viewer.SetValue(StateProperty, new State());
        viewer.ScrollChanged += OnScrollChanged;
        // A row rented from the pool for a new stream starts fresh: the previous row's scroll-away must not silence it.
        viewer.DataContextChanged += OnDataContextChanged;
    }

    private static void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is ScrollViewer viewer && viewer.GetValue(FollowProperty))
        {
            viewer.SetValue(StateProperty, new State());
        }
    }

    private static void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer
            || !viewer.GetValue(FollowProperty)
            || viewer.GetValue(StateProperty) is not { } state)
        {
            return;
        }

        // Growth while following: pin to the bottom. The offset change this produces is ours, and the position check
        // below would only confirm it, so the growth event itself decides nothing.
        if (e.ExtentDelta.Y > 0 && state.Following)
        {
            viewer.ScrollToEnd();
            return;
        }

        // Anything else is a move to judge by position: away from the bottom stops the follow, back to it resumes it.
        var bottom = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        state.Following = viewer.Offset.Y >= bottom - BottomSlack;
    }
}
