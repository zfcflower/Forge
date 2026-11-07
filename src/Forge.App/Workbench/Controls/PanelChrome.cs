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

namespace Forge.App.Workbench.Controls;

/// <summary>
/// A panel of the workbench: a 28 px title bar (title or tab strip on the left, capability-driven icon buttons on the
/// right) above the body. The control holds no layout state — every button calls back into <see cref="WorkbenchLayout"/>,
/// and the title bar's blank space starts a region-swap drag on the host.
/// </summary>
internal sealed class PanelChrome : TemplatedControl
{
    /// <summary>Height of the title bar; presses below it belong to the body, not to a region-swap drag.</summary>
    public const double TitleBarHeight = 28;

    public static readonly StyledProperty<PanelModel?> ModelProperty =
        AvaloniaProperty.Register<PanelChrome, PanelModel?>(nameof(Model));

    public static readonly StyledProperty<WorkbenchLayout?> LayoutProperty =
        AvaloniaProperty.Register<PanelChrome, WorkbenchLayout?>(nameof(Layout));

    public static readonly StyledProperty<string?> HeaderTitleProperty =
        AvaloniaProperty.Register<PanelChrome, string?>(nameof(HeaderTitle));

    public static readonly StyledProperty<bool> ShowTitleProperty =
        AvaloniaProperty.Register<PanelChrome, bool>(nameof(ShowTitle));

    public static readonly StyledProperty<bool> ShowTabStripProperty =
        AvaloniaProperty.Register<PanelChrome, bool>(nameof(ShowTabStrip));

    public static readonly StyledProperty<TabGroupModel?> TabGroupProperty =
        AvaloniaProperty.Register<PanelChrome, TabGroupModel?>(nameof(TabGroup));

    public static readonly StyledProperty<IEnumerable?> HeaderToolsProperty =
        AvaloniaProperty.Register<PanelChrome, IEnumerable?>(nameof(HeaderTools));

    public static readonly StyledProperty<object?> BodyProperty =
        AvaloniaProperty.Register<PanelChrome, object?>(nameof(Body));

    public static readonly StyledProperty<bool> CoverVisibleProperty =
        AvaloniaProperty.Register<PanelChrome, bool>(nameof(CoverVisible));

    public static readonly StyledProperty<Geometry?> CoverIconProperty =
        AvaloniaProperty.Register<PanelChrome, Geometry?>(nameof(CoverIcon));

    public static readonly StyledProperty<string?> CoverTooltipProperty =
        AvaloniaProperty.Register<PanelChrome, string?>(nameof(CoverTooltip));

    public static readonly StyledProperty<string?> CoverAutomationIdProperty =
        AvaloniaProperty.Register<PanelChrome, string?>(nameof(CoverAutomationId));

    public static readonly StyledProperty<bool> PinVisibleProperty =
        AvaloniaProperty.Register<PanelChrome, bool>(nameof(PinVisible));

    public static readonly StyledProperty<Geometry?> PinIconProperty =
        AvaloniaProperty.Register<PanelChrome, Geometry?>(nameof(PinIcon));

    public static readonly StyledProperty<string?> PinTooltipProperty =
        AvaloniaProperty.Register<PanelChrome, string?>(nameof(PinTooltip));

    public static readonly StyledProperty<string?> PinAutomationIdProperty =
        AvaloniaProperty.Register<PanelChrome, string?>(nameof(PinAutomationId));

    public static readonly StyledProperty<bool> MaximizeVisibleProperty =
        AvaloniaProperty.Register<PanelChrome, bool>(nameof(MaximizeVisible));

    public static readonly StyledProperty<Geometry?> MaximizeIconProperty =
        AvaloniaProperty.Register<PanelChrome, Geometry?>(nameof(MaximizeIcon));

    public static readonly StyledProperty<string?> MaximizeTooltipProperty =
        AvaloniaProperty.Register<PanelChrome, string?>(nameof(MaximizeTooltip));

    public static readonly StyledProperty<string?> MaximizeAutomationIdProperty =
        AvaloniaProperty.Register<PanelChrome, string?>(nameof(MaximizeAutomationId));

    public static readonly StyledProperty<bool> FloatVisibleProperty =
        AvaloniaProperty.Register<PanelChrome, bool>(nameof(FloatVisible));

    public static readonly StyledProperty<Geometry?> FloatIconProperty =
        AvaloniaProperty.Register<PanelChrome, Geometry?>(nameof(FloatIcon));

    public static readonly StyledProperty<string?> FloatTooltipProperty =
        AvaloniaProperty.Register<PanelChrome, string?>(nameof(FloatTooltip));

    public static readonly StyledProperty<string?> FloatAutomationIdProperty =
        AvaloniaProperty.Register<PanelChrome, string?>(nameof(FloatAutomationId));

    public static readonly StyledProperty<bool> SidebarProperty =
        AvaloniaProperty.Register<PanelChrome, bool>(nameof(Sidebar));

    private PanelIconButton? coverButton;
    private PanelIconButton? pinButton;
    private PanelIconButton? maximizeButton;
    private PanelIconButton? floatButton;
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

    public PanelModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public WorkbenchLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public string? HeaderTitle
    {
        get => GetValue(HeaderTitleProperty);
        set => SetValue(HeaderTitleProperty, value);
    }

    public bool ShowTitle
    {
        get => GetValue(ShowTitleProperty);
        set => SetValue(ShowTitleProperty, value);
    }

    public bool ShowTabStrip
    {
        get => GetValue(ShowTabStripProperty);
        set => SetValue(ShowTabStripProperty, value);
    }

    public TabGroupModel? TabGroup
    {
        get => GetValue(TabGroupProperty);
        set => SetValue(TabGroupProperty, value);
    }

    public IEnumerable? HeaderTools
    {
        get => GetValue(HeaderToolsProperty);
        set => SetValue(HeaderToolsProperty, value);
    }

    /// <summary>What the body shows: a plain panel's content, or the active tab's content for a tab group.</summary>
    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    /// <summary>True for the panels of a sidebar region (Project or Inspector); their body uses the sidebar surface.</summary>
    public bool Sidebar
    {
        get => GetValue(SidebarProperty);
        set => SetValue(SidebarProperty, value);
    }

    public bool CoverVisible
    {
        get => GetValue(CoverVisibleProperty);
        set => SetValue(CoverVisibleProperty, value);
    }

    public Geometry? CoverIcon
    {
        get => GetValue(CoverIconProperty);
        set => SetValue(CoverIconProperty, value);
    }

    public string? CoverTooltip
    {
        get => GetValue(CoverTooltipProperty);
        set => SetValue(CoverTooltipProperty, value);
    }

    public string? CoverAutomationId
    {
        get => GetValue(CoverAutomationIdProperty);
        set => SetValue(CoverAutomationIdProperty, value);
    }

    public bool PinVisible
    {
        get => GetValue(PinVisibleProperty);
        set => SetValue(PinVisibleProperty, value);
    }

    public Geometry? PinIcon
    {
        get => GetValue(PinIconProperty);
        set => SetValue(PinIconProperty, value);
    }

    public string? PinTooltip
    {
        get => GetValue(PinTooltipProperty);
        set => SetValue(PinTooltipProperty, value);
    }

    public string? PinAutomationId
    {
        get => GetValue(PinAutomationIdProperty);
        set => SetValue(PinAutomationIdProperty, value);
    }

    public bool MaximizeVisible
    {
        get => GetValue(MaximizeVisibleProperty);
        set => SetValue(MaximizeVisibleProperty, value);
    }

    public Geometry? MaximizeIcon
    {
        get => GetValue(MaximizeIconProperty);
        set => SetValue(MaximizeIconProperty, value);
    }

    public string? MaximizeTooltip
    {
        get => GetValue(MaximizeTooltipProperty);
        set => SetValue(MaximizeTooltipProperty, value);
    }

    public string? MaximizeAutomationId
    {
        get => GetValue(MaximizeAutomationIdProperty);
        set => SetValue(MaximizeAutomationIdProperty, value);
    }

    public bool FloatVisible
    {
        get => GetValue(FloatVisibleProperty);
        set => SetValue(FloatVisibleProperty, value);
    }

    public Geometry? FloatIcon
    {
        get => GetValue(FloatIconProperty);
        set => SetValue(FloatIconProperty, value);
    }

    public string? FloatTooltip
    {
        get => GetValue(FloatTooltipProperty);
        set => SetValue(FloatTooltipProperty, value);
    }

    public string? FloatAutomationId
    {
        get => GetValue(FloatAutomationIdProperty);
        set => SetValue(FloatAutomationIdProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        DetachButtons();
        coverButton = e.NameScope.Find<PanelIconButton>("PART_Cover");
        pinButton = e.NameScope.Find<PanelIconButton>("PART_Pin");
        maximizeButton = e.NameScope.Find<PanelIconButton>("PART_Maximize");
        floatButton = e.NameScope.Find<PanelIconButton>("PART_Float");
        if (coverButton is not null)
        {
            coverButton.Click += OnCoverClick;
        }

        if (pinButton is not null)
        {
            pinButton.Click += OnPinClick;
        }

        if (maximizeButton is not null)
        {
            maximizeButton.Click += OnMaximizeClick;
        }

        if (floatButton is not null)
        {
            floatButton.Click += OnFloatClick;
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

        if (model.Region == RegionRole.Main)
        {
            Layout.IsBottomCollapsed = !Layout.IsBottomCollapsed;
            return;
        }

        Layout.SetAutoHidden(model.Region, !Layout.GetRegion(model.Region).IsAutoHidden);
    }

    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model)
        {
            Layout?.ToggleMaximize(model);
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

        if (maximizeButton is not null)
        {
            maximizeButton.Click -= OnMaximizeClick;
            maximizeButton = null;
        }

        if (floatButton is not null)
        {
            floatButton.Click -= OnFloatClick;
            floatButton = null;
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
            or nameof(PanelModel.CanMaximize)
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
        if (e.PropertyName is nameof(RegionModel.IsAutoHidden) or nameof(RegionModel.MaximizedPanel))
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
        // A control of a window whose session is gone (a test host) still sits in the subscriber list of the
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
        if (e.PropertyName is nameof(WorkbenchLayout.IsBottomCollapsed) or nameof(WorkbenchLayout.IsPreviewCoveringMain))
        {
            RefreshChrome();
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
            MaximizeVisible = false;
            FloatVisible = false;
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

        var layout = Layout;
        CoverVisible = layout is not null && model.Id == WorkbenchIds.PreviewTabs && layout.PreviewTabs.IsVisible;
        CoverIcon = WorkbenchIcons.Find(this, "IconCoverMain");
        CoverAutomationId = "panel-" + model.Id + "-cover";

        PinVisible = model.CanPin;
        MaximizeVisible = model.CanMaximize;
        FloatVisible = !model.IsFloating && (model.CanFloat || group?.ActiveTab is not null);
        FloatIcon = WorkbenchIcons.Find(this, "IconWindowExternal");
        FloatTooltip = ShellStrings.Float;
        FloatAutomationId = "panel-" + model.Id + "-float";
        PinAutomationId = "panel-" + model.Id + "-pin";
        MaximizeAutomationId = "panel-" + model.Id + "-maximize";

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

        if (model.Region == RegionRole.Main)
        {
            var collapsed = layout?.IsBottomCollapsed == true;
            PinIcon = WorkbenchIcons.Find(this, collapsed ? "IconChevronUp" : "IconChevronDown");
            PinTooltip = collapsed ? ShellStrings.Pin : ShellStrings.AutoHide;
        }
        else
        {
            var hidden = layout?.GetRegion(model.Region).IsAutoHidden == true;
            PinIcon = WorkbenchIcons.Find(this, hidden ? "IconPin" : "IconAutoHide");
            PinTooltip = hidden ? ShellStrings.Pin : ShellStrings.AutoHide;
        }

        var maximized = layout is not null && ReferenceEquals(layout.GetRegion(model.Region).MaximizedPanel, model);
        MaximizeIcon = WorkbenchIcons.Find(this, maximized ? "IconRestore" : "IconMaximize");
        MaximizeTooltip = maximized ? ShellStrings.RestorePanel : ShellStrings.MaximizePanel;
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
