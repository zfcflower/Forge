using System.Text.Json.Nodes;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Preferences;
using Forge.App.ViewModels.Settings;
using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests.Settings;

/// <summary>
/// The Tools settings page and the <c>toolsDisabled</c> preference: grouping by origin, switches only on built-ins that may
/// be switched off, a switch writing through to the preference, the stored list normalized, and the required tools
/// ignored even when the file names them.
/// </summary>
public sealed class ToolSwitchSettingsTests : IDisposable
{
    private readonly string directory;
    private readonly string settingsPath;

    public ToolSwitchSettingsTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "forge-tool-switch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        settingsPath = Path.Combine(directory, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static IReadOnlyList<ToolDescriptor> Catalog() =>
    [
        ToolCatalog.DescribeCore(SpawnAgentTool.Name),
        ToolCatalog.DescribeCore(SubAgentStopTool.Name),
        ToolCatalog.DescribeCore(ReadArtifactTool.Name),
        new("plugin_tool", ToolOrigin.Extension, ToolGroups.Extensions, Source: "my-plugin", CanDisable: false),
        new("grep", ToolOrigin.BuiltIn, ToolGroups.Files),
        new("bash", ToolOrigin.BuiltIn, ToolGroups.Files),
        new("git_status", ToolOrigin.BuiltIn, ToolGroups.Git),
        new("mcp_query", ToolOrigin.Mcp, "server-a", Source: "server-a"),
    ];

    [Fact]
    public void Built_in_groups_come_first_in_fixed_order_then_extension_then_mcp()
    {
        var groups = ToolsSettingsSectionViewModel.Build(Catalog(), new AppPreferences());

        var firstNames = groups.Select(group => group.Tools[0].Name).ToArray();
        Assert.Equal(["bash", "git_status", "spawn_agent", "read_artifact", "plugin_tool", "mcp_query"], firstNames);
        Assert.Equal(["bash", "grep"], groups[0].Tools.Select(tool => tool.Name));
    }

    [Fact]
    public void Only_built_ins_that_may_be_switched_off_get_a_switch()
    {
        var rows = ToolsSettingsSectionViewModel.Build(Catalog(), new AppPreferences())
            .SelectMany(group => group.Tools)
            .ToDictionary(row => row.Name);

        Assert.True(rows["bash"].CanToggle);
        Assert.True(rows[SpawnAgentTool.Name].CanToggle);
        Assert.False(rows[SubAgentStopTool.Name].CanToggle);
        Assert.False(rows[ReadArtifactTool.Name].CanToggle);
        Assert.False(rows["plugin_tool"].CanToggle);
        Assert.False(rows["mcp_query"].CanToggle);
        Assert.True(rows[ReadArtifactTool.Name].HasDetail);
        Assert.StartsWith("my-plugin", rows["plugin_tool"].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_switch_writes_through_to_the_preference_and_reflects_it()
    {
        var preferences = new AppPreferences();
        var raised = 0;
        preferences.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppPreferences.ToolsDisabled))
            {
                raised++;
            }
        };
        var bash = ToolsSettingsSectionViewModel.Build(Catalog(), preferences).SelectMany(g => g.Tools).Single(r => r.Name == "bash");

        bash.IsEnabled = false;
        Assert.Equal(["bash"], preferences.ToolsDisabled);
        Assert.False(ToolsSettingsSectionViewModel.Build(Catalog(), preferences)
            .SelectMany(g => g.Tools).Single(r => r.Name == "bash").IsEnabled);

        bash.IsEnabled = true;
        Assert.Empty(preferences.ToolsDisabled);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void A_row_without_a_switch_ignores_writes()
    {
        var preferences = new AppPreferences();
        var stop = ToolsSettingsSectionViewModel.Build(Catalog(), preferences)
            .SelectMany(g => g.Tools).Single(r => r.Name == SubAgentStopTool.Name);

        stop.IsEnabled = false;

        Assert.True(stop.IsEnabled);
        Assert.Empty(preferences.ToolsDisabled);
    }

    [Fact]
    public void The_first_registration_of_a_name_wins_in_the_catalog()
    {
        var groups = ToolsSettingsSectionViewModel.Build(
            [new("grep", ToolOrigin.BuiltIn, ToolGroups.Files), new("grep", ToolOrigin.Extension, ToolGroups.Extensions)],
            new AppPreferences());

        var row = Assert.Single(groups.SelectMany(g => g.Tools));
        Assert.Equal(ToolOrigin.BuiltIn, row.Descriptor.Origin);
    }

    [Fact]
    public void The_disabled_list_is_trimmed_deduplicated_and_sorted()
    {
        var preferences = new AppPreferences { ToolsDisabled = [" web_fetch ", "bash", "", "bash"] };

        Assert.Equal(["bash", "web_fetch"], preferences.ToolsDisabled);
        Assert.False(preferences.IsToolEnabled("bash"));
        Assert.True(preferences.IsToolEnabled("grep"));
    }

    [Fact]
    public void Required_tools_are_never_switched_off_even_when_the_file_names_them()
    {
        var effective = ToolCatalog.EffectiveDisabled([ReadArtifactTool.Name, "bash", SubAgentSteerTool.Name]).ToArray();

        Assert.Equal(["bash"], effective);
    }

    [Fact]
    public async Task The_list_round_trips_through_settings_json_and_is_omitted_when_empty()
    {
        await File.WriteAllTextAsync(settingsPath, """{ "preferences": { "toolsDisabled": ["web_search", "bash"] } }""");
        await using (var store = new PreferencesStore(settingsPath))
        {
            await store.LoadAsync(CancellationToken.None);
            Assert.Null(store.LoadProblem);
            Assert.Equal(["bash", "web_search"], store.Current.ToolsDisabled);

            store.Current.SetToolEnabled("bash", true);
            await store.SaveAsync(CancellationToken.None);
        }

        var preferences = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!["preferences"]);
        Assert.Equal(["web_search"], preferences["toolsDisabled"]!.AsArray().Select(node => node!.GetValue<string>()));

        await using (var store = new PreferencesStore(settingsPath))
        {
            await store.LoadAsync(CancellationToken.None);
            store.Current.SetToolEnabled("web_search", true);
            await store.SaveAsync(CancellationToken.None);
        }

        preferences = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!["preferences"]);
        Assert.False(preferences.ContainsKey("toolsDisabled"));
    }
}
