using System.Runtime.CompilerServices;
using System.Text;

namespace Forge.IO;

/// <summary>
/// A path glob compiled once and matched many times. <c>*</c> matches any run of characters inside one path segment
/// (never a separator), <c>?</c> matches one character inside a segment, <c>**</c> as a whole segment matches any
/// number of segments, <c>[...]</c> is a character class with ranges and <c>!</c>/<c>^</c> negation, and <c>\</c> quotes
/// the character after it. Case folding follows <see cref="WorkspacePath.Comparer"/>.
/// <para>A leading <c>/</c> or any separator inside the pattern anchors it: the pattern then has to match the whole
/// path. A pattern without a separator names a single segment, which <see cref="IsPathMatch(ReadOnlySpan{char})"/>
/// accepts at any depth (the git pathspec rule) while <see cref="IsMatch(ReadOnlySpan{char})"/> does not.</para>
/// </summary>
public sealed class GlobMatcher
{
    /// <summary>Segments a path is indexed into on the stack; a deeper path indexes on the heap instead.</summary>
    private const int MaxStackSegments = 64;

    private static readonly bool IgnoreCase = WorkspacePath.Comparer == StringComparer.OrdinalIgnoreCase;

    private static readonly StringComparison PathComparison =
        IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly GlobToken[] NoTokens = [];

    private readonly Segment[] segments;

    /// <summary>The pattern with every escape resolved, when it has no wildcard at all; <c>null</c> otherwise.</summary>
    private readonly string? literal;

    private readonly bool segmentPattern;

    private GlobMatcher(string pattern, Segment[] segments, string? literal, bool segmentPattern)
    {
        this.Pattern = pattern;
        this.segments = segments;
        this.literal = literal;
        this.segmentPattern = segmentPattern;
    }

    /// <summary>The pattern as it was passed to <see cref="Compile"/>.</summary>
    public string Pattern { get; }

    /// <summary>
    /// True when the pattern names one path segment (no separator anywhere), so it can be matched against the last
    /// segment of a path at any depth.
    /// </summary>
    public bool IsSegmentPattern => segmentPattern;

    /// <summary>
    /// Compiles <paramref name="pattern"/>. A trailing separator is dropped (directory markers are the caller's
    /// concern); empty segments are ignored.
    /// </summary>
    /// <exception cref="ArgumentException">The pattern is empty or only separators.</exception>
    public static GlobMatcher Compile(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var text = pattern;
        var start = 0;
        var anchored = false;
        if (text[0] == '/')
        {
            anchored = true;
            start = 1;
        }

        var end = text.Length;
        while (end > start && text[end - 1] == '/')
        {
            end--;
        }

        if (end > start && !anchored)
        {
            anchored = text.AsSpan(start, end - start).IndexOf('/') >= 0;
        }

        var parsed = new List<Segment>(4);
        var wildcards = false;
        var index = start;
        while (index < end)
        {
            var next = text.IndexOf('/', index, end - index);
            if (next < 0)
            {
                next = end;
            }

            if (next > index)
            {
                var segment = ParseSegment(text.AsSpan(index, next - index), ref wildcards);
                parsed.Add(segment);
            }

            index = next + 1;
        }

        if (parsed.Count == 0)
        {
            throw new ArgumentException($"'{pattern}' names no path segment.", nameof(pattern));
        }

        // "a/**" means everything inside "a": the trailing wildcard run still needs one segment to land on.
        if (parsed.Count > 1 && parsed[^1].AnySegments)
        {
            parsed.Insert(parsed.Count - 1, new Segment([new GlobToken(GlobTokenKind.Run)], anySegments: false));
        }

        var segments = parsed.ToArray();
        string? literal = null;
        if (!wildcards)
        {
            var joined = new StringBuilder(pattern.Length);
            for (var index2 = 0; index2 < segments.Length; index2++)
            {
                if (index2 > 0)
                {
                    joined.Append('/');
                }

                joined.Append(segments[index2].LiteralSource);
            }

            literal = joined.ToString();
        }

        // "**" names every path, not the segment literally called "**".
        var segmentPattern = !anchored && !(segments.Length == 1 && segments[0].AnySegments);
        return new GlobMatcher(pattern, segments, literal, segmentPattern);
    }

    /// <summary>Matches the whole <paramref name="path"/> against the pattern.</summary>
    public bool IsMatch(string path) => IsMatch(path.AsSpan());

    /// <summary>Matches the whole <paramref name="path"/> against the pattern.</summary>
    public bool IsMatch(ReadOnlySpan<char> path)
    {
        if (literal is not null)
        {
            return MemoryExtensions.Equals(path, literal.AsSpan(), PathComparison);
        }

        return MatchSegments(path);
    }

    /// <summary>
    /// Matches <paramref name="path"/>, applying the git pathspec rule: a segment pattern is matched against the last
    /// segment at any depth, an anchored pattern against the whole path.
    /// </summary>
    public bool IsPathMatch(string path) => IsPathMatch(path.AsSpan());

    /// <summary>
    /// Matches <paramref name="path"/>, applying the git pathspec rule: a segment pattern is matched against the last
    /// segment at any depth, an anchored pattern against the whole path.
    /// </summary>
    public bool IsPathMatch(ReadOnlySpan<char> path)
    {
        if (!segmentPattern)
        {
            return IsMatch(path);
        }

        if (literal is not null)
        {
            var last = path.LastIndexOf('/');
            return MemoryExtensions.Equals(
                last < 0 ? path : path[(last + 1)..],
                literal.AsSpan(),
                PathComparison);
        }

        var offset = path.LastIndexOf('/');
        var name = offset < 0 ? path : path[(offset + 1)..];
        return !name.IsEmpty && MatchSegment(segments[0], name);
    }

    /// <inheritdoc />
    public override string ToString() => Pattern;

    private bool MatchSegments(ReadOnlySpan<char> path)
    {
        var count = 0;
        for (var index = 0; index < path.Length; index++)
        {
            if (path[index] != '/' && (index == 0 || path[index - 1] == '/'))
            {
                count++;
            }
        }

        // One int per segment start plus one per segment end.
        Span<int> buffer = count > MaxStackSegments ? new int[(count + 1) * 2] : stackalloc int[(MaxStackSegments + 1) * 2];
        var begins = buffer[..(count + 1)];
        var ends = buffer[(count + 1)..];

        var ordinals = 0;
        var cursor = 0;
        while (cursor < path.Length)
        {
            while (cursor < path.Length && path[cursor] == '/')
            {
                cursor++;
            }

            if (cursor >= path.Length)
            {
                break;
            }

            var segmentStart = cursor;
            while (cursor < path.Length && path[cursor] != '/')
            {
                cursor++;
            }

            begins[ordinals] = segmentStart;
            ends[ordinals] = cursor;
            ordinals++;
        }

        var patterns = segments;
        int patternIndex = 0, pathIndex = 0, starPattern = -1, starPath = -1;
        while (pathIndex < ordinals)
        {
            if (patternIndex < patterns.Length && !patterns[patternIndex].AnySegments
                && MatchSegment(patterns[patternIndex], path[begins[pathIndex]..ends[pathIndex]]))
            {
                patternIndex++;
                pathIndex++;
            }
            else if (patternIndex < patterns.Length && patterns[patternIndex].AnySegments)
            {
                starPattern = patternIndex;
                starPath = pathIndex;
                patternIndex++;
            }
            else if (starPattern >= 0 && starPath < ordinals)
            {
                starPath++;
                pathIndex = starPath;
                patternIndex = starPattern + 1;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < patterns.Length && patterns[patternIndex].AnySegments)
        {
            patternIndex++;
        }

        return patternIndex == patterns.Length;
    }

    private static bool MatchSegment(in Segment segment, ReadOnlySpan<char> text)
    {
        var tokens = segment.Tokens;
        int tokenIndex = 0, textIndex = 0, runToken = -1, runText = 0;
        while (textIndex < text.Length)
        {
            if (tokenIndex < tokens.Length && tokens[tokenIndex].Kind != GlobTokenKind.Run
                && tokens[tokenIndex].Matches(text[textIndex]))
            {
                tokenIndex++;
                textIndex++;
            }
            else if (tokenIndex < tokens.Length && tokens[tokenIndex].Kind == GlobTokenKind.Run)
            {
                runToken = tokenIndex;
                runText = textIndex;
                tokenIndex++;
            }
            else if (runToken >= 0 && runText < text.Length)
            {
                runText++;
                textIndex = runText;
                tokenIndex = runToken + 1;
            }
            else
            {
                return false;
            }
        }

        while (tokenIndex < tokens.Length && tokens[tokenIndex].Kind == GlobTokenKind.Run)
        {
            tokenIndex++;
        }

        return tokenIndex == tokens.Length;
    }

    private static Segment ParseSegment(ReadOnlySpan<char> text, ref bool wildcards)
    {
        if (text.Length == 2 && text[0] == '*' && text[1] == '*')
        {
            wildcards = true;
            return new Segment(NoTokens, anySegments: true);
        }

        var tokens = new List<GlobToken>(text.Length);
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var current = text[index];
            switch (current)
            {
                case '\\' when index + 1 < text.Length:
                    index++;
                    tokens.Add(new GlobToken(GlobTokenKind.Literal, text[index]));
                    builder.Append(text[index]);
                    break;
                case '*':
                    // Inside one segment "**" is two stars, exactly like git's wildmatch.
                    while (index + 1 < text.Length && text[index + 1] == '*')
                    {
                        index++;
                    }

                    wildcards = true;
                    tokens.Add(new GlobToken(GlobTokenKind.Run));
                    break;
                case '?':
                    wildcards = true;
                    tokens.Add(new GlobToken(GlobTokenKind.AnyCharacter));
                    break;
                case '[' when TryParseClass(text, ref index, out var characterClass):
                    wildcards = true;
                    tokens.Add(new GlobToken(GlobTokenKind.Class, default, characterClass));
                    break;
                default:
                    tokens.Add(new GlobToken(GlobTokenKind.Literal, current));
                    builder.Append(current);
                    break;
            }

            index++;
        }

        return new Segment(tokens.ToArray(), anySegments: false, builder.ToString());
    }

    private static bool TryParseClass(ReadOnlySpan<char> text, ref int index, out GlobClass characterClass)
    {
        characterClass = null!;
        var cursor = index + 1;
        var negated = false;
        if (cursor < text.Length && text[cursor] is '!' or '^')
        {
            negated = true;
            cursor++;
        }

        var first = cursor;
        var ranges = new List<char>(8);
        var closed = false;
        while (cursor < text.Length)
        {
            var current = text[cursor];
            if (current == '\\' && cursor + 1 < text.Length)
            {
                cursor++;
                current = text[cursor];
            }

            if (current == ']' && cursor > first)
            {
                closed = true;
                break;
            }

            var high = current;
            if (cursor + 2 < text.Length && text[cursor + 1] == '-' && text[cursor + 2] != ']')
            {
                cursor += 2;
                high = text[cursor];
                if (high == '\\' && cursor + 1 < text.Length)
                {
                    cursor++;
                    high = text[cursor];
                }
            }

            ranges.Add(current);
            ranges.Add(high);
            cursor++;
        }

        if (!closed)
        {
            return false;
        }

        index = cursor;
        characterClass = new GlobClass(ranges.ToArray(), negated);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SameCharacter(char left, char right) =>
        IgnoreCase
            ? char.ToUpperInvariant(left) == char.ToUpperInvariant(right)
            : left == right;

    private enum GlobTokenKind : byte
    {
        Literal,
        AnyCharacter,
        Run,
        Class,
    }

    private readonly struct GlobToken(GlobTokenKind kind, char literal = default, GlobClass? characterClass = null)
    {
        public GlobTokenKind Kind { get; } = kind;

        public char Literal { get; } = literal;

        private GlobClass? CharacterClass { get; } = characterClass;

        public bool Matches(char value) => Kind switch
        {
            GlobTokenKind.Literal => SameCharacter(Literal, value),
            GlobTokenKind.AnyCharacter => true,
            GlobTokenKind.Class => CharacterClass!.Matches(value),
            _ => false,
        };
    }

    private sealed class GlobClass(char[] ranges, bool negated)
    {
        public bool Matches(char value)
        {
            var target = IgnoreCase ? char.ToUpperInvariant(value) : value;
            var hit = false;
            for (var index = 0; index < ranges.Length; index += 2)
            {
                var low = IgnoreCase ? char.ToUpperInvariant(ranges[index]) : ranges[index];
                var high = IgnoreCase ? char.ToUpperInvariant(ranges[index + 1]) : ranges[index + 1];
                if (target >= low && target <= high)
                {
                    hit = true;
                    break;
                }
            }

            return hit != negated;
        }
    }

    private readonly struct Segment(GlobToken[] tokens, bool anySegments, string? literalSource = null)
    {
        public GlobToken[] Tokens { get; } = tokens;

        public bool AnySegments { get; } = anySegments;

        public string? LiteralSource { get; } = literalSource;
    }
}
