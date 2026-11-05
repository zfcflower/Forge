// Governed by THIRD_PARTY_NOTICES.md: the multi-pattern scanner semantics are adapted from Tint (MIT, Sebastien Ros)
// and vscode-textmate (MIT, Microsoft).

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Forge.Editor.TextMate.Internal.Regexes;

// The compiled patterns of one rule and anchor variant: the earliest match wins, the first pattern on a tie, and every
// search is left to right so per-pattern results stay valid until the line ends. Owned by one tokenizer.
internal sealed class RuleScanner
{
    private const int NotScanned = int.MinValue;
    private const int PromotionThreshold = 128;
    private const int NoMatch = -1;

    private readonly CompiledRegex[] _patterns;
    private readonly int[] _ruleIds;
    private readonly int[] _nextMatch;
    private readonly int[] _nextMatchLength;
    private readonly int[] _noMatchFrom;
    private readonly int[] _searches;
    private readonly bool _startSensitive;
    private string? _firstError;

    private OnigCapture[] _captures = new OnigCapture[8];
    private int _captureCount;
    private int _cacheLine = -1;
    private int _cacheLength = -1;
    private int _cacheStart = int.MaxValue;
    private int _lastPattern = -1;

    // The searched region when it is shorter than the text, kept so consecutive searches reuse one string.
    private string? _region;
    private string? _regionSource;
    private int _regionLength = -1;

    public RuleScanner(CompiledRegex[] patterns, int[] ruleIds)
    {
        _patterns = patterns;
        _ruleIds = ruleIds;
        _nextMatch = new int[patterns.Length];
        _nextMatchLength = new int[patterns.Length];
        _noMatchFrom = new int[patterns.Length];
        _searches = new int[patterns.Length];
        Array.Fill(_nextMatch, NotScanned);
        foreach (var pattern in patterns)
        {
            if (pattern.Error is { } error)
            {
                _firstError ??= error;
            }

            _startSensitive |= pattern.StartSensitive;
        }
    }

    // The reason the first pattern of this scanner does not run, when it does not.
    public string? FirstError => _firstError;

    // The captures of the last successful search, group 0 first. Empty when the last search found nothing.
    public ReadOnlySpan<OnigCapture> Captures => _captures.AsSpan(0, _captureCount);

    // Searches text[..length] from start for the earliest match of any pattern, recording only group 0. Capture groups
    // are collected on demand by CollectCaptures, so patterns that never use them search the line without a string.
    [MethodImpl(Jit.FullOpts)]
    public bool FindNextMatch(ReadOnlySpan<char> text, int lineId, int length, int start, out int ruleId)
    {
        if (_cacheLine != lineId || _cacheLength != length || start < _cacheStart)
        {
            _cacheLine = lineId;
            _cacheLength = length;
            _cacheStart = start;
            if (!_startSensitive)
            {
                Array.Fill(_nextMatch, NotScanned);
            }
        }

        _cacheStart = start;

        var bestIndex = int.MaxValue;
        var bestLength = 0;
        var bestPattern = -1;
        for (var i = 0; i < _patterns.Length; i++)
        {
            var index = _startSensitive ? Scan(i, text, length, start, out var matchLength) : Lookup(i, text, length, start, out matchLength);
            if (index >= 0 && index < bestIndex)
            {
                bestIndex = index;
                bestLength = matchLength;
                bestPattern = i;
                if (index == start)
                {
                    break;
                }
            }
        }

        if (bestPattern < 0)
        {
            _captureCount = 0;
            _lastPattern = -1;
            ruleId = 0;
            return false;
        }

        ruleId = _ruleIds[bestPattern];
        _lastPattern = bestPattern;

        EnsureCaptures(1);
        _captures[0] = new OnigCapture(bestIndex, bestLength, participated: true);
        _captureCount = 1;
        return true;
    }

    // Runs the winning pattern again over the materialised line string, from the search start that produced the last
    // FindNextMatch result, and records every group. The search starts where the first search started: searching from
    // the match itself would move \G to the match and could pick another alternative of the pattern.
    [MethodImpl(Jit.FullOpts)]
    public int CollectCaptures(string text, int length, int searchStart)
    {
        if (_lastPattern < 0)
        {
            _captureCount = 0;
            return 0;
        }

        Match match;
        try
        {
            // A region shorter than the text (a capture being retokenized) is searched as its own text, so that $ and
            // \z see its end; the common case searches the line itself.
            match = _patterns[_lastPattern].Pattern.Match(length == text.Length ? text : GetRegion(text, length), searchStart);
        }
        catch (RegexMatchTimeoutException exception)
        {
            _firstError ??= exception.Message;
            _captureCount = 0;
            return 0;
        }

        var count = match.Groups.Count;
        EnsureCaptures(count);
        for (var i = 0; i < count; i++)
        {
            var group = match.Groups[i];
            _captures[i] = group.Success ? new OnigCapture(group.Index, group.Length, participated: true) : default;
        }

        _captureCount = count;
        return count;
    }

    [MethodImpl(Jit.FullOpts)]
    private int Lookup(int pattern, ReadOnlySpan<char> text, int length, int start, out int matchLength)
    {
        var cached = _nextMatch[pattern];
        if (cached >= start)
        {
            matchLength = _nextMatchLength[pattern];
            return cached;
        }

        if (cached == NoMatch && _noMatchFrom[pattern] <= start)
        {
            matchLength = 0;
            return NoMatch;
        }

        var index = Scan(pattern, text, length, start, out matchLength);
        if (index >= 0)
        {
            _nextMatch[pattern] = index;
            _nextMatchLength[pattern] = matchLength;
        }
        else
        {
            _nextMatch[pattern] = NoMatch;
            _noMatchFrom[pattern] = start;
        }

        return index;
    }

    [MethodImpl(Jit.FullOpts)]
    private string GetRegion(string text, int length)
    {
        if (!ReferenceEquals(_regionSource, text) || _regionLength != length)
        {
            _region = text.Substring(0, length);
            _regionSource = text;
            _regionLength = length;
        }

        return _region!;
    }

    [MethodImpl(Jit.FullOpts)]
    private int Scan(int pattern, ReadOnlySpan<char> text, int length, int start, out int matchLength)
    {
        matchLength = 0;
        var compiled = _patterns[pattern];
        if (!compiled.CanMatch)
        {
            return NoMatch;
        }

        // A pattern searched this often has repaid the cost of compiling it.
        if (++_searches[pattern] == PromotionThreshold)
        {
            compiled.Promote();
        }

        var regex = compiled.Current;

        try
        {
            var enumerator = regex.EnumerateMatches(text.Slice(0, length), start);
            if (enumerator.MoveNext())
            {
                var match = enumerator.Current;
                matchLength = match.Length;
                return match.Index;
            }

            return NoMatch;
        }
        catch (RegexMatchTimeoutException exception)
        {
            _firstError ??= exception.Message;
            return NoMatch;
        }
    }

    [MethodImpl(Jit.FullOpts)]
    private void EnsureCaptures(int count)
    {
        if (_captures.Length < count)
        {
            _captures = new OnigCapture[Math.Max(_captures.Length * 2, count)];
        }
    }
}
