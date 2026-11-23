using System.Text.Json;
using Forge.Abstractions;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// What a <c>find</c> with no hit, or a cut one, tells the caller: how many entries the walk actually offered separates "your
/// glob matched nothing" from "nothing was walked at all", and a truncated listing says how to see the rest.
/// </summary>
public sealed class FindToolOutputTests : IDisposable
{
    private readonly string workspace = Directory.CreateTempSubdirectory("forge-find-output-").FullName;

    public void Dispose() => Directory.Delete(workspace, recursive: true);

    private void Make(string relative, string content = "x")
    {
        var path = Path.Combine(workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<string> FindAsync(params (string Name, object Value)[] arguments)
    {
        var json = JsonSerializer.Serialize(arguments.ToDictionary(argument => argument.Name, argument => argument.Value));
        var result = await new FindTool().InvokeAsync(
            new ToolCall("call", "find", json),
            new ToolContext("session", workspace),
            CancellationToken.None);
        Assert.False(result.IsError, result.Content);
        return result.Content;
    }

    [Fact]
    public async Task A_workspace_whose_every_entry_is_ignored_says_nothing_was_walked()
    {
        Make(".gitignore", "*\n");
        Make("a.md");

        Assert.Equal(
            "no entries: the directory is empty or excluded by .gitignore — verify with ls",
            await FindAsync(("pattern", "*.md")));
    }

    [Fact]
    public async Task A_glob_that_matches_nothing_reports_how_many_entries_were_walked()
    {
        Make(".gitignore", "secret/\n");
        Make("a.txt");
        Make("secret/b.md");

        Assert.Equal("no entry matched '*.md' (walked 2 entries)", await FindAsync(("pattern", "*.md")));
    }

    [Fact]
    public async Task A_truncated_listing_says_how_to_see_the_rest()
    {
        Make("a.md");
        Make("b.md");
        Make("c.md");

        Assert.Equal(
            "a.md\nb.md\n… (truncated: showing 2 of 3 entries; raise limit or narrow pattern)",
            await FindAsync(("pattern", "*.md"), ("limit", 2)));
    }
}
