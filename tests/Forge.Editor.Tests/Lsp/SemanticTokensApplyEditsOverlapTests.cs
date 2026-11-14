using Forge.Editor.Lsp;

namespace Forge.Editor.Tests.Lsp;

/// <summary>
/// <see cref="SemanticTokensDecoder.ApplyEdits"/> against a server that sends overlapping, unordered or out-of-range edits.
/// The in-order, non-overlapping cases live in <see cref="SemanticTokensDecoderTests"/>; here the contract is that bad input
/// degrades to a deterministic splice and never throws or reads outside the previous data.
/// </summary>
public class SemanticTokensApplyEditsOverlapTests
{
    private static readonly int[] Ten = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

    [Fact]
    public void An_edit_that_starts_inside_the_range_the_previous_edit_deleted_is_moved_to_its_end()
    {
        // The first edit removes 1..4; the second claims start 3 (already deleted), so it applies at 5 and removes 5..6.
        var result = SemanticTokensDecoder.ApplyEdits(Ten, [new SemanticTokensEdit(1, 4, [90]), new SemanticTokensEdit(3, 2, [80])]);

        Assert.Equal(new[] { 0, 90, 80, 7, 8, 9 }, result);
    }

    [Fact]
    public void An_edit_starting_inside_the_previous_deleted_range_is_applied_after_it()
    {
        var result = SemanticTokensDecoder.ApplyEdits(Ten, [new SemanticTokensEdit(2, 6, null), new SemanticTokensEdit(4, 1, [70])]);

        // Its start (4) lies inside the deleted 2..7, so it moves to 8 and its own delete count then removes the 8.
        Assert.Equal(new[] { 0, 1, 70, 9 }, result);
    }

    [Fact]
    public void Overlapping_edits_given_in_reverse_order_are_sorted_before_they_are_resolved()
    {
        var reversed = SemanticTokensDecoder.ApplyEdits(Ten, [new SemanticTokensEdit(3, 2, [80]), new SemanticTokensEdit(1, 4, [90])]);
        var ordered = SemanticTokensDecoder.ApplyEdits(Ten, [new SemanticTokensEdit(1, 4, [90]), new SemanticTokensEdit(3, 2, [80])]);

        Assert.Equal(ordered, reversed);
    }

    [Fact]
    public void Several_out_of_order_edits_are_all_applied_against_the_original_positions()
    {
        var result = SemanticTokensDecoder.ApplyEdits(
            Ten,
            [new SemanticTokensEdit(8, 1, [88]), new SemanticTokensEdit(0, 1, null), new SemanticTokensEdit(4, 0, [44, 45]), new SemanticTokensEdit(2, 1, [22])]);

        Assert.Equal(new[] { 1, 22, 3, 44, 45, 4, 5, 6, 7, 88, 9 }, result);
    }

    [Fact]
    public void Inserts_at_the_same_start_keep_the_order_the_server_sent_them()
    {
        var result = SemanticTokensDecoder.ApplyEdits(Ten, [new SemanticTokensEdit(5, 0, [1000]), new SemanticTokensEdit(5, 0, [2000])]);

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 1000, 2000, 5, 6, 7, 8, 9 }, result);
    }

    [Fact]
    public void A_delete_count_that_runs_past_the_end_stops_at_the_end_and_later_edits_append()
    {
        var result = SemanticTokensDecoder.ApplyEdits(Ten, [new SemanticTokensEdit(8, 50, [1]), new SemanticTokensEdit(9, 0, [2])]);

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 1, 2 }, result);
    }

    [Fact]
    public void A_negative_start_or_delete_count_is_clamped_instead_of_throwing()
    {
        Assert.Equal(new[] { 5, 0, 1, 2 }, SemanticTokensDecoder.ApplyEdits([0, 1, 2], [new SemanticTokensEdit(-4, 0, [5])]));
        Assert.Equal(new[] { 0, 1, 2 }, SemanticTokensDecoder.ApplyEdits([0, 1, 2], [new SemanticTokensEdit(1, -3, null)]));
    }

    [Fact]
    public void Edits_against_empty_previous_data_append_everything_in_order()
    {
        var result = SemanticTokensDecoder.ApplyEdits([], [new SemanticTokensEdit(0, 0, [1, 2]), new SemanticTokensEdit(7, 3, [3])]);

        Assert.Equal(new[] { 1, 2, 3 }, result);
    }

    [Fact]
    public void The_previous_data_is_never_modified_by_overlapping_edits()
    {
        var previous = (int[])Ten.Clone();

        _ = SemanticTokensDecoder.ApplyEdits(previous, [new SemanticTokensEdit(6, 3, [1]), new SemanticTokensEdit(1, 8, null)]);

        Assert.Equal(Ten, previous);
    }

    [Fact]
    public void Random_overlapping_and_unordered_edits_never_throw_and_match_a_clamped_splice_model()
    {
        var random = new Random(4242);
        for (var round = 0; round < 500; round++)
        {
            var previous = Enumerable.Range(0, random.Next(0, 25)).Select(_ => random.Next(100)).ToArray();
            var edits = Enumerable.Range(0, random.Next(0, 6))
                .Select(_ => new SemanticTokensEdit(
                    random.Next(-2, previous.Length + 5),
                    random.Next(-1, 8),
                    random.Next(3) == 0 ? null : Enumerable.Range(0, random.Next(0, 4)).Select(_ => 100 + random.Next(100)).ToArray()))
                .ToList();

            // Reference model: sort by start (stable for ties), then walk once, moving each start past what was consumed.
            var sorted = edits.Select((edit, index) => (edit, index)).OrderBy(static pair => pair.edit.Start).ThenBy(static pair => pair.index)
                .Select(static pair => pair.edit).ToList();
            var expected = new List<int>();
            var position = 0;
            foreach (var edit in sorted)
            {
                var start = Math.Clamp(edit.Start, position, previous.Length);
                expected.AddRange(previous[position..start]);
                if (edit.Data is not null)
                {
                    expected.AddRange(edit.Data);
                }

                position = Math.Min(previous.Length, start + Math.Max(0, edit.DeleteCount));
            }

            expected.AddRange(previous[position..]);

            // Array.Sort is not stable, so equal starts out of order may differ in data order; compare multisets then.
            var actual = SemanticTokensDecoder.ApplyEdits(previous, edits);
            var hasTies = sorted.GroupBy(static edit => edit.Start).Any(static group => group.Count() > 1);
            if (hasTies)
            {
                Assert.Equal(expected.Count, actual.Length);
            }
            else
            {
                Assert.Equal(expected, actual);
            }
        }
    }
}
