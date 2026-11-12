using System.Text;

namespace Forge.Core.Prompts.Templates;

/// <summary>
/// The flat frontmatter of a template file: <c>key: value</c> scalars and lists (<c>[a, b]</c> inline or <c>- item</c>
/// lines under the key), between a leading <c>---</c> line and the next <c>---</c> line. No leading <c>---</c> means the
/// whole file is body. Keys are compared case-insensitively and stored lower-case; a repeated key keeps its last value.
/// Hand-written (no YAML library) so it stays AOT-safe and allocation-light.
/// </summary>
internal sealed class PromptFrontmatter
{
    private PromptFrontmatter(
        Dictionary<string, string> scalars,
        Dictionary<string, List<string>> lists,
        List<string> keyOrder,
        string body,
        bool unclosed)
    {
        Scalars = scalars;
        Lists = lists;
        Keys = keyOrder;
        Body = body;
        Unclosed = unclosed;
    }

    public IReadOnlyDictionary<string, string> Scalars { get; }

    public IReadOnlyDictionary<string, List<string>> Lists { get; }

    /// <summary>Every key seen, lower-case, in file order (duplicates removed).</summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>The text after the frontmatter, line endings as in the file.</summary>
    public string Body { get; }

    /// <summary>A <c>---</c> block was opened but never closed; then every key is dropped and the whole file is body.</summary>
    public bool Unclosed { get; }

    public string? Scalar(string key) => Scalars.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    public IReadOnlyList<string>? List(string key) => Lists.TryGetValue(key, out var value) ? value : null;

    public static PromptFrontmatter Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var scalars = new Dictionary<string, string>(StringComparer.Ordinal);
        var lists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var keys = new List<string>();
        var start = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        var position = start;
        if (!TryReadLine(text, ref position, out var first) || !IsDelimiter(first))
        {
            return new PromptFrontmatter(scalars, lists, keys, text[start..], unclosed: false);
        }

        string? listKey = null;
        while (TryReadLine(text, ref position, out var line))
        {
            if (IsDelimiter(line))
            {
                return new PromptFrontmatter(scalars, lists, keys, text[position..], unclosed: false);
            }

            var trimmed = line.Trim();
            if (trimmed.IsEmpty || trimmed[0] == '#')
            {
                continue;
            }

            if (trimmed[0] == '-' && listKey is not null)
            {
                var item = ParseValue(StripComment(trimmed[1..].Trim()));
                if (item.Length > 0)
                {
                    lists[listKey].Add(item);
                }

                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
            {
                listKey = null;
                continue;
            }

            var key = trimmed[..colon].Trim().ToString().ToLowerInvariant();
            var raw = StripComment(trimmed[(colon + 1)..].Trim());
            if (!keys.Contains(key))
            {
                keys.Add(key);
            }

            if (raw.IsEmpty)
            {
                // "tools:" followed by "- item" lines.
                scalars.Remove(key);
                lists[key] = [];
                listKey = key;
                continue;
            }

            listKey = null;
            if (raw[0] == '[' && raw[^1] == ']')
            {
                scalars.Remove(key);
                lists[key] = SplitInline(raw[1..^1]);
                continue;
            }

            lists.Remove(key);
            scalars[key] = ParseValue(raw);
        }

        // Unclosed: nothing above was frontmatter after all.
        return new PromptFrontmatter([], [], [], text[start..], unclosed: true);
    }

    /// <summary>Renders frontmatter + body back to text, LF line endings; used by the editor to build new files.</summary>
    /// <remarks>A key in <paramref name="listKeys"/> has a comma-separated value written as an inline <c>[a, b]</c> list.</remarks>
    public static string Render(IReadOnlyList<KeyValuePair<string, string>> scalars, string body, IReadOnlyList<string>? listKeys = null)
    {
        if (scalars.Count == 0)
        {
            return body;
        }

        var builder = new StringBuilder(body.Length + 64);
        builder.Append("---\n");
        foreach (var (key, value) in scalars)
        {
            builder.Append(key).Append(": ");
            if (listKeys is not null && listKeys.Contains(key, StringComparer.Ordinal))
            {
                builder.Append('[').Append(value).Append(']').Append('\n');
                continue;
            }

            builder.Append(Quote(value)).Append('\n');
        }

        builder.Append("---\n").Append(body);
        return builder.ToString();
    }

    private static string Quote(string value)
    {
        var needs = value.Length == 0 || value.AsSpan().IndexOfAny(":#\"'[]") >= 0 || value != value.Trim();
        if (!needs)
        {
            return value;
        }

        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static List<string> SplitInline(ReadOnlySpan<char> value)
    {
        var items = new List<string>();
        var start = 0;
        while (start <= value.Length)
        {
            var comma = value[start..].IndexOf(',');
            var end = comma < 0 ? value.Length : start + comma;
            var item = ParseValue(value[start..end]);
            if (item.Length > 0)
            {
                items.Add(item);
            }

            if (comma < 0)
            {
                break;
            }

            start = end + 1;
        }

        return items;
    }

    private static bool TryReadLine(string text, ref int position, out ReadOnlySpan<char> line)
    {
        if (position >= text.Length)
        {
            line = default;
            return false;
        }

        var span = text.AsSpan();
        var end = position;
        while (end < span.Length && span[end] != '\n' && span[end] != '\r')
        {
            end++;
        }

        line = span[position..end];
        position = end;
        if (position < span.Length)
        {
            position += span[position] == '\r' && position + 1 < span.Length && span[position + 1] == '\n' ? 2 : 1;
        }

        return true;
    }

    private static bool IsDelimiter(ReadOnlySpan<char> line) => line.Trim().SequenceEqual("---");

    private static ReadOnlySpan<char> StripComment(ReadOnlySpan<char> value)
    {
        var quote = '\0';
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(value[i - 1])))
            {
                return value[..i].TrimEnd();
            }
        }

        return value;
    }

    private static string ParseValue(ReadOnlySpan<char> value)
    {
        value = value.Trim();
        if (value.IsEmpty)
        {
            return string.Empty;
        }

        var quote = value[0];
        if (quote is not ('"' or '\''))
        {
            return value.ToString();
        }

        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && quote == '"')
            {
                i++;
                continue;
            }

            if (c == quote)
            {
                var inner = value[1..i];
                return quote == '"' ? Unescape(inner) : inner.ToString();
            }
        }

        return value.ToString();
    }

    private static string Unescape(ReadOnlySpan<char> value)
    {
        if (value.IndexOf('\\') < 0)
        {
            return value.ToString();
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                builder.Append(c);
                continue;
            }

            c = value[++i];
            builder.Append(c switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                _ => c,
            });
        }

        return builder.ToString();
    }
}
