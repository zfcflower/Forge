using System.Collections;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Workbench.Controls;

/// <summary>
/// A panel of the workbench: a 28 px title bar (title or tab strip on the left, capability-driven icon buttons on the
/// right — the bottom group's two are drawn in every state) above the body. The control holds no layout state — every button
/// calls back into <see cref="WorkbenchLayout"/>, and the title bar's blank space starts a region-swap drag on the host.
/// </summary>
internal sealed partial class PanelChrome : TemplatedControl
{
    /// <summary>Height of the title bar; presses below it belong to the body, not to a region-swap drag.</summary>
    public const double TitleBarHeight = 28;

    private PanelIconButton? coverButton;
    private PanelIconButton? pinButton;
    private PanelIconButton? minimizeButton;

    [GeneratedStyledProperty]
    public partial bool MinimizeVisible { get; set; }

    /// <summary>Text the body shows instead of a tab's content, for a group opened while it holds none.</summary>
    [GeneratedStyledProperty]
    public partial string? EmptyText { get; set; }

    [GeneratedStyledProperty]
    public partial bool ShowEmptyText { get; set; }

    [GeneratedStyledProperty]
    public partial Geometry? MinimizeIcon { get; set; }
    [GeneratedStyledProperty]
    public partial string? MinimizeTooltip { get; set; }
    [GeneratedStyledProperty]
    public partial string? MinimizeAutomationId { get; set; }
    private PanelIconButton? floatButton;
    private PanelIconButton? closeButton;
    private TabModel? watchedTab;
    private RegionModel? watchedRegion;
    private bool attached;
    private bool subscribed;
    private int attachedThread;

    static PanelChrome()
    {
        ModelProperty.Changed.AddClassHandler<PanelChrome>((chrome, _) => chrome.Resubscribe());
        LayoutProperty.Changed.AddClassHandler<PanelChrome>((chrome, _) => chrome.Resubscribe());
        SidebarProperty.Changed.AddClassHandler<PanelChrome>((chrome, e) =>
            chrome.PseudoClasses.Set(":sidebar", e.NewValue is true));
    }

    [GeneratedStyledProperty]
    public partial PanelModel? Model { get; set; }

    [GeneratedStyledProperty]
    public partial WorkbenchLayout? Layout { get; set; }

    /// <summary>True while this panel is drawn inside its region's drawer - not only the panel the drawer was opened on: a
    /// files drawer also holds the preview column beside it.</summary>
    private bool IsDrawerContent => Model is { } model && Layout?.DrawerPanel is { } drawer && drawer.Region == model.Region;

    [GeneratedStyledProperty]
    public partial string? HeaderTitle { get; set; }

    [GeneratedStyledProperty]
    public partial bool ShowTitle { get; set; }

    [GeneratedStyledProperty]
    public partial bool ShowTabStrip { get; set; }

    [GeneratedStyledProperty]
    public partial TabGroupModel? TabGroup { get; set; }

    [GeneratedStyledProperty]
    public partial IEnumerable? HeaderTools { get; set; }

    /// <summary>What the body shows: a plain panel's content, or the active tab's content for a tab group.</summary>
    [GeneratedStyledProperty]
    public partial object? Body { get; set; }

    /// <summary>True for the panels of a sidebar region (Project or Inspector); their body uses the sidebar surface.</summary>
    [GeneratedStyledProperty]
    public partial bool Sidebar { get; set; }

    [GeneratedStyledProperty]
    public partial bool CoverVisible { get; set; }

    [GeneratedStyledProperty]
    public partial Geometry? CoverIcon { get; set; }

    [GeneratedStyledProperty]
    public partial string? CoverTooltip { get; set; }

    [GeneratedStyledProperty]
    public partial string? CoverAutomationId { get; set; }

    [GeneratedStyledProperty]
    public partial bool PinVisible { get; set; }

    [GeneratedStyledProperty]
    public partial Geometry? PinIcon { get; set; }

    /// <summary>True while the pin is the solid one: the region is docked, so the click auto-hides it.</summary>
    [GeneratedStyledProperty]
    public partial string? PinTooltip { get; set; }

    [GeneratedStyledProperty]
    public partial string? PinAutomationId { get; set; }

    [GeneratedStyledProperty]
    public partial bool FloatVisible { get; set; }

    [GeneratedStyledProperty]
    public partial Geometry? FloatIcon { get; set; }

    [GeneratedStyledProperty]
    public partial string? FloatTooltip { get; set; }

    [GeneratedStyledProperty]
    public partial string? FloatAutomationId { get; set; }

    [GeneratedStyledProperty]
    public partial bool CloseVisible { get; set; }

    [GeneratedStyledProperty]
    public partial Geometry? CloseIcon { get; set; }

    [GeneratedStyledProperty]
    public partial string? CloseTooltip { get; set; }

    [GeneratedStyledProperty]
    public partial string? CloseAutomationId { get; set; }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        DetachButtons();
        coverButton = e.NameScope.Find<PanelIconButton>("PART_Cover");
        pinButton = e.NameScope.Find<PanelIconButton>("PART_Pin");
        minimizeButton = e.NameScope.Find<PanelIconButton>("PART_Minimize");
        floatButton = e.NameScope.Find<PanelIconButton>("PART_Float");
        closeButton = e.NameScope.Find<PanelIconButton>("PART_Close");
        if (coverButton is not null)
        {
            coverButton.Click += OnCoverClick;
        }

        if (pinButton is not null)
        {
            pinButton.Click += OnPinClick;
        }

        if (minimizeButton is not null)
        {
            minimizeButton.Click += OnMinimizeClick;
        }

        if (floatButton is not null)
        {
            floatButton.Click += OnFloatClick;
        }

        if (closeButton is not null)
        {
            closeButton.Click += OnCloseClick;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        attachedThread = Environment.CurrentManagedThreadId;
        Subscribe();
        RefreshModel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unsubscribe();
        attached = false;
        DetachButtons();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Model is not { } model || Layout is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || point.Position.Y > TitleBarHeight)
        {
            return;
        }

        if (Layout.GetRegion(model.Region).IsAutoHidden)
        {
            return;
        }

        this.FindAncestorOfType<WorkbenchHost>()?.BeginRegionSwapDrag(model.Region, e);
    }

    private void OnCoverClick(object? sender, RoutedEventArgs e) => Layout?.TogglePreviewCoverMain();

    private void OnPinClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || Layout is null)
        {
            return;
        }

        Layout.SetAutoHidden(model.Region, !Layout.GetRegion(model.Region).IsAutoHidden);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model)
        {
            Layout?.ToggleMinimize(model);
        }
    }

    private void OnFloatClick(object? sender, RoutedEventArgs e)
    {
        if (Model is TabGroupModel group)
        {
            if (group.ActiveTab is { } tab)
            {
                Layout?.FloatTab(tab);
            }

            return;
        }

        if (Model is { CanFloat: true })
        {
            Layout?.FloatFilesPanel();
        }
    }

    /// <summary>
    /// Closes the preview group: every tab leaves, the group stops being docked-visible and its borrowed column width
    /// returns to Main — the same end state as closing the group's last remaining tab.
    /// </summary>
    private async void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (Layout is not { } layout || Model is not TabGroupModel { Id: WorkbenchIds.PreviewTabs } group)
        {
            return;
        }

        try
        {
            var tabs = group.Tabs;
            for (var i = tabs.Count - 1; i >= 0; i--)
            {
                if (i >= tabs.Count)
                {
                    continue;
                }

                if (!await layout.TryCloseTabAsync(tabs[i]).ConfigureAwait(true))
                {
                    return;
                }

            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Workbench.ZLogWarning($"closing the preview group failed: {exception.Message}");
        }
    }

    private void DetachButtons()
    {
        if (coverButton is not null)
        {
            coverButton.Click -= OnCoverClick;
            coverButton = null;
        }

        if (pinButton is not null)
        {
            pinButton.Click -= OnPinClick;
            pinButton = null;
        }

        if (minimizeButton is not null)
        {
            minimizeButton.Click -= OnMinimizeClick;
            minimizeButton = null;
        }

        if (floatButton is not null)
        {
            floatButton.Click -= OnFloatClick;
            floatButton = null;
        }

        if (closeButton is not null)
        {
            closeButton.Click -= OnCloseClick;
            closeButton = null;
        }
    }

    private void Resubscribe()
    {
        Unsubscribe();
        if (attached)
        {
            Subscribe();
        }

        RefreshModel();
    }

    private void Subscribe()
    {
        if (subscribed || Model is not { } model || Layout is not { } layout)
        {
            return;
        }

        subscribed = true;
        model.PropertyChanged += OnModelChanged;
        layout.PropertyChanged += OnLayoutChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        watchedRegion = layout.GetRegion(model.Region);
        watchedRegion.PropertyChanged += OnRegionChanged;
        if (model is TabGroupModel group)
        {
            group.PropertyChanged += OnModelChanged;
            WatchTab(group.ActiveTab);
        }

        if (model.Id == WorkbenchIds.PreviewTabs)
        {
            layout.PreviewTabs.PropertyChanged += OnModelChanged;
        }
    }

    private void Unsubscribe()
    {
        if (!subscribed)
        {
            return;
        }

        subscribed = false;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        if (Model is { } model)
        {
            model.PropertyChanged -= OnModelChanged;
            if (model is TabGroupModel group)
            {
                group.PropertyChanged -= OnModelChanged;
            }
        }

        if (Layout is { } layout)
        {
            layout.PropertyChanged -= OnLayoutChanged;
            layout.PreviewTabs.PropertyChanged -= OnModelChanged;
        }

        if (watchedRegion is not null)
        {
            watchedRegion.PropertyChanged -= OnRegionChanged;
            watchedRegion = null;
        }

        WatchTab(null);
    }

    private void WatchTab(TabModel? tab)
    {
        if (ReferenceEquals(watchedTab, tab))
        {
            return;
        }

        if (watchedTab is not null)
        {
            watchedTab.PropertyChanged -= OnTabChanged;
        }

        watchedTab = tab;
        if (watchedTab is not null)
        {
            watchedTab.PropertyChanged += OnTabChanged;
        }
    }

    private static bool AffectsPanelChrome(string? propertyName) =>
        propertyName is nameof(PanelModel.Title)
            or nameof(PanelModel.IsVisible)
            or nameof(PanelModel.CanMinimize)
            or nameof(PanelModel.IsMinimized)
            or nameof(PanelModel.IsFloating)
            or nameof(TabGroupModel.ActiveTab);

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (AffectsPanelChrome(e.PropertyName))
        {
            RefreshModel();
        }
    }

    private void OnRegionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RegionModel.IsAutoHidden))
        {
            RefreshChrome();
        }
    }

    /// <summary>
    /// The button tooltips are built in code, so this control reads them again on a language switch. It is subscribed
    /// only while it is in the visual tree, so a panel of a closed window holds no reference to the static table.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A control of a window whose session is gone still sits in the subscriber list of the
        // process-wide table; touching its properties from another thread would queue a layout pass on the wrong
        // dispatcher. This control lives on the thread it was attached on.
        if (Environment.CurrentManagedThreadId != attachedThread)
        {
            return;
        }

        RefreshChrome();
    }

    private void OnLayoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WorkbenchLayout.IsPreviewCoveringMain):
                RefreshChrome();
                break;
            case nameof(WorkbenchLayout.DrawerPanel):
                RefreshModel();
                break;
        }
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabModel.Content))
        {
            RefreshBody();
        }
    }

    /// <summary>Recomputes the model-derived chrome: identity strings, title, tools, body and the static button capabilities.</summary>
    private void RefreshModel()
    {
        if (Model is not { } model)
        {
            Body = null;
            HeaderTools = null;
            ShowTitle = false;
            ShowTabStrip = false;
            TabGroup = null;
            CoverVisible = false;
            PinVisible = false;
            MinimizeVisible = false;
            ShowEmptyText = false;
            FloatVisible = false;
            CloseVisible = false;
            return;
        }

        var group = model as TabGroupModel;
        AutomationProperties.SetAutomationId(this, "panel-" + model.Id);
        AutomationProperties.SetName(this, model.Title);
        HeaderTitle = model.Title;
        HeaderTools = model.HeaderTools;
        ShowTitle = group is null;
        ShowTabStrip = group is not null;
        TabGroup = group;
        if (group is not null)
        {
            WatchTab(group.ActiveTab);
        }

        Body = model.IsVisible ? (group is null ? model.Content : group.ActiveTab?.Content) : null;
        // An empty bottom group can be opened by its own restore button: with no tab on show its body carries a line of text
        // instead of a blank box. Only that group - the others have nothing to open without a tab.
        ShowEmptyText = model.Id == WorkbenchIds.BottomTabs && model.IsVisible && !model.IsMinimized && group?.ActiveTab is null;

        var layout = Layout;
        CoverVisible = layout is not null && model.Id == WorkbenchIds.PreviewTabs && layout.PreviewTabs.IsVisible;
        CoverIcon = WorkbenchIcons.Find(this, "IconCoverMain");
        CoverAutomationId = "panel-" + model.Id + "-cover";

        CloseVisible = layout is not null && model.Id == WorkbenchIds.PreviewTabs && layout.PreviewTabs.IsVisible;
        CloseIcon = WorkbenchIcons.Find(this, "IconClose");
        CloseTooltip = ShellStrings.Close;
        CloseAutomationId = "panel-" + model.Id + "-close";

        PinVisible = model.CanPin;
        // The bottom group's title bar keeps one shape: its minimize/restore button is drawn in every state, so a tab docking
        // or the last one leaving never shifts the tools beside it. The float button still follows the active tab - with
        // nothing open there is nothing to float. Everything else is offered only while it can act.
        var fixedMinimize = model.Id == WorkbenchIds.BottomTabs;
        // The drawer holds one panel and has no splitter to drag one back: folding it to its title bar would leave the
        // drawer all but empty, so the minimize button is only offered to a panel docked in a region.
        MinimizeVisible = fixedMinimize || (model.CanMinimize && !IsDrawerContent);
        FloatVisible = !model.IsFloating && (model.CanFloat || group?.ActiveTab is not null);
        FloatIcon = WorkbenchIcons.Find(this, "IconWindowExternal");
        FloatTooltip = ShellStrings.Float;
        FloatAutomationId = "panel-" + model.Id + "-float";
        PinAutomationId = "panel-" + model.Id + "-pin";
        MinimizeAutomationId = "panel-" + model.Id + "-minimize";

        RefreshChrome();
    }

    /// <summary>Recomputes only the layout/region-derived button icons and tooltips, which a splitter drag must not touch.</summary>
    private void RefreshChrome()
    {
        if (Model is not { } model)
        {
            return;
        }

        var layout = Layout;
        CoverTooltip = layout?.IsPreviewCoveringMain == true ? ShellStrings.Uncover : ShellStrings.Cover;

        // One button, one word: the pin shows the state (solid = docked, outline = auto-hidden), the tooltip names the
        // action the region takes.
        var autoHidden = layout?.GetRegion(model.Region).IsAutoHidden == true;
        PinIcon = WorkbenchIcons.Find(this, autoHidden ? "IconPin" : "IconPinSolid");
        PinTooltip = ShellStrings.AutoHide;

        var minimized = model.IsMinimized;
        EmptyText = ShellStrings.BottomGroupEmpty;
        // The restore glyph is the same FIS arrow_maximize_vertical the files panel's "expand all" draws, so the one shape
        // lives in one key rather than being copied a second time under a panel-specific name.
        MinimizeIcon = WorkbenchIcons.Find(this, minimized ? "IconArrowMaximizeVertical" : "IconMinimize");
        MinimizeTooltip = minimized ? ShellStrings.RestorePanel : ShellStrings.MinimizePanel;
        PseudoClasses.Set(":minimized", minimized);
    }

    /// <summary>Updates only the body when the active tab's content is re-bound (a preview file re-opened in place).</summary>
    private void RefreshBody()
    {
        if (Model is not { } model)
        {
            Body = null;
            return;
        }

        var group = model as TabGroupModel;
        Body = model.IsVisible ? (group is null ? model.Content : group.ActiveTab?.Content) : null;
    }
}
