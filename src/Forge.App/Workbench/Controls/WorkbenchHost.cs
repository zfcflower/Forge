using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Forge.App.Workbench.Controls;

/// <summary>
/// The workbench surface: it turns <see cref="WorkbenchLayout"/> into rectangles — the docked regions with a 2 px splitter
/// between them, the auto-hide strips on the window edges, the drawer over Main and the drop targets of a region swap —
/// and keeps one <see cref="FloatingPanelWindow"/> per floating item. It holds no layout state of its own.
/// </summary>
internal sealed class WorkbenchHost : Panel
{
    public static readonly StyledProperty<WorkbenchLayout?> LayoutProperty =
        AvaloniaProperty.Register<WorkbenchHost, WorkbenchLayout?>(nameof(Layout));

    private readonly RegionPresenter?[] presenters = new RegionPresenter?[3];
    private readonly AutoHideStrip?[] strips = new AutoHideStrip?[3];
    private readonly RegionSplitter?[] regionSplitters = new RegionSplitter?[2];
    private readonly Rect[] regionRects = new Rect[3];
    private readonly bool[] regionDocked = new bool[3];
    private readonly int[] dockedRoles = new int[3];
    private readonly SwapDropOverlay dropOverlay = new();
    private readonly Border drawerSurface = new() { IsVisible = false };
    private readonly Dictionary<string, FloatingPanelWindow> windows = new(StringComparer.Ordinal);
    private TopLevel? keyHost;
    private RegionRole? raisedRole;
    private RegionRole? swapSource;
    private RegionRole? swapHovered;
    private Point swapStart;
    private bool swapPending;
    private bool swapping;
    private bool subscribed;
    private bool attached;
    private bool arranging;
    private bool zOrderPending;
    private Rect drawerRect;
    private Rect drawerContentRect;
    private PanelModel? sizedDrawer;
    private bool sizedDrawerWithPreview;
    private double sizedDrawerWidth;
    private WorkbenchLayout? subscribedLayout;

    static WorkbenchHost()
    {
        LayoutProperty.Changed.AddClassHandler<WorkbenchHost>((host, _) => host.OnLayoutPropertyChanged());
    }

    public WorkbenchHost()
    {
        ClipToBounds = true;
        AutomationProperties.SetAutomationId(this, "workbench-host");
        dropOverlay.IsVisible = false;
        WorkbenchResources.Link(drawerSurface, Border.BackgroundProperty, "WindowBackgroundBrush");
        WorkbenchResources.Link(drawerSurface, Border.BorderBrushProperty, "BorderBrush");
        Children.Add(drawerSurface);
        Children.Add(dropOverlay);
        AddHandler(PointerPressedEvent, OnDrawerOutsidePress, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public WorkbenchLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    /// <summary>Starts a region-swap drag from a panel title bar; the targets appear once the pointer travels sideways.</summary>
    public void BeginRegionSwapDrag(RegionRole source, PointerPressedEventArgs e)
    {
        if (Layout is not { } layout || swapPending || swapping)
        {
            return;
        }

        if (layout.GetRegion(source).IsAutoHidden)
        {
            return;
        }

        swapPending = true;
        swapSource = source;
        swapHovered = null;
        swapStart = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : 0,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
        return LayoutChildren(size, measureOnly: true);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        arranging = true;
        try
        {
            return LayoutChildren(finalSize, measureOnly: false);
        }
        finally
        {
            arranging = false;
            if (zOrderPending)
            {
                zOrderPending = false;
                Dispatcher.UIThread.Post(ApplyZOrder);
            }
        }
    }

    /// <summary>Uses the same region, drawer and cover slots for measurement and arrangement.</summary>
    private Size LayoutChildren(Size finalSize, bool measureOnly)
    {
        var width = finalSize.Width;
        var height = finalSize.Height;
        if (Layout is not { } layout)
        {
            for (var i = 0; i < Children.Count; i++)
            {
                Place(Children[i], default, measureOnly);
            }

            return finalSize;
        }

        if (width > 0 && Math.Abs(width - layout.WindowWidth) > 0.01)
        {
            layout.SetWindowWidth(width);
        }

        var leftStrips = 0;
        var rightStrips = 0;
        var docked = 0;
        for (var i = 0; i < layout.Order.Count; i++)
        {
            var region = layout.Order[i];
            var isDocked = region.Role == RegionRole.Main || !region.IsAutoHidden;
            regionDocked[(int)region.Role] = isDocked;
            if (region.Role != RegionRole.Main && region.IsAutoHidden)
            {
                if (layout.EdgeOf(region.Role) == RegionEdge.Left)
                {
                    leftStrips++;
                }
                else
                {
                    rightStrips++;
                }
            }

            if (!isDocked)
            {
                regionRects[(int)region.Role] = default;
                continue;
            }

            docked++;
        }

        var leftPad = leftStrips * WorkbenchMetrics.AutoHideStripWidth;
        var rightPad = rightStrips * WorkbenchMetrics.AutoHideStripWidth;
        var gaps = Math.Max(0, docked - 1) * WorkbenchMetrics.RegionSplitterWidth;
        var space = Math.Max(0, width - leftPad - rightPad - gaps);
        var x = leftPad;
        var assigned = 0.0;
        var placed = 0;
        for (var i = 0; i < layout.Order.Count; i++)
        {
            var region = layout.Order[i];
            if (!regionDocked[(int)region.Role])
            {
                continue;
            }

            placed++;
            var remaining = Math.Max(0, space - assigned);
            var regionWidth = placed == docked ? remaining : Math.Min(Math.Max(0, region.Width), remaining);
            assigned += regionWidth;
            regionRects[(int)region.Role] = new Rect(x, 0, regionWidth, height);
            x += regionWidth + (placed < docked ? WorkbenchMetrics.RegionSplitterWidth : 0);
        }

        var drawer = layout.DrawerPanel;
        var drawerRole = drawer?.Region;
        var drawerOpen = drawerRole is { } open && layout.GetRegion(open).IsAutoHidden;
        drawerRect = default;
        drawerContentRect = default;
        if (drawerOpen && drawer is not null)
        {
            var room = Math.Max(0, width - leftPad - rightPad);
            var withPreview = layout.PreviewTabs.IsVisible;

            // The drawer is measured when it opens or when a column joins or leaves it - never on every pass: a splitter drag
            // inside the drawer moves the two columns against each other, it does not resize the drawer.
            if (!ReferenceEquals(sizedDrawer, drawer) || sizedDrawerWithPreview != withPreview)
            {
                sizedDrawer = drawer;
                sizedDrawerWithPreview = withPreview;
                sizedDrawerWidth = DrawerWidthOf(layout, drawer);
            }

            var drawerWidth = Math.Min(sizedDrawerWidth, room);
            var drawerX = layout.EdgeOf(drawer.Region) == RegionEdge.Left ? leftPad : Math.Max(0, width - rightPad - drawerWidth);
            drawerContentRect = new Rect(drawerX, 0, drawerWidth, height);
            drawerRect = drawerContentRect;

            // Covering Main takes Main's area as well - the same union the pinned inspector covers - so the button means the
            // same thing in the drawer as it does in the region.
            var mainRect = regionRects[(int)RegionRole.Main];
            if (layout.IsPreviewCoveringMain && withPreview && mainRect.Width > 0)
            {
                drawerRect = drawerContentRect.Union(mainRect);
            }
        }
        else
        {
            sizedDrawer = null;
        }

        var covering = layout.IsPreviewCoveringMain
            && regionDocked[(int)RegionRole.Inspector]
            && layout.PreviewTabs.IsVisible
            && !drawerOpen;
        var coverRect = default(Rect);
        if (covering)
        {
            var inspector = regionRects[(int)RegionRole.Inspector];
            // Same width the presenter lays the column out at, so the covered area ends exactly where the files column begins.
            var filesColumnVisible = layout.FilesPanel.IsVisible || layout.InspectorTools.IsVisible;
            var previewWidth = RegionPresenter.PreviewColumnWidth(layout, inspector.Width, filesColumnVisible);
            var column = layout.IsPreviewLeftOfFiles
                ? new Rect(inspector.X, inspector.Y, previewWidth, inspector.Height)
                : new Rect(inspector.Right - previewWidth, inspector.Y, previewWidth, inspector.Height);
            coverRect = column.Union(regionRects[(int)RegionRole.Main]);
        }

        for (var i = 0; i < layout.Order.Count; i++)
        {
            var region = layout.Order[i];
            var index = (int)region.Role;
            if (presenters[index] is not { } presenter)
            {
                continue;
            }

            if (regionDocked[index])
            {
                presenter.SoloPanel = null;
                var rect = regionRects[index];
                if (index == (int)RegionRole.Inspector && coverRect.Width > 0)
                {
                    var union = rect.Union(coverRect);
                    presenter.RegionRect = new Rect(rect.X - union.X, rect.Y - union.Y, rect.Width, rect.Height);
                    presenter.PreviewCoverRect = new Rect(
                        coverRect.X - union.X,
                        coverRect.Y - union.Y,
                        coverRect.Width,
                        coverRect.Height);
                    Place(presenter, union, measureOnly);
                }
                else
                {
                    presenter.RegionRect = new Rect(0, 0, rect.Width, rect.Height);
                    presenter.PreviewCoverRect = default;
                    Place(presenter, rect, measureOnly);
                }

                continue;
            }

            if (drawerOpen && drawerRole == region.Role && drawer is not null)
            {
                // The surface's 1 px outline is the side facing Main, so the panel body starts after it and ends before it:
                // widening the presenter over that column would paint the very line the drawer is meant to have.
                var borderOnRight = layout.EdgeOf(region.Role) == RegionEdge.Left;
                var content = new Rect(
                    borderOnRight ? drawerContentRect.X : drawerContentRect.X + 1,
                    drawerContentRect.Y,
                    Math.Max(0, drawerContentRect.Width - 1),
                    drawerContentRect.Height);
                presenter.SoloPanel = drawer;

                if (drawerRect.Width > drawerContentRect.Width)
                {
                    // Covering Main: the presenter spans the drawer plus Main, and it is told where Main is - the preview's own
                    // covered span is worked out there against its column, so it never runs under the file tree.
                    var mainRect = regionRects[(int)RegionRole.Main];
                    presenter.RegionRect = new Rect(content.X - drawerRect.X, content.Y - drawerRect.Y, content.Width, content.Height);
                    presenter.PreviewCoverRect = new Rect(
                        mainRect.X - drawerRect.X,
                        mainRect.Y - drawerRect.Y,
                        mainRect.Width,
                        mainRect.Height);
                    Place(presenter, drawerRect, measureOnly);
                    continue;
                }

                presenter.RegionRect = new Rect(0, 0, content.Width, content.Height);
                presenter.PreviewCoverRect = default;
                Place(presenter, content, measureOnly);
                continue;
            }

            presenter.SoloPanel = null;
            presenter.RegionRect = default;
            presenter.PreviewCoverRect = default;
            Place(presenter, default, measureOnly);
        }

        var leftIndex = 0;
        var rightIndex = 0;
        for (var i = 0; i < layout.Order.Count; i++)
        {
            var region = layout.Order[i];
            var index = (int)region.Role;
            if (strips[index] is not { } strip)
            {
                continue;
            }

            if (region.Role == RegionRole.Main || !region.IsAutoHidden)
            {
                strip.ShownPanelId = null;
                Place(strip, default, measureOnly);
                continue;
            }

            var edge = EdgeOf(layout, region.Role);
            strip.Edge = edge;
            strip.ShownPanelId = drawerOpen && drawerRole == region.Role ? drawer!.Id : null;
            var stripWidth = WorkbenchMetrics.AutoHideStripWidth;
            if (edge == RegionEdge.Left)
            {
                Place(strip, new Rect(leftIndex * stripWidth, 0, stripWidth, height), measureOnly);
                leftIndex++;
            }
            else
            {
                Place(strip, new Rect(width - ((rightIndex + 1) * stripWidth), 0, stripWidth, height), measureOnly);
                rightIndex++;
            }
        }

        var dockedCount = 0;
        for (var i = 0; i < layout.Order.Count; i++)
        {
            var index = (int)layout.Order[i].Role;
            if (regionDocked[index])
            {
                dockedRoles[dockedCount++] = index;
            }
        }

        var splitterIndex = 0;
        for (var i = 0; i + 1 < dockedCount && splitterIndex < regionSplitters.Length; i++)
        {
            if (regionSplitters[splitterIndex] is not { } splitter)
            {
                splitterIndex++;
                continue;
            }

            var gapX = regionRects[dockedRoles[i]].Right;
            splitter.SplitterIndex = splitterIndex;

            // Only the lines the drawer or the covered preview actually lies on are taken out: a drawer over one side region
            // must not cost the user the line between the regions that are still pinned.
            var underDrawer = drawerOpen && drawerRect.Width > 0 && gapX >= drawerRect.X && gapX < drawerRect.Right;
            var underCover = coverRect.Width > 0 && gapX >= coverRect.X && gapX < coverRect.Right;
            Place(splitter, underDrawer || underCover
                ? default
                : new Rect(
                    gapX + ((WorkbenchMetrics.RegionSplitterWidth - WorkbenchMetrics.SplitterHitWidth) / 2),
                    0,
                    WorkbenchMetrics.SplitterHitWidth,
                    height), measureOnly);
            splitterIndex++;
        }

        for (var i = splitterIndex; i < regionSplitters.Length; i++)
        {
            if (regionSplitters[i] is { } splitter)
            {
                Place(splitter, default, measureOnly);
            }
        }

        Place(drawerSurface, drawerOpen ? drawerRect : default, measureOnly);

        if (swapping)
        {
            dropOverlay.IsVisible = true;
            dropOverlay.SetTargets(
                regionDocked[0],
                regionRects[0],
                regionDocked[1],
                regionRects[1],
                regionDocked[2],
                regionRects[2]);
            dropOverlay.SetHovered(swapHovered);
            Place(dropOverlay, new Rect(0, 0, width, height), measureOnly);
        }
        else
        {
            dropOverlay.IsVisible = false;
            dropOverlay.Clear();
            Place(dropOverlay, default, measureOnly);
        }

        return finalSize;
    }

    private static void Place(Control child, Rect rect, bool measureOnly)
    {
        child.Measure(rect.Size);
        if (!measureOnly)
        {
            child.Arrange(rect);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        keyHost = TopLevel.GetTopLevel(this);
        keyHost?.AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unsubscribe();
        if (keyHost is not null)
        {
            keyHost.RemoveHandler(KeyDownEvent, OnPreviewKeyDown);
            keyHost = null;
        }

        CloseAllWindows();
        attached = false;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>A press anywhere outside the open drawer closes it; presses on an auto-hide strip belong to its buttons.</summary>
    private void OnDrawerOutsidePress(object? sender, PointerPressedEventArgs e)
    {
        if (Layout is not { DrawerPanel: not null } layout || drawerRect.Contains(e.GetPosition(this)))
        {
            return;
        }

        if (e.Source is Visual source
            && (source is AutoHideStrip || source.FindAncestorOfType<AutoHideStrip>() is not null))
        {
            return;
        }

        layout.CloseDrawer();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!swapPending)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (!swapping)
        {
            if (Math.Abs(position.X - swapStart.X) <= WorkbenchMetrics.TabDragThreshold)
            {
                return;
            }

            swapping = true;
            InvalidateArrange();
        }

        var hovered = RegionAt(position);
        if (hovered != swapHovered)
        {
            swapHovered = hovered;
            dropOverlay.SetHovered(hovered);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!swapPending)
        {
            return;
        }

        var target = swapping ? RegionAt(e.GetPosition(this)) : null;
        FinishSwap(target);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (swapPending)
        {
            FinishSwap(null);
        }
    }

    private void OnLayoutPropertyChanged()
    {
        Unsubscribe();
        CloseAllWindows();
        RebuildChildren();
        if (attached)
        {
            Subscribe();
        }

        InvalidateMeasure();
    }

    private void RebuildChildren()
    {
        Children.Clear();
        Array.Clear(presenters);
        Array.Clear(strips);
        raisedRole = null;
        dropOverlay.IsVisible = false;
        dropOverlay.Clear();

        if (Layout is not { } layout)
        {
            Children.Add(drawerSurface);
            Children.Add(dropOverlay);
            return;
        }

        for (var i = 0; i < strips.Length; i++)
        {
            var role = (RegionRole)i;
            var strip = new AutoHideStrip { Layout = layout, Role = role, Edge = EdgeOf(layout, role) };
            strips[i] = strip;
            Children.Add(strip);
            strip.Rebuild();
        }

        for (var i = 0; i < presenters.Length; i++)
        {
            var presenter = new RegionPresenter(layout, (RegionRole)i);
            presenters[i] = presenter;
            Children.Add(presenter);
        }

        for (var i = 0; i < regionSplitters.Length; i++)
        {
            var splitter = regionSplitters[i] ??= new RegionSplitter();
            splitter.Layout = layout;
            splitter.SplitterIndex = i;
            Children.Add(splitter);
        }

        Children.Add(drawerSurface);
        Children.Add(dropOverlay);
    }

    private void Subscribe()
    {
        if (subscribed || Layout is not { } layout)
        {
            return;
        }

        subscribed = true;
        subscribedLayout = layout;
        layout.PropertyChanged += OnLayoutChanged;
        layout.Order.CollectionChanged += OnOrderChanged;
        layout.Floating.CollectionChanged += OnFloatingChanged;
        for (var i = 0; i < layout.Order.Count; i++)
        {
            AttachRegion(layout.Order[i]);
        }

        SyncFloatingWindows();
    }

    private void Unsubscribe()
    {
        if (!subscribed || subscribedLayout is not { } layout)
        {
            return;
        }

        subscribed = false;
        subscribedLayout = null;
        layout.PropertyChanged -= OnLayoutChanged;
        layout.Order.CollectionChanged -= OnOrderChanged;
        layout.Floating.CollectionChanged -= OnFloatingChanged;
        for (var i = 0; i < layout.Order.Count; i++)
        {
            DetachRegion(layout.Order[i]);
        }

    }

    private void AttachRegion(RegionModel region)
    {
        region.PropertyChanged += OnRegionChanged;
        region.Panels.CollectionChanged += OnRegionPanelsChanged;
        for (var i = 0; i < region.Panels.Count; i++)
        {
            region.Panels[i].PropertyChanged += OnPanelChanged;
        }
    }

    private void DetachRegion(RegionModel region)
    {
        region.PropertyChanged -= OnRegionChanged;
        region.Panels.CollectionChanged -= OnRegionPanelsChanged;
        for (var i = 0; i < region.Panels.Count; i++)
        {
            region.Panels[i].PropertyChanged -= OnPanelChanged;
        }
    }

    private void OnLayoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        InvalidateAll();
        UpdateZOrder();
    }

    private void OnOrderChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateAll();
        UpdateZOrder();
        RefreshStripEdges();
    }

    private void OnRegionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RegionModel.Width):
                InvalidateArrange();
                break;
            case nameof(RegionModel.IsAutoHidden):
                InvalidateArrange();
                UpdateZOrder();
                break;
        }
    }

    /// <summary>Invalidates the host, its presenters and the strips.</summary>
    private void InvalidateAll()
    {
        InvalidateMeasure();
        for (var i = 0; i < presenters.Length; i++)
        {
            presenters[i]?.InvalidateMeasure();
        }

        for (var i = 0; i < strips.Length; i++)
        {
            strips[i]?.InvalidateMeasure();
        }
    }

    private void OnRegionPanelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildPresenters();
        RefreshStrips();
    }

    private void OnPanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        InvalidateAll();
        if (e.PropertyName == nameof(PanelModel.IsVisible) || e.PropertyName == nameof(PanelModel.Title))
        {
            RefreshStrips();
        }
    }

    private void OnFloatingChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncFloatingWindows();

    private void RebuildPresenters()
    {
        for (var i = 0; i < presenters.Length; i++)
        {
            presenters[i]?.Rebuild();
        }
    }

    private void RefreshStrips()
    {
        for (var i = 0; i < strips.Length; i++)
        {
            strips[i]?.Rebuild();
        }
    }

    private void RefreshStripEdges()
    {
        if (Layout is not { } layout)
        {
            return;
        }

        for (var i = 0; i < strips.Length; i++)
        {
            if (strips[i] is { } strip)
            {
                strip.Edge = EdgeOf(layout, (RegionRole)i);
            }
        }
    }

    /// <summary>
    /// Keeps the presenter that has to paint last on top: the open drawer's region, otherwise the Inspector while the
    /// preview covers Main. Region splitters sit above the docked content and are hidden while either is active.
    /// </summary>
    private void UpdateZOrder()
    {
        if (Layout is not { } layout)
        {
            return;
        }

        var drawerRole = layout.DrawerPanel is { } drawer && layout.GetRegion(drawer.Region).IsAutoHidden ? drawer.Region : (RegionRole?)null;
        RegionRole? raise = drawerRole;
        if (raise is null && layout.IsPreviewCoveringMain && !layout.Inspector.IsAutoHidden && layout.PreviewTabs.IsVisible)
        {
            raise = RegionRole.Inspector;
        }

        drawerSurface.IsVisible = drawerRole is not null;
        if (drawerRole is { } drawn)
        {
            AutomationProperties.SetAutomationId(drawerSurface, "drawer-" + drawn);
            drawerSurface.BorderThickness = layout.EdgeOf(drawn) == RegionEdge.Left
                ? new Thickness(0, 0, 1, 0)
                : new Thickness(1, 0, 0, 0);
        }

        if (raise == raisedRole)
        {
            return;
        }

        raisedRole = raise;
        if (arranging)
        {
            zOrderPending = true;
            return;
        }

        ApplyZOrder();
    }

    private void ApplyZOrder()
    {
        if (Layout is not { } layout || raisedRole is not { } role || presenters[(int)role] is not { } presenter)
        {
            return;
        }

        var overlayIndex = Children.IndexOf(dropOverlay);
        if (overlayIndex < 0)
        {
            return;
        }

        var surfaceIndex = Children.IndexOf(drawerSurface);
        if (surfaceIndex >= 0 && layout.DrawerPanel is not null && surfaceIndex > overlayIndex - 1)
        {
            Children.Move(surfaceIndex, Math.Max(0, overlayIndex - 1));
            overlayIndex = Children.IndexOf(dropOverlay);
        }

        var from = Children.IndexOf(presenter);
        var to = Math.Max(0, overlayIndex - 1);
        if (from >= 0 && from != to)
        {
            Children.Move(from, to);
        }

        InvalidateMeasure();
    }

    /// <summary>The auto-hide edge of a region; Main never collapses, so asking the model for it would throw.</summary>
    private static RegionEdge EdgeOf(WorkbenchLayout layout, RegionRole role) =>
        role == RegionRole.Main ? RegionEdge.Left : layout.EdgeOf(role);

    /// <summary>
    /// Width the drawer opens at. A drawer shows one column of its region, not the region: the preview keeps the width its
    /// column has while the region is pinned (covering Main adds Main), the files column is the side region's default width
    /// and the preview column joins it on the left as soon as it has a tab - the two are pinned together and travel together.
    /// Anything else is the region's own default width, never the width it happened to be dragged to before auto-hiding.
    /// </summary>
    private static double DrawerWidthOf(WorkbenchLayout layout, PanelModel panel)
    {
        var previewWidth = Math.Max(WorkbenchMetrics.PreviewMinWidth, layout.PreviewWidth);
        if (ReferenceEquals(panel, layout.PreviewTabs))
        {
            return previewWidth;
        }

        if (ReferenceEquals(panel, layout.FilesPanel))
        {
            return WorkbenchMetrics.DefaultInspectorWidth
                + (layout.PreviewTabs.IsVisible ? previewWidth + WorkbenchMetrics.PanelSplitterWidth : 0);
        }

        return panel.Region == RegionRole.Project ? WorkbenchMetrics.DefaultProjectWidth : WorkbenchMetrics.DefaultInspectorWidth;
    }

    private RegionRole? RegionAt(Point point)
    {
        for (var i = 0; i < regionDocked.Length; i++)
        {
            if (regionDocked[i] && regionRects[i].Width > 0 && regionRects[i].Contains(point))
            {
                return (RegionRole)i;
            }
        }

        return null;
    }

    private void FinishSwap(RegionRole? target)
    {
        var source = swapSource;
        swapPending = false;
        swapping = false;
        swapSource = null;
        swapHovered = null;
        dropOverlay.SetHovered(null);
        dropOverlay.IsVisible = false;
        dropOverlay.Clear();
        InvalidateArrange();
        if (source is { } from && target is { } to && from != to)
        {
            Layout?.SwapRegions(from, to);
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (swapPending)
        {
            FinishSwap(null);
            e.Handled = true;
            return;
        }

        if (Layout is { DrawerPanel: not null } layout)
        {
            layout.CloseDrawer();
            e.Handled = true;
        }
    }

    private void SyncFloatingWindows()
    {
        if (Layout is not { } layout)
        {
            CloseAllWindows();
            return;
        }

        if (windows.Count > 0)
        {
            List<string>? stale = null;
            foreach (var pair in windows)
            {
                if (!ContainsFloating(layout, pair.Key))
                {
                    (stale ??= []).Add(pair.Key);
                }
            }

            if (stale is not null)
            {
                for (var i = 0; i < stale.Count; i++)
                {
                    var window = windows[stale[i]];
                    windows.Remove(stale[i]);
                    if (!window.IsClosingSelf)
                    {
                        window.CloseWithoutDocking();
                    }
                }
            }
        }

        for (var i = 0; i < layout.Floating.Count; i++)
        {
            var item = layout.Floating[i];
            if (windows.ContainsKey(item.Id))
            {
                continue;
            }

            var window = new FloatingPanelWindow(layout, item);
            window.Closed += OnFloatingWindowClosed;
            windows[item.Id] = window;
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }
        }
    }

    private static bool ContainsFloating(WorkbenchLayout layout, string id)
    {
        for (var i = 0; i < layout.Floating.Count; i++)
        {
            if (string.Equals(layout.Floating[i].Id, id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void OnFloatingWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not FloatingPanelWindow window)
        {
            return;
        }

        window.Closed -= OnFloatingWindowClosed;
        List<string>? stale = null;
        foreach (var pair in windows)
        {
            if (ReferenceEquals(pair.Value, window))
            {
                (stale ??= []).Add(pair.Key);
            }
        }

        if (stale is not null)
        {
            for (var i = 0; i < stale.Count; i++)
            {
                windows.Remove(stale[i]);
            }
        }
    }

    private void CloseAllWindows()
    {
        if (windows.Count == 0)
        {
            return;
        }

        List<string>? keys = null;
        foreach (var pair in windows)
        {
            (keys ??= []).Add(pair.Key);
        }

        if (keys is not null)
        {
            for (var i = 0; i < keys.Count; i++)
            {
                var window = windows[keys[i]];
                windows.Remove(keys[i]);
                if (!window.IsClosingSelf)
                {
                    window.CloseWithoutDocking();
                }
            }
        }
    }
}
