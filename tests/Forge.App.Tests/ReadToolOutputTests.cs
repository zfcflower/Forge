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
