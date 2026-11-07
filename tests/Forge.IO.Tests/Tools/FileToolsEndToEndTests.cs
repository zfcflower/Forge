using Forge.Abstractions;
using Forge.IO.Tools;
using Xunit;

namespace Forge.IO.Tests;

/// <summary>
/// One agent-sized flow through the real tool set: write a file, read it back with line numbers, replace one line with
/// <c>edit</c>, read the change, overwrite the file, see it through <c>ls</c> and <c>find</c>, and find text in it with
/// <c>grep</c>. Every step goes through <see cref="ITool.InvokeAsync"/> with the workspace root as the tool context, so
/// the path gate, the atomic write and the search walk are all exercised the way the agent loop calls them.
/// </summary>
public class FileToolsEndToEndTests
{
    [Fact]
    public async Task An_agent_can_write_read_edit_overwrite_list_find_and_grep_the_same_file()
    {
        using var workspace = new TemporaryWorkspace();
        var tools = new FileTools();

        var written = await workspace.InvokeSuccessAsync(
            tools.Write,
            TemporaryWorkspace.Json(new { path = "notes.txt", content = "alpha\nbeta\ngamma\n" }));
        Assert.Equal("wrote 17 bytes to notes.txt", written.Content);

        var read = await workspace.InvokeSuccessAsync(tools.Read, TemporaryWorkspace.Json(new { path = "notes.txt" }));
        Assert.Equal("notes.txt: 3 lines\n1→alpha\n2→beta\n3→gamma", Normalize(read.Content));

        var edited = await workspace.InvokeSuccessAsync(
            tools.Edit,
            TemporaryWorkspace.Json(new { path = "notes.txt", old_string = "beta", new_string = "BETA" }));
        Assert.Equal("replaced 1 occurrence in notes.txt (line 2)", edited.Content);

        var afterEdit = await workspace.InvokeSuccessAsync(tools.Read, TemporaryWorkspace.Json(new { path = "notes.txt" }));
        Assert.Equal("notes.txt: 3 lines\n1→alpha\n2→BETA\n3→gamma", Normalize(afterEdit.Content));

        var overwritten = await workspace.InvokeSuccessAsync(
            tools.Write,
            TemporaryWorkspace.Json(new { path = "notes.txt", content = "one\ntwo\n" }));
        Assert.Equal("wrote 8 bytes to notes.txt", overwritten.Content);
        Assert.Equal("notes.txt: 2 lines\n1→one\n2→two", Normalize(
            (await workspace.InvokeSuccessAsync(tools.Read, TemporaryWorkspace.Json(new { path = "notes.txt" }))).Content));

        var nested = await workspace.InvokeSuccessAsync(
            tools.Write,
            TemporaryWorkspace.Json(new { path = "sub/nested.txt", content = "two in nested\n" }));
        Assert.Equal("wrote 14 bytes to sub/nested.txt", nested.Content);

        var listed = await workspace.InvokeSuccessAsync(tools.List, TemporaryWorkspace.Json(new { depth = 2 }));
        Assert.Equal(["d sub/", "f notes.txt", "f sub/nested.txt"], listed.Content.Split('\n').Order(StringComparer.Ordinal));

        var found = await workspace.InvokeSuccessAsync(tools.Find, TemporaryWorkspace.Json(new { pattern = "*.txt" }));
        Assert.Equal(["notes.txt", "sub/nested.txt"], found.Content.Split('\n').Order(StringComparer.Ordinal));

        var grepped = await workspace.InvokeSuccessAsync(tools.Grep, TemporaryWorkspace.Json(new { pattern = "two" }));
        Assert.Equal(
            ["notes.txt:2:two", "sub/nested.txt:1:two in nested"],
            grepped.Content.Split('\n').Order(StringComparer.Ordinal));

        Assert.Empty(workspace.TemporaryFiles());
    }

    [Fact]
    public async Task An_edit_that_does_not_match_exactly_once_reports_the_count_and_leaves_the_file_alone()
    {
        using var workspace = new TemporaryWorkspace();
        var tools = new FileTools();
        var original = "same line\nother line\nsame line\n";
        var path = workspace.Write("twice.txt", original);

        var noMatch = await workspace.InvokeAsync(
            tools.Edit,
            TemporaryWorkspace.Json(new { path = "twice.txt", old_string = "missing", new_string = "x" }));
        var twice = await workspace.InvokeAsync(
            tools.Edit,
            TemporaryWorkspace.Json(new { path = "twice.txt", old_string = "same line", new_string = "x" }));

        Assert.True(noMatch.IsError);
        Assert.Contains("no match for old_string", noMatch.Content, StringComparison.Ordinal);
        Assert.True(twice.IsError);
        Assert.Contains("occurs 2 times", twice.Content, StringComparison.Ordinal);
        Assert.Contains("replace_all=true", twice.Content, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(path));

        var replaced = await workspace.InvokeSuccessAsync(
            tools.Edit,
            TemporaryWorkspace.Json(new { path = "twice.txt", old_string = "same line", new_string = "changed", replace_all = true }));
        Assert.Equal("replaced 2 occurrences in twice.txt (line 1)", replaced.Content);
        Assert.Equal("changed\nother line\nchanged\n", File.ReadAllText(path));
        Assert.Empty(workspace.TemporaryFiles());
    }

    [Fact]
    public async Task A_write_outside_the_workspace_is_refused_and_creates_nothing()
    {
        using var workspace = new TemporaryWorkspace();
        using var outside = new TestDirectory();
        var tools = new FileTools();
        var target = Path.Combine(outside.Path, "escaped.txt");

        var result = await workspace.InvokeAsync(
            tools.Write,
            TemporaryWorkspace.Json(new { path = target, content = "escaped\n" }));

        Assert.True(result.IsError);
        Assert.Contains("outside the workspace", result.Content, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
    }

    private static string Normalize(string content) => content.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public async Task Every_tool_works_when_the_workspace_root_itself_is_a_link()
    {
        // The workspace root can be a junction or a symbolic link (macOS '/var' is one, and a worktree under a linked
        // repository inherits it). Resolve answers with the real path, so a root kept in its link spelling would make the
        // read/write/edit tools fail while ls/find/grep kept working; this pins that all seven agree.
        using var temp = new TestDirectory();
        var real = Path.Combine(temp.Path, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(temp.Path, "linked");
        if (!DirectoryLinks.TryCreate(link, real))
        {
            return;
        }

        var tools = new FileTools();
        var context = new ToolContext("test-session", link);

        var written = await InvokeAsync(tools.Write, """{"path":"notes.txt","content":"alpha\nbeta\n"}""");
        var read = await InvokeAsync(tools.Read, """{"path":"notes.txt"}""");
        var edited = await InvokeAsync(
            tools.Edit,
            """{"path":"notes.txt","old_string":"beta","new_string":"BETA"}""");
        var afterEdit = await InvokeAsync(tools.Read, """{"path":"notes.txt"}""");
        var listed = await InvokeAsync(tools.List, "{}");
        var found = await InvokeAsync(tools.Find, """{"pattern":"*.txt"}""");
        var grepped = await InvokeAsync(tools.Grep, """{"pattern":"BETA"}""");

        Assert.False(written.IsError, written.Content);
        Assert.False(read.IsError, read.Content);
        Assert.False(edited.IsError, edited.Content);
        Assert.False(listed.IsError, listed.Content);
        Assert.False(found.IsError, found.Content);
        Assert.False(grepped.IsError, grepped.Content);
        Assert.Equal("notes.txt: 2 lines\n1→alpha\n2→BETA", Normalize(afterEdit.Content));
        Assert.Contains("f notes.txt", listed.Content, StringComparison.Ordinal);
        Assert.Equal("notes.txt", found.Content);
        Assert.Equal("notes.txt:2:BETA", grepped.Content);

        async Task<ToolResult> InvokeAsync(ITool tool, string arguments) =>
            await tool.InvokeAsync(new ToolCall("call-1", tool.Definition.Name, arguments), context, CancellationToken.None);
    }
}
