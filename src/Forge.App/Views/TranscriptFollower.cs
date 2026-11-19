using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// Scroll behaviour of a transcript list: it follows the reply while the user stays at the bottom; once the user scrolls
/// away (wheel, scroll bar, keys) it stops and shows a floating "follow replies" button that jumps back and resumes
/// following. While a turn is partly on screen but its user message has scrolled off the top, that message floats at the
/// top (click it to jump back to it). The list itself is untouched; the two overlays are added next to it.
/// </summary>
internal sealed class TranscriptFollower
{
    private const double BottomSlack = 2;

    private readonly ListBox list;
    private readonly Border sticky;
    private readonly TextBlock stickyText;
    private readonly Button follow;
    private readonly TranscriptScrollAnchor anchor;
    private INotifyCollectionChanged? items;
    private ScrollViewer? scroller;
    private ScrollThumbDrag? thumbDrag;
    private bool following = true;
    private bool scrollPending;
    private int stickyIndex = -1;

    private TranscriptFollower(ListBox list, Panel host)
    {
        this.list = list;
        stickyText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
        };
        stickyText.Bind(TextBlock.ForegroundProperty, stickyText.GetResourceObservable("TextPrimaryBrush"));
        sticky = new Border
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 6),
            BorderThickness = new Thickness(0, 0, 0, 1),
            IsVisible = false,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = stickyText,
        };
        // Same identity as the user bubble it stands in for: the brand tint, hairline border below.
        sticky.Bind(Border.BackgroundProperty, sticky.GetResourceObservable("BrandTintBrush"));
        sticky.Bind(Border.BorderBrushProperty, sticky.GetResourceObservable("BorderBrush"));
        sticky.PointerPressed += OnStickyPressed;

        follow = new Button
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(12, 5),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            IsVisible = false,
        };
        follow.Bind(TemplatedControl.BackgroundProperty, follow.GetResourceObservable("CardBackgroundBrush"));
        follow.Bind(TemplatedControl.BorderBrushProperty, follow.GetResourceObservable("BrandInteractiveBrush"));
        follow.Bind(TemplatedControl.ForegroundProperty, follow.GetResourceObservable("BrandActionBrush"));
        follow.Click += (_, _) => Resume();
        RefreshText();

        foreach (var overlay in new Control[] { sticky, follow })
        {
            Grid.SetRow(overlay, Grid.GetRow(list));
            Grid.SetColumn(overlay, Grid.GetColumn(list));
            Grid.SetRowSpan(overlay, Grid.GetRowSpan(list));
            Grid.SetColumnSpan(overlay, Grid.GetColumnSpan(list));
            host.Children.Add(overlay);
        }

        anchor = new TranscriptScrollAnchor(list, () => following);
        list.TemplateApplied += OnTemplateApplied;
        list.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        list.PropertyChanged += OnListPropertyChanged;
        list.AttachedToVisualTree += (_, _) =>
        {
            // The language may have changed while the list was off the tree and nobody was listening.
            Loc.Instance.LanguageChanged += OnLanguageChanged;
            RefreshText();
        };
        list.DetachedFromVisualTree += (_, _) => Loc.Instance.LanguageChanged -= OnLanguageChanged;
        Hook(list.ItemsSource as INotifyCollectionChanged);
    }

    /// <summary>Installs the behaviour on <paramref name="list"/>, whose parent must be a panel (the overlays join it).</summary>
    public static TranscriptFollower? Attach(ListBox list) =>
        list.Parent is Panel host ? new TranscriptFollower(list, host) : null;

    /// <summary>Whether the list is following the reply (kept at the bottom as it grows).</summary>
    internal bool IsFollowing => following;

    /// <summary>What keeps the paused transcript's content in place while rows change.</summary>
    internal TranscriptScrollAnchor Anchor => anchor;

    private void Hook(INotifyCollectionChanged? source)
    {
        if (items is not null)
        {
            items.CollectionChanged -= OnItemsChanged;
        }

        items = source;
        if (items is not null)
        {
            items.CollectionChanged += OnItemsChanged;
        }
    }

    private void OnListPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ItemsControl.ItemsSourceProperty)
        {
            Hook(list.ItemsSource as INotifyCollectionChanged);
            Resume();
        }
    }

    private void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        thumbDrag?.Detach();
        thumbDrag = null;
        if (scroller is not null)
        {
            scroller.ScrollChanged -= OnScrollChanged;
            scroller.RemoveHandler(InputElement.PointerPressedEvent, OnScrollerPressed);
            scroller.RemoveHandler(InputElement.PointerReleasedEvent, OnScrollerReleased);
            scroller.RemoveHandler(InputElement.PointerCaptureLostEvent, OnScrollerCaptureLost);
        }

        scroller = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer")
            ?? list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        anchor.SetScroller(scroller);
        if (scroller is null)
        {
            return;
        }

        scroller.ScrollChanged += OnScrollChanged;
        // The thumb follows the mouse although the extent behind it is an estimate that changes while it is dragged.
        thumbDrag = ScrollThumbDrag.Attach(scroller);
        // A press on the scroll bar is the user taking the scroll position: stop following until they reach the bottom again.
        scroller.AddHandler(InputElement.PointerPressedEvent, OnScrollerPressed, RoutingStrategies.Tunnel);
        // The thumb captures the pointer while dragged; its release (or a lost capture) ends the user's scroll.
        scroller.AddHandler(InputElement.PointerReleasedEvent, OnScrollerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        scroller.AddHandler(InputElement.PointerCaptureLostEvent, OnScrollerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnScrollerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual visual)
        {
            return;
        }

        // A press on the scroll bar is the user taking the scroll position. So is a press on a link-style button inside the
        // transcript (a tool row's expander or path): the row grows under the pointer, and following the tail would scroll it away.
        if (visual.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null)
        {
            anchor.BeginUserScroll();
            StopFollowing();
        }
        else if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed && visual.FindAncestorOfType<Button>(includeSelf: true) is { } button && button.Classes.Contains("link"))
        {
            StopFollowing();
        }
    }

    private void OnScrollerReleased(object? sender, PointerReleasedEventArgs e) => anchor.EndUserScroll();

    private void OnScrollerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => anchor.EndUserScroll();

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        anchor.UserScrolled();
        if (e.Delta.Y > 0 && CanScrollUp)
        {
            StopFollowing();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox && e.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Up or Key.Down or Key.Space)
        {
            anchor.UserScrolled();
        }

        // Enter or Space on a row's expander is the keyboard's press on it (see OnScrollerPressed): the row grows under the
        // focus, and following the tail would run to the end of what opened.
        if (e.Key is Key.Enter or Key.Space && e.Source is Button { } pressed && pressed.Classes.Contains("link"))
        {
            StopFollowing();
        }

        if (e.Key is Key.PageUp or Key.Home or Key.Up && e.Source is not TextBox && CanScrollUp)
        {
            StopFollowing();
        }
    }

    internal void StopFollowing()
    {
        following = false;
        UpdateOverlays();
    }

    private void Resume()
    {
        following = true;
        PostScrollToEnd();
        UpdateOverlays();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Sending a message always brings the user back to the bottom. A message opens its own round, so it is the round's
        // first row rather than the list's item.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { Count: > 0 } added
            && added[^1] is TranscriptRoundViewModel { Rows.Count: > 0 } opened && opened.Rows[0] is UserMessageViewModel)
        {
            following = true;
        }

        if (following)
        {
            PostScrollToEnd();
        }
        else
        {
            anchor.ItemsChanged(e);
        }
    }

    /// <summary>Whether an upward scroll would move anything; a wheel turned over a transcript that fits is not "the user left".</summary>
    private bool CanScrollUp => scroller is { } sv && sv.Offset.Y > 0.5;

    private bool AtBottom => scroller is not { } sv || sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - BottomSlack;

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var contentChanged = Math.Abs(e.ExtentDelta.Y) > 0.5 || Math.Abs(e.ViewportDelta.Y) > 0.5;
        // The anchor holding the content in place is not the user returning to the bottom.
        var anchored = anchor.TakeOwnWrite();
        if (following)
        {
            if (contentChanged && !AtBottom)
            {
                PostScrollToEnd();
            }
        }
        else if (!contentChanged && !anchored && AtBottom)
        {
            following = true;
        }

        UpdateOverlays();
    }

    private void PostScrollToEnd()
    {
        if (scrollPending)
        {
            return;
        }

        scrollPending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                scrollPending = false;
                if (following)
                {
                    scroller?.ScrollToEnd();
                }
            },
            DispatcherPriority.Background);
    }

    private void UpdateOverlays()
    {
        follow.IsVisible = !following && scroller is { } sv && sv.Extent.Height > sv.Viewport.Height + BottomSlack;
        UpdateSticky();
    }

    /// <summary>
    /// The user message of the turn that reaches into the viewport, when that message itself is above it: it is the nearest
    /// user message before the first item that is still visible (an item counts as visible while its bottom is below the top).
    /// </summary>
    private void UpdateSticky()
    {
        stickyIndex = -1;
        if (scroller is { } sv && list.ItemsSource is IList source && source.Count > 0)
        {
            var first = -1;
            foreach (var container in list.GetRealizedContainers())
            {
                var index = list.IndexFromContainer(container);
                if (index < 0 || (first >= 0 && index >= first))
                {
                    continue;
                }

                if (container.TranslatePoint(new Point(0, container.Bounds.Height), sv) is { Y: > 0 })
                {
                    first = index;
                }
            }

            for (var i = Math.Min(first, source.Count - 1); i >= 0; i--)
            {
                if (source[i] is TranscriptRoundViewModel { Rows.Count: > 0 } round && round.Rows[0] is UserMessageViewModel)
                {
                    // When the first visible item is the message itself, it is on screen and nothing floats.
                    stickyIndex = i < first ? i : -1;
                    break;
                }
            }
        }

        if (stickyIndex >= 0 && list.ItemsSource is IList all
            && all[stickyIndex] is TranscriptRoundViewModel { Rows.Count: > 0 } stickyRound
            && stickyRound.Rows[0] is UserMessageViewModel message)
        {
            stickyText.Text = message.Text;
            sticky.IsVisible = true;
        }
        else
        {
            sticky.IsVisible = false;
        }
    }

    private void OnStickyPressed(object? sender, PointerPressedEventArgs e)
    {
        if (stickyIndex >= 0)
        {
            StopFollowing();
            anchor.UserScrolled();
            list.ScrollIntoView(stickyIndex);
            e.Handled = true;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshText();

    private void RefreshText() => follow.Content = "↓  " + TranscriptScrollStrings.Follow;
}
