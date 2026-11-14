using System.Text.Json.Nodes;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Preferences;

/// <summary>
/// Pins the "reduce motion" preference (forge-app spec, "聊天时间轴(W13)"): it defaults to off, its setter announces the
/// change, it round-trips through the <c>reduceMotion</c> key of the <c>preferences</c> section of <c>settings.json</c>,
/// and a file without the key keeps the default.
/// </summary>
public sealed class ReduceMotionPreferenceTests : IDisposable
{
    private readonly string directory;
    private readonly string settingsPath;

    public ReduceMotionPreferenceTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "forge-reduce-motion-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void Reduce_motion_is_off_by_default()
    {
        Assert.False(AppPreferences.DefaultReduceMotion);
        Assert.False(new AppPreferences().ReduceMotion);
    }

    [Fact]
    public void The_setter_announces_the_change()
    {
        var preferences = new AppPreferences();
        var changed = new List<string?>();
        preferences.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        preferences.ReduceMotion = true;

        Assert.True(preferences.ReduceMotion);
        Assert.Contains(nameof(AppPreferences.ReduceMotion), changed);
    }

    [Fact]
    public async Task Reduce_motion_round_trips_through_the_settings_file()
    {
        await File.WriteAllTextAsync(settingsPath, """{ "preferences": { "reduceMotion": true } }""");

        await using var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);
        Assert.Null(store.LoadProblem);
        Assert.True(store.Current.ReduceMotion);

        store.Current.ReduceMotion = false;
        await store.SaveAsync(CancellationToken.None);

        var document = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(settingsPath)));
        var section = Assert.IsType<JsonObject>(document["preferences"]);
        Assert.False(section["reduceMotion"]?.GetValue<bool>());

        await using var reloaded = new PreferencesStore(settingsPath);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.Null(reloaded.LoadProblem);
        Assert.False(reloaded.Current.ReduceMotion);
    }

    [Fact]
    public async Task A_file_without_the_key_keeps_the_default()
    {
        await File.WriteAllTextAsync(settingsPath, """{ "preferences": { "uiFontSize": 14 } }""");

        await using var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);

        Assert.Null(store.LoadProblem);
        Assert.Equal(AppPreferences.DefaultReduceMotion, store.Current.ReduceMotion);
        Assert.False(store.Current.ReduceMotion);
    }
}
