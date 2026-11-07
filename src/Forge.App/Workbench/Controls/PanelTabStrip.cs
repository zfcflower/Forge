using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Forge.App.Localization;

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
/// inside the group and floating a tab out of it — and gives every tab its own context menu instance.
/// </summary>
internal sealed class PanelTabStrip : TemplatedControl
{
    public static readonly StyledProperty<WorkbenchLayout?> LayoutProperty =
        AvaloniaProperty.Register<PanelTabStrip, WorkbenchLayout?>(nameof(Layout));

    public static readonly StyledProperty<TabGroupModel?> GroupProperty =
        AvaloniaProperty.Register<PanelTabStrip, TabGroupModel?>(nameof(Group));

    private readonly List<TabHeaderView> headers = [];
    private StackPanel? tabsHost;
    private TabGroupModel? subscribedGroup;
    private int attachedThread;
    private TabModel? dragTab;
    private Point dragStart;
    private TabDragMode dragMode;
    private int dragIndex;

    static PanelTabStrip()
    {
        GroupProperty.Changed.AddClassHandler<PanelTabStrip>((strip, _) => strip.OnGroupChanged());
        LayoutProperty.Changed.AddClassHandler<PanelTabStrip>((strip, _) => strip.InvalidateMeasure());
    }

    public WorkbenchLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public TabGroupModel? Group
    {
        get => GetValue(GroupProperty);
        set => SetValue(GroupProperty, value);
    }

    /// <summary>Activates a tab of this strip (keyboard activation of a focused header).</summary>
    public void Activate(TabModel tab) => Layout?.ActivateTab(tab);

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
            System.Diagnostics.Trace.WriteLine("[workbench] closing the tab failed: " + exception.Message);
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
        tabsHost = e.NameScope.Find<StackPanel>("PART_Tabs");
        Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attachedThread = Environment.CurrentManagedThreadId;
        Subscribe();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        Unsubscribe();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Every tab owns a context menu whose headers are built in code, so the strip re-reads them by rebuilding the
    /// headers on a language switch instead of subscribing each menu to the process-wide table. A notification that
    /// arrives on another thread belongs to a session whose window is gone (a test host) and is ignored: this control
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
            Layout?.ActivateTab(tab);
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
                Math.Max(0, y - 14),
                WorkbenchMetrics.FloatingTabWidth,
                WorkbenchMetrics.FloatingTabHeight,
                false));
    }

    private void Rebuild()
    {
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
                if (!ReferenceEquals(tabs[i], tab) && tabs[i].IsClosable
                    && !await layout.TryCloseTabAsync(tabs[i]).ConfigureAwait(true))
                {
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("[workbench] closing the other tabs failed: " + exception.Message);
        }
    }

    private void RefreshActive()
    {
        var active = Group?.ActiveTab;
        for (var i = 0; i < headers.Count; i++)
        {
            headers[i].IsActive = ReferenceEquals(headers[i].Model, active);
        }
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
