// Governed by THIRD_PARTY_NOTICES.md.

namespace Forge.Editor.Highlighting;

// A token of one line: the range and the id of its scope stack in the result's ScopeTable. Categories are resolved per
// scope id, so retheming does not re-tokenize.
public readonly record struct HighlightToken(int Start, int Length, int ScopeId)
{
    public int End => Start + Length;
}

// A token a language server classified, in line coordinates.
public readonly record struct SemanticToken(int Start, int Length, TokenCategory Category)
{
    public int End => Start + Length;
}

// A token ready to render: the category resolved from either layer, and the scope id when it came from the lexical one
// (negative for a semantic one).
public readonly record struct StyledToken(int Start, int Length, TokenCategory Category, int ScopeId)
{
    public int End => Start + Length;
}
