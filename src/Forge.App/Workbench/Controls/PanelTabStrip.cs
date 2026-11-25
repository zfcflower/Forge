using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Workbench.Controls;

/// <summary>What a pointer drag on a tab header is currently doing.</summary>
internal enum TabDragMode
{
    None,
    Reorder,
    Float,
}

/// <summary>
/// The tab strip of a <see cref="PanelChrome"/>: horizontal and scrollable. It owns the tab drag state — reordering
/// inside the group and floating a tab out of it — and gives every tab its own context menu instance. When the tabs no
/// longer fit, an ellipsis button pinned to the strip's right edge lists the ones that were clipped.
/// </summary>
internal sealed partial class PanelTabStrip : TemplatedControl
{
    /// <summary>Half a pixel: a tab whose edge lands exactly on the viewport edge still counts as visible.</summary>
    private const double ClippedEdge = 0.5;

    private readonly List<TabHeaderView> headers = [];
    private StackPanel? tabsHost;
    private ScrollViewer? scroller;
    private PanelIconButton? overflowButton;
    private TabGroupModel? subscribedGroup;
    private int attachedThread;
    private TabModel? dragTab;
    private Point dragStart;
    private TabDragMode dragMode;
    private int dragIndex;

    /// <summary>The header waiting to be scrolled into the strip; cleared once it is, or once it is no longer the active tab.</summary>
    private TabHeaderView? pendingReveal;

    /// <summary>
    /// The active tab the strip has already announced. Only a change of this scrolls the strip: a rebuild (a tab opening
    /// in the background, a language switch) re-applies the same active tab all the time and must leave the strip where
    /// the user scrolled it.
    /// </summary>
    private TabModel? announcedActive;

    static PanelTabStrip()
    {
        GroupProperty.Changed.AddClassHandler<PanelTabStrip>((strip, _) => strip.OnGroupChanged());
        LayoutProperty.Changed.AddClassHandler<PanelTabStrip>((strip, _) => strip.InvalidateMeasure());
    }

    [GeneratedStyledProperty]
    public partial WorkbenchLayout? Layout { get; set; }

    [GeneratedStyledProperty]
    public partial TabGroupModel? Group { get; set; }

    /// <summary>True while at least one tab does not fit in the strip; the ellipsis button is visible exactly then.</summary>
    [GeneratedStyledProperty]
    public partial bool HasOverflow { get; set; }

    /// <summary>Activates a tab of this strip (keyboard activation of a focused header).</summary>
    public void Activate(TabModel tab) => Layout?.ActivateTabByUser(tab);

    /// <summary>
    /// Closes a tab of this strip (the header's close button, middle click and the context menu all come through here).
    /// The layout's <see cref="WorkbenchLayout.TabCloseRequested"/> runs first and may veto the close, so a terminal with
    /// a program still running gets to ask.
    /// </summary>
    public void CloseTab(TabModel? tab) => _ = CloseTabAsync(tab);

    private async Task CloseTabAsync(TabModel? tab)
    {
        if (tab is null || Layout is not { } layout)
        {
            return;
        }

        try
        {
            await layout.TryCloseTabAsync(tab).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Workbench.ZLogWarning($"closing the tab failed: {exception.Message}");
        }
    }

    /// <summary>Starts the pointer drag of a tab: middle-click closes, left drag reorders or floats the tab out.</summary>
    public void BeginTabDrag(TabModel tab, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsMiddleButtonPressed)
        {
            CloseTab(tab);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed || dragTab is not null)
        {
            return;
        }

        dragTab = tab;
        dragIndex = Group?.Tabs.IndexOf(tab) ?? -1;
        dragStart = e.GetPosition(this);
        dragMode = TabDragMode.None;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (overflowButton is not null)
        {
            overflowButton.Click -= OnOverflowClick;
        }

        tabsHost = e.NameScope.Find<StackPanel>("PART_Tabs");
        scroller = e.NameScope.Find<ScrollViewer>("PART_Scroller");
        overflowButton = e.NameScope.Find<PanelIconButton>("PART_Overflow");
        if (overflowButton is not null)
        {
            overflowButton.Click += OnOverflowClick;
        }

        Rebuild();
        RefreshOverflow();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attachedThread = Environment.CurrentManagedThreadId;
        // A strip that is put back in the tree starts from the left edge: its active tab is revealed again.
        announcedActive = null;
        Subscribe();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        // The answer to "do the tabs fit" only exists once a layout pass has placed them, and it changes with the
        // panel's width, the tab set and the tab titles; one cheap pass keeps it current without a second layout round.
        LayoutUpdated += OnStripLayoutUpdated;
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LayoutUpdated -= OnStripLayoutUpdated;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        Unsubscribe();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Every tab owns a context menu whose headers are built in code, so the strip re-reads them by rebuilding the
    /// headers on a language switch instead of subscribing each menu to the process-wide table. A notification that
    /// arrives on another thread belongs to a session whose window is gone and is ignored: this control
    /// only ever lives on the thread it was attached on.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Environment.CurrentManagedThreadId != attachedThread)
        {
            return;
        }

        Rebuild();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragTab is not { } tab || Group is not { } group || tabsHost is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        var dx = position.X - dragStart.X;
        var dy = position.Y - dragStart.Y;
        if (dragMode == TabDragMode.None)
        {
            if (Math.Abs(dy) > WorkbenchMetrics.TabFloatOutDistance && (position.Y < 0 || position.Y > Bounds.Height))
            {
                dragMode = TabDragMode.Float;
            }
            else if (Math.Abs(dx) > WorkbenchMetrics.TabDragThreshold)
            {
                dragMode = TabDragMode.Reorder;
            }
        }

        if (dragMode != TabDragMode.Reorder)
        {
            return;
        }

        var target = TargetIndex(position);
        if (target >= 0 && target != dragIndex)
        {
            Layout?.MoveTab(group, dragIndex, target);
            dragIndex = target;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragTab is not { } tab)
        {
            return;
        }

        var completed = dragMode;
        dragTab = null;
        dragMode = TabDragMode.None;
        e.Pointer.Capture(null);
        e.Handled = true;

        if (completed == TabDragMode.Float)
        {
            FloatOut(tab, e.GetPosition(this));
        }
        else if (completed == TabDragMode.None)
        {
            Layout?.ActivateTabByUser(tab);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        dragTab = null;
        dragMode = TabDragMode.None;
    }

    /// <summary>The tab index whose slot the pointer is in: the first header whose midpoint is to the right of it.</summary>
    private int TargetIndex(Point point)
    {
        if (Group is not { } group || tabsHost is null || this.TranslatePoint(point, tabsHost) is not { } local)
        {
            return -1;
        }

        var last = -1;
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            if (!header.IsVisible || header.Model is not { } tab)
            {
                continue;
            }

            last = group.Tabs.IndexOf(tab);
            if (local.X < header.Bounds.X + (header.Bounds.Width / 2))
            {
                return last;
            }
        }

        return last;
    }

    private void FloatOut(TabModel tab, Point position)
    {
        if (Layout is null)
        {
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        var scaling = top?.RenderScaling ?? 1;
        if (scaling <= 0)
        {
            scaling = 1;
        }

        var screen = top?.PointToScreen(position);
        var x = screen is { } pixel ? pixel.X / scaling : 0;
        var y = screen is { } origin ? origin.Y / scaling : 0;
        Layout.FloatTab(
            tab,
            new FloatGeometry(
                Math.Max(0, x - 80),
                Math.Max(0, y - (PanelChrome.TitleBarHeight / 2)),
                WorkbenchMetrics.FloatingTabWidth,
                WorkbenchMetrics.FloatingTabHeight,
                false));
    }

    private void Rebuild()
    {
        pendingReveal = null;
        headers.Clear();
        tabsHost?.Children.Clear();
        if (tabsHost is null || Group is not { } group)
        {
            return;
        }

        for (var i = 0; i < group.Tabs.Count; i++)
        {
            var tab = group.Tabs[i];
            var header = new TabHeaderView
            {
                Model = tab,
                Strip = this,
                ContextMenu = BuildContextMenu(tab),
            };
            headers.Add(header);
            tabsHost.Children.Add(header);
        }

        RefreshActive();
        if (overflowButton is not null)
        {
            AutomationProperties.SetAutomationId(overflowButton, "tabs-" + group.Id + "-overflow");
        }
    }

    private ContextMenu BuildContextMenu(TabModel tab)
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = ShellStrings.OpenInNewWindow };
        open.Click += (_, _) => Layout?.FloatTab(tab);
        menu.Items.Add(open);

        if (!tab.IsClosable)
        {
            return menu;
        }

        var close = new MenuItem { Header = ShellStrings.CloseTab };
        close.Click += (_, _) => CloseTab(tab);
        // The tab's live state flips while this menu sits on its header (a run ends, a history tab is resumed), so
        // the close offer follows the state at the moment the menu opens, not the moment the strip built it.
        menu.Opening += (_, _) => close.IsVisible = !TabModel.BlocksUserClose(tab);
        menu.Items.Add(close);

        var closeOthers = new MenuItem { Header = ShellStrings.CloseOtherTabs };
        closeOthers.Click += (_, _) => _ = CloseOtherTabsAsync(tab);
        menu.Items.Add(closeOthers);
        return menu;
    }

    private async Task CloseOtherTabsAsync(TabModel tab)
    {
        if (Layout is not { } layout || tab.Group is not { } group)
        {
            return;
        }

        try
        {
            var tabs = group.Tabs.ToArray();
            for (var i = 0; i < tabs.Length; i++)
            {
                // A live sub-agent's tab is not closable by the user, but it must not stop the sweep either - the
                // tabs after it still get their close.
                if (!ReferenceEquals(tabs[i], tab) && tabs[i].IsClosable && !TabModel.BlocksUserClose(tabs[i]))
                {
                    if (!await layout.TryCloseTabAsync(tabs[i]).ConfigureAwait(true))
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Workbench.ZLogWarning($"closing the other tabs failed: {exception.Message}");
        }
    }

    private void RefreshActive()
    {
        var active = Group?.ActiveTab;
        for (var i = 0; i < headers.Count; i++)
        {
            headers[i].IsActive = ReferenceEquals(headers[i].Model, active);
        }

        if (ReferenceEquals(active, announcedActive))
        {
            return;
        }

        announcedActive = active;
        Reveal(active);
    }

    /// <summary>
    /// Scrolls <paramref name="tab"/> into view. A tab opened at the end of a full strip lands entirely outside the
    /// viewport, and nothing on screen would then say which tab is on show; a strip that already holds it does not move.
    /// <para>Posted, because activation usually arrives before the header has bounds: <see cref="RevealPending"/> is retried
    /// from the next layout pass until it can run, then dropped, so a strip the user scrolled away is never pulled back.</para>
    /// </summary>
    private void Reveal(TabModel? tab)
    {
        pendingReveal = null;
        if (tab is null)
        {
            return;
        }

        for (var i = 0; i < headers.Count; i++)
        {
            if (ReferenceEquals(headers[i].Model, tab))
            {
                pendingReveal = headers[i];
                Dispatcher.UIThread.Post(RevealPending, DispatcherPriority.Loaded);
                return;
            }
        }
    }

    /// <summary>
    /// Runs the pending reveal once the strip has placed the header and that header is outside the viewport; a header still
    /// without bounds waits one more pass, and one the strip already holds is left where it is.
    /// </summary>
    private void RevealPending()
    {
        if (pendingReveal is not { } header || !ReferenceEquals(header.Model, Group?.ActiveTab))
        {
            pendingReveal = null;
            return;
        }

        if (header.Bounds.Width <= 0 || scroller is not { } viewer || viewer.Viewport.Width <= 0)
        {
            return;
        }

        pendingReveal = null;
        if (!IsHeaderInside(header, viewer))
        {
            header.BringIntoView();
        }
    }

    /// <summary>Whether a header sits entirely inside the strip's viewport, the same test the overflow button uses.</summary>
    private bool IsHeaderInside(TabHeaderView header, ScrollViewer viewer) =>
        tabsHost is { } host
        && !IsClipped(
            host.Bounds.X + header.Bounds.X,
            header.Bounds.Width,
            viewer.Offset.X,
            viewer.Offset.X + viewer.Viewport.Width);

    private void OnStripLayoutUpdated(object? sender, EventArgs e)
    {
        RefreshOverflow();
        RevealPending();
    }

    /// <summary>Rereads the overflow flag after a layout pass; writing it only on a flip keeps a steady title bar free.</summary>
    private void RefreshOverflow()
    {
        var overflow = HasClippedTab();
        if (overflow != HasOverflow)
        {
            HasOverflow = overflow;
        }
    }

    private bool HasClippedTab()
    {
        if (scroller is not { } viewer || tabsHost is not { } host)
        {
            return false;
        }

        double left = viewer.Offset.X;
        double right = left + viewer.Viewport.Width;
        if (!(viewer.Viewport.Width > 0))
        {
            return false;
        }

        for (var i = 0; i < headers.Count; i++)
        {
            TabHeaderView header = headers[i];
            if (header.IsVisible && header.Bounds.Width > 0
                && IsClipped(host.Bounds.X + header.Bounds.X, header.Bounds.Width, left, right))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The tabs the strip had to clip, in strip order: what the ellipsis button lists.</summary>
    private List<TabModel> CollectClippedTabs()
    {
        var clipped = new List<TabModel>();
        if (scroller is not { } viewer || tabsHost is not { } host)
        {
            return clipped;
        }

        double left = viewer.Offset.X;
        double right = left + viewer.Viewport.Width;
        for (var i = 0; i < headers.Count; i++)
        {
            TabHeaderView header = headers[i];
            if (!header.IsVisible || header.Model is not { } tab || header.Bounds.Width <= 0)
            {
                continue;
            }

            if (IsClipped(host.Bounds.X + header.Bounds.X, header.Bounds.Width, left, right))
            {
                clipped.Add(tab);
            }
        }

        return clipped;
    }

    /// <summary>A tab counts as reachable only while it sits entirely inside the strip's viewport, left or right edge.</summary>
    private static bool IsClipped(double start, double width, double viewportLeft, double viewportRight) =>
        start < viewportLeft - ClippedEdge || start + width > viewportRight + ClippedEdge;

    /// <summary>
    /// Opens the list of clipped tabs: the ellipsis button's click handler. The menu is rebuilt on every open instead of
    /// bound, because which tabs are clipped is a property of the current layout, not of the group.
    /// </summary>
    private void OnOverflowClick(object? sender, RoutedEventArgs e)
    {
        if (overflowButton is not { } button)
        {
            return;
        }

        if (BuildClippedMenu() is not { } menu)
        {
            return;
        }

        button.Flyout = menu;
        menu.ShowAt(button);
    }

    /// <summary>
    /// Builds the menu of the tabs the strip had to clip, or null when nothing is clipped.
    /// </summary>
    internal MenuFlyout? BuildClippedMenu()
    {
        List<TabModel> clipped = CollectClippedTabs();
        if (clipped.Count == 0)
        {
            RefreshOverflow();
            return null;
        }

        var menu = new MenuFlyout();
        for (var i = 0; i < clipped.Count; i++)
        {
            TabModel tab = clipped[i];
            var item = new MenuItem { Header = tab.Title };
            AutomationProperties.SetAutomationId(item, "tab-" + tab.Id + "-overflow-item");
            item.Click += (_, _) => ActivateAndReveal(tab);
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>Activates a tab picked from the clipped list and scrolls it into the strip, so the click has a visible effect.</summary>
    private void ActivateAndReveal(TabModel tab)
    {
        Layout?.ActivateTabByUser(tab);
        Reveal(tab);
    }

    private void OnGroupChanged()
    {
        Subscribe();
        Rebuild();
    }

    private void Subscribe()
    {
        Unsubscribe();
        if (Group is not { } group)
        {
            return;
        }

        group.Tabs.CollectionChanged += OnTabsChanged;
        group.PropertyChanged += OnGroupChanged;
        subscribedGroup = group;
    }

    private void Unsubscribe()
    {
        if (subscribedGroup is not { } group)
        {
            return;
        }

        group.Tabs.CollectionChanged -= OnTabsChanged;
        group.PropertyChanged -= OnGroupChanged;
        subscribedGroup = null;
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabGroupModel.ActiveTab))
        {
            RefreshActive();
        }
    }
}
