using System.Text.Json;
using Forge.Abstractions;
using Forge.IO;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The first line of a <c>read</c>: the file's own line count only when the whole file came back, the range this answer covers
/// otherwise. A partial read that reports its own size as the file's is how a model ends up believing a 1 461-line file has 72.
/// </summary>
public sealed class ReadToolOutputTests : IDisposable
{
    private readonly string workspace = Directory.CreateTempSubdirectory("forge-read-output-").FullName;

    public void Dispose() => Directory.Delete(workspace, recursive: true);

    private async Task<ToolResult> InvokeAsync(params (string Name, object Value)[] arguments)
    {
        var json = JsonSerializer.Serialize(arguments.ToDictionary(argument => argument.Name, argument => argument.Value));
        return await new ReadTool().InvokeAsync(
            new ToolCall("call", "read", json),
            new ToolContext("session", workspace),
            CancellationToken.None);
    }

    private async Task<string> ReadAsync(params (string Name, object Value)[] arguments)
    {
        var json = JsonSerializer.Serialize(arguments.ToDictionary(argument => argument.Name, argument => argument.Value));
        var result = await new ReadTool().InvokeAsync(
            new ToolCall("call", "read", json),
            new ToolContext("session", workspace),
            CancellationToken.None);
        Assert.False(result.IsError, result.Content);
        return result.Content;
    }

    private void Make(string relative, string content)
    {
        var path = Path.Combine(workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task A_whole_file_reports_its_line_count()
    {
        Make("a.txt", "one\ntwo\nthree\n");

        Assert.Equal("a.txt: 3 lines\n1→one\n2→two\n3→three", await ReadAsync(("path", "a.txt")));
    }

    [Fact]
    public async Task A_window_reports_the_range_it_covers_and_where_to_continue()
    {
        Make("a.txt", "l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\n");

        Assert.Equal(
            "a.txt: lines 4–6\n4→l4\n5→l5\n6→l6\n… (truncated) Shown lines 4–6. Continue with offset=7.",
            await ReadAsync(("path", "a.txt"), ("offset", 4), ("limit", 3)));
    }

    [Fact]
    public async Task An_offset_past_the_end_says_so_instead_of_reporting_an_empty_file()
    {
        Make("a.txt", "one\ntwo\nthree\n");

        Assert.Equal("a.txt: the file ends before line 99", await ReadAsync(("path", "a.txt"), ("offset", 99)));
    }

    [Fact]
    public async Task A_byte_limited_window_also_reports_a_range()
    {
        Make("a.txt", string.Join('\n', Enumerable.Range(1, 40).Select(index => $"line {index} with some padding")) + "\n");

        var content = await ReadAsync(("path", "a.txt"), ("max_bytes", 120));

        var header = content.Split('\n')[0];
        Assert.StartsWith("a.txt: lines 1–", header, StringComparison.Ordinal);
        Assert.Contains("Continue with offset=", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Several_files_in_one_call_keep_their_own_headers()
    {
        Make("a.txt", "one\ntwo\nthree\n");
        Make("b.txt", "alpha\nbeta\n");

        Assert.Equal(
            "a.txt: 3 lines\n1→one\n2→two\n3→three\nb.txt: 2 lines\n1→alpha\n2→beta",
            await ReadAsync(("paths", new[] { "a.txt", "b.txt" })));
    }

    [Fact]
    public async Task A_pattern_returns_only_matching_lines_with_their_context()
    {
        Make("a.txt", "l1\nl2\nl3\nl4\nl5\n");

        Assert.Equal(
            "a.txt: 1 matching line\n2→l2\n3→l3\n4→l4",
            await ReadAsync(("path", "a.txt"), ("pattern", "l3"), ("context", 1)));
    }

    [Fact]
    public async Task Two_matches_far_apart_are_shown_as_two_excerpts()
    {
        Make("a.txt", "x\nl2\nl3\nl4\nx\n");

        Assert.Equal(
            "a.txt: 2 matching lines\n1→x\n…\n5→x",
            await ReadAsync(("path", "a.txt"), ("pattern", "x")));
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_reported_in_place_and_the_rest_still_comes_back()
    {
        Make("a.txt", "one\ntwo\n");

        Assert.Equal(
            "missing.txt: does not exist\na.txt: 2 lines\n1→one\n2→two",
            await ReadAsync(("paths", new[] { "missing.txt", "a.txt" })));
    }

    [Fact]
    public async Task A_call_whose_files_all_fail_is_an_error()
    {
        var result = await InvokeAsync(("paths", new[] { "one.txt", "two.txt" }));

        Assert.True(result.IsError);
        Assert.Equal("one.txt: does not exist\ntwo.txt: does not exist", result.Content);
    }

    [Fact]
    public async Task The_budget_cuts_the_answer_and_names_the_files_it_did_not_return()
    {
        Make("a.txt", string.Join('\n', Enumerable.Range(1, 40).Select(index => $"line {index} {new string('x', 60)}")) + "\n");
        Make("b.txt", "beta\n");

        var content = await ReadAsync(("paths", new[] { "a.txt", "b.txt" }), ("max_chars", 1000));

        Assert.StartsWith("a.txt: 40 lines\n1→line 1 ", content, StringComparison.Ordinal);
        Assert.Contains("… (cut at line ", content, StringComparison.Ordinal);
        Assert.EndsWith("1 more file not returned: b.txt; raise max_chars, read them in a smaller group, or narrow with pattern)", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Path_and_paths_together_are_refused()
    {
        Make("a.txt", "one\n");

        var result = await InvokeAsync(("path", "a.txt"), ("paths", new[] { "a.txt" }));

        Assert.True(result.IsError);
        Assert.Equal("read: pass either 'path' or 'paths', not both.", result.Content);
    }

    [Fact]
    public async Task An_absolute_path_outside_the_workspace_is_read_and_reported_as_it_is()
    {
        var outside = Directory.CreateTempSubdirectory("forge-read-outside-").FullName;
        try
        {
            var path = Path.Combine(outside, "SKILL.md");
            File.WriteAllText(path, "outside\n");

            Assert.Equal(
                $"{WorkspacePath.ResolveRealPath(Path.GetFullPath(path))}: 1 lines\n1→outside",
                await ReadAsync(("path", path)));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task A_relative_path_that_climbs_out_of_the_workspace_is_read_too()
    {
        var outside = Directory.CreateTempSubdirectory("forge-read-climb-").FullName;
        try
        {
            const string name = "climbed.txt";
            File.WriteAllText(Path.Combine(outside, name), "climbed\n");

            var content = await ReadAsync(("path", Path.Combine("..", Path.GetFileName(outside), name)));

            Assert.EndsWith(": 1 lines\n1→climbed", content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
