using System.Collections.ObjectModel;
using System.ComponentModel;
using Forge.App.ViewModels;

namespace Forge.App.Workbench;

/// <summary>
/// The single owner of the workbench layout state (forge-app spec, "工作台布局"). No Avalonia types: controls render it
/// and call these operations, view models open and close tabs through it, and nothing else edits the collections.
/// UI-thread only. Every state change that the snapshot records raises <see cref="LayoutChanged"/>.
/// </summary>
internal sealed class WorkbenchLayout : ObservableObject
{
    private const int RegionCount = 3;

    private static readonly string InspectorToolsTitle =
        Strings.TodoTitle + " | " + Strings.ReviewTitle + " | " + Strings.SpecTreeTitle;

    private readonly RegionModel project;
    private readonly RegionModel main;
    private readonly RegionModel inspector;

    private readonly PanelModel projectPanel;
    private readonly PanelModel filesPanel;
    private readonly TabGroupModel mainTabs;
    private readonly TabGroupModel bottomTabs;
    private readonly TabGroupModel previewTabs;
    private readonly TabGroupModel inspectorTools;
    private readonly TabGroupModel[] groups;

    private readonly double[] dragStartWidths = new double[RegionCount];
    private readonly double[] dragStartMins = new double[RegionCount];
    private readonly double[] dragOutWidths = new double[RegionCount];
    private readonly bool[] dragOutCollapsed = new bool[RegionCount];
    private readonly bool[] dragCollapsed = new bool[RegionCount];
    private readonly RegionModel?[] dragRegions = new RegionModel?[RegionCount];

    private double windowWidth;
    private double previewWidth = WorkbenchMetrics.DefaultPreviewWidth;

    /// <summary>True while <see cref="PreviewTabs"/> is docked-visible, so the Inspector's width includes the preview column.</summary>
    private bool inspectorHasPreviewColumn;

    private double inspectorSplitRatio = WorkbenchMetrics.DefaultInspectorSplitRatio;
    private double bottomHeight = WorkbenchMetrics.DefaultBottomHeight;
    private bool isBottomCollapsed;
    private bool bottomHasContent;
    private bool isDragging;
    private RegionRole? openDrawer;
    private bool isPreviewCoveringMain;
    private int dragSplitter = -1;
    private int dragMain = -1;
    private int dragCount;

    private event EventHandler<TabModel>? tabClosed;
    private event EventHandler<TabModel>? activeTabChanged;
    private event EventHandler? layoutChanged;

    /// <summary>Builds the default layout: Project | Main | Inspector, all docked, preview closed, bottom group empty.</summary>
    /// <param name="projectContent">View model of the project panel.</param>
    /// <param name="filesContent">View model of the files panel.</param>
    /// <param name="todo">View model of the TODO tab of the inspector tools group.</param>
    /// <param name="review">View model of the review tab.</param>
    /// <param name="specs">View model of the spec tree tab.</param>
    public WorkbenchLayout(object projectContent, object filesContent, object todo, object review, object specs)
    {
        project = new RegionModel(RegionRole.Project, WorkbenchMetrics.DefaultProjectWidth);
        main = new RegionModel(RegionRole.Main, 0);
        inspector = new RegionModel(RegionRole.Inspector, WorkbenchMetrics.DefaultInspectorWidth);

        projectPanel = new PanelModel(WorkbenchIds.ProjectPanel, RegionRole.Project, Strings.SectionProjects)
        {
            Content = projectContent,
            CanPin = true,
        };

        filesPanel = new PanelModel(WorkbenchIds.FilesPanel, RegionRole.Inspector, Strings.FilesName)
        {
            Content = filesContent,
            CanPin = true,
            CanFloat = true,
        };

        mainTabs = new TabGroupModel(WorkbenchIds.MainTabs, RegionRole.Main, Strings.DocumentsTitle);

        bottomTabs = new TabGroupModel(WorkbenchIds.BottomTabs, RegionRole.Main, Strings.SubAgentsTitle)
        {
            CanPin = true,
        };

        previewTabs = new TabGroupModel(WorkbenchIds.PreviewTabs, RegionRole.Inspector, Strings.PreviewTitle)
        {
            CanPin = true,
        };

        inspectorTools = new TabGroupModel(WorkbenchIds.InspectorTools, RegionRole.Inspector, InspectorToolsTitle)
        {
            CanPin = true,
        };

        inspectorTools.Tabs.Add(CreateTab(WorkbenchIds.TodoTab, TabKind.Todo, todo, Strings.TodoTitle, closable: false, inspectorTools));
        inspectorTools.Tabs.Add(CreateTab(WorkbenchIds.ReviewTab, TabKind.Review, review, Strings.ReviewTitle, closable: false, inspectorTools));
        inspectorTools.Tabs.Add(CreateTab(WorkbenchIds.SpecsTab, TabKind.Specs, specs, Strings.SpecTreeTitle, closable: false, inspectorTools));
        inspectorTools.ActiveTab = inspectorTools.Tabs[0];

        project.Panels.Add(projectPanel);
        main.Panels.Add(mainTabs);
        main.Panels.Add(bottomTabs);
        inspector.Panels.Add(previewTabs);
        inspector.Panels.Add(filesPanel);
        inspector.Panels.Add(inspectorTools);

        Order.Add(project);
        Order.Add(main);
        Order.Add(inspector);

        groups = [mainTabs, bottomTabs, previewTabs, inspectorTools];
        foreach (var group in groups)
        {
            group.PropertyChanged += OnGroupPropertyChanged;
        }

        RecomposeVisibility();
    }

    // ---------------------------------------------------------------- regions

    /// <summary>The regions in visual order, left to right. Swapping reorders this collection (Move, never Reset).</summary>
    public ObservableCollection<RegionModel> Order { get; } = [];

    public RegionModel Project => project;

    public RegionModel Main => main;

    public RegionModel Inspector => inspector;

    public RegionModel GetRegion(RegionRole role) => role switch
    {
        RegionRole.Project => project,
        RegionRole.Main => main,
        _ => inspector,
    };

    public int IndexOf(RegionRole role)
    {
        var order = Order;
        for (int i = 0; i < order.Count; i++)
        {
            if (order[i].Role == role)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The edge a side region's auto-hide strip and drawer use: the side of Main it currently sits on.</summary>
    public RegionEdge EdgeOf(RegionRole role) => role == RegionRole.Main
        ? throw new ArgumentException("Main has no auto-hide edge.", nameof(role))
        : IndexOf(role) < IndexOf(RegionRole.Main) ? RegionEdge.Left : RegionEdge.Right;

    /// <summary>Swaps two regions; widths travel with the regions. Then refits. A swap involving an auto-hidden region is refused (returns false).</summary>
    public bool SwapRegions(RegionRole a, RegionRole b)
    {
        if (a == b)
        {
            return false;
        }

        RegionModel first = GetRegion(a);
        RegionModel second = GetRegion(b);
        if (first.IsAutoHidden || second.IsAutoHidden)
        {
            return false;
        }

        int from = IndexOf(a);
        int to = IndexOf(b);
        if (from < 0 || to < 0)
        {
            return false;
        }

        Order.Move(from, to);
        Order.Move(from < to ? to - 1 : to + 1, from);

        OnPropertyChanged(nameof(IsPreviewLeftOfFiles));
        RecomposeVisibility();
        Fit();
        OnLayoutChanged();
        return true;
    }

    // ---------------------------------------------------------------- widths

    /// <summary>The width the workbench host was last arranged at.</summary>
    public double WindowWidth => windowWidth;

    /// <summary>Called by the host on every arrange with a new width; runs <see cref="RegionWidthSolver.Fit"/> (hide order Inspector, then Project).</summary>
    public void SetWindowWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            return;
        }

        if (Set(ref windowWidth, width))
        {
            Fit();
        }
    }

    /// <summary>Captures the start widths for a drag of the splitter between docked regions <paramref name="splitterIndex"/> and +1.</summary>
    public void BeginSplitterDrag(int splitterIndex)
    {
        dragCount = 0;
        dragMain = -1;
        dragSplitter = -1;
        isDragging = false;

        var order = Order;
        for (int i = 0; i < order.Count && dragCount < RegionCount; i++)
        {
            RegionModel region = order[i];
            if (region.IsAutoHidden)
            {
                continue;
            }

            dragRegions[dragCount] = region;
            dragStartWidths[dragCount] = region.Width;
            dragStartMins[dragCount] = MinWidthOf(region.Role);
            dragCollapsed[dragCount] = false;
            if (region.Role == RegionRole.Main)
            {
                dragMain = dragCount;
            }

            dragCount++;
        }

        dragSplitter = ResolveSplitterIndex(splitterIndex);
        isDragging = dragMain >= 0 && dragSplitter >= 0 && dragSplitter < dragCount - 1;
    }

    /// <summary>Applies <see cref="RegionWidthSolver.Drag"/> for the pointer offset since drag start; a collapse auto-hides the region.</summary>
    public void UpdateSplitterDrag(double delta)
    {
        if (!isDragging)
        {
            return;
        }

        int count = dragCount;
        RegionWidthSolver.Drag(
            dragStartWidths.AsSpan(0, count),
            dragStartMins.AsSpan(0, count),
            dragMain,
            dragSplitter,
            delta,
            dragOutWidths.AsSpan(0, count),
            dragOutCollapsed.AsSpan(0, count));

        bool anyCollapsed = false;
        for (int k = 0; k < count; k++)
        {
            RegionModel region = dragRegions[k]!;
            region.Width = dragOutWidths[k];

            bool collapse = dragOutCollapsed[k];
            if (collapse != dragCollapsed[k])
            {
                dragCollapsed[k] = collapse;
                region.IsAutoHidden = collapse;
            }

            anyCollapsed |= collapse;
        }

        if (anyCollapsed)
        {
            Fit();
        }
    }

    /// <summary>Ends the drag and raises <see cref="LayoutChanged"/> once (not on every move).</summary>
    public void EndSplitterDrag()
    {
        if (!isDragging)
        {
            return;
        }

        isDragging = false;
        dragSplitter = -1;
        Fit();
        OnLayoutChanged();
    }

    /// <summary>Effective minimum width of a region; the Inspector's grows by the preview column while the preview group is open in it.</summary>
    public double MinWidthOf(RegionRole role) => role switch
    {
        RegionRole.Project => WorkbenchMetrics.SideMinWidth,
        RegionRole.Main => WorkbenchMetrics.MainMinWidth,
        _ => WorkbenchMetrics.SideMinWidth
            + (previewTabs.IsVisible ? WorkbenchMetrics.PreviewMinWidth + WorkbenchMetrics.PanelSplitterWidth : 0),
    };

    // ---------------------------------------------------------------- auto-hide and drawers

    /// <summary>Pins (false) or auto-hides (true) a side region. Pinning refits with that region protected.</summary>
    public void SetAutoHidden(RegionRole role, bool autoHidden)
    {
        if (role == RegionRole.Main)
        {
            return;
        }

        RegionModel region = GetRegion(role);
        if (region.IsAutoHidden == autoHidden)
        {
            return;
        }

        region.IsAutoHidden = autoHidden;
        if (autoHidden)
        {
            if (OpenDrawer == role)
            {
                OpenDrawer = null;
            }

            Fit();
        }
        else
        {
            if (OpenDrawer == role)
            {
                OpenDrawer = null;
            }

            Fit(IndexOf(role));
        }

        OnLayoutChanged();
    }

    /// <summary>The auto-hidden region whose drawer is open over Main, or null.</summary>
    public RegionRole? OpenDrawer
    {
        get => openDrawer;
        private set => Set(ref openDrawer, value);
    }

    public void ToggleDrawer(RegionRole role)
    {
        if (role == RegionRole.Main || !GetRegion(role).IsAutoHidden)
        {
            return;
        }

        OpenDrawer = OpenDrawer == role ? null : role;
    }

    public void CloseDrawer() => OpenDrawer = null;

    // ---------------------------------------------------------------- panels

    public PanelModel ProjectPanel => projectPanel;

    public PanelModel FilesPanel => filesPanel;

    /// <summary>Session tabs.</summary>
    public TabGroupModel MainTabs => mainTabs;

    /// <summary>Sub-agent tabs (later also terminals); hidden while it has neither a docked tab nor a kept-visible tool.</summary>
    public TabGroupModel BottomTabs => bottomTabs;

    /// <summary>
    /// True while the bottom group has something to show: a docked tab, or a title-bar tool that keeps it alive (the
    /// finished sub-agent menu). It stays true while the group is collapsed, because the collapsed strip shows its tabs.
    /// </summary>
    public bool BottomHasContent => bottomHasContent;

    /// <summary>File previews and the fixed changes tab; open only while it has a tab.</summary>
    public TabGroupModel PreviewTabs => previewTabs;

    /// <summary>TODO | review | spec tree.</summary>
    public TabGroupModel InspectorTools => inspectorTools;

    /// <summary>Maximizes <paramref name="panel"/> within its region, or restores when it already is. Ignored when <see cref="PanelModel.CanMaximize"/> is false.</summary>
    public void ToggleMaximize(PanelModel panel)
    {
        if (panel is null)
        {
            return;
        }

        RegionModel region = GetRegion(panel.Region);
        if (region.MaximizedPanel == panel)
        {
            region.MaximizedPanel = null;
            return;
        }

        if (!panel.CanMaximize || !panel.IsVisible)
        {
            return;
        }

        region.MaximizedPanel = panel;
    }

    /// <summary>True when the preview group sits left of the files column (Inspector right of Main), false when right of it.</summary>
    public bool IsPreviewLeftOfFiles => IndexOf(RegionRole.Inspector) > IndexOf(RegionRole.Main);

    /// <summary>Width of the preview column; opening the preview borrows it from Main (Main's minimum still holds).</summary>
    public double PreviewWidth
    {
        get => previewWidth;
        set
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            bool docked = inspectorHasPreviewColumn;
            double maximum = docked
                ? inspector.Width - WorkbenchMetrics.SideMinWidth - WorkbenchMetrics.PanelSplitterWidth
                : double.PositiveInfinity;
            double clamped = Math.Clamp(value, WorkbenchMetrics.PreviewMinWidth, Math.Max(WorkbenchMetrics.PreviewMinWidth, maximum));
            if (clamped == previewWidth)
            {
                return;
            }

            double delta = clamped - previewWidth;
            previewWidth = clamped;
            if (docked)
            {
                inspector.Width += delta;
                Fit(IndexOf(RegionRole.Inspector));
            }

            OnPropertyChanged();
            OnLayoutChanged();
        }
    }

    /// <summary>Share of the files panel in the Inspector's right column (files above, tools below), clamped by <see cref="WorkbenchMetrics.PanelMinHeight"/> in the view.</summary>
    public double InspectorSplitRatio
    {
        get => inspectorSplitRatio;
        set
        {
            if (!double.IsFinite(value) || value <= 0 || value >= 1)
            {
                return;
            }

            if (Set(ref inspectorSplitRatio, value))
            {
                OnLayoutChanged();
            }
        }
    }

    /// <summary>"铺满主区": the preview group spreads over the whole Main region. Not persisted.</summary>
    public bool IsPreviewCoveringMain
    {
        get => isPreviewCoveringMain;
        private set => Set(ref isPreviewCoveringMain, value);
    }

    public void TogglePreviewCoverMain()
    {
        if (!previewTabs.IsVisible)
        {
            return;
        }

        IsPreviewCoveringMain = !IsPreviewCoveringMain;
    }

    // ---------------------------------------------------------------- bottom group

    /// <summary>Height of the bottom group when shown; kept while it is collapsed or empty so it pops up at the same height.</summary>
    public double BottomHeight
    {
        get => bottomHeight;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                return;
            }

            if (Set(ref bottomHeight, value))
            {
                OnLayoutChanged();
            }
        }
    }

    /// <summary>Collapsed by its pin button to a strip on Main's bottom edge.</summary>
    public bool IsBottomCollapsed
    {
        get => isBottomCollapsed;
        set
        {
            if (!Set(ref isBottomCollapsed, value))
            {
                return;
            }

            RecomposeVisibility();
            OnLayoutChanged();
        }
    }

    // ---------------------------------------------------------------- tabs

    /// <summary>
    /// Opens (or, for an id already present, re-binds content/title and activates) a tab in <paramref name="group"/>.
    /// Opening the first preview tab opens the preview group and borrows its width from Main; <paramref name="index"/> null appends.
    /// </summary>
    public TabModel OpenTab(TabGroupModel group, string id, TabKind kind, object content, string title, bool activate = true, bool closable = true, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(id);

        TabModel? existing = FindTabIn(group, id);
        if (existing is not null)
        {
            existing.Content = content;
            existing.Title = title;
            if (activate)
            {
                ActivateTab(existing);
            }

            OnLayoutChanged();
            return existing;
        }

        var tab = CreateTab(id, kind, content, title, closable, group);
        if (index is int position)
        {
            group.Tabs.Insert(Math.Clamp(position, 0, group.Tabs.Count), tab);
        }
        else
        {
            group.Tabs.Add(tab);
        }

        RecomposeVisibility();

        if (activate || group.ActiveTab is null)
        {
            ActivateTab(tab);
        }

        OnLayoutChanged();
        return tab;
    }

    public TabModel? FindTab(string id)
    {
        var all = groups;
        for (int g = 0; g < all.Length; g++)
        {
            TabModel? found = FindTabIn(all[g], id);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    public void ActivateTab(TabModel tab)
    {
        if (tab is null || tab.IsFloating || tab.Group is not { } group || group.ActiveTab == tab)
        {
            return;
        }

        if (group.Tabs.IndexOf(tab) < 0)
        {
            return;
        }

        group.ActiveTab = tab;
    }

    /// <summary>
    /// Removes the tab (closing its floating window if any) and raises <see cref="TabClosed"/>. Closing the last preview tab
    /// closes the preview group; its borrowed width returns to Main once the preview group stops being docked-visible.
    /// </summary>
    public void CloseTab(TabModel tab)
    {
        if (tab is null || tab.Group is not { } group)
        {
            return;
        }

        int index = group.Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        bool wasActive = group.ActiveTab == tab;
        group.Tabs.RemoveAt(index);
        RemoveFloating(tab, null);

        if (wasActive)
        {
            group.ActiveTab = NeighbourTab(group, index);
        }

        tab.Group = null;

        RecomposeVisibility();

        tabClosed?.Invoke(this, tab);
        OnLayoutChanged();
    }

    /// <summary>Reorders within one group only; tabs never change group.</summary>
    public void MoveTab(TabGroupModel group, int fromIndex, int toIndex)
    {
        if (group is null)
        {
            return;
        }

        var tabs = group.Tabs;
        if (tabs.Count == 0)
        {
            return;
        }

        int from = Math.Clamp(fromIndex, 0, tabs.Count - 1);
        int to = Math.Clamp(toIndex, 0, tabs.Count - 1);
        if (from == to)
        {
            return;
        }

        tabs.Move(from, to);
        OnLayoutChanged();
    }

    // ---------------------------------------------------------------- floating

    public ObservableCollection<FloatingItem> Floating { get; } = [];

    /// <summary>Moves a tab to a window of its own; the group activates its neighbour. <paramref name="geometry"/> null uses the default size.</summary>
    public FloatingItem FloatTab(TabModel tab, FloatGeometry? geometry = null)
    {
        ArgumentNullException.ThrowIfNull(tab);

        for (int i = 0; i < Floating.Count; i++)
        {
            if (Floating[i].Tab == tab)
            {
                return Floating[i];
            }
        }

        var item = new FloatingItem(
            tab,
            geometry ?? FloatGeometry.Default(WorkbenchMetrics.FloatingTabWidth, WorkbenchMetrics.FloatingTabHeight));
        Subscribe(item);
        Floating.Add(item);
        tab.IsFloating = true;

        if (tab.Group is { } group && group.ActiveTab == tab)
        {
            group.ActiveTab = NeighbourTab(group, group.Tabs.IndexOf(tab));
        }

        RecomposeVisibility();
        OnLayoutChanged();
        return item;
    }

    /// <summary>Moves the files panel, with the preview group, to a window of its own.</summary>
    public FloatingItem FloatFilesPanel(FloatGeometry? geometry = null)
    {
        for (int i = 0; i < Floating.Count; i++)
        {
            if (Floating[i].FilesPanel == filesPanel)
            {
                return Floating[i];
            }
        }

        var item = new FloatingItem(
            filesPanel,
            previewTabs,
            geometry ?? FloatGeometry.Default(WorkbenchMetrics.FloatingFilesWidth, WorkbenchMetrics.FloatingFilesHeight));
        Subscribe(item);
        Floating.Add(item);
        filesPanel.IsFloating = true;
        previewTabs.IsFloating = true;

        RecomposeVisibility();
        Fit();
        OnLayoutChanged();
        return item;
    }

    /// <summary>Returns a floating item to its slot ("停靠" button or the window closing). During shutdown it is kept floating so the snapshot records it.</summary>
    public void Dock(FloatingItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (IsShuttingDown || Floating.IndexOf(item) < 0)
        {
            return;
        }

        Unsubscribe(item);
        Floating.Remove(item);

        if (item.Tab is { } tab)
        {
            tab.IsFloating = false;
            if (tab.Group is { } group && group.Tabs.IndexOf(tab) >= 0)
            {
                group.ActiveTab = tab;
            }

            RecomposeVisibility();
            GrowInspectorToMinimum();
            OnLayoutChanged();
            return;
        }

        filesPanel.IsFloating = false;
        if (item.PreviewTabs is { } preview)
        {
            preview.IsFloating = false;
        }

        RecomposeVisibility();
        GrowInspectorToMinimum();
        OnLayoutChanged();
    }

    /// <summary>Set by the window before it closes; from then on closing floating windows does not dock them.</summary>
    public bool IsShuttingDown { get; set; }

    // ---------------------------------------------------------------- persistence and events

    public LayoutSnapshot Capture()
    {
        var snapshot = new LayoutSnapshot { Version = LayoutSnapshot.CurrentVersion };

        var order = Order;
        for (int i = 0; i < order.Count; i++)
        {
            RegionModel region = order[i];
            string role = RoleName(region.Role);
            snapshot.Order.Add(role);
            snapshot.Regions.Add(new RegionSnapshot
            {
                Role = role,
                Width = region.Role == RegionRole.Inspector && previewTabs.IsVisible
                    ? Math.Max(WorkbenchMetrics.SideMinWidth, region.Width - previewWidth)
                    : region.Width,
                AutoHidden = region.IsAutoHidden,
            });
        }

        snapshot.PreviewWidth = previewWidth;
        snapshot.InspectorSplitRatio = inspectorSplitRatio;
        snapshot.BottomHeight = bottomHeight;
        snapshot.BottomCollapsed = isBottomCollapsed;

        var tools = inspectorTools.Tabs;
        for (int i = 0; i < tools.Count; i++)
        {
            snapshot.ToolsOrder.Add(tools[i].Id);
        }

        snapshot.ToolsActive = inspectorTools.ActiveTab?.Id;

        for (int i = 0; i < Floating.Count; i++)
        {
            FloatingItem item = Floating[i];
            FloatGeometry geometry = item.Geometry;
            snapshot.Floating.Add(new FloatingSnapshot
            {
                Id = item.Id,
                X = Placed(geometry.X),
                Y = Placed(geometry.Y),
                Width = geometry.Width,
                Height = geometry.Height,
                IsMaximized = geometry.IsMaximized,
            });
        }

        return snapshot;
    }

    /// <summary>Applies the region part of a v3 snapshot; unknown ids are dropped and missing parts keep their default.</summary>
    public void Apply(LayoutSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ApplyOrder(snapshot.Order);

        if (IsLength(snapshot.PreviewWidth))
        {
            previewWidth = Math.Max(WorkbenchMetrics.PreviewMinWidth, snapshot.PreviewWidth);
        }

        if (IsLength(snapshot.InspectorSplitRatio) && snapshot.InspectorSplitRatio < 1)
        {
            inspectorSplitRatio = snapshot.InspectorSplitRatio;
        }

        if (IsLength(snapshot.BottomHeight))
        {
            bottomHeight = snapshot.BottomHeight;
        }

        isBottomCollapsed = snapshot.BottomCollapsed;

        ApplyRegions(snapshot.Regions);
        ApplyTools(snapshot.ToolsOrder, snapshot.ToolsActive);

        OnPropertyChanged(nameof(PreviewWidth));
        OnPropertyChanged(nameof(InspectorSplitRatio));
        OnPropertyChanged(nameof(BottomHeight));
        OnPropertyChanged(nameof(IsBottomCollapsed));
        OnPropertyChanged(nameof(IsPreviewLeftOfFiles));

        RecomposeVisibility();
        if (windowWidth > 0)
        {
            Fit();
        }

        OnLayoutChanged();
    }

    /// <summary>Re-floats the windows of the snapshot whose tabs exist by now (call after the open tabs are restored).</summary>
    public void ApplyFloating(LayoutSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Floating is null)
        {
            return;
        }

        for (int i = 0; i < snapshot.Floating.Count; i++)
        {
            FloatingSnapshot entry = snapshot.Floating[i];
            if (entry is null || string.IsNullOrEmpty(entry.Id))
            {
                continue;
            }

            if (entry.Id == WorkbenchIds.FilesPanel)
            {
                FloatFilesPanel(GeometryOf(entry, WorkbenchMetrics.FloatingFilesWidth, WorkbenchMetrics.FloatingFilesHeight));
                continue;
            }

            TabModel? tab = FindTab(entry.Id);
            if (tab is null || tab.IsFloating)
            {
                continue;
            }

            FloatTab(tab, GeometryOf(entry, WorkbenchMetrics.FloatingTabWidth, WorkbenchMetrics.FloatingTabHeight));
        }
    }

    public event EventHandler<TabModel>? TabClosed
    {
        add => tabClosed += value;
        remove => tabClosed -= value;
    }

    /// <summary>Raised when any group's active tab changes; the view model follows the main group to switch sessions.</summary>
    public event EventHandler<TabModel>? ActiveTabChanged
    {
        add => activeTabChanged += value;
        remove => activeTabChanged -= value;
    }

    /// <summary>Raised after a change the snapshot records (debounced by the caller).</summary>
    public event EventHandler? LayoutChanged
    {
        add => layoutChanged += value;
        remove => layoutChanged -= value;
    }

    // ---------------------------------------------------------------- helpers

    private static TabModel CreateTab(string id, TabKind kind, object content, string title, bool closable, TabGroupModel group) =>
        new(id, kind, content, title)
        {
            IsClosable = closable,
            Group = group,
        };

    private static TabModel? FindTabIn(TabGroupModel group, string id)
    {
        var tabs = group.Tabs;
        for (int i = 0; i < tabs.Count; i++)
        {
            if (string.Equals(tabs[i].Id, id, StringComparison.Ordinal))
            {
                return tabs[i];
            }
        }

        return null;
    }

    private static TabModel? NeighbourTab(TabGroupModel group, int index)
    {
        var tabs = group.Tabs;
        for (int i = Math.Max(0, index); i < tabs.Count; i++)
        {
            if (!tabs[i].IsFloating)
            {
                return tabs[i];
            }
        }

        for (int i = Math.Min(index - 1, tabs.Count - 1); i >= 0; i--)
        {
            if (!tabs[i].IsFloating)
            {
                return tabs[i];
            }
        }

        return null;
    }

    private static bool IsLength(double value) => double.IsFinite(value) && value > 0;

    private static double? Placed(double value) => double.IsFinite(value) ? value : null;

    private static string RoleName(RegionRole role) => role switch
    {
        RegionRole.Project => nameof(RegionRole.Project),
        RegionRole.Main => nameof(RegionRole.Main),
        _ => nameof(RegionRole.Inspector),
    };

    private static bool TryParseRole(string? name, out RegionRole role)
    {
        if (string.Equals(name, nameof(RegionRole.Project), StringComparison.Ordinal))
        {
            role = RegionRole.Project;
            return true;
        }

        if (string.Equals(name, nameof(RegionRole.Main), StringComparison.Ordinal))
        {
            role = RegionRole.Main;
            return true;
        }

        if (string.Equals(name, nameof(RegionRole.Inspector), StringComparison.Ordinal))
        {
            role = RegionRole.Inspector;
            return true;
        }

        role = default;
        return false;
    }

    private static FloatGeometry GeometryOf(FloatingSnapshot entry, double defaultWidth, double defaultHeight) =>
        new(
            entry.X is { } x && double.IsFinite(x) ? x : double.NaN,
            entry.Y is { } y && double.IsFinite(y) ? y : double.NaN,
            IsLength(entry.Width) ? entry.Width : defaultWidth,
            IsLength(entry.Height) ? entry.Height : defaultHeight,
            entry.IsMaximized);

    private int ResolveSplitterIndex(int index)
    {
        if (dragCount < 2)
        {
            return -1;
        }

        int dockedUpTo = 0;
        var order = Order;
        for (int i = 0; i <= index && i < order.Count; i++)
        {
            if (!order[i].IsAutoHidden)
            {
                dockedUpTo++;
            }
        }

        int fromVisual = dockedUpTo - 1;
        if (fromVisual >= 0 && fromVisual < dragCount - 1)
        {
            return fromVisual;
        }

        return index >= 0 && index < dragCount - 1 ? index : -1;
    }

    private void Fit(int protectedIndex = -1)
    {
        if (!(windowWidth > 0) || !double.IsFinite(windowWidth))
        {
            return;
        }

        Span<double> widths = stackalloc double[RegionCount];
        Span<double> minWidths = stackalloc double[RegionCount];
        Span<bool> hidden = stackalloc bool[RegionCount];
        Span<RegionEdge> edges = stackalloc RegionEdge[RegionCount];
        Span<int> shrinkOrder = stackalloc int[2];
        Span<double> outWidths = stackalloc double[RegionCount];
        Span<bool> outHidden = stackalloc bool[RegionCount];

        var order = Order;
        for (int i = 0; i < order.Count && i < RegionCount; i++)
        {
            RegionModel region = order[i];
            widths[i] = region.Width;
            minWidths[i] = MinWidthOf(region.Role);
            hidden[i] = region.IsAutoHidden;
            edges[i] = region.Role == RegionRole.Main ? RegionEdge.Left : EdgeOf(region.Role);
        }

        shrinkOrder[0] = IndexOf(RegionRole.Inspector);
        shrinkOrder[1] = IndexOf(RegionRole.Project);

        RegionWidthSolver.Fit(
            widths,
            minWidths,
            hidden,
            edges,
            IndexOf(RegionRole.Main),
            shrinkOrder,
            protectedIndex,
            windowWidth,
            outWidths,
            outHidden);

        for (int i = 0; i < order.Count && i < RegionCount; i++)
        {
            order[i].Width = outWidths[i];
            order[i].IsAutoHidden = outHidden[i];
        }

        if (OpenDrawer is RegionRole drawer && !GetRegion(drawer).IsAutoHidden)
        {
            OpenDrawer = null;
        }

        RecomposeVisibility();
    }

    private void GrowInspectorToMinimum()
    {
        double minimum = MinWidthOf(RegionRole.Inspector);
        if (inspector.Width >= minimum)
        {
            return;
        }

        inspector.Width = minimum;
        Fit(IndexOf(RegionRole.Inspector));
    }

    /// <summary>
    /// Adds the preview column to the Inspector's width when the preview group becomes docked-visible, or gives it back
    /// to Main when it stops being docked-visible. The single place the borrowed width is added or removed, so floating
    /// the files panel, floating the last preview tab, or closing it all return the same width.
    /// </summary>
    private void AdjustInspectorForPreview(bool include)
    {
        if (include == inspectorHasPreviewColumn)
        {
            return;
        }

        inspectorHasPreviewColumn = include;
        if (include)
        {
            inspector.Width += previewWidth;
            Fit(IndexOf(RegionRole.Inspector));
        }
        else
        {
            inspector.Width = Math.Max(WorkbenchMetrics.SideMinWidth, inspector.Width - previewWidth);
            Fit();
        }
    }

    private void RecomposeVisibility()
    {
        filesPanel.IsVisible = !filesPanel.IsFloating;

        bool previewVisible = !filesPanel.IsFloating && previewTabs.HasDockedTab;
        bool wasPreviewVisible = previewTabs.IsVisible;
        previewTabs.IsVisible = previewVisible;
        if (previewVisible != wasPreviewVisible)
        {
            AdjustInspectorForPreview(previewVisible);
        }

        bool hasContent = bottomTabs.HasDockedTab || bottomTabs.KeepsVisibleWhenEmpty;
        bottomTabs.IsVisible = hasContent && !isBottomCollapsed;
        if (hasContent != bottomHasContent)
        {
            bottomHasContent = hasContent;
            OnPropertyChanged(nameof(BottomHasContent));
        }

        UpdateCanMaximize(project);
        UpdateCanMaximize(main);
        UpdateCanMaximize(inspector);

        if (!previewTabs.IsVisible)
        {
            IsPreviewCoveringMain = false;
        }
    }

    private static void UpdateCanMaximize(RegionModel region)
    {
        int visible = 0;
        var panels = region.Panels;
        for (int i = 0; i < panels.Count; i++)
        {
            if (panels[i].IsVisible)
            {
                visible++;
            }
        }

        bool canMaximize = visible >= 2;
        for (int i = 0; i < panels.Count; i++)
        {
            PanelModel panel = panels[i];
            panel.CanMaximize = canMaximize && panel.IsVisible;
        }

        if (region.MaximizedPanel is { } maximized && (!maximized.IsVisible || !canMaximize))
        {
            region.MaximizedPanel = null;
        }
    }

    private void Subscribe(FloatingItem item) => item.PropertyChanged += OnFloatingItemChanged;

    private void Unsubscribe(FloatingItem item) => item.PropertyChanged -= OnFloatingItemChanged;

    private void RemoveFloating(TabModel? tab, PanelModel? files)
    {
        for (int i = Floating.Count - 1; i >= 0; i--)
        {
            FloatingItem item = Floating[i];
            bool matches = tab is not null ? item.Tab == tab : item.FilesPanel == files;
            if (!matches)
            {
                continue;
            }

            Unsubscribe(item);
            Floating.RemoveAt(i);
        }
    }

    private void ApplyOrder(List<string> order)
    {
        if (order is null || order.Count != RegionCount)
        {
            return;
        }

        Span<bool> seen = stackalloc bool[RegionCount];
        for (int i = 0; i < order.Count; i++)
        {
            if (!TryParseRole(order[i], out RegionRole role) || seen[(int)role])
            {
                return;
            }

            seen[(int)role] = true;
        }

        for (int target = 0; target < order.Count; target++)
        {
            TryParseRole(order[target], out RegionRole role);
            int current = IndexOf(role);
            if (current >= 0 && current != target)
            {
                Order.Move(current, target);
            }
        }

        OnPropertyChanged(nameof(IsPreviewLeftOfFiles));
    }

    private void ApplyRegions(List<RegionSnapshot> regions)
    {
        if (regions is null)
        {
            return;
        }

        for (int i = 0; i < regions.Count; i++)
        {
            RegionSnapshot entry = regions[i];
            if (entry is null || !TryParseRole(entry.Role, out RegionRole role))
            {
                continue;
            }

            RegionModel region = GetRegion(role);
            if (IsLength(entry.Width))
            {
                region.Width = role == RegionRole.Inspector && previewTabs.IsVisible
                    ? entry.Width + previewWidth
                    : entry.Width;
            }

            if (role != RegionRole.Main)
            {
                region.IsAutoHidden = entry.AutoHidden;
            }
        }

        if (OpenDrawer is RegionRole drawer && !GetRegion(drawer).IsAutoHidden)
        {
            OpenDrawer = null;
        }
    }

    private void ApplyTools(List<string> order, string? active)
    {
        var tabs = inspectorTools.Tabs;
        if (order is { Count: > 0 })
        {
            var ordered = new List<TabModel>(tabs.Count);
            for (int i = 0; i < order.Count; i++)
            {
                TabModel? tab = FindTabIn(inspectorTools, order[i]);
                if (tab is not null && !ordered.Contains(tab))
                {
                    ordered.Add(tab);
                }
            }

            for (int i = 0; i < tabs.Count; i++)
            {
                if (!ordered.Contains(tabs[i]))
                {
                    ordered.Add(tabs[i]);
                }
            }

            for (int target = 0; target < ordered.Count; target++)
            {
                int current = tabs.IndexOf(ordered[target]);
                if (current >= 0 && current != target)
                {
                    tabs.Move(current, target);
                }
            }
        }

        if (active is not null && FindTabIn(inspectorTools, active) is { } activeTab)
        {
            inspectorTools.ActiveTab = activeTab;
        }
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TabGroupModel group)
        {
            return;
        }

        if (e.PropertyName == nameof(TabGroupModel.KeepsVisibleWhenEmpty))
        {
            RecomposeVisibility();
            return;
        }

        if (e.PropertyName != nameof(TabGroupModel.ActiveTab))
        {
            return;
        }

        if (group.ActiveTab is { IsFloating: false } tab)
        {
            activeTabChanged?.Invoke(this, tab);
        }

        OnLayoutChanged();
    }

    private void OnFloatingItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FloatingItem.Geometry))
        {
            OnLayoutChanged();
        }
    }

    private void OnLayoutChanged() => layoutChanged?.Invoke(this, EventArgs.Empty);
}
