using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Forge.App.ViewModels;

namespace Forge.App.Workbench.Controls;

/// <summary>
/// One tab of a <see cref="PanelTabStrip"/>: the title, the active silhouette and the close button. Interaction is not
/// handled here — presses are forwarded to the strip, which owns the drag, reorder and float-out state.
/// </summary>
internal sealed class TabHeaderView : TemplatedControl
{
    /// <summary>Radius of the two top corners, measured on the frame's outer edge.</summary>
    private const double Radius = 5;

    /// <summary>
    /// Radius of the outward fillet at the two bottom corners, measured on the frame's outer edge: the frame leaves the
    /// tab's sides and curves outward as it reaches the title bar's bottom line, so the active tab widens into the panel
    /// body below instead of ending on a square step. Larger than the top corner rounds on purpose — the foot only works
    /// if the flare reads at a glance — but kept a notch under it so the neighbouring tab never feels crowded.
    /// </summary>
    private const double FootRadius = 5;

    /// <summary>Horizontal span of the short, tangent-matched transition from the divider row into the inner foot arc.</summary>
    private const double FootBandTransition = 2.5;

    /// <summary>Thickness of the frame.</summary>
    private const double FrameThickness = 1;

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

    /// <summary>
    /// The glyph this tab leads with: a conversation wears the chat bubble, a file preview wears the same document the
    /// file tree puts in front of the file's name, and the remaining kinds carry their marker in the title text alone.
    /// </summary>
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<TabHeaderView, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> CloseAutomationIdProperty =
        AvaloniaProperty.Register<TabHeaderView, string?>(nameof(CloseAutomationId));

    private PanelIconButton? closeButton;
    private TabModel? watched;
    private Size silhouetteSize;
    private StreamGeometry? silhouette;
    private StreamGeometry? frame;

    static TabHeaderView()
    {
        ModelProperty.Changed.AddClassHandler<TabHeaderView>((header, _) => header.ApplyModel());
        IsActiveProperty.Changed.AddClassHandler<TabHeaderView>((header, e) =>
        {
            header.PseudoClasses.Set(":selected", e.NewValue is true);
            header.ApplyIcon();
        });
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
    /// True for a tab whose kind draws a leading icon: a conversation's chat bubble, or the document a file preview opens
    /// with — the same glyph the file tree draws before the file's name. Other kinds reserve no icon slot and carry their
    /// marker in the title text.
    /// </summary>
    public bool IsIconVisible
    {
        get => GetValue(IsIconVisibleProperty);
        set => SetValue(IsIconVisibleProperty, value);
    }

    /// <summary>The leading glyph, already resolved to the active/inactive variant of this tab's kind by <see cref="ApplyIcon"/>.</summary>
    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
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
        ApplyIcon();
        AutomationProperties.SetName(this, tab.Title);
        ToolTip.SetTip(this, tab.Title);
    }

    /// <summary>
    /// Resolves the tab's leading glyph: the chat bubble (filled while the tab is active) for a conversation, the file
    /// tree's document for a file preview, nothing for the kinds that spell themselves out in the title.
    /// </summary>
    private void ApplyIcon()
    {
        var (rest, active) = Model?.Kind switch
        {
            TabKind.Session or TabKind.SubAgent =>
                (WorkbenchIcons.Find(this, "IconChat"), WorkbenchIcons.Find(this, "IconChatFilled")),
            TabKind.FilePreview => (TreeIcons.File, null),
            _ => (null, null),
        };

        IsIconVisible = rest is not null;
        Icon = IsActive && active is not null ? active : rest;
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
    /// Draws the active tab: its body, whose top corners are rounded and whose two bottom corners turn outward through
    /// <see cref="FootRadius"/> into the title bar's bottom line, and a <see cref="FrameThickness"/> frame inside that
    /// outline. Drawn here rather than by the template's border because a border can only round a corner inward. The frame
    /// is a filled ring between the outline and its own concentric-and-straight inward offset — the ring's foot band and
    /// the divider line then sit on the same pixel row for the whole joint, so no hairline tangent survives anywhere.
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
        if (size.Width <= 2 * FootRadius || size.Height <= Radius + FootRadius)
        {
            return;
        }

        if (silhouetteSize != size || silhouette is null || frame is null)
        {
            silhouetteSize = size;
            silhouette = BuildSilhouette(size);
            frame = BuildFrame(size);
        }

        if (active && Background is { } fill)
        {
            context.DrawGeometry(fill, null, silhouette);
        }

        if (BorderBrush is { } border)
        {
            context.DrawGeometry(border, null, frame);
        }
    }

    /// <summary>The tab's body: its box, rounded on top and widened by <see cref="FootRadius"/> at the bottom.</summary>
    private static StreamGeometry BuildSilhouette(Size size)
    {
        var right = size.Width;
        var bottom = size.Height;
        var corner = new Size(Radius, Radius);
        var foot = new Size(FootRadius, FootRadius);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(Radius, 0), true);
            path.LineTo(new Point(right - Radius, 0));
            path.ArcTo(new Point(right, Radius), corner, 0, false, SweepDirection.Clockwise);
            path.LineTo(new Point(right, bottom - FootRadius));
            path.ArcTo(new Point(right + FootRadius, bottom), foot, 0, false, SweepDirection.CounterClockwise);
            path.LineTo(new Point(-FootRadius, bottom));
            path.ArcTo(new Point(0, bottom - FootRadius), foot, 0, false, SweepDirection.CounterClockwise);
            path.LineTo(new Point(0, Radius));
            path.ArcTo(new Point(Radius, 0), corner, 0, false, SweepDirection.Clockwise);
            path.EndFigure(true);
        }

        return geometry;
    }

    /// <summary>
    /// The frame: the silhouette traversed one way and, back along the bottom, the same outline shrunk by the frame
    /// thickness, so the filled region between them is the frame. The foot's inner edge is concentric with its outer
    /// edge — the flare is convex, so stepping inward off a radius-R arc means the same circle at R+1 — which keeps the
    /// ring a true 1 px through the whole curve. The inner circle crosses the row one frame thickness above the bottom
    /// edge at x = r + R − 2·√(R·T). At each crossing, a short cubic blends the divider-row run into the inner arc with
    /// matching endpoint tangents. The transition and the remaining straight run stay outside the tab's bounds, so the
    /// frame joins the exposed divider without a sharp corner or a border tongue inside the tab.
    /// </summary>
    private static StreamGeometry BuildFrame(Size size)
    {
        var right = size.Width;
        var bottom = size.Height;
        var corner = new Size(Radius, Radius);
        var innerCorner = new Size(Radius - FrameThickness, Radius - FrameThickness);
        var foot = new Size(FootRadius, FootRadius);
        var innerFoot = new Size(FootRadius + FrameThickness, FootRadius + FrameThickness);
        // Half-chord of the inner circle's intersection with the row one frame thickness above the bottom edge.
        var crossing = Math.Sqrt(4 * FootRadius * FrameThickness);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            // Outer edge: left tip, up the left fillet and side, over the top, down the right side and fillet to the tip.
            path.BeginFigure(new Point(-FootRadius, bottom), true);
            path.ArcTo(new Point(0, bottom - FootRadius), foot, 0, false, SweepDirection.CounterClockwise);
            path.LineTo(new Point(0, Radius));
            path.ArcTo(new Point(Radius, 0), corner, 0, false, SweepDirection.Clockwise);
            path.LineTo(new Point(right - Radius, 0));
            path.ArcTo(new Point(right, Radius), corner, 0, false, SweepDirection.Clockwise);
            path.LineTo(new Point(right, bottom - FootRadius));
            path.ArcTo(new Point(right + FootRadius, bottom), foot, 0, false, SweepDirection.CounterClockwise);
            // Inner, right foot: keep only a short straight run on the divider row, then blend into the concentric inner
            // arc. The cubic's endpoint tangents match the horizontal divider and the arc, so the frame has no sharp join.
            var rightCrossing = right + FootRadius - crossing;
            var rightTransitionStart = rightCrossing + FootBandTransition;
            var transitionHandle = FootBandTransition / 3;
            var innerRadius = FootRadius + FrameThickness;
            var arcTangentX = (FootRadius - FrameThickness) / innerRadius;
            var arcTangentY = crossing / innerRadius;
            path.LineTo(new Point(rightTransitionStart, bottom));
            path.CubicBezierTo(
                new Point(rightTransitionStart - transitionHandle, bottom),
                new Point(rightCrossing + (transitionHandle * arcTangentX), bottom - FrameThickness + (transitionHandle * arcTangentY)),
                new Point(rightCrossing, bottom - FrameThickness));
            path.ArcTo(new Point(right - FrameThickness, bottom - FootRadius), innerFoot, 0, false, SweepDirection.Clockwise);
            path.LineTo(new Point(right - FrameThickness, Radius));
            path.ArcTo(new Point(right - Radius, FrameThickness), innerCorner, 0, false, SweepDirection.CounterClockwise);
            path.LineTo(new Point(Radius, FrameThickness));
            path.ArcTo(new Point(FrameThickness, Radius), innerCorner, 0, false, SweepDirection.CounterClockwise);
            path.LineTo(new Point(FrameThickness, bottom - FootRadius));
            // Inner, left foot: mirrored tangent-matched transition; EndFigure closes the short divider run to the outer tip.
            var leftCrossing = -FootRadius + crossing;
            var leftTransitionEnd = leftCrossing - FootBandTransition;
            path.ArcTo(new Point(leftCrossing, bottom - FrameThickness), innerFoot, 0, false, SweepDirection.Clockwise);
            path.CubicBezierTo(
                new Point(leftCrossing - (transitionHandle * arcTangentX), bottom - FrameThickness + (transitionHandle * arcTangentY)),
                new Point(leftTransitionEnd + transitionHandle, bottom),
                new Point(leftTransitionEnd, bottom));
            path.EndFigure(true);
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

        // Resource lookups need a host, so a header that was refreshed before it joined the tree resolves its glyph here.
        ApplyIcon();
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
