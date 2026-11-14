using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Forge.Editor.Highlighting;

namespace Forge.Editor.Tests.Highlighting;

public class SemanticLineIndexTests
{
    [Fact]
    public void Tokens_are_bucketed_onto_their_own_lines()
    {
        var index = SemanticLineIndex.Build(
            [0, 1, 1, 2],
            [
                Token(0, 3, TokenCategory.Keyword),
                Token(2, 4, TokenCategory.String),
                Token(7, 1, TokenCategory.Number),
                Token(1, 2, TokenCategory.Comment),
            ],
            lineCount: 3,
            version: 9);

        Assert.Equal(9, index.Version);
        Assert.Equal(3, index.LineCount);
        Assert.Equal(4, index.TokenCount);
        Assert.False(index.IsEmpty);
        Assert.Equal(3, index.BuiltLines);
        Assert.Equal(0, index.ReusedLines);
        Assert.Equal(0, index.DroppedTokens);
        Assert.Equal(new[] { Token(0, 3, TokenCategory.Keyword) }, Lines(index, 0));
        Assert.Equal(new[] { Token(2, 4, TokenCategory.String), Token(7, 1, TokenCategory.Number) }, Lines(index, 1));
        Assert.Equal(new[] { Token(1, 2, TokenCategory.Comment) }, Lines(index, 2));
    }

    [Fact]
    public void Tokens_of_one_line_are_sorted_by_start_even_when_they_arrive_unordered()
    {
        var index = SemanticLineIndex.Build(
            [0, 0, 0],
            [
                Token(6, 1, TokenCategory.String),
                Token(0, 2, TokenCategory.Keyword),
                Token(3, 2, TokenCategory.Number),
            ],
            lineCount: 1,
            version: 1);

        Assert.Equal(3, index.TokenCount);
        Assert.Equal(1, index.BuiltLines);
        Assert.Equal(
            [Token(0, 2, TokenCategory.Keyword), Token(3, 2, TokenCategory.Number), Token(6, 1, TokenCategory.String)],
            Lines(index, 0));
    }

    [Fact]
    public void Tokens_outside_the_document_or_without_a_length_are_dropped()
    {
        var index = SemanticLineIndex.Build(
            [0, 0, 1, 2, 5],
            [
                Token(0, 3, TokenCategory.Keyword),
                Token(4, 0, TokenCategory.String), // zero length
                Token(0, -2, TokenCategory.Number), // negative length
                Token(0, 1, TokenCategory.Comment), // line 2 is one past the last line
                Token(0, 1, TokenCategory.String), // line 5 is far outside the document
            ],
            lineCount: 2,
            version: 1);

        Assert.Equal(4, index.DroppedTokens);
        Assert.Equal(1, index.TokenCount);
        Assert.Equal(1, index.BuiltLines);
        Assert.Equal(2, index.LineCount);
        Assert.Equal(new[] { Token(0, 3, TokenCategory.Keyword) }, Lines(index, 0));
        Assert.Empty(Lines(index, 1));
    }

    [Fact]
    public void An_index_whose_tokens_were_all_dropped_is_empty()
    {
        var index = SemanticLineIndex.Build(
            [2, -1],
            [Token(0, 1, TokenCategory.Keyword), Token(0, 2, TokenCategory.String)],
            lineCount: 2,
            version: 3);

        Assert.Equal(3, index.Version);
        Assert.Equal(2, index.DroppedTokens);
        Assert.Equal(0, index.TokenCount);
        Assert.True(index.IsEmpty);
        Assert.Empty(Lines(index, 0));
        Assert.Empty(Lines(index, 1));
    }

    [Fact]
    public void Empty_input_keeps_the_version_and_the_line_count_without_tokens()
    {
        var index = SemanticLineIndex.Build([], [], lineCount: 4, version: 42);

        Assert.Equal(42, index.Version);
        Assert.Equal(4, index.LineCount);
        Assert.Equal(0, index.TokenCount);
        Assert.Equal(0, index.BuiltLines);
        Assert.Equal(0, index.ReusedLines);
        Assert.Equal(0, index.DroppedTokens);
        Assert.True(index.IsEmpty);
        Assert.Empty(Lines(index, 0));
        Assert.Empty(Lines(index, 3));
        Assert.Empty(Lines(index, 4));
    }

    [Fact]
    public void The_empty_index_has_no_tokens_and_no_document_version()
    {
        Assert.Equal(-1, SemanticLineIndex.Empty.Version);
        Assert.Equal(0, SemanticLineIndex.Empty.LineCount);
        Assert.Equal(0, SemanticLineIndex.Empty.TokenCount);
        Assert.True(SemanticLineIndex.Empty.IsEmpty);
        Assert.Empty(Lines(SemanticLineIndex.Empty, 0));
    }

    [Fact]
    public void An_unchanged_line_keeps_the_previous_array_and_a_changed_line_does_not()
    {
        var previous = SemanticLineIndex.Build(
            [0, 0, 1],
            [
                Token(0, 3, TokenCategory.Keyword),
                Token(4, 2, TokenCategory.String),
                Token(0, 5, TokenCategory.Comment),
            ],
            lineCount: 2,
            version: 1);

        var next = SemanticLineIndex.Build(
            [0, 0, 1],
            [
                Token(0, 3, TokenCategory.Keyword),
                Token(4, 2, TokenCategory.String),
                Token(0, 7, TokenCategory.Comment), // only this line changed
            ],
            lineCount: 2,
            version: 2,
            previous);

        Assert.Equal(2, next.Version);
        Assert.Equal(3, next.TokenCount);
        Assert.Equal(1, next.BuiltLines);
        Assert.Equal(1, next.ReusedLines);
        Assert.Equal(0, next.DroppedTokens);
        Assert.Equal(new[] { Token(0, 3, TokenCategory.Keyword), Token(4, 2, TokenCategory.String) }, Lines(next, 0));
        Assert.True(SharesStorage(previous.GetLine(0), next.GetLine(0)), "line 0 did not change, so it must keep the previous array");
        Assert.Equal(new[] { Token(0, 7, TokenCategory.Comment) }, Lines(next, 1));
        Assert.False(SharesStorage(previous.GetLine(1), next.GetLine(1)), "line 1 changed, so it must be a new array");
    }

    [Fact]
    public void A_second_build_of_the_same_tokens_reuses_every_line_that_has_tokens()
    {
        SemanticToken[] tokens = [Token(0, 3, TokenCategory.Keyword), Token(0, 5, TokenCategory.Comment)];
        var previous = SemanticLineIndex.Build([0, 2], tokens, lineCount: 3, version: 1);

        var next = SemanticLineIndex.Build([0, 2], tokens, lineCount: 3, version: 2, previous);

        Assert.Equal(3, next.LineCount);
        Assert.Equal(2, next.TokenCount);
        Assert.Equal(0, next.BuiltLines);
        Assert.Equal(2, next.ReusedLines);
        Assert.True(SharesStorage(previous.GetLine(0), next.GetLine(0)));
        Assert.True(SharesStorage(previous.GetLine(2), next.GetLine(2)));
        Assert.Empty(Lines(next, 1));
    }

    [Fact]
    public void A_line_that_lost_all_of_its_tokens_reads_as_empty_and_reuses_nothing()
    {
        var previous = SemanticLineIndex.Build(
            [0, 1],
            [Token(0, 3, TokenCategory.Keyword), Token(2, 4, TokenCategory.String)],
            lineCount: 3,
            version: 1);

        var next = SemanticLineIndex.Build(
            [0, 2],
            [Token(0, 5, TokenCategory.Keyword), Token(4, 1, TokenCategory.Number)],
            lineCount: 3,
            version: 2,
            previous);

        Assert.Equal(new[] { Token(2, 4, TokenCategory.String) }, Lines(previous, 1)); // line 1 had tokens in the previous build
        Assert.Empty(Lines(next, 1));
        Assert.False(
            SharesStorage(previous.GetLine(1), next.GetLine(1)),
            "line 1 lost every token, so it must not keep the previous array");
        Assert.Equal(0, next.ReusedLines); // line 0 changed and line 2 is new, so nothing was carried over
        Assert.Equal(2, next.BuiltLines);
        Assert.Equal(2, next.TokenCount);
        Assert.Equal(new[] { Token(0, 5, TokenCategory.Keyword) }, Lines(next, 0));
        Assert.Equal(new[] { Token(4, 1, TokenCategory.Number) }, Lines(next, 2));
    }

    [Fact]
    public void A_line_that_lost_all_of_its_tokens_is_not_counted_as_reused_while_an_unchanged_line_is()
    {
        var previous = SemanticLineIndex.Build(
            [0, 1, 2],
            [Token(0, 3, TokenCategory.Keyword), Token(2, 4, TokenCategory.String), Token(1, 2, TokenCategory.Number)],
            lineCount: 3,
            version: 1);

        var next = SemanticLineIndex.Build(
            [0, 2],
            [Token(0, 3, TokenCategory.Keyword), Token(1, 2, TokenCategory.Number)],
            lineCount: 3,
            version: 2,
            previous);

        Assert.Equal(2, next.ReusedLines); // lines 0 and 2 only: the emptied line 1 is not a reuse
        Assert.Equal(0, next.BuiltLines);
        Assert.Empty(Lines(next, 1));
        Assert.False(
            SharesStorage(previous.GetLine(1), next.GetLine(1)),
            "line 1 lost every token, so it must not keep the previous array");
        Assert.True(SharesStorage(previous.GetLine(0), next.GetLine(0)));
        Assert.True(SharesStorage(previous.GetLine(2), next.GetLine(2)));
    }

    [Fact]
    public void A_fully_unordered_line_number_array_still_buckets_correctly()
    {
        var index = SemanticLineIndex.Build(
            [2, 0, 1, 0],
            [
                Token(0, 1, TokenCategory.Number), // line 2
                Token(0, 2, TokenCategory.Keyword), // line 0, second
                Token(5, 1, TokenCategory.String), // line 1
                Token(7, 2, TokenCategory.Comment), // line 0, first
            ],
            lineCount: 3,
            version: 3);

        Assert.Equal(3, index.LineCount);
        Assert.Equal(4, index.TokenCount);
        Assert.Equal(3, index.BuiltLines);
        Assert.Equal(0, index.DroppedTokens);
        Assert.Equal(new[] { Token(0, 2, TokenCategory.Keyword), Token(7, 2, TokenCategory.Comment) }, Lines(index, 0));
        Assert.Equal(new[] { Token(5, 1, TokenCategory.String) }, Lines(index, 1));
        Assert.Equal(new[] { Token(0, 1, TokenCategory.Number) }, Lines(index, 2));
    }

    [Fact]
    public void An_unordered_line_number_array_is_sorted_and_still_reuses_unchanged_lines()
    {
        var previous = SemanticLineIndex.Build(
            [0, 0, 1],
            [Token(0, 3, TokenCategory.Keyword), Token(4, 2, TokenCategory.String), Token(0, 5, TokenCategory.Comment)],
            lineCount: 2,
            version: 1);

        var next = SemanticLineIndex.Build(
            [1, 0, 0],
            [Token(0, 5, TokenCategory.Comment), Token(4, 2, TokenCategory.String), Token(0, 3, TokenCategory.Keyword)],
            lineCount: 2,
            version: 2,
            previous);

        Assert.Equal(0, next.BuiltLines);
        Assert.Equal(2, next.ReusedLines);
        Assert.Equal(new[] { Token(0, 3, TokenCategory.Keyword), Token(4, 2, TokenCategory.String) }, Lines(next, 0));
        Assert.Equal(new[] { Token(0, 5, TokenCategory.Comment) }, Lines(next, 1));
        Assert.True(SharesStorage(previous.GetLine(0), next.GetLine(0)));
        Assert.True(SharesStorage(previous.GetLine(1), next.GetLine(1)));
    }

    [Fact]
    public void A_line_outside_the_document_reads_as_empty()
    {
        var index = SemanticLineIndex.Build([1], [Token(0, 2, TokenCategory.Keyword)], lineCount: 2, version: 1);

        Assert.Equal(new[] { Token(0, 2, TokenCategory.Keyword) }, Lines(index, 1));
        Assert.Empty(Lines(index, -1));
        Assert.Empty(Lines(index, 2));
        Assert.Empty(Lines(index, int.MaxValue));
        Assert.Empty(Lines(index, int.MinValue));
    }

    [Fact]
    public void A_token_without_a_line_number_is_rejected()
    {
        var error = Assert.Throws<ArgumentException>(
            () => _ = SemanticLineIndex.Build([0, 1], [Token(0, 2, TokenCategory.Keyword)], lineCount: 2, version: 1));

        Assert.Equal("tokens", error.ParamName);
    }

    private static SemanticToken Token(int start, int length, TokenCategory category) => new(start, length, category);

    private static SemanticToken[] Lines(SemanticLineIndex index, int line) => index.GetLine(line).ToArray();

    /// <summary>
    /// True when both spans read the same array: a reused line must be the previous build's array itself, not an equal copy.
    /// </summary>
    private static bool SharesStorage(ReadOnlySpan<SemanticToken> left, ReadOnlySpan<SemanticToken> right)
    {
        ref var leftReference = ref MemoryMarshal.GetReference(left);
        ref var rightReference = ref MemoryMarshal.GetReference(right);
        return Unsafe.AreSame(ref leftReference, ref rightReference);
    }
}
