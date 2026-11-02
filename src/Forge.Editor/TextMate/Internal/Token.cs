// Governed by THIRD_PARTY_NOTICES.md: adapted from Tint (MIT, Sebastien Ros).

namespace Forge.Editor.TextMate;

// A token of a line: its range and the scopes that apply to it.
internal readonly record struct Token(int Start, int Length, AttributedScopeStack Scopes)
{
    public int End => Start + Length;
}
