// Governed by THIRD_PARTY_NOTICES.md.

namespace Forge.Editor.Highlighting;

// Places the semantic tokens a language server returns over the lexical ones: semantic wins on its range, lexical keeps
// the rest, and both are split at each other's boundaries. Both inputs are sorted by start, as LSP deltas decode into.
public static class HighlightMerger
{
    public static StyledToken[] Overlay(ReadOnlySpan<HighlightToken> lexical, ReadOnlySpan<TokenCategory> lexicalCategories, ReadOnlySpan<SemanticToken> semantic)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(lexical.Length, lexicalCategories.Length);
        var result = new List<StyledToken>(lexical.Length + semantic.Length);
        if (lexical.IsEmpty && semantic.IsEmpty)
        {
            return [];
        }

        var end = lexical.IsEmpty ? semantic[^1].End : semantic.IsEmpty ? lexical[^1].End : Math.Max(lexical[^1].End, semantic[^1].End);
        var lexicalIndex = 0;
        var semanticIndex = 0;
        var pos = 0;
        while (pos < end)
        {
            while (lexicalIndex < lexical.Length && lexical[lexicalIndex].End <= pos)
            {
                lexicalIndex++;
            }

            while (semanticIndex < semantic.Length && semantic[semanticIndex].End <= pos)
            {
                semanticIndex++;
            }

            var inSemantic = semanticIndex < semantic.Length && semantic[semanticIndex].Start <= pos;
            var inLexical = lexicalIndex < lexical.Length && lexical[lexicalIndex].Start <= pos;
            if (!inSemantic && !inLexical)
            {
                var nextStart = int.MaxValue;
                if (lexicalIndex < lexical.Length)
                {
                    nextStart = lexical[lexicalIndex].Start;
                }

                if (semanticIndex < semantic.Length)
                {
                    nextStart = Math.Min(nextStart, semantic[semanticIndex].Start);
                }

                pos = nextStart >= end ? end : nextStart;
                continue;
            }

            var segmentEnd = inSemantic ? semantic[semanticIndex].End : lexical[lexicalIndex].End;
            if (semanticIndex < semantic.Length && semantic[semanticIndex].Start > pos)
            {
                segmentEnd = Math.Min(segmentEnd, semantic[semanticIndex].Start);
            }

            if (lexicalIndex < lexical.Length && lexical[lexicalIndex].Start > pos)
            {
                segmentEnd = Math.Min(segmentEnd, lexical[lexicalIndex].Start);
            }

            if (segmentEnd <= pos)
            {
                break;
            }

            result.Add(inSemantic
                ? new StyledToken(pos, segmentEnd - pos, semantic[semanticIndex].Category, -1)
                : new StyledToken(pos, segmentEnd - pos, lexicalCategories[lexicalIndex], lexical[lexicalIndex].ScopeId));
            pos = segmentEnd;
        }

        return [.. result];
    }
}
