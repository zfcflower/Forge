using System.Text;
using Forge.Abstractions;

namespace Forge.Core.Prompts;

/// <summary>
/// Hand-written reader for agent Markdown files: optional YAML-like frontmatter (name, description,
/// model, thinking, tools) followed by the prompt body. No leading <c>---</c> means the whole file is body.
/// </summary>
internal static class AgentMarkdownParser
{
    private const string NameKey = "name";
    private const string DescriptionKey = "description";
    private const string ModelKey = "model";
    private const string ThinkingKey = "thinking";
    private const string ToolsKey = "tools";
    private const string DefaultModel = "default";

    public static AgentDefinition Parse(string text, string fallbackName)
    {
        ArgumentNullException.ThrowIfNull(text);

        string? name = null;
        string? description = null;
        string? model = null;
        var thinking = ThinkingEffort.Off;
        List<string>? tools = null;
        var position = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;
        var body = position == 0 ? text : text[position..];
        if (TryReadLine(text, ref position, out var first) && IsDelimiter(first))
        {
            var terminated = false;
            var inTools = false;
            while (position < text.Length && TryReadLine(text, ref position, out var line))
            {
                if (IsDelimiter(line))
                {
                    terminated = true;
                    break;
                }

                var trimmed = line.Trim();
                if (trimmed.IsEmpty || trimmed[0] == '#')
                {
                    continue;
                }

                if (trimmed[0] == '-' && inTools)
                {
                    var item = ParseValue(trimmed[1..]);
                    if (item.Length > 0)
                    {
                        (tools ??= []).Add(item);
                    }

                    continue;
                }

                var colon = trimmed.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var key = trimmed[..colon].Trim();
                var value = trimmed[(colon + 1)..];

                if (KeyIs(key, NameKey))
                {
                    name = ParseValue(value);
                    inTools = false;
                }
                else if (KeyIs(key, DescriptionKey))
                {
                    description = ParseValue(value);
                    inTools = false;
                }
                else if (KeyIs(key, ModelKey))
                {
                    model = ParseValue(value);
                    inTools = false;
                }
                else if (KeyIs(key, ThinkingKey))
                {
                    thinking = ParseThinking(ParseValue(value));
                    inTools = false;
                }
                else if (KeyIs(key, ToolsKey))
                {
                    tools = ParseTools(value, tools);
                    inTools = true;
                }
                else
                {
                    inTools = false;
                }
            }

            if (terminated)
            {
                body = text[position..];
            }
            else
            {
                name = null;
                description = null;
                model = null;
                thinking = ThinkingEffort.Off;
                tools = null;
            }
        }

        var resolvedName = string.IsNullOrWhiteSpace(name) ? fallbackName : name.Trim();
        var resolvedModel = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        var prompt = TextNormalizer.Normalize(body);

        return new AgentDefinition(
            resolvedName,
            description?.Trim() ?? string.Empty,
            prompt,
            new ModelSettings(resolvedModel, thinking),
            tools is { Count: > 0 } ? tools.ToArray() : null);
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

    private static bool IsDelimiter(ReadOnlySpan<char> line)
    {
        var trimmed = line.Trim();
        return trimmed.SequenceEqual("---");
    }

    private static bool KeyIs(ReadOnlySpan<char> key, string expected) =>
        key.Equals(expected.AsSpan(), StringComparison.OrdinalIgnoreCase);

    private static ThinkingEffort ParseThinking(string value)
    {
        if (value.Equals("low", StringComparison.OrdinalIgnoreCase))
        {
            return ThinkingEffort.Low;
        }

        if (value.Equals("medium", StringComparison.OrdinalIgnoreCase))
        {
            return ThinkingEffort.Medium;
        }

        if (value.Equals("high", StringComparison.OrdinalIgnoreCase))
        {
            return ThinkingEffort.High;
        }

        return ThinkingEffort.Off;
    }

    private static List<string>? ParseTools(ReadOnlySpan<char> value, List<string>? existing)
    {
        value = StripComment(value.Trim()).Trim();
        if (value.StartsWith("[") && value.EndsWith("]"))
        {
            value = value[1..^1];
        }

        if (value.IsEmpty)
        {
            return existing;
        }

        var items = existing ?? [];
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
                    continue;
                }

                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c == '#' && (i == 0 || char.IsWhiteSpace(value[i - 1])))
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
        if (quote is '"' or '\'')
        {
            var close = FindClosingQuote(value, quote);
            if (close >= 0)
            {
                var inner = value[1..close];
                return quote == '"' ? Unescape(inner) : inner.ToString();
            }

            return value.ToString();
        }

        var comment = value.IndexOf('#');
        if (comment > 0 && char.IsWhiteSpace(value[comment - 1]))
        {
            value = value[..comment];
        }

        return value.TrimEnd().ToString();
    }

    private static int FindClosingQuote(ReadOnlySpan<char> value, char quote)
    {
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
                return i;
            }
        }

        return -1;
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
