using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.App.Workbench.Controls;

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

    /// <summary>One block inside an expanded turn, an item of the turn's block list.</summary>
    Block,

    /// <summary>The turn's footnote, rendered next to the turn's blocks but not one of them.</summary>
    Footnote,
}

/// <summary>
/// The left column of one transcript row: the vertical axis with this row's node on it. The control owns only the geometry
/// decision (it asks <see cref="TimelineLayout"/> and supplies the row's position from the visual tree it is already in);
/// the axis line, the icon size, the colours and the running pulse all live in <c>Views/TimelineStyles.axaml</c>.
/// <para>Nothing here allocates per message: the gutter is part of the row template the list already realizes, the geometry
/// is read from the icon dictionary when the state changes, and the subscriptions are limited to the realized rows (the
/// two collections that can move a node, the turn view model while a turn's expansion changes, the language and the motion
/// gate). The row keeps its position current across append, expand and collapse without rebuilding anything.</para>
/// </summary>
internal sealed class TimelineGutter : TemplatedControl
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

    /// <summary>Whether this row draws its node; the turn header does not, and neither does a row whose node is off-screen by position.</summary>
    public static readonly StyledProperty<bool> ShowNodeProperty =
        AvaloniaProperty.Register<TimelineGutter, bool>(nameof(ShowNode), true);

    /// <summary>Whether the axis is drawn from the row's top to the node (there is a node above).</summary>
    public static readonly StyledProperty<bool> ShowTopLineProperty =
        AvaloniaProperty.Register<TimelineGutter, bool>(nameof(ShowTopLine));

    /// <summary>Whether the axis is drawn from the node to the row's bottom (there is a node below).</summary>
    public static readonly StyledProperty<bool> ShowBottomLineProperty =
        AvaloniaProperty.Register<TimelineGutter, bool>(nameof(ShowBottomLine));

    private FluentIcon? icon;
    private Border? middleLine;
    private ItemsControl? owner;
    private Control? container;
    private ItemsControl? outer;
    private Control? outerContainer;
    private INotifyCollectionChanged? ownerItems;
    private INotifyCollectionChanged? outerItems;
    private TurnCardViewModel? turn;
    private bool attached;
    private bool hooked;
    private bool retryScheduled;
    private int placementRetries;

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

    /// <summary>The node's readable name for the current language; empty for the node-less turn header.</summary>
    internal string NodeName => AutomationProperties.GetName(this) ?? string.Empty;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        icon = e.NameScope.Find<FluentIcon>("PART_Icon");
        middleLine = e.NameScope.Find<Border>("PART_MidLine");
        Recompute();
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

        if (outer is not null && (outerItems = Observable(outer)) is not null)
        {
            outerItems.CollectionChanged += OnCollectionChanged;
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

        if (outerItems is not null)
        {
            outerItems.CollectionChanged -= OnCollectionChanged;
            outerItems = null;
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
        retryScheduled = false;
        placementRetries = 0;
        UnsubscribeCollections();
        turn = null;
        TreeMotion.Changed -= OnMotionChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        owner = null;
        container = null;
        outer = null;
        outerContainer = null;
    }

    private static INotifyCollectionChanged? Observable(ItemsControl? control) =>
        (control?.ItemsSource as INotifyCollectionChanged) ?? (control?.Items as INotifyCollectionChanged);

    private void ResolvePlacement()
    {
        owner = this.GetVisualAncestors().OfType<ItemsControl>().FirstOrDefault();
        container = owner is null || DataContext is null ? null : owner.ContainerFromItem(DataContext);
        turn = Role switch
        {
            TimelineRowRole.TurnHeader or TimelineRowRole.Footnote => DataContext as TurnCardViewModel,
            TimelineRowRole.Block => owner?.DataContext as TurnCardViewModel,
            _ => null,
        };

        if (Role == TimelineRowRole.Block && owner is not null && owner.DataContext is not null)
        {
            outer = owner.GetVisualAncestors().OfType<ItemsControl>().FirstOrDefault();
            outerContainer = outer?.ContainerFromItem(owner.DataContext);
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Recompute();

    /// <summary>Queues one retry for a row whose container was not readable yet; at most <see cref="MaxPlacementRetries"/> per attach.</summary>
    private void SchedulePlacementRetry()
    {
        if (retryScheduled || placementRetries >= MaxPlacementRetries)
        {
            return;
        }

        retryScheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
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

    private void OnMotionChanged(object? sender, EventArgs e) => ApplyNode();

    private void OnLanguageChanged(object? sender, EventArgs e) => ApplyName();

    /// <summary>Recomputes the row's place and repaints only the parts that changed.</summary>
    private void Recompute()
    {
        if (!hooked)
        {
            return;
        }

        if (owner is null || container is null || owner.IndexFromContainer(container) < 0)
        {
            // The container can be missing when the row attaches (a recycled panel whose items were replaced, a lazy
            // container): resolve once more and re-subscribe before falling back to "node with no line".
            ResolvePlacement();
            Subscribe();
            if (owner is null || container is null || owner.IndexFromContainer(container) < 0)
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
        ApplyNode();
        ApplyName();
    }

    private TimelineRowLayout ComputeLayout()
    {
        if (owner is null || container is null)
        {
            // The row is not in a realized container: draw the node and no line rather than guess a direction.
            return new TimelineRowLayout(true, false, false);
        }

        var index = owner.IndexFromContainer(container);
        if (index < 0)
        {
            return new TimelineRowLayout(true, false, false);
        }

        var count = owner.ItemCount;
        var isFirst = index == 0;
        var isLast = index == count - 1;
        switch (Role)
        {
            case TimelineRowRole.TurnHeader:
                return TimelineLayout.TurnHeader(isFirst, isLast, VisibleBlocks, turn?.HasFootnote == true);
            case TimelineRowRole.Footnote:
                return TimelineLayout.TurnFootnote(VisibleBlocks, isFirst, isLast);
            case TimelineRowRole.Block:
                var (turnFirst, turnLast) = TurnPosition();
                return TimelineLayout.TurnBlock(index, count, turnFirst, turnLast, turn?.HasFootnote == true);
            default:
                return TimelineLayout.Item(isFirst, isLast);
        }
    }

    private int VisibleBlocks => turn is { IsExpanded: true } expanded ? expanded.Blocks.Count : 0;

    private (bool First, bool Last) TurnPosition()
    {
        if (outer is null || outerContainer is null)
        {
            return (false, false);
        }

        var index = outer.IndexFromContainer(outerContainer);
        if (index < 0)
        {
            return (false, false);
        }

        return (index == 0, index == outer.ItemCount - 1);
    }

    private (TimelineNodeKind Kind, TimelineNodeState State) Effective()
    {
        var kind = Kind == TimelineNodeKind.Tool && !string.IsNullOrEmpty(ToolName)
            ? TimelineNodeMap.KindForTool(ToolName)
            : Kind;
        var state = TimelineNodeMap.StateOf(kind, IsRunning, IsFailing);
        return (TimelineNodeMap.EffectiveKind(kind, state), state);
    }

    private void ApplyNode()
    {
        if (icon is null)
        {
            return;
        }

        var (kind, state) = Effective();
        var key = TimelineNodeMap.IconKey(kind, state is TimelineNodeState.Running or TimelineNodeState.Failed or TimelineNodeState.Warning);
        icon.Data = this.TryFindResource(key, out var value) && value is Geometry geometry ? geometry : null;
        icon.IsVisible = ShowNode;
        icon.Classes.Set("running", state == TimelineNodeState.Running);
        icon.Classes.Set("failed", state == TimelineNodeState.Failed);
        icon.Classes.Set("warning", state == TimelineNodeState.Warning);
        icon.Classes.Set("pulsing", state == TimelineNodeState.Running && !TreeMotion.ReducedMotion);

        // The turn header has no node: its icon slot becomes one more segment, so the axis never opens a gap there.
        middleLine?.SetCurrentValue(IsVisibleProperty, !ShowNode && ShowTopLine && ShowBottomLine);
    }

    private void ApplyName()
    {
        var (kind, state) = Effective();
        AutomationProperties.SetName(this, ShowNode ? TimelineStrings.Describe(kind, state) : string.Empty);
    }
}