using System.Text;

namespace Forge.Workspace;

/// <summary>
/// One frontmatter entry. A scalar is a single value; a list keeps the order written.
/// <see cref="RawText"/> holds the original lines of a field whose syntax this parser does not support, so an
/// update writes it back untouched instead of rewriting it into something else.
/// </summary>
public sealed record SpecField(string Name, IReadOnlyList<string> Values)
{
    public string? RawText { get; init; }
}

/// <summary>
/// YAML-like frontmatter of a spec file: flat <c>name: value</c> pairs, flow lists (<c>[a, "b,c"]</c>) and block
/// lists (<c>- a</c>). Quoted scalars and list items are scanned with their escapes, so parsing and writing are
/// inverse; unsupported syntax keeps its raw text. Unknown fields are kept in their original order so updates
/// round-trip them.
/// </summary>
public sealed class SpecFrontmatter
{
    public const string IdField = "id";
    public const string TypeField = "type";
    public const string TitleField = "title";
    public const string StatusField = "status";
    public const string ParentField = "parent";
    public const string DependsOnField = "depends-on";
    public const string ReferencesField = "references";
    public const string ImplementsField = "implements";
    public const string CoversField = "covers";
    public const string TagsField = "tags";

    private static readonly HashSet<string> ListFields = new(StringComparer.Ordinal)
    {
        DependsOnField, ReferencesField, ImplementsField, CoversField, TagsField,
    };

    private readonly Dictionary<string, int> indexByName;

    public SpecFrontmatter(IEnumerable<SpecField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var list = new List<SpecField>();
        indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field.Name);
            var existing = indexByName.GetValueOrDefault(field.Name, -1);
            if (existing >= 0)
            {
                list[existing] = field;
            }
            else
            {
                indexByName[field.Name] = list.Count;
                list.Add(field);
            }
        }

        Fields = list;
    }

    public IReadOnlyList<SpecField> Fields { get; }

    public string? Id => Scalar(IdField);

    public string? Type => Scalar(TypeField);

    public string? Title => Scalar(TitleField);

    public string? Status => Scalar(StatusField);

    public string? Parent => Scalar(ParentField);

    public IReadOnlyList<string> DependsOn => List(DependsOnField);

    public IReadOnlyList<string> References => List(ReferencesField);

    public IReadOnlyList<string> Implements => List(ImplementsField);

    public IReadOnlyList<string> Covers => List(CoversField);

    public IReadOnlyList<string> Tags => List(TagsField);

    /// <summary>True for fields whose value is always a list, so callers never render them as a bare scalar.</summary>
    public static bool IsListField(string name) => ListFields.Contains(name);

    public bool Has(string name) => indexByName.ContainsKey(name);

    public string? Scalar(string name) => indexByName.TryGetValue(name, out var index) && Fields[index].Values.Count > 0
        ? Fields[index].Values[0]
        : null;

    public IReadOnlyList<string> List(string name) =>
        indexByName.TryGetValue(name, out var index) ? Fields[index].Values : Array.Empty<string>();

    /// <summary>
    /// Parses a file that starts with a <c>---</c> line. <paramref name="body"/> is everything after the closing
    /// line, keeping its leading newline.
    /// </summary>
    public static bool TryParse(string text, out SpecFrontmatter frontmatter, out string body)
    {
        ArgumentNullException.ThrowIfNull(text);
        frontmatter = Empty;
        body = text;

        var start = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;
        var firstLineEnd = text.IndexOf('\n', start);
        if (firstLineEnd < 0)
        {
            return false;
        }

        if (!text.AsSpan(start, firstLineEnd - start).Trim().SequenceEqual("---"))
        {
            return false;
        }

        var lines = new List<string>();
        var position = firstLineEnd + 1;
        while (position <= text.Length)
        {
            var end = text.IndexOf('\n', position);
            var hasNewLine = end >= 0;
            if (!hasNewLine)
            {
                end = text.Length;
            }

            var line = text[position..end].TrimEnd('\r');
            if (line.Trim().SequenceEqual("---"))
            {
                body = hasNewLine ? text[(end + 1)..] : string.Empty;
                frontmatter = new SpecFrontmatter(ReadFields(lines));
                return true;
            }

            lines.Add(line);
            if (!hasNewLine)
            {
                break;
            }

            position = end + 1;
        }

        return false;
    }

    public static SpecFrontmatter Empty { get; } = new(Array.Empty<SpecField>());

    private static List<SpecField> ReadFields(List<string> lines)
    {
        var fields = new List<SpecField>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-")
            {
                if (fields.Count == 0)
                {
                    continue;
                }

                var last = fields[^1];
                var itemText = trimmed.Length > 2 ? trimmed[2..].Trim() : string.Empty;
                if (last.RawText is not null)
                {
                    fields[^1] = last with { RawText = last.RawText + "\n" + line };
                }
                else if (TryParseScalar(itemText, out var item))
                {
                    fields[^1] = last with { Values = [.. last.Values, item] };
                }
                else
                {
                    fields[^1] = last with { RawText = line };
                }

                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var value = line[(colon + 1)..].Trim();
            if (value.Length == 0)
            {
                fields.Add(new SpecField(name, []));
                continue;
            }

            if (IsUnsupported(value))
            {
                var raw = new StringBuilder(line);
                while (index + 1 < lines.Count && IsContinuation(lines[index + 1]))
                {
                    raw.Append('\n').Append(lines[++index]);
                }

                fields.Add(new SpecField(name, []) { RawText = raw.ToString() });
                continue;
            }

            if (value[0] == '[')
            {
                if (TryParseFlowList(value, out var items))
                {
                    fields.Add(new SpecField(name, items));
                }
                else
                {
                    fields.Add(new SpecField(name, []) { RawText = line });
                }

                continue;
            }

            if (TryParseScalar(value, out var scalar))
            {
                fields.Add(new SpecField(name, [scalar]));
            }
            else
            {
                fields.Add(new SpecField(name, []) { RawText = line });
            }
        }

        return fields;
    }

    /// <summary>
    /// True for values this parser cannot represent faithfully: block scalars, flow maps, anchors, tags, aliases
    /// and anything quoted that is not closed on the same line. Their raw text is preserved instead of rewritten.
    /// </summary>
    private static bool IsUnsupported(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        switch (value[0])
        {
            case '|' or '>' or '{' or '&' or '*' or '!':
                return true;
            case '[':
                return !value.EndsWith(']');
            case '"' or '\'':
                return !HasClosingQuote(value);
            default:
                return false;
        }
    }

    private static bool HasClosingQuote(string value)
    {
        var quote = value[0];
        for (var i = 1; i < value.Length; i++)
        {
            if (quote == '"' && value[i] == '\\')
            {
                i++;
                continue;
            }

            if (value[i] == quote)
            {
                if (quote == '\'' && i + 1 < value.Length && value[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                return i == value.Length - 1;
            }
        }

        return false;
    }

    private static bool IsContinuation(string line)
    {
        if (line.Length == 0)
        {
            return false;
        }

        if (line[0] is ' ' or '\t')
        {
            return true;
        }

        var trimmed = line.TrimStart();
        return trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-";
    }

    /// <summary>Reads one scalar, including its quoting; false when the text is not a scalar this parser understands.</summary>
    private static bool TryParseScalar(string value, out string parsed)
    {
        parsed = value;
        if (value.Length == 0)
        {
            return true;
        }

        var quote = value[0];
        if (quote is not ('"' or '\''))
        {
            return true;
        }

        if (!HasClosingQuote(value))
        {
            return false;
        }

        var inner = value[1..^1];
        parsed = quote == '"' ? Unescape(inner) : inner.Replace("''", "'", StringComparison.Ordinal);
        return true;
    }

    /// <summary>Splits a flow list on commas that are outside quotes, honouring escapes inside double quotes.</summary>
    private static bool TryParseFlowList(string value, out List<string> items)
    {
        items = [];
        if (value.Length < 2 || value[0] != '[' || value[^1] != ']')
        {
            return false;
        }

        var inner = value[1..^1];
        if (inner.Trim().Length == 0)
        {
            return true;
        }

        var position = 0;
        while (position <= inner.Length)
        {
            while (position < inner.Length && inner[position] is ' ' or '\t')
            {
                position++;
            }

            if (position >= inner.Length)
            {
                return false;
            }

            var start = position;
            var quote = inner[position];
            if (quote is '"' or '\'')
            {
                position++;
                var closed = false;
                while (position < inner.Length)
                {
                    var ch = inner[position];
                    if (quote == '"' && ch == '\\')
                    {
                        position += 2;
                        continue;
                    }

                    if (ch == quote)
                    {
                        if (quote == '\'' && position + 1 < inner.Length && inner[position + 1] == '\'')
                        {
                            position += 2;
                            continue;
                        }

                        position++;
                        closed = true;
                        break;
                    }

                    position++;
                }

                if (!closed)
                {
                    return false;
                }

                var raw = inner[start..position];
                var item = quote == '"' ? Unescape(raw[1..^1]) : raw[1..^1].Replace("''", "'", StringComparison.Ordinal);
                while (position < inner.Length && inner[position] is ' ' or '\t')
                {
                    position++;
                }

                if (position < inner.Length && inner[position] != ',')
                {
                    return false;
                }

                items.Add(item);
            }
            else
            {
                var comma = inner.IndexOf(',', position);
                var end = comma < 0 ? inner.Length : comma;
                var raw = inner[start..end].TrimEnd();
                if (raw.Length == 0 || ContainsAnyOf(raw, "[]{}\"\'"))
                {
                    return false;
                }

                items.Add(raw);
                position = end;
            }

            if (position >= inner.Length)
            {
                return true;
            }

            position++;
        }

        return true;
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                builder.Append(value[i]);
                continue;
            }

            var next = value[++i];
            builder.Append(next switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '\\' => '\\',
                '"' => '"',
                _ => next,
            });
        }

        return builder.ToString();
    }

    private static bool ContainsAnyOf(ReadOnlySpan<char> value, ReadOnlySpan<char> candidates)
    {
        foreach (var ch in value)
        {
            if (candidates.Contains(ch))
            {
                return true;
            }
        }

        return false;
    }
}
