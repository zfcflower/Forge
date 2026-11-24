using System.Text.Json;
using Forge.Abstractions;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// What <c>edit</c> says when an exact match misses: the line the payload nearly matched and the character it diverges at,
/// whether a payload line matches on its own (so the context is what is wrong), and whether the payload spells a line
/// break itself. A bare "no match" cost a production run ten calls and four turns before the cause was found.
/// </summary>
public sealed class EditDiagnosticsTests : IDisposable
{
    private readonly string workspace = Directory.CreateTempSubdirectory("forge-edit-diagnostics-").FullName;

    public void Dispose() => Directory.Delete(workspace, recursive: true);

    private void Make(string content) => File.WriteAllText(Path.Combine(workspace, "a.txt"), content);

    private async Task<ToolResult> EditAsync(string oldString, string newString = "x")
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["path"] = "a.txt",
            ["old_string"] = oldString,
            ["new_string"] = newString,
        });
        return await new EditTool().InvokeAsync(
            new ToolCall("call", "edit", json),
            new ToolContext("session", workspace),
            CancellationToken.None);
    }

    [Fact]
    public async Task A_line_that_matches_on_its_own_blames_the_surrounding_lines()
    {
        Make("alpha\nbeta\ngamma\n");

        var result = await EditAsync("beta\ndelta");

        Assert.True(result.IsError);
        Assert.Equal(
            "edit: no match for old_string in 'a.txt' (3 lines); nothing changed.\n"
            + "line 2 matches an old_string line on its own; the surrounding lines are what differ.",
            result.Content);
    }

    [Fact]
    public async Task A_typo_is_reported_with_its_line_and_the_first_differing_character()
    {
        Make("alpha\nbeta line\ngamma\n");

        var result = await EditAsync("beta lime");

        Assert.Equal(
            "edit: no match for old_string in 'a.txt' (3 lines); nothing changed.\n"
            + "closest line 2 differs at character 8: \"beta lime\" vs \"beta line\".",
            result.Content);
    }

    [Fact]
    public async Task A_payload_line_the_file_continues_is_reported_with_the_tail()
    {
        Make("alpha\nbeta line\ngamma\n");

        var result = await EditAsync("beta\ndelta");

        Assert.Equal(
            "edit: no match for old_string in 'a.txt' (3 lines); nothing changed.\n"
            + "closest line 2: your line ends at character 4; the file continues \" line\".",
            result.Content);
    }

    [Fact]
    public async Task A_payload_spelling_its_own_line_break_is_named()
    {
        Make("one\ntwo\n");

        var result = await EditAsync(@"one\r\ntwo");

        Assert.Equal(
            "edit: no match for old_string in 'a.txt' (2 lines); nothing changed.\n"
            + @"old_string contains '\' followed by 'r' or 'n': those are two literal characters, not a line break; matching runs on LF text, so no \r is needed.",
            result.Content);
    }

    [Fact]
    public async Task A_payload_with_nothing_in_common_keeps_the_bare_message()
    {
        Make("alpha\nbeta\ngamma\n");

        var result = await EditAsync("zulu zulu");

        Assert.Equal("edit: no match for old_string in 'a.txt' (3 lines); nothing changed.", result.Content);
    }
}
