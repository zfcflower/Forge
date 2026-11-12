using System.Globalization;
using System.Text;

namespace Forge.Core.Tools.Web;

/// <summary>
/// Self-written HTML to readable-text converter used by <c>web_fetch</c>. It is a forgiving single-pass tokenizer, not a
/// parser: it never builds a tree and never throws on malformed markup. Headings become <c>#</c> lines, list items
/// <c>-</c> lines, links <c>[text](absolute-url)</c>, <c>pre</c> a fenced block and inline <c>code</c> backticks;
/// script, style, noscript, nav, footer, svg and similar subtrees are dropped, entities are decoded and whitespace is
/// collapsed.
/// </summary>
public static class HtmlToText
{
    private static readonly HashSet<string> SkipTags = new(StringComparer.Ordinal)
    {
        "script", "style", "noscript", "nav", "footer", "svg", "template", "iframe", "object", "canvas", "select", "button",
    };

    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
    {
        "p", "div", "section", "article", "header", "main", "aside", "table", "thead", "tbody", "tfoot", "tr", "blockquote",
        "form", "fieldset", "figure", "figcaption", "dl", "dt", "dd", "details", "summary", "address", "caption", "body",
        "html", "center", "hgroup",
    };

    private static readonly Dictionary<string, string> NamedEntities = new(StringComparer.Ordinal)
    {
        ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'", ["nbsp"] = " ",
        ["copy"] = "©", ["reg"] = "®", ["trade"] = "™", ["mdash"] = "—", ["ndash"] = "–",
        ["hellip"] = "…", ["laquo"] = "«", ["raquo"] = "»", ["ldquo"] = "“", ["rdquo"] = "”",
        ["lsquo"] = "‘", ["rsquo"] = "’", ["bull"] = "•", ["middot"] = "·", ["euro"] = "€",
        ["times"] = "×", ["deg"] = "°", ["pound"] = "£", ["yen"] = "¥", ["sect"] = "§",
        ["para"] = "¶", ["larr"] = "←", ["rarr"] = "→",
    };

    /// <summary>Converts <paramref name="html"/> to text; <paramref name="baseUri"/> absolutizes relative links.</summary>
    public static string Convert(string html, Uri? baseUri, out string? title)
    {
        ArgumentNullException.ThrowIfNull(html);
        var writer = new Writer(baseUri);
        writer.Run(html);
        title = writer.Title;
        return writer.Finish();
    }

    /// <summary>Plain text of an HTML fragment (tags dropped, entities decoded), for snippets that carry markup.</summary>
    public static string ToPlain(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (html.AsSpan().IndexOfAny('<', '&') < 0)
        {
            return html.Trim();
        }

        var text = Convert(html, null, out _);
        return text.Replace('\n', ' ').Trim();
    }

    /// <summary>Decodes character references in <paramref name="text"/>; unknown ones are left untouched.</summary>
    public static string DecodeEntities(string text)
    {
        var amp = text.IndexOf('&', StringComparison.Ordinal);
        if (amp < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var pos = 0;
        while (amp >= 0)
        {
            sb.Append(text, pos, amp - pos);
            var semi = text.IndexOf(';', amp + 1);
            if (semi > amp + 1 && semi - amp <= 12 && TryDecode(text.AsSpan(amp + 1, semi - amp - 1), out var decoded))
            {
                sb.Append(decoded);
                pos = semi + 1;
            }
            else
            {
                sb.Append('&');
                pos = amp + 1;
            }

            amp = text.IndexOf('&', pos);
        }

        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    private static bool TryDecode(ReadOnlySpan<char> name, out string decoded)
    {
        decoded = string.Empty;
        if (name[0] == '#')
        {
            var digits = name[1..];
            var style = NumberStyles.None;
            if (digits.Length > 0 && (digits[0] == 'x' || digits[0] == 'X'))
            {
                digits = digits[1..];
                style = NumberStyles.AllowHexSpecifier;
            }

            if (digits.Length == 0 || !int.TryParse(digits, style, CultureInfo.InvariantCulture, out var code))
            {
                return false;
            }

            if (code is <= 0 or > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF)
            {
                decoded = "�";
                return true;
            }

            decoded = char.ConvertFromUtf32(code);
            return true;
        }

        return NamedEntities.TryGetValue(name.ToString(), out decoded!);
    }

    private sealed class Writer
    {
        private readonly Uri? baseUri;
        private readonly StringBuilder sb = new();
        private readonly List<int> linkStarts = [];
        private readonly List<string?> linkTargets = [];
        private readonly List<bool> listIsOrdered = [];
        private readonly StringBuilder titleText = new();
        private bool pendingSpace;
        private int preDepth;
        private string? skipName;
        private int skipDepth;
        private bool inTitle;

        public Writer(Uri? baseUri) => this.baseUri = baseUri;

        public string? Title { get; private set; }

        public string Finish()
        {
            while (sb.Length > 0 && (sb[^1] is '\n' or ' ' or '\t'))
            {
                sb.Length--;
            }

            var start = 0;
            while (start < sb.Length && sb[start] == '\n')
            {
                start++;
            }

            return sb.ToString(start, sb.Length - start);
        }

        public void Run(string html)
        {
            var i = 0;
            var n = html.Length;
            while (i < n)
            {
                var lt = html.IndexOf('<', i);
                if (lt < 0)
                {
                    Text(html.AsSpan(i));
                    break;
                }

                if (lt > i)
                {
                    Text(html.AsSpan(i, lt - i));
                }

                i = Tag(html, lt);
            }
        }

        // Handles the markup starting at html[lt] == '<' and returns the index to continue from.
        private int Tag(string html, int lt)
        {
            var n = html.Length;
            if (lt + 1 >= n)
            {
                Text("<".AsSpan());
                return n;
            }

            var next = html[lt + 1];
            if (next == '!' || next == '?')
            {
                if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
                {
                    var end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                    return end < 0 ? n : end + 3;
                }

                var gt = html.IndexOf('>', lt + 1);
                return gt < 0 ? n : gt + 1;
            }

            var closing = next == '/';
            var nameStart = lt + (closing ? 2 : 1);
            var p = nameStart;
            while (p < n && (char.IsAsciiLetterOrDigit(html[p]) || html[p] is '-' or ':' or '_'))
            {
                p++;
            }

            if (p == nameStart || !char.IsAsciiLetter(html[nameStart]))
            {
                // Not a tag ("a < b"): the angle bracket is text.
                Text("<".AsSpan());
                return lt + 1;
            }

            var name = html.Substring(nameStart, p - nameStart).ToLowerInvariant();

            // Find the end of the tag, honoring quoted attribute values.
            var q = p;
            var quote = '\0';
            while (q < n)
            {
                var c = html[q];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '>')
                {
                    break;
                }

                q++;
            }

            if (q >= n)
            {
                // Unterminated tag: drop it rather than leak markup into the text.
                return n;
            }

            var attributes = html.AsSpan(p, q - p);
            var selfClosing = attributes.Length > 0 && attributes[^1] == '/';
            var after = q + 1;

            if (skipDepth > 0)
            {
                if (name == skipName && !selfClosing)
                {
                    skipDepth += closing ? -1 : 1;
                    if (skipDepth == 0)
                    {
                        skipName = null;
                    }
                }

                return after;
            }

            if (closing)
            {
                Close(name);
                return after;
            }

            if (SkipTags.Contains(name))
            {
                if (name is "script" or "style")
                {
                    // Raw text: the body may contain '<' that is not markup.
                    var end = html.IndexOf("</" + name, after, StringComparison.OrdinalIgnoreCase);
                    if (end < 0)
                    {
                        return n;
                    }

                    var gt = html.IndexOf('>', end);
                    return gt < 0 ? n : gt + 1;
                }

                if (!selfClosing)
                {
                    skipName = name;
                    skipDepth = 1;
                }

                return after;
            }

            Open(name, attributes);
            return after;
        }

        private void Open(string name, ReadOnlySpan<char> attributes)
        {
            switch (name)
            {
                case "title":
                    inTitle = Title is null;
                    titleText.Clear();
                    return;
                case "br":
                    Newline(1);
                    return;
                case "hr":
                    Newline(2);
                    AppendRaw("---");
                    Newline(2);
                    return;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Newline(2);
                    AppendRaw(new string('#', name[1] - '0') + " ");
                    return;
                case "ul" or "ol":
                    Newline(listIsOrdered.Count == 0 ? 2 : 1);
                    listIsOrdered.Add(name == "ol");
                    return;
                case "li":
                    Newline(1);
                    AppendRaw(new string(' ', Math.Max(0, listIsOrdered.Count - 1) * 2) + "- ");
                    return;
                case "pre":
                    Newline(2);
                    AppendRaw("```\n");
                    preDepth++;
                    return;
                case "code":
                    if (preDepth == 0)
                    {
                        Space();
                        AppendRaw("`");
                    }

                    return;
                case "td" or "th":
                    if (sb.Length > 0 && sb[^1] != '\n')
                    {
                        AppendRaw(" | ");
                    }

                    return;
                case "a":
                    Space();
                    linkStarts.Add(sb.Length);
                    linkTargets.Add(ResolveHref(Attribute(attributes, "href")));
                    return;
                default:
                    if (BlockTags.Contains(name))
                    {
                        Newline(2);
                    }

                    return;
            }
        }

        private void Close(string name)
        {
            switch (name)
            {
                case "title":
                    if (inTitle)
                    {
                        inTitle = false;
                        var t = Collapse(titleText.ToString());
                        if (t.Length > 0)
                        {
                            Title = t;
                        }
                    }

                    return;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "p" or "div":
                    Newline(2);
                    return;
                case "ul" or "ol":
                    if (listIsOrdered.Count > 0)
                    {
                        listIsOrdered.RemoveAt(listIsOrdered.Count - 1);
                    }

                    Newline(listIsOrdered.Count == 0 ? 2 : 1);
                    return;
                case "li" or "tr":
                    Newline(1);
                    return;
                case "pre":
                    if (preDepth > 0)
                    {
                        preDepth--;
                        if (preDepth == 0)
                        {
                            if (sb.Length > 0 && sb[^1] != '\n')
                            {
                                sb.Append('\n');
                            }

                            sb.Append("```");
                            Newline(2);
                        }
                    }

                    return;
                case "code":
                    if (preDepth == 0 && sb.Length > 0)
                    {
                        AppendRaw("`");
                    }

                    return;
                case "a":
                    CloseLink();
                    return;
                default:
                    if (BlockTags.Contains(name))
                    {
                        Newline(2);
                    }

                    return;
            }
        }

        private void CloseLink()
        {
            if (linkStarts.Count == 0)
            {
                return;
            }

            var start = linkStarts[^1];
            var target = linkTargets[^1];
            linkStarts.RemoveAt(linkStarts.Count - 1);
            linkTargets.RemoveAt(linkTargets.Count - 1);
            if (start > sb.Length)
            {
                return;
            }

            var label = Collapse(sb.ToString(start, sb.Length - start));
            sb.Length = start;
            pendingSpace = false;
            if (label.Length == 0)
            {
                return;
            }

            AppendRaw(target is null ? label : "[" + label + "](" + target + ")");
        }

        private string? ResolveHref(string? href)
        {
            if (string.IsNullOrWhiteSpace(href))
            {
                return null;
            }

            href = href.Trim();
            if (href[0] == '#')
            {
                return null;
            }

            Uri? absolute;
            if (baseUri is not null)
            {
                if (!Uri.TryCreate(baseUri, href, out absolute))
                {
                    return null;
                }
            }
            else if (!Uri.TryCreate(href, UriKind.Absolute, out absolute))
            {
                return null;
            }

            return absolute.Scheme is "http" or "https" ? absolute.AbsoluteUri : null;
        }

        private static string? Attribute(ReadOnlySpan<char> attributes, string wanted)
        {
            var i = 0;
            var n = attributes.Length;
            while (i < n)
            {
                while (i < n && (char.IsWhiteSpace(attributes[i]) || attributes[i] == '/'))
                {
                    i++;
                }

                var nameStart = i;
                while (i < n && !char.IsWhiteSpace(attributes[i]) && attributes[i] is not ('=' or '/' or '>'))
                {
                    i++;
                }

                var attributeName = attributes[nameStart..i];
                while (i < n && char.IsWhiteSpace(attributes[i]))
                {
                    i++;
                }

                string? value = null;
                if (i < n && attributes[i] == '=')
                {
                    i++;
                    while (i < n && char.IsWhiteSpace(attributes[i]))
                    {
                        i++;
                    }

                    if (i < n && (attributes[i] == '"' || attributes[i] == '\''))
                    {
                        var quote = attributes[i++];
                        var valueStart = i;
                        while (i < n && attributes[i] != quote)
                        {
                            i++;
                        }

                        value = attributes[valueStart..i].ToString();
                        if (i < n)
                        {
                            i++;
                        }
                    }
                    else
                    {
                        var valueStart = i;
                        while (i < n && !char.IsWhiteSpace(attributes[i]))
                        {
                            i++;
                        }

                        value = attributes[valueStart..i].ToString();
                    }
                }

                if (attributeName.Length > 0 && attributeName.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return value is null ? null : DecodeEntities(value);
                }

                if (i == nameStart)
                {
                    i++;
                }
            }

            return null;
        }

        private void Text(ReadOnlySpan<char> raw)
        {
            if (raw.Length == 0 || skipDepth > 0)
            {
                return;
            }

            var text = DecodeEntities(raw.ToString());
            if (inTitle)
            {
                titleText.Append(text);
                return;
            }

            if (preDepth > 0)
            {
                sb.Append(text.Replace("\r\n", "\n", StringComparison.Ordinal));
                return;
            }

            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = true;
                    continue;
                }

                Space();
                sb.Append(c);
            }
        }

        // Writes the whitespace a preceding text run asked for, so a position recorded afterwards starts at the next glyph.
        private void Space()
        {
            if (!pendingSpace)
            {
                return;
            }

            pendingSpace = false;
            if (sb.Length > 0 && sb[^1] is not ('\n' or ' '))
            {
                sb.Append(' ');
            }
        }

        private void AppendRaw(string text)
        {
            Space();
            sb.Append(text);
        }

        // Ends the current line and leaves (count - 1) blank lines; nothing is written at the very start.
        private void Newline(int count)
        {
            if (preDepth > 0)
            {
                return;
            }

            pendingSpace = false;
            if (sb.Length == 0)
            {
                return;
            }

            while (sb.Length > 0 && (sb[^1] == ' ' || sb[^1] == '\t'))
            {
                sb.Length--;
            }

            var trailing = 0;
            for (var k = sb.Length - 1; k >= 0 && sb[k] == '\n' && trailing < count; k--)
            {
                trailing++;
            }

            for (; trailing < count; trailing++)
            {
                sb.Append('\n');
            }
        }

        private static string Collapse(string text)
        {
            var result = new StringBuilder(text.Length);
            var space = false;
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    space = result.Length > 0;
                    continue;
                }

                if (space)
                {
                    result.Append(' ');
                    space = false;
                }

                result.Append(c);
            }

            return result.ToString();
        }
    }
}
