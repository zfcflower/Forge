using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests;

public sealed class FileVersionLedgerTests : IDisposable
{
    private readonly string workspace = Directory.CreateTempSubdirectory("forge-file-version-").FullName;

    public void Dispose() => Directory.Delete(workspace, recursive: true);

    private string Make(string relative, string content)
    {
        var path = Path.Combine(workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private ToolContext Context(IFileVersionLedger? fileVersions) =>
        new("session", workspace) { FileVersions = fileVersions };

    private static ToolContext Context(AgentInstance agent) =>
        new(agent.SessionId, agent.WorkingDirectory)
        {
            AgentId = agent.Id,
            AgentName = agent.Definition.Name,
            FileVersions = agent.FileVersions,
        };

    private static AgentHost CreateAgentHost() => new(new AgentHostOptions
    {
        Providers = static _ => throw new NotSupportedException("This test does not run an agent turn."),
        Tools = [],
        Layers = new PromptLayers("system", null, null),
    });

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
    public async Task New_file_write_records_version_for_an_immediate_whole_file_overwrite()
    {
        var ledger = CreateAgentHost().CreateRoot("session", workspace).FileVersions;
        var context = Context(ledger);

        var created = await InvokeAsync(new WriteTool(), context, ("path", "new.txt"), ("content", "first version"));
        Assert.False(created.IsError, created.Content);
        Assert.Equal("first version", File.ReadAllText(Path.Combine(workspace, "new.txt")));

        var replaced = await InvokeAsync(new WriteTool(), context, ("path", "new.txt"), ("content", "second version"));

        Assert.False(replaced.IsError, replaced.Content);
        Assert.Equal("second version", File.ReadAllText(Path.Combine(workspace, "new.txt")));
    }

    [Fact]
    public async Task Write_refuses_an_existing_file_not_read_by_this_agent_without_changing_it()
    {
        var path = Make("existing.txt", "original content stays intact");
        var ledger = CreateAgentHost().CreateRoot("session", workspace).FileVersions;

        var result = await InvokeAsync(new WriteTool(), Context(ledger), ("path", "existing.txt"), ("content", "replacement"));

        Assert.True(result.IsError);
        Assert.Contains("has not read", result.Content);
        Assert.Equal("original content stays intact", File.ReadAllText(path));
    }

    [Fact]
    public async Task Write_allows_replacing_an_existing_file_after_reading_it()
    {
        var path = Make("existing.txt", "before");
        var context = Context(CreateAgentHost().CreateRoot("session", workspace).FileVersions);
        var read = await InvokeAsync(new ReadTool(), context, ("path", "existing.txt"));
        Assert.False(read.IsError, read.Content);

        var written = await InvokeAsync(new WriteTool(), context, ("path", "existing.txt"), ("content", "after"));

        Assert.False(written.IsError, written.Content);
        Assert.Equal("after", File.ReadAllText(path));
    }

    [Fact]
    public async Task Write_refuses_a_file_changed_after_read_and_preserves_the_external_content()
    {
        var path = Make("existing.txt", "original");
        var context = Context(CreateAgentHost().CreateRoot("session", workspace).FileVersions);
        Assert.False((await InvokeAsync(new ReadTool(), context, ("path", "existing.txt"))).IsError);
        File.WriteAllText(path, "external content with a different length");

        var result = await InvokeAsync(new WriteTool(), context, ("path", "existing.txt"), ("content", "agent replacement"));

        Assert.True(result.IsError);
        Assert.Contains("changed after", result.Content);
        Assert.Equal("external content with a different length", File.ReadAllText(path));
    }

    [Fact]
    public async Task Partial_read_records_the_file_version_for_a_following_overwrite()
    {
        var path = Make("lines.txt", "first\nsecond\nthird\n");
        var context = Context(CreateAgentHost().CreateRoot("session", workspace).FileVersions);
        var read = await InvokeAsync(
            new ReadTool(), context,
            ("path", "lines.txt"), ("offset", 2), ("limit", 1));
        Assert.False(read.IsError, read.Content);
        Assert.Contains("2→second", read.Content);

        var written = await InvokeAsync(new WriteTool(), context, ("path", "lines.txt"), ("content", "replacement after partial read"));

        Assert.False(written.IsError, written.Content);
        Assert.Equal("replacement after partial read", File.ReadAllText(path));
    }

    [Fact]
    public async Task Edit_over_an_external_change_succeeds_without_refreshing_the_stale_read_record()
    {
        var path = Make("existing.txt", "original needle");
        var context = Context(CreateAgentHost().CreateRoot("session", workspace).FileVersions);
        Assert.False((await InvokeAsync(new ReadTool(), context, ("path", "existing.txt"))).IsError);
        File.WriteAllText(path, "external version with needle");

        var edited = await InvokeAsync(
            new EditTool(), context,
            ("path", "existing.txt"), ("old_string", "needle"), ("new_string", "edited"));
        Assert.False(edited.IsError, edited.Content);
        Assert.Equal("external version with edited", File.ReadAllText(path));

        var written = await InvokeAsync(new WriteTool(), context, ("path", "existing.txt"), ("content", "stale whole-file replacement"));

        Assert.True(written.IsError);
        Assert.Contains("changed after", written.Content);
        Assert.Equal("external version with edited", File.ReadAllText(path));
    }

    [Fact]
    public async Task Edit_of_the_recorded_version_advances_the_record_for_a_following_overwrite()
    {
        var path = Make("existing.txt", "before");
        var context = Context(CreateAgentHost().CreateRoot("session", workspace).FileVersions);
        Assert.False((await InvokeAsync(new ReadTool(), context, ("path", "existing.txt"))).IsError);

        var edited = await InvokeAsync(
            new EditTool(), context,
            ("path", "existing.txt"), ("old_string", "before"), ("new_string", "after edit"));
        Assert.False(edited.IsError, edited.Content);
        Assert.Equal("after edit", File.ReadAllText(path));

        var written = await InvokeAsync(new WriteTool(), context, ("path", "existing.txt"), ("content", "whole-file replacement"));

        Assert.False(written.IsError, written.Content);
        Assert.Equal("whole-file replacement", File.ReadAllText(path));
    }

    [Fact]
    public async Task Append_over_an_external_change_succeeds_but_does_not_refresh_the_stale_record()
    {
        var path = Make("existing.txt", "original");
        var context = Context(CreateAgentHost().CreateRoot("session", workspace).FileVersions);
        Assert.False((await InvokeAsync(new ReadTool(), context, ("path", "existing.txt"))).IsError);
        File.WriteAllText(path, "external content");

        var appended = await InvokeAsync(
            new WriteTool(), context,
            ("path", "existing.txt"), ("content", " + appended"), ("append", true));
        Assert.False(appended.IsError, appended.Content);
        Assert.Equal("external content + appended", File.ReadAllText(path));

        var written = await InvokeAsync(new WriteTool(), context, ("path", "existing.txt"), ("content", "stale whole-file replacement"));

        Assert.True(written.IsError);
        Assert.Contains("changed after", written.Content);
        Assert.Equal("external content + appended", File.ReadAllText(path));
    }

    [Fact]
    public async Task Append_to_the_recorded_version_advances_the_record_for_a_following_overwrite()
    {
        var path = Make("existing.txt", "original");
        var context = Context(CreateAgentHost().CreateRoot("session", workspace).FileVersions);
        Assert.False((await InvokeAsync(new ReadTool(), context, ("path", "existing.txt"))).IsError);

        var appended = await InvokeAsync(
            new WriteTool(), context,
            ("path", "existing.txt"), ("content", " + appended"), ("append", true));
        Assert.False(appended.IsError, appended.Content);
        Assert.Equal("original + appended", File.ReadAllText(path));

        var written = await InvokeAsync(new WriteTool(), context, ("path", "existing.txt"), ("content", "whole-file replacement"));

        Assert.False(written.IsError, written.Content);
        Assert.Equal("whole-file replacement", File.ReadAllText(path));
    }

    [Fact]
    public async Task Read_and_write_skip_version_checks_when_context_has_no_ledger()
    {
        var path = Make("existing.txt", "before read");
        var context = Context(fileVersions: null);

        var writtenWithoutRead = await InvokeAsync(new WriteTool(), context, ("path", "existing.txt"), ("content", "written without a ledger read"));
        Assert.False(writtenWithoutRead.IsError, writtenWithoutRead.Content);
        Assert.Equal("written without a ledger read", File.ReadAllText(path));

        var readWithoutLedger = await InvokeAsync(new ReadTool(), context, ("path", "existing.txt"));

        Assert.False(readWithoutLedger.IsError, readWithoutLedger.Content);
        Assert.Contains("written without a ledger read", readWithoutLedger.Content);
    }

    [Fact]
    public async Task Agent_instances_do_not_share_file_versions()
    {
        var path = Make("existing.txt", "original content");
        var host = CreateAgentHost();
        var agentA = host.CreateRoot("session", workspace, id: "agent-a");
        var agentB = host.CreateRoot("session", workspace, id: "agent-b");
        Assert.False((await InvokeAsync(new ReadTool(), Context(agentA), ("path", "existing.txt"))).IsError);

        var result = await InvokeAsync(new WriteTool(), Context(agentB), ("path", "existing.txt"), ("content", "agent B replacement"));

        Assert.True(result.IsError);
        Assert.Contains("has not read", result.Content);
        Assert.Equal("original content", File.ReadAllText(path));
    }

    [Fact]
    public async Task Different_relative_absolute_dot_and_windows_case_paths_hit_the_same_record()
    {
        var path = Make("nested/file.txt", "original");
        var context = Context(CreateAgentHost().CreateRoot("session", workspace).FileVersions);
        var read = await InvokeAsync(new ReadTool(), context, ("path", "nested/file.txt"));
        Assert.False(read.IsError, read.Content);

        var alternatePath = OperatingSystem.IsWindows()
            ? Path.Combine(workspace.ToUpperInvariant(), ".", "NESTED", "FILE.TXT")
            : Path.Combine(workspace, ".", "nested", "file.txt");
        var written = await InvokeAsync(new WriteTool(), context, ("path", alternatePath), ("content", "written through another path spelling"));

        Assert.False(written.IsError, written.Content);
        Assert.Equal("written through another path spelling", File.ReadAllText(path));
    }
}
