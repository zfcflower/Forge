using Forge.Editor.Folding;
using Forge.Editor.Text;

namespace Forge.Editor.Tests.Folding;

public class StructuralFoldingTests
{
    private static IReadOnlyList<FoldingRange> Braces(string text) =>
        StructuralFolding.Compute(TextSnapshot.FromText(text), FoldingSyntax.Braces);

    private static IReadOnlyList<FoldingRange> Indent(string text) =>
        StructuralFolding.Compute(TextSnapshot.FromText(text), FoldingSyntax.Indent);

    private static IReadOnlyList<FoldingRange> Region(string text) =>
        StructuralFolding.Compute(TextSnapshot.FromText(text), FoldingSyntax.Region);

    [Fact]
    public void A_brace_pair_folds_from_the_opening_line_to_the_closing_line()
    {
        var ranges = Braces("class A\n{\n    void M()\n    {\n    }\n}\n");

        Assert.Contains(new FoldingRange(1, 5), ranges);
        Assert.Contains(new FoldingRange(3, 4), ranges);
    }

    [Fact]
    public void Braces_inside_strings_and_comments_are_ignored()
    {
        var text = """
            var a = "{ not a block";
            // { also not a block
            /* { neither
               } */
            var b = "escaped \" still a string {";
            if (true)
            {
            }
            """;

        var ranges = Braces(text);

        Assert.Equal([new FoldingRange(6, 7)], ranges);
    }

    [Fact]
    public void A_verbatim_string_keeps_its_braces_and_a_raw_string_keeps_three_quotes()
    {
        var text = string.Join("\n",
            "var path = @\"C:\\{temp}\\ \";",
            "var raw = \"\"\"",
            "    { not folded",
            "    \"\"\";",
            "void M()",
            "{",
            "}",
            "");

        var ranges = Braces(text);

        Assert.DoesNotContain(ranges, range => range.StartLine <= 3);
        Assert.Contains(new FoldingRange(5, 6), ranges);
    }

    [Fact]
    public void A_missing_closer_does_not_swallow_the_following_blocks()
    {
        var text = """
            void A()
            {
                void B() {
            }
            void C()
            {
            }
            """;

        var ranges = Braces(text);

        Assert.Contains(new FoldingRange(2, 3), ranges);
        Assert.Contains(new FoldingRange(5, 6), ranges);
        Assert.DoesNotContain(ranges, range => range.StartLine == 1);
    }

    [Fact]
    public void An_unclosed_block_extends_to_no_range_rather_than_to_the_document_end()
    {
        var ranges = Braces("void M()\n{\n    var x = 1;\n");

        Assert.Empty(ranges);
    }

    [Fact]
    public void Crlf_line_endings_count_as_one_break()
    {
        var ranges = Braces("a\r\n{\r\n}\r\nb\r\n");

        Assert.Equal([new FoldingRange(1, 2)], ranges);
    }

    [Fact]
    public void A_parenthesis_spanning_lines_folds_but_a_single_line_pair_does_not()
    {
        var ranges = Braces("Call(\n    1,\n    2);\nShort(3);\n");

        Assert.Equal([new FoldingRange(0, 2)], ranges);
    }

    [Fact]
    public void Regions_fold_in_every_dialect_and_nest()
    {
        var text = """
            // #region outer
            #region inner
            var a = 1;
            #endregion
            var b = 2;
            // #endregion
            """;

        var ranges = Region(text);

        Assert.Contains(new FoldingRange(0, 5, FoldingKind.Region), ranges);
        Assert.Contains(new FoldingRange(1, 3, FoldingKind.Region), ranges);
    }

    [Fact]
    public void An_unterminated_region_closes_at_the_last_content_line()
    {
        var ranges = Region("#region open\nvar a = 1;\n\n\n");

        Assert.Equal([new FoldingRange(0, 1, FoldingKind.Region)], ranges);
    }

    [Fact]
    public void Indentation_steps_nest_and_stop_at_the_last_content_line()
    {
        var text = """
            def outer():
                if x:
                    pass
                return 1
            def second():
                pass
            """;

        var ranges = Indent(text);

        Assert.Contains(new FoldingRange(0, 3), ranges);
        Assert.Contains(new FoldingRange(1, 2), ranges);
        Assert.Contains(new FoldingRange(4, 5), ranges);
    }

    [Fact]
    public void A_tab_counts_as_four_columns_for_indentation()
    {
        var ranges = Indent("a\n\tb\n\t\tc\n\td\ne\n");

        Assert.Contains(new FoldingRange(0, 3), ranges);
        Assert.Contains(new FoldingRange(1, 2), ranges);
    }

    [Fact]
    public void Blank_lines_never_close_a_block()
    {
        var ranges = Indent("def f():\n\n    x = 1\n\n    y = 2\nz = 3\n");

        Assert.Equal([new FoldingRange(0, 4)], ranges);
    }

    [Fact]
    public void One_range_per_start_line_keeps_the_longest()
    {
        var ranges = Braces("{ ( [\n] ) }\n");

        Assert.Single(ranges);
        Assert.Equal(new FoldingRange(0, 1), ranges[0]);
    }

    [Fact]
    public void A_document_above_the_line_cap_yields_nothing()
    {
        var text = string.Concat(Enumerable.Repeat("{\n", StructuralFolding.MaxLines + 1));

        Assert.Empty(Braces(text));
    }

    [Fact]
    public void The_range_cap_bounds_a_pathological_document()
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < StructuralFolding.MaxRanges + 200; i++)
        {
            builder.Append("{\n");
        }

        var ranges = Braces(builder.ToString());

        Assert.True(ranges.Count <= StructuralFolding.MaxRanges);
    }

    [Fact]
    public void Ranges_are_sorted_by_start_line()
    {
        var ranges = Braces("{\n}\n{\n}\n{\n}\n");

        Assert.Equal([0, 2, 4], ranges.Select(range => range.StartLine));
    }

    [Fact]
    public void An_empty_document_has_no_ranges()
    {
        Assert.Empty(StructuralFolding.Compute(TextSnapshot.Empty, FoldingSyntax.Braces));
        Assert.Empty(StructuralFolding.Compute(TextSnapshot.FromText("\n"), FoldingSyntax.Indent));
    }

    [Fact]
    public void A_document_without_a_break_scans_as_one_line()
    {
        Assert.Empty(Braces("{{}}"));
    }

    [Fact]
    public void A_document_beyond_one_window_still_folds_across_the_chunk_boundary()
    {
        var filler = new string('x', 40_000);
        var text = filler + "\n{\n}\n" + filler + "\n";

        var ranges = Braces(text);

        Assert.Equal([new FoldingRange(1, 2)], ranges);
    }
}
