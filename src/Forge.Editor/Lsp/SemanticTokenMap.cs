// Governed by THIRD_PARTY_NOTICES.md.

using Forge.Editor.Highlighting;
using Forge.Editor.Text;
using SemanticToken = Forge.Editor.Lsp.SemanticToken;
using StyledSemanticToken = Forge.Editor.Highlighting.SemanticToken;

namespace Forge.Editor.Lsp;

/// <summary>
/// Maps a server's <c>semanticTokens</c> onto the line-local categories the highlight layer merges with: the server speaks
/// (line, character, length) in the session's position encoding, the highlighter speaks (start, length) within one line.
/// <para>This runs off the UI thread against one immutable snapshot, so the result carries the snapshot's version and the
/// view can drop it as soon as the document moved on.</para>
/// </summary>
public static class SemanticTokenMap
{
    public static SemanticLineIndex ToLineIndex(
        SemanticTokensResult result,
        TextSnapshot snapshot,
        PositionEncoding encoding,
        SemanticLineIndex? previous = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(snapshot);

        var tokens = result.Tokens;
        var version = snapshot.Version;
        if (tokens.Count == 0 || snapshot.LineCount == 0)
        {
            return SemanticLineIndex.Build([], [], snapshot.LineCount, version, previous);
        }

        // The legend is mapped positionally once, so a token costs an array index instead of a name lookup.
        var categories = SemanticTokenMapper.MapLegend(result.Legend.TokenTypes);
        var lineNumbers = new int[tokens.Count];
        var mapped = new StyledSemanticToken[tokens.Count];
        var count = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if ((uint)token.Type >= (uint)categories.Length || categories[token.Type] == TokenCategory.None ||
                (uint)token.Line >= (uint)snapshot.LineCount || token.Length <= 0)
            {
                continue;
            }

            var (start, length) = MapRange(snapshot, token, encoding);
            if (length <= 0)
            {
                continue;
            }

            lineNumbers[count] = token.Line;
            mapped[count] = new StyledSemanticToken(start, length, categories[token.Type]);
            count++;
        }

        return SemanticLineIndex.Build(lineNumbers.AsSpan(0, count), mapped.AsSpan(0, count), snapshot.LineCount, version, previous);
    }

    /// <summary>
    /// One token's range in line-local UTF-16 offsets. A token that runs past its line's text is clipped to the line: the
    /// merger paints one line at a time and a range crossing the terminator would paint past everything else on that line.
    /// </summary>
    private static (int Start, int Length) MapRange(TextSnapshot snapshot, SemanticToken token, PositionEncoding encoding)
    {
        var lineStart = snapshot.GetLineStart(token.Line);
        var lineLength = snapshot.GetLineEnd(token.Line) - lineStart;
        if (encoding == PositionEncoding.Utf16)
        {
            var start = Math.Clamp(token.StartChar, 0, lineLength);
            var end = Math.Clamp(token.StartChar + token.Length, start, lineLength);
            return (start, end - start);
        }

        // A UTF-8 session counts bytes, so both ends have to be decoded back to offsets: a multi-byte character inside the
        // range shifts the length as well as the start.
        var from = snapshot.OffsetOfCharacter(token.Line, token.StartChar, encoding) - lineStart;
        var to = snapshot.OffsetOfCharacter(token.Line, token.StartChar + token.Length, encoding) - lineStart;
        var utf8Start = Math.Clamp(from, 0, lineLength);
        var utf8End = Math.Clamp(to, utf8Start, lineLength);
        return (utf8Start, utf8End - utf8Start);
    }
}
