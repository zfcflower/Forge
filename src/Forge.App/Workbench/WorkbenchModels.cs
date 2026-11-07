using System.Collections.ObjectModel;
using Forge.App.ViewModels;

namespace Forge.App.Workbench;

/// <summary>One of the three regions. Its width is the docked width; while auto-hidden it is the drawer width.</summary>
internal sealed class RegionModel(RegionRole role, double width) : ObservableObject
{
    private double width = width;
    private bool isAutoHidden;
    private PanelModel? maximizedPanel;

    public RegionRole Role { get; } = role;

    public double Width
    {
        get => width;
        set => Set(ref width, value);
    }

    /// <summary>Side regions only; Main is never auto-hidden.</summary>
    public bool IsAutoHidden
    {
        get => isAutoHidden;
        set => Set(ref isAutoHidden, value);
    }

    /// <summary>The panel filling the region while the others are set aside; null when nothing is maximized. Not persisted.</summary>
    public PanelModel? MaximizedPanel
    {
        get => maximizedPanel;
        set => Set(ref maximizedPanel, value);
    }

    /// <summary>The panels this region holds, top to bottom (Inspector: preview group first, then the files column).</summary>
    public ObservableCollection<PanelModel> Panels { get; } = [];
}

/// <summary>A titled panel: title on the left of its title bar, capability-driven icon buttons on the right.</summary>
internal class PanelModel(string id, RegionRole region, string title) : ObservableObject
{
    private string title = title;
    private bool isVisible = true;
    private bool canMaximize;
    private bool canMinimize;
    private bool isMinimized;
    private bool isFloating;

    public string Id { get; } = id;

    public RegionRole Region { get; } = region;

    public string Title
    {
        get => title;
        set => Set(ref title, value);
    }

    /// <summary>View model of a plain panel's body (project tree, files); tab groups show their active tab instead.</summary>
    public object? Content { get; init; }

    /// <summary>
    /// Extra title-bar items, left of the standard buttons, rendered by data template: a <see cref="PanelToolButton"/>
    /// becomes an icon button, any other view model (e.g. the finished sub-agent dropdown) uses its own template.
    /// </summary>
    public ObservableCollection<object> HeaderTools { get; } = [];

    /// <summary>False while the panel has nothing to show (empty bottom group, closed preview group) or floats away.</summary>
    public bool IsVisible
    {
        get => isVisible;
        set => Set(ref isVisible, value);
    }

    /// <summary>Kept by <see cref="WorkbenchLayout"/>: true while the region shows two or more panels.</summary>
    public bool CanMaximize
    {
        get => canMaximize;
        set => Set(ref canMaximize, value);
    }

    public bool CanMinimize
    {
        get => canMinimize;
        set => Set(ref canMinimize, value);
    }

    public bool IsMinimized
    {
        get => isMinimized;
        set => Set(ref isMinimized, value);
    }

    /// <summary>The pin / auto-hide button applies (panels of a side region and the bottom group).</summary>
    public bool CanPin { get; init; }

    /// <summary>The panel itself can move to a window of its own (only the files panel).</summary>
    public bool CanFloat { get; init; }

    public bool IsFloating
    {
        get => isFloating;
        set => Set(ref isFloating, value);
    }
}

/// <summary>An extra icon button of a panel title bar. <see cref="IconKey"/> names a geometry resource.</summary>
internal sealed class PanelToolButton(string id, string tooltip, string iconKey, System.Windows.Input.ICommand command)
{
    /// <summary>Also the AutomationId of the button.</summary>
    public string Id { get; } = id;

    public string Tooltip { get; } = tooltip;

    public string IconKey { get; } = iconKey;

    public System.Windows.Input.ICommand Command { get; } = command;
}

/// <summary>A panel whose title bar is a tab strip. Floating tabs stay in <see cref="Tabs"/> (so they return to their slot) but are not shown.</summary>
internal sealed class TabGroupModel(string id, RegionRole region, string title) : PanelModel(id, region, title)
{
    private TabModel? activeTab;
    private bool keepsVisibleWhenEmpty;

    public ObservableCollection<TabModel> Tabs { get; } = [];

    /// <summary>Never a floating tab; null when the group has no docked tab.</summary>
    public TabModel? ActiveTab
    {
        get => activeTab;
        set => Set(ref activeTab, value);
    }

    /// <summary>
    /// True while a title-bar tool of the group still has content (the finished sub-agent menu), so the group keeps its
    /// place in the layout without a docked tab. <see cref="WorkbenchLayout"/> recomputes the group's visibility when it
    /// changes; the flag itself is not persisted.
    /// </summary>
    public bool KeepsVisibleWhenEmpty
    {
        get => keepsVisibleWhenEmpty;
        set => Set(ref keepsVisibleWhenEmpty, value);
    }

    /// <summary>True while at least one tab is docked; a floating tab stays in <see cref="Tabs"/> but is not shown.</summary>
    public bool HasDockedTab
    {
        get
        {
            var tabs = Tabs;
            for (int i = 0; i < tabs.Count; i++)
            {
                if (!tabs[i].IsFloating)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>One tab. <see cref="Content"/> is the view model the data templates render, in the group or in a floating window.</summary>
internal sealed class TabModel(string id, TabKind kind, object content, string title) : ObservableObject
{
    private string title = title;
    private object content = content;
    private bool isFloating;

    public string Id { get; } = id;

    public TabKind Kind { get; } = kind;

    public object Content
    {
        get => content;
        set => Set(ref content, value);
    }

    public string Title
    {
        get => title;
        set => Set(ref title, value);
    }

    public bool IsClosable { get; init; } = true;

    public TabGroupModel? Group { get; internal set; }

    public bool IsFloating
    {
        get => isFloating;
        set => Set(ref isFloating, value);
    }
}

/// <summary>
/// Something living in a window of its own: one tab, or the files panel (which takes the preview group with it).
/// The view keeps one window per item and writes the geometry back while the window moves.
/// </summary>
internal sealed class FloatingItem : ObservableObject
{
    private FloatGeometry geometry;

    public FloatingItem(TabModel tab, FloatGeometry geometry)
    {
        Tab = tab;
        Id = tab.Id;
        this.geometry = geometry;
    }

    public FloatingItem(PanelModel filesPanel, TabGroupModel previewTabs, FloatGeometry geometry)
    {
        FilesPanel = filesPanel;
        PreviewTabs = previewTabs;
        Id = filesPanel.Id;
        this.geometry = geometry;
    }

    public string Id { get; }

    public TabModel? Tab { get; }

    public PanelModel? FilesPanel { get; }

    /// <summary>Set with <see cref="FilesPanel"/>: the preview group rides along, still on the files panel's Main-facing side.</summary>
    public TabGroupModel? PreviewTabs { get; }

    public FloatGeometry Geometry
    {
        get => geometry;
        set => Set(ref geometry, value);
    }
}
