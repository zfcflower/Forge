using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

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
    public Rect RegionRect { get; set; }

    /// <summary>Where the preview group goes while it covers Main, in this control's coordinates; empty otherwise.</summary>
    public Rect PreviewCoverRect { get; set; }

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
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(size);
        }

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ResetChildren();
        var body = RegionRect;
        if (body.Width <= 0 || body.Height <= 0)
        {
            return finalSize;
        }

        var region = Layout.GetRegion(Role);
        if (region.MaximizedPanel is { IsVisible: true } maximized && ChromeOf(maximized) is { } maximizedChrome)
        {
            maximizedChrome.Arrange(body);
            return finalSize;
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

        return finalSize;
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
            mainChrome?.Arrange(new Rect(body.X, body.Y, body.Width, Math.Max(0, body.Height - stripHeight)));
            bottomStrip.Arrange(new Rect(body.X, body.Y + body.Height - stripHeight, body.Width, stripHeight));
            return;
        }

        if (!bottomVisible)
        {
            mainChrome?.Arrange(body);
            return;
        }

        var splitterHeight = WorkbenchMetrics.PanelSplitterWidth;
        var space = Math.Max(0, body.Height - splitterHeight);
        var bottomHeight = Math.Clamp(
            Layout.BottomHeight,
            WorkbenchMetrics.PanelMinHeight,
            Math.Max(WorkbenchMetrics.PanelMinHeight, space - WorkbenchMetrics.PanelMinHeight));
        bottomHeight = Math.Min(bottomHeight, space);
        var mainHeight = Math.Max(0, space - bottomHeight);
        mainChrome?.Arrange(new Rect(body.X, body.Y, body.Width, mainHeight));
        bottomChrome?.Arrange(new Rect(body.X, body.Y + mainHeight + splitterHeight, body.Width, bottomHeight));
        bottomSplitter.TrackLength = body.Height;
        bottomSplitter.Arrange(new Rect(
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
                    chromes[i].Arrange(body);
                    return;
                }
            }

            return;
        }

        var splitterHeight = WorkbenchMetrics.PanelSplitterWidth;
        var height = (body.Height - ((visible - 1) * splitterHeight)) / visible;
        var y = body.Y;
        for (var i = 0; i < chromes.Count; i++)
        {
            if (chromes[i].Model is not { IsVisible: true })
            {
                continue;
            }

            chromes[i].Arrange(new Rect(body.X, y, body.Width, Math.Max(0, height)));
            y += height + splitterHeight;
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
        if (previewVisible)
        {
            var previewWidth = Math.Min(
                Math.Max(WorkbenchMetrics.PreviewMinWidth, Layout.PreviewWidth),
                Math.Max(WorkbenchMetrics.PreviewMinWidth, body.Width));
            if (filesColumnVisible)
            {
                previewWidth = Math.Min(previewWidth, Math.Max(0, body.Width - splitterWidth - WorkbenchMetrics.SideMinWidth));
            }

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
                previewSplitter.Arrange(new Rect(
                    previewColumn.X + ((splitterWidth - WorkbenchMetrics.SplitterHitWidth) / 2),
                    body.Y,
                    WorkbenchMetrics.SplitterHitWidth,
                    body.Height));
            }
        }

        if (previewVisible)
        {
            var coverRect = PreviewCoverRect;
            previewChrome!.Arrange(coverRect.Width > 0 && coverRect.Height > 0 ? coverRect : previewColumn);
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
            filesChrome!.Arrange(new Rect(filesColumn.X, filesColumn.Y, filesColumn.Width, filesHeight));
            toolsChrome!.Arrange(new Rect(
                filesColumn.X,
                filesColumn.Y + filesHeight + splitterWidth,
                filesColumn.Width,
                Math.Max(0, space - filesHeight)));
            inspectorSplitter.TrackLength = filesColumn.Height;
            inspectorSplitter.Arrange(new Rect(
                filesColumn.X,
                filesColumn.Y + filesHeight + ((splitterWidth - WorkbenchMetrics.SplitterHitWidth) / 2),
                filesColumn.Width,
                WorkbenchMetrics.SplitterHitWidth));
            return;
        }

        if (filesVisible)
        {
            filesChrome!.Arrange(filesColumn);
            return;
        }

        toolsChrome!.Arrange(filesColumn);
    }

    /// <summary>Gives every child an empty rectangle: an unused splitter or strip then paints nothing and cannot be hit.</summary>
    private void ResetChildren()
    {
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Arrange(default);
        }
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
