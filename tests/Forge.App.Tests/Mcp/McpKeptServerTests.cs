using Forge.App.Localization;
using Forge.App.Services;
using Forge.App.ViewModels.Settings;
using Forge.Mcp;
using Xunit;

namespace Forge.App.Tests.Mcp;

/// <summary>
/// The servers a broken file hides from the page: the manager keeps running them, so the page names them (with the file to
/// repair) instead of leaving a running server with no row and no way to see it.
/// </summary>
public sealed class McpKeptServerTests
{
    private static McpServerConfig Config(string name, string source) =>
        new() { Name = name, Command = "cmd", Source = source };

    private static McpConfigSnapshot Snapshot(params McpServerEntry[] entries) =>
        new([new McpServerFile("/global/mcp.json", false, entries, [])], []);

    [Fact]
    public void A_server_whose_name_the_file_read_could_not_see_is_reported_with_its_file()
    {
        var kept = McpSettingsSectionViewModel.KeptServers(
            Snapshot(),
            [Config("ghost", "/global/mcp.json")]);

        var single = Assert.Single(kept);
        Assert.Equal("ghost", single.Name);
        Assert.Equal("/global/mcp.json", single.Source);
    }

    [Fact]
    public void A_server_the_file_lists_is_not_reported()
    {
        var kept = McpSettingsSectionViewModel.KeptServers(
            Snapshot(new McpServerEntry { Name = "visible", Command = "cmd" }),
            [Config("visible", "/global/mcp.json")]);

        Assert.Empty(kept);
    }

    [Fact]
    public void A_config_without_a_source_still_names_the_server()
    {
        var kept = McpSettingsSectionViewModel.KeptServers(Snapshot(), [Config("anonymous", string.Empty)]);

        Assert.Equal(string.Empty, Assert.Single(kept).Source);
    }

    [Fact]
    public void The_diagnostic_sentence_names_the_server_and_the_file()
    {
        var text = McpSettingsStrings.DiagnosticKeptServer("ghost", "/global/mcp.json");

        Assert.Contains("ghost", text);
        Assert.Contains("/global/mcp.json", text);
    }
}
