using System.Text.Json.Nodes;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Settings;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// <see cref="CompactionSettingsStore"/> owns the <c>compaction</c> section of <c>settings.json</c>; the settings page edits
/// it through <see cref="CompactionSettingsViewModel"/>, which validates inline and saves only valid percentages.
/// </summary>
public sealed class CompactionSettingsStoreTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-compaction-settings-");


    private string SettingsPath => Path.Combine(root, "settings.json");

    [Fact]
    public void A_missing_file_reads_as_the_defaults()
    {
        var store = new CompactionSettingsStore(SettingsPath);

        Assert.Equal(new CompactionSettings(true, 85, 50), store.Current);
    }

    [Fact]
    public void A_damaged_file_reads_as_the_defaults_and_is_not_overwritten()
    {
        File.WriteAllText(SettingsPath, "{ not json");
        var store = new CompactionSettingsStore(SettingsPath);

        Assert.Equal(CompactionSettings.Default, store.Current);
        Assert.ThrowsAny<Exception>(() => store.Save(new CompactionSettings(false, 80, 40)));
        Assert.Equal("{ not json", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Saved_values_round_trip_and_other_sections_survive()
    {
        File.WriteAllText(SettingsPath, """{"providers":{"default":"alpha"},"compaction":{"note":"kept"}}""");
        var store = new CompactionSettingsStore(SettingsPath);
        var changed = 0;
        store.Changed += (_, _) => changed++;

        store.Save(new CompactionSettings(false, 90, 30));

        Assert.Equal(1, changed);
        Assert.Equal(new CompactionSettings(false, 90, 30), new CompactionSettingsStore(SettingsPath).Current);
        var saved = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.Equal("alpha", (string?)saved["providers"]!["default"]);
        Assert.Equal("kept", (string?)saved["compaction"]!["note"]);
        Assert.False((bool)saved["compaction"]!["enabled"]!);
        Assert.Equal(90, (int)saved["compaction"]!["triggerPercent"]!);
        Assert.Equal(30, (int)saved["compaction"]!["targetPercent"]!);
    }

    [Theory]
    [InlineData(49, 30)]
    [InlineData(96, 30)]
    [InlineData(85, 85)]
    [InlineData(85, 90)]
    [InlineData(85, 0)]
    public void Out_of_range_values_are_refused_on_save(int trigger, int target)
    {
        var store = new CompactionSettingsStore(SettingsPath);

        Assert.Throws<ArgumentException>(() => store.Save(new CompactionSettings(true, trigger, target)));
        Assert.False(File.Exists(SettingsPath));
    }

    [Theory]
    [InlineData("""{"enabled":false,"triggerPercent":99,"targetPercent":50}""", false)]
    [InlineData("""{"enabled":false,"triggerPercent":60,"targetPercent":70}""", false)]
    [InlineData("""{"triggerPercent":"high"}""", true)]
    public void Invalid_percentages_in_the_file_read_as_the_defaults_keeping_enabled(string section, bool enabled)
    {
        File.WriteAllText(SettingsPath, "{\"compaction\":" + section + "}");

        var current = new CompactionSettingsStore(SettingsPath).Current;

        Assert.Equal(new CompactionSettings(enabled, 85, 50), current);
    }

    [Fact]
    public void Whole_number_doubles_are_accepted()
    {
        File.WriteAllText(SettingsPath, """{"compaction":{"triggerPercent":70.0,"targetPercent":35}}""");

        Assert.Equal(new CompactionSettings(true, 70, 35), new CompactionSettingsStore(SettingsPath).Current);
    }

    [Fact]
    public void The_policy_carries_the_ratios_and_resolvers()
    {
        Func<ModelSettings, int?> window = _ => 200_000;
        var policy = new CompactionSettings(true, 90, 40).ToPolicy(window, null);

        Assert.True(policy.Enabled);
        Assert.Equal(0.9, policy.TriggerRatio, 6);
        Assert.Equal(0.4, policy.TargetRatio, 6);
        Assert.Same(window, policy.ContextWindow);
        policy.Validate();

        var fallback = new CompactionSettings(false, 99, 120).ToPolicy(window, null);
        Assert.False(fallback.Enabled);
        Assert.Equal(0.85, fallback.TriggerRatio, 6);
        Assert.Equal(0.5, fallback.TargetRatio, 6);
    }

    [Fact]
    public void The_view_model_validates_inline_and_saves_only_valid_values()
    {
        var store = new CompactionSettingsStore(SettingsPath);
        using var model = new CompactionSettingsViewModel(store);
        model.Load();

        Assert.Equal("85", model.TriggerText);
        Assert.Equal("50", model.TargetText);
        Assert.False(model.HasTriggerError);
        Assert.False(model.HasTargetError);

        model.TriggerText = "97";
        Assert.True(model.HasTriggerError);
        Assert.False(model.Commit());
        Assert.False(File.Exists(SettingsPath));

        model.TriggerText = "60";
        model.TargetText = "60";
        Assert.False(model.HasTriggerError);
        Assert.True(model.HasTargetError);
        Assert.False(model.Commit());

        model.TargetText = "abc";
        Assert.True(model.HasTargetError);

        model.TargetText = "20";
        Assert.False(model.HasTargetError);
        Assert.True(model.Commit());
        Assert.Equal(new CompactionSettings(true, 60, 20), new CompactionSettingsStore(SettingsPath).Current);

        model.Enabled = false;
        Assert.False(new CompactionSettingsStore(SettingsPath).Current.Enabled);
    }

    [Fact]
    public void The_view_model_reload_shows_the_file_again()
    {
        var store = new CompactionSettingsStore(SettingsPath);
        using var model = new CompactionSettingsViewModel(store);
        model.Load();
        File.WriteAllText(SettingsPath, """{"compaction":{"enabled":false,"triggerPercent":75,"targetPercent":25}}""");

        model.Reload();

        Assert.False(model.Enabled);
        Assert.Equal("75", model.TriggerText);
        Assert.Equal("25", model.TargetText);
        Assert.False(File.ReadAllText(SettingsPath).Contains("\n", StringComparison.Ordinal)); // loading did not re-save
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
