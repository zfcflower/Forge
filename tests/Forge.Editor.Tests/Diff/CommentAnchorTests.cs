using Forge.Editor.Diff;
using Forge.Editor.Text;

namespace Forge.Editor.Tests.Diff;

public class CommentAnchorTests
{
    private static TextSnapshot Lines(params string[] lines) => TextSnapshot.FromText(string.Join("\n", lines));

    private static readonly string[] Base = ["alpha one", "beta two", "gamma three", "delta four", "epsilon five", "zeta six"];

    [Fact]
    public void Create_records_the_block_hashes_preview_and_kind()
    {
        var snapshot = Lines(Base);

        var anchor = CommentAnchor.Create("src/a.cs", snapshot, 1, 3);

        Assert.Equal("src/a.cs", anchor.FilePath);
        Assert.Equal((1, 3), anchor.Range);
        Assert.Equal(3, anchor.LineCount);
        Assert.Equal(CommentAnchorKind.Block, anchor.Kind);
        Assert.Equal(3, anchor.LineHashes!.Count);
        Assert.Equal("beta two\ngamma three\ndelta four", anchor.Preview);
        Assert.Matches("^[0-9a-f]{16}$", anchor.ContentHash);
        Assert.True(anchor.Matches(snapshot));
    }

    [Fact]
    public void Single_line_and_explicit_line_anchors_are_line_kind()
    {
        var snapshot = Lines(Base);

        Assert.Equal(CommentAnchorKind.Line, CommentAnchor.Create("f", snapshot, 2, 2).Kind);
        Assert.Equal(CommentAnchorKind.Line, CommentAnchor.Create("f", snapshot, 1, 3, CommentAnchorKind.Line).Kind);
    }

    [Fact]
    public void Reversed_and_out_of_range_requests_are_normalised_into_the_document()
    {
        var snapshot = Lines(Base);

        var reversed = CommentAnchor.Create("f", snapshot, 3, 1);
        var beyond = CommentAnchor.Create("f", snapshot, 4, 99);

        Assert.Equal((1, 3), reversed.Range);
        Assert.Equal((4, 5), beyond.Range);
    }

    [Fact]
    public void Preview_is_cut_to_the_requested_length()
    {
        var anchor = CommentAnchor.Create("f", Lines(Base), 0, 5, previewChars: 12);

        Assert.Equal(12, anchor.Preview!.Length);
        Assert.Equal("alpha one\nbe", anchor.Preview);
    }

    [Fact]
    public void Matches_turns_false_when_the_block_changes_or_the_lines_disappear()
    {
        var anchor = CommentAnchor.Create("f", Lines(Base), 1, 3);
        var edited = Base.ToArray();
        edited[2] = "gamma THREE";

        Assert.False(anchor.Matches(Lines(edited)));
        Assert.False(anchor.Matches(Lines("only", "two")));
        Assert.True(anchor.Matches(Lines(Base)));
        Assert.False(new CommentAnchor("f", 10, 12, anchor.ContentHash).Matches(Lines(Base)));
        Assert.False(new CommentAnchor("f", 0, 0, "not-hex").Matches(Lines(Base)));
    }

    [Fact]
    public void Exact_reanchor_keeps_the_position_and_refreshes_line_hashes()
    {
        var snapshot = Lines(Base);
        var anchor = CommentAnchor.Create("f", snapshot, 1, 3) with { LineHashes = null };

        var result = anchor.Reanchor(snapshot);

        Assert.Equal(ReanchorStatus.Exact, result.Status);
        Assert.Equal(1.0, result.Similarity);
        Assert.True(result.Found);
        Assert.Equal((1, 3), (result.StartLine, result.EndLine));
        Assert.NotNull(result.Anchor.LineHashes);
        Assert.Equal(3, result.Anchor.LineHashes.Count);
    }

    [Fact]
    public void Moved_block_is_found_at_its_new_lines()
    {
        var original = Lines(Base);
        var anchor = CommentAnchor.Create("f", original, 1, 3);
        var moved = Lines(["inserted A", "inserted B", "inserted C", .. Base]);

        var result = anchor.Reanchor(moved);

        Assert.Equal(ReanchorStatus.Moved, result.Status);
        Assert.Equal((4, 6), (result.StartLine, result.EndLine));
        Assert.True(result.Found);
        Assert.True(result.Anchor.Matches(moved));
        Assert.StartsWith("beta two", result.Anchor.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void Closest_identical_block_wins_when_several_copies_exist()
    {
        var anchor = CommentAnchor.Create("f", Lines(Base), 1, 3);
        var duplicated = Lines(["x", "y", "z", "beta two", "gamma three", "delta four", "q", "beta two", "gamma three", "delta four"]);

        var result = anchor.Reanchor(duplicated);

        Assert.Equal(ReanchorStatus.Moved, result.Status);
        Assert.Equal(3, result.StartLine);
    }

    [Fact]
    public void Edited_line_inside_the_block_is_a_fuzzy_match_and_the_anchor_adopts_the_new_text()
    {
        var anchor = CommentAnchor.Create("f", Lines(Base), 1, 4);
        var edited = Base.ToArray();
        edited[2] = "gamma THREE changed";

        var result = anchor.Reanchor(Lines(edited));

        Assert.Equal(ReanchorStatus.Fuzzy, result.Status);
        Assert.True(result.Similarity >= 0.6 && result.Similarity < 1.0, result.Similarity.ToString());
        Assert.Equal((1, 4), (result.StartLine, result.EndLine));
        Assert.True(result.Anchor.Matches(Lines(edited)));
        Assert.True(result.Found);
    }

    [Fact]
    public void Raising_the_fuzzy_threshold_turns_the_same_edit_into_a_loss()
    {
        var anchor = CommentAnchor.Create("f", Lines(Base), 1, 4);
        var edited = Base.ToArray();
        edited[2] = "gamma THREE changed";

        var result = anchor.Reanchor(Lines(edited), new ReanchorOptions { FuzzyThreshold = 0.99 });

        Assert.Equal(ReanchorStatus.Lost, result.Status);
        Assert.False(result.Found);
    }

    [Fact]
    public void Unrelated_text_is_lost_and_the_original_lines_are_clamped_into_the_document()
    {
        var anchor = CommentAnchor.Create("f", Lines(Base), 4, 5);
        var unrelated = Lines("q1", "q2", "q3");

        var result = anchor.Reanchor(unrelated);

        Assert.Equal(ReanchorStatus.Lost, result.Status);
        Assert.False(result.Found);
        Assert.InRange(result.StartLine, 0, 2);
        Assert.InRange(result.EndLine, result.StartLine, 2);
    }

    [Fact]
    public void Max_distance_excludes_far_copies_of_the_block()
    {
        var anchor = CommentAnchor.Create("f", Lines(Base), 1, 3);
        var far = Lines([.. Enumerable.Range(0, 10).Select(i => "filler " + i), .. Base]);

        var limited = anchor.Reanchor(far, new ReanchorOptions { MaxDistance = 2 });
        var unlimited = anchor.Reanchor(far);

        Assert.NotEqual(ReanchorStatus.Moved, limited.Status);
        Assert.Equal(ReanchorStatus.Moved, unlimited.Status);
        Assert.Equal(11, unlimited.StartLine);
    }

    [Fact]
    public void Reanchoring_an_empty_document_does_not_throw()
    {
        var anchor = CommentAnchor.Create("f", Lines(Base), 2, 4);

        var result = anchor.Reanchor(TextSnapshot.FromText(string.Empty));

        Assert.Equal(ReanchorStatus.Lost, result.Status);
        Assert.Equal((0, 0), (result.StartLine, result.EndLine));
    }

    [Fact]
    public void Hash_helpers_are_stable_and_order_sensitive()
    {
        Assert.Equal(CommentAnchor.HashLine("x"), CommentAnchor.HashLine("x"));
        Assert.NotEqual(CommentAnchor.Combine([1UL, 2UL]), CommentAnchor.Combine([2UL, 1UL]));
        Assert.NotEqual(CommentAnchor.Combine([1UL]), CommentAnchor.Combine([1UL, 0UL]));
        Assert.Equal(
            CommentAnchor.LineHashesOf(Lines(Base), 0, 1),
            new[] { CommentAnchor.HashLine("alpha one"), CommentAnchor.HashLine("beta two") });
    }

    [Fact]
    public void Line_endings_do_not_change_anchor_hashes()
    {
        var lf = CommentAnchor.Create("f", TextSnapshot.FromText("a\nb\nc"), 0, 2);
        var crlf = CommentAnchor.Create("f", TextSnapshot.FromText("a\r\nb\r\nc"), 0, 2);

        Assert.Equal(lf.ContentHash, crlf.ContentHash);
        Assert.True(crlf.Matches(TextSnapshot.FromText("a\nb\nc")));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var snapshot = Lines(Base);
        Assert.Throws<ArgumentNullException>(() => CommentAnchor.Create("f", null!, 0, 0));
        Assert.Throws<ArgumentException>(() => CommentAnchor.Create(" ", snapshot, 0, 0));
        Assert.Throws<ArgumentNullException>(() => CommentAnchorSearch.Reanchor(null!, snapshot));
        Assert.Throws<ArgumentNullException>(() => CommentAnchor.Combine(null!));
    }
}
