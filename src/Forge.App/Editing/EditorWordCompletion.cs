using AvaloniaEdit.Document;
using Forge.Editor.Lsp;

namespace Forge.App.Editing;

/// <summary>
/// Completion without a language server: the words already in the document. This keeps "code completion" usable in files
/// whose language has no server, or where the server declares none, instead of leaving Ctrl+Space dead.
/// <para>The scan is bounded (a window of the document around the caret, a fixed number of words) and nothing is cached,
/// so a large file costs one pass on an explicit request rather than memory held per tab.</para>
/// </summary>
internal static class EditorWordCompletion
{
    /// <summary>Shortest word the editor offers; shorter prefixes match too many names to be useful.</summary>
    public const int MinWordLength = 3;

    /// <summary>Most items one request returns.</summary>
    public const int MaxItems = 200;

    /// <summary>Characters scanned per request; a window around the caret, not the whole document.</summary>
    public const int MaxScanChars = 262_144;

    /// <summary>
    /// Builds the candidate list at <paramref name="caretOffset"/>, or null when there is nothing to offer (no prefix, no
    /// words). Items replace the prefix exactly and carry the word as both label and insert text.
    /// </summary>
    public static EditorLspCompletion? Build(TextDocument document, int caretOffset)
    {
        ArgumentNullException.ThrowIfNull(document);
        var total = document.TextLength;
        var offset = Math.Clamp(caretOffset, 0, total);
        var prefixStart = WordStart(document, offset);
        if (offset - prefixStart <= 0)
        {
            return null;
        }

        var prefix = document.GetText(prefixStart, offset - prefixStart);
        var words = CollectWords(document, offset, out var scanned);
        if (words.Count == 0)
        {
            return null;
        }

        var items = new List<EditorLspCompletionItem>(Math.Min(words.Count, MaxItems));
        foreach (var word in words)
        {
            if (items.Count >= MaxItems)
            {
                break;
            }

            if (!word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || string.Equals(word, prefix, StringComparison.Ordinal))
            {
                continue;
            }

            items.Add(new EditorLspCompletionItem(
                word,
                word,
                CompletionItemKind.Text,
                null,
                null,
                word,
                new EditorLspEdit(prefixStart, offset, word),
                []));
        }

        if (items.Count == 0)
        {
            return null;
        }

        // The completion record carries a version only so a caller can check it is still the same document; word
        // completion is computed synchronously from the caller's own document, so any value it does not read is fine.
        return new EditorLspCompletion(-1, offset, prefixStart, items, IsIncomplete: false);
    }

    /// <summary>The start of the identifier-like run that ends at <paramref name="offset"/>.</summary>
    public static int WordStart(TextDocument document, int offset)
    {
        var start = Math.Clamp(offset, 0, document.TextLength);
        while (start > 0 && IsWordChar(document.GetCharAt(start - 1)))
        {
            start--;
        }

        return start;
    }

    /// <summary>Distinct words of the scan window, most frequent first, then alphabetical; the word at the caret is skipped.</summary>
    private static List<string> CollectWords(TextDocument document, int offset, out int scanned)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = document.TextLength;
        var window = Math.Min(total, MaxScanChars);
        var start = Math.Max(0, Math.Min(offset - (window / 2), total - window));
        var end = Math.Min(total, start + window);
        scanned = end - start;

        var current = new System.Text.StringBuilder(32);
        for (var i = start; i < end; i++)
        {
            var c = document.GetCharAt(i);
            if (IsWordChar(c))
            {
                if (current.Length < 64)
                {
                    current.Append(c);
                }

                continue;
            }

            Flush();
        }

        Flush();
        if (counts.Count == 0)
        {
            return [];
        }

        var words = new List<string>(counts.Keys);
        words.Sort((left, right) =>
        {
            var order = counts[right].CompareTo(counts[left]);
            return order != 0 ? order : string.CompareOrdinal(left, right);
        });
        return words;

        void Flush()
        {
            if (current.Length >= MinWordLength)
            {
                var word = current.ToString();
                counts[word] = counts.TryGetValue(word, out var count) ? count + 1 : 1;
            }

            current.Clear();
        }
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Diagnostics hook for tests: whether a character can appear inside an identifier.</summary>
    internal static bool IsWordCharacter(char c) => IsWordChar(c);
}
