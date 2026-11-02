using Forge.Editor.Diff;
using Forge.Editor.Text;

namespace Forge.Editor.Tests.Diff;

public class LineDiffTests
{
    [Fact]
    public void Identical_inputs_produce_an_empty_diff_with_all_lines_equal()
    {
        var result = LineDiff.Compute(new[] { "a", "b" }, new[] { "a", "b" });

        Assert.True(result.IsEmpty);
        Assert.Equal(0, result.ChangedCount);
        Assert.Equal(2, result.EqualCount);
        Assert.Empty(result.Hunks);
        Assert.All(result.Lines, line => Assert.Equal(DiffLineKind.Equal, line.Kind));
    }

    [Fact]
    public void Two_empty_sides_produce_nothing()
    {
        var result = LineDiff.Compute(Array.Empty<string>(), Array.Empty<string>());

        Assert.Empty(result.Lines);
        Assert.Empty(result.Hunks);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Empty_old_side_is_a_single_insert_hunk()
    {
        var result = LineDiff.Compute(Array.Empty<string>(), new[] { "a", "b" });

        Assert.Equal(2, result.InsertedCount);
        Assert.Equal(0, result.DeletedCount);
        var hunk = Assert.Single(result.Hunks);
        Assert.Equal((0, 0, 0, 2), (hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount));
        Assert.True(hunk.HasChanges);
    }

    [Fact]
    public void Empty_new_side_is_a_single_delete_hunk()
    {
        var result = LineDiff.Compute(new[] { "a", "b" }, Array.Empty<string>());

        Assert.Equal(2, result.DeletedCount);
        var hunk = Assert.Single(result.Hunks);
        Assert.Equal((0, 2, 0, 0), (hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount));
    }

    [Fact]
    public void Completely_different_lines_pair_up_as_replacements()
    {
        var result = LineDiff.Compute(new[] { "a", "b" }, new[] { "c", "d" });

        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(2, result.InsertedCount);
        var hunk = Assert.Single(result.Hunks);
        Assert.Equal(new[] { (0, 0), (1, 1) }, LineDiff.PairReplacements(hunk.Lines));
    }

    [Fact]
    public void Insertion_in_the_middle_is_reported_with_neighbours_as_equal()
    {
        var result = LineDiff.Compute(new[] { "a", "b", "c" }, new[] { "a", "x", "b", "c" });

        Assert.Equal(
            new[]
            {
                new DiffLine(DiffLineKind.Equal, 0, 0),
                new DiffLine(DiffLineKind.Insert, -1, 1),
                new DiffLine(DiffLineKind.Equal, 1, 2),
                new DiffLine(DiffLineKind.Equal, 2, 3),
            },
            result.Lines);
        Assert.Equal(3, result.EqualCount);
        var hunk = Assert.Single(result.Hunks);
        Assert.Equal((0, 3, 0, 4), (hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount));
        Assert.Equal(0, hunk.OldLineAt(0));
        Assert.Equal(2, hunk.NewLineAt(2));
    }

    [Fact]
    public void Unique_line_anchors_align_the_remaining_changes()
    {
        var result = LineDiff.Compute(new[] { "a", "b", "c", "d", "e" }, new[] { "a", "x", "c", "y", "e" });

        Assert.Equal(3, result.EqualCount);
        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(2, result.InsertedCount);
        Assert.Equal(
            new[] { (1, -1), (3, -1) },
            result.Lines.Where(l => l.Kind == DiffLineKind.Delete).Select(l => (l.OldLine, l.NewLine)).ToArray());
    }

    [Fact]
    public void Repeated_lines_use_an_exact_lcs()
    {
        var result = LineDiff.Compute(new[] { "a", "b", "a", "b" }, new[] { "b", "a", "b" });

        Assert.Equal(3, result.EqualCount);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(0, result.InsertedCount);
        Assert.Equal(new DiffLine(DiffLineKind.Delete, 0, -1), result.Lines[0]);
    }

    [Fact]
    public void Large_mismatched_blocks_without_anchors_fall_back_to_delete_then_insert()
    {
        var old = Enumerable.Repeat("x", 60).ToArray();
        var updated = Enumerable.Repeat("y", 60).ToArray();

        var result = LineDiff.Compute(old, updated);

        Assert.Equal(0, result.EqualCount);
        Assert.Equal(60, result.DeletedCount);
        Assert.Equal(60, result.InsertedCount);
        Assert.Equal(DiffLineKind.Delete, result.Lines[0].Kind);
        Assert.Equal(DiffLineKind.Insert, result.Lines[60].Kind);
    }

    [Fact]
    public void Large_file_with_two_distant_changes_yields_two_hunks_with_default_context()
    {
        var old = Enumerable.Range(0, 200).Select(i => $"line {i}").ToArray();
        var updated = (string[])old.Clone();
        updated[50] = "CHANGED";
        updated[150] = "CHANGED TOO";

        var result = LineDiff.Compute(old, updated);

        Assert.Equal(198, result.EqualCount);
        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(2, result.InsertedCount);
        Assert.Equal(2, result.Hunks.Count);
        Assert.Equal((47, 7, 47, 7), (result.Hunks[0].OldStart, result.Hunks[0].OldCount, result.Hunks[0].NewStart, result.Hunks[0].NewCount));
        Assert.Equal(147, result.Hunks[1].OldStart);
        Assert.All(result.Hunks, hunk => Assert.True(hunk.HasChanges));
    }

    [Fact]
    public void Large_context_merges_distant_hunks_into_one()
    {
        var old = Enumerable.Range(0, 200).Select(i => $"line {i}").ToArray();
        var updated = (string[])old.Clone();
        updated[50] = "CHANGED";
        updated[150] = "CHANGED TOO";

        var result = LineDiff.Compute(old, updated, new DiffOptions { ContextLines = 1000 });

        var hunk = Assert.Single(result.Hunks);
        Assert.Equal((0, 200, 0, 200), (hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(5, 1)]
    public void Context_size_decides_whether_changes_three_lines_apart_share_a_hunk(int context, int expectedHunks)
    {
        var old = Enumerable.Range(0, 20).Select(i => $"l{i}").ToArray();
        var updated = (string[])old.Clone();
        updated[10] = "A";
        updated[14] = "B";

        var result = LineDiff.Compute(old, updated, new DiffOptions { ContextLines = context });

        Assert.Equal(expectedHunks, result.Hunks.Count);
    }

    [Fact]
    public void Zero_context_separates_changes_that_have_any_line_between_them()
    {
        var old = Enumerable.Range(0, 20).Select(i => $"l{i}").ToArray();
        var updated = (string[])old.Clone();
        updated[10] = "A";
        updated[12] = "B";

        var result = LineDiff.Compute(old, updated, new DiffOptions { ContextLines = 0 });

        Assert.Equal(2, result.Hunks.Count);
        Assert.All(result.Hunks, hunk => Assert.Equal(0, hunk.Lines.Count(l => !l.IsChange)));
    }

    [Fact]
    public void Ignore_whitespace_treats_spacing_differences_as_equal()
    {
        var result = LineDiff.Compute(new[] { "a  b\t" }, new[] { "a b" }, new DiffOptions { IgnoreWhitespace = true });

        Assert.True(result.IsEmpty);
        Assert.False(LineDiff.Compute(new[] { "a  b" }, new[] { "a b" }).IsEmpty);
    }

    [Fact]
    public void Ignore_case_treats_letter_case_differences_as_equal()
    {
        Assert.True(LineDiff.Compute(new[] { "Hello" }, new[] { "hello" }, new DiffOptions { IgnoreCase = true }).IsEmpty);
        Assert.False(LineDiff.Compute(new[] { "Hello" }, new[] { "hello" }).IsEmpty);
    }

    [Fact]
    public void Snapshots_compare_line_terminators_by_default_and_ignore_them_on_request()
    {
        var crlf = TextSnapshot.FromText("a\r\nb\r\n");
        var lf = TextSnapshot.FromText("a\nb\n");

        var byDefault = LineDiff.Compute(crlf, lf);

        Assert.False(byDefault.IsEmpty);
        Assert.Equal(2, byDefault.DeletedCount);
        Assert.Equal(2, byDefault.InsertedCount);
        Assert.True(LineDiff.Compute(crlf, lf, new DiffOptions { IgnoreLineEndings = true }).IsEmpty);
    }

    [Fact]
    public void A_trailing_newline_is_compared_as_part_of_the_line()
    {
        var result = LineDiff.Compute(TextSnapshot.FromText("a\n"), TextSnapshot.FromText("a"));

        Assert.Equal(1, result.InsertedCount);
        Assert.Equal(2, result.DeletedCount);
        var hunk = Assert.Single(result.Hunks);
        Assert.Equal((0, 2, 0, 1), (hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount));
        Assert.Equal(
            new[]
            {
                new DiffLine(DiffLineKind.Delete, 0, -1),
                new DiffLine(DiffLineKind.Delete, 1, -1),
                new DiffLine(DiffLineKind.Insert, -1, 0),
            },
            result.Lines);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => LineDiff.Compute((TextSnapshot)null!, TextSnapshot.FromText("x")));
        Assert.Throws<ArgumentNullException>(() => LineDiff.PairReplacements(null!));
    }

    [Fact]
    public void Output_script_always_walks_both_sides_in_order()
    {
        var random = new Random(11);
        for (var round = 0; round < 40; round++)
        {
            var old = Enumerable.Range(0, random.Next(0, 60)).Select(_ => "l" + random.Next(6)).ToArray();
            var updated = Enumerable.Range(0, random.Next(0, 60)).Select(_ => "l" + random.Next(6)).ToArray();

            var result = LineDiff.Compute(old, updated);

            var oldSeen = result.Lines.Where(l => l.OldLine >= 0).Select(l => l.OldLine).ToArray();
            var newSeen = result.Lines.Where(l => l.NewLine >= 0).Select(l => l.NewLine).ToArray();
            Assert.Equal(Enumerable.Range(0, old.Length), oldSeen);
            Assert.Equal(Enumerable.Range(0, updated.Length), newSeen);
            foreach (var line in result.Lines.Where(l => l.Kind == DiffLineKind.Equal))
            {
                Assert.Equal(old[line.OldLine], updated[line.NewLine]);
            }
        }
    }
}
