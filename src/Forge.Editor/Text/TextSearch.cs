using System.Buffers;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Forge.Editor.Text;

[Flags]
public enum SearchOptions
{
    None = 0,
    CaseSensitive = 1,
    WholeWord = 2,
    Regex = 4,

    /// <summary>Let a regular expression span line breaks; literal search always matches inside one line set.</summary>
    Multiline = 8,
}

public readonly record struct SearchMatch(int Offset, int Length)
{
    public int End => Offset + Length;
}

/// <summary>
/// Literal and regular-expression search over snapshots. Literal search streams the rope through a pooled buffer so
/// no full-text copy is made; regex search runs line by line unless <see cref="SearchOptions.Multiline"/> is set.
/// </summary>
public static class TextSearch
{
    private const int SliceSize = 64 * 1024;
    private const int RegexCacheLimit = 64;

    private static readonly ConcurrentDictionary<(string Pattern, SearchOptions Options), Regex> RegexCache = new();

    public static IEnumerable<SearchMatch> FindAll(TextSnapshot snapshot, string pattern, SearchOptions options = SearchOptions.None) =>
        FindAll(snapshot, pattern, options, 0);

    public static IEnumerable<SearchMatch> FindAll(TextSnapshot snapshot, string pattern, SearchOptions options, int startOffset)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        startOffset = Math.Clamp(startOffset, 0, snapshot.Length);
        return options.HasFlag(SearchOptions.Regex)
            ? FindRegex(snapshot, pattern, options, startOffset)
            : FindLiteral(snapshot, pattern, options, startOffset);
    }

    public static bool TryFindNext(TextSnapshot snapshot, string pattern, SearchOptions options, int startOffset, out SearchMatch match)
    {
        foreach (var candidate in FindAll(snapshot, pattern, options, startOffset))
        {
            match = candidate;
            return true;
        }

        match = default;
        return false;
    }

    public static int Count(TextSnapshot snapshot, string pattern, SearchOptions options = SearchOptions.None)
    {
        var count = 0;
        foreach (var _ in FindAll(snapshot, pattern, options))
        {
            count++;
        }

        return count;
    }

    private static IEnumerable<SearchMatch> FindLiteral(TextSnapshot snapshot, string pattern, SearchOptions options, int startOffset)
    {
        var comparison = options.HasFlag(SearchOptions.CaseSensitive)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var buffer = ArrayPool<char>.Shared.Rent(Math.Max(SliceSize, pattern.Length * 2));
        var hits = new List<int>(32);
        try
        {
            var position = startOffset;
            var acceptFrom = startOffset;
            while (position < snapshot.Length)
            {
                var take = Math.Min(buffer.Length, snapshot.Length - position);
                snapshot.CopyTo(position, buffer.AsSpan(0, take));

                var final = position + take >= snapshot.Length;
                var acceptUntil = final ? int.MaxValue : position + take - pattern.Length + 1;
                CollectLiteral(buffer, take, pattern, comparison, snapshot, options, acceptFrom, acceptUntil, position, hits);
                foreach (var hit in hits)
                {
                    yield return new SearchMatch(hit, pattern.Length);
                }

                hits.Clear();
                if (final)
                {
                    break;
                }

                acceptFrom = acceptUntil;
                position = acceptUntil;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static void CollectLiteral(
        char[] buffer,
        int take,
        string pattern,
        StringComparison comparison,
        TextSnapshot snapshot,
        SearchOptions options,
        int acceptFrom,
        int acceptUntil,
        int position,
        List<int> hits)
    {
        var slice = buffer.AsSpan(0, take);
        var index = 0;
        while (index <= take - pattern.Length)
        {
            var found = slice[index..].IndexOf(pattern.AsSpan(), comparison);
            if (found < 0)
            {
                break;
            }

            var absolute = position + index + found;
            if (absolute >= acceptFrom && absolute < acceptUntil &&
                (!options.HasFlag(SearchOptions.WholeWord) || IsWholeWord(snapshot, absolute, pattern.Length)))
            {
                hits.Add(absolute);
            }

            index += found + 1;
        }
    }

    private static IEnumerable<SearchMatch> FindRegex(TextSnapshot snapshot, string pattern, SearchOptions options, int startOffset)
    {
        var regex = RegexFor(pattern, options);
        var wholeWord = options.HasFlag(SearchOptions.WholeWord);
        if (options.HasFlag(SearchOptions.Multiline))
        {
            var text = snapshot.GetText();
            for (var match = regex.Match(text, Math.Min(startOffset, text.Length)); match.Success; match = match.NextMatch())
            {
                if (match.Length > 0 && (!wholeWord || IsWholeWord(snapshot, match.Index, match.Length)))
                {
                    yield return new SearchMatch(match.Index, match.Length);
                }

                if (match.Index + Math.Max(match.Length, 1) >= text.Length)
                {
                    break;
                }
            }

            yield break;
        }

        var lines = snapshot.Lines;
        var firstLine = lines.GetLineAtOffset(startOffset);
        for (var line = firstLine; line < lines.LineCount; line++)
        {
            var lineStart = lines.GetLineStart(line);
            var lineEnd = lines.GetLineEnd(line);
            if (lineEnd < startOffset)
            {
                continue;
            }

            var text = snapshot.GetText(lineStart, lineEnd - lineStart);
            var from = Math.Clamp(startOffset - lineStart, 0, text.Length);
            for (var match = regex.Match(text, from); match.Success; match = match.NextMatch())
            {
                if (match.Length == 0)
                {
                    continue;
                }

                var absolute = lineStart + match.Index;
                if (absolute < startOffset)
                {
                    continue;
                }

                if (wholeWord && !IsWholeWord(snapshot, absolute, match.Length))
                {
                    continue;
                }

                yield return new SearchMatch(absolute, match.Length);
            }
        }
    }

    private static bool IsWholeWord(TextSnapshot snapshot, int offset, int length)
    {
        if (offset > 0 && IsWordChar(snapshot[offset - 1]))
        {
            return false;
        }

        var end = offset + length;
        return end >= snapshot.Length || !IsWordChar(snapshot[end]);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static Regex RegexFor(string pattern, SearchOptions options)
    {
        var key = (pattern, options & (SearchOptions.CaseSensitive | SearchOptions.Multiline));
        if (RegexCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var regexOptions = RegexOptions.CultureInvariant | RegexOptions.Compiled;
        if (!options.HasFlag(SearchOptions.CaseSensitive))
        {
            regexOptions |= RegexOptions.IgnoreCase;
        }

        if (options.HasFlag(SearchOptions.Multiline))
        {
            regexOptions |= RegexOptions.Singleline;
        }

        var regex = new Regex(pattern, regexOptions, TimeSpan.FromSeconds(2));
        if (RegexCache.Count >= RegexCacheLimit)
        {
            RegexCache.Clear();
        }

        RegexCache[key] = regex;
        return regex;
    }
}
