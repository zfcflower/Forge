using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The line-ending contract of the file tools: <c>read</c> hands the model LF text, so an <c>old_string</c> copied out of
/// that view has to match a CRLF file on disk, and every write lands as LF on every platform.
/// </summary>
public sealed class LineEndingToolsTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string workspace = Directory.CreateTempSubdirectory("forge-line-endings-").FullName;

    public void Dispose() => Directory.Delete(workspace, recursive: true);

    /// <summary>Creates the file byte for byte, so an intended <c>\r\n</c> survives the setup.</summary>
    private string Make(string relative, string content)
    {
        var path = Path.Combine(workspace, relative);
        File.WriteAllBytes(path, Utf8.GetBytes(content));
        return path;
    }

    private ToolContext Context() => new("session", workspace);

    private static async Task<ToolResult> InvokeAsync(
        ITool tool,
        ToolContext context,
        params (string Name, object Value)[] arguments)
    {
        var json = JsonSerializer.Serialize(arguments.ToDictionary(argument => argument.Name, argument => argument.Value));
        return await tool.InvokeAsync(
            new ToolCall("call", tool.Definition.Name, json),
            context,
            CancellationToken.None);
    }

    [Fact]
    public async Task Edit_matches_a_crlf_file_through_the_lf_view_and_writes_the_file_back_as_lf()
    {
        var path = Make("crlf.cs", "alpha\r\nbeta\r\ngamma\r\n");

        var result = await InvokeAsync(
            new EditTool(),
            Context(),
            ("path", "crlf.cs"),
            ("old_string", "alpha\nbeta"),
            ("new_string", "ALPHA\nBETA"));

        Assert.False(result.IsError, result.Content);
        Assert.Contains("replaced 1 occurrence", result.Content);
        Assert.Equal("ALPHA\nBETA\ngamma\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task Edit_treats_a_bare_carriage_return_as_a_line_break()
    {
        var path = Make("classic-mac.txt", "one\rtwo\rthree\r");

        var result = await InvokeAsync(
            new EditTool(),
            Context(),
            ("path", "classic-mac.txt"),
            ("old_string", "one\ntwo"),
            ("new_string", "ONE\nTWO"));

        Assert.False(result.IsError, result.Content);
        Assert.Equal("ONE\nTWO\nthree\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task Edit_still_refuses_text_that_really_differs_in_a_crlf_file()
    {
        const string original = "alpha\r\nbeta\r\ngamma\r\n";
        var path = Make("crlf.cs", original);

        var result = await InvokeAsync(
            new EditTool(),
            Context(),
            ("path", "crlf.cs"),
            ("old_string", "alpha\nBETA"),
            ("new_string", "x"));

        Assert.True(result.IsError);
        Assert.Contains("no match for old_string", result.Content);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public async Task Edit_counts_every_occurrence_and_names_the_first_two_lines_of_a_crlf_file()
    {
        var path = Make("repeat.txt", "a\r\nb\r\nc\r\na\r\nb\r\n");

        var result = await InvokeAsync(
            new EditTool(),
            Context(),
            ("path", "repeat.txt"),
            ("old_string", "a\nb"),
            ("new_string", "x"));

        Assert.True(result.IsError);
        Assert.Contains("occurs 2 times", result.Content);
        Assert.Contains("(first at lines 1, 4)", result.Content);
        Assert.Equal("a\r\nb\r\nc\r\na\r\nb\r\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task Edit_refuses_an_old_string_that_is_only_line_breaks_and_carries_a_carriage_return()
    {
        const string original = "alpha\r\nbeta\r\n";
        var path = Make("crlf.cs", original);

        var result = await InvokeAsync(
            new EditTool(),
            Context(),
            ("path", "crlf.cs"),
            ("old_string", "\r"),
            ("new_string", string.Empty),
            ("replace_all", true));

        Assert.True(result.IsError);
        Assert.Contains("only line breaks", result.Content);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public async Task Write_lands_crlf_content_as_lf()
    {
        var path = Path.Combine(workspace, "written.txt");

        var result = await InvokeAsync(
            new WriteTool(),
            Context(),
            ("path", "written.txt"),
            ("content", "one\r\ntwo\r\n"));

        Assert.False(result.IsError, result.Content);
        Assert.Equal("one\ntwo\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task Append_lands_its_text_as_lf_without_touching_the_existing_bytes()
    {
        const string original = "kept\r\n";
        var path = Make("log.txt", original);

        var result = await InvokeAsync(
            new WriteTool(),
            Context(),
            ("path", "log.txt"),
            ("content", "added\r\n"),
            ("append", true));

        Assert.False(result.IsError, result.Content);
        Assert.Equal($"{original}added\n", File.ReadAllText(path));
    }
}
