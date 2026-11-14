using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// The four CodeScript preferences of the tools section (wave W14): an out-of-range value lands on the documented
/// bound, a fresh or key-less store keeps the defaults, and the values survive the <c>preferences</c> section's JSON
/// round trip under their DTO key names — a key missing from the serialization is silent data loss.
/// </summary>
public sealed class CodeModePreferencesTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(999, 32)]
    public void Out_of_range_concurrency_is_clamped_to_the_bounds(int written, int expected)
    {
        Assert.Equal(expected, new AppPreferences { CodeScriptMaxConcurrentCalls = written }.CodeScriptMaxConcurrentCalls);
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(999999, 400000)]
    public void Out_of_range_result_chars_are_clamped_to_the_bounds(int written, int expected)
    {
        Assert.Equal(expected, new AppPreferences { CodeScriptResultChars = written }.CodeScriptResultChars);
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(999999, 400000)]
    public void Out_of_range_inner_result_chars_are_clamped_to_the_bounds(int written, int expected)
    {
        Assert.Equal(expected, new AppPreferences { CodeScriptInnerResultChars = written }.CodeScriptInnerResultChars);
    }

    [Fact]
    public void A_new_preferences_instance_keeps_the_documented_defaults()
    {
        var preferences = new AppPreferences();

        Assert.Equal(AppPreferences.DefaultCodeScriptMaxConcurrentCalls, preferences.CodeScriptMaxConcurrentCalls);
        Assert.Equal(AppPreferences.DefaultCodeScriptResultChars, preferences.CodeScriptResultChars);
        Assert.Equal(AppPreferences.DefaultCodeScriptInnerResultChars, preferences.CodeScriptInnerResultChars);
        Assert.Equal(AppPreferences.DefaultCodeScriptTools, preferences.CodeScriptTools);
    }

    [Fact]
    public void A_section_without_the_keys_loads_the_defaults()
    {
        var reloaded = new AppPreferences();
        reloaded.ApplyLoaded(new PreferencesSection(), UiLanguage.English);

        Assert.Equal(AppPreferences.DefaultCodeScriptMaxConcurrentCalls, reloaded.CodeScriptMaxConcurrentCalls);
        Assert.Equal(AppPreferences.DefaultCodeScriptResultChars, reloaded.CodeScriptResultChars);
        Assert.Equal(AppPreferences.DefaultCodeScriptInnerResultChars, reloaded.CodeScriptInnerResultChars);
        Assert.Equal(string.Empty, reloaded.CodeScriptTools);
    }

    [Fact]
    public async Task The_four_values_survive_the_json_round_trip_under_their_dto_keys()
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-code-mode-prefs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            await using (var store = new PreferencesStore(settingsPath))
            {
                await store.LoadAsync(CancellationToken.None);
                store.Current.CodeScriptMaxConcurrentCalls = 12;
                store.Current.CodeScriptResultChars = 12345;
                store.Current.CodeScriptInnerResultChars = 6789;
                store.Current.CodeScriptTools = "read, mcp__*";
                await store.SaveAsync(CancellationToken.None);
            }

            // The section really carries the four DTO keys — not just values that happen to survive a re-read.
            var json = await File.ReadAllTextAsync(settingsPath);
            Assert.Contains("\"codeScriptMaxConcurrentCalls\"", json, StringComparison.Ordinal);
            Assert.Contains("\"codeScriptResultChars\"", json, StringComparison.Ordinal);
            Assert.Contains("\"codeScriptInnerResultChars\"", json, StringComparison.Ordinal);
            Assert.Contains("\"codeScriptTools\"", json, StringComparison.Ordinal);

            await using var reloaded = new PreferencesStore(settingsPath);
            await reloaded.LoadAsync(CancellationToken.None);
            Assert.Null(reloaded.LoadProblem);
            Assert.Equal(12, reloaded.Current.CodeScriptMaxConcurrentCalls);
            Assert.Equal(12345, reloaded.Current.CodeScriptResultChars);
            Assert.Equal(6789, reloaded.Current.CodeScriptInnerResultChars);
            Assert.Equal("read, mcp__*", reloaded.Current.CodeScriptTools);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
