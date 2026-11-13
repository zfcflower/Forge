using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Changes.Markdown;

/// <summary>A selectable text block whose links open through the launcher (http/https only) on a plain click.</summary>
internal sealed class MarkdownTextBlock : SelectableTextBlock
{
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private readonly List<(int Start, int End, string Url)> links = [];

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    internal IReadOnlyList<(int Start, int End, string Url)> Links => links;

    internal void SetInlines(IReadOnlyList<MdInline> source)
    {
        links.Clear();
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
                    run.Bind(TextElement.BackgroundProperty, this.GetResourceObservable("SurfaceMutedBrush"));
                    target.Add(run);
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

                case MdInlineKind.Link:
                {
                    var span = new Span { TextDecorations = Decorations(Avalonia.Media.TextDecorationLocation.Underline) };
                    span.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("AccentTextBrush"));
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
        Cursor = LinkAt(e.GetPosition(this)) is { } url && MarkdownRenderer.IsOpenable(url) ? HandCursor : null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left || SelectionStart != SelectionEnd)
        {
            return;
        }

        if (LinkAt(e.GetPosition(this)) is { } url && MarkdownRenderer.IsOpenable(url))
        {
            _ = LaunchAsync(url);
        }
    }

    private async Task LaunchAsync(string url)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is { } top && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                await top.Launcher.LaunchUriAsync(uri).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotSupportedException
            or System.Runtime.InteropServices.ExternalException or UriFormatException)
        {
            AppLogs.Markdown.ZLogWarning($"link launch failed: {exception.Message}");
        }
    }

    // Avalonia's TextDecorations.Underline/Strikethrough are cached AvaloniaObjects bound to the first thread that read them;
    // a fresh collection per span keeps the preview safe on any UI thread.
    private static TextDecorationCollection Decorations(TextDecorationLocation location) =>
        [new TextDecoration { Location = location }];
}

/// <summary>Builds Avalonia controls from parsed Markdown blocks. Colors and sizes come from the md-* styles of the hosting view.</summary>
internal static class MarkdownRenderer
{
    /// <summary>Only web links are opened; everything else is shown but inert.</summary>
    internal static bool IsOpenable(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

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
        MdBlockKind.Paragraph => Text(block.Inlines, "md-p"),
        MdBlockKind.List => RenderList(block),
        MdBlockKind.Quote => RenderQuote(block),
        MdBlockKind.Code => RenderCode(block.Text, block.Info),
        MdBlockKind.Html => RenderCode(block.Text, "html"),
        MdBlockKind.Table => RenderTable(block),
        MdBlockKind.Rule => new Border { Classes = { "md-rule" } },
        _ => new Control(),
    };

    private static MarkdownTextBlock Text(IReadOnlyList<MdInline> inlines, string cls)
    {
        var tb = new MarkdownTextBlock();
        tb.Classes.Add("md-text");
        tb.Classes.Add(cls);
        tb.SetInlines(inlines);
        return tb;
    }

    private static Control RenderCode(string text, string info)
    {
        var tb = new SelectableTextBlock { Text = text };
        tb.Classes.Add("md-code-text");
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = tb,
        };
        var border = new Border { Child = scroller };
        border.Classes.Add("md-code");
        if (info.Length > 0)
        {
            AutomationProperties.SetName(border, info);
        }

        return border;
    }

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
        var number = block.Items.Count > 0 ? block.Items[0].Number ?? 1 : 1;
        foreach (var item in block.Items)
        {
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
                marker.Text = number.ToString(CultureInfo.InvariantCulture) + ".";
            }
            else
            {
                marker.Text = "•";
            }

            number++;
            var content = RenderBlocks(item.Blocks, 2);
            Grid.SetColumn(content, 1);
            grid.Children.Add(marker);
            grid.Children.Add(content);
            stack.Children.Add(grid);
        }

        return stack;
    }

    private static Control RenderTable(MdBlock block)
    {
        var columns = block.Aligns.Length;
        var grid = new Grid();
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        }

        var rowCount = block.Rows.Count + 1;
        for (var r = 0; r < rowCount; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (var c = 0; c < columns; c++)
        {
            grid.Children.Add(Cell(block.Header[c], block.Aligns[c], 0, c, header: true));
        }

        for (var r = 0; r < block.Rows.Count; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                grid.Children.Add(Cell(block.Rows[r][c], block.Aligns[c], r + 1, c, header: false));
            }
        }

        var outer = new Border { Child = grid };
        outer.Classes.Add("md-table");
        return outer;
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

        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        return cell;
    }
}
