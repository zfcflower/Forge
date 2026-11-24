using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Forge.App.Views;

/// <summary>One run of selectable text inside a card: a <see cref="SelectableTextBlock"/> is the only implementation today.</summary>
internal interface ISelectableBlock
{
    string? Text { get; }

    string SelectedText { get; }

    int SelectionStart { get; set; }

    int SelectionEnd { get; set; }

    void SelectAll();

    void ClearSelection();

    /// <summary>The character index nearest to <paramref name="point"/>, in the block's own coordinates.</summary>
    int IndexAt(Point point);
}

/// <summary>
/// Point-to-character for a selectable block. <see cref="TextLayout"/> has no point hit test of its own any more, so the line
/// under the point is found by accumulating line heights and the character by asking that line for the hit at an x offset.
/// </summary>
internal static class SelectableTextHit
{
    public static int IndexAt(SelectableTextBlock block, Point point)
    {
        var length = block.Text?.Length ?? 0;
        if (block.TextLayout is not { } layout || layout.TextLines.Count == 0)
        {
            return 0;
        }

        var top = 0.0;
        TextLine? hitLine = null;
        foreach (var line in layout.TextLines)
        {
            if (point.Y < top + line.Height)
            {
                hitLine = line;
                break;
            }

            top += line.Height;
        }

        if (hitLine is null)
        {
            hitLine = layout.TextLines[^1];
        }

        var hit = hitLine.GetCharacterHitFromDistance(point.X - hitLine.Start);
        return Math.Clamp(hit.FirstCharacterIndex + hit.TrailingLength, 0, length);
    }
}

/// <summary>
/// The block range one card selection covers: the block the press anchored in and the one the pointer is over, each with a
/// character offset. The arithmetic (which block is fully inside, which two are half inside) lives here so it can be checked
/// without a pointer, and so a move repaints only the blocks whose share actually changed.
/// </summary>
internal readonly record struct CardSelection(int AnchorBlock, int AnchorChar, int HeadBlock, int HeadChar)
{
    /// <summary>The range in reading order: first block and its start offset, last block and its end offset.</summary>
    public (int First, int FirstChar, int Last, int LastChar) Ordered()
    {
        if (AnchorBlock < HeadBlock)
        {
            return (AnchorBlock, AnchorChar, HeadBlock, HeadChar);
        }

        if (AnchorBlock > HeadBlock)
        {
            return (HeadBlock, HeadChar, AnchorBlock, AnchorChar);
        }

        return (AnchorBlock, Math.Min(AnchorChar, HeadChar), AnchorBlock, Math.Max(AnchorChar, HeadChar));
    }

    /// <summary>What one block contributes: a start and an end, or (-1, -1) when it is outside the range.</summary>
    public (int Start, int End) For(int index, int length)
    {
        var (first, firstChar, last, lastChar) = Ordered();
        if (index < first || index > last)
        {
            return (-1, -1);
        }

        var start = index == first ? Math.Clamp(firstChar, 0, length) : 0;
        var end = index == last ? Math.Clamp(lastChar, 0, length) : length;
        return end <= start ? (start, start) : (start, end);
    }
}

/// <summary>
/// A message card that selects as one run of text: a drag started in any paragraph keeps going through lists, quotes and code
/// blocks, and Ctrl+C copies the card's part of it. Cross-card selection is deliberately not supported — a press in another
/// card clears this one — because the transcript is a list of cards and a selection that silently spans them is not what a
/// reader wants when they drag to scroll.
/// <para>
/// The press itself is left alone so a plain click still reaches the block (word select) and a link still opens; the card takes
/// the pointer over on the first move past <see cref="DragThreshold"/>, clears the block's own selection and drives every block
/// in the range from then on. The range lives on the card as (block, character) pairs, so a block re-created after the row was
/// recycled is painted again when the card is re-attached.
/// </para>
/// </summary>
internal sealed class SelectableCard : Border
{
    private const double DragThreshold = 3;
    private const double EdgeStep = 16;

    /// <summary>The one card holding a selection; a press anywhere else clears it.</summary>
    private static SelectableCard? owner;

    private readonly List<ISelectableBlock> range = [];

    /// <summary>Per-block applied selection, so a move inside one block does not repaint its neighbours.</summary>
    private readonly List<int> appliedStart = [];
    private readonly List<int> appliedEnd = [];
    private readonly Stack<Visual> walk = new();

    private ISelectableBlock? pressed;
    private IPointer? pointer;
    private Point pressedAt;
    private bool dragging;
    private bool hasSelection;
    private CardSelection selection;
    private ScrollViewer? scroller;
    private DispatcherTimer? edgeTimer;
    private int edgeDirection;
    private double pointerX;

    public SelectableCard()
    {
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }

    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>Whether this card currently holds a selection; the copy command and the clearance rule both read it.</summary>
    internal bool HasSelection => hasSelection;

    /// <summary>
    /// Takes over as the one card with a selection: the previous holder is cleared, and the press that started it is recorded
    /// so a drag can begin from the character under the pointer.
    /// </summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (owner is { } other && !ReferenceEquals(other, this))
        {
            other.Clear();
        }

        pressedAt = e.GetPosition(this);
        pressed = BlockAt(pressedAt);
        pointer = e.Pointer;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (pressed is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (!dragging)
        {
            if (Math.Abs(point.X - pressedAt.X) < DragThreshold && Math.Abs(point.Y - pressedAt.Y) < DragThreshold)
            {
                return;
            }

            BeginDrag();
        }

        // From here the card owns the gesture: a block that also extended its own selection would paint a second range.
        e.Handled = true;
        pointerX = point.X;
        Extend(point);
        TrackEdgeScroll(e);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!dragging)
        {
            pressed = null;
            return;
        }

        dragging = false;
        pressed = null;
        StopEdgeScroll();
        pointer?.Capture(null);
        pointer = null;
        e.Handled = true;
    }

    private void BeginDrag()
    {
        Collect();
        if (range.Count == 0 || pressed is not { } block)
        {
            return;
        }

        owner = this;
        hasSelection = true;
        dragging = true;
        pointer?.Capture(this);

        var index = IndexOf(block);
        var character = block.IndexAt(Translate(pressedAt, block));
        selection = new CardSelection(index, character, index, character);
        Apply();
    }

    private void Extend(Point point)
    {
        if (range.Count == 0)
        {
            return;
        }

        var block = BlockAt(point);
        if (block is null)
        {
            // The pointer left the text (padding, or beyond the card): hold the nearest block and let the edge scroll carry it.
            if (point.Y < 0)
            {
                selection = selection with { HeadBlock = 0, HeadChar = 0 };
            }
            else if (point.Y > Bounds.Height)
            {
                selection = selection with { HeadBlock = range.Count - 1, HeadChar = range[^1].Text?.Length ?? 0 };
            }

            Apply();
            return;
        }

        selection = selection with { HeadBlock = IndexOf(block), HeadChar = block.IndexAt(Translate(point, block)) };
        Apply();
    }

    private void Apply()
    {
        for (var index = 0; index < range.Count; index++)
        {
            var block = range[index];
            var (start, end) = selection.For(index, block.Text?.Length ?? 0);
            if (appliedStart[index] == start && appliedEnd[index] == end)
            {
                continue;
            }

            // An empty range is not a selection: ClearSelection keeps the state the copy command reads consistent.
            if (start < 0 || end <= start)
            {
                if (appliedStart[index] >= 0)
                {
                    block.ClearSelection();
                }

                appliedStart[index] = start;
                appliedEnd[index] = end;
                continue;
            }

            appliedStart[index] = start;
            appliedEnd[index] = end;
            block.SelectionStart = start;
            block.SelectionEnd = end;
        }
    }

    /// <summary>Drops this card's selection and forgets it as the holder.</summary>
    internal void Clear()
    {
        if (!hasSelection && owner is null)
        {
            return;
        }

        hasSelection = false;
        dragging = false;
        StopEdgeScroll();
        for (var index = 0; index < range.Count; index++)
        {
            if (appliedStart[index] >= 0)
            {
                range[index].ClearSelection();
                appliedStart[index] = -1;
                appliedEnd[index] = -1;
            }
        }

        if (ReferenceEquals(owner, this))
        {
            owner = null;
        }
    }

    /// <summary>The selected text, blocks joined in reading order; code blocks are fenced so a copied snippet stays code.</summary>
    internal string? SelectedText()
    {
        if (!hasSelection || range.Count == 0)
        {
            return null;
        }

        var (first, _, last, _) = selection.Ordered();
        var builder = new System.Text.StringBuilder();
        for (var index = first; index <= last; index++)
        {
            var block = range[index];
            var part = index > first && index < last ? block.Text ?? string.Empty : block.SelectedText;
            if (part.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            if (block is MarkdownCodeTextBlock)
            {
                builder.Append("```\n").Append(part).Append("\n```");
            }
            else
            {
                builder.Append(part);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            top.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        }

        // A recycled row rebuilds the blocks: paint the selection the card still holds onto the new ones.
        if (hasSelection && ReferenceEquals(owner, this))
        {
            Collect();
            Apply();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            top.RemoveHandler(KeyDownEvent, OnKeyDown);
        }

        StopEdgeScroll();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!ReferenceEquals(owner, this) || e.KeyModifiers != KeyModifiers.Control)
        {
            return;
        }

        if (e.Key is Key.C or Key.Insert)
        {
            if (SelectedText() is not { Length: > 0 } text)
            {
                return;
            }

            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                _ = clipboard.SetTextAsync(text);
            }

            e.Handled = true;
        }
        else if (e.Key is Key.Escape)
        {
            Clear();
            e.Handled = true;
        }
    }

    /// <summary>Keeps the gesture alive past the viewport edge: drag out of the list and the transcript scrolls under the pointer.</summary>
    private void TrackEdgeScroll(PointerEventArgs e)
    {
        scroller ??= this.FindAncestorOfType<ScrollViewer>();
        if (scroller is null)
        {
            return;
        }

        var point = e.GetPosition(scroller);
        var direction = point.Y < 0 ? -1 : point.Y > scroller.Viewport.Height ? 1 : 0;
        if (direction == 0)
        {
            StopEdgeScroll();
            return;
        }

        edgeDirection = direction;
        if (edgeTimer is not null)
        {
            return;
        }

        edgeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, OnEdgeTick);
        edgeTimer.Start();
    }

    private void OnEdgeTick(object? sender, EventArgs e)
    {
        if (!dragging || scroller is null)
        {
            StopEdgeScroll();
            return;
        }

        var offset = scroller.Offset;
        var next = offset.WithY(Math.Max(0, offset.Y + (edgeDirection * EdgeStep)));
        if (Math.Abs(next.Y - offset.Y) < 0.5)
        {
            StopEdgeScroll();
            return;
        }

        scroller.Offset = next;
        var edgeY = edgeDirection < 0 ? 0.5 : scroller.Viewport.Height - 0.5;
        var inCard = scroller.TranslatePoint(new Point(0, edgeY), this);
        Extend(inCard is { } point ? new Point(pointerX, point.Y) : new Point(pointerX, edgeDirection < 0 ? 0 : Bounds.Height));
    }

    private void StopEdgeScroll()
    {
        if (edgeTimer is null)
        {
            return;
        }

        edgeTimer.Stop();
        edgeTimer = null;
    }

    /// <summary>Every selectable block of the card, in reading order, into the reused list.</summary>
    private void Collect()
    {
        range.Clear();
        appliedStart.Clear();
        appliedEnd.Clear();
        walk.Clear();
        for (var index = VisualChildren.Count - 1; index >= 0; index--)
        {
            walk.Push(VisualChildren[index]);
        }

        while (walk.Count > 0)
        {
            var visual = walk.Pop();
            if (visual is ISelectableBlock block)
            {
                range.Add(block);
                appliedStart.Add(-1);
                appliedEnd.Add(-1);
            }
            else
            {
                foreach (var child in visual.GetVisualChildren())
                {
                    walk.Push(child);
                }
            }
        }
    }

    /// <summary>The selectable block under a point, or null for padding.</summary>
    private ISelectableBlock? BlockAt(Point point)
    {
        if (this.GetVisualAt(point) is not { } hit)
        {
            return null;
        }

        while (hit is not null && !ReferenceEquals(hit, this))
        {
            if (hit is ISelectableBlock block)
            {
                return block;
            }

            hit = hit.GetVisualParent();
        }

        return null;
    }

    private int IndexOf(ISelectableBlock block)
    {
        for (var index = 0; index < range.Count; index++)
        {
            if (ReferenceEquals(range[index], block))
            {
                return index;
            }
        }

        return -1;
    }

    private static Point Translate(Point point, ISelectableBlock block) =>
        block is Visual visual && visual.TranslatePoint(point, visual) is { } translated ? translated : point;
}

/// <summary>A code block's text: the same card selection, with the hit test its own <see cref="SelectableTextBlock"/> needs.</summary>
internal sealed class MarkdownCodeTextBlock : SelectableTextBlock, ISelectableBlock
{
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public int IndexAt(Point point) => SelectableTextHit.IndexAt(this, point);
}
