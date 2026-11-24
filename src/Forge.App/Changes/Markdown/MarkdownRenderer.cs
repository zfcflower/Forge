using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Forge.App.Logging;
using Forge.App.Services;
using Forge.App.Views;
using ZLogger;

namespace Forge.App.Changes.Markdown;

/// <summary>A selectable text block whose links (web addresses and local files) open on a plain click.</summary>
internal sealed class MarkdownTextBlock : SelectableTextBlock, ISelectableBlock
{
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private readonly List<(int Start, int End, string Url)> links = [];

    /// <summary>Links that come from inline code that merely looks like a path: they only count once the file exists.</summary>
    private readonly HashSet<string> probed = [];

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    internal IReadOnlyList<(int Start, int End, string Url)> Links => links;

    /// <summary>The card's drag maps a point on this block to the character under it.</summary>
    public int IndexAt(Avalonia.Point point) => SelectableTextHit.IndexAt(this, point);

    internal void SetInlines(IReadOnlyList<MdInline> source)
    {
        links.Clear();
        probed.Clear();
        var collection = new InlineCollection();
        var position = 0;
        AddInlines(collection, source, ref position, bold: false);
        Inlines = collection;
    }

    private void AddInlines(InlineCollection target, IReadOnlyList<MdInline> source, ref int position, bool bold)
    {
        foreach (var inline in source)
        {
            switch (inline.Kind)
            {
                case MdInlineKind.Text:
                    target.Add(new Run(inline.Text));
                    position += inline.Text.Length;
                    break;
                case MdInlineKind.Code:
                {
                    var run = new Run(inline.Text);
                    run.Bind(TextElement.FontFamilyProperty, this.GetResourceObservable("FontMono"));
                    run.Bind(TextElement.BackgroundProperty, this.GetResourceObservable("SurfaceSubtleBrush"));
                    target.Add(run);
                    if (LinkTarget.LooksLikeFilePath(inline.Text))
                    {
                        links.Add((position, position + inline.Text.Length, inline.Text));
                        probed.Add(inline.Text);
                    }

                    position += inline.Text.Length;
                    break;
                }

                case MdInlineKind.SoftBreak:
                    target.Add(new Run(" "));
                    position++;
                    break;
                case MdInlineKind.LineBreak:
                    target.Add(new LineBreak());
                    position++;
                    break;
                case MdInlineKind.Bold:
                {
                    var span = new Bold();
                    AddInlines(span.Inlines, inline.Children ?? [], ref position, true);
                    target.Add(span);
                    break;
                }

                case MdInlineKind.Italic:
                {
                    var span = new Italic();
                    AddInlines(span.Inlines, inline.Children ?? [], ref position, bold);
                    target.Add(span);
                    break;
                }

                case MdInlineKind.Strike:
                {
                    var span = new Span { TextDecorations = Decorations(Avalonia.Media.TextDecorationLocation.Strikethrough) };
                    AddInlines(span.Inlines, inline.Children ?? [], ref position, bold);
                    target.Add(span);
                    break;
                }

                case MdInlineKind.Math:
                    target.Add(MathView.Inline(inline.Text));
                    position++;
                    break;
                case MdInlineKind.Footnote:
                {
                    var mark = new Run(inline.Text)
                    {
                        BaselineAlignment = BaselineAlignment.Superscript,
                        FontSize = Math.Max(8, FontSize * 0.75),
                    };
                    mark.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("BrandActionBrush"));
                    target.Add(mark);
                    position += inline.Text.Length;
                    break;
                }

                case MdInlineKind.Link:
                case MdInlineKind.Image:
                {
                    var span = new Span { TextDecorations = Decorations(Avalonia.Media.TextDecorationLocation.Underline) };
                    span.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("BrandActionBrush"));
                    var start = position;
                    AddInlines(span.Inlines, inline.Children ?? [], ref position, bold);
                    if (inline.Url is { Length: > 0 } url)
                    {
                        links.Add((start, position, url));
                    }

                    target.Add(span);
                    break;
                }
            }
        }
    }

    private string? LinkAt(Point point)
    {
        if (links.Count == 0 || TextLayout is null)
        {
            return null;
        }

        var local = new Point(point.X - Padding.Left, point.Y - Padding.Top);
        var hit = TextLayout.HitTestPoint(local);
        if (!hit.IsInside)
        {
            return null;
        }

        var index = hit.TextPosition;
        foreach (var (start, end, url) in links)
        {
            if (index >= start && index < end)
            {
                return url;
            }
        }

        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Cursor = LinkAt(e.GetPosition(this)) is { } url && IsLive(url) ? HandCursor : null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left || SelectionStart != SelectionEnd)
        {
            return;
        }

        if (LinkAt(e.GetPosition(this)) is { } url && IsLive(url))
        {
            _ = LaunchAsync(url);
        }
    }

    private bool IsLive(string url) =>
        probed.Contains(url) ? LinkLauncher.ExistsCached(this, url) : MarkdownRenderer.IsOpenable(url);

    private Task LaunchAsync(string url) => LinkLauncher.OpenAsync(this, url);

    // Avalonia's TextDecorations.Underline/Strikethrough are cached AvaloniaObjects bound to the first thread that read them;
    // a fresh collection per span keeps the preview safe on any UI thread.
    private static TextDecorationCollection Decorations(TextDecorationLocation location) =>
        [new TextDecoration { Location = location }];
}

/// <summary>Builds Avalonia controls from parsed Markdown blocks. Colors and sizes come from the md-* styles of the hosting view.</summary>
internal static class MarkdownRenderer
{
    /// <summary>Web links and local file paths are opened (see <see cref="LinkLauncher"/>); everything else is shown but inert.</summary>
    internal static bool IsOpenable(string url) => LinkLauncher.LooksOpenable(url);

    public static Control RenderBlocks(IReadOnlyList<MdBlock> blocks, double spacing = 8)
    {
        var stack = new StackPanel { Spacing = spacing };
        foreach (var block in blocks)
        {
            stack.Children.Add(Render(block));
        }

        return stack;
    }

    public static Control Render(MdBlock block) => block.Kind switch
    {
        MdBlockKind.Heading => Text(block.Inlines, "md-h" + block.Level.ToString(CultureInfo.InvariantCulture)),
        MdBlockKind.Paragraph => RenderParagraph(block.Inlines),
        MdBlockKind.List => RenderList(block),
        MdBlockKind.Quote => RenderQuote(block),
        MdBlockKind.Code or MdBlockKind.Html => RenderCode(block),
        MdBlockKind.Table => RenderTable(block),
        MdBlockKind.Rule => new Border { Classes = { "md-rule" } },
        _ => new Control(),
    };

    /// <summary>
    /// A paragraph: a display formula alone in it is drawn as a centred block, footnote definitions (<c>[^1]: text</c>) become small
    /// numbered lines, a paragraph with pictures splits into text and picture rows, anything else is one text block.
    /// </summary>
    private static Control RenderParagraph(List<MdInline> inlines)
    {
        if (inlines.Exists(static inline => inline.Kind == MdInlineKind.Image))
        {
            return RenderWithImages(inlines);
        }

        if (inlines.Count == 1 && inlines[0] is { Kind: MdInlineKind.Math, Url: "display" } math)
        {
            return new MathView(math.Text, display: true);
        }

        if (inlines.Count > 1 && inlines[0].Kind == MdInlineKind.Footnote && inlines[1] is { Kind: MdInlineKind.Text } rest &&
            rest.Text.StartsWith(':'))
        {
            var note = new List<MdInline>(inlines.Count) { new(MdInlineKind.Bold, children: [new MdInline(MdInlineKind.Text, inlines[0].Text + ".")]) };
            note.Add(new MdInline(MdInlineKind.Text, rest.Text[1..]));
            note.AddRange(inlines.Skip(2));
            var definition = Text(note, "md-p");
            definition.Classes.Add("md-footnote");
            return definition;
        }

        return Text(inlines, "md-p");
    }

    /// <summary>
    /// A paragraph that holds pictures: the text around each picture stays text, each picture becomes its own block. (Inside a
    /// heading, list item or table cell a picture is just its alt text, as a link - those rows keep their one text block.)
    /// </summary>
    private static Control RenderWithImages(List<MdInline> inlines)
    {
        var stack = new StackPanel { Spacing = 6 };
        var run = new List<MdInline>();
        WrapPanel? row = null;
        foreach (var inline in inlines)
        {
            if (inline.Kind != MdInlineKind.Image)
            {
                // Blanks between two pictures keep them in one row; any real text ends the row.
                if (inline.Kind is MdInlineKind.SoftBreak or MdInlineKind.LineBreak ||
                    (inline.Kind == MdInlineKind.Text && string.IsNullOrWhiteSpace(inline.Text)))
                {
                    if (row is null)
                    {
                        run.Add(inline);
                    }

                    continue;
                }

                row = null;
                run.Add(inline);
                continue;
            }

            if (inline.Url is not { Length: > 0 } url)
            {
                continue;
            }

            if (row is null)
            {
                FlushRun(stack, run);
                row = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
                stack.Children.Add(row);
            }

            row.Children.Add(new MarkdownImageView(url, PlainText(inline.Children)));
        }

        FlushRun(stack, run);
        return stack;
    }

    private static void FlushRun(StackPanel stack, List<MdInline> run)
    {
        if (run.Exists(static inline => inline.Kind is not (MdInlineKind.SoftBreak or MdInlineKind.LineBreak) &&
                                        !(inline.Kind == MdInlineKind.Text && string.IsNullOrWhiteSpace(inline.Text))))
        {
            stack.Children.Add(Text([.. run], "md-p"));
        }

        run.Clear();
    }

    private static string PlainText(List<MdInline>? inlines)
    {
        if (inlines is null)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var inline in inlines)
        {
            builder.Append(inline.Text).Append(PlainText(inline.Children));
        }

        return builder.ToString();
    }

    private static MarkdownTextBlock Text(IReadOnlyList<MdInline> inlines, string cls)
    {
        var tb = new MarkdownTextBlock();
        tb.Classes.Add("md-text");
        tb.Classes.Add(cls);
        tb.SetInlines(inlines);
        return tb;
    }

    /// <summary>
    /// A fenced block: a Mermaid diagram when the fence says so, an ordinary code block otherwise. Whether a Mermaid type is
    /// enabled is decided by the block itself, so a disabled type keeps exactly the code-block chrome without a rebuild when the
    /// setting changes.
    /// </summary>
    private static Control RenderCode(MdBlock block)
    {
        var info = CodeInfo(block);
        if (block.Kind == MdBlockKind.Code && block.FenceClosed && info.AsSpan().Trim().Equals("math", StringComparison.OrdinalIgnoreCase) &&
            block.Text.Trim().Length > 0)
        {
            return new MathView(block.Text, display: true);
        }

        if (block.Kind == MdBlockKind.Code && IsMermaid(info))
        {
            return new MermaidBlockView(block.Text, info, block.FenceClosed);
        }

        // A closed svg fence is drawn as a picture tile; while it streams (or when it is not a loadable document) it is code.
        if (block.Kind == MdBlockKind.Code && block.FenceClosed && IsSvg(info) && block.Text.Contains("<svg", StringComparison.OrdinalIgnoreCase))
        {
            return new MarkdownImageView("svg", "SVG", block.Text);
        }

        return new MarkdownCodeBlock(block.Text, info);
    }

    /// <summary>Whether a fence's info string declares SVG.</summary>
    internal static bool IsSvg(string info) =>
        info.AsSpan().Trim().Equals("svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a fence's info string declares Mermaid - its first word, case-insensitive ("mermaid title=…" too).</summary>
    internal static bool IsMermaid(string info)
    {
        var span = info.AsSpan().TrimStart();
        var end = span.IndexOfAny(' ', '\t');
        return (end < 0 ? span : span[..end]).Equals("mermaid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The language label of a code block: its fence info string, or "html" for a block the parser read as raw HTML. One owner,
    /// because a streaming block is refreshed in place from the same block and would otherwise lose the label it first got.
    /// </summary>
    internal static string CodeInfo(MdBlock block) => block.Kind == MdBlockKind.Html ? "html" : block.Info;

    private static Control RenderQuote(MdBlock block)
    {
        var border = new Border { Child = RenderBlocks(block.Children, 6) };
        border.Classes.Add("md-quote");
        return border;
    }

    private static Control RenderList(MdBlock block)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Classes.Add("md-list");
        AddListItems(stack, block, 0);
        return stack;
    }

    /// <summary>Appends the rows of <paramref name="block"/>'s items from <paramref name="from"/> on.</summary>
    private static void AddListItems(StackPanel stack, MdBlock block, int from)
    {
        var first = block.Items.Count > 0 ? block.Items[0].Number ?? 1 : 1;
        for (var index = from; index < block.Items.Count; index++)
        {
            var item = block.Items[index];
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var marker = new TextBlock
            {
                MinWidth = 22,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 6, 0),
            };
            marker.Classes.Add("md-marker");
            if (item.Checked is { } done)
            {
                marker.Text = done ? "☑" : "☐";
                marker.Classes.Add(done ? "md-task-done" : "md-task-open");
            }
            else if (block.Ordered)
            {
                marker.Text = (first + index).ToString(CultureInfo.InvariantCulture) + ".";
            }
            else
            {
                marker.Text = "•";
            }

            var content = RenderBlocks(item.Blocks, 2);
            Grid.SetColumn(content, 1);
            grid.Children.Add(marker);
            grid.Children.Add(content);
            stack.Children.Add(grid);
        }
    }

    private static Control RenderTable(MdBlock block)
    {
        var columns = block.Aligns.Length;
        var grid = new Grid();
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < columns; c++)
        {
            grid.Children.Add(Cell(block.Header[c], block.Aligns[c], 0, c, header: true));
        }

        AddTableRows(grid, block, 0);
        var outer = new Border { Child = grid };
        outer.Classes.Add("md-table");
        return outer;
    }

    /// <summary>Appends the body rows of <paramref name="block"/> from <paramref name="from"/> on (grid row = body row + 1).</summary>
    private static void AddTableRows(Grid grid, MdBlock block, int from)
    {
        var columns = block.Aligns.Length;
        for (var r = from; r < block.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < columns; c++)
            {
                grid.Children.Add(Cell(block.Rows[r][c], block.Aligns[c], r + 1, c, header: false));
            }
        }
    }

    /// <summary>
    /// Grows a rendered list or table in place into <paramref name="next"/>, the next parse of the same streaming block: the
    /// item rows and table rows that come only from lines that had ended when <paramref name="rendered"/> was parsed
    /// (<paramref name="completeLines"/>) cannot change any more, so they are kept, and only the rest is rebuilt. Returns false
    /// when the block changed shape (another list type, other table columns) and the caller must render it afresh.
    /// </summary>
    internal static bool TryGrow(Control control, MdBlock rendered, MdBlock next, int completeLines)
    {
        if (rendered.Kind != next.Kind || rendered.StartLine != next.StartLine)
        {
            return false;
        }

        switch (next.Kind)
        {
            case MdBlockKind.List when control is StackPanel stack && stack.Classes.Contains("md-list") &&
                rendered.Ordered == next.Ordered && stack.Children.Count == rendered.Items.Count:
            {
                // An item is final once the item after it starts on an ended line: its lines are then fixed.
                var keep = 0;
                while (keep + 1 < rendered.Items.Count && keep < next.Items.Count &&
                    rendered.Items[keep + 1].StartLine <= completeLines &&
                    rendered.Items[keep].StartLine == next.Items[keep].StartLine &&
                    rendered.Items[keep].EndLine == next.Items[keep].EndLine &&
                    rendered.Items[keep].Number == next.Items[keep].Number)
                {
                    keep++;
                }

                stack.Children.RemoveRange(keep, stack.Children.Count - keep);
                AddListItems(stack, next, keep);
                return true;
            }

            case MdBlockKind.Table when control is Border { Child: Grid grid } border && border.Classes.Contains("md-table") &&
                next.Aligns.AsSpan().SequenceEqual(rendered.Aligns) && rendered.StartLine + 1 <= completeLines &&
                grid.RowDefinitions.Count == rendered.Rows.Count + 1:
            {
                // Header and delimiter have ended, so the columns are fixed; a body row is one line, final once that line ended.
                var keep = Math.Min(Math.Max(0, completeLines - (rendered.StartLine + 1)), Math.Min(rendered.Rows.Count, next.Rows.Count));
                var columns = next.Aligns.Length;
                var cells = (keep + 1) * columns;
                grid.Children.RemoveRange(cells, grid.Children.Count - cells);
                grid.RowDefinitions.RemoveRange(keep + 1, grid.RowDefinitions.Count - keep - 1);
                AddTableRows(grid, next, keep);
                return true;
            }

            default:
                return false;
        }
    }

    private static Border Cell(IReadOnlyList<MdInline> inlines, MdAlign align, int row, int column, bool header)
    {
        var text = Text(inlines, header ? "md-th" : "md-td");
        text.TextAlignment = align switch
        {
            MdAlign.Center => TextAlignment.Center,
            MdAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
        var cell = new Border { Child = text };
        cell.Classes.Add("md-cell");
        if (header)
        {
            cell.Classes.Add("md-cell-head");
        }
        else if (row % 2 == 0)
        {
            cell.Classes.Add("md-cell-alt");
        }

        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        return cell;
    }
}
