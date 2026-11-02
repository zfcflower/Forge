using SemanticTokenMapper = Forge.Editor.Highlighting.SemanticTokenMapper;
using TokenCategory = Forge.Editor.Highlighting.TokenCategory;
using System.Text.Json;

using Forge.Editor.Lsp;

namespace Forge.Editor.Tests.Lsp;

public class SemanticTokensDecoderTests
{
    private static readonly SemanticTokensLegend Legend = new(
        ["namespace", "type", "class", "method"],
        ["declaration", "static", "readonly"]);

    [Fact]
    public void Empty_or_partial_data_decodes_to_complete_tokens_only()
    {
        Assert.Empty(SemanticTokensDecoder.Decode(ReadOnlySpan<int>.Empty, Legend));
        Assert.Empty(SemanticTokensDecoder.Decode(new[] { 0, 0, 1, 0 }, Legend));

        var tokens = SemanticTokensDecoder.Decode(new[] { 0, 0, 1, 0, 0, 0, 2, 1, 0, 0, 9, 9 }, Legend);

        Assert.Equal(2, tokens.Count);
    }

    [Fact]
    public void Same_line_deltas_accumulate_the_start_column()
    {
        var tokens = SemanticTokensDecoder.Decode(new[] { 0, 2, 1, 2, 0, 0, 3, 4, 3, 0 }, Legend);

        Assert.Equal(new[] { 2, 5 }, tokens.Select(t => t.StartChar));
        Assert.Equal(new[] { 0, 0 }, tokens.Select(t => t.Line));
        Assert.Equal(new[] { 3, 9 }, tokens.Select(t => t.EndChar));
    }

    [Fact]
    public void A_line_change_resets_the_start_column_to_the_delta()
    {
        var tokens = SemanticTokensDecoder.Decode(new[] { 0, 6, 2, 1, 0, 2, 1, 1, 2, 0, 0, 3, 1, 3, 1 }, Legend);

        Assert.Equal(new[] { 0, 2, 2 }, tokens.Select(t => t.Line));
        Assert.Equal(new[] { 6, 1, 4 }, tokens.Select(t => t.StartChar));
        Assert.Equal("type", tokens[0].TypeName);
        Assert.Equal("class", tokens[1].TypeName);
        Assert.Equal("method", tokens[2].TypeName);
    }

    [Fact]
    public void Unknown_type_index_has_an_unknown_name_and_keeps_the_numeric_type()
    {
        var tokens = SemanticTokensDecoder.Decode(new[] { 0, 0, 1, 42, 0 }, Legend);

        Assert.Equal(42, tokens[0].Type);
        Assert.Equal("unknown", tokens[0].TypeName);
        Assert.Equal("unknown", Legend.TypeName(-1));
    }

    [Fact]
    public void Modifier_bits_map_to_names_in_legend_order_and_ignore_unknown_bits()
    {
        Assert.Equal(new[] { "declaration", "readonly" }, Legend.ModifierNames(0b101));
        Assert.Empty(Legend.ModifierNames(0));
        Assert.Equal(new[] { "declaration" }, Legend.ModifierNames(0b1001));
        Assert.Empty(SemanticTokensLegend.Empty.ModifierNames(7));

        var token = new SemanticToken(0, 0, 1, 0, 0b010);
        Assert.Equal(new[] { "static" }, new SemanticTokensResult(null, [token], Legend).ModifierNames(token));
    }

    [Fact]
    public void Empty_edit_list_returns_a_copy_of_the_previous_data()
    {
        int[] previous = [1, 2, 3, 4, 5];

        var result = SemanticTokensDecoder.ApplyEdits(previous, []);

        Assert.Equal(previous, result);
        Assert.NotSame(previous, result);
    }

    [Fact]
    public void Edit_removing_the_middle_of_the_data_splices_it_out()
    {
        int[] previous = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

        var result = SemanticTokensDecoder.ApplyEdits(previous, [new SemanticTokensEdit(2, 3, [100, 200])]);

        Assert.Equal(new[] { 0, 1, 100, 200, 5, 6, 7, 8, 9 }, result);
    }

    [Fact]
    public void Insertion_at_the_start_and_at_the_end_are_both_applied()
    {
        int[] previous = [1, 2, 3];

        var result = SemanticTokensDecoder.ApplyEdits(previous, [new SemanticTokensEdit(0, 0, [9]), new SemanticTokensEdit(3, 0, [8])]);

        Assert.Equal(new[] { 9, 1, 2, 3, 8 }, result);
    }

    [Fact]
    public void Edits_given_out_of_order_are_applied_against_original_positions()
    {
        int[] previous = [0, 1, 2, 3, 4, 5, 6, 7];

        var result = SemanticTokensDecoder.ApplyEdits(previous, [new SemanticTokensEdit(6, 1, null), new SemanticTokensEdit(1, 2, [42])]);

        Assert.Equal(new[] { 0, 42, 3, 4, 5, 7 }, result);
    }

    [Fact]
    public void Delete_only_edit_without_data_and_full_replacement_work()
    {
        int[] previous = [5, 5, 5];

        Assert.Equal(new[] { 5 }, SemanticTokensDecoder.ApplyEdits(previous, [new SemanticTokensEdit(1, 2, null)]));
        Assert.Equal(new[] { 7, 7 }, SemanticTokensDecoder.ApplyEdits(previous, [new SemanticTokensEdit(0, 3, [7, 7])]));
        Assert.Empty(SemanticTokensDecoder.ApplyEdits(previous, [new SemanticTokensEdit(0, 99, null)]));
    }

    [Fact]
    public void Edit_starting_past_the_end_is_clamped_to_an_append()
    {
        int[] previous = [1, 2];

        var result = SemanticTokensDecoder.ApplyEdits(previous, [new SemanticTokensEdit(50, 4, [3])]);

        Assert.Equal(new[] { 1, 2, 3 }, result);
    }

    [Fact]
    public void Delta_inserting_a_token_on_a_later_line_keeps_line_numbers_consistent()
    {
        int[] previous = [0, 0, 1, 0, 0, 2, 0, 1, 0, 0];

        var merged = SemanticTokensDecoder.ApplyEdits(previous, [new SemanticTokensEdit(5, 0, [1, 4, 2, 3, 0])]);
        var tokens = SemanticTokensDecoder.Decode(merged, Legend);

        Assert.Equal(new[] { 0, 1, 3 }, tokens.Select(t => t.Line));
        Assert.Equal(new[] { 0, 4, 0 }, tokens.Select(t => t.StartChar));
        Assert.Equal(new[] { "namespace", "method", "namespace" }, tokens.Select(t => t.TypeName));
    }

    [Fact]
    public void Delta_that_removes_a_whole_line_of_tokens_shifts_later_lines_by_the_removed_delta()
    {
        int[] previous = [0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 2, 0, 1, 0, 0];

        var merged = SemanticTokensDecoder.ApplyEdits(previous, [new SemanticTokensEdit(5, 5, null)]);
        var tokens = SemanticTokensDecoder.Decode(merged, Legend);

        Assert.Equal(new[] { 0, 2 }, tokens.Select(t => t.Line));
    }

    [Fact]
    public void Random_non_overlapping_edits_match_a_splice_model()
    {
        var random = new Random(77);
        for (var round = 0; round < 200; round++)
        {
            int[] previous = Enumerable.Range(0, random.Next(0, 30)).Select(_ => random.Next(100)).ToArray();
            var edits = new List<SemanticTokensEdit>();
            var position = 0;
            while (position <= previous.Length && edits.Count < 4)
            {
                var start = position + random.Next(0, 4);
                if (start > previous.Length)
                {
                    break;
                }

                var delete = random.Next(0, Math.Min(3, previous.Length - start) + 1);
                int[]? data = random.Next(2) == 0 ? null : Enumerable.Range(0, random.Next(0, 4)).Select(_ => random.Next(100)).ToArray();
                edits.Add(new SemanticTokensEdit(start, delete, data));
                position = start + delete + 1;
            }

            var expected = new List<int>();
            var cursor = 0;
            foreach (var edit in edits)
            {
                expected.AddRange(previous[cursor..edit.Start]);
                if (edit.Data is not null)
                {
                    expected.AddRange(edit.Data);
                }

                cursor = edit.Start + edit.DeleteCount;
            }

            expected.AddRange(previous[cursor..]);
            Assert.Equal(expected, SemanticTokensDecoder.ApplyEdits(previous, edits));
        }
    }

    [Fact]
    public void Read_data_and_int_arrays_tolerate_wrong_shapes()
    {
        using var document = JsonDocument.Parse("""{"data":[1,2,3],"other":"x"}""");
        using var wrong = JsonDocument.Parse("""{"data":"nope"}""");
        using var array = JsonDocument.Parse("[4,5]");

        Assert.Equal(new[] { 1, 2, 3 }, SemanticTokensDecoder.ReadData(document.RootElement));
        Assert.Empty(SemanticTokensDecoder.ReadData(wrong.RootElement));
        Assert.Empty(SemanticTokensDecoder.ReadData(array.RootElement));
        Assert.Equal(new[] { 4, 5 }, SemanticTokensDecoder.ReadIntArray(array.RootElement));
        Assert.Empty(SemanticTokensDecoder.ReadIntArray(document.RootElement));
    }

    [Fact]
    public void Known_lsp_token_types_map_to_lexical_categories_case_insensitively()
    {
        Assert.Equal(TokenCategory.Function, SemanticTokenMapper.GetCategory("method"));
        Assert.Equal(TokenCategory.Keyword, SemanticTokenMapper.GetCategory("KEYWORD"));
        Assert.Equal(TokenCategory.TypeParameter, SemanticTokenMapper.GetCategory("typeParameter"));
        Assert.Equal(TokenCategory.None, SemanticTokenMapper.GetCategory("notAType"));
        Assert.True(SemanticTokenMapper.TryGetCategory("comment", out var comment));
        Assert.Equal(TokenCategory.Comment, comment);
        Assert.False(SemanticTokenMapper.TryGetCategory("zzz", out _));
        Assert.Contains("comment", SemanticTokenMapper.KnownTokenTypes);
    }

    [Fact]
    public void Legend_mapping_is_positional_and_unknown_entries_are_none()
    {
        var mapped = SemanticTokenMapper.MapLegend(["class", "unknownX", "enumMember"]);

        Assert.Equal(new[] { TokenCategory.Class, TokenCategory.None, TokenCategory.EnumMember }, mapped);
        Assert.Empty(SemanticTokenMapper.MapLegend([]));
    }
}
