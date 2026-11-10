using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
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
    private readonly List<Border> stackGaps = [];
    private readonly List<PanelChrome> below = [];
    private readonly List<PanelChrome> columnSlots = [];
    private readonly List<PanelSplitter> belowSplitters = [];
    private readonly double[] columnHeights = new double[PanelStackSolver.MaxSlots];
    private readonly double[] columnWeights = new double[PanelStackSolver.MaxSlots];
    private readonly double[] columnFlexibleWeights = new double[PanelStackSolver.MaxSlots];
    private readonly double[] columnOut = new double[PanelStackSolver.MaxSlots];
    private readonly int[] columnFlexible = new int[PanelStackSolver.MaxSlots];
    private readonly PanelSplitter bottomSplitter = new() { Kind = PanelSplitterKind.Bottom };
    private readonly PanelSplitter previewSplitter = new() { Kind = PanelSplitterKind.Preview };
    private readonly PanelSplitter inspectorSplitter = new() { Kind = PanelSplitterKind.Inspector };
    private readonly PanelSplitter projectSplitter = new() { Kind = PanelSplitterKind.Project };
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
        AutomationProperties.SetAutomationId(projectSplitter, role == RegionRole.Project ? "panel-splitter-project" : null);
        bottomSplitter.Layout = layout;
        previewSplitter.Layout = layout;
        inspectorSplitter.Layout = layout;
        projectSplitter.Layout = layout;
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
        stackGaps.Clear();
        belowSplitters.Clear();
        Children.Clear();
        var region = Layout.GetRegion(Role);
        var sidebar = Role is RegionRole.Project or RegionRole.Inspector;
        for (var i = 0; i < region.Panels.Count; i++)
        {
            var chrome = new PanelChrome { Model = region.Panels[i], Layout = Layout, Sidebar = sidebar };
            chromes.Add(chrome);
            Children.Add(chrome);
        }

        // One 1 px line per gap between stacked panels, for the gaps that get no splitter (a minimized panel's own edge): the
        // gap alone is the lower panel's title bar sitting on the window background.
        for (var i = 1; i < region.Panels.Count; i++)
        {
            var line = new Border { Height = WorkbenchMetrics.PanelSplitterWidth, IsHitTestVisible = false };
            WorkbenchResources.Link(line, Border.BackgroundProperty, "BorderBrush");
            stackGaps.Add(line);
            Children.Add(line);
        }

        // One splittable line per gap of the Inspector column, created with the chromes so a layout pass never mutates the
        // child list; a gap that cannot be dragged is simply arranged to nothing.
        if (Role == RegionRole.Inspector)
        {
            for (var i = 1; i < region.Panels.Count; i++)
            {
                var splitter = new PanelSplitter { Layout = Layout, Kind = PanelSplitterKind.BelowStack };
                belowSplitters.Add(splitter);
                Children.Add(splitter);
            }
        }

        Children.Add(bottomSplitter);
        Children.Add(previewSplitter);
        Children.Add(inspectorSplitter);
        Children.Add(projectSplitter);
        childRects = new Rect[Children.Count];
        InvalidateMeasure();
    }

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
        if (childRects.Length < Children.Count)
        {
            Array.Resize(ref childRects, Children.Count);
        }

        Array.Clear(childRects);
        var body = RegionRect;
        if (body.Width <= 0 || body.Height <= 0)
        {
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

    private void ArrangeMain(Rect body)
    {
        var mainChrome = ChromeOf(Layout.MainTabs);
        var group = Layout.BottomTabs;
        var bottomChrome = ChromeOf(group);
        var bottomVisible = bottomChrome is not null && group.IsVisible;

        if (!bottomVisible)
        {
            Place(mainChrome, body);
            return;
        }

        var splitterHeight = WorkbenchMetrics.PanelSplitterWidth;
        var space = Math.Max(0, body.Height - splitterHeight);
        // A minimized group keeps its title bar - that is where its tabs and its restore button live.
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
        var available = Math.Max(0, body.Height - ((visible - 1) * splitterHeight) - (minimized * title));

        // The left column's two panels (projects above, general chats below) are not equal halves: the first one keeps the
        // persisted share and the line under it drags that share. Every other stacked region keeps equal slots.
        double firstSlot = -1;
        if (Role == RegionRole.Project && expanded > 1 && !AnyMinimized())
        {
            // Whole pixels, for the same reason as the equal slots below.
            firstSlot = Math.Floor(Math.Clamp(
                Layout.ProjectSplitRatio * available,
                WorkbenchMetrics.PanelMinHeight,
                Math.Max(WorkbenchMetrics.PanelMinHeight, available - ((expanded - 1) * WorkbenchMetrics.PanelMinHeight))));
        }
        // Whole pixels per expanded panel, the remainder on the last one: a fractional slot would land the split on a
        // half pixel, and layout rounding then swallows the 1 px gap — and with it the separator above the lower panel's
        // title bar — whenever the region height is not a multiple of the panel count.
        var expandedSlot = expanded > 0 ? Math.Floor(available / expanded) : 0;
        var lastExpandedSlot = expanded > 0 ? available - (expandedSlot * (expanded - 1)) : 0;
        var y = body.Y;
        var placed = 0;
        var expandedPlaced = 0;
        for (var i = 0; i < chromes.Count; i++)
        {
            if (chromes[i].Model is not { IsVisible: true })
            {
                continue;
            }

            double slot;
            if (chromes[i].Model!.IsMinimized)
            {
                slot = title;
            }
            else
            {
                expandedPlaced++;
                if (firstSlot >= 0)
                {
                    slot = expandedPlaced == 1 ? firstSlot : (available - firstSlot) / (expanded - 1);
                }
                else
                {
                    slot = expandedPlaced == expanded ? lastExpandedSlot : expandedSlot;
                }
            }

            Place(chromes[i], new Rect(body.X, y, body.Width, Math.Max(0, slot)));
            y += Math.Max(0, slot);
            placed++;
            if (placed < visible)
            {
                if (placed - 1 < stackGaps.Count)
                {
                    Place(stackGaps[placed - 1], new Rect(body.X, y, body.Width, splitterHeight));
                }

                // The first gap of the left column is draggable: it moves the projects/general-chats split.
                if (placed == 1 && Role == RegionRole.Project && firstSlot >= 0)
                {
                    projectSplitter.TrackLength = body.Height;
                    Place(projectSplitter, new Rect(
                        body.X,
                        y + ((splitterHeight - WorkbenchMetrics.SplitterHitWidth) / 2),
                        body.Width,
                        WorkbenchMetrics.SplitterHitWidth));
                }

                y += splitterHeight;
            }
        }
    }

    private bool AnyMinimized()
    {
        for (var i = 0; i < chromes.Count; i++)
        {
            if (chromes[i].Model is { IsVisible: true, IsMinimized: true })
            {
                return true;
            }
        }

        return false;
    }

    private void ArrangeInspector(Rect body)
    {
        var previewChrome = ChromeOf(Layout.PreviewTabs);
        var filesChrome = ChromeOf(Layout.FilesPanel);
        var previewVisible = previewChrome is not null && Layout.PreviewTabs.IsVisible;
        var filesVisible = filesChrome is not null && Layout.FilesPanel.IsVisible;

        // Everything docked below the files panel (Git, then the tool group), in panel order; the column is one stack whose
        // top slot is the files panel and whose lines all drag.
        below.Clear();
        foreach (var panel in Layout.GetRegion(RegionRole.Inspector).Panels)
        {
            if (ReferenceEquals(panel, Layout.PreviewTabs) || ReferenceEquals(panel, Layout.FilesPanel))
            {
                continue;
            }

            if (ChromeOf(panel) is { Model: { IsVisible: true } } chrome)
            {
                below.Add(chrome);
            }
        }

        var filesColumnVisible = filesVisible || below.Count > 0;
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

        ArrangeFilesColumn(filesColumn, filesVisible ? filesChrome : null, splitterWidth);
    }

    /// <summary>
    /// The inspector's right column: the files panel on top, then every docked panel below it (Git, the tool group), one
    /// draggable 1 px line between each. The files slot keeps the persisted ratio against the rest of the column and the
    /// panels below share what is left in the proportions they were last dragged to, so every line behaves the same; a
    /// minimized panel takes only its title bar and its own edge is not draggable.
    /// </summary>
    private void ArrangeFilesColumn(Rect column, PanelChrome? filesChrome, double splitterWidth)
    {
        columnSlots.Clear();
        if (filesChrome is not null)
        {
            columnSlots.Add(filesChrome);
        }

        for (var i = 0; i < below.Count; i++)
        {
            columnSlots.Add(below[i]);
        }

        var count = columnSlots.Count;
        if (count == 0)
        {
            return;
        }

        var title = PanelChrome.TitleBarHeight;
        var space = Math.Max(0, column.Height - ((count - 1) * splitterWidth));
        var titleRows = 0;
        for (var i = 0; i < count; i++)
        {
            if (columnSlots[i].Model is { IsMinimized: true })
            {
                titleRows++;
            }
        }

        var available = Math.Max(0, space - (titleRows * title));
        DistributeColumnHeights(count, filesChrome is not null, available, title);

        var y = column.Y;
        for (var i = 0; i < count; i++)
        {
            var height = Math.Max(0, columnHeights[i]);
            Place(columnSlots[i], new Rect(column.X, y, column.Width, height));
            y += height;
            if (i == count - 1)
            {
                break;
            }

            y = PlaceColumnGap(column, i, y, filesChrome is not null, splitterWidth);
        }
    }

    /// <summary>
    /// Fills <see cref="columnHeights"/> for the column's slots. The files slot takes the persisted ratio of the column, the
    /// panels below it divide the rest by the heights they were last dragged to (a panel never dragged takes the mean of the
    /// dragged ones), and a minimized slot keeps just its title bar, so the space it gives up flows to the others.
    /// </summary>
    private void DistributeColumnHeights(int count, bool hasFiles, double available, double title)
    {
        if (count > PanelStackSolver.MaxSlots)
        {
            for (var i = 0; i < count; i++)
            {
                columnHeights[i] = available / count;
            }

            return;
        }

        var ratio = Layout.InspectorSplitRatio;
        if (!double.IsFinite(ratio) || ratio <= 0 || ratio >= 1)
        {
            ratio = WorkbenchMetrics.DefaultInspectorSplitRatio;
        }

        var firstBelow = hasFiles ? 1 : 0;
        var baseline = 1.0;
        var storedSum = 0.0;
        var storedCount = 0;
        for (var i = firstBelow; i < count; i++)
        {
            var stored = columnSlots[i].Model is { } storedModel ? Layout.BelowPanelHeight(storedModel.Id) : 0;
            if (stored > 0)
            {
                storedSum += stored;
                storedCount++;
            }
        }

        if (storedCount > 0)
        {
            baseline = storedSum / storedCount;
        }

        var belowSum = 0.0;
        for (var i = firstBelow; i < count; i++)
        {
            var stored = columnSlots[i].Model is { } storedModel ? Layout.BelowPanelHeight(storedModel.Id) : 0;
            var weight = stored > 0 ? stored : baseline;
            columnWeights[i] = weight;
            belowSum += weight;
        }

        // The files weight is the ratio itself, so the below weights are scaled to fill what the ratio leaves: that keeps every
        // flexible slot on one scale, whether it was dragged or not.
        var flexible = 0;
        for (var i = 0; i < count; i++)
        {
            if (columnSlots[i].Model is { IsMinimized: true })
            {
                columnHeights[i] = Math.Min(title, available);
                continue;
            }

            columnFlexibleWeights[flexible] = i == 0 && hasFiles
                ? ratio
                : belowSum > 0 ? (1 - ratio) * (columnWeights[i] / belowSum) : 0;
            columnFlexible[flexible] = i;
            flexible++;
        }

        if (flexible == 0)
        {
            return;
        }

        PanelStackSolver.Distribute(
            available,
            columnFlexibleWeights.AsSpan(0, flexible),
            WorkbenchMetrics.PanelMinHeight,
            columnOut.AsSpan(0, flexible));
        for (var k = 0; k < flexible; k++)
        {
            columnHeights[columnFlexible[k]] = columnOut[k];
        }
    }

    /// <summary>
    /// Places the 1 px line under slot <paramref name="index"/> and returns the y the next slot starts at. The line under the
    /// files slot moves the persisted ratio; a line further down moves the pair of panels around it, which is what puts the
    /// tool group's own height in the user's hands.
    /// </summary>
    private double PlaceColumnGap(Rect column, int index, double y, bool hasFiles, double splitterWidth)
    {
        var hitTop = y + ((splitterWidth - WorkbenchMetrics.SplitterHitWidth) / 2);
        if (index == 0 && hasFiles)
        {
            // A minimized slot has no height to trade, so its own edge is not draggable either.
            if (columnSlots[0].Model is { IsMinimized: true })
            {
                return y + splitterWidth;
            }

            inspectorSplitter.TrackLength = column.Height;
            Place(inspectorSplitter, new Rect(column.X, hitTop, column.Width, WorkbenchMetrics.SplitterHitWidth));
            return y + splitterWidth;
        }

        var upper = index - (hasFiles ? 1 : 0);
        if (upper < 0 || upper + 1 >= below.Count || upper >= belowSplitters.Count)
        {
            return y + splitterWidth;
        }

        var upperChrome = below[upper];
        var lowerChrome = below[upper + 1];
        if (upperChrome.Model is not { } upperModel || lowerChrome.Model is not { } lowerModel)
        {
            return y + splitterWidth;
        }

        var splitter = belowSplitters[upper];
        if (upperModel.IsMinimized || lowerModel.IsMinimized)
        {
            return y + splitterWidth;
        }

        splitter.LowerPanelId = lowerModel.Id;
        splitter.UpperHeight = columnHeights[index];
        splitter.LowerHeight = columnHeights[index + 1];
        if (splitter.UpperPanelId != upperModel.Id)
        {
            // The id only follows the pair, so arranging never allocates a fresh automation id for an unchanged line.
            splitter.UpperPanelId = upperModel.Id;
            AutomationProperties.SetAutomationId(splitter, "panel-splitter-" + upperModel.Id);
        }

        Place(splitter, new Rect(column.X, hitTop, column.Width, WorkbenchMetrics.SplitterHitWidth));
        return y + splitterWidth;
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
