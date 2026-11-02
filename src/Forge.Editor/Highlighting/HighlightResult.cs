// Governed by THIRD_PARTY_NOTICES.md.

using Forge.Editor.TextMate;

namespace Forge.Editor.Highlighting;

// One line of a highlight result. Immutable once the result is published: an incremental update replaces whole lines,
// so a result a UI thread holds stays valid while the next one is built.
internal sealed class HighlightLineData
{
    public static readonly HighlightLineData Empty = new([], null!, false);

    public HighlightLineData(HighlightToken[] tokens, StateStack endState, bool continued)
    {
        Tokens = tokens;
        EndState = endState;
        Continued = continued;
    }

    public HighlightToken[] Tokens { get; }

    // The tokenizer state after the line; null for a line that was never tokenized.
    public StateStack EndState { get; }

    // Whether the line starts inside a rule opened on an earlier line.
    public bool Continued { get; }
}

// An immutable snapshot of the tokens of every line, with the scope ids they reference and the categories a theme
// resolved for those ids.
public sealed class HighlightResult
{
    private readonly HighlightLineData[] _lines;
    private readonly TokenCategory[] _categories;
    private readonly int[] _foregrounds;

    internal HighlightResult(ScopeTable scopes, HighlightLineData[] lines, int lineCount, TokenCategory[] categories, int[] foregrounds, long version, string scopeName)
    {
        Scopes = scopes;
        _lines = lines;
        LineCount = lineCount;
        _categories = categories;
        _foregrounds = foregrounds;
        Version = version;
        ScopeName = scopeName;
    }

    // The scope stacks the tokens refer to.
    public ScopeTable Scopes { get; }

    public int LineCount { get; }

    // The root scope of the grammar the result was produced with.
    public string ScopeName { get; }

    // Increases with every result the highlighter publishes.
    public long Version { get; }

    public ReadOnlyMemory<HighlightToken> GetTokens(int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(line, LineCount);
        return _lines[line].Tokens;
    }

    // Whether the line starts with a rule that an earlier line opened: a block comment, a class body, an unterminated
    // string. A line that reopens the same scope on its own, as two adjacent line comments do, is not a continuation.
    public bool IsContinuation(int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(line, LineCount);
        return _lines[line].Continued;
    }

    public TokenCategory GetCategory(int scopeId) => (uint)scopeId < (uint)_categories.Length ? _categories[scopeId] : TokenCategory.None;

    public int GetForeground(int scopeId) => (uint)scopeId < (uint)_foregrounds.Length ? _foregrounds[scopeId] : 0;

    // The line's tokens with categories resolved, semantic tokens of a language server overlaid when given.
    public StyledToken[] RenderLine(int line, ReadOnlySpan<SemanticToken> semantic = default)
    {
        var tokens = GetTokens(line).Span;
        var categories = new TokenCategory[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            categories[i] = GetCategory(tokens[i].ScopeId);
        }

        return HighlightMerger.Overlay(tokens, categories, semantic);
    }

    internal HighlightLineData[] Lines => _lines;

    internal HighlightResult WithTheme(TokenCategory[] categories, int[] foregrounds)
        => new(Scopes, _lines, LineCount, categories, foregrounds, Version + 1, ScopeName);
}
