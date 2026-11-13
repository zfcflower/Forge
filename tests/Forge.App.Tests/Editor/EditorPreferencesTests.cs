using System.Text.Json.Nodes;
using Forge.App.Editing;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Editor;

/// <summary>
/// Pins the editor preferences contract: every editor option is optional in <c>settings.json</c> (a missing value keeps
/// its default), values are validated on the way in, and a round-trip through the store keeps the other sections.
/// </summary>
public sealed class EditorPreferencesTests : IDisposable
{
    private readonly string directory;
    private readonly string settingsPath;

    public EditorPreferencesTests()
    {
        directory = TestTempFiles.NewDirectory("forge-editor-preferences-");
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
    public void A_fresh_preferences_object_has_the_documented_editor_defaults()
    {
        var preferences = new AppPreferences();

        Assert.True(preferences.EditorShowLineNumbers);
        Assert.False(preferences.EditorWordWrap);
        Assert.False(preferences.EditorShowSpaces);
        Assert.False(preferences.EditorShowTabs);
        Assert.False(preferences.EditorShowEndOfLine);
        Assert.False(preferences.EditorShowControlCharacters);
        Assert.True(preferences.EditorScrollBelowDocument);
        Assert.True(preferences.EditorHighlightCurrentLine);
        Assert.True(preferences.EditorHyperlinks);
        Assert.True(preferences.EditorAutoFolding);
        Assert.Equal(4, preferences.EditorIndentationSize);
        Assert.True(preferences.EditorTabsToSpaces);
        Assert.Equal(string.Empty, preferences.EditorColumnRulers);
        Assert.Equal(AppPreferences.EditorThemeAuto, preferences.EditorTheme);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(9, 8)]
    [InlineData(3, 3)]
    public void The_indentation_size_is_clamped(int value, int expected)
    {
        var preferences = new AppPreferences { EditorIndentationSize = value };

        Assert.Equal(expected, preferences.EditorIndentationSize);
    }

    [Theory]
    [InlineData("80", "80")]
    [InlineData("120, 80 ,80", "120, 80 ,80")]
    [InlineData("  72,96 ", "72,96")]
    [InlineData(null, "")]
    public void The_ruler_text_is_stored_as_typed_but_parsed_strictly(string? value, string stored)
    {
        var preferences = new AppPreferences { EditorColumnRulers = value! };

        Assert.Equal(stored, preferences.EditorColumnRulers);
    }

    [Theory]
    [InlineData("80", new[] { 80 })]
    [InlineData("120, 80 ,80", new[] { 80, 120 })]
    [InlineData("80;120", new[] { 80, 120 })]
    [InlineData("0, 401, 80", new[] { 80 })]
    [InlineData("abc", new int[0])]
    [InlineData("", new int[0])]
    [InlineData(null, new int[0])]
    public void The_editor_parses_only_the_columns_a_ruler_may_use(string? value, int[] expected) =>
        Assert.Equal(expected, AppPreferences.ParseRulers(value));

    [Theory]
    [InlineData("light", AppPreferences.EditorThemeLight)]
    [InlineData(" DARK ", AppPreferences.EditorThemeDark)]
    [InlineData("auto", AppPreferences.EditorThemeAuto)]
    [InlineData("solarized", AppPreferences.EditorThemeAuto)]
    [InlineData(null, AppPreferences.EditorThemeAuto)]
    public void An_unknown_theme_token_falls_back_to_following_the_ui(string? value, string expected) =>
        Assert.Equal(expected, new AppPreferences { EditorTheme = value! }.EditorTheme);

    [Fact]
    public async Task The_editor_section_round_trips_and_keeps_the_other_sections()
    {
        await File.WriteAllTextAsync(settingsPath, """
            {
              "provider": { "name": "openai" },
              "custom": { "keep": true }
            }
            """);
        var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);
        var preferences = store.Current;

        preferences.EditorShowSpaces = true;
        preferences.EditorShowEndOfLine = true;
        preferences.EditorIndentationSize = 2;
        preferences.EditorColumnRulers = "100, 90";
        preferences.EditorTheme = AppPreferences.EditorThemeDark;
        preferences.EditorHyperlinks = false;
        await store.SaveAsync(CancellationToken.None);
        await store.DisposeAsync();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
        Assert.NotNull(root["provider"]);
        Assert.NotNull(root["custom"]);
        var section = root["preferences"]!.AsObject();
        Assert.True(section["editorShowSpaces"]!.GetValue<bool>());
        Assert.True(section["editorShowEndOfLine"]!.GetValue<bool>());
        Assert.Equal(2, section["editorIndentationSize"]!.GetValue<int>());
        Assert.Equal("100, 90", section["editorColumnRulers"]!.GetValue<string>());
        Assert.Equal("dark", section["editorTheme"]!.GetValue<string>());
        Assert.False(section["editorHyperlinks"]!.GetValue<bool>());

        var reloaded = new PreferencesStore(settingsPath);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.True(reloaded.Current.EditorShowSpaces);
        Assert.True(reloaded.Current.EditorShowEndOfLine);
        Assert.Equal(2, reloaded.Current.EditorIndentationSize);
        Assert.Equal("100, 90", reloaded.Current.EditorColumnRulers);
        Assert.Equal(AppPreferences.EditorThemeDark, reloaded.Current.EditorTheme);
        Assert.False(reloaded.Current.EditorHyperlinks);
        Assert.True(reloaded.Current.EditorShowLineNumbers);
        await reloaded.DisposeAsync();
    }

    [Fact]
    public async Task A_hand_written_file_with_invalid_editor_values_loads_with_the_clamped_ones()
    {
        await File.WriteAllTextAsync(settingsPath, """
            {
              "preferences": {
                "editorIndentationSize": 42,
                "editorColumnRulers": "80,oops,80",
                "editorTheme": "neon",
                "editorShowTabs": true
              }
            }
            """);
        var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);

        Assert.Equal(8, store.Current.EditorIndentationSize);
        Assert.Equal("80,oops,80", store.Current.EditorColumnRulers);
        Assert.Equal([80], AppPreferences.ParseRulers(store.Current.EditorColumnRulers));
        Assert.Equal(AppPreferences.EditorThemeAuto, store.Current.EditorTheme);
        Assert.True(store.Current.EditorShowTabs);
        Assert.True(store.Current.EditorShowLineNumbers);
        await store.DisposeAsync();
    }

    [Fact]
    public void The_settings_object_projects_the_preferences_and_writes_through()
    {
        var preferences = new AppPreferences();
        var settings = new EditorSettings(preferences);

        Assert.True(settings.ShowLineNumbers);
        settings.ShowSpaces = true;
        settings.IndentationSize = 6;
        settings.ColumnRulers = "72, 80";
        settings.ThemeIndex = 2;

        Assert.True(preferences.EditorShowSpaces);
        Assert.Equal(6, preferences.EditorIndentationSize);
        Assert.Equal("72, 80", preferences.EditorColumnRulers);
        Assert.Equal(AppPreferences.EditorThemeDark, preferences.EditorTheme);
        Assert.True(settings.IsDarkTheme(uiIsDark: false));
        Assert.False(new EditorSettings { ThemeIndex = 1 }.IsDarkTheme(uiIsDark: true));
        Assert.True(new EditorSettings().IsDarkTheme(uiIsDark: true));
    }

    [Fact]
    public void A_settings_change_reaches_a_listener()
    {
        var settings = new EditorSettings();
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.ShowTabs = true;
        settings.ShowTabs = true;

        Assert.Equal([nameof(EditorSettings.ShowTabs)], changed);
    }
}
