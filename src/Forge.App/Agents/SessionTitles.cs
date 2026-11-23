using System.Text;

namespace Forge.App.Agents;

/// <summary>
/// The title a session shows before anyone has named it. It keeps only what the first user message says about the task, and
/// nothing else: no code, no URLs, no paths, no mention references - those carry credentials, absolute locations and other
/// things that do not belong in a tab label, or in the notification and approval cards that repeat it.
/// </summary>
internal static class SessionTitles
{
    /// <summary>Longest derived title, in characters; single-line rows cut the rest off anyway.</summary>
    internal const int MaxLength = 48;

    /// <summary>Punctuation a kept word sheds from either end, so a label does not start or end with a colon.</summary>
    private static readonly char[] Edges = [' ', '\t', '.', ',', ':', ';', '!', '?', '(', ')', '[', ']', '"', '\'', '`',
        '，', '。', '、', '：', '；', '！', '？', '（', '）', '「', '」', '“', '”'];

    /// <summary>Where one sentence ends: the label keeps the words on either side, never the mark itself.</summary>
    private static readonly char[] SentenceBreaks = ['，', '。', '、', '：', '；', '！', '？', '（', '）', '「', '」', '“', '”',
        ',', ';', '!', '?', '(', ')', '[', ']', '"'];

    /// <summary>
    /// The derived title for <paramref name="text"/>, or null when it says nothing usable (empty, only an attachment mention,
    /// only code). Pure and cheap: it runs on the turn's own path before the first request.
    /// </summary>
    public static string? Derive(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = FirstUsableLine(text);
        if (line is null)
        {
            return null;
        }

        var kept = line
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(static word => word.Split(SentenceBreaks, StringSplitOptions.RemoveEmptyEntries))
            .Where(static word => !IsReference(word))
            .Where(static word => KeepsMeaning(word))
            .Select(static word => word.Trim(Edges))
            .Where(static word => word.Length > 0);

        var title = string.Join(' ', kept).Trim(Edges);
        if (title.Length == 0 || !title.Any(char.IsLetterOrDigit))
        {
            return null;
        }

        return title.Length <= MaxLength ? title : Cut(title);
    }

    /// <summary>The first line with a readable word in it; skips a leading fence, blank lines and a bare list marker.</summary>
    private static string? FirstUsableLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimStart(' ', '\t', '-', '*', '>', '#', '\r').Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Any(char.IsLetterOrDigit))
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>
    /// True for a whole word that is a location or a reference rather than part of the sentence: a URL, a path, a file name,
    /// an <c>@</c> mention or a <c>#</c> reference. Read after the sentence was cut at its punctuation, so the sentence word next
    /// to a path stays ("修复登录跳转：src/Auth/Login.cs" keeps the first half) and a URL still holds its own slashes and colons.
    /// </summary>
    private static bool IsReference(string word) =>
        word.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
        || word.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
        || word.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
        || word.Contains("://", StringComparison.Ordinal)
        || word.Contains('/') || word.Contains('\\') || word.Contains('`')
        || word.StartsWith('@') || word.StartsWith('#');

    /// <summary>False for a word with nothing readable in it ("--flag", "11-21", "14:00"), or a bare file name (App.cs).</summary>
    private static bool KeepsMeaning(string word)
    {
        // A file name: a dot with a short letter extension on the end (App.cs, main.ts). "3.5" is not one.
        var dot = word.LastIndexOf('.');
        if (dot > 0 && word.Length - dot is > 1 and <= 6 && word[(dot + 1)..].All(char.IsLetter))
        {
            return false;
        }

        return word.Any(char.IsLetter);
    }

    /// <summary>Cuts at a word boundary; a single long word is cut hard so the label never overflows its row.</summary>
    private static string Cut(string title)
    {
        var cut = title[..(MaxLength + 1)];
        var boundary = cut.LastIndexOf(' ');
        var result = (boundary > 0 ? cut[..boundary] : title[..MaxLength]).TrimEnd();
        return result.Length == 0 ? title[..MaxLength].TrimEnd() : result;
    }

    /// <summary>
    /// The line a still-unnamed session appends to its requests as a dynamic suffix: the model cannot see whether the session
    /// already has a name, so it is told once per session, and the line disappears on its own once a name is written.
    /// </summary>
    public static string Nudge(string? currentTitle) =>
        "Session title: this chat is still unnamed"
        + (string.IsNullOrWhiteSpace(currentTitle)
            ? string.Empty
            : $" (showing \"{currentTitle}\", a placeholder derived from the first message)")
        + ". Call set_title once with a 3-6 word name for this task, in the user's language; "
        + "do not call it again after it answers \"set\" or \"kept\".";
}
