using System.Diagnostics.CodeAnalysis;
using System.Text;
using Forge.Editor.Lsp;
using Forge.Editor.Text;

namespace Forge.App.Editing;

/// <summary>Maps protocol results against one immutable snapshot off the UI thread.</summary>
internal static class EditorLspMapping
{
    public static bool TryStrictOffset(TextSnapshot snapshot, LspPosition position, PositionEncoding encoding, out int offset)
    {
        offset = 0;
        if (position.Line < 0 || position.Character < 0 || position.Line >= snapshot.LineCount)
        {
            return false;
        }

        offset = snapshot.OffsetOfCharacter(position.Line, position.Character, encoding);
        var actual = snapshot.OffsetToPosition(offset, encoding);
        return actual.Line == position.Line && actual.Character == position.Character;
    }

    public static int WordStart(TextSnapshot snapshot, int offset)
    {
        var start = Math.Clamp(offset, 0, snapshot.Length);
        while (start > 0 && (char.IsLetterOrDigit(snapshot[start - 1]) || snapshot[start - 1] == '_'))
        {
            start--;
        }

        return start;
    }

    public static EditorLspDiagnosticSet MapDiagnostics(DiagnosticSnapshot published, TextSnapshot text, PositionEncoding encoding)
    {
        var items = new List<EditorLspDiagnostic>(published.Diagnostics.Count);
        var malformed = 0;
        foreach (var diagnostic in published.Diagnostics)
        {
            var range = diagnostic.Range;
            if (diagnostic.Severity is < DiagnosticSeverity.Error or > DiagnosticSeverity.Hint ||
                range.Start.Line < 0 || range.Start.Character < 0 || range.End.Line < 0 || range.End.Character < 0 ||
                range.Start.Line >= text.LineCount)
            {
                malformed++;
                continue;
            }

            var start = text.OffsetOfCharacter(range.Start.Line, range.Start.Character, encoding);
            int end;
            if (range.End.Line < text.LineCount)
            {
                end = text.OffsetOfCharacter(range.End.Line, range.End.Character, encoding);
            }
            else if (range.End.Line == text.LineCount && range.End.Character == 0)
            {
                end = text.Length;
            }
            else
            {
                malformed++;
                continue;
            }

            if (end < start)
            {
                malformed++;
                continue;
            }

            if (end == start)
            {
                if (start < text.Length && text[start] is not ('\r' or '\n'))
                {
                    end = start + 1;
                }
                else if (start > 0 && text[start - 1] is not ('\r' or '\n'))
                {
                    end = start;
                    start--;
                }
                else
                {
                    continue;
                }
            }

            items.Add(new EditorLspDiagnostic(start, end, diagnostic.Severity, diagnostic.Message, diagnostic.Code, diagnostic.Source));
        }

        return new EditorLspDiagnosticSet(text.Version, items, malformed);
    }

    public static EditorLspCompletionMap MapCompletion(CompletionResult result, TextSnapshot text, int offset, PositionEncoding encoding, string newLine)
    {
        var prefixStart = WordStart(text, offset);
        var items = new List<EditorLspCompletionItem>(result.Items.Count);
        var snippets = 0;
        var invalid = result.MalformedItems;
        foreach (var item in result.Items)
        {
            // The client advertises no snippet support, so a snippet item would normally be dropped. It is kept instead,
            // with its placeholders reduced to their text, because dropping it hides a completion that works elsewhere.
            var mapped = item;
            if (item.InsertTextFormat == InsertTextFormat.Snippet)
            {
                snippets++;
                mapped = ToPlainText(item);
            }

            if (!TryMapItem(mapped, text, offset, prefixStart, encoding, newLine, out var completion))
            {
                invalid++;
                continue;
            }

            items.Add(completion);
        }

        items.Sort(static (a, b) =>
        {
            var order = string.CompareOrdinal(a.SortKey, b.SortKey);
            return order != 0 ? order : string.CompareOrdinal(a.Label, b.Label);
        });
        return new EditorLspCompletionMap(new EditorLspCompletion(text.Version, offset, prefixStart, items, result.IsIncomplete), snippets, invalid);
    }

    public static string NormalizeNewLines(string text, string newLine)
    {
        if (text.AsSpan().IndexOfAny('\r', '\n') < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                builder.Append(newLine);
            }
            else if (c == '\n')
            {
                builder.Append(newLine);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>The same item with its insert text reduced to the plain text of a snippet.</summary>
    private static CompletionItem ToPlainText(CompletionItem item) => item with
    {
        InsertText = item.InsertText is null ? null : EditorLspSnippets.ToPlainText(item.InsertText),
        TextEdit = item.TextEdit is null
            ? null
            : item.TextEdit with { NewText = EditorLspSnippets.ToPlainText(item.TextEdit.NewText) },
        InsertTextFormat = InsertTextFormat.PlainText,
    };

    private static bool TryMapItem(
        CompletionItem item, TextSnapshot text, int offset, int prefixStart, PositionEncoding encoding,
        string newLine, [NotNullWhen(true)] out EditorLspCompletionItem? mapped)
    {
        mapped = null;
        if (item.Label.Length == 0)
        {
            return false;
        }

        EditorLspEdit main;
        if (item.TextEdit is { } textEdit)
        {
            if (!TryMapEdit(textEdit, text, encoding, newLine, out main) || main.Start > offset || main.End < offset)
            {
                return false;
            }
        }
        else
        {
            main = new EditorLspEdit(prefixStart, offset, NormalizeNewLines(item.InsertText ?? item.Label, newLine));
        }

        var additional = Array.Empty<EditorLspEdit>();
        if (item.AdditionalTextEdits is { Count: > 0 } extra)
        {
            additional = new EditorLspEdit[extra.Count];
            for (var i = 0; i < additional.Length; i++)
            {
                if (!TryMapEdit(extra[i], text, encoding, newLine, out additional[i]))
                {
                    return false;
                }
            }

            if (!AreDisjoint(main, additional))
            {
                return false;
            }
        }

        mapped = new EditorLspCompletionItem(
            item.Label, string.IsNullOrEmpty(item.FilterText) ? item.Label : item.FilterText, item.Kind, item.Detail,
            EditorLspMarkup.ToPlain(item.Documentation, 1200, 20), item.SortText ?? item.Label, main, additional);
        return true;
    }

    private static bool TryMapEdit(LspTextEdit edit, TextSnapshot text, PositionEncoding encoding, string newLine, out EditorLspEdit mapped)
    {
        mapped = default;
        if (!TryStrictOffset(text, edit.Range.Start, encoding, out var start) ||
            !TryStrictOffset(text, edit.Range.End, encoding, out var end) || end < start)
        {
            return false;
        }

        mapped = new EditorLspEdit(start, end, NormalizeNewLines(edit.NewText, newLine));
        return true;
    }

    private static bool AreDisjoint(EditorLspEdit main, EditorLspEdit[] additional)
    {
        var all = new EditorLspEdit[additional.Length + 1];
        all[0] = main;
        Array.Copy(additional, 0, all, 1, additional.Length);
        Array.Sort(all, static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        for (var i = 1; i < all.Length; i++)
        {
            if (all[i].Start == all[i - 1].Start || all[i].Start < all[i - 1].End)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Converts server markup to inert plain text, never UI markup.</summary>
internal static class EditorLspMarkup
{
    public static string? ToPlain(string? markup, int maxChars = 4000, int maxLines = 40)
    {
        if (string.IsNullOrWhiteSpace(markup))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(markup.Length, maxChars));
        var inFence = false;
        var previousBlank = true;
        var lines = 0;
        foreach (var raw in markup.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith('#'))
                {
                    line = trimmed.TrimStart('#').TrimStart();
                }

                line = StripLinks(line)
                    .Replace("**", string.Empty, StringComparison.Ordinal)
                    .Replace("`", string.Empty, StringComparison.Ordinal)
                    .Replace("&nbsp;", " ", StringComparison.Ordinal)
                    .Replace("&lt;", "<", StringComparison.Ordinal)
                    .Replace("&gt;", ">", StringComparison.Ordinal)
                    .Replace("&amp;", "&", StringComparison.Ordinal);
            }

            line = RemoveControls(line);
            if (line.Length == 0)
            {
                if (previousBlank)
                {
                    continue;
                }

                previousBlank = true;
            }
            else
            {
                previousBlank = false;
            }

            if (lines >= maxLines || builder.Length + line.Length > maxChars)
            {
                builder.Append("\n…");
                break;
            }

            if (lines > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line);
            lines++;
        }

        var text = builder.ToString().TrimEnd();
        return text.Length == 0 ? null : text;
    }

    private static string StripLinks(string line)
    {
        if (line.IndexOf('[', StringComparison.Ordinal) < 0)
        {
            return line;
        }

        var builder = new StringBuilder(line.Length);
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] == '[')
            {
                var middle = line.IndexOf("](", i + 1, StringComparison.Ordinal);
                var end = middle < 0 ? -1 : line.IndexOf(')', middle + 2);
                if (middle > 0 && end > middle)
                {
                    builder.Append(line, i + 1, middle - i - 1);
                    i = end + 1;
                    continue;
                }
            }

            builder.Append(line[i]);
            i++;
        }

        return builder.ToString();
    }

    private static string RemoveControls(string line)
    {
        foreach (var c in line)
        {
            if (char.IsControl(c) && c != '\t')
            {
                return string.Concat(line.Where(static ch => !char.IsControl(ch) || ch == '\t'));
            }
        }

        return line;
    }
}
