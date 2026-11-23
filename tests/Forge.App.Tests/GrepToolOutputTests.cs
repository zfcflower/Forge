using System.Text.Json;
using Forge.Abstractions;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// What a <c>grep</c> with no hit tells the caller: the scan count that separates "nothing matched" from "nothing was
/// searched", and the literal-versus-regex note that separates "absent" from "this pattern could never match". The hit path
/// stays exactly what it was, which the last case pins.
/// </summary>
public sealed class GrepToolOutputTests : IDisposable
{
    private readonly string workspace = Directory.CreateTempSubdirectory("forge-grep-output-").FullName;

    public void Dispose() => Directory.Delete(workspace, recursive: true);

    private async Task<ToolResult> RunAsync(params (string Name, object Value)[] arguments) =>
        await RunAsync(new ToolOptions { RipgrepPath = null }, arguments);

    private async Task<ToolResult> RunAsync(ToolOptions options, params (string Name, object Value)[] arguments)
    {
        var json = JsonSerializer.Serialize(arguments.ToDictionary(argument => argument.Name, argument => argument.Value));
        var tool = new GrepTool(options);
        return await tool.InvokeAsync(new ToolCall("call", tool.Definition.Name, json), new ToolContext("session", workspace), CancellationToken.None);
    }

    private void Make(string relative, string content = "x")
    {
        var path = Path.Combine(workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string[] Lines(ToolResult result) => result.Content.Split('\n');

    [Fact]
    public async Task A_query_without_a_hit_reports_how_many_files_it_scanned()
    {
        Make("a.txt", "alpha");
        Make("b.txt", "beta");
        Make("nested/c.txt", "gamma");

        var result = await RunAsync(("pattern", "needle"));

        Assert.False(result.IsError, result.Content);
        Assert.Equal(["no matches", "scanned 3 file(s)"], Lines(result));
    }

    [Fact]
    public async Task A_workspace_whose_every_file_is_ignored_says_that_nothing_was_scanned()
    {
        Make(".gitignore", "*\n");
        Make("a.txt", "alpha");

        var result = await RunAsync(("pattern", "alpha"));

        Assert.Equal(
            ["no matches", "scanned 0 files: the path is empty or excluded by .gitignore — verify with ls"],
            Lines(result));
    }

    [Fact]
    public async Task Hits_come_out_in_path_order_so_a_many_file_answer_reads_top_down()
    {
        Make("z/b.txt", "needle\n");
        Make("m/c.txt", "needle\n");
        Make("a.txt", "needle\n");

        var result = await RunAsync(("pattern", "needle"));

        Assert.Equal(["a.txt:1:needle", "m/c.txt:1:needle", "z/b.txt:1:needle"], result.Content.Split('\n'));
    }

    [Fact]
    public async Task An_empty_result_mentions_case_sensitivity_only_when_the_pattern_has_an_upper_case_letter()
    {
        Make("a.txt", "alpha\n");

        var cased = await RunAsync(("pattern", "Alpha"));
        Assert.Equal(
            ["no matches", "scanned 1 file(s)", "`case_sensitive` is true (pass `case_sensitive: false` to ignore case)"],
            cased.Content.Split('\n'));

        var lower = await RunAsync(("pattern", "beta"));
        Assert.Equal(["no matches", "scanned 1 file(s)"], lower.Content.Split('\n'));

        var insensitive = await RunAsync(("pattern", "Alpha"), ("case_sensitive", false));
        Assert.Equal(["a.txt:1:alpha"], insensitive.Content.Split('\n'));
    }

    [Fact]
    public async Task Regex_syntax_searched_literally_is_called_out_and_the_same_pattern_with_regex_finds_the_line()
    {
        Make("a.txt", "alpha");

        var literal = await RunAsync(("pattern", "alpha|beta"));
        Assert.Equal(
            [
                "no matches",
                "scanned 1 file(s)",
                "the pattern contains regular-expression syntax but `regex` was false, so it was searched literally; retry with `regex: true`",
            ],
            Lines(literal));

        var regex = await RunAsync(("pattern", "alpha|beta"), ("regex", true));
        Assert.Equal(["a.txt:1:alpha"], Lines(regex));
    }

    [Fact]
    public async Task A_literal_carrying_dots_and_parentheses_is_not_reported_as_a_regex()
    {
        Make("a.txt", "Foo.Bar(baz)\n");

        var result = await RunAsync(("pattern", "Foo.Bar(baz)"));

        Assert.Equal(["a.txt:1:Foo.Bar(baz)"], Lines(result));
    }

    [Fact]
    public async Task A_context_above_the_cap_is_capped_and_the_result_says_so()
    {
        var lines = Enumerable.Range(1, 24).Select(index => index == 13 ? "needle" : $"line{index}").ToArray();
        Make("a.txt", string.Join('\n', lines) + "\n");

        var result = await RunAsync(("pattern", "needle"), ("context", 10));

        Assert.False(result.IsError, result.Content);
        var lines2 = result.Content.Split('\n');
        // Five lines each side of line 13 — eleven lines in all, the hit included — plus the note about the clipped request.
        Assert.Equal(12, lines2.Length);
        Assert.Equal("a.txt-8-line8", lines2[0]);
        Assert.Equal("a.txt:13:needle", lines2[5]);
        Assert.Equal("a.txt-18-line18", lines2[10]);
        Assert.Equal("context 10 requested, capped at 5", lines2[11]);
    }

    [Fact]
    public async Task A_coverage_parameter_out_of_range_is_still_refused_with_the_value_that_arrived()
    {
        Make("a.txt", "needle\n");

        var result = await RunAsync(("pattern", "needle"), ("limit", 20000));

        Assert.True(result.IsError);
        Assert.Equal("grep: 'limit' is 20000; it must be between 1 and 10000.", result.Content);
    }

    [Fact]
    public async Task A_cut_result_says_how_to_see_the_rest()
    {
        Make("a.txt", "needle\nneedle\nneedle\n");

        var result = await RunAsync(("pattern", "needle"), ("limit", 1));

        Assert.Equal(
            ["a.txt:1:needle", "… (truncated: showing 1 of 3 matching lines; raise limit or narrow with glob)"],
            result.Content.Split('\n'));
    }

    [Fact]
    public async Task Skipped_files_are_counted_beside_the_scan_count()
    {
        Make("a.txt", "xxxxxx");
        Make("big.txt", new string('y', 20));
        File.WriteAllBytes(Path.Combine(workspace, "bin.dat"), [0x61, 0x62, 0x00, 0x63]);

        var result = await RunAsync(
            new ToolOptions { RipgrepPath = null, GrepMaxFileBytes = 8 },
            ("pattern", "needle"));

        Assert.Equal(
            [
                "no matches",
                "scanned 2 file(s)",
                "1 file(s) skipped: larger than 8 bytes",
                "1 file(s) skipped: binary",
            ],
            Lines(result));
    }
}
