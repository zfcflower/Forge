using Avalonia.Controls.Documents;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Forge.App.Editing;
using Forge.App.Review;

namespace Forge.App.Changes;

/// <summary>Brushes of the diff editor, resolved from Forge's token table for the current theme variant.</summary>
internal sealed class DiffPalette
{
    public IBrush Added { get; private set; } = Brushes.Transparent;

    public IBrush Removed { get; private set; } = Brushes.Transparent;

    public IBrush AddedStripe { get; private set; } = Brushes.Green;

    public IBrush RemovedStripe { get; private set; } = Brushes.Red;

    public IBrush Spacer { get; private set; } = Brushes.Transparent;

    public IBrush GutterText { get; private set; } = Brushes.Gray;

    public IPen SpacerPen { get; private set; } = new Pen(Brushes.Gray, 1);

    /// <summary>Bumped on every refresh, so caches keyed by brush can drop themselves.</summary>
    public int Version { get; private set; }

    public void Refresh(StyledElement scope)
    {
        var variant = scope is Control control ? control.ActualThemeVariant : ThemeVariant.Default;
        Added = Get(scope, variant, "DiffAddBrush", Added);
        Removed = Get(scope, variant, "DiffRemoveBrush", Removed);
        AddedStripe = Get(scope, variant, "SuccessBrush", AddedStripe);
        RemovedStripe = Get(scope, variant, "DangerBrush", RemovedStripe);
        Spacer = Get(scope, variant, "SurfaceMutedBrush", Spacer);
        GutterText = Get(scope, variant, "TextMutedBrush", GutterText);
        var line = Get(scope, variant, "BorderStrongBrush", Brushes.Gray);
        SpacerPen = new Pen(line, 1);
        Version++;
    }

    public IBrush? BackgroundOf(DiffRowKind kind) => kind switch
    {
        DiffRowKind.Added => Added,
        DiffRowKind.Removed => Removed,
        DiffRowKind.Spacer => Spacer,
        _ => null,
    };

    private static IBrush Get(StyledElement scope, ThemeVariant? variant, string key, IBrush fallback) =>
        EditorOptionsApplier.Brush(scope, variant, key) ?? fallback;

    /// <summary>Diagonal hatching over a spacer row.</summary>
    public void DrawHatch(DrawingContext context, Rect rect)
    {
        using var clip = context.PushClip(rect);
        const double step = 6;
        for (var x = rect.Left - rect.Height; x < rect.Right; x += step)
        {
            context.DrawLine(SpacerPen, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Top));
        }
    }
}

/// <summary>Row backgrounds: green for added, red for removed, hatched for the spacers of a split side.</summary>
internal sealed class DiffBackgroundRenderer(Func<DiffRowMap> map, DiffPalette palette) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid)
        {
            return;
        }

        var rows = map();
        var width = Math.Max(textView.Bounds.Width, 1);
        var scroll = textView.ScrollOffset.Y;
        foreach (var line in textView.VisualLines)
        {
            var number = line.FirstDocumentLine.LineNumber;
            if (!rows.Contains(number))
            {
                continue;
            }

            var kind = rows.ForLine(number).Kind;
            if (palette.BackgroundOf(kind) is not { } brush)
            {
                continue;
            }

            var rect = new Rect(0, line.VisualTop - scroll, width, line.Height);
            drawingContext.DrawRectangle(brush, null, rect);
            if (kind == DiffRowKind.Spacer)
            {
                palette.DrawHatch(drawingContext, rect);
            }
        }
    }
}

/// <summary>
/// Which number a gutter shows: Inline = new for context/added and old for removed; Old/New = that side only (split).
/// </summary>
internal enum DiffGutterSide
{
    Inline,
    Old,
    New,
}

/// <summary>
/// The gutter of the diff editor: line numbers of the shown side, the row tint and the 3px stripe of changed rows. Clicking
/// or dragging selects whole lines, which is what the review overlay turns into a comment range.
/// </summary>
internal sealed class DiffLineNumberMargin : AbstractMargin, IReviewGutterMargin
{
    private const double Padding = 8;
    private const double Stripe = 3;
    private readonly DiffPalette palette;
    private readonly Dictionary<int, FormattedText> cache = [];
    private DiffRowMap map = DiffRowMap.Empty;
    private DiffGutterSide side;
    private int digits = 2;
    private int cacheVersion = -1;
    private double glyphWidth = 8;
    private TextArea? textArea;
    private int dragAnchorLine;
    private bool dragging;

    public DiffLineNumberMargin(DiffPalette palette, DiffGutterSide side)
    {
        this.palette = palette;
        this.side = side;
    }

    public void SetMap(DiffRowMap value, DiffGutterSide newSide)
    {
        map = value;
        side = newSide;
        var max = 0;
        for (var i = 0; i < map.Count; i++)
        {
            var row = map[i];
            max = Math.Max(max, Math.Max(row.OldLine, row.NewLine));
        }

        digits = Math.Max(2, max.ToString(CultureInfo.InvariantCulture).Length);
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Swaps in a map in which only <paramref name="changed"/> rows are new; the number width only ever grows.</summary>
    public void UpdateMap(DiffRowMap value, ReadOnlySpan<DiffRow> changed)
    {
        map = value;
        var max = 0;
        foreach (var row in changed)
        {
            max = Math.Max(max, Math.Max(row.OldLine, row.NewLine));
        }

        var needed = Math.Max(2, max.ToString(CultureInfo.InvariantCulture).Length);
        if (needed > digits)
        {
            digits = needed;
            InvalidateMeasure();
        }

        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var typeface = new Typeface(TextView?.GetValue(TextElement.FontFamilyProperty) ?? FontFamily.Default);
        var size = TextView?.GetValue(TextElement.FontSizeProperty) ?? 12;
        glyphWidth = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black).Width;
        return new Size(Stripe + digits * glyphWidth + 2 * Padding, 0);
    }

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnViewChanged;
            oldTextView.ScrollOffsetChanged -= OnViewChanged;
        }

        textArea = newTextView?.GetService(typeof(TextArea)) as TextArea;
        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnViewChanged;
            newTextView.ScrollOffsetChanged += OnViewChanged;
        }

        base.OnTextViewChanged(oldTextView, newTextView);
    }

    private void OnViewChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var textView = TextView;
        if (textView is null || !textView.VisualLinesValid)
        {
            return;
        }

        if (cacheVersion != palette.Version)
        {
            cache.Clear();
            cacheVersion = palette.Version;
        }

        var typeface = new Typeface(textView.GetValue(TextElement.FontFamilyProperty));
        var size = textView.GetValue(TextElement.FontSizeProperty);
        var scroll = textView.ScrollOffset.Y;
        var width = Bounds.Width;
        foreach (var line in textView.VisualLines)
        {
            var number = line.FirstDocumentLine.LineNumber;
            if (!map.Contains(number))
            {
                continue;
            }

            var row = map.ForLine(number);
            if (row.IsPlaceholder)
            {
                continue;
            }

            var top = line.VisualTop - scroll;
            var rect = new Rect(0, top, width, line.Height);
            if (palette.BackgroundOf(row.Kind) is { } tint)
            {
                context.DrawRectangle(tint, null, rect);
                if (row.Kind == DiffRowKind.Spacer)
                {
                    palette.DrawHatch(context, rect);
                }
            }

            if (row.Kind == DiffRowKind.Added)
            {
                context.DrawRectangle(palette.AddedStripe, null, new Rect(0, top, Stripe, line.Height));
            }
            else if (row.Kind == DiffRowKind.Removed)
            {
                context.DrawRectangle(palette.RemovedStripe, null, new Rect(0, top, Stripe, line.Height));
            }

            var value = NumberOf(row);
            if (value <= 0)
            {
                continue;
            }

            if (!cache.TryGetValue(value, out var text))
            {
                if (cache.Count > 4096)
                {
                    cache.Clear();
                }

                text = new FormattedText(
                    value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, size, palette.GutterText);
                cache[value] = text;
            }

            context.DrawText(text, new Point(width - Padding - text.Width, top));
        }
    }

    private int NumberOf(in DiffRow row) => side switch
    {
        DiffGutterSide.Old => row.OldLine,
        DiffGutterSide.New => row.NewLine,
        _ => row.Kind == DiffRowKind.Removed ? row.OldLine : row.NewLine,
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (textArea is null || TextView is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var line = LineAt(e);
        if (line is null)
        {
            return;
        }

        dragAnchorLine = line.LineNumber;
        dragging = true;
        e.Pointer.Capture(this);
        Select(dragAnchorLine, dragAnchorLine);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragging && LineAt(e) is { } line)
        {
            Select(dragAnchorLine, line.LineNumber);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragging)
        {
            dragging = false;
            e.Pointer.Capture(null);
        }
    }

    private AvaloniaEdit.Document.DocumentLine? LineAt(PointerEventArgs e)
    {
        var textView = TextView!;
        var y = e.GetPosition(textView).Y + textView.ScrollOffset.Y;
        return textView.GetDocumentLineByVisualTop(y);
    }

    private void Select(int a, int b)
    {
        var doc = textArea!.Document;
        if (doc is null)
        {
            return;
        }

        var first = doc.GetLineByNumber(Math.Min(a, b));
        var last = doc.GetLineByNumber(Math.Max(a, b));
        textArea.Selection = Selection.Create(textArea, first.Offset, last.EndOffset);
        textArea.Caret.Offset = last.EndOffset;
    }
}

/// <summary>Turns each placeholder line (one space) into an invisible inline object that reserves the row's height.</summary>
internal sealed class DiffPlaceholderGenerator : VisualLineElementGenerator
{
    public const double HeaderHeight = 36;
    public const double FoldHeight = 28;
    public const double MinEmbedHeight = 24;

    private DiffRowMap map = DiffRowMap.Empty;
    private double[] embedHeights = [];

    /// <summary>
    /// Height the text line adds below the inline object (the font's descent): the object is made that much shorter so the
    /// whole line is exactly the row height. Measured by the view from the first constructed placeholder line.
    /// </summary>
    public double Compensation { get; set; }

    public void Configure(DiffRowMap value, double[] embed)
    {
        map = value;
        embedHeights = embed;
    }

    public static double HeightOf(in DiffRow row, double[] embed) => row.Kind switch
    {
        DiffRowKind.FileHeader => HeaderHeight,
        DiffRowKind.BlockBar => DiffBlockBar.BarHeight,
        DiffRowKind.Fold => FoldHeight,
        DiffRowKind.Embed => (uint)row.File < (uint)embed.Length ? Math.Max(embed[row.File], MinEmbedHeight) : MinEmbedHeight,
        _ => 0,
    };

    public override int GetFirstInterestedOffset(int startOffset)
    {
        var document = CurrentContext.Document;
        var line = document.GetLineByOffset(startOffset);
        if (line.Offset >= startOffset && map.Contains(line.LineNumber) && map.ForLine(line.LineNumber).IsPlaceholder)
        {
            return line.Offset;
        }

        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var line = CurrentContext.Document.GetLineByOffset(offset);
        if (!map.Contains(line.LineNumber))
        {
            return null;
        }

        var row = map.ForLine(line.LineNumber);
        if (!row.IsPlaceholder)
        {
            return null;
        }

        var spacer = new Border { Width = 1, Height = Math.Max(1, Math.Ceiling(HeightOf(row, embedHeights) - Compensation)), IsHitTestVisible = false };
        return new InlineObjectElement(Math.Max(1, line.Length), spacer);
    }
}
