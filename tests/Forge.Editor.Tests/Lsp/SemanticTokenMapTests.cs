using Forge.Editor.Highlighting;
using Forge.Editor.Lsp;
using Forge.Editor.Text;
using LspToken = Forge.Editor.Lsp.SemanticToken;
using StyledToken = Forge.Editor.Highlighting.SemanticToken;

namespace Forge.Editor.Tests.Lsp;

public class SemanticTokenMapTests
{
    // "\u00E9 foo": the accented letter is one UTF-16 unit but two UTF-8 bytes, so a byte column is not a character column.
    private const string AccentedLine = "\u00E9 foo";

    // Two ideographs followed by " x": three UTF-8 bytes each, one UTF-16 unit each.
    private const string WideLine = "\u4E2D\u6587 x";

    [Fact]
    public void Token_types_are_mapped_through_the_legend_positionally()
    {
        var snapshot = TextSnapshot.FromText("kw str typ unk x", version: 3);

        var index = SemanticTokenMap.ToLineIndex(
            Result(
                ["keyword", "string", "type", "method"],
                new LspToken(0, 0, 2, 0),
                new LspToken(0, 3, 3, 1),
                new LspToken(0, 7, 3, 2),
                new LspToken(0, 11, 1, 3)),
            snapshot,
            PositionEncoding.Utf16);

        Assert.Equal(3, index.Version);
        Assert.Equal(4, index.TokenCount);
        Assert.Equal(
            new[]
            {
                new StyledToken(0, 2, TokenCategory.Keyword),
                new StyledToken(3, 3, TokenCategory.String),
                new StyledToken(7, 3, TokenCategory.Type),

                // A legend index maps to the category the type name stands for, not to the name itself.
                new StyledToken(11, 1, TokenCategory.Function),
            },
            Lines(index, 0));
    }

    [Fact]
    public void A_token_whose_type_has_no_category_is_dropped()
    {
        var snapshot = TextSnapshot.FromText("aa bb cc dd");

        var index = SemanticTokenMap.ToLineIndex(
            Result(
                ["keyword", "unknownType"],
                new LspToken(0, 0, 2, 0), // keyword
                new LspToken(0, 3, 2, 1), // a legend type the mapper does not know
                new LspToken(0, 6, 2, 7)), // a type index past the end of the legend
            snapshot,
            PositionEncoding.Utf16);

        Assert.Equal(1, index.TokenCount);
        Assert.Equal(new[] { new StyledToken(0, 2, TokenCategory.Keyword) }, Lines(index, 0));
    }

    [Fact]
    public void A_token_running_past_its_line_is_clipped_to_the_line()
    {
        var snapshot = TextSnapshot.FromText("abc\ndefgh");

        var index = SemanticTokenMap.ToLineIndex(
            Result(
                ["keyword"],
                new LspToken(0, 1, 100, 0),
                new LspToken(0, 7, 2, 0), // starts past the end of line 0, so nothing of it is left to paint
                new LspToken(1, 3, 100, 0)),
            snapshot,
            PositionEncoding.Utf16);

        Assert.Equal(2, index.TokenCount);
        var firstLine = Assert.Single(Lines(index, 0));
        Assert.Equal(new StyledToken(1, 2, TokenCategory.Keyword), firstLine);
        Assert.Equal(3, firstLine.Start + firstLine.Length); // stops at the line end, never past it
        Assert.Equal(new[] { new StyledToken(3, 2, TokenCategory.Keyword) }, Lines(index, 1));
    }

    [Fact]
    public void A_token_on_a_line_the_snapshot_does_not_have_is_dropped()
    {
        var snapshot = TextSnapshot.FromText("abc");

        var index = SemanticTokenMap.ToLineIndex(
            Result(["keyword"], new LspToken(3, 0, 1, 0), new LspToken(0, 0, 2, 0)),
            snapshot,
            PositionEncoding.Utf16);

        Assert.Equal(1, index.LineCount);
        Assert.Equal(1, index.TokenCount);
        Assert.Equal(new[] { new StyledToken(0, 2, TokenCategory.Keyword) }, Lines(index, 0));
    }

    [Fact]
    public void A_token_without_a_length_is_dropped()
    {
        var snapshot = TextSnapshot.FromText("abc");

        var index = SemanticTokenMap.ToLineIndex(
            Result(["keyword"], new LspToken(0, 1, 0, 0)),
            snapshot,
            PositionEncoding.Utf16);

        Assert.True(index.IsEmpty);
        Assert.Equal(0, index.TokenCount);
        Assert.Empty(Lines(index, 0));
    }

    [Fact]
    public void Utf16_columns_are_taken_as_character_offsets()
    {
        var snapshot = TextSnapshot.FromText(AccentedLine);

        var index = SemanticTokenMap.ToLineIndex(
            Result(["keyword"], new LspToken(0, 2, 3, 0)),
            snapshot,
            PositionEncoding.Utf16);

        // Character 2 is the first "f": the accent counts as one column, as it does in .NET strings.
        Assert.Equal(new[] { new StyledToken(2, 3, TokenCategory.Keyword) }, Lines(index, 0));
    }

    [Fact]
    public void Utf8_byte_columns_are_decoded_to_character_offsets()
    {
        var snapshot = TextSnapshot.FromText(AccentedLine);

        var index = SemanticTokenMap.ToLineIndex(
            Result(["keyword"], new LspToken(0, 0, 2, 0), new LspToken(0, 3, 3, 0)),
            snapshot,
            PositionEncoding.Utf8);

        // Bytes 0..2 are the accent (one character), bytes 3..6 are "foo" (characters 2..5).
        Assert.Equal(
            new[] { new StyledToken(0, 1, TokenCategory.Keyword), new StyledToken(2, 3, TokenCategory.Keyword) },
            Lines(index, 0));
    }

    [Fact]
    public void Utf8_byte_columns_after_a_wide_character_decode_to_the_right_range()
    {
        var snapshot = TextSnapshot.FromText(WideLine);

        var index = SemanticTokenMap.ToLineIndex(
            Result(["keyword"], new LspToken(0, 0, 3, 0), new LspToken(0, 6, 2, 0)),
            snapshot,
            PositionEncoding.Utf8);

        // Bytes 0..3 are the first ideograph, bytes 6..8 are the space and the "x".
        Assert.Equal(
            new[] { new StyledToken(0, 1, TokenCategory.Keyword), new StyledToken(2, 2, TokenCategory.Keyword) },
            Lines(index, 0));
    }

    [Fact]
    public void The_index_carries_the_snapshot_version_and_reuses_the_previous_one()
    {
        var snapshot = TextSnapshot.FromText("abc\nabc", version: 5);
        var result = Result(["keyword"], new LspToken(0, 0, 3, 0), new LspToken(1, 0, 3, 0));

        var first = SemanticTokenMap.ToLineIndex(result, snapshot, PositionEncoding.Utf16);
        var second = SemanticTokenMap.ToLineIndex(result, snapshot, PositionEncoding.Utf16, first);

        Assert.Equal(5, first.Version);
        Assert.Equal(2, first.LineCount);
        Assert.Equal(2, first.TokenCount);
        Assert.Equal(2, first.BuiltLines);
        Assert.Equal(0, first.ReusedLines);
        Assert.Equal(5, second.Version);
        Assert.Equal(0, second.BuiltLines);
        Assert.Equal(2, second.ReusedLines);
    }

    [Fact]
    public void An_empty_result_gives_an_empty_index_of_the_snapshot_version()
    {
        var snapshot = TextSnapshot.FromText("abc", version: 11);

        var index = SemanticTokenMap.ToLineIndex(
            SemanticTokensResult.Empty(new SemanticTokensLegend(["keyword"], [])),
            snapshot,
            PositionEncoding.Utf16);

        Assert.Equal(11, index.Version);
        Assert.True(index.IsEmpty);
        Assert.Equal(0, index.TokenCount);
        Assert.Empty(Lines(index, 0));
    }

    [Fact]
    public void A_missing_result_or_snapshot_is_rejected()
    {
        var snapshot = TextSnapshot.FromText("abc");

        Assert.Throws<ArgumentNullException>(() => SemanticTokenMap.ToLineIndex(null!, snapshot, PositionEncoding.Utf16));
        Assert.Throws<ArgumentNullException>(() => SemanticTokenMap.ToLineIndex(SemanticTokensResult.Empty(), null!, PositionEncoding.Utf16));
    }

    private static SemanticTokensResult Result(IReadOnlyList<string> tokenTypes, params LspToken[] tokens) =>
        new(null, tokens, new SemanticTokensLegend(tokenTypes, []));

    private static StyledToken[] Lines(SemanticLineIndex index, int line) => index.GetLine(line).ToArray();
}
