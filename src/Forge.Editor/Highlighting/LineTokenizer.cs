// Governed by THIRD_PARTY_NOTICES.md.

using Forge.Editor.TextMate;
using Forge.Editor.TextMate.Internal;

namespace Forge.Editor.Highlighting;

// One grammar's tokenizer plus the interning of its scope stacks. Owned by a Tokenizer, so not shared across documents.
internal sealed class LineTokenizer
{
    private readonly TextMate.Tokenizer _tokenizer;
    private readonly ScopeTable _scopes;
    private readonly Dictionary<AttributedScopeStack, int> _ids = new(ScopeComparer.Instance);

    public LineTokenizer(TextMate.Tokenizer tokenizer, ScopeTable scopes)
    {
        _tokenizer = tokenizer;
        _scopes = scopes;
        Initial = StateStack.Initial;
    }

    public StateStack Initial { get; }

    public string ScopeName => _tokenizer.ScopeName;

    public IReadOnlyList<string> MissingGrammars => _tokenizer.MissingGrammars;

    public IReadOnlyCollection<string> Errors => _tokenizer.Errors;

    // Tokenizes one line from the state the previous line ended in.
    public HighlightLineData TokenizeLine(ReadOnlySpan<char> line, StateStack state)
    {
        var tokens = _tokenizer.TokenizeLine(line, state, out var next);
        var result = new HighlightToken[tokens.Length];
        var count = 0;
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var length = Math.Min(token.Length, line.Length - token.Start);
            if (length <= 0)
            {
                continue;
            }

            result[count++] = new HighlightToken(token.Start, length, Intern(token.Scopes));
        }

        if (count != result.Length)
        {
            Array.Resize(ref result, count);
        }

        return new HighlightLineData(result, next, _tokenizer.ContinuesEarlierRule);
    }

    private int Intern(AttributedScopeStack scopes)
    {
        if (_ids.TryGetValue(scopes, out var id))
        {
            return id;
        }

        id = _scopes.Intern(scopes);
        _ids[scopes] = id;
        return id;
    }

    private sealed class ScopeComparer : IEqualityComparer<AttributedScopeStack>
    {
        public static readonly ScopeComparer Instance = new();

        public bool Equals(AttributedScopeStack? x, AttributedScopeStack? y) => AttributedScopeStack.Equals(x, y);

        public int GetHashCode(AttributedScopeStack obj) => obj.GetHashCode();
    }
}
