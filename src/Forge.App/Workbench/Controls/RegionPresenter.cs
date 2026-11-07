using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Forge.App.ViewModels;

namespace Forge.App.Workbench.Controls;

/// <summary>
/// Lays out the panels of one region — Main is the tab group above the bottom group, Inspector is the preview column
/// beside the files column, Project is a single panel — with the 1 px panel splitters between them. All widths and
/// ratios come from <see cref="WorkbenchLayout"/>; the presenter only turns them into rectangles.
/// </summary>
internal sealed class RegionPresenter : Panel
{
    private readonly List<PanelChrome> chromes = [];
    private readonly PanelSplitter bottomSplitter = new() { Kind = PanelSplitterKind.Bottom };
    private readonly PanelSplitter previewSplitter = new() { Kind = PanelSplitterKind.Preview };
    private readonly PanelSplitter inspectorSplitter = new() { Kind = PanelSplitterKind.Inspector };
    private readonly BottomCollapsedStrip bottomStrip = new();
    private PanelModel? lastMaximized;
    private Rect[] childRects = [];
    private Rect regionRect;
    private Rect previewCoverRect;

    public RegionPresenter(WorkbenchLayout layout, RegionRole role)
    {
        Layout = layout;
        Role = role;
        AutomationProperties.SetAutomationId(this, "region-" + role);
        // Only the region that actually uses a gap exposes its automation id.
        AutomationProperties.SetAutomationId(bottomSplitter, role == RegionRole.Main ? "panel-splitter-bottom" : null);
        AutomationProperties.SetAutomationId(previewSplitter, role == RegionRole.Inspector ? "panel-splitter-preview" : null);
        AutomationProperties.SetAutomationId(inspectorSplitter, role == RegionRole.Inspector ? "panel-splitter-inspector" : null);
        AutomationProperties.SetAutomationId(bottomStrip, role == RegionRole.Main ? "autohide-bottom" : null);
        bottomSplitter.Layout = layout;
        previewSplitter.Layout = layout;
        inspectorSplitter.Layout = layout;
        bottomStrip.Layout = layout;
        Rebuild();
    }

    public WorkbenchLayout Layout { get; }

    public RegionRole Role { get; }

    /// <summary>The region's own rectangle in this control's coordinates; smaller than the bounds when the preview covers Main.</summary>
    public Rect RegionRect
    {
        get => regionRect;
        set
        {
            if (regionRect != value)
            {
                regionRect = value;
                InvalidateMeasure();
            }
        }
    }

    /// <summary>Where the preview group goes while it covers Main, in this control's coordinates; empty otherwise.</summary>
    public Rect PreviewCoverRect
    {
        get => previewCoverRect;
        set
        {
            if (previewCoverRect != value)
            {
                previewCoverRect = value;
                InvalidateMeasure();
            }
        }
    }

    /// <summary>
    /// Width of the preview column as <see cref="ArrangeInspector"/> lays it out: the preview width clamped to the column
    /// space, and to the side minimum while the files column is visible. The host's cover rectangle uses the same width, so
    /// the covered area ends exactly where the files column begins.
    /// </summary>
    internal static double PreviewColumnWidth(WorkbenchLayout layout, double columnWidth, bool filesColumnVisible)
    {
        var previewWidth = Math.Min(
            Math.Max(WorkbenchMetrics.PreviewMinWidth, layout.PreviewWidth),
            Math.Max(WorkbenchMetrics.PreviewMinWidth, columnWidth));
        if (filesColumnVisible)
        {
            previewWidth = Math.Min(previewWidth, Math.Max(0, columnWidth - WorkbenchMetrics.PanelSplitterWidth - WorkbenchMetrics.SideMinWidth));
        }

        return previewWidth;
    }

    /// <summary>Recreates one chrome per panel; called when the panel list of the region changes.</summary>
    public void Rebuild()
    {
        chromes.Clear();
        Children.Clear();
        var region = Layout.GetRegion(Role);
        var sidebar = Role is RegionRole.Project or RegionRole.Inspector;
        for (var i = 0; i < region.Panels.Count; i++)
        {
            var chrome = new PanelChrome { Model = region.Panels[i], Layout = Layout, Sidebar = sidebar };
            chromes.Add(chrome);
            Children.Add(chrome);
        }

        Children.Add(bottomSplitter);
        Children.Add(previewSplitter);
        Children.Add(inspectorSplitter);
        Children.Add(bottomStrip);
        childRects = new Rect[Children.Count];
        bottomStrip.Rebuild();
        InvalidateMeasure();
    }

    /// <summary>Rebuilds the buttons of the collapsed bottom strip (its tabs changed or their titles did).</summary>
    public void RefreshBottomStrip() => bottomStrip.Rebuild();

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : 0,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
        UpdateChildRects();
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(childRects[i].Size);
        }

        return size;
    }

    private void Place(Control? chrome, Rect rect)
    {
        if (chrome is null)
        {
            return;
        }

        childRects[Children.IndexOf(chrome)] = rect;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateChildRects();
        for (var i = 0; i < Children.Count; i++)
        {
            // Splitter changes can invalidate only arrange; measure at the new slot before arranging.
            Children[i].Measure(childRects[i].Size);
            Children[i].Arrange(childRects[i]);
        }

        return finalSize;
    }

    private void UpdateChildRects()
    {
        Array.Clear(childRects);
        var body = RegionRect;
        if (body.Width <= 0 || body.Height <= 0)
        {
            return;
        }

        var region = Layout.GetRegion(Role);
        var maximizedChrome = region.MaximizedPanel is { IsVisible: true } maximized && ChromeOf(maximized) is { } chrome
            ? chrome
            : null;
        ApplyMaximizedChrome(maximizedChrome);
        if (maximizedChrome is not null)
        {
            Place(maximizedChrome, body);
            return;
        }

        switch (Role)
        {
            case RegionRole.Main:
                ArrangeMain(body);
                break;
            case RegionRole.Inspector:
                ArrangeInspector(body);
                break;
            default:
                ArrangeStack(body);
                break;
        }
    }

    /// <summary>
    /// While a panel is maximized its chrome is the only visible one (the others collapse, not just to empty rectangles);
    /// restoring brings every chrome back. The change fades the affected chromes in unless reduced motion is on — the
    /// maximized panel on engage, the whole region on restore.
    /// </summary>
    private void ApplyMaximizedChrome(PanelChrome? maximized)
    {
        if (ReferenceEquals(maximized?.Model, lastMaximized))
        {
            return;
        }

        lastMaximized = maximized?.Model;
        var engage = maximized is not null;
        for (var i = 0; i < chromes.Count; i++)
        {
            var chrome = chromes[i];
            var visible = !engage || ReferenceEquals(chrome, maximized);
            if (!visible && chrome.IsVisible)
            {
                // Hidden controls skip arrange, so clear only the chrome being collapsed before hiding it.
                chrome.Measure(default);
                chrome.Arrange(default);
            }

            chrome.IsVisible = visible;
        }

        if (!engage)
        {
            for (var i = 0; i < chromes.Count; i++)
            {
                FadeIn(chromes[i]);
            }

            return;
        }

        FadeIn(maximized!);
    }

    /// <summary>Ramps the chrome in from transparent; with reduced motion the final opacity applies immediately.</summary>
    private static void FadeIn(PanelChrome chrome)
    {
        if (TreeMotion.ReducedMotion)
        {
            chrome.Opacity = 1;
            return;
        }

        // Start transparent in this arrange pass; the posted restore runs after layout, so the Opacity transition
        // animates the fade. A window that is gone by then just keeps a detached chrome at full opacity.
        chrome.Opacity = 0;
        Dispatcher.UIThread.Post(() => chrome.Opacity = 1);
    }

    private void ArrangeMain(Rect body)
    {
        var mainChrome = ChromeOf(Layout.MainTabs);
        var group = Layout.BottomTabs;
        var bottomChrome = ChromeOf(group);
        var collapsed = Layout.IsBottomCollapsed;
        var bottomVisible = bottomChrome is not null && group.IsVisible;
        if (collapsed && bottomChrome is not null && Layout.BottomHasContent)
        {
            var stripHeight = Math.Min(WorkbenchMetrics.AutoHideStripWidth, body.Height);
            Place(mainChrome, new Rect(body.X, body.Y, body.Width, Math.Max(0, body.Height - stripHeight)));
            Place(bottomStrip, new Rect(body.X, body.Y + body.Height - stripHeight, body.Width, stripHeight));
            return;
        }

        if (!bottomVisible)
        {
            Place(mainChrome, body);
            return;
        }

        var splitterHeight = WorkbenchMetrics.PanelSplitterWidth;
        var space = Math.Max(0, body.Height - splitterHeight);
        if (Layout.MainTabs.IsMinimized || group.IsMinimized)
        {
            var title = Math.Min(PanelChrome.TitleBarHeight, space);
            var topHeight = Layout.MainTabs.IsMinimized ? title : Math.Max(0, space - title);
            Place(mainChrome, new Rect(body.X, body.Y, body.Width, topHeight));
            Place(bottomChrome, new Rect(body.X, body.Y + topHeight + splitterHeight, body.Width, Math.Max(0, space - topHeight)));
            return;
        }
        var bottomHeight = Math.Clamp(
            Layout.BottomHeight,
            WorkbenchMetrics.PanelMinHeight,
            Math.Max(WorkbenchMetrics.PanelMinHeight, space - WorkbenchMetrics.PanelMinHeight));
        bottomHeight = Math.Min(bottomHeight, space);
        var mainHeight = Math.Max(0, space - bottomHeight);
        Place(mainChrome, new Rect(body.X, body.Y, body.Width, mainHeight));
        Place(bottomChrome, new Rect(body.X, body.Y + mainHeight + splitterHeight, body.Width, bottomHeight));
        bottomSplitter.TrackLength = body.Height;
        Place(bottomSplitter, new Rect(
            body.X,
            body.Y + mainHeight + ((splitterHeight - WorkbenchMetrics.SplitterHitWidth) / 2),
            body.Width,
            WorkbenchMetrics.SplitterHitWidth));
    }

    private void ArrangeStack(Rect body)
    {
        var visible = 0;
        for (var i = 0; i < chromes.Count; i++)
        {
            if (chromes[i].Model is { IsVisible: true })
            {
                visible++;
            }
        }

        if (visible == 0)
        {
            return;
        }

        if (visible == 1)
        {
            for (var i = 0; i < chromes.Count; i++)
            {
                if (chromes[i].Model is { IsVisible: true })
                {
                    Place(chromes[i], body);
                    return;
                }
            }

            return;
        }

        var splitterHeight = WorkbenchMetrics.PanelSplitterWidth;
        var minimized = 0;
        for (var i = 0; i < chromes.Count; i++)
        {
            if (chromes[i].Model is { IsVisible: true, IsMinimized: true })
            {
                minimized++;
            }
        }
        var title = PanelChrome.TitleBarHeight;
        var expanded = visible - minimized;
        var height = expanded > 0
            ? (body.Height - ((visible - 1) * splitterHeight) - (minimized * title)) / expanded
            : 0;
        var y = body.Y;
        for (var i = 0; i < chromes.Count; i++)
        {
            if (chromes[i].Model is not { IsVisible: true })
            {
                continue;
            }

            var slot = chromes[i].Model!.IsMinimized ? title : Math.Max(0, height);
            Place(chromes[i], new Rect(body.X, y, body.Width, slot));
            y += slot + splitterHeight;
        }
    }

    private void ArrangeInspector(Rect body)
    {
        var previewChrome = ChromeOf(Layout.PreviewTabs);
        var filesChrome = ChromeOf(Layout.FilesPanel);
        var toolsChrome = ChromeOf(Layout.InspectorTools);
        var previewVisible = previewChrome is not null && Layout.PreviewTabs.IsVisible;
        var filesVisible = filesChrome is not null && Layout.FilesPanel.IsVisible;
        var toolsVisible = toolsChrome is not null && Layout.InspectorTools.IsVisible;
        var filesColumnVisible = filesVisible || toolsVisible;
        var splitterWidth = WorkbenchMetrics.PanelSplitterWidth;

        var previewColumn = default(Rect);
        var filesColumn = body;
        var previewMinimized = previewVisible && filesColumnVisible && Layout.PreviewTabs.IsMinimized;
        if (previewMinimized)
        {
            var title = Math.Min(PanelChrome.TitleBarHeight, body.Height);
            Place(previewChrome!, new Rect(body.X, body.Y, body.Width, title));
            filesColumn = new Rect(body.X, body.Y + title, body.Width, Math.Max(0, body.Height - title));
        }
        else if (previewVisible)
        {
            var previewWidth = PreviewColumnWidth(Layout, body.Width, filesColumnVisible);

            if (Layout.IsPreviewLeftOfFiles)
            {
                previewColumn = new Rect(body.X, body.Y, previewWidth, body.Height);
                filesColumn = new Rect(
                    body.X + previewWidth + splitterWidth,
                    body.Y,
                    Math.Max(0, body.Width - previewWidth - splitterWidth),
                    body.Height);
            }
            else
            {
                previewColumn = new Rect(body.X + body.Width - previewWidth, body.Y, previewWidth, body.Height);
                filesColumn = new Rect(body.X, body.Y, Math.Max(0, body.Width - previewWidth - splitterWidth), body.Height);
            }

            if (filesColumnVisible)
            {
                previewSplitter.TrackLength = body.Width;
                // The 1 px gap between the two columns; the hit band straddles it on whichever side the preview column sits.
                var gapX = Layout.IsPreviewLeftOfFiles ? previewColumn.Right : filesColumn.Right;
                Place(previewSplitter, new Rect(
                    gapX + ((splitterWidth - WorkbenchMetrics.SplitterHitWidth) / 2),
                    body.Y,
                    WorkbenchMetrics.SplitterHitWidth,
                    body.Height));
            }
        }

        if (previewVisible && !previewMinimized)
        {
            var coverRect = PreviewCoverRect;
            Place(previewChrome!, coverRect.Width > 0 && coverRect.Height > 0 ? coverRect : previewColumn);
        }

        if (!filesColumnVisible)
        {
            return;
        }

        if (filesVisible && toolsVisible)
        {
            var space = Math.Max(0, filesColumn.Height - splitterWidth);
            var filesHeight = Math.Clamp(
                Layout.InspectorSplitRatio * filesColumn.Height,
                WorkbenchMetrics.PanelMinHeight,
                Math.Max(WorkbenchMetrics.PanelMinHeight, space - WorkbenchMetrics.PanelMinHeight));
            filesHeight = Math.Min(filesHeight, space);
            if (Layout.FilesPanel.IsMinimized || Layout.InspectorTools.IsMinimized)
            {
                var title = Math.Min(PanelChrome.TitleBarHeight, space);
                filesHeight = Layout.FilesPanel.IsMinimized ? title : Math.Max(0, space - title);
                Place(filesChrome!, new Rect(filesColumn.X, filesColumn.Y, filesColumn.Width, filesHeight));
                Place(toolsChrome!, new Rect(filesColumn.X, filesColumn.Y + filesHeight + splitterWidth, filesColumn.Width, Math.Max(0, space - filesHeight)));
                return;
            }
            Place(filesChrome!, new Rect(filesColumn.X, filesColumn.Y, filesColumn.Width, filesHeight));
            Place(toolsChrome!, new Rect(
                filesColumn.X,
                filesColumn.Y + filesHeight + splitterWidth,
                filesColumn.Width,
                Math.Max(0, space - filesHeight)));
            inspectorSplitter.TrackLength = filesColumn.Height;
            Place(inspectorSplitter, new Rect(
                filesColumn.X,
                filesColumn.Y + filesHeight + ((splitterWidth - WorkbenchMetrics.SplitterHitWidth) / 2),
                filesColumn.Width,
                WorkbenchMetrics.SplitterHitWidth));
            return;
        }

        if (filesVisible)
        {
            Place(filesChrome!, filesColumn);
            return;
        }

        Place(toolsChrome!, filesColumn);
    }

    private PanelChrome? ChromeOf(PanelModel? panel)
    {
        if (panel is null)
        {
            return null;
        }

        for (var i = 0; i < chromes.Count; i++)
        {
            if (ReferenceEquals(chromes[i].Model, panel))
            {
                return chromes[i];
            }
        }

        return null;
    }
}
