// Governed by THIRD_PARTY_NOTICES.md: adapted from Tint (MIT, Sebastien Ros), whose pattern compilation targets an
// Oniguruma-compatible engine. This build translates the same rewritten patterns to System.Text.RegularExpressions.

using System.Text.RegularExpressions;

namespace Forge.Editor.TextMate.Internal.Regexes;

// A grammar pattern translated to .NET syntax. Patterns start interpreted and switch to a compiled Regex once a
// tokenizer has searched them often enough to pay for the far higher cost of compilation; an untranslatable or invalid
// pattern is kept as a never-matching pattern together with the reason, so one bad rule degrades that rule instead of
// failing the document.
internal sealed class CompiledRegex
{
    private static readonly Regex s_never = new("(?!x)x", RegexOptions.CultureInvariant);

    // A search that takes longer than this on one line is abandoned and counts as no match: a pattern the translation
    // made ambiguous must not freeze the editor.
    private static readonly TimeSpan s_matchTimeout = TimeSpan.FromMilliseconds(500);

    private readonly string? _text;
    private readonly Regex? _interpreted;
    private Regex? _compiled;

    private CompiledRegex(Regex? interpreted, string? text, string? error)
    {
        _interpreted = interpreted;
        _text = text;
        Error = error;
    }

    public string? Error { get; }

    // Whether the compiled text anchors on \G, whose meaning depends on where the search starts, so no result can be
    // cached across search positions.
    public bool StartSensitive { get; init; }

    // Whether the pattern can match at all; an invalid pattern never does.
    public bool CanMatch => _interpreted is not null;

    // The interpreted pattern, or the never-matching one.
    public Regex Pattern => _interpreted ?? s_never;

    // The compiled pattern when a tokenizer promoted this pattern, the interpreted one otherwise.
    public Regex Current => Volatile.Read(ref _compiled) ?? Pattern;

    public static CompiledRegex Never(string error) => new(null, null, error);

    public static CompiledRegex Compile(string text)
    {
        if (!OnigurumaTranslator.TryTranslate(text, out var translated, out var error))
        {
            return new CompiledRegex(null, null, $"Invalid regular expression '{text}': {error}");
        }

        try
        {
            var regex = new Regex(translated!, RegexOptions.CultureInvariant, s_matchTimeout);
            return new CompiledRegex(regex, translated, null) { StartSensitive = ContainsStartAnchor(translated!) };
        }
        catch (ArgumentException exception)
        {
            return new CompiledRegex(null, null, $"Invalid regular expression '{text}': {exception.Message}");
        }
    }

    // Replaces the interpreted pattern with a compiled one. Concurrent callers may both build it; the first published
    // wins and the extra instance is discarded.
    public void Promote()
    {
        if (_text is null || Volatile.Read(ref _compiled) is not null)
        {
            return;
        }

        Regex compiled;
        try
        {
            compiled = new Regex(_text, RegexOptions.CultureInvariant | RegexOptions.Compiled, s_matchTimeout);
        }
        catch (ArgumentException)
        {
            // A pattern the interpreter accepts but the compiler rejects keeps running interpreted.
            return;
        }

        Interlocked.CompareExchange(ref _compiled, compiled, null);
    }

    private static bool ContainsStartAnchor(string pattern)
    {
        for (var i = pattern.IndexOf("\\G", StringComparison.Ordinal); i >= 0; i = pattern.IndexOf("\\G", i + 1, StringComparison.Ordinal))
        {
            var backslashes = 0;
            for (var j = i - 1; j >= 0 && pattern[j] == '\\'; j--)
            {
                backslashes++;
            }

            if (backslashes % 2 == 0)
            {
                return true;
            }
        }

        return false;
    }
}
