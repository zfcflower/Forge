using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Forge.Workspace.Tests.Specs;

public sealed class SpecToolsLinkSafetyTests(ITestOutputHelper output)
{
    private const string NoLinkReason = "No directory junction or symbolic link could be created on this host; the test was not executed.";

    private static ToolContext Context(string directory) => new("session-1", directory);

    private static async Task<ToolResult> Invoke(ITool tool, string json, string directory) =>
        await tool.InvokeAsync(new ToolCall("call-1", tool.Definition.Name, json), Context(directory), CancellationToken.None);

    [Fact]
    public async Task Create_refuses_a_path_that_runs_through_a_directory_link_pointing_outside()
    {
        using var workspace = new TempDirectory();
        using var outside = new TempDirectory();
        using var link = WorkspaceLink.TryCreate(Path.Combine(workspace.Path, "linked"), outside.Path, isDirectory: true);
        if (link is null)
        {
            output.WriteLine(NoLinkReason);
            return;
        }

        var result = await Invoke(new SpecTools(dir => new SpecGraph(dir)).Create, """{"path":"linked/escape.md","id":"escape","type":"t","title":"Escape"}""", workspace.Path);

        Assert.True(result.IsError);
        Assert.StartsWith("spec_create failed:", result.Content, StringComparison.Ordinal);
        Assert.Contains("passes through the link", result.Content, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
    }

    [Fact]
    public async Task Specs_reached_through_a_directory_link_pointing_outside_are_not_indexed()
    {
        using var workspace = new TempDirectory();
        using var outside = new TempDirectory();
        SpecFixture.Write(outside.Path, "external.md", SpecFixture.Spec("external-dir-spec", "module-design"));
        using var link = WorkspaceLink.TryCreate(Path.Combine(workspace.Path, "linked"), outside.Path, isDirectory: true);
        if (link is null)
        {
            output.WriteLine(NoLinkReason);
            return;
        }

        var tools = new SpecTools(dir => new SpecGraph(dir));
        var get = await Invoke(tools.Get, """{"id":"external-dir-spec","refresh":true}""", workspace.Path);
        var grep = await Invoke(tools.Grep, """{"pattern":"external-dir-spec"}""", workspace.Path);

        Assert.True(get.IsError);
        Assert.Contains("No spec with id 'external-dir-spec'", get.Content, StringComparison.Ordinal);
        Assert.Equal(0, JsonDocument.Parse(grep.Content).RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task File_link_pointing_outside_is_not_indexed_and_spec_update_leaves_the_target_intact()
    {
        using var workspace = new TempDirectory();
        using var outside = new TempDirectory();
        var external = SpecFixture.Spec("external-file-spec", "module-design", "status: active\n");
        var externalPath = outside.Combine("external.md");
        File.WriteAllText(externalPath, external, new UTF8Encoding(false));
        SpecFixture.Write(workspace.Path, "docs/arch.md", SpecFixture.Spec("arch", "architecture-design"));
        using var link = WorkspaceLink.TryCreate(workspace.Combine("docs", "linked.md"), externalPath, isDirectory: false);
        if (link is null)
        {
            output.WriteLine(NoLinkReason);
            return;
        }

        var tools = new SpecTools(dir => new SpecGraph(dir));
        var get = await Invoke(tools.Get, """{"id":"external-file-spec","refresh":true}""", workspace.Path);
        var update = await Invoke(tools.Update, """{"id":"external-file-spec","set":{"status":"done"}}""", workspace.Path);

        Assert.True(get.IsError);
        Assert.True(update.IsError);
        Assert.Equal(external, File.ReadAllText(externalPath));
    }

    [Fact]
    public async Task Create_refuses_to_write_through_a_file_link_pointing_outside()
    {
        using var workspace = new TempDirectory();
        using var outside = new TempDirectory();
        var external = SpecFixture.Spec("external-file-spec", "module-design", "status: active\n");
        var externalPath = outside.Combine("external.md");
        File.WriteAllText(externalPath, external, new UTF8Encoding(false));
        SpecFixture.Write(workspace.Path, "docs/arch.md", SpecFixture.Spec("arch", "architecture-design"));
        using var link = WorkspaceLink.TryCreate(workspace.Combine("docs", "linked.md"), externalPath, isDirectory: false);
        if (link is null)
        {
            output.WriteLine(NoLinkReason);
            return;
        }

        var result = await Invoke(new SpecTools(dir => new SpecGraph(dir)).Create, """{"path":"docs/linked.md","id":"through-link","type":"t","title":"Through"}""", workspace.Path);

        Assert.True(result.IsError);
        Assert.Contains("passes through the link", result.Content, StringComparison.Ordinal);
        Assert.Equal(external, File.ReadAllText(externalPath));
    }

    private sealed class WorkspaceLink : IDisposable
    {
        private readonly string path;
        private readonly bool isDirectory;

        private WorkspaceLink(string path, bool isDirectory)
        {
            this.path = path;
            this.isDirectory = isDirectory;
        }

        public static WorkspaceLink? TryCreate(string path, string target, bool isDirectory)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return TryCreateOnHost(path, target, isDirectory) ? new WorkspaceLink(path, isDirectory) : null;
        }

        public void Dispose()
        {
            if (isDirectory && OperatingSystem.IsWindows())
            {
                Directory.Delete(path);
                return;
            }

            File.Delete(path);
        }

        private static bool TryCreateOnHost(string path, string target, bool isDirectory)
        {
            try
            {
                if (isDirectory && OperatingSystem.IsWindows())
                {
                    return RunMklinkJunction(path, target) && Directory.Exists(path);
                }

                if (isDirectory)
                {
                    Directory.CreateSymbolicLink(path, target);
                }
                else
                {
                    File.CreateSymbolicLink(path, target);
                }

                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or PlatformNotSupportedException or Win32Exception)
            {
                return false;
            }
        }

        private static bool RunMklinkJunction(string path, string target)
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", path, target },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
    }
}
