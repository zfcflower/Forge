using Forge.App.ViewModels.Preview;
using Xunit;

namespace Forge.App.Tests.ViewModels.Preview;

public sealed class TablePreviewReaderTests
{
    private static TablePreviewParse Parse(string text, int maxRows = 100, char separator = ',') =>
        TablePreviewReader.Parse(new StringReader(text), maxRows, separator);

    private static void AssertRow(IReadOnlyList<string> row, params string[] expected) =>
        Assert.Equal(expected, row);

    [Fact]
    public void A_quoted_field_keeps_its_comma()
    {
        var parsed = Parse("\"a,b\",c");

        var row = Assert.Single(parsed.Rows);
        AssertRow(row, "a,b", "c");
    }

    [Fact]
    public void Doubled_quotes_are_one_literal_quote()
    {
        var parsed = Parse("\"a\"\"b\",c");

        var row = Assert.Single(parsed.Rows);
        AssertRow(row, "a\"b", "c");
    }

    [Fact]
    public void A_quoted_field_spanning_two_lines_is_one_field()
    {
        var parsed = Parse("\"a\nb\",c");

        var row = Assert.Single(parsed.Rows);
        AssertRow(row, "a\nb", "c");
    }

    [Fact]
    public void Crlf_ends_a_row()
    {
        var parsed = Parse("a,b\r\nc,d\r\n");

        Assert.Equal(2, parsed.TotalRows);
        AssertRow(parsed.Rows[0], "a", "b");
        AssertRow(parsed.Rows[1], "c", "d");
    }

    [Fact]
    public void A_ragged_row_is_padded_to_the_widest_row()
    {
        var parsed = Parse("a,b\nc\n");

        Assert.Equal(2, parsed.Rows.Count);
        AssertRow(parsed.Rows[0], "a", "b");
        AssertRow(parsed.Rows[1], "c", "");
    }

    [Fact]
    public void A_trailing_newline_adds_no_row_and_an_empty_line_is_one_empty_record()
    {
        var parsed = Parse("a\n\nb\n");

        Assert.Equal(3, parsed.TotalRows);
        AssertRow(parsed.Rows[0], "a");
        AssertRow(parsed.Rows[1], "");
        AssertRow(parsed.Rows[2], "b");
    }

    [Fact]
    public void Tsv_uses_tabs_and_treats_quotes_as_literal_characters()
    {
        var parsed = Parse("a\tb\n\"c\"\td\n", maxRows: 100, separator: '\t');

        Assert.Equal(2, parsed.Rows.Count);
        AssertRow(parsed.Rows[0], "a", "b");
        AssertRow(parsed.Rows[1], "\"c\"", "d");
    }

    [Fact]
    public void A_thousand_row_file_stops_at_the_row_limit_with_the_skip_count()
    {
        var text = string.Concat(Enumerable.Range(0, 1000).Select(static i => "r" + i + ",v\n"));

        var parsed = Parse(text, maxRows: 50);

        Assert.Equal(50, parsed.Rows.Count);
        Assert.Equal(950, parsed.SkippedRows);
        Assert.Equal(1000, parsed.TotalRows);
        AssertRow(parsed.Rows[49], "r49", "v");
    }

    [Fact]
    public void The_header_heuristic_finds_named_columns_over_numbers()
    {
        var parsed = Parse("name,age\nalice,30\nbob,40");

        Assert.True(TablePreviewReader.HasHeaderRow(parsed.Rows));
    }

    [Fact]
    public void All_number_rows_have_no_header()
    {
        var parsed = Parse("1,2\n3,4\n5,6");

        Assert.False(TablePreviewReader.HasHeaderRow(parsed.Rows));
    }

    [Fact]
    public void A_single_row_has_no_header()
    {
        var parsed = Parse("name,age");

        Assert.False(TablePreviewReader.HasHeaderRow(parsed.Rows));
    }

    [Fact]
    public void Wide_east_asian_characters_count_two_monospace_columns()
    {
        Assert.Equal(2, TablePreviewReader.DisplayWidth("ab"));
        Assert.Equal(2, TablePreviewReader.DisplayWidth("中"));
        Assert.Equal(3, TablePreviewReader.DisplayWidth("a中"));
        Assert.Equal(0, TablePreviewReader.DisplayWidth(""));
    }
}
