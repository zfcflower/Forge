using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// Where one gutter sits in the transcript. The templates state it instead of the control guessing from the data context,
/// so the same control serves a leaf message, a turn header, a turn block and the turn's footnote without type checks.
/// </summary>
internal enum TimelineRowRole
{
    /// <summary>A top-level item that always has a node: a user message or a transcript notice.</summary>
    Item,

    /// <summary>The turn's metadata row: no node, the axis only passes through it.</summary>
    TurnHeader,

    /// <summary>One block of an expanded turn, a row of the flat transcript list.</summary>
    Block,

    /// <summary>The turn's footnote, rendered next to the turn's blocks but not one of them.</summary>
    Footnote,
}

/// <summary>
/// The left column of one transcript row: the vertical axis with this row's node on it. The control owns the geometry
/// decision (it asks <see cref="TimelineLayout"/> and supplies the row's position from the visual tree it is already in) and
/// draws the result itself: the axis segments, the node's icon, and the ring behind a thinking or waiting node are one
/// <see cref="Render"/>, not a template of a dozen visuals.
/// <para>
/// Why it draws itself: the gutter is on every row of the transcript, and a row is built again each time the scroll bar
/// jumps. A templated gutter (a grid, panels, three borders, an ellipse, an icon with its own viewbox and canvas) was about
/// 14 of the ~37 visuals of a collapsed tool row, and a visual costs ~12-35 KB and tens of microseconds to attach (style
/// matching, bindings, resource lookups) whatever it shows; this control is one visual with no template, no binding and no
/// style. The sizes still come from <c>Tokens.axaml</c> (read once), the colours are the theme's brushes (resolved when
/// drawn and again after a theme or resource change), the icons are the dictionary's geometries on their 24×24 grid scaled
/// into the node box exactly as <c>FluentIcon</c> does. The running pulse and the two rings are computed from one process
/// clock and ask for a frame only while one is on screen and motion is allowed.
/// </para>
/// <para>Nothing here allocates per message: the geometry is read from the icon dictionary when first drawn, and the
/// subscriptions are limited to the realized rows (the two collections that can move a node, the turn view model while a
/// turn's expansion changes, the language and the motion gate). The row keeps its position current across append, expand and
/// collapse without rebuilding anything.</para>
/// </summary>
internal sealed class TimelineGutter : Control
{
    public static readonly StyledProperty<TimelineNodeKind> KindProperty =
        AvaloniaProperty.Register<TimelineGutter, TimelineNodeKind>(nameof(Kind));

    public static readonly StyledProperty<TimelineRowRole> RoleProperty =
        AvaloniaProperty.Register<TimelineGutter, TimelineRowRole>(nameof(Role));

    /// <summary>The tool call's name; only read when <see cref="Kind"/> is <see cref="TimelineNodeKind.Tool"/>.</summary>
    public static readonly StyledProperty<string?> ToolNameProperty =
        AvaloniaProperty.Register<TimelineGutter, string?>(nameof(ToolName));

    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<TimelineGutter, bool>(nameof(IsRunning));

    public static readonly StyledProperty<bool> IsFailingProperty =
        AvaloniaProperty.Register<TimelineGutter, bool>(nameof(IsFailing));

    /// <summary>
    /// Whether this row draws its node. The turn header does not, except when it is the end node of a turn that has nothing
    /// visible of its own (see <see cref="TimelineLayout.TurnHeader"/>); every other row always does.
    /// </summary>
    public static readonly StyledProperty<bool> ShowNodeProperty =
        AvaloniaProperty.Register<TimelineGutter, bool>(nameof(ShowNode), true);

    /// <summary>Whether the axis is drawn from the row's top to the node (there is a node above).</summary>
    public static readonly StyledProperty<bool> ShowTopLineProperty =
        AvaloniaProperty.Register<TimelineGutter, bool>(nameof(ShowTopLine));

    /// <summary>Whether the axis is drawn from the node to the row's bottom (there is a node below).</summary>
    public static readonly StyledProperty<bool> ShowBottomLineProperty =
        AvaloniaProperty.Register<TimelineGutter, bool>(nameof(ShowBottomLine));

    private ItemsControl? owner;
    private Control? container;
    private INotifyCollectionChanged? ownerItems;
    private TurnCardViewModel? turn;
    private bool attached;
    private bool hooked;

    /// <summary>The thread this gutter was hooked on; a language notification from another one is not ours.</summary>
    private int hookedThread;
    private bool retryScheduled;
    private int placementRetries;
    private int placementGeneration;
    private (TimelineNodeKind Kind, TimelineNodeState State, bool ShowNode)? appliedNode;
    private (TimelineNodeKind Kind, TimelineNodeState State, bool ShowNode)? appliedName;

    public TimelineNodeKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public TimelineRowRole Role
    {
        get => GetValue(RoleProperty);
        set => SetValue(RoleProperty, value);
    }

    public string? ToolName
    {
        get => GetValue(ToolNameProperty);
        set => SetValue(ToolNameProperty, value);
    }

    public bool IsRunning
    {
        get => GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    public bool IsFailing
    {
        get => GetValue(IsFailingProperty);
        set => SetValue(IsFailingProperty, value);
    }

    public bool ShowNode
    {
        get => GetValue(ShowNodeProperty);
        private set => SetValue(ShowNodeProperty, value);
    }

    public bool ShowTopLine
    {
        get => GetValue(ShowTopLineProperty);
        private set => SetValue(ShowTopLineProperty, value);
    }

    public bool ShowBottomLine
    {
        get => GetValue(ShowBottomLineProperty);
        private set => SetValue(ShowBottomLineProperty, value);
    }

    /// <summary>How many placement retries the current attach has used (bounded by <c>MaxPlacementRetries</c>).</summary>
    internal int PlacementRetriesUsed => placementRetries;

    /// <summary>The node's readable name for the current language; empty while the row draws no node.</summary>
    internal string NodeName => AutomationProperties.GetName(this) ?? string.Empty;

    static TimelineGutter()
    {
        AffectsRender<TimelineGutter>(ShowNodeProperty, ShowTopLineProperty, ShowBottomLineProperty);
    }

    public TimelineGutter()
    {
        Width = Metrics.GutterWidth;
        Focusable = false;
        IsTabStop = false;
        IsHitTestVisible = false;

        // The brushes and the icon are looked up when drawn; a new theme variant or a replaced resource (the accent colour)
        // makes them stale.
        ActualThemeVariantChanged += (_, _) => ResourcesStale();
        ResourcesChanged += (_, _) => ResourcesStale();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        Hook();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unhook();
        attached = false;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (attached)
        {
            Unhook();
            Hook();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty
            || change.Property == RoleProperty
            || change.Property == ToolNameProperty
            || change.Property == IsRunningProperty
            || change.Property == IsFailingProperty)
        {
            Recompute();
        }
    }

    /// <summary>Finds the row's collections and its turn, then subscribes to the few things that can move a node.</summary>
    private void Hook()
    {
        if (hooked)
        {
            return;
        }

        hooked = true;
        hookedThread = Environment.CurrentManagedThreadId;

        // The motion gate and the language may have changed while this control was detached (and so unsubscribed), so the
        // first apply after an attach is never skipped.
        appliedNode = null;
        appliedName = null;
        ResolvePlacement();
        Subscribe();
        TreeMotion.Changed += OnMotionChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        Recompute();
    }

    /// <summary>
    /// Subscribes the collections that can move this row, plus its turn. Safe to call again after a re-resolve: the current
    /// subscriptions are dropped first. A block row's ItemsControl already <em>is</em> the turn's block list, so only the
    /// header and footnote rows need the turn's own collection too.
    /// </summary>
    private void Subscribe()
    {
        UnsubscribeCollections();
        ownerItems = Observable(owner);
        if (ownerItems is not null)
        {
            ownerItems.CollectionChanged += OnCollectionChanged;
        }

        if (turn is not null)
        {
            turn.PropertyChanged += OnTurnChanged;
            if (Role != TimelineRowRole.Block)
            {
                ((INotifyCollectionChanged)turn.Blocks).CollectionChanged += OnCollectionChanged;
            }
        }
    }

    private void UnsubscribeCollections()
    {
        if (ownerItems is not null)
        {
            ownerItems.CollectionChanged -= OnCollectionChanged;
            ownerItems = null;
        }

        if (turn is not null)
        {
            turn.PropertyChanged -= OnTurnChanged;
            ((INotifyCollectionChanged)turn.Blocks).CollectionChanged -= OnCollectionChanged;
        }
    }

    private void Unhook()
    {
        if (!hooked)
        {
            return;
        }

        hooked = false;
        placementGeneration++;
        retryScheduled = false;
        placementRetries = 0;
        UnsubscribeCollections();
        turn = null;
        TreeMotion.Changed -= OnMotionChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        owner = null;
        container = null;
    }

    private static INotifyCollectionChanged? Observable(ItemsControl? control) =>
        (control?.ItemsSource as INotifyCollectionChanged) ?? (control?.Items as INotifyCollectionChanged);

    private void ResolvePlacement()
    {
        owner = this.GetVisualAncestors().OfType<ItemsControl>().FirstOrDefault();
        container = owner is null || DataContext is null ? null : owner.ContainerFromItem(DataContext);
        turn = Role switch
        {
            TimelineRowRole.TurnHeader => DataContext as TurnCardViewModel,
            TimelineRowRole.Footnote => (DataContext as TurnFootnoteViewModel)?.Card,
            TimelineRowRole.Block => (DataContext as TurnBlockViewModel)?.Turn,
            _ => null,
        };
    }

    /// <summary>
    /// A collection this row watches changed. The common case, an append at the end, only moves the collection's previous
    /// last row (its bottom line starts to continue); every other row would recompute to the same flags, so it skips.
    /// For the turn's block list (watched by the header and footnote rows) only the first block matters, because those rows
    /// ask just "is there any visible block". Every other change (insert, remove, replace, move, reset) recomputes.
    /// </summary>
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add
            && e.NewItems is { Count: > 0 } added
            && sender is System.Collections.ICollection collection
            && e.NewStartingIndex >= 0
            && e.NewStartingIndex + added.Count == collection.Count
            && !AppendAffectsThisRow(sender, e.NewStartingIndex))
        {
            return;
        }

        Recompute();
    }

    private bool AppendAffectsThisRow(object? sender, int firstNewIndex)
    {
        int position;
        if (ReferenceEquals(sender, ownerItems) && owner is not null && container is not null)
        {
            position = owner.IndexFromContainer(container);
        }
        else if (turn is not null && ReferenceEquals(sender, turn.Blocks))
        {
            // The header and footnote rows read only whether any block is visible: the first block flips it.
            return firstNewIndex == 0;
        }
        else
        {
            return true;
        }

        // An unresolved row (position < 0) must recompute so it can resolve and retry; the old last row moves by one.
        return position < 0 || position == firstNewIndex - 1;
    }

    /// <summary>Queues one retry for a row whose container was not readable yet; at most <see cref="MaxPlacementRetries"/> per attach.</summary>
    private void SchedulePlacementRetry()
    {
        if (retryScheduled || placementRetries >= MaxPlacementRetries)
        {
            return;
        }

        retryScheduled = true;
        var generation = placementGeneration;
        Dispatcher.UIThread.Post(
            () =>
            {
                // Unhook cannot cancel a posted callback: a callback queued by an earlier attach must not touch this one.
                if (generation != placementGeneration)
                {
                    return;
                }

                retryScheduled = false;
                placementRetries++;
                Recompute();
            },
            DispatcherPriority.Background);
    }

    /// <summary>How often one attach waits for its container before giving up; the fallback is only a last resort.</summary>
    private const int MaxPlacementRetries = 3;

    private void OnTurnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TurnCardViewModel.IsExpanded) or nameof(TurnCardViewModel.HasFootnote))
        {
            Recompute();
        }
    }

    private void OnMotionChanged(object? sender, EventArgs e) => ApplyNode(force: true);

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A gutter of a window whose session is gone still sits in the subscriber list of the process-wide
        // table; re-applying its read-screen name from another thread would touch this control off the dispatcher that owns
        // it.
        if (Environment.CurrentManagedThreadId == hookedThread)
        {
            ApplyName(force: true);
        }
    }

    /// <summary>How many times any gutter recomputed its place (all instances, UI thread only); the append cost.</summary>
    internal static long RecomputeCount { get; private set; }

    /// <summary>Recomputes the row's place and repaints only the parts that changed.</summary>
    private void Recompute()
    {
        RecomputeCount++;
        if (!hooked)
        {
            return;
        }

        if (!PlacementResolved())
        {
            // The container can be missing when the row attaches (a recycled panel whose items were replaced, a lazy
            // container): resolve once more and re-subscribe before falling back to "node with no line". The old
            // subscriptions go first, while the old turn is still known, so none of them outlives the re-resolve.
            UnsubscribeCollections();
            ResolvePlacement();
            Subscribe();
            if (!PlacementResolved())
            {
                // A virtualizing panel realizes the container during its own layout pass, after this control attached; the
                // generator mapping may only be readable once that pass is over. Retry on the dispatcher a bounded number of
                // times so a recycled row never keeps the fallback flags until some unrelated change happens.
                SchedulePlacementRetry();
            }
        }
        else
        {
            placementRetries = 0;
        }

        var layout = ComputeLayout();
        ShowNode = layout.ShowNode;
        ShowTopLine = layout.ShowTopLine;
        ShowBottomLine = layout.ShowBottomLine;
        ApplyNode(force: false);
        ApplyName(force: false);
    }

    /// <summary>Whether this row's container is realized and mapped in the list, so its position is known.</summary>
    private bool PlacementResolved() => owner is not null && container is not null && owner.IndexFromContainer(container) >= 0;

    private TimelineRowLayout ComputeLayout()
    {
        if (owner is null || container is null || !PlacementResolved())
        {
            // The row is not in a realized container: draw the node and no line rather than guess a direction.
            return new TimelineRowLayout(true, false, false);
        }

        // The owning ItemsControl is the round this row belongs to, so the index and the count are the row's place inside
        // its own round and the axis is continuous there by construction.
        var index = owner.IndexFromContainer(container);
        return TimelineLayout.Row(index, owner.ItemCount, Role != TimelineRowRole.TurnHeader);
    }

    private (TimelineNodeKind Kind, TimelineNodeState State) Effective()
    {
        var kind = Kind == TimelineNodeKind.Tool && !string.IsNullOrEmpty(ToolName)
            ? TimelineNodeMap.KindForTool(ToolName)
            : Kind;
        var state = TimelineNodeMap.StateOf(kind, IsRunning, IsFailing);
        return (TimelineNodeMap.EffectiveKind(kind, state), state);
    }

    /// <summary>
    /// Repaints the node. Skipped when neither the effective kind, the state nor the node's visibility changed since the last
    /// paint: a collection change or a turn property that left the node alone costs no redraw. Only the motion gate (which is
    /// not part of that key) forces a repaint.
    /// </summary>
    private void ApplyNode(bool force)
    {
        var (kind, state) = Effective();
        if (!force && appliedNode == (kind, state, ShowNode))
        {
            return;
        }

        appliedNode = (kind, state, ShowNode);
        InvalidateVisual();
        UpdateClock(kind, state);
    }

    private void ResourcesStale()
    {
        iconKey = null;
        icon = null;
        lineBrush = null;
        InvalidateVisual();
    }

    private enum Motion
    {
        None,

        /// <summary>A running node's icon breathes (opacity 1 → 0.3 → 1 over 1.2 s).</summary>
        Pulse,

        /// <summary>The slow halo of a running reasoning stretch.</summary>
        Thinking,

        /// <summary>Ripples outward from a node that waits for the user (a pending question or approval).</summary>
        Waiting,

        /// <summary>The waiting ring with motion reduced: it does not move but stays as a still outline.</summary>
        Still,
    }

    /// <summary>
    /// What moves on this node. The ring is the second signal besides the pulse: a running reasoning stretch breathes a slow
    /// halo, and a node that waits for the user sends ripples outward. With motion reduced the waiting ring stays as a still
    /// outline, so the state reads without animation.
    /// </summary>
    private Motion MotionOf(TimelineNodeKind kind, TimelineNodeState state)
    {
        if (!ShowNode || state != TimelineNodeState.Running)
        {
            return Motion.None;
        }

        var animated = !TreeMotion.ReducedMotion;
        if (kind is TimelineNodeKind.Approval or TimelineNodeKind.Notice)
        {
            return animated ? Motion.Waiting : Motion.Still;
        }

        if (kind == TimelineNodeKind.Thinking)
        {
            return animated ? Motion.Thinking : Motion.None;
        }

        return animated ? Motion.Pulse : Motion.None;
    }

    private static bool Moves(Motion motion) => motion is not (Motion.None or Motion.Still);

    /// <summary>Asks for frames while something on this node moves and the control is on screen; stops by itself otherwise.</summary>
    private void UpdateClock(TimelineNodeKind kind, TimelineNodeState state)
    {
        if (ticking || !attached || !Moves(MotionOf(kind, state)))
        {
            return;
        }

        RequestFrame();
    }

    private void RequestFrame()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            ticking = false;
            return;
        }

        ticking = true;
        top.RequestAnimationFrame(frame ??= OnFrame);
    }

    private void OnFrame(TimeSpan time)
    {
        var (kind, state) = Effective();
        if (!attached || !Moves(MotionOf(kind, state)))
        {
            ticking = false;
            return;
        }

        InvalidateVisual();
        RequestFrame();
    }

    protected override Size MeasureOverride(Size availableSize) => new(Metrics.GutterWidth, Metrics.NodeTopGap + Metrics.NodeSize);

    /// <summary>The sizes of the axis, from <c>Tokens.axaml</c> (the one place that states them), read once.</summary>
    private static class Metrics
    {
        public static readonly double GutterWidth = Read("TimelineGutterWidth", 26);
        public static readonly double NodeSize = Read("TimelineNodeSize", 14);
        public static readonly double NodeTopGap = Read("TimelineNodeTopGap", 6);
        public static readonly double AxisWidth = Read("TimelineAxisWidth", 1);

        /// <summary>The axis' left edge inside the gutter: half a node minus half the axis, the node's own centre.</summary>
        public static readonly double AxisLeft = (NodeSize - AxisWidth) / 2;

        private static double Read(string key, double fallback) =>
            Application.Current is { } app && app.TryGetResource(key, null, out var value) && value is double number ? number : fallback;
    }

    private const double IconGrid = 24;
    private static readonly long Origin = System.Diagnostics.Stopwatch.GetTimestamp();

    private static double Phase(double seconds) =>
        System.Diagnostics.Stopwatch.GetElapsedTime(Origin).TotalSeconds / seconds % 1;

    private static double SineInOut(double t) => -(Math.Cos(Math.PI * t) - 1) / 2;

    /// <summary>0 → 1 → 0 across one cycle (the keyframes at 0%, 50% and 100% after the easing has moved the time).</summary>
    private static double Triangle(double f) => f < 0.5 ? f * 2 : (1 - f) * 2;

    private IBrush? lineBrush;
    private Geometry? icon;
    private string? iconKey;
    private Action<TimeSpan>? frame;
    private bool ticking;

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    public override void Render(DrawingContext context)
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        // Layout rounding puts the half pixel of the axis on the column to its right (6.5 -> 7), as the border it replaces was.
        double Snap(double value) => Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale;

        var node = Metrics.NodeSize;
        var nodeTop = Snap(Metrics.NodeTopGap);
        var nodeBottom = Snap(Metrics.NodeTopGap + node);
        var left = Snap(Metrics.AxisLeft);
        var width = Math.Max(Snap(Metrics.AxisWidth), 1 / scale);
        var height = Bounds.Height;

        if (ShowTopLine || ShowBottomLine)
        {
            if ((lineBrush ??= Brush("BorderBrush")) is { } brush)
            {
                if (!ShowNode && ShowTopLine && ShowBottomLine)
                {
                    // The header has no node: its icon slot becomes one more segment, so the axis never opens a gap there.
                    context.DrawRectangle(brush, null, new Rect(left, 0, width, height));
                }
                else
                {
                    if (ShowTopLine)
                    {
                        context.DrawRectangle(brush, null, new Rect(left, 0, width, nodeTop));
                    }

                    if (ShowBottomLine && height > nodeBottom)
                    {
                        context.DrawRectangle(brush, null, new Rect(left, nodeBottom, width, height - nodeBottom));
                    }
                }
            }
        }

        if (!ShowNode)
        {
            return;
        }

        var (kind, state) = Effective();
        var motion = MotionOf(kind, state);
        if (motion is Motion.Thinking or Motion.Waiting or Motion.Still)
        {
            DrawRing(context, motion, new Point(node / 2, Metrics.NodeTopGap + node / 2));
        }

        var filled = state is TimelineNodeState.Running or TimelineNodeState.Failed or TimelineNodeState.Warning;
        var key = TimelineNodeMap.IconKey(kind, filled);
        if (iconKey != key)
        {
            iconKey = key;
            icon = this.TryFindResource(key, out var value) && value is Geometry geometry ? geometry : null;
        }

        var foreground = Brush(state switch
        {
            TimelineNodeState.Running => "BrandActionBrush",
            TimelineNodeState.Failed => "StatusErrorBrush",
            TimelineNodeState.Warning => "StatusWarningBrush",
            TimelineNodeState.Done => "StatusSuccessBrush",
            _ => "TextMutedBrush",
        });
        if (icon is null || foreground is null)
        {
            return;
        }

        // The icon's 24x24 grid scaled into the node box, as FluentIcon's viewbox does.
        var place = Matrix.CreateScale(node / IconGrid, node / IconGrid) * Matrix.CreateTranslation(0, Metrics.NodeTopGap);
        if (motion == Motion.Pulse)
        {
            using (context.PushOpacity(1 - 0.7 * Triangle(SineInOut(Phase(1.2)))))
            using (context.PushTransform(place))
            {
                context.DrawGeometry(foreground, null, icon);
            }
        }
        else
        {
            using (context.PushTransform(place))
            {
                context.DrawGeometry(foreground, null, icon);
            }
        }
    }

    private void DrawRing(DrawingContext context, Motion motion, Point centre)
    {
        double size;
        double opacity;
        switch (motion)
        {
            case Motion.Thinking:
            {
                var t = Triangle(SineInOut(Phase(1.8)));
                size = 14 + 7 * t;
                opacity = 0.15 + 0.7 * t;
                break;
            }

            case Motion.Waiting:
            {
                var f = 1 - Math.Pow(1 - Phase(1.4), 3);
                size = 11.2 + (26.6 - 11.2) * f;
                opacity = 0.9 * (1 - f);
                break;
            }

            default:
                size = 18.9;
                opacity = 0.8;
                break;
        }

        if (Brush("BrandInteractiveBrush") is not { } brush)
        {
            return;
        }

        // A stroke is drawn inside the ellipse's box, like the Ellipse shape this replaces.
        const double Stroke = 1.5;
        var radius = (size - Stroke) / 2;
        using (context.PushOpacity(opacity))
        {
            context.DrawEllipse(null, new Pen(brush, Stroke), centre, radius, radius);
        }
    }

    /// <summary>Renames the node; skipped while the name's inputs are unchanged, forced by a language switch.</summary>
    private void ApplyName(bool force)
    {
        var (kind, state) = Effective();
        if (!force && appliedName == (kind, state, ShowNode))
        {
            return;
        }

        appliedName = (kind, state, ShowNode);
        AutomationProperties.SetName(this, ShowNode ? TimelineStrings.Describe(kind, state) : string.Empty);
    }
}