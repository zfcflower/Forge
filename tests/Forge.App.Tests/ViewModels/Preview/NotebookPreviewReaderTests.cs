using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels.Preview;
using Xunit;

namespace Forge.App.Tests.ViewModels.Preview;

public sealed class NotebookPreviewReaderTests
{
    private const int Limit = 100;

    private static NotebookParseResult Parse(string json, int limit = Limit) =>
        NotebookPreviewReader.Parse(json, limit);

    [Fact]
    public void A_code_cell_with_a_stream_output_parses()
    {
        var parsed = Parse("""
            {
              "cells": [
                {
                  "cell_type": "code",
                  "execution_count": 1,
                  "source": "print(1)",
                  "outputs": [ { "output_type": "stream", "name": "stdout", "text": "1\n" } ]
                }
              ]
            }
            """);

        var cell = Assert.Single(parsed.Cells);
        Assert.Equal(NotebookCellKind.Code, cell.Kind);
        Assert.Equal("print(1)", cell.Source);
        var output = Assert.IsType<NotebookTextOutputData>(Assert.Single(cell.Outputs));
        Assert.Equal(NotebookOutputKind.Stream, output.Kind);
        Assert.Equal("1\n", output.Text);
    }

    [Fact]
    public void An_execute_result_text_output_is_truncated_to_the_configured_limit()
    {
        using var _ = new LanguageScope(UiLanguage.English);
        var text = new string('x', 150);
        var json = "{\"cells\":[{\"cell_type\":\"code\",\"source\":\"\",\"outputs\":[{\"output_type\":\"execute_result\",\"data\":{\"text/plain\":[\"" + text + "\"]}}]}]}";
        var parsed = Parse(json, limit: 20);

        var cell = Assert.Single(parsed.Cells);
        var output = Assert.IsType<NotebookTextOutputData>(Assert.Single(cell.Outputs));
        Assert.Equal(NotebookOutputKind.Text, output.Kind);
        Assert.StartsWith(new string('x', 20), output.Text, StringComparison.Ordinal);
        Assert.EndsWith("... (truncated)", output.Text, StringComparison.Ordinal);
        Assert.Equal(20 + "... (truncated)".Length, output.Text.Length);
    }

    [Fact]
    public void An_error_output_carries_name_value_and_traceback()
    {
        var parsed = Parse("""
            {
              "cells": [
                {
                  "cell_type": "code",
                  "source": "1/0",
                  "outputs": [
                    {
                      "output_type": "error",
                      "ename": "ZeroDivisionError",
                      "evalue": "division by zero",
                      "traceback": ["line1", "\n", "line2"]
                    }
                  ]
                }
              ]
            }
            """);

        var cell = Assert.Single(parsed.Cells);
        var output = Assert.IsType<NotebookErrorOutputData>(Assert.Single(cell.Outputs));
        Assert.Equal("ZeroDivisionError", output.Name);
        Assert.Equal("division by zero", output.Value);
        Assert.Equal("line1\nline2", output.Traceback);
    }

    [Fact]
    public void A_markdown_cell_parses_with_its_source_array_joined()
    {
        var parsed = Parse("""
            {"cells":[{"cell_type":"markdown","source":["# Ti","tle\n"],"outputs":[]}]}
            """);

        var cell = Assert.Single(parsed.Cells);
        Assert.Equal(NotebookCellKind.Markdown, cell.Kind);
        Assert.Equal("# Title\n", cell.Source);
        Assert.Empty(cell.Outputs);
    }

    [Fact]
    public void An_image_png_output_carries_the_decoded_bytes()
    {
        const string base64 = "AQID";
        var json = "{\"cells\":[{\"cell_type\":\"code\",\"source\":\"\",\"outputs\":[{\"output_type\":\"display_data\",\"data\":{\"image/png\":\"" + base64 + "\"}}]}]}";
        var parsed = Parse(json);

        var cell = Assert.Single(parsed.Cells);
        var output = Assert.IsType<NotebookImageOutputData>(Assert.Single(cell.Outputs));
        Assert.Equal([1, 2, 3], output.Bytes);
    }

    [Fact]
    public void Malformed_json_is_a_problem_message_never_an_exception()
    {
        var parsed = Parse("{not json", limit: 100);

        Assert.True(parsed.Failed);
        Assert.False(string.IsNullOrEmpty(parsed.Problem));
        Assert.Empty(parsed.Cells);
    }

    [Fact]
    public void A_json_document_without_a_cells_array_is_a_problem_message()
    {
        var parsed = Parse("{\"metadata\": {}}", limit: 100);

        Assert.True(parsed.Failed);
        Assert.Empty(parsed.Cells);
    }

    [Fact]
    public void Unknown_cell_and_output_types_are_skipped_not_errors()
    {
        var parsed = Parse("""
            {"cells":[
              {"cell_type":"widget","source":"ignored"},
              {"cell_type":"code","source":"ok","outputs":[{"output_type":"display_data","data":{"application/vnd.x":1}}]}
            ]}
            """);

        var cell = Assert.Single(parsed.Cells);
        Assert.Equal(NotebookCellKind.Code, cell.Kind);
        Assert.Empty(cell.Outputs);
    }

    [Fact]
    public void A_corrupted_png_payload_is_skipped_without_failing_the_notebook()
    {
        var parsed = Parse("""{"cells":[{"cell_type":"code","source":"","outputs":[{"output_type":"display_data","data":{"image/png":"%%%not base64"}}]}]}""");

        var cell = Assert.Single(parsed.Cells);
        Assert.Empty(cell.Outputs);
    }

    [Fact]
    public void An_image_output_over_the_size_cap_is_skipped_without_decoding_it()
    {
        var base64 = Convert.ToBase64String(new byte[NotebookPreviewReader.MaxImageBytes + 10]);
        var json = "{\"cells\":[{\"cell_type\":\"code\",\"source\":\"\",\"outputs\":[{\"output_type\":\"display_data\",\"data\":{\"image/png\":\"" + base64 + "\"}}]}]}";

        var parsed = Parse(json);

        var cell = Assert.Single(parsed.Cells);
        Assert.Empty(cell.Outputs);
    }

    [Fact]
    public void Truncation_never_cuts_between_the_halves_of_a_surrogate_pair()
    {
        using var _ = new LanguageScope(UiLanguage.English);
        var text = new string('x', 9) + "\U0001F600" + "more";
        var json = "{\"cells\":[{\"cell_type\":\"code\",\"source\":\"\",\"outputs\":[{\"output_type\":\"stream\",\"text\":\"" + text + "\"}]}]}";

        var parsed = Parse(json, limit: 10);

        var cell = Assert.Single(parsed.Cells);
        var output = Assert.IsType<NotebookTextOutputData>(Assert.Single(cell.Outputs));
        Assert.Equal(new string('x', 9) + "... (truncated)", output.Text);
        Assert.DoesNotContain(output.Text, static character => char.IsHighSurrogate(character) || char.IsLowSurrogate(character));
    }
}
