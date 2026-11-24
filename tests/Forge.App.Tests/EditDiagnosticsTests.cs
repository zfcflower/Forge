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

    private string Make(string content)
    {
        var path = Path.Combine(workspace, "a.txt");
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<ToolResult> EditAsync(string oldString, string newString = "x", IFileVersionLedger? ledger = null)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["path"] = "a.txt",
            ["old_string"] = oldString,
            ["new_string"] = newString,
        });
        return await new EditTool().InvokeAsync(
            new ToolCall("call", "edit", json),
            new ToolContext("session", workspace) { FileVersions = ledger },
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

    [Fact]
    public async Task A_file_somebody_else_wrote_after_the_read_says_so()
    {
        // The shape that cost the most: the payload was right when the agent read the file, somebody else rewrote it, and
        // the bare "no match" reads as "your text is wrong". The ledger already knows a read happened — it is the one thing
        // that separates "typo" from "stale text", so it is said out loud.
        Make("alpha\nbeta\ngamma\n");
        var ledger = new Ledger();
        ledger.Record(Path.Combine(workspace, "a.txt"), new FileVersion(1, 1));

        var result = await EditAsync("zulu zulu", ledger: ledger);

        Assert.Equal(
            "edit: no match for old_string in 'a.txt' (3 lines); nothing changed.\n"
            + "the file changed after your read (1 → 17 bytes); read it again and edit against what is there now.",
            result.Content);
    }

    [Fact]
    public async Task A_file_that_still_matches_the_read_keeps_the_bare_message()
    {
        // The control: a payload that really is wrong must not be excused by a stale-read note.
        var path = Make("alpha\nbeta\ngamma\n");
        var info = new FileInfo(path);
        var ledger = new Ledger();
        ledger.Record(path, new FileVersion(info.Length, info.LastWriteTimeUtc.Ticks));

        var result = await EditAsync("zulu zulu", ledger: ledger);

        Assert.Equal("edit: no match for old_string in 'a.txt' (3 lines); nothing changed.", result.Content);
    }

    /// <summary>The one thing <c>edit</c> asks of a ledger: what this agent last saw of the path.</summary>
    private sealed class Ledger : IFileVersionLedger
    {
        private readonly Dictionary<string, FileVersion> versions = new(StringComparer.Ordinal);

        public bool TryGet(string path, out FileVersion version) => versions.TryGetValue(path, out version);

        public void Record(string path, FileVersion version) => versions[path] = version;
    }
}
