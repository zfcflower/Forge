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
    private INotifyCollectionChanged? items;
    private ScrollViewer? scroller;
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
    public static void Attach(ListBox list)
    {
        if (list.Parent is Panel host)
        {
            _ = new TranscriptFollower(list, host);
        }
    }

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
        if (scroller is not null)
        {
            scroller.ScrollChanged -= OnScrollChanged;
            scroller.RemoveHandler(InputElement.PointerPressedEvent, OnScrollerPressed);
        }

        scroller = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer")
            ?? list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroller is null)
        {
            return;
        }

        scroller.ScrollChanged += OnScrollChanged;
        // A press on the scroll bar is the user taking the scroll position: stop following until they reach the bottom again.
        scroller.AddHandler(InputElement.PointerPressedEvent, OnScrollerPressed, RoutingStrategies.Tunnel);
    }

    private void OnScrollerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual visual && visual.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null)
        {
            StopFollowing();
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y > 0 && CanScrollUp)
        {
            StopFollowing();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.PageUp or Key.Home or Key.Up && e.Source is not TextBox && CanScrollUp)
        {
            StopFollowing();
        }
    }

    private void StopFollowing()
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
        // Sending a message always brings the user back to the bottom.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { Count: > 0 } added && added[^1] is UserMessageViewModel)
        {
            following = true;
        }

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
        if (following)
        {
            if (contentChanged && !AtBottom)
            {
                PostScrollToEnd();
            }
        }
        else if (!contentChanged && AtBottom)
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
                if (source[i] is UserMessageViewModel)
                {
                    // When the first visible item is the message itself, it is on screen and nothing floats.
                    stickyIndex = i < first ? i : -1;
                    break;
                }
            }
        }

        if (stickyIndex >= 0 && list.ItemsSource is IList all && all[stickyIndex] is UserMessageViewModel message)
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
            list.ScrollIntoView(stickyIndex);
            e.Handled = true;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshText();

    private void RefreshText() => follow.Content = "↓  " + TranscriptScrollStrings.Follow;
}
