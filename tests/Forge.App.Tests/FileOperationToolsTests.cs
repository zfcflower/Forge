using System.Text.Json;
using Forge.Abstractions;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests;

public sealed class FileOperationToolsTests : IDisposable
{
    private readonly string workspace = Directory.CreateTempSubdirectory("forge-fileops-").FullName;

    public void Dispose() => Directory.Delete(workspace, recursive: true);

    private async Task<ToolResult> RunAsync(ITool tool, params (string Name, object Value)[] arguments)
    {
        var json = JsonSerializer.Serialize(arguments.ToDictionary(argument => argument.Name, argument => argument.Value));
        return await tool.InvokeAsync(new ToolCall("call", tool.Definition.Name, json), new ToolContext("session", workspace), CancellationToken.None);
    }

    private string Make(string relative, string content = "x")
    {
        var path = Path.Combine(workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Copy_copies_a_tree_and_refuses_an_existing_destination_until_overwrite()
    {
        Make("src/a.txt", "a");
        Make("src/sub/b.txt", "b");
        Make("src/.git", "gitdir: elsewhere");

        var first = await RunAsync(new CopyTool(), ("from", "src"), ("to", "dst/copy"));
        Assert.False(first.IsError, first.Content);
        Assert.Equal("b", File.ReadAllText(Path.Combine(workspace, "dst", "copy", "sub", "b.txt")));
        Assert.False(File.Exists(Path.Combine(workspace, "dst", "copy", ".git")));

        var again = await RunAsync(new CopyTool(), ("from", "src"), ("to", "dst/copy"));
        Assert.True(again.IsError);

        File.WriteAllText(Path.Combine(workspace, "src", "a.txt"), "changed");
        var overwritten = await RunAsync(new CopyTool(), ("from", "src"), ("to", "dst/copy"), ("overwrite", true));
        Assert.False(overwritten.IsError, overwritten.Content);
        Assert.Equal("changed", File.ReadAllText(Path.Combine(workspace, "dst", "copy", "a.txt")));
    }

    [Fact]
    public async Task Copy_refuses_a_directory_into_itself_and_a_target_outside_the_workspace()
    {
        Make("src/a.txt");

        Assert.True((await RunAsync(new CopyTool(), ("from", "src"), ("to", "src/inner"))).IsError);
        Assert.True((await RunAsync(new CopyTool(), ("from", "src/a.txt"), ("to", "../escape.txt"))).IsError);
    }

    [Fact]
    public async Task Move_renames_without_replacing_unless_asked_and_never_replaces_a_directory()
    {
        Make("a.txt", "a");
        Make("b.txt", "b");
        Make("dir/c.txt", "c");

        Assert.True((await RunAsync(new MoveTool(), ("from", "a.txt"), ("to", "b.txt"))).IsError);
        Assert.True((await RunAsync(new MoveTool(), ("from", "a.txt"), ("to", "dir"), ("overwrite", true))).IsError);
        Assert.True(File.Exists(Path.Combine(workspace, "a.txt")));

        Assert.False((await RunAsync(new MoveTool(), ("from", "a.txt"), ("to", "b.txt"), ("overwrite", true))).IsError);
        Assert.Equal("a", File.ReadAllText(Path.Combine(workspace, "b.txt")));

        Assert.False((await RunAsync(new MoveTool(), ("from", "dir"), ("to", "moved/deeper"))).IsError);
        Assert.True(File.Exists(Path.Combine(workspace, "moved", "deeper", "c.txt")));
        Assert.True((await RunAsync(new MoveTool(), ("from", "moved"), ("to", "moved/deeper/inside"))).IsError);
    }

    [Fact]
    public async Task Delete_needs_recursive_for_a_full_directory_and_never_touches_root_or_git()
    {
        Make("full/a.txt");
        Make(".git/HEAD");

        Assert.True((await RunAsync(new DeleteTool(), ("path", "full"))).IsError);
        Assert.True(Directory.Exists(Path.Combine(workspace, "full")));

        var removed = await RunAsync(new DeleteTool(), ("path", "full"), ("recursive", true));
        Assert.False(removed.IsError, removed.Content);
        Assert.False(Directory.Exists(Path.Combine(workspace, "full")));

        Assert.True((await RunAsync(new DeleteTool(), ("path", "."), ("recursive", true))).IsError);
        Assert.True((await RunAsync(new DeleteTool(), ("path", ".git"), ("recursive", true))).IsError);
        Assert.True((await RunAsync(new DeleteTool(), ("path", "../elsewhere"))).IsError);
        Assert.True(File.Exists(Path.Combine(workspace, ".git", "HEAD")));
    }

    [Fact]
    public async Task A_large_tree_is_copied_and_deleted_completely()
    {
        for (var index = 0; index < 150; index++)
        {
            Make($"big/d{index % 7}/f{index}.txt", index.ToString());
        }

        var copied = await RunAsync(new CopyTool(), ("from", "big"), ("to", "big2"));
        Assert.False(copied.IsError, copied.Content);
        Assert.Equal(150, Directory.GetFiles(Path.Combine(workspace, "big2"), "*", SearchOption.AllDirectories).Length);
        Assert.Equal("149", File.ReadAllText(Path.Combine(workspace, "big2", "d2", "f149.txt")));

        var deleted = await RunAsync(new DeleteTool(), ("path", "big2"), ("recursive", true));
        Assert.False(deleted.IsError, deleted.Content);
        Assert.Contains("150 files", deleted.Content);
        Assert.False(Directory.Exists(Path.Combine(workspace, "big2")));
        Assert.Equal(150, Directory.GetFiles(Path.Combine(workspace, "big"), "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Delete_removes_read_only_files()
    {
        var path = Make("locked/ro.txt");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var result = await RunAsync(new DeleteTool(), ("path", "locked"), ("recursive", true));

        Assert.False(result.IsError, result.Content);
        Assert.False(Directory.Exists(Path.Combine(workspace, "locked")));
    }

    [Fact]
    public async Task Delete_of_a_link_removes_the_link_and_keeps_the_target()
    {
        var target = Make("real/keep.txt");
        var link = Path.Combine(workspace, "link");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(workspace, "real"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return; // Creating links needs a privilege some machines do not grant.
        }

        var result = await RunAsync(new DeleteTool(), ("path", "link"), ("recursive", true));

        Assert.False(result.IsError, result.Content);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(target));
    }
}
