using System.Text;

namespace Forge.App.Changes.Markdown;

internal enum MdBlockKind { Paragraph, Heading, List, Quote, Code, Table, Rule, Html }

internal enum MdAlign { None, Left, Center, Right }

internal enum MdInlineKind { Text, Code, Bold, Italic, Strike, Link, SoftBreak, LineBreak }

/// <summary>Small inline AST node. <see cref="Text"/> is set for Text/Code, <see cref="Url"/> for Link, <see cref="Children"/> for the wrappers.</summary>
internal sealed class MdInline
{
    public MdInline(MdInlineKind kind, string text = "", string? url = null, List<MdInline>? children = null)
    {
        Kind = kind;
        Text = text;
        Url = url;
        Children = children;
    }

    public MdInlineKind Kind { get; }

    public string Text { get; }

    public string? Url { get; }

    public List<MdInline>? Children { get; }
}

internal sealed class MdListItem
{
    /// <summary>Ordered number of the item, or null for a bullet.</summary>
    public int? Number { get; init; }

    /// <summary>Task list state: null = not a task item.</summary>
    public bool? Checked { get; init; }

    public List<MdBlock> Blocks { get; } = [];

    public int StartLine { get; init; }

    public int EndLine { get; set; }
}

/// <summary>One block with its source line range (inclusive) in the numbering given to the parser.</summary>
internal sealed class MdBlock
{
    public MdBlock(MdBlockKind kind, int startLine, int endLine)
    {
        Kind = kind;
        StartLine = startLine;
        EndLine = endLine;
    }

    public MdBlockKind Kind { get; }

    public int StartLine { get; }

    public int EndLine { get; set; }

    /// <summary>Heading level 1..6.</summary>
    public int Level { get; init; }

    /// <summary>Paragraph/heading content.</summary>
    public List<MdInline> Inlines { get; init; } = [];

    /// <summary>Code/Html raw text (lines joined with \n).</summary>
    public string Text { get; init; } = "";

    /// <summary>Fence info string; "yaml" for front matter.</summary>
    public string Info { get; init; } = "";

    public bool Ordered { get; init; }

    public List<MdListItem> Items { get; } = [];

    /// <summary>Quote content.</summary>
    public List<MdBlock> Children { get; } = [];

    public MdAlign[] Aligns { get; init; } = [];

    public List<List<MdInline>> Header { get; } = [];

    public List<List<List<MdInline>>> Rows { get; } = [];

    /// <summary>Plain text of a heading/paragraph.</summary>
    public string PlainText => MarkdownBlockParser.PlainText(Inlines);

    public bool Intersects(int firstLine, int lastLine) => StartLine <= lastLine && EndLine >= firstLine;
}

/// <summary>
/// Pure, allocation-conscious Markdown block + inline parser (CommonMark/GFM subset: ATX/setext headings, paragraphs,
/// nested lists and task items, blockquotes, fenced/indented code, pipe tables, rules, YAML front matter, HTML blocks as text).
/// </summary>
internal static class MarkdownBlockParser
{
    /// <summary>Parses <paramref name="lines"/>; the first line is numbered <paramref name="firstLineNumber"/>.</summary>
    public static List<MdBlock> Parse(IReadOnlyList<string> lines, int firstLineNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var text = new string[lines.Count];
        var src = new int[lines.Count];
        for (var i = 0; i < text.Length; i++)
        {
            text[i] = ExpandTabs(lines[i]);
            src[i] = firstLineNumber + i;
        }

        var result = new List<MdBlock>();
        ParseCore(text, src, 0, text.Length, result, top: true);
        return result;
    }

    public static string PlainText(IReadOnlyList<MdInline> inlines)
    {
        var sb = new StringBuilder();
        AppendPlain(sb, inlines);
        return sb.ToString();
    }

    private static void AppendPlain(StringBuilder sb, IReadOnlyList<MdInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline.Kind)
            {
                case MdInlineKind.Text:
                case MdInlineKind.Code:
                    sb.Append(inline.Text);
                    break;
                case MdInlineKind.SoftBreak:
                case MdInlineKind.LineBreak:
                    sb.Append(' ');
                    break;
                default:
                    if (inline.Children is not null)
                    {
                        AppendPlain(sb, inline.Children);
                    }

                    break;
            }
        }
    }

    private static string ExpandTabs(string line)
    {
        if (line.IndexOf('\t') < 0)
        {
            return line;
        }

        var sb = new StringBuilder(line.Length + 8);
        var col = 0;
        var leading = true;
        foreach (var c in line)
        {
            if (c == '\t' && leading)
            {
                var n = 4 - (col % 4);
                sb.Append(' ', n);
                col += n;
                continue;
            }

            if (c != ' ')
            {
                leading = false;
            }

            sb.Append(c);
            col++;
        }

        return sb.ToString();
    }

    private static int Indent(string s)
    {
        var n = 0;
        while (n < s.Length && s[n] == ' ')
        {
            n++;
        }

        return n;
    }

    private static bool IsBlank(string s)
    {
        foreach (var c in s)
        {
            if (c != ' ' && c != '\t' && c != '\r')
            {
                return false;
            }
        }

        return true;
    }

    private static void ParseCore(string[] t, int[] s, int from, int to, List<MdBlock> output, bool top)
    {
        var i = from;
        if (top && i < to && t[i].TrimEnd() == "---")
        {
            for (var j = i + 1; j < to; j++)
            {
                var closing = t[j].TrimEnd();
                if (closing is "---" or "...")
                {
                    output.Add(new MdBlock(MdBlockKind.Code, s[i], s[j]) { Text = Join(t, i + 1, j), Info = "yaml" });
                    i = j + 1;
                    break;
                }
            }
        }

        while (i < to)
        {
            var line = t[i];
            if (IsBlank(line))
            {
                i++;
                continue;
            }

            var ind = Indent(line);
            if (ind >= 4)
            {
                i = IndentedCode(t, s, i, to, output);
                continue;
            }

            var rest = line.AsSpan(ind);
            if (TryFence(rest, out var fenceChar, out var fenceLen, out var info))
            {
                i = Fence(t, s, i, to, ind, fenceChar, fenceLen, info, output);
                continue;
            }

            if (TryAtx(rest, out var level, out var headingText))
            {
                output.Add(new MdBlock(MdBlockKind.Heading, s[i], s[i]) { Level = level, Inlines = ParseInlines(headingText) });
                i++;
                continue;
            }

            if (IsRule(rest))
            {
                output.Add(new MdBlock(MdBlockKind.Rule, s[i], s[i]));
                i++;
                continue;
            }

            if (rest[0] == '>')
            {
                i = Quote(t, s, i, to, output);
                continue;
            }

            if (TryListMarker(line, out _))
            {
                i = List(t, s, i, to, output);
                continue;
            }

            if (IsHtmlStart(rest))
            {
                i = Html(t, s, i, to, output);
                continue;
            }

            if (i + 1 < to && line.Contains('|') && TryTable(t, s, i, to, output, out var next))
            {
                i = next;
                continue;
            }

            i = Paragraph(t, s, i, to, output);
        }
    }

    private static string Join(string[] t, int from, int to)
    {
        if (to <= from)
        {
            return "";
        }

        return to - from == 1 ? t[from] : string.Join('\n', t, from, to - from);
    }

    // ---- code ----

    private static int IndentedCode(string[] t, int[] s, int i, int to, List<MdBlock> output)
    {
        var j = i;
        var last = i;
        while (j < to && (IsBlank(t[j]) || Indent(t[j]) >= 4))
        {
            if (!IsBlank(t[j]))
            {
                last = j;
            }

            j++;
        }

        var sb = new StringBuilder();
        for (var k = i; k <= last; k++)
        {
            if (k > i)
            {
                sb.Append('\n');
            }

            var l = t[k];
            if (l.Length >= 4)
            {
                sb.Append(l.AsSpan(4));
            }
        }

        output.Add(new MdBlock(MdBlockKind.Code, s[i], s[last]) { Text = sb.ToString() });
        return last + 1;
    }

    private static bool TryFence(ReadOnlySpan<char> rest, out char ch, out int len, out string info)
    {
        ch = default;
        len = 0;
        info = "";
        if (rest.Length < 3 || (rest[0] != '`' && rest[0] != '~'))
        {
            return false;
        }

        ch = rest[0];
        while (len < rest.Length && rest[len] == ch)
        {
            len++;
        }

        if (len < 3)
        {
            return false;
        }

        var tail = rest[len..];
        if (ch == '`' && tail.Contains('`'))
        {
            return false;
        }

        info = tail.Trim().ToString();
        return true;
    }

    private static int Fence(string[] t, int[] s, int i, int to, int ind, char ch, int len, string info, List<MdBlock> output)
    {
        var closeAt = -1;
        for (var j = i + 1; j < to; j++)
        {
            var l = t[j];
            if (Indent(l) > 3)
            {
                continue;
            }

            var span = l.AsSpan().Trim();
            if (span.Length >= len && IsAll(span, ch))
            {
                closeAt = j;
                break;
            }
        }

        var end = closeAt >= 0 ? closeAt : to;
        var sb = new StringBuilder();
        for (var k = i + 1; k < end; k++)
        {
            if (k > i + 1)
            {
                sb.Append('\n');
            }

            var l = t[k];
            sb.Append(l.AsSpan(Math.Min(ind, Indent(l))));
        }

        output.Add(new MdBlock(MdBlockKind.Code, s[i], s[closeAt >= 0 ? closeAt : to - 1]) { Text = sb.ToString(), Info = info });
        return closeAt >= 0 ? closeAt + 1 : to;
    }

    private static bool IsAll(ReadOnlySpan<char> span, char ch)
    {
        foreach (var c in span)
        {
            if (c != ch)
            {
                return false;
            }
        }

        return span.Length > 0;
    }

    // ---- headings / rules ----

    private static bool TryAtx(ReadOnlySpan<char> rest, out int level, out string text)
    {
        level = 0;
        text = "";
        while (level < rest.Length && rest[level] == '#')
        {
            level++;
        }

        if (level is < 1 or > 6 || (level < rest.Length && rest[level] != ' '))
        {
            return false;
        }

        var body = rest[level..].Trim();
        var end = body.Length;
        while (end > 0 && body[end - 1] == '#')
        {
            end--;
        }

        if (end == 0)
        {
            body = [];
        }
        else if (end < body.Length && body[end - 1] == ' ')
        {
            body = body[..end].TrimEnd();
        }

        text = body.ToString();
        return true;
    }

    private static bool IsRule(ReadOnlySpan<char> rest)
    {
        if (rest.Length < 3 || (rest[0] != '-' && rest[0] != '*' && rest[0] != '_'))
        {
            return false;
        }

        var ch = rest[0];
        var count = 0;
        foreach (var c in rest)
        {
            if (c == ch)
            {
                count++;
            }
            else if (c != ' ')
            {
                return false;
            }
        }

        return count >= 3;
    }

    // ---- quote ----

    private static int Quote(string[] t, int[] s, int i, int to, List<MdBlock> output)
    {
        var lines = new List<string>();
        var srcs = new List<int>();
        var j = i;
        var inFence = false;
        for (; j < to; j++)
        {
            var l = t[j];
            if (IsBlank(l))
            {
                break;
            }

            var ind = Indent(l);
            if (ind < 4 && ind < l.Length && l[ind] == '>')
            {
                var start = ind + 1;
                if (start < l.Length && l[start] == ' ')
                {
                    start++;
                }

                var content = l[start..];
                if (TryFence(content.AsSpan().TrimStart(), out _, out _, out _))
                {
                    inFence = !inFence;
                }

                lines.Add(content);
                srcs.Add(s[j]);
                continue;
            }

            // Lazy continuation: only of a paragraph, never of a fence, and not when the line starts another block.
            if (inFence || lines.Count == 0 || StartsBlock(l))
            {
                break;
            }

            lines.Add(l);
            srcs.Add(s[j]);
        }

        var block = new MdBlock(MdBlockKind.Quote, s[i], s[j - 1]);
        ParseCore([.. lines], [.. srcs], 0, lines.Count, block.Children, top: false);
        output.Add(block);
        return j;
    }

    private static bool StartsBlock(string line)
    {
        var ind = Indent(line);
        if (ind >= 4 || ind >= line.Length)
        {
            return false;
        }

        var rest = line.AsSpan(ind);
        return TryFence(rest, out _, out _, out _) || TryAtx(rest, out _, out _) || IsRule(rest) || rest[0] == '>' ||
            (TryListMarker(line, out var m) && !m.Empty && (!m.Ordered || m.Number == 1));
    }

    // ---- lists ----

    private readonly record struct ListMarker(int Indent, int ContentColumn, bool Ordered, int Number, char Delimiter, bool Empty);

    private static bool TryListMarker(string line, out ListMarker marker)
    {
        marker = default;
        var ind = Indent(line);
        if (ind >= 4 || ind >= line.Length)
        {
            return false;
        }

        var c = line[ind];
        var pos = ind;
        var ordered = false;
        var number = 0;
        char delimiter;
        if (c is '-' or '+' or '*')
        {
            delimiter = c;
            pos++;
        }
        else if (char.IsAsciiDigit(c))
        {
            var digits = 0;
            while (pos < line.Length && char.IsAsciiDigit(line[pos]) && digits < 10)
            {
                number = unchecked(number * 10 + (line[pos] - '0'));
                pos++;
                digits++;
            }

            if (digits > 9 || pos >= line.Length || (line[pos] != '.' && line[pos] != ')'))
            {
                return false;
            }

            ordered = true;
            delimiter = line[pos];
            pos++;
        }
        else
        {
            return false;
        }

        if (pos == line.Length)
        {
            marker = new ListMarker(ind, pos + 1, ordered, number, delimiter, true);
            return true;
        }

        if (line[pos] != ' ')
        {
            return false;
        }

        var spaces = 0;
        while (pos + spaces < line.Length && line[pos + spaces] == ' ')
        {
            spaces++;
        }

        var empty = pos + spaces == line.Length;
        // 5+ spaces after the marker means the content is an indented code block: content starts after one space.
        var width = spaces >= 5 || empty ? 1 : spaces;
        marker = new ListMarker(ind, pos + width, ordered, number, delimiter, empty);
        return true;
    }

    private static int List(string[] t, int[] s, int i, int to, List<MdBlock> output)
    {
        TryListMarker(t[i], out var first);
        var list = new MdBlock(MdBlockKind.List, s[i], s[i]) { Ordered = first.Ordered };
        var j = i;
        while (true)
        {
            TryListMarker(t[j], out var marker);
            j = ListItem(t, s, j, to, marker, list);

            var k = j;
            while (k < to && IsBlank(t[k]))
            {
                k++;
            }

            if (k < to && TryListMarker(t[k], out var next) && next.Ordered == first.Ordered && next.Delimiter == first.Delimiter &&
                next.Indent < marker.ContentColumn)
            {
                j = k;
                continue;
            }

            break;
        }

        list.EndLine = list.Items[^1].EndLine;
        output.Add(list);
        return j;
    }

    private static int ListItem(string[] t, int[] s, int i, int to, ListMarker marker, MdBlock list)
    {
        var content = marker.ContentColumn;
        var lines = new List<string>();
        var srcs = new List<int>();
        var firstText = marker.Empty ? "" : t[i][Math.Min(content, t[i].Length)..];

        bool? isChecked = null;
        if (firstText.Length >= 3 && firstText[0] == '[' && firstText[2] == ']' && (firstText.Length == 3 || firstText[3] == ' ') &&
            firstText[1] is ' ' or 'x' or 'X')
        {
            isChecked = firstText[1] != ' ';
            firstText = firstText.Length > 4 ? firstText[4..] : "";
        }

        lines.Add(firstText);
        srcs.Add(s[i]);
        var j = i + 1;
        var lastNonBlank = i;
        var inFence = TryFence(firstText.AsSpan().TrimStart(), out _, out _, out _);
        for (; j < to; j++)
        {
            var l = t[j];
            if (IsBlank(l))
            {
                // A blank line stays in the item only when the next non-blank line is indented into it.
                var k = j + 1;
                while (k < to && IsBlank(t[k]))
                {
                    k++;
                }

                if (k < to && Indent(t[k]) >= content)
                {
                    for (; j < k; j++)
                    {
                        lines.Add("");
                        srcs.Add(s[j]);
                    }

                    j--;
                    continue;
                }

                break;
            }

            var ind = Indent(l);
            if (ind >= content)
            {
                var stripped = l[content..];
                if (TryFence(stripped.AsSpan().TrimStart(), out _, out _, out _))
                {
                    inFence = !inFence;
                }

                lines.Add(stripped);
                srcs.Add(s[j]);
                lastNonBlank = j;
                continue;
            }

            // Lazy continuation of the item's paragraph.
            if (!inFence && !IsBlank(lines[^1]) && !StartsBlock(l) && !TryListMarker(l, out _))
            {
                lines.Add(l.TrimStart());
                srcs.Add(s[j]);
                lastNonBlank = j;
                continue;
            }

            break;
        }

        var item = new MdListItem { Number = list.Ordered ? marker.Number : null, Checked = isChecked, StartLine = s[i] };
        ParseCore([.. lines], [.. srcs], 0, lines.Count, item.Blocks, top: false);
        item.EndLine = s[lastNonBlank];
        list.Items.Add(item);
        return j;
    }

    // ---- html ----

    private static readonly HashSet<string> HtmlBlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "p", "details", "summary", "table", "thead", "tbody", "tr", "td", "th", "ul", "ol", "li", "pre", "h1", "h2", "h3",
        "h4", "h5", "h6", "br", "hr", "img", "center", "section", "article", "blockquote", "script", "style", "picture", "figure",
        "figcaption", "nav", "header", "footer", "aside", "dl", "dt", "dd", "form", "iframe", "video", "audio",
    };

    private static bool IsHtmlStart(ReadOnlySpan<char> rest)
    {
        if (rest.Length < 2 || rest[0] != '<')
        {
            return false;
        }

        if (rest.StartsWith("<!--") || rest[1] == '?' || rest[1] == '!')
        {
            return true;
        }

        var p = rest[1] == '/' ? 2 : 1;
        var q = p;
        while (q < rest.Length && char.IsAsciiLetterOrDigit(rest[q]))
        {
            q++;
        }

        if (q == p || (q < rest.Length && rest[q] is not (' ' or '>' or '/')))
        {
            return false;
        }

        return HtmlBlockTags.Contains(rest[p..q].ToString());
    }

    private static int Html(string[] t, int[] s, int i, int to, List<MdBlock> output)
    {
        var comment = t[i].AsSpan().TrimStart().StartsWith("<!--");
        var j = i;
        if (comment)
        {
            while (j < to && !t[j].Contains("-->", StringComparison.Ordinal))
            {
                j++;
            }

            j = Math.Min(j, to - 1);
        }
        else
        {
            while (j + 1 < to && !IsBlank(t[j + 1]))
            {
                j++;
            }
        }

        output.Add(new MdBlock(MdBlockKind.Html, s[i], s[j]) { Text = Join(t, i, j + 1) });
        return j + 1;
    }

    // ---- paragraph ----

    private static int Paragraph(string[] t, int[] s, int i, int to, List<MdBlock> output)
    {
        var j = i + 1;
        var level = 0;
        for (; j < to; j++)
        {
            var l = t[j];
            if (IsBlank(l))
            {
                break;
            }

            var ind = Indent(l);
            if (ind < 4)
            {
                var rest = l.AsSpan(ind).TrimEnd();
                if (rest.Length > 0 && (IsAll(rest, '=') || IsAll(rest, '-')))
                {
                    level = rest[0] == '=' ? 1 : 2;
                    break;
                }

                if (StartsBlock(l))
                {
                    break;
                }
            }
        }

        var sb = new StringBuilder();
        for (var k = i; k < j; k++)
        {
            if (k > i)
            {
                sb.Append('\n');
            }

            sb.Append(t[k].AsSpan(Indent(t[k])));
        }

        var text = sb.ToString().TrimEnd();
        if (level > 0)
        {
            output.Add(new MdBlock(MdBlockKind.Heading, s[i], s[j]) { Level = level, Inlines = ParseInlines(text) });
            return j + 1;
        }

        output.Add(new MdBlock(MdBlockKind.Paragraph, s[i], s[j - 1]) { Inlines = ParseInlines(text) });
        return j;
    }

    // ---- tables ----

    private static bool TryTable(string[] t, int[] s, int i, int to, List<MdBlock> output, out int next)
    {
        next = i;
        var header = SplitRow(t[i]);
        if (header.Count == 0 || Indent(t[i + 1]) >= 4)
        {
            return false;
        }

        var delim = SplitRow(t[i + 1]);
        if (delim.Count != header.Count)
        {
            return false;
        }

        var aligns = new MdAlign[delim.Count];
        for (var c = 0; c < delim.Count; c++)
        {
            var cell = delim[c].Trim();
            if (cell.Length == 0)
            {
                return false;
            }

            var left = cell[0] == ':';
            var right = cell[^1] == ':';
            var dashes = cell.Trim(':');
            if (dashes.Length == 0 || !IsAll(dashes, '-'))
            {
                return false;
            }

            aligns[c] = left && right ? MdAlign.Center : left ? MdAlign.Left : right ? MdAlign.Right : MdAlign.None;
        }

        var block = new MdBlock(MdBlockKind.Table, s[i], s[i + 1]) { Aligns = aligns };
        foreach (var cell in header)
        {
            block.Header.Add(ParseInlines(cell.Trim()));
        }

        var j = i + 2;
        for (; j < to; j++)
        {
            var l = t[j];
            if (IsBlank(l) || StartsBlock(l))
            {
                break;
            }

            var cells = SplitRow(l);
            var row = new List<List<MdInline>>(aligns.Length);
            for (var c = 0; c < aligns.Length; c++)
            {
                row.Add(c < cells.Count ? ParseInlines(cells[c].Trim()) : []);
            }

            block.Rows.Add(row);
            block.EndLine = s[j];
        }

        output.Add(block);
        next = j;
        return true;
    }

    private static List<string> SplitRow(string line)
    {
        var text = line.AsSpan().Trim();
        var cells = new List<string>();
        if (text.Length == 0)
        {
            return cells;
        }

        var start = text[0] == '|' ? 1 : 0;
        var sb = new StringBuilder();
        for (var p = start; p < text.Length; p++)
        {
            var c = text[p];
            if (c == '\\' && p + 1 < text.Length && text[p + 1] == '|')
            {
                sb.Append('|');
                p++;
            }
            else if (c == '|')
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        // A trailing pipe leaves an empty remainder that is not a cell.
        if (sb.Length > 0 && !IsBlank(sb.ToString()))
        {
            cells.Add(sb.ToString());
        }

        return cells;
    }

    // ---- inlines ----

    public static List<MdInline> ParseInlines(string text)
    {
        var result = new List<MdInline>();
        ParseInlineRange(text, 0, text.Length, result);
        return result;
    }

    private static bool IsAsciiPunct(char c) => c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~';

    private static void Flush(StringBuilder sb, List<MdInline> o)
    {
        if (sb.Length > 0)
        {
            o.Add(new MdInline(MdInlineKind.Text, sb.ToString()));
            sb.Clear();
        }
    }

    private static void ParseInlineRange(string s, int start, int end, List<MdInline> o)
    {
        var sb = new StringBuilder();
        var i = start;
        while (i < end)
        {
            var c = s[i];
            switch (c)
            {
                case '\\':
                    if (i + 1 < end && s[i + 1] == '\n')
                    {
                        Flush(sb, o);
                        o.Add(new MdInline(MdInlineKind.LineBreak));
                        i += 2;
                        while (i < end && s[i] == ' ')
                        {
                            i++;
                        }

                        continue;
                    }

                    if (i + 1 < end && IsAsciiPunct(s[i + 1]))
                    {
                        sb.Append(s[i + 1]);
                        i += 2;
                        continue;
                    }

                    sb.Append(c);
                    i++;
                    break;
                case '`':
                    i = CodeSpan(s, i, end, sb, o);
                    break;
                case '*':
                case '_':
                case '~':
                    i = Emphasis(s, start, i, end, c, sb, o);
                    break;
                case '!':
                    if (i + 1 < end && s[i + 1] == '[' && TryLink(s, i + 1, end, o, sb, out var imageEnd))
                    {
                        i = imageEnd;
                    }
                    else
                    {
                        sb.Append(c);
                        i++;
                    }

                    break;
                case '[':
                    if (TryLink(s, i, end, o, sb, out var linkEnd))
                    {
                        i = linkEnd;
                    }
                    else
                    {
                        sb.Append(c);
                        i++;
                    }

                    break;
                case '<':
                    if (TryAutolink(s, i, end, o, sb, out var autoEnd))
                    {
                        i = autoEnd;
                    }
                    else
                    {
                        sb.Append(c);
                        i++;
                    }

                    break;
                case '\n':
                {
                    var trailing = 0;
                    while (sb.Length - trailing > 0 && sb[sb.Length - 1 - trailing] == ' ')
                    {
                        trailing++;
                    }

                    sb.Length -= trailing;
                    Flush(sb, o);
                    o.Add(new MdInline(trailing >= 2 ? MdInlineKind.LineBreak : MdInlineKind.SoftBreak));
                    i++;
                    while (i < end && s[i] == ' ')
                    {
                        i++;
                    }

                    break;
                }

                case 'h':
                    if ((i == start || !char.IsLetterOrDigit(s[i - 1])) && TryBareUrl(s, i, end, o, sb, out var urlEnd))
                    {
                        i = urlEnd;
                    }
                    else
                    {
                        sb.Append(c);
                        i++;
                    }

                    break;
                default:
                    sb.Append(c);
                    i++;
                    break;
            }
        }

        Flush(sb, o);
    }

    private static int CodeSpan(string s, int i, int end, StringBuilder sb, List<MdInline> o)
    {
        var n = 1;
        while (i + n < end && s[i + n] == '`')
        {
            n++;
        }

        var k = i + n;
        while (k < end)
        {
            if (s[k] != '`')
            {
                k++;
                continue;
            }

            var r = 1;
            while (k + r < end && s[k + r] == '`')
            {
                r++;
            }

            if (r == n)
            {
                var content = s.Substring(i + n, k - i - n).Replace('\n', ' ');
                if (content.Length >= 2 && content[0] == ' ' && content[^1] == ' ' && content.AsSpan().Trim().Length > 0)
                {
                    content = content[1..^1];
                }

                Flush(sb, o);
                o.Add(new MdInline(MdInlineKind.Code, content));
                return k + r;
            }

            k += r;
        }

        sb.Append('`', n);
        return i + n;
    }

    private static int Emphasis(string s, int start, int i, int end, char c, StringBuilder sb, List<MdInline> o)
    {
        var run = 1;
        while (i + run < end && s[i + run] == c)
        {
            run++;
        }

        var afterRun = i + run;
        if (c == '~' && run != 2)
        {
            sb.Append(c, run);
            return afterRun;
        }

        var canOpen = afterRun < end && !char.IsWhiteSpace(s[afterRun]);
        if (c == '_' && i > start && char.IsLetterOrDigit(s[i - 1]))
        {
            canOpen = false;
        }

        if (!canOpen)
        {
            sb.Append(c, run);
            return afterRun;
        }

        var max = c == '~' ? 2 : 3;
        for (var n = Math.Min(run, max); n >= 1; n--)
        {
            var close = FindClose(s, afterRun, end, c, n);
            if (close < 0)
            {
                continue;
            }

            sb.Append(c, run - n);
            Flush(sb, o);
            var inner = new List<MdInline>();
            ParseInlineRange(s.Substring(afterRun, close - afterRun), 0, close - afterRun, inner);
            MdInline node;
            if (c == '~')
            {
                node = new MdInline(MdInlineKind.Strike, children: inner);
            }
            else if (n == 1)
            {
                node = new MdInline(MdInlineKind.Italic, children: inner);
            }
            else if (n == 2)
            {
                node = new MdInline(MdInlineKind.Bold, children: inner);
            }
            else
            {
                node = new MdInline(MdInlineKind.Bold, children: [new MdInline(MdInlineKind.Italic, children: inner)]);
            }

            o.Add(node);
            return close + n;
        }

        sb.Append(c, run);
        return afterRun;
    }

    /// <summary>Index of the first char of the closing n-run (the last n chars of a longer run), or -1.</summary>
    private static int FindClose(string s, int from, int end, char c, int n)
    {
        var j = from;
        while (j < end)
        {
            var ch = s[j];
            if (ch == '\\')
            {
                j += 2;
                continue;
            }

            if (ch == '`')
            {
                var r = 1;
                while (j + r < end && s[j + r] == '`')
                {
                    r++;
                }

                var found = -1;
                var k = j + r;
                while (k < end)
                {
                    if (s[k] != '`')
                    {
                        k++;
                        continue;
                    }

                    var r2 = 1;
                    while (k + r2 < end && s[k + r2] == '`')
                    {
                        r2++;
                    }

                    if (r2 == r)
                    {
                        found = k + r2;
                        break;
                    }

                    k += r2;
                }

                j = found >= 0 ? found : j + r;
                continue;
            }

            if (ch == c)
            {
                var r = 1;
                while (j + r < end && s[j + r] == c)
                {
                    r++;
                }

                var canClose = j > from && !char.IsWhiteSpace(s[j - 1]) && r >= n;
                if (canClose && c == '_' && j + r < end && char.IsLetterOrDigit(s[j + r]))
                {
                    canClose = false;
                }

                if (canClose)
                {
                    return j + (r - n);
                }

                j += r;
                continue;
            }

            j++;
        }

        return -1;
    }

    private static bool TryLink(string s, int open, int end, List<MdInline> o, StringBuilder sb, out int next)
    {
        next = open;
        var depth = 0;
        var close = -1;
        for (var j = open; j < end; j++)
        {
            var ch = s[j];
            if (ch == '\\')
            {
                j++;
            }
            else if (ch == '`')
            {
                var r = 1;
                while (j + r < end && s[j + r] == '`')
                {
                    r++;
                }

                var idx = s.IndexOf(new string('`', r), j + r, end - j - r, StringComparison.Ordinal);
                j = idx >= 0 ? idx + r - 1 : j + r - 1;
            }
            else if (ch == '[')
            {
                depth++;
            }
            else if (ch == ']')
            {
                depth--;
                if (depth == 0)
                {
                    close = j;
                    break;
                }
            }
        }

        if (close < 0 || close + 1 >= end || s[close + 1] != '(')
        {
            return false;
        }

        var p = close + 2;
        while (p < end && s[p] == ' ')
        {
            p++;
        }

        string url;
        if (p < end && s[p] == '<')
        {
            var gt = s.IndexOf('>', p + 1, end - p - 1);
            if (gt < 0)
            {
                return false;
            }

            url = s.Substring(p + 1, gt - p - 1);
            p = gt + 1;
        }
        else
        {
            var urlStart = p;
            var parens = 0;
            while (p < end && s[p] != ' ' && s[p] != '\n')
            {
                if (s[p] == '\\' && p + 1 < end)
                {
                    p++;
                }
                else if (s[p] == '(')
                {
                    parens++;
                }
                else if (s[p] == ')')
                {
                    if (parens == 0)
                    {
                        break;
                    }

                    parens--;
                }

                p++;
            }

            url = s.Substring(urlStart, p - urlStart);
        }

        while (p < end && s[p] == ' ')
        {
            p++;
        }

        if (p < end && (s[p] == '"' || s[p] == '\''))
        {
            var q = s.IndexOf(s[p], p + 1, end - p - 1);
            if (q < 0)
            {
                return false;
            }

            p = q + 1;
            while (p < end && s[p] == ' ')
            {
                p++;
            }
        }

        if (p >= end || s[p] != ')')
        {
            return false;
        }

        Flush(sb, o);
        var label = new List<MdInline>();
        ParseInlineRange(s.Substring(open + 1, close - open - 1), 0, close - open - 1, label);
        o.Add(new MdInline(MdInlineKind.Link, url: Unescape(url), children: label));
        next = p + 1;
        return true;
    }

    private static string Unescape(string url)
    {
        if (url.IndexOf('\\') < 0)
        {
            return url;
        }

        var sb = new StringBuilder(url.Length);
        for (var i = 0; i < url.Length; i++)
        {
            if (url[i] == '\\' && i + 1 < url.Length && IsAsciiPunct(url[i + 1]))
            {
                i++;
            }

            sb.Append(url[i]);
        }

        return sb.ToString();
    }

    private static bool TryAutolink(string s, int open, int end, List<MdInline> o, StringBuilder sb, out int next)
    {
        next = open;
        var gt = s.IndexOf('>', open + 1, end - open - 1);
        if (gt < 0)
        {
            return false;
        }

        var body = s.Substring(open + 1, gt - open - 1);
        if (body.Length == 0 || body.AsSpan().IndexOfAny(" \n<") >= 0)
        {
            return false;
        }

        string? url = null;
        var colon = body.IndexOf(':');
        var at = body.IndexOf('@');
        if (colon >= 2 && char.IsAsciiLetter(body[0]) && colon < body.Length - 1)
        {
            url = body;
        }
        else if (at > 0 && at < body.Length - 1)
        {
            url = "mailto:" + body;
        }

        if (url is null)
        {
            return false;
        }

        Flush(sb, o);
        o.Add(new MdInline(MdInlineKind.Link, url: url, children: [new MdInline(MdInlineKind.Text, body)]));
        next = gt + 1;
        return true;
    }

    private static bool TryBareUrl(string s, int i, int end, List<MdInline> o, StringBuilder sb, out int next)
    {
        next = i;
        var span = s.AsSpan(i, end - i);
        var schemeLen = span.StartsWith("https://", StringComparison.Ordinal) ? 8 : span.StartsWith("http://", StringComparison.Ordinal) ? 7 : 0;
        if (schemeLen == 0)
        {
            return false;
        }

        var q = i;
        while (q < end && !char.IsWhiteSpace(s[q]) && s[q] != '<')
        {
            q++;
        }

        while (q > i)
        {
            var last = s[q - 1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '*' or '_' or '~' or '\'' or '"')
            {
                q--;
            }
            else if (last == ')' && s.AsSpan(i, q - i).Count('(') < s.AsSpan(i, q - i).Count(')'))
            {
                q--;
            }
            else
            {
                break;
            }
        }

        if (q - i <= schemeLen)
        {
            return false;
        }

        Flush(sb, o);
        var url = s.Substring(i, q - i);
        o.Add(new MdInline(MdInlineKind.Link, url: url, children: [new MdInline(MdInlineKind.Text, url)]));
        next = q;
        return true;
    }
}
