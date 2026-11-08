using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Forge.App.Workbench.Controls;

/// <summary>
/// One tab of a <see cref="PanelTabStrip"/>: the title, the active silhouette and the close button. Interaction is not
/// handled here — presses are forwarded to the strip, which owns the drag, reorder and float-out state.
/// </summary>
internal sealed class TabHeaderView : TemplatedControl
{
    /// <summary>Radius of the two top corners.</summary>
    private const double TopCornerRadius = 4;

    /// <summary>
    /// Radius of the outward fillet at the two bottom corners. The outline leaves the tab's sides and curves outward as it
    /// reaches the title bar's bottom line, so the active tab widens into the panel body below instead of ending on a
    /// square step.
    /// </summary>
    private const double FootRadius = 4;

    /// <summary>Half pixel: a 1 px pen centred on a half-pixel line covers exactly one device pixel.</summary>
    private const double Inset = 0.5;

    /// <summary>Backdrop brush of the active tab; the theme keeps the rest transparent.</summary>
    public static readonly StyledProperty<TabModel?> ModelProperty =
        AvaloniaProperty.Register<TabHeaderView, TabModel?>(nameof(Model));

    public static readonly StyledProperty<string?> TabTitleProperty =
        AvaloniaProperty.Register<TabHeaderView, string?>(nameof(TabTitle));

    public static readonly StyledProperty<bool> IsClosableProperty =
        AvaloniaProperty.Register<TabHeaderView, bool>(nameof(IsClosable));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<TabHeaderView, bool>(nameof(IsActive));

    public static readonly StyledProperty<bool> IsIconVisibleProperty =
        AvaloniaProperty.Register<TabHeaderView, bool>(nameof(IsIconVisible));

    public static readonly StyledProperty<string?> CloseAutomationIdProperty =
        AvaloniaProperty.Register<TabHeaderView, string?>(nameof(CloseAutomationId));

    private PanelIconButton? closeButton;
    private TabModel? watched;
    private Size silhouetteSize;
    private StreamGeometry? silhouetteFill;
    private StreamGeometry? silhouetteOutline;

    static TabHeaderView()
    {
        ModelProperty.Changed.AddClassHandler<TabHeaderView>((header, _) => header.ApplyModel());
        IsActiveProperty.Changed.AddClassHandler<TabHeaderView>((header, e) => header.PseudoClasses.Set(":selected", e.NewValue is true));
        AffectsRender<TabHeaderView>(IsActiveProperty, BackgroundProperty, BorderBrushProperty);
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

    /// <summary>
    /// True for a tab whose kind draws a leading icon (the session tab's chat bubble; the template owns the geometry and
    /// the filled-while-active treatment). Other kinds carry their marker in the title text and reserve no icon slot.
    /// </summary>
    public bool IsIconVisible
    {
        get => GetValue(IsIconVisibleProperty);
        set => SetValue(IsIconVisibleProperty, value);
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
        IsIconVisible = tab.Kind == TabKind.Session;
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

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (silhouetteSize != finalSize)
        {
            InvalidateVisual();
        }

        return base.ArrangeOverride(finalSize);
    }

    /// <summary>
    /// Draws the active tab's silhouette: a 1 px frame around a panel-coloured body whose two top corners are rounded and
    /// whose two bottom corners turn outward into the title bar's bottom line. Drawn here rather than by the template's
    /// border because a border can only round a corner inward; the bottom edge carries no stroke at all, since the tab
    /// stands on the panel body and merges with it.
    /// </summary>
    public override void Render(DrawingContext context)
    {
        var active = IsActive;
        var focused = Classes.Contains(":focus-visible");
        if (!active && !focused)
        {
            return;
        }

        var size = Bounds.Size;
        if (size.Width <= (2 * FootRadius) + 1 || size.Height <= TopCornerRadius + FootRadius + 1)
        {
            return;
        }

        if (silhouetteSize != size || silhouetteFill is null || silhouetteOutline is null)
        {
            silhouetteSize = size;
            silhouetteFill = BuildFill(size);
            silhouetteOutline = BuildOutline(size);
        }

        if (active && Background is { } fill)
        {
            context.DrawGeometry(fill, null, silhouetteFill);
        }

        if (BorderBrush is { } border)
        {
            context.DrawGeometry(null, new Pen(border, 1), silhouetteOutline);
        }
    }

    /// <summary>The filled silhouette: the tab's box, rounded on top and widened by <see cref="FootRadius"/> at the bottom.</summary>
    private static StreamGeometry BuildFill(Size size)
    {
        var right = size.Width;
        var bottom = size.Height;
        var corner = new Size(TopCornerRadius, TopCornerRadius);
        var foot = new Size(FootRadius, FootRadius);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(TopCornerRadius, 0), true);
            path.LineTo(new Point(right - TopCornerRadius, 0));
            path.ArcTo(new Point(right, TopCornerRadius), corner, 0, false, SweepDirection.Clockwise);
            path.LineTo(new Point(right, bottom - FootRadius));
            path.ArcTo(new Point(right + FootRadius, bottom), foot, 0, false, SweepDirection.CounterClockwise);
            path.LineTo(new Point(-FootRadius, bottom));
            path.ArcTo(new Point(0, bottom - FootRadius), foot, 0, false, SweepDirection.CounterClockwise);
            path.LineTo(new Point(0, TopCornerRadius));
            path.ArcTo(new Point(TopCornerRadius, 0), corner, 0, false, SweepDirection.Clockwise);
            path.EndFigure(true);
        }

        return geometry;
    }

    /// <summary>
    /// The 1 px frame: the fill's boundary half a pixel in, so straight runs land on whole device pixels, open along the
    /// bottom because the tab has no line between itself and the panel body there.
    /// </summary>
    private static StreamGeometry BuildOutline(Size size)
    {
        var right = size.Width - Inset;
        var bottom = size.Height - Inset;
        var corner = new Size(TopCornerRadius - Inset, TopCornerRadius - Inset);
        var foot = FootRadius - Inset;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            // Up the left foot, up the left side, over the top, down the right side and out along the right foot.
            path.BeginFigure(new Point(Inset - foot, bottom), false);
            path.ArcTo(new Point(Inset, bottom - FootRadius), new Size(foot, foot), 0, false, SweepDirection.CounterClockwise);
            path.LineTo(new Point(Inset, TopCornerRadius));
            path.ArcTo(new Point(TopCornerRadius, Inset), corner, 0, false, SweepDirection.Clockwise);
            path.LineTo(new Point(size.Width - TopCornerRadius, Inset));
            path.ArcTo(new Point(right, TopCornerRadius), corner, 0, false, SweepDirection.Clockwise);
            path.LineTo(new Point(right, bottom - FootRadius));
            path.ArcTo(new Point(right + foot, bottom), new Size(foot, foot), 0, false, SweepDirection.CounterClockwise);
            path.EndFigure(false);
        }

        return geometry;
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
