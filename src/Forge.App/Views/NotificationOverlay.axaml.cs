using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Forge.App.Notifications;

namespace Forge.App.Views;

/// <summary>
/// The in-window toast stack. Like the approval overlay it carries no dependency on the root view model: it resolves the
/// shell's <see cref="NotificationCenter"/> from <see cref="NotificationCenter.Current"/>, which the composition root
/// creates before the window, and shows nothing at all when there is none (a test window or a shell built without the
/// notification wiring).
/// <para>The center decides <em>whether</em> a notification becomes a toast; this view only decides where the toasts sit
/// and how they come and go (the stack panel slides them, the styles fade them). It also keeps
/// <see cref="NotificationCenter.IsBackground"/> in step with the window: the app counts as in the background while the
/// window is minimized or while no Forge window is the active one, in which case the center sends the notification to the
/// desktop instead. The subscriptions live only between attach and detach.</para>
/// </summary>
public partial class NotificationOverlay : UserControl
{
    private readonly NotificationCenter? center;

    /// <summary>The Forge windows whose activation this overlay listens to; a dialog or a floated panel counts too.</summary>
    private readonly List<Window> tracked = [];

    /// <summary>The toasts whose <c>IsLeaving</c> is followed, so a leaving card can be slid out by the stack.</summary>
    private readonly List<NotificationItemViewModel> watchedItems = [];

    private Window? host;

    public NotificationOverlay()
    {
        InitializeComponent();
        center = NotificationCenter.Current;
        if (center is null)
        {
            IsVisible = false;
            return;
        }

        DataContext = center;
        IsVisible = center.Items.Count > 0;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (center is null)
        {
            return;
        }

        ((INotifyCollectionChanged)center.Items).CollectionChanged += OnItemsChanged;
        WatchItems();
        host = TopLevel.GetTopLevel(this) as Window;
        if (host is not null)
        {
            host.PropertyChanged += OnHostPropertyChanged;
        }

        TrackWindows();
        UpdateBackground();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (center is null)
        {
            return;
        }

        ((INotifyCollectionChanged)center.Items).CollectionChanged -= OnItemsChanged;
        UnwatchItems();
        if (host is not null)
        {
            host.PropertyChanged -= OnHostPropertyChanged;
            host = null;
        }

        foreach (var window in tracked)
        {
            window.PropertyChanged -= OnTrackedPropertyChanged;
        }

        tracked.Clear();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            // The center is also disposed from the shutdown path; the visibility of the overlay belongs to the UI thread.
            Dispatcher.UIThread.Post(() => OnItemsChanged(sender, e));
            return;
        }

        IsVisible = center is not null && center.Items.Count > 0;
        WatchItems();
    }

    /// <summary>
    /// Follows the items (not their containers): the stack panel cannot see a view model flag, so the overlay turns
    /// <see cref="NotificationItemViewModel.IsLeaving"/> into the slide the card takes while it fades out.
    /// </summary>
    private void WatchItems()
    {
        if (center is null)
        {
            return;
        }

        foreach (var item in center.Items)
        {
            if (!watchedItems.Contains(item))
            {
                item.PropertyChanged += OnItemPropertyChanged;
                watchedItems.Add(item);
            }
        }

        for (var i = watchedItems.Count - 1; i >= 0; i--)
        {
            if (center.Items.Contains(watchedItems[i]))
            {
                continue;
            }

            watchedItems[i].PropertyChanged -= OnItemPropertyChanged;
            watchedItems.RemoveAt(i);
        }
    }

    private void UnwatchItems()
    {
        foreach (var item in watchedItems)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        watchedItems.Clear();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NotificationItemViewModel.IsLeaving) ||
            sender is not NotificationItemViewModel { IsLeaving: true } item)
        {
            return;
        }

        if (Toasts.ItemsPanelRoot is ToastStackPanel panel && Toasts.ContainerFromItem(item) is Control container)
        {
            panel.SlideAway(container);
        }
    }

    private void OnHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == WindowBase.IsActiveProperty)
        {
            UpdateBackground();
        }
    }

    private void OnTrackedPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowBase.IsActiveProperty)
        {
            return;
        }

        // A floating panel or a dialog can be opened at any time: pick up (and drop) windows on every activation change.
        TrackWindows();
        UpdateBackground();
    }

    /// <summary>Follows the activation of every Forge window, including the ones opened after the overlay was attached.</summary>
    private void TrackWindows()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        for (var i = tracked.Count - 1; i >= 0; i--)
        {
            var window = tracked[i];
            if (desktop.Windows.Contains(window))
            {
                continue;
            }

            window.PropertyChanged -= OnTrackedPropertyChanged;
            tracked.RemoveAt(i);
        }

        foreach (var window in desktop.Windows)
        {
            if (tracked.Contains(window))
            {
                continue;
            }

            window.PropertyChanged += OnTrackedPropertyChanged;
            tracked.Add(window);
        }
    }

    private void UpdateBackground()
    {
        if (center is null)
        {
            return;
        }

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Without a desktop lifetime (a test) there is no window state to follow and the app is never in the background.
            center.IsBackground = false;
            return;
        }

        center.IsBackground =
            host?.WindowState == WindowState.Minimized ||
            !desktop.Windows.Any(static window => window.IsActive);
    }
}
