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
    private readonly int[] _noMatchFrom;
    private readonly int[] _searches;
    private readonly bool _startSensitive;
    private string? _firstError;

    private OnigCapture[] _captures = new OnigCapture[8];
    private int _captureCount;
    private string? _cacheText;
    private int _cacheLength = -1;
    private int _cacheStart = int.MaxValue;

    // The searched region when it is shorter than the text, kept so consecutive searches reuse one string.
    private string? _region;
    private string? _regionSource;
    private int _regionLength = -1;

    public RuleScanner(CompiledRegex[] patterns, int[] ruleIds)
    {
        _patterns = patterns;
        _ruleIds = ruleIds;
        _nextMatch = new int[patterns.Length];
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

    // Searches text[..length] from start for the earliest match of any pattern.
    [MethodImpl(Jit.FullOpts)]
    public bool FindNextMatch(string text, int length, int start, out int ruleId)
    {
        if (!ReferenceEquals(text, _cacheText) || _cacheLength != length || start < _cacheStart)
        {
            _cacheText = text;
            _cacheLength = length;
            _cacheStart = start;
            if (!_startSensitive)
            {
                Array.Fill(_nextMatch, NotScanned);
            }
        }

        _cacheStart = start;

        var bestIndex = int.MaxValue;
        var bestPattern = -1;
        for (var i = 0; i < _patterns.Length; i++)
        {
            var index = _startSensitive ? Scan(i, text.AsSpan(), length, start) : Lookup(i, text.AsSpan(), length, start);
            if (index >= 0 && index < bestIndex)
            {
                bestIndex = index;
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
            ruleId = 0;
            return false;
        }

        ruleId = _ruleIds[bestPattern];

        // The captures come from a second search of the winning pattern, started where the winning search started:
        // searching from bestIndex instead would move \G to the match and could pick another alternative of the pattern.
        CollectCaptures(_patterns[bestPattern].Pattern, text, length, start);
        return true;
    }

    [MethodImpl(Jit.FullOpts)]
    private int Lookup(int pattern, ReadOnlySpan<char> text, int length, int start)
    {
        var cached = _nextMatch[pattern];
        if (cached >= start)
        {
            return cached;
        }

        if (cached == NoMatch && _noMatchFrom[pattern] <= start)
        {
            return NoMatch;
        }

        var index = Scan(pattern, text, length, start);
        if (index >= 0)
        {
            _nextMatch[pattern] = index;
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
    private int Scan(int pattern, ReadOnlySpan<char> text, int length, int start)
    {
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
            return enumerator.MoveNext() ? enumerator.Current.Index : NoMatch;
        }
        catch (RegexMatchTimeoutException exception)
        {
            _firstError ??= exception.Message;
            return NoMatch;
        }
    }

    [MethodImpl(Jit.FullOpts)]
    private void CollectCaptures(Regex regex, string text, int length, int searchStart)
    {
        Match match;
        try
        {
            // A region shorter than the text (a capture being retokenized) is searched as its own text, so that $ and
            // \z see its end; the common case searches the line itself.
            match = regex.Match(length == text.Length ? text : GetRegion(text, length), searchStart);
        }
        catch (RegexMatchTimeoutException exception)
        {
            _firstError ??= exception.Message;
            _captureCount = 0;
            return;
        }

        var count = match.Groups.Count;
        if (_captures.Length < count)
        {
            _captures = new OnigCapture[Math.Max(_captures.Length * 2, count)];
        }

        for (var i = 0; i < count; i++)
        {
            var group = match.Groups[i];
            _captures[i] = group.Success ? new OnigCapture(group.Index, group.Length, participated: true) : default;
        }

        _captureCount = count;
    }
}
