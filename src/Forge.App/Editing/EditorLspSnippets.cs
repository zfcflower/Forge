using System.Text;

namespace Forge.App.Editing;

/// <summary>
/// Turns an LSP snippet into the plain text it would insert. The client advertises <c>snippetSupport: false</c>, so a
/// server should never send one; when it does anyway, dropping the item (the old behaviour) hides completions that work
/// everywhere else, so the editor inserts the snippet's literal text instead.
/// <para>Placeholders keep their default text (<c>${1:name}</c> → <c>name</c>), a placeholder without a default inserts
/// nothing (<c>$1</c>, the final <c>$0</c>, <c>$TM_FILENAME</c>), and a choice takes its first option. A malformed snippet
/// never produces visible syntax: an unclosed placeholder drops the rest of the text. Tab stops are not recreated — the
/// inserted text is ordinary text with the caret after it.</para>
/// </summary>
internal static class EditorLspSnippets
{
    /// <summary>The literal text of <paramref name="snippet"/>; never throws on malformed input.</summary>
    public static string ToPlainText(string snippet)
    {
        if (snippet.Length == 0 || (snippet.IndexOf('$') < 0 && snippet.IndexOf('\\') < 0))
        {
            return snippet;
        }

        var builder = new StringBuilder(snippet.Length);
        Write(snippet, 0, snippet.Length, builder);
        return builder.ToString();
    }

    /// <summary>Copies the literal text of <c>snippet[start..end)</c>; returns the index after what it consumed.</summary>
    private static int Write(string snippet, int start, int end, StringBuilder builder)
    {
        var i = start;
        while (i < end)
        {
            var c = snippet[i];
            if (c == '\\' && i + 1 < end && snippet[i + 1] is '$' or '}' or '\\')
            {
                builder.Append(snippet[i + 1]);
                i += 2;
                continue;
            }

            if (c == '$' && i + 1 < end)
            {
                if (snippet[i + 1] == '{')
                {
                    i = Placeholder(snippet, i + 2, end, builder);
                    continue;
                }

                if (char.IsDigit(snippet[i + 1]))
                {
                    i++;
                    while (i < end && char.IsDigit(snippet[i]))
                    {
                        i++;
                    }

                    continue;
                }

                if (char.IsLetter(snippet[i + 1]) || snippet[i + 1] == '_')
                {
                    i++;
                    while (i < end && (char.IsLetterOrDigit(snippet[i]) || snippet[i] == '_'))
                    {
                        i++;
                    }

                    continue;
                }
            }

            builder.Append(c);
            i++;
        }

        return i;
    }

    /// <summary>
    /// Reads <c>${...}</c> starting right after the brace. Returns the index after its closing brace; an unclosed
    /// placeholder consumes the rest of the text and rolls back whatever it appended.
    /// </summary>
    private static int Placeholder(string snippet, int at, int end, StringBuilder builder)
    {
        var mark = builder.Length;
        if (at >= end)
        {
            return Abandon(end, builder, mark);
        }

        if (!char.IsDigit(snippet[at]))
        {
            return Abandon(end, builder, mark);
        }

        var index = at;
        while (index < end && char.IsDigit(snippet[index]))
        {
            index++;
        }

        if (index >= end)
        {
            return Abandon(end, builder, mark);
        }

        if (snippet[index] == '|')
        {
            // ${1|one,two,three|}: the first choice is what the text would have started as.
            var close = snippet.IndexOf('|', index + 1);
            if (close < 0 || close + 1 >= end || snippet[close + 1] != '}')
            {
                return Abandon(end, builder, mark);
            }

            var choices = snippet.AsSpan(index + 1, close - index - 1);
            var separator = choices.IndexOf(',');
            builder.Append(separator < 0 ? choices : choices[..separator]);
            return close + 2;
        }

        if (snippet[index] == '}')
        {
            // A bare $1 or $0: nothing to insert.
            return index + 1;
        }

        if (snippet[index] != ':')
        {
            return Abandon(end, builder, mark);
        }

        // The default text may itself hold placeholders and escaped braces; it ends at the brace that closes this one.
        index++;
        var depth = 1;
        while (index < end)
        {
            var c = snippet[index];
            if (c == '\\' && index + 1 < end && snippet[index + 1] is '$' or '}' or '\\')
            {
                builder.Append(snippet[index + 1]);
                index += 2;
                continue;
            }

            if (c == '$' && index + 1 < end && snippet[index + 1] == '{')
            {
                index = Placeholder(snippet, index + 2, end, builder);
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return index + 1;
                }
            }

            builder.Append(c);
            index++;
        }

        return Abandon(end, builder, mark);
    }

    /// <summary>A malformed placeholder disappears entirely rather than leaving <c>${1:</c> in the document.</summary>
    private static int Abandon(int end, StringBuilder builder, int mark)
    {
        builder.Length = mark;
        return end;
    }
}
