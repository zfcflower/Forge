using System.Text.Json;
using Forge.Abstractions;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// One rule for all three walking tools: the ignore rules and the build-output directory list apply to everything below
/// the path a caller named, never to that path itself — so a search of <c>src</c> prunes <c>src/**/obj</c> while naming
/// <c>obj</c> still searches it. <c>grep</c>, <c>find</c> and <c>ls</c> are pinned against each other here because the same
/// directory must not answer differently per tool.
/// </summary>
public sealed class GrepIgnoreScopeTests : IDisposable
{
    private readonly string workspace = Directory.CreateTempSubdirectory("forge-grep-scope-").FullName;

    public void Dispose() => Directory.Delete(workspace, recursive: true);

    private void Make(string relative, string content = "x")
    {
        var path = Path.Combine(workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<ToolResult> RunAsync(ITool tool, params (string Name, object Value)[] arguments)
    {
        var json = JsonSerializer.Serialize(arguments.ToDictionary(argument => argument.Name, argument => argument.Value));
        return await tool.InvokeAsync(new ToolCall("call", tool.Definition.Name, json), new ToolContext("session", workspace), CancellationToken.None);
    }

    private void MakeIgnoredSecret()
    {
        Make(".gitignore", "secret/\n");
        Make("secret/needle.txt", "needle\n");
    }

    [Fact]
    public async Task A_root_search_is_pruned_and_the_same_directory_named_in_full_is_searched()
    {
        MakeIgnoredSecret();
        var options = new ToolOptions { RipgrepPath = null };

        var root = await RunAsync(new GrepTool(options), ("pattern", "needle"));
        Assert.Equal(["no matches", "scanned 1 file(s)"], root.Content.Split('\n'));

        var namedWithDot = await RunAsync(new GrepTool(options), ("pattern", "needle"), ("path", "."));
        Assert.Equal(root.Content, namedWithDot.Content);

        var named = await RunAsync(new GrepTool(options), ("pattern", "needle"), ("path", "secret"));
        Assert.False(named.IsError, named.Content);
        Assert.Equal(["secret/needle.txt:1:needle"], named.Content.Split('\n'));
    }

    [Fact]
    public async Task Find_list_and_grep_report_the_same_entries_for_an_explicitly_named_directory()
    {
        MakeIgnoredSecret();

        var found = await RunAsync(new FindTool(), ("pattern", "*"), ("path", "secret"));
        Assert.Equal(["secret/needle.txt"], found.Content.Split('\n'));

        var listed = await RunAsync(new ListTool(), ("path", "secret"));
        Assert.Equal(["f secret/needle.txt"], listed.Content.Split('\n'));

        var grepped = await RunAsync(new GrepTool(new ToolOptions { RipgrepPath = null }), ("pattern", "needle"), ("path", "secret"));
        Assert.Equal(["secret/needle.txt:1:needle"], grepped.Content.Split('\n'));
    }

    [Fact]
    public async Task A_search_below_a_named_directory_prunes_build_output_and_naming_it_searches_it()
    {
        Make("src/keep.cs", "x\n");
        Make("src/obj/gen.cs", "needle\n");
        var options = new ToolOptions { RipgrepPath = null };

        var below = await RunAsync(new GrepTool(options), ("pattern", "needle"), ("path", "src"));
        Assert.Equal(["no matches", "scanned 1 file(s)"], below.Content.Split('\n'));

        var named = await RunAsync(new GrepTool(options), ("pattern", "needle"), ("path", "src/obj"));
        Assert.Equal(["src/obj/gen.cs:1:needle"], named.Content.Split('\n'));

        var found = await RunAsync(new FindTool(), ("pattern", "*.cs"), ("path", "src"));
        Assert.Equal(["src/keep.cs"], found.Content.Split('\n'));
    }

    [Fact]
    public async Task A_directory_the_preference_names_is_pruned_at_any_depth_and_an_empty_list_keeps_everything()
    {
        Make("src/keep.cs", "x\n");
        Make("src/generated/gen.cs", "needle\n");

        var pruned = await RunAsync(
            new GrepTool(new ToolOptions { RipgrepPath = null, IgnoredDirectories = ["generated"] }),
            ("pattern", "needle"),
            ("path", "src"));
        Assert.Equal(["no matches", "scanned 1 file(s)"], pruned.Content.Split('\n'));

        var everything = await RunAsync(
            new GrepTool(new ToolOptions { RipgrepPath = null, IgnoredDirectories = [] }),
            ("pattern", "needle"),
            ("path", "src"));
        Assert.Equal(["src/generated/gen.cs:1:needle"], everything.Content.Split('\n'));
    }

    [Fact]
    public async Task Ignore_rules_reach_below_a_named_directory_but_not_the_path_named_in_full()
    {
        Make(".gitignore", "src/secret/\n");
        Make("src/keep.cs", "x\n");
        Make("src/secret/needle.txt", "needle\n");

        var below = await RunAsync(new GrepTool(new ToolOptions { RipgrepPath = null }), ("pattern", "needle"), ("path", "src"));
        Assert.Equal(["no matches", "scanned 1 file(s)"], below.Content.Split('\n'));

        var named = await RunAsync(
            new GrepTool(new ToolOptions { RipgrepPath = null }),
            ("pattern", "needle"),
            ("path", "src/secret"));
        Assert.Equal(["src/secret/needle.txt:1:needle"], named.Content.Split('\n'));
    }

    [Fact]
    public async Task An_ignored_directory_is_still_skipped_below_the_root_by_every_walking_tool()
    {
        MakeIgnoredSecret();

        Assert.Equal(["no entry matched '*.txt' (walked 1 entries)"], (await RunAsync(new FindTool(), ("pattern", "*.txt"))).Content.Split('\n'));
        var listed = await RunAsync(new ListTool());
        Assert.DoesNotContain(listed.Content.Split('\n'), line => line.StartsWith("f secret/", StringComparison.Ordinal));
    }
}
