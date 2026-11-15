using System.Text.Json.Nodes;
using Avalonia.Input;
using Forge.App.Input;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Keymap;

/// <summary>
/// The stored keymap overrides (W10e): the <c>actionId|gesture</c> shape normalizes on write, round-trips through the
/// <c>keymapOverrides</c> key of the <c>preferences</c> section of <c>settings.json</c>, and drives
/// <see cref="KeymapApplier"/> so a stored override wins over the catalog default.
/// </summary>
public sealed class KeymapOverridesTests : IDisposable
{
    private readonly string directory = TestTempFiles.NewDirectory("forge-keymap-");

    public void Dispose()
    {
        KeymapApplier.Unbind();
        TestTempFiles.DeleteQuietly(directory);
    }

    private string SettingsPath => Path.Combine(directory, "settings.json");

    [Fact]
    public void Keymap_overrides_are_empty_by_default()
    {
        Assert.Empty(new AppPreferences().KeymapOverrides);
    }

    /// <summary>
    /// The preference is a set, not a sequence: the setter sorts the entries, so the same overrides in another order are the
    /// same value and raise no <c>PropertyChanged</c>. Without that gate a page that rebuilds the list from a dictionary
    /// iteration order would dirty <c>settings.json</c> on every repaint.
    /// </summary>
    [Fact]
    public void Reordering_the_same_entries_is_not_a_change()
    {
        var preferences = new AppPreferences();
        preferences.KeymapOverrides = ["window.newTerminal|Ctrl+T", "view.saveDocument|Ctrl+U"];

        var changed = new List<string?>();
        preferences.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        preferences.KeymapOverrides = ["view.saveDocument|Ctrl+U", "window.newTerminal|Ctrl+T"];

        Assert.Empty(changed);
        Assert.Equal(["view.saveDocument|Ctrl+U", "window.newTerminal|Ctrl+T"], preferences.KeymapOverrides);
    }

    /// <summary>
    /// Only the shape is validated here (one separator, both sides non-empty): a half-written hand edit must not survive as an
    /// entry nothing can ever apply. The gesture syntax itself is the keymap catalog's business.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("view.saveDocument")]
    [InlineData("|Ctrl+U")]
    [InlineData("view.saveDocument|")]
    [InlineData("view.saveDocument|   ")]
    [InlineData("   |Ctrl+U")]
    public void A_malformed_entry_is_dropped_instead_of_stored(string entry)
    {
        var preferences = new AppPreferences();
        preferences.KeymapOverrides = [entry];
        Assert.Empty(preferences.KeymapOverrides);
    }

    /// <summary>
    /// The last entry for an action wins even when its gesture cannot be parsed: the preference keeps what the user last chose
    /// and <see cref="KeymapApplier"/> decides that an unparsable gesture means the catalog default. Keeping the first entry
    /// instead would make a row that shows the default un-resettable without visiting the file.
    /// </summary>
    [Fact]
    public void The_last_entry_for_an_action_wins_even_when_its_gesture_cannot_parse()
    {
        var preferences = new AppPreferences();
        preferences.KeymapOverrides = ["view.saveDocument|Ctrl+U", "view.saveDocument|Ctrl+`"];

        Assert.Equal(["view.saveDocument|Ctrl+`"], preferences.KeymapOverrides);
    }

    [Fact]
    public void The_setter_keeps_one_entry_per_action_and_announces_the_change()
    {
        var preferences = new AppPreferences();
        var changed = new List<string?>();
        preferences.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        preferences.KeymapOverrides =
        [
            "view.saveDocument|Ctrl+U",
            "  view.saveDocument | Ctrl+Shift+U  ",
            "window.newTerminal|Ctrl+`",
        ];

        Assert.Equal(["view.saveDocument|Ctrl+Shift+U", "window.newTerminal|Ctrl+`"], preferences.KeymapOverrides);
        Assert.Contains(nameof(AppPreferences.KeymapOverrides), changed);
    }

    [Fact]
    public async Task Keymap_overrides_round_trip_through_the_settings_file()
    {
        await File.WriteAllTextAsync(SettingsPath, """{ "preferences": { "keymapOverrides": ["view.saveDocument|Ctrl+U"] } }""");

        await using var store = new PreferencesStore(SettingsPath);
        await store.LoadAsync(CancellationToken.None);
        Assert.Null(store.LoadProblem);
        Assert.Equal(["view.saveDocument|Ctrl+U"], store.Current.KeymapOverrides);

        store.Current.KeymapOverrides = ["view.nextUnviewedChange|Shift+J"];
        await store.SaveAsync(CancellationToken.None);

        var document = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath)));
        var section = Assert.IsType<JsonObject>(document["preferences"]);
        Assert.Equal(["view.nextUnviewedChange|Shift+J"], section["keymapOverrides"]!.AsArray().Select(static node => node!.GetValue<string>()));

        await using var reloaded = new PreferencesStore(SettingsPath);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.Null(reloaded.LoadProblem);
        Assert.Equal(["view.nextUnviewedChange|Shift+J"], reloaded.Current.KeymapOverrides);
    }

    [Fact]
    public void The_applier_prefers_a_stored_override_and_falls_back_to_the_default()
    {
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        try
        {
            Assert.Equal(new KeyGesture(Key.S, KeyModifiers.Control), KeymapApplier.Gesture(KeymapActionIds.SaveDocument));

            preferences.KeymapOverrides = ["view.saveDocument|Ctrl+U"];
            Assert.Equal(new KeyGesture(Key.U, KeyModifiers.Control), KeymapApplier.Gesture(KeymapActionIds.SaveDocument));

            // A hand-written entry whose gesture cannot be parsed falls back to the default.
            preferences.KeymapOverrides = ["view.saveDocument|Ctrl+`"];
            Assert.Equal(new KeyGesture(Key.S, KeyModifiers.Control), KeymapApplier.Gesture(KeymapActionIds.SaveDocument));
        }
        finally
        {
            KeymapApplier.Unbind();
        }
    }

    [Fact]
    public void Display_gesture_follows_the_override()
    {
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        try
        {
            Assert.Equal("Ctrl+S", KeymapApplier.DisplayGesture(KeymapActionIds.SaveDocument));
            preferences.KeymapOverrides = ["view.saveDocument|Ctrl+U"];
            Assert.Equal("Ctrl+U", KeymapApplier.DisplayGesture(KeymapActionIds.SaveDocument));
        }
        finally
        {
            KeymapApplier.Unbind();
        }
    }

    /// <summary>
    /// An empty override table is the shipped state and has to mean "every action uses its catalog default" — the resolved map
    /// covers the whole catalog and every value is the catalog value, not an empty gesture.
    /// </summary>
    [Fact]
    public void An_empty_override_table_resolves_to_the_catalog_defaults()
    {
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        try
        {
            preferences.KeymapOverrides =
            [
                "editor.undo|Ctrl+Alt+Z",
                "view.saveDocument|Ctrl+U",
                "window.newTerminal|Ctrl+T",
            ];
            Assert.Equal(new KeyGesture(Key.U, KeyModifiers.Control), KeymapApplier.Gesture(KeymapActionIds.SaveDocument));

            preferences.KeymapOverrides = [];

            var resolved = KeymapApplier.ResolveAll();
            Assert.Equal(KeymapCatalog.Actions.Count, resolved.Count);
            foreach (var action in KeymapCatalog.Actions)
            {
                Assert.Equal(action.DefaultGesture, resolved[action.Id]);
            }
        }
        finally
        {
            KeymapApplier.Unbind();
        }
    }
}
