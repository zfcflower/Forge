using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Forge.App.Workbench.Controls;

/// <summary>
/// One tab of a <see cref="PanelTabStrip"/>: the title, the active underline and the close button. Interaction is not
/// handled here — presses are forwarded to the strip, which owns the drag, reorder and float-out state.
/// </summary>
internal sealed class TabHeaderView : TemplatedControl
{
    /// <summary>Backdrop brush of the active tab; the theme keeps the rest transparent.</summary>
    public static readonly StyledProperty<TabModel?> ModelProperty =
        AvaloniaProperty.Register<TabHeaderView, TabModel?>(nameof(Model));

    public static readonly StyledProperty<string?> TabTitleProperty =
        AvaloniaProperty.Register<TabHeaderView, string?>(nameof(TabTitle));

    public static readonly StyledProperty<bool> IsClosableProperty =
        AvaloniaProperty.Register<TabHeaderView, bool>(nameof(IsClosable));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<TabHeaderView, bool>(nameof(IsActive));

    public static readonly StyledProperty<string?> CloseAutomationIdProperty =
        AvaloniaProperty.Register<TabHeaderView, string?>(nameof(CloseAutomationId));

    private PanelIconButton? closeButton;
    private TabModel? watched;

    static TabHeaderView()
    {
        ModelProperty.Changed.AddClassHandler<TabHeaderView>((header, _) => header.ApplyModel());
        IsActiveProperty.Changed.AddClassHandler<TabHeaderView>((header, e) => header.PseudoClasses.Set(":selected", e.NewValue is true));
    }

    /// <summary>The strip that owns this header; presses and the close button are routed through it.</summary>
    public PanelTabStrip? Strip { get; set; }

    public TabModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public string? TabTitle
    {
        get => GetValue(TabTitleProperty);
        set => SetValue(TabTitleProperty, value);
    }

    public bool IsClosable
    {
        get => GetValue(IsClosableProperty);
        set => SetValue(IsClosableProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Automation id of the close button, so tests can click the tab's own close box.</summary>
    public string? CloseAutomationId
    {
        get => GetValue(CloseAutomationIdProperty);
        set => SetValue(CloseAutomationIdProperty, value);
    }

    /// <summary>Refreshes the visuals after the tab model changed a property the header shows.</summary>
    public void Refresh()
    {
        if (Model is not { } tab)
        {
            return;
        }

        TabTitle = tab.Title;
        IsClosable = tab.IsClosable;
        IsActive = ReferenceEquals(tab, tab.Group?.ActiveTab);
        AutomationProperties.SetName(this, tab.Title);
        ToolTip.SetTip(this, tab.Title);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (closeButton is not null)
        {
            closeButton.Click -= OnCloseClick;
        }

        closeButton = e.NameScope.Find<PanelIconButton>("PART_Close");
        if (closeButton is not null)
        {
            closeButton.Click += OnCloseClick;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Enter or Key.Space) || Model is not { } tab)
        {
            return;
        }

        Strip?.Activate(tab);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Model is { } tab && Strip is { } strip)
        {
            strip.BeginTabDrag(tab, e);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Strip?.CloseTab(Model);

    private void ApplyModel()
    {
        if (Model is not { } tab)
        {
            CloseAutomationId = null;
            return;
        }

        AutomationProperties.SetAutomationId(this, "tab-" + tab.Id);
        CloseAutomationId = "tab-" + tab.Id + "-close";
        IsVisible = !tab.IsFloating;
        Watch(tab);
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Watch(Model);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Watch(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void Watch(TabModel? tab)
    {
        if (ReferenceEquals(watched, tab))
        {
            return;
        }

        if (watched is not null)
        {
            watched.PropertyChanged -= OnTabChanged;
        }

        watched = tab;
        if (watched is not null)
        {
            watched.PropertyChanged += OnTabChanged;
        }
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TabModel tab)
        {
            return;
        }

        if (e.PropertyName == nameof(TabModel.IsFloating))
        {
            IsVisible = !tab.IsFloating;
            return;
        }

        Refresh();
    }
}
