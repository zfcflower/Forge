using System.Text.Json;
using Forge.Abstractions;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The <c>path</c> argument the three walking tools share: absent and blank both mean the workspace root, which is what
/// their schemas promise ("default the root") and what <c>bash</c> already does for <c>workdir</c>. A model that fills an
/// unused optional string with <c>""</c> gets the root answer instead of the argument guard's message, one call site per
/// tool pinned because each reads the argument itself.
/// </summary>
public sealed class PathArgumentTests : IDisposable
{
    private readonly string workspace = Directory.CreateTempSubdirectory("forge-path-argument-").FullName;

    public void Dispose() => Directory.Delete(workspace, recursive: true);

    private void Make(string relative, string content = "x")
    {
        var path = Path.Combine(workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<string> RunAsync(ITool tool, params (string Name, object Value)[] arguments)
    {
        var json = JsonSerializer.Serialize(arguments.ToDictionary(argument => argument.Name, argument => argument.Value));
        var result = await tool.InvokeAsync(
            new ToolCall("call", tool.Definition.Name, json),
            new ToolContext("session", workspace),
            CancellationToken.None);
        Assert.False(result.IsError, result.Content);
        return result.Content;
    }

    [Fact]
    public async Task A_blank_path_makes_grep_search_the_workspace_root()
    {
        Make("src/App.cs", "needle\n");

        Assert.Equal(
            "src/App.cs:1:needle",
            await RunAsync(new GrepTool(new ToolOptions { RipgrepPath = null }), ("pattern", "needle"), ("path", "")));
    }

    [Fact]
    public async Task A_blank_path_makes_find_walk_the_workspace_root()
    {
        Make("src/App.cs");

        Assert.Equal("src/App.cs", await RunAsync(new FindTool(), ("pattern", "*.cs"), ("path", "   ")));
    }

    [Fact]
    public async Task A_blank_path_makes_ls_list_the_workspace_root()
    {
        Make("src/App.cs");

        Assert.Equal("d src/", await RunAsync(new ListTool(), ("path", "")));
    }
}
