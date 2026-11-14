using Forge.App.Composition;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.Mcp;
using Xunit;

namespace Forge.App.Tests.Mcp;

/// <summary>
/// The composition-facing MCP services over real temp files: a save lands in the manager without starting anything
/// (servers start lazily on first use), reads reflect the files verbatim, and the approval/limit helpers behave.
/// </summary>
public sealed class McpServicesTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-mcp-services-");
    private readonly string globalPath;
    private readonly string workspace;

    public McpServicesTests()
    {
        globalPath = Path.Combine(root, "global", "mcp.json");
        workspace = Path.Combine(root, "workspace");
    }

    public void Dispose() => TestTempFiles.DeleteTree(root);

    private McpConfigStore Store() => new(globalPath, workspace);

    private PreferencesStore Preferences() => new(Path.Combine(root, "settings.json"));

    private void WriteGlobal(string json) => TestTempFiles.WriteFile(root, "global/mcp.json", json);

    private static McpServerEntry Entry(string name, string command = "cmd") => new() { Name = name, Command = command };

    [Fact]
    public async Task Create_reports_the_configured_server_without_starting_it()
    {
        WriteGlobal("""{"mcpServers":{"srv":{"command":"never-spawned"}}}""");
        await using var preferences = Preferences();
        await using var services = McpServices.Create(Store(), preferences, null, null);

        Assert.Equal(["srv"], services.Manager.ServerNames);
        var state = Assert.Single(services.States);
        Assert.Equal("srv", state.Name);
        Assert.Equal(McpServerStatus.Stopped, state.Status);
        Assert.True(state.Enabled);
    }

    [Fact]
    public async Task ReadConfigAsync_reflects_the_files_on_disk_verbatim()
    {
        WriteGlobal("""{"mcpServers":{"g":{"command":"g","env":{"TOKEN":"${TOKEN}"}},"broken":5}}""");
        TestTempFiles.WriteFile(root, "workspace/.forge/mcp.json", """{"mcpServers":{"p":{"command":"p"}}}""");
        await using var preferences = Preferences();
        await using var services = McpServices.Create(Store(), preferences, null, null);

        var snapshot = await services.ReadConfigAsync(CancellationToken.None);

        Assert.Equal(2, snapshot.Files.Count);
        Assert.Equal(["g"], snapshot.Files[0].Entries.Select(entry => entry.Name));
        Assert.Equal("${TOKEN}", Assert.Single(snapshot.Files[0].Entries).Env[0].Value);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.ServerName == "broken");
        Assert.Equal(["p"], snapshot.Files[1].Entries.Select(entry => entry.Name));
    }

    [Fact]
    public async Task SaveAsync_adds_the_server_to_the_manager_still_stopped()
    {
        await using var preferences = Preferences();
        await using var services = McpServices.Create(Store(), preferences, null, null);

        await services.SaveAsync(globalPath, Entry("new", "new-cmd"), CancellationToken.None);

        Assert.Equal(["new"], services.Manager.ServerNames);
        var state = Assert.Single(services.States);
        Assert.Equal(McpServerStatus.Stopped, state.Status);
        var entry = Assert.Single((await services.ReadConfigAsync(CancellationToken.None)).Files[0].Entries);
        Assert.Equal("new", entry.Name);
        Assert.Equal("new-cmd", entry.Command);
    }

    [Fact]
    public async Task SaveAsync_into_the_project_file_leaves_the_global_file_alone()
    {
        WriteGlobal("""{"mcpServers":{"g":{"command":"g"}}}""");
        await using var preferences = Preferences();
        var store = Store();
        await using var services = McpServices.Create(store, preferences, null, null);
        var before = File.ReadAllBytes(globalPath);

        await services.SaveAsync(store.ProjectPath!, Entry("p", "p-cmd"), CancellationToken.None);

        Assert.Equal(["g", "p"], services.Manager.ServerNames);
        Assert.True(File.ReadAllBytes(globalPath).SequenceEqual(before));
        Assert.Equal("p", Assert.Single(store.Read().Files[1].Entries).Name);
    }

    [Fact]
    public async Task RemoveAsync_drops_the_server_from_the_manager_and_the_file()
    {
        WriteGlobal("""{"mcpServers":{"keep":{"command":"k"},"drop":{"command":"d"}}}""");
        await using var preferences = Preferences();
        await using var services = McpServices.Create(Store(), preferences, null, null);
        Assert.Equal(["keep", "drop"], services.Manager.ServerNames);

        await services.RemoveAsync(globalPath, "drop", CancellationToken.None);

        Assert.Equal(["keep"], services.Manager.ServerNames);
        Assert.DoesNotContain("\"drop\"", File.ReadAllText(globalPath));
    }

    [Fact]
    public async Task SetDisabledAsync_disables_the_server_and_writes_the_flag()
    {
        WriteGlobal("""{"mcpServers":{"srv":{"command":"cmd"}}}""");
        await using var preferences = Preferences();
        await using var services = McpServices.Create(Store(), preferences, null, null);

        await services.SetDisabledAsync(globalPath, "srv", true, CancellationToken.None);

        var state = Assert.Single(services.States);
        Assert.False(state.Enabled);
        Assert.Equal(McpServerStatus.Stopped, state.Status);
        Assert.Contains("\"disabled\": true", File.ReadAllText(globalPath));
    }

    [Fact]
    public async Task DescribeForApproval_returns_null_for_an_unknown_tool()
    {
        WriteGlobal("""{"mcpServers":{"srv":{"command":"cmd"}}}""");
        await using var preferences = Preferences();
        await using var services = McpServices.Create(Store(), preferences, null, null);

        Assert.Null(services.DescribeForApproval("srv__tool"));
        Assert.Null(services.DescribeForApproval("anything"));
        Assert.Null(services.DescribeForApproval(""));
    }

    [Theory]
    [InlineData("a\nb\nc", 1, "c")]
    [InlineData("a\nb\nc", 2, "b\nc")]
    [InlineData("a\nb\nc", 3, "a\nb\nc")]
    [InlineData("a\nb\nc", 99, "a\nb\nc")]
    [InlineData("a\nb\nc", 0, "a\nb\nc")]
    [InlineData("abc", 1, "abc")]
    [InlineData("", 5, "")]
    [InlineData(null, 5, null)]
    public void Tail_keeps_the_last_lines(string? stderr, int lines, string? expected) =>
        Assert.Equal(expected, McpServices.Tail(stderr, lines));

    [Fact]
    public async Task ToolSearchLimit_follows_the_preferences()
    {
        await using var preferences = Preferences();
        await using var services = McpServices.Create(Store(), preferences, null, null);

        Assert.Equal(0, services.ToolSearchLimit);

        preferences.Current.McpToolSearchLimit = 7;
        Assert.Equal(7, services.ToolSearchLimit);

        preferences.Current.McpToolSearchLimit = 0;
        Assert.Equal(0, services.ToolSearchLimit);
    }
}
