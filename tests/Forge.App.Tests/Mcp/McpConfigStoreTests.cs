using Forge.App.Services;
using Forge.Mcp;
using Xunit;

namespace Forge.App.Tests.Mcp;

/// <summary>
/// The file side of MCP configuration: <see cref="McpConfigStore"/> reads both mcp.json files (the project's after the
/// global one) and edits exactly one of them per operation, keeping every other entry and unknown field as they were.
/// </summary>
public sealed class McpConfigStoreTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-mcp-config-");
    private readonly string globalPath;
    private readonly string workspace;
    private readonly string projectPath;

    public McpConfigStoreTests()
    {
        globalPath = Path.Combine(root, "global", "mcp.json");
        workspace = Path.Combine(root, "workspace");
        projectPath = Path.Combine(workspace, ".forge", "mcp.json");
    }

    public void Dispose() => TestTempFiles.DeleteTree(root);

    private McpConfigStore Store() => new(globalPath, workspace);

    private McpConfigStore StoreWithoutWorkspace() => new(globalPath, null);

    private static McpServerEntry Entry(string name, string command = "cmd") => new() { Name = name, Command = command };

    private void WriteGlobal(string json) => TestTempFiles.WriteFile(root, "global/mcp.json", json);

    private void WriteProject(string json) => TestTempFiles.WriteFile(root, "workspace/.forge/mcp.json", json);

    [Fact]
    public void The_paths_are_resolved_from_the_constructor_arguments()
    {
        var store = Store();

        Assert.Equal(Path.GetFullPath(globalPath), store.GlobalPath);
        Assert.Equal(Path.GetFullPath(projectPath), store.ProjectPath);
        Assert.Null(new McpConfigStore(globalPath, null).ProjectPath);
    }

    [Fact]
    public void Read_returns_both_files_with_the_project_second_and_their_diagnostics()
    {
        WriteGlobal("""{"mcpServers":{"g1":{"command":"g"},"broken":5}}""");
        WriteProject("""{"mcpServers":{"p1":{"url":"https://example.com/mcp"}}}""");

        var snapshot = Store().Read();

        Assert.Equal(2, snapshot.Files.Count);
        var global = snapshot.Files[0];
        Assert.Equal(Path.GetFullPath(globalPath), global.Path);
        Assert.False(global.IsProject);
        Assert.Equal(["g1"], global.Entries.Select(entry => entry.Name));
        var project = snapshot.Files[1];
        Assert.Equal(Path.GetFullPath(projectPath), project.Path);
        Assert.True(project.IsProject);
        Assert.Equal(["p1"], project.Entries.Select(entry => entry.Name));
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.ServerName == "broken");
    }

    [Fact]
    public void Read_reports_missing_files_as_empty_files()
    {
        var snapshot = Store().Read();

        Assert.Equal(2, snapshot.Files.Count);
        Assert.All(snapshot.Files, file => Assert.Empty(file.Entries));
        Assert.Empty(snapshot.Diagnostics);
    }

    [Fact]
    public void Read_without_a_workspace_returns_only_the_global_file()
    {
        var snapshot = StoreWithoutWorkspace().Read();

        Assert.Single(snapshot.Files);
    }

    [Fact]
    public void Load_lets_a_project_entry_override_the_global_entry_by_name()
    {
        WriteGlobal("""{"mcpServers":{"srv":{"command":"global-cmd"}}}""");
        WriteProject("""{"mcpServers":{"srv":{"command":"project-cmd"},"other":{"command":"other-cmd"}}}""");

        var config = Store().Load();

        Assert.Equal(2, config.Servers.Count);
        Assert.Equal("project-cmd", config.Find("srv")!.Command);
        Assert.Equal("other-cmd", config.Find("other")!.Command);
        Assert.Empty(config.Diagnostics);
    }

    [Fact]
    public void Upsert_writes_into_the_target_file_and_leaves_the_other_file_byte_identical()
    {
        WriteGlobal("""{"mcpServers":{"a":{"command":"a","x-custom":42}}}""");
        WriteProject("""{"mcpServers":{"p":{"command":"p"}}}""");
        var before = File.ReadAllBytes(projectPath);
        var store = Store();

        store.Upsert(globalPath, Entry("b", "b-cmd"));

        var added = Assert.Single(store.Read().Files[0].Entries, entry => entry.Name == "b");
        Assert.Equal("b-cmd", added.Command);
        Assert.Equal(["a", "b"], store.Read().Files[0].Entries.Select(entry => entry.Name));
        Assert.True(File.ReadAllBytes(projectPath).SequenceEqual(before));
    }

    [Fact]
    public void Upsert_keeps_unknown_fields_of_the_replaced_entry()
    {
        WriteGlobal("""{"mcpServers":{"a":{"command":"a","x-custom":42}}}""");
        var store = Store();

        store.Upsert(globalPath, new McpServerEntry { Name = "a", Command = "new-cmd" });

        var entry = Assert.Single(store.Read().Files[0].Entries);
        Assert.Equal("new-cmd", entry.Command);
        Assert.Contains("\"x-custom\"", entry.RawNodeJson);
        Assert.Contains("\"x-custom\"", File.ReadAllText(globalPath));
    }

    [Fact]
    public void Upsert_creates_missing_directories_and_files_and_the_next_read_sees_the_entry()
    {
        var path = Path.Combine(root, "deep", "nested", "mcp.json");
        var store = new McpConfigStore(path, null);

        store.Upsert(path, new McpServerEntry { Name = "srv", Command = "cmd" });

        Assert.Equal("srv", Assert.Single(store.Read().Files[0].Entries).Name);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Remove_deletes_only_the_named_entry_from_the_target_file()
    {
        WriteGlobal("""{"mcpServers":{"a":{"command":"a"},"b":{"command":"b"}}}""");
        WriteProject("""{"mcpServers":{"p":{"command":"p"}}}""");
        var before = File.ReadAllBytes(projectPath);
        var store = Store();

        store.Remove(globalPath, "a");

        Assert.Equal(["b"], store.Read().Files[0].Entries.Select(entry => entry.Name));
        Assert.True(File.ReadAllBytes(projectPath).SequenceEqual(before));
    }

    [Fact]
    public void SetDisabled_for_a_missing_name_returns_false_and_does_not_touch_the_file()
    {
        WriteGlobal("""{"mcpServers":{"a":{"command":"a"}}}""");
        var before = File.ReadAllBytes(globalPath);
        var store = Store();

        Assert.False(store.SetDisabled(globalPath, "ghost", true));
        Assert.True(File.ReadAllBytes(globalPath).SequenceEqual(before));
    }

    [Fact]
    public void SetDisabled_flips_the_flag_and_keeps_the_rest_of_the_entry()
    {
        WriteGlobal("""{"mcpServers":{"a":{"command":"a","env":{"K":"v"},"x-custom":7}}}""");
        var store = Store();

        Assert.True(store.SetDisabled(globalPath, "a", true));
        var disabled = Assert.Single(store.Read().Files[0].Entries);
        Assert.True(disabled.Disabled);
        Assert.Contains("\"x-custom\"", disabled.RawNodeJson);
        Assert.Contains("\"disabled\": true", File.ReadAllText(globalPath));

        Assert.True(store.SetDisabled(globalPath, "a", false));
        var enabled = Assert.Single(store.Read().Files[0].Entries);
        Assert.False(enabled.Disabled);
        Assert.DoesNotContain("\"disabled\"", File.ReadAllText(globalPath));
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("not json at all")]
    [InlineData("""{"mcpServers":[]}""")]
    public void A_write_over_a_broken_root_throws_InvalidDataException_and_preserves_the_file(string broken)
    {
        WriteGlobal(broken);
        var before = File.ReadAllBytes(globalPath);
        var store = Store();

        Assert.Throws<InvalidDataException>(() => store.Upsert(globalPath, Entry("x")));
        Assert.True(File.ReadAllBytes(globalPath).SequenceEqual(before));
    }

    [Fact]
    public void Remove_also_refuses_a_broken_root()
    {
        WriteGlobal("[1,2,3]");

        Assert.Throws<InvalidDataException>(() => Store().Remove(globalPath, "a"));
    }

    [Fact]
    public void SetDisabled_on_a_broken_root_reports_false_without_rewriting_the_file()
    {
        WriteGlobal("[1,2,3]");
        var before = File.ReadAllBytes(globalPath);

        Assert.False(Store().SetDisabled(globalPath, "a", true));
        Assert.True(File.ReadAllBytes(globalPath).SequenceEqual(before));
    }

    [Fact]
    public void Import_writes_entries_verbatim_keeping_placeholders_and_unknown_fields()
    {
        var sourcePath = TestTempFiles.WriteFile(
            root, "other/mcp.json", """{"mcpServers":{"src":{"command":"c","env":{"TOKEN":"${TOKEN}"},"x-keep":true}}}""");
        var source = McpConfigFile.Read(File.ReadAllText(sourcePath), sourcePath).Entries;
        WriteGlobal("""{"mcpServers":{"keep":{"command":"k"}}}""");
        var store = Store();

        store.Import(globalPath, source);

        var text = File.ReadAllText(globalPath);
        Assert.Contains("\"src\"", text);
        Assert.Contains("${TOKEN}", text);
        Assert.Contains("\"x-keep\"", text);
        Assert.Contains("\"keep\"", text);
        Assert.Equal(["keep", "src"], store.Read().Files[0].Entries.Select(entry => entry.Name));
    }

    [Fact]
    public void A_saved_entry_round_trips_every_managed_field()
    {
        var store = Store();
        var entry = new McpServerEntry
        {
            Name = "full",
            Command = "node",
            Args = ["serve.js", "--port", "8080"],
            Env = [new("PORT", "8080"), new("TOKEN", "sekret")],
            WorkingDirectory = "/tmp/srv",
            Headers = [new("Authorization", "Bearer x")],
            ReadOnlyTools = ["view"],
            TimeoutSeconds = 12.5,
            Disabled = true,
        };

        store.Upsert(globalPath, entry);

        var read = Assert.Single(store.Read().Files[0].Entries);
        Assert.Equal("node", read.Command);
        Assert.Equal(["serve.js", "--port", "8080"], read.Args);
        Assert.Equal([("PORT", "8080"), ("TOKEN", "sekret")], read.Env.Select(pair => (pair.Key, pair.Value)));
        Assert.Equal("/tmp/srv", read.WorkingDirectory);
        Assert.Equal([("Authorization", "Bearer x")], read.Headers.Select(pair => (pair.Key, pair.Value)));
        Assert.Equal(["view"], read.ReadOnlyTools);
        Assert.Equal(12.5, read.TimeoutSeconds);
        Assert.True(read.Disabled);
    }
}
