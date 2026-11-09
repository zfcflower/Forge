using AvaloniaEdit.Document;
using Forge.App.Changes;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Changes;

public sealed class DiffDocumentBuilderTests
{
    private static DiffFileSpec Spec(
        FileDiff diff, bool collapsed = false, bool markdown = false, params int[] expanded) =>
        new(diff, collapsed, markdown, true, expanded.Length == 0 ? null : expanded.ToHashSet());

    private static DiffRowKind[] Kinds(DiffRowMap map) => [.. Enumerable.Range(0, map.Count).Select(i => map[i].Kind)];

    [Fact]
    public void An_inline_file_is_a_header_then_folds_context_bar_and_the_changed_rows_in_order()
    {
        var result = DiffDocumentBuilder.Build([Spec(DiffFixtures.Replace("a.cs"))], split: false);
        Assert.Null(result.Secondary);
        var map = result.Primary.Map;
        Assert.Equal(
            [
                DiffRowKind.FileHeader, DiffRowKind.Fold, DiffRowKind.Context, DiffRowKind.Context, DiffRowKind.Context,
                DiffRowKind.BlockBar, DiffRowKind.Removed, DiffRowKind.Added, DiffRowKind.Context, DiffRowKind.Context,
                DiffRowKind.Context, DiffRowKind.Fold,
            ],
            Kinds(map));
        string[] expected = [" ", " ", "line7", "line8", "line9", " ", "old10", "new10", "line11", "line12", "line13", " "];
        Assert.Equal(expected, result.Primary.Text.Split('\n'));
    }

    [Fact]
    public void Placeholder_lines_hold_exactly_the_one_space_the_placeholder_element_replaces()
    {
        var result = DiffDocumentBuilder.Build([Spec(DiffFixtures.Replace("a.cs"))], split: false);
        var lines = result.Primary.Text.Split('\n');
        Assert.Equal(result.Primary.Map.Count, lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var row = result.Primary.Map[i];
            Assert.Equal(row.IsPlaceholder, lines[i] == DiffDocumentBuilder.PlaceholderText);
        }

        Assert.Equal(" ", DiffDocumentBuilder.PlaceholderText);
    }

    [Fact]
    public void Folds_report_how_many_lines_they_hide_and_which_new_line_they_start_at()
    {
        var map = DiffDocumentBuilder.Build([Spec(DiffFixtures.Replace("a.cs"))], false).Primary.Map;
        var first = map[1];
        Assert.Equal((6, 1, true), (first.Count, first.Key, first.Expandable));
        var last = map[11];
        Assert.Equal((7, 14, true), (last.Count, last.Key, last.Expandable));
    }

    [Fact]
    public void Expanding_a_fold_replaces_the_bar_with_its_context_lines_and_keeps_the_other_fold()
    {
        var head = DiffDocumentBuilder.Build([Spec(DiffFixtures.Replace("a.cs"), expanded: 1)], false).Primary.Map;
        Assert.Equal(17, head.Count);
        Assert.Equal(DiffRowKind.Context, head[1].Kind);
        Assert.Equal(1, head[1].NewLine);
        Assert.Equal(DiffRowKind.Fold, head[16].Kind);

        var tail = DiffDocumentBuilder.Build([Spec(DiffFixtures.Replace("a.cs"), expanded: 14)], false).Primary.Map;
        Assert.Equal(18, tail.Count);
        Assert.Equal(DiffRowKind.Fold, tail[1].Kind);
        Assert.All(Enumerable.Range(11, 7), i => Assert.Equal(DiffRowKind.Context, tail[i].Kind));
    }

    [Fact]
    public void A_gap_between_two_hunks_is_a_fold_that_cannot_be_expanded()
    {
        var map = DiffDocumentBuilder.Build([Spec(DiffFixtures.TwoHunks("a.cs"))], false).Primary.Map;
        Assert.Equal(16, map.Count);
        Assert.Equal(DiffRowKind.Fold, map[8].Kind);
        Assert.Equal((21, false), (map[8].Count, map[8].Expandable));
        Assert.Equal(0, map[2].Block);
        Assert.Equal(1, map[12].Block);
        Assert.Equal((0, 1), (map[3].Hunk, map[13].Hunk));
    }

    [Fact]
    public void Each_change_block_gets_one_bar_numbered_like_DiffBlocks_Split()
    {
        var diff = DiffFixtures.TwoHunks("a.cs");
        var map = DiffDocumentBuilder.Build([Spec(diff)], false).Primary.Map;
        var bars = Enumerable.Range(0, map.Count).Where(i => map[i].Kind == DiffRowKind.BlockBar).Select(i => map[i].Block).ToArray();
        Assert.Equal(DiffBlocks.Split(diff).Count, bars.Length);
        Assert.Equal([0, 1], bars);
    }

    [Fact]
    public void A_collapsed_file_is_its_header_row_only()
    {
        var result = DiffDocumentBuilder.Build([Spec(DiffFixtures.Replace("a.cs"), collapsed: true), Spec(DiffFixtures.Replace("b.cs"))], false);
        var map = result.Primary.Map;
        Assert.Equal(1, map.HeaderLine(0));
        Assert.Equal(1, map.FileLastLine(0));
        Assert.Equal(2, map.HeaderLine(1));
        Assert.Equal(DiffRowKind.FileHeader, map[1].Kind);
    }

    [Fact]
    public void A_markdown_preview_file_is_its_header_and_one_embed_row()
    {
        var map = DiffDocumentBuilder.Build([Spec(DiffFixtures.Replace("a.md"), markdown: true)], false).Primary.Map;
        Assert.Equal([DiffRowKind.FileHeader, DiffRowKind.Embed], Kinds(map));
        Assert.True(map[1].IsPlaceholder);
    }

    [Fact]
    public void Files_follow_each_other_and_the_map_knows_where_each_one_starts_and_ends()
    {
        var map = DiffDocumentBuilder.Build([Spec(DiffFixtures.Replace("a.cs")), Spec(DiffFixtures.Replace("b.cs"))], false).Primary.Map;
        Assert.Equal(24, map.Count);
        Assert.Equal(2, map.FileCount);
        Assert.Equal((1, 12, 13, 24), (map.HeaderLine(0), map.FileLastLine(0), map.HeaderLine(1), map.FileLastLine(1)));
        Assert.Equal((0, 0, 1, 1, -1), (map.FileAt(1), map.FileAt(12), map.FileAt(13), map.FileAt(24), map.FileAt(25)));
        Assert.Equal(0, map.HeaderLine(2));
        Assert.Equal(0, map.FileLastLine(-1));
    }

    [Fact]
    public void No_files_build_an_empty_document()
    {
        var result = DiffDocumentBuilder.Build([], false);
        Assert.Equal(string.Empty, result.Primary.Text);
        Assert.Equal(0, result.Primary.Map.Count);
    }

    [Fact]
    public void A_pure_deletion_has_a_bar_and_removed_rows_but_nothing_to_comment_on()
    {
        var map = DiffDocumentBuilder.Build([Spec(DiffFixtures.DeletionOnly("a.cs"))], false).Primary.Map;
        Assert.Equal(
            [DiffRowKind.FileHeader, DiffRowKind.Fold, DiffRowKind.Context, DiffRowKind.BlockBar, DiffRowKind.Removed, DiffRowKind.Removed],
            Kinds(map));
        Assert.False(map.TryMapRange(5, 6, out _));
        Assert.True(map.TryMapRange(3, 6, out var range));
        Assert.Equal(new DiffRange(0, 0, 4, 4), range);
    }

    [Fact]
    public void Split_mode_builds_two_documents_with_the_same_number_of_rows_and_the_same_placeholders()
    {
        var result = DiffDocumentBuilder.Build([Spec(DiffFixtures.Replace("a.cs")), Spec(DiffFixtures.TwoHunks("b.cs"))], split: true);
        var left = result.Primary.Map;
        var right = result.Secondary!.Map;
        Assert.Equal(left.Count, right.Count);
        for (var i = 0; i < left.Count; i++)
        {
            Assert.Equal(left[i].IsPlaceholder, right[i].IsPlaceholder);
            if (left[i].IsPlaceholder)
            {
                Assert.Equal(left[i].Kind, right[i].Kind);
            }
        }

        Assert.Equal(result.Primary.Text.Count(static c => c == '\n'), result.Secondary.Text.Count(static c => c == '\n'));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 3)]
    [InlineData(0, 2)]
    [InlineData(3, 0)]
    public void Split_mode_pads_the_shorter_side_of_a_block_with_spacers(int removed, int added)
    {
        var result = DiffDocumentBuilder.Build([Spec(DiffFixtures.Uneven("a.cs", removed, added))], true);
        var left = result.Primary.Map;
        var right = result.Secondary!.Map;
        var rows = Math.Max(removed, added);
        Assert.Equal(1 + 3 + 1 + rows, left.Count);
        Assert.Equal(left.Count, right.Count);
        var firstBody = 5;
        for (var k = 0; k < rows; k++)
        {
            Assert.Equal(k < removed ? DiffRowKind.Removed : DiffRowKind.Spacer, left[firstBody + k].Kind);
            Assert.Equal(k < added ? DiffRowKind.Added : DiffRowKind.Spacer, right[firstBody + k].Kind);
        }

        Assert.Equal(DiffRowKind.Context, left[1].Kind);
        Assert.Equal(DiffRowKind.Context, right[1].Kind);
    }

    [Fact]
    public void Split_mode_shows_old_line_numbers_on_the_left_and_new_ones_on_the_right()
    {
        var result = DiffDocumentBuilder.Build([Spec(DiffFixtures.Replace("a.cs"))], true);
        var left = result.Primary.Map;
        var right = result.Secondary!.Map;
        Assert.Equal((10, 0), (left[6].OldLine, left[6].NewLine));
        Assert.Equal((0, 10), (right[6].OldLine, right[6].NewLine));
        Assert.Equal(DiffRowKind.Removed, left[6].Kind);
        Assert.Equal(DiffRowKind.Added, right[6].Kind);
        Assert.Equal(11, left[7].OldLine);
        Assert.Equal(11, right[7].NewLine);
    }

    [Fact]
    public void Line_text_never_carries_a_line_break_that_would_shift_the_row_numbers()
    {
        var lines = new List<DiffLine>
        {
            new(DiffLineKind.Context, 1, 1, "alpha\r"),
            new(DiffLineKind.Added, 0, 2, "be\nta"),
        };
        var diff = new FileDiff("a.cs", "a.cs", FileDiffStatus.Modified, false, [new DiffHunk(1, 1, 1, 2, lines)]);
        var result = DiffDocumentBuilder.Build([Spec(diff)], false);
        Assert.Equal(result.Primary.Map.Count, result.Primary.Text.Split('\n').Length);
        Assert.Contains("alpha", result.Primary.Text);
        Assert.DoesNotContain('\r', result.Primary.Text);
    }

    [Fact]
    public void TryFindNewLine_finds_shown_new_side_lines_and_nothing_inside_a_fold()
    {
        var map = DiffDocumentBuilder.Build([Spec(DiffFixtures.Replace("a.cs"))], false).Primary.Map;
        Assert.True(map.TryFindNewLine(0, 10, out var added));
        Assert.Equal(8, added);
        Assert.True(map.TryFindNewLine(0, 7, out var context));
        Assert.Equal(3, context);
        Assert.False(map.TryFindNewLine(0, 3, out _));
        Assert.False(map.TryFindNewLine(5, 1, out _));
    }

    [Fact]
    public void A_comment_range_maps_to_new_side_lines_of_one_file_and_hunk()
    {
        var map = DiffDocumentBuilder.Build([Spec(DiffFixtures.Replace("a.cs")), Spec(DiffFixtures.TwoHunks("b.cs"))], false).Primary.Map;
        Assert.True(map.TryMapRange(3, 4, out var two));
        Assert.Equal(new DiffRange(0, 0, 7, 8), two);
        Assert.True(map.TryMapRange(3, 8, out var across));
        Assert.Equal(new DiffRange(0, 0, 7, 10), across);
        Assert.True(map.TryMapRange(8, 8, out var added));
        Assert.Equal(10, added.NewStart);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    [InlineData(2, 2)]
    [InlineData(11, 14)]
    [InlineData(0, 3)]
    [InlineData(5, 3)]
    [InlineData(1, 999)]
    public void A_comment_range_without_a_new_side_line_or_across_files_is_rejected(int start, int end)
    {
        var map = DiffDocumentBuilder.Build([Spec(DiffFixtures.Replace("a.cs")), Spec(DiffFixtures.TwoHunks("b.cs"))], false).Primary.Map;
        Assert.False(map.TryMapRange(start, end, out _));
    }

    [Fact]
    public void A_comment_range_across_two_hunks_is_rejected()
    {
        var map = DiffDocumentBuilder.Build([Spec(DiffFixtures.TwoHunks("b.cs"))], false).Primary.Map;
        Assert.False(map.TryMapRange(2, 11, out _));
        Assert.True(map.TryMapRange(2, 5, out _));
    }

    [Fact]
    public void Copy_text_skips_headers_bars_folds_and_spacers_but_keeps_every_code_line()
    {
        var model = DiffDocumentBuilder.Build([Spec(DiffFixtures.Replace("a.cs"))], false).Primary;
        var document = new TextDocument(model.Text);
        var all = DiffSelectionText.Extract(document, model.Map, 0, document.TextLength);
        Assert.Equal("line7\nline8\nline9\nold10\nnew10\nline11\nline12\nline13", all);
    }

    [Fact]
    public void Copy_text_of_a_partial_selection_cuts_the_first_and_last_line_and_drops_a_trailing_empty_line()
    {
        var model = DiffDocumentBuilder.Build([Spec(DiffFixtures.Replace("a.cs"))], false).Primary;
        var document = new TextDocument(model.Text);
        var line7 = document.GetLineByNumber(3);
        var line8 = document.GetLineByNumber(4);
        Assert.Equal("ne7\nlin", DiffSelectionText.Extract(document, model.Map, line7.Offset + 2, line8.Offset + 3));
        Assert.Equal("line7", DiffSelectionText.Extract(document, model.Map, line7.Offset, line8.Offset));
        Assert.Equal(string.Empty, DiffSelectionText.Extract(document, model.Map, 0, 2));
        Assert.Equal(string.Empty, DiffSelectionText.Extract(document, model.Map, 5, 5));
    }

    [Fact]
    public void Copy_text_leaves_out_the_spacer_rows_of_a_split_side()
    {
        var result = DiffDocumentBuilder.Build([Spec(DiffFixtures.Uneven("a.cs", 1, 2))], true);
        var left = new TextDocument(result.Primary.Text);
        var right = new TextDocument(result.Secondary!.Text);
        Assert.Equal("line1\nline2\nline3\nold0", DiffSelectionText.Extract(left, result.Primary.Map, 0, left.TextLength));
        Assert.Equal("line1\nline2\nline3\nnew0\nnew1", DiffSelectionText.Extract(right, result.Secondary.Map, 0, right.TextLength));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replacing_one_file_in_a_map_and_text_equals_a_full_rebuild(bool split)
    {
        var diffs = new[] { DiffFixtures.Replace("a.cs"), DiffFixtures.Replace("b.cs"), DiffFixtures.TwoHunks("c.cs") };
        var before = DiffDocumentBuilder.Build([.. diffs.Select(d => Spec(d))], split);
        var specs = diffs.Select(d => Spec(d)).ToArray();
        specs[1] = Spec(diffs[1], collapsed: true);
        var after = DiffDocumentBuilder.Build(specs, split);
        var (left, right) = DiffDocumentBuilder.BuildOne(specs[1], 1, split);
        var map = before.Primary.Map.WithFile(1, left.Rows);
        Assert.Equal(Kinds(after.Primary.Map), Kinds(map));
        for (var f = 0; f < 3; f++)
        {
            Assert.Equal(after.Primary.Map.HeaderLine(f), map.HeaderLine(f));
            Assert.Equal(after.Primary.Map.FileLastLine(f), map.FileLastLine(f));
        }

        var lines = before.Primary.Text.Split('\n').ToList();
        var first = before.Primary.Map.HeaderLine(1) - 1;
        lines.RemoveRange(first, before.Primary.Map.FileLastLine(1) - first);
        lines.InsertRange(first, left.Text.Split('\n'));
        Assert.Equal(after.Primary.Text, string.Join('\n', lines));
        Assert.Equal(split, right is not null);
        if (right is not null)
        {
            Assert.Equal(Kinds(after.Secondary!.Map), Kinds(before.Secondary!.Map.WithFile(1, right.Rows)));
        }
    }

    [Fact]
    public void Fold_keys_list_every_fold_the_diff_could_show_even_when_expanded()
    {
        var diff = DiffFixtures.Replace("a.cs");
        Assert.Equal([1, 14], DiffDocumentBuilder.FoldKeys(diff).Order());
        Assert.Empty(DiffDocumentBuilder.FoldKeys(DiffFixtures.Replace("b.cs", 6, 3)));
    }
}
