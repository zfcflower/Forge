namespace Forge.App.Services;

/// <summary>
/// The query side of the palette: the leading character that switches source, and the ranking every source shares.
/// <para>Ranking is deliberately the cheap one: an exact match, then a prefix, then a segment start, then a substring,
/// then the query's characters in order anywhere in the text. Each step is a single scan, and a keystroke runs it once
/// per snapshot entry, which is what keeps typing over a 20 000-file workspace inside one frame.</para>
/// </summary>
internal static class PaletteQuery
{
    /// <summary>Switches to the command source; VS Code's convention, so the gesture and the typed prefix agree.</summary>
    public const char CommandsPrefix = '>';

    /// <summary>Switches to the content source.</summary>
    public const char ContentPrefix = '#';

    /// <summary>Switches to the session source.</summary>
    public const char SessionsPrefix = ':';

    /// <summary>Switches to the symbol source.</summary>
    public const char SymbolsPrefix = '@';

    /// <summary>
    /// Splits a typed line into the source it switches to (null = keep the current one) and the rest of the query. The
    /// prefix only counts as the first character of the line, so a query that merely contains <c>&gt;</c> is a query.
    /// </summary>
    public static (PaletteSource? Source, string Text) Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return (null, string.Empty);
        }

        var source = raw[0] switch
        {
            CommandsPrefix => PaletteSource.Commands,
            ContentPrefix => PaletteSource.Content,
            SessionsPrefix => PaletteSource.Sessions,
            SymbolsPrefix => PaletteSource.Symbols,
            _ => (PaletteSource?)null,
        };

        return source is null ? (null, raw) : (source, raw[1..]);
    }

    /// <summary>The prefix that switches to <paramref name="source"/>; '\0' for a source without one (files is the default).</summary>
    public static char PrefixOf(PaletteSource source) => source switch
    {
        PaletteSource.Commands => CommandsPrefix,
        PaletteSource.Content => ContentPrefix,
        PaletteSource.Sessions => SessionsPrefix,
        PaletteSource.Symbols => SymbolsPrefix,
        _ => '\0',
    };

    /// <summary>
    /// The rank of <paramref name="text"/> against <paramref name="query"/>: 0 exact, 1 prefix, 2 segment start, 3
    /// substring, 4 subsequence, -1 no match. An empty query matches everything with rank 0.
    /// </summary>
    public static int Score(string? text, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return 0;
        }

        if (string.IsNullOrEmpty(text))
        {
            return -1;
        }

        if (text.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        // A match that starts a word - after '/', '\', '.', ':', '-' or a space - reads as better than one in the middle
        // of a word, which is what makes "mod" find "src/Models.cs" before "Commands.cs".
        for (var index = 1; index + query.Length <= text.Length; index++)
        {
            if (!IsSegmentStart(text[index - 1]))
            {
                continue;
            }

            if (text.AsSpan(index).StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
        }

        if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return ComposerSuggestions.IsSubsequence(text, query) ? 4 : -1;
    }

    /// <summary>
    /// The better of the two ranks, with <paramref name="secondary"/> never beating <paramref name="primary"/>: a hit in
    /// the secondary text (a file's directory, a session's project) ranks one step worse than the same hit in the title.
    /// </summary>
    public static int ScorePair(string? primary, string? secondary, string query)
    {
        var first = Score(primary, query);
        var second = Score(secondary, query);
        if (second < 0)
        {
            return first;
        }

        return first < 0 ? second + 1 : Math.Min(first, second + 1);
    }

    /// <summary>Clamps a configured result limit into the range the settings page also allows.</summary>
    public static int ClampLimit(int limit) =>
        Math.Clamp(limit, Preferences.AppPreferences.MinPalettePerSourceLimit, Preferences.AppPreferences.MaxPalettePerSourceLimit);

    private static bool IsSegmentStart(char previous) =>
        previous is '/' or '\\' or '.' or ':' or '-' or ' ' or '_' or '(';
}
