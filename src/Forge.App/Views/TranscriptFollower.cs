using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.Localization;
using Forge.App.Workbench.Controls;
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

    /// <summary>How far above the viewport the reader must be from the top before the next page is asked for: a few screens
    /// of runway, so the page lands while the reader is still scrolling into the old one.</summary>
    private const double PrefetchRunway = 4;

    private readonly ListBox list;
    private readonly Border sticky;
    private readonly TextBlock stickyText;
    private readonly TextBlock stickyTip;
    private readonly Button follow;
    private readonly TranscriptScrollAnchor anchor;
    private readonly Func<Task<bool>>? loadOlder;
    private INotifyCollectionChanged? items;
    private ScrollViewer? scroller;
    private ScrollThumbDrag? thumbDrag;
    private TranscriptViewModel? transcript;
    private bool following = true;
    private bool scrollPending;
    private bool prefetchArmed = true;
    private bool prefetchStopped;
    private double prefetchOffset;
    private double prefetchExtent;
    private int stickyIndex = -1;

    private TranscriptFollower(ListBox list, Panel host, Func<Task<bool>>? loadOlder)
    {
        this.list = list;
        this.loadOlder = loadOlder;
        // One line, one panel-title-bar height: the floating row stands in for the user message and must not push the
        // transcript down by growing with it, so it reads as chrome (28 px, like the title bars above and beside it) and
        // shows the message's first line; the click takes the reader to the whole of it.
        stickyText = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stickyText.Bind(TextBlock.ForegroundProperty, stickyText.GetResourceObservable("TextPrimaryBrush"));
        sticky = new Border
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = PanelChrome.TitleBarHeight,
            Padding = new Thickness(12, 0),
            BorderThickness = new Thickness(0, 0, 0, 1),
            IsVisible = false,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = stickyText,
        };
        // The row can only show the beginning of the message, so the tip carries all of it, wrapped inside the tooltip's own
        // width cap. It hangs on the row, not on its text: a tooltip whose owner is a TextBlock is handed that owner's text
        // layout, and a trimmed one-line owner would force MaxLines=1 plus an ellipsis onto the tip itself.
        stickyTip = ToolTipText.Wrap(null);
        ToolTip.SetTip(sticky, stickyTip);
        // Same identity as the user bubble it stands in for: the brand tint, hairline border below.
        sticky.Bind(Border.BackgroundProperty, sticky.GetResourceObservable("BrandTintBrush"));
        sticky.Bind(Border.BorderBrushProperty, sticky.GetResourceObservable("BorderBrush"));
        sticky.PointerPressed += OnStickyPressed;

        var followIcon = new FluentIcon { Width = 16, Height = 16 };
        followIcon.Bind(FluentIcon.DataProperty, followIcon.GetResourceObservable("IconChatArrowBackDown"));
        follow = new Button
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 12, 12),
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            IsVisible = false,
            Classes = { "icon" },
            Content = followIcon,
        };
        follow.Bind(TemplatedControl.BackgroundProperty, follow.GetResourceObservable("CardBackgroundBrush"));
        follow.Bind(TemplatedControl.BorderBrushProperty, follow.GetResourceObservable("BrandSoftBrush"));
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
        AttachScroller(list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault());
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

    /// <summary>
    /// Installs the behaviour on <paramref name="list"/>, whose parent must be a panel (the overlays join it).
    /// <paramref name="loadOlder"/> asks for the page above when the reader gets close to the top; it answers whether the
    /// behaviour may ask again on a later scroll. A transcript without paging (a sub-agent's) passes none.
    /// </summary>
    public static TranscriptFollower? Attach(ListBox list, Func<Task<bool>>? loadOlder = null) =>
        list.Parent is Panel host ? new TranscriptFollower(list, host, loadOlder) : null;

    /// <summary>
    /// Points the behaviour at the transcript it scrolls: a prepend must not be read as the user sending a message, and the
    /// prefetch resumes after a page lands.
    /// <para>The transcript is read off the collection the list shows (see <see cref="TranscriptRoundCollection"/>): the
    /// signals cannot be left unbound by a view that forgot a second call. The fields above are not the list's business, so a
    /// swapped ItemsSource also re-arms the prefetch, which is exactly what a new tab needs.</para>
    /// </summary>
    private void BindTranscript(TranscriptViewModel? value)
    {
        if (transcript is not null)
        {
            transcript.Prepended -= OnPrepended;
        }

        transcript = value;
        prefetchArmed = true;
        prefetchStopped = false;
        if (transcript is not null)
        {
            transcript.Prepended += OnPrepended;
        }
    }

    /// <summary>Whether the list is following the reply (kept at the bottom as it grows).</summary>
    internal bool IsFollowing => following;

    /// <summary>What keeps the paused transcript's content in place while rows change.</summary>
    internal TranscriptScrollAnchor Anchor => anchor;

    /// <summary>The list's round collection changed (a new transcript, or none): listen to what it now shows.</summary>
    private void Hook(INotifyCollectionChanged? source)
    {
        if (items is not null)
        {
            items.CollectionChanged -= OnItemsChanged;
        }

        BindTranscript((source as TranscriptRoundCollection)?.Owner);
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

    private void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e) =>
        AttachScroller(
            e.NameScope.Find<ScrollViewer>("PART_ScrollViewer")
            ?? list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault());

    /// <summary>
    /// Points the behaviour at the list's scroll viewer. A list that was already templated when the behaviour attached
    /// (a test mounting a shown list, a view re-attaching) never raises <see cref="TemplateAppliedEventArgs"/> again, so
    /// this is also called with whatever the list already has.
    /// </summary>
    private void AttachScroller(ScrollViewer? next)
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

        scroller = next;
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
        // A wheel over an open tool body's own scroll area belongs to that viewer, not to the transcript: treating it as a
        // transcript scroll is what made the list jump when the pointer crossed the inner scroll bar.
        if (e.Source is Visual source
            && source.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault(viewer => !ReferenceEquals(viewer, scroller)) is not null)
        {
            return;
        }

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

        // The anchor hears about every change, following or not: a prepend turns following back off in the same turn (the
        // behaviour binds the transcript through the rounds collection it shows), and the anchor then has to know the rows
        // moved around the one it holds.
        anchor.ItemsChanged(e);
        if (following)
        {
            PostScrollToEnd();
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
        TryPrefetch();
    }

    /// <summary>
    /// Asks for the page above once the reader is within <see cref="PrefetchRunway"/> screens of the top. One page per ask,
    /// and a landing does not re-arm it: on this panel the prepend leaves both the offset and the extent alone (measured),
    /// so re-arming has to wait until the reader moved a screen or the landed page reached the extent, or a burst of pages
    /// would arrive one per layout pass.
    /// </summary>
    private void TryPrefetch()
    {
        if (loadOlder is null || prefetchStopped || following || scroller is not { } sv)
        {
            return;
        }

        if (!prefetchArmed)
        {
            // A short transcript asks again as soon as the page is laid out: opening a tab whose first page does not fill
            // the window keeps reading until it does (or the store runs out).
            var shortTranscript = sv.Extent.Height <= sv.Viewport.Height;
            var moved = Math.Abs(sv.Offset.Y - prefetchOffset) >= sv.Viewport.Height;
            var grew = sv.Extent.Height - prefetchExtent >= sv.Viewport.Height;
            if (!shortTranscript && !moved && !grew)
            {
                return;
            }

            prefetchArmed = true;
        }

        if (sv.Offset.Y > PrefetchRunway * sv.Viewport.Height)
        {
            return;
        }

        prefetchArmed = false;
        prefetchOffset = sv.Offset.Y;
        prefetchExtent = sv.Extent.Height;
        _ = AskAsync(loadOlder);
    }

    private async Task AskAsync(Func<Task<bool>> load)
    {
        bool again;
        try
        {
            again = await load().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // The view model owns its failures (the red line is its retry); a probe that threw must not take the
            // transcript's scroll behaviour down with it.
            again = false;
        }

        if (!again)
        {
            prefetchStopped = true;
        }
    }

    /// <summary>An older page landed in front of the transcript: it is not the user sending a message.</summary>
    private void OnPrepended(object? sender, EventArgs e)
    {
        following = false;
        prefetchStopped = false;
        prefetchOffset = scroller?.Offset.Y ?? 0;
        prefetchExtent = scroller?.Extent.Height ?? 0;

        // A transcript that still does not fill the window keeps reading - opening a tab whose first page leaves the
        // screen half empty must not stop at one page. Nothing else re-arms there: a page that fits under the viewport
        // changes neither the offset nor the extent, so no scroll is reported and nothing would ask again.
        var shortTranscript = scroller is { } sv && sv.Extent.Height <= sv.Viewport.Height;
        prefetchArmed = shortTranscript;
        UpdateOverlays();
        if (shortTranscript)
        {
            Dispatcher.UIThread.Post(TryPrefetch, DispatcherPriority.Background);
        }
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

            // The list's items are rounds, so the message and the content below it are the same item: the sticky belongs to the
            // first visible round when that round's own top has scrolled out of view, and to the round above it otherwise.
            var candidate = first;
            if (candidate >= 0 && candidate < source.Count
                && list.ContainerFromIndex(candidate)?.TranslatePoint(default, sv) is { Y: >= 0 })
            {
                candidate--;
            }

            for (var i = Math.Min(candidate, source.Count - 1); i >= 0; i--)
            {
                if (source[i] is TranscriptRoundViewModel { Rows.Count: > 0 } round && round.Rows[0] is UserMessageViewModel)
                {
                    stickyIndex = i;
                    break;
                }
            }
        }

        if (stickyIndex >= 0 && list.ItemsSource is IList all
            && all[stickyIndex] is TranscriptRoundViewModel { Rows.Count: > 0 } stickyRound
            && stickyRound.Rows[0] is UserMessageViewModel message)
        {
            stickyText.Text = message.Text;
            stickyTip.Text = message.Text;
            sticky.IsVisible = true;
        }
        else
        {
            sticky.IsVisible = false;
        }
    }

    private void OnStickyPressed(object? sender, PointerPressedEventArgs e)
    {
        if (stickyIndex < 0)
        {
            return;
        }

        StopFollowing();
        anchor.UserScrolled();
        TranscriptStackPanel.AllowNextBringIntoView();
        list.ScrollIntoView(stickyIndex);

        // ScrollIntoView only guarantees "somewhere visible"; the sticky stands for the message itself, so the round's own top
        // goes to the top of the viewport once the panel has placed it.
        var index = stickyIndex;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (scroller is { } sv && list.ContainerFromIndex(index) is { } container
                    && container.TranslatePoint(default, sv) is { } point)
                {
                    sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y + point.Y));
                }
            },
            DispatcherPriority.Background);
        e.Handled = true;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshText();

    /// <summary>The button is an icon: the tooltip carries the words, and it is refreshed with the language.</summary>
    private void RefreshText()
    {
        ToolTip.SetTip(follow, TranscriptScrollStrings.Follow);
        AutomationProperties.SetName(follow, TranscriptScrollStrings.Follow);
    }
}
