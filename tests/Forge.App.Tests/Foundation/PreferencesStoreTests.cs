using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Foundation;

/// <summary>
/// Pins the preferences contract (forge-app spec, "偏好、文案与设置分区"): the store owns the <c>preferences</c> section of
/// <c>settings.json</c> and keeps every other section, a corrupt file is never overwritten silently (it is backed up
/// once, before the first write), a burst of edits becomes one debounced write, and nothing is written unless the user
/// changed something.
/// </summary>
public sealed class PreferencesStoreTests : IDisposable
{
    /// <summary>The rest of the document, including a section no app code knows about.</summary>
    private const string OtherSections = """
        {
          "provider": {
            "kind": "openai-compatible",
            "baseUrl": "https://example.test/v1",
            "model": "model-x",
            "apiKeyEnv": "FORGE_KEY"
          },
          "unknown": { "nested": [1, 2, 3], "flag": true }
        }
        """;

    /// <summary>Long enough to be inside the debounce window, short enough to keep the test quick.</summary>
    private const int InsideTheDebounceWindowMilliseconds = 150;

    /// <summary>Twice the window, so a stray second write would have landed before the count is pinned.</summary>
    private const int AfterTheDebounceWindowMilliseconds = 700;

    private const int PollIntervalMilliseconds = 20;
    private static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(10);

    private readonly string directory;
    private readonly string settingsPath;

    public PreferencesStoreTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "forge-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        settingsPath = Path.Combine(directory, "settings.json");
    }

    private string BackupPath => settingsPath + ".bak";

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
    public async Task Round_trip_keeps_every_other_section()
    {
        await File.WriteAllTextAsync(settingsPath, OtherSections);

        await using (var store = new PreferencesStore(settingsPath))
        {
            await store.LoadAsync(CancellationToken.None);
            Assert.Null(store.LoadProblem);

            store.Current.UiFontFamily = "Georgia, serif";
            store.Current.UiFontSize = 15.5;
            store.Current.CodeFontFamily = "Fira Code";
            store.Current.CodeFontSize = 12;
            store.Current.Language = UiLanguage.Chinese;
            store.Current.TerminalShell = "pwsh";
            store.Current.TerminalArguments = "-NoLogo -NoProfile";

            await store.SaveAsync(CancellationToken.None);
            Assert.Equal(1, store.SaveCount);
        }

        var document = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(settingsPath)));
        var provider = Assert.IsType<JsonObject>(document["provider"]);
        Assert.Equal("openai-compatible", provider["kind"]?.GetValue<string>());
        Assert.Equal("https://example.test/v1", provider["baseUrl"]?.GetValue<string>());
        Assert.Equal("model-x", provider["model"]?.GetValue<string>());
        Assert.Equal("FORGE_KEY", provider["apiKeyEnv"]?.GetValue<string>());

        var unknown = Assert.IsType<JsonObject>(document["unknown"]);
        Assert.True(unknown["flag"]?.GetValue<bool>());
        Assert.Equal(3, Assert.IsType<JsonArray>(unknown["nested"]).Count);

        await using var reloaded = new PreferencesStore(settingsPath);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.Null(reloaded.LoadProblem);
        Assert.Equal("Georgia, serif", reloaded.Current.UiFontFamily);
        Assert.Equal(15.5, reloaded.Current.UiFontSize);
        Assert.Equal("Fira Code", reloaded.Current.CodeFontFamily);
        Assert.Equal(12, reloaded.Current.CodeFontSize);
        Assert.Equal(UiLanguage.Chinese, reloaded.Current.Language);
        Assert.Equal("pwsh", reloaded.Current.TerminalShell);
        Assert.Equal("-NoLogo -NoProfile", reloaded.Current.TerminalArguments);
    }

    [Fact]
    public async Task Missing_file_yields_defaults_and_writes_nothing()
    {
        var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);

        Assert.Null(store.LoadProblem);
        Assert.Equal(AppPreferences.DefaultUiFontFamily, store.Current.UiFontFamily);
        Assert.Equal(AppPreferences.DefaultCodeFontSize, store.Current.CodeFontSize);
        Assert.Equal(UiLanguage.System, store.Current.Language);

        await store.DisposeAsync();

        Assert.False(File.Exists(settingsPath));
        Assert.False(File.Exists(BackupPath));
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task An_existing_file_is_untouched_when_nothing_changed()
    {
        var original = Encoding.UTF8.GetBytes(OtherSections);
        await File.WriteAllBytesAsync(settingsPath, original);

        var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);
        await store.DisposeAsync();

        Assert.Equal(original, await File.ReadAllBytesAsync(settingsPath));
        Assert.False(File.Exists(BackupPath));
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task A_corrupt_file_yields_defaults_and_is_not_overwritten_by_the_load()
    {
        var corrupt = Encoding.UTF8.GetBytes("{ this is not json");
        await File.WriteAllBytesAsync(settingsPath, corrupt);

        await using var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);

        Assert.NotNull(store.LoadProblem);
        Assert.Equal(AppPreferences.DefaultUiFontSize, store.Current.UiFontSize);
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(settingsPath));
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public async Task A_corrupt_file_is_backed_up_once_before_the_first_write()
    {
        var corrupt = Encoding.UTF8.GetBytes("{ this is not json");
        await File.WriteAllBytesAsync(settingsPath, corrupt);

        await using var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);
        store.Current.UiFontSize = 16;
        await store.SaveAsync(CancellationToken.None);

        Assert.Equal(corrupt, await File.ReadAllBytesAsync(BackupPath));
        var written = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(settingsPath)));
        Assert.Equal(16, Assert.IsType<JsonObject>(written["preferences"])["uiFontSize"]?.GetValue<double>());

        // One backup only: the next edit replaces the already-corrected file without touching it again.
        File.Delete(BackupPath);
        store.Current.UiFontSize = 17;
        await store.SaveAsync(CancellationToken.None);

        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public async Task Stored_values_are_clamped_and_an_empty_family_falls_back_to_the_default()
    {
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "preferences": {
                "uiFontSize": 99,
                "codeFontSize": 1,
                "uiFontFamily": "   ",
                "codeFontFamily": "Fira Code"
              }
            }
            """);

        await using var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);

        Assert.Null(store.LoadProblem);
        Assert.Equal(AppPreferences.MaxFontSize, store.Current.UiFontSize);
        Assert.Equal(AppPreferences.MinFontSize, store.Current.CodeFontSize);
        Assert.Equal(AppPreferences.DefaultUiFontFamily, store.Current.UiFontFamily);
        Assert.Equal("Fira Code", store.Current.CodeFontFamily);
    }

    [Theory]
    [InlineData(1000, AppPreferences.MaxFontSize)]
    [InlineData(-5, AppPreferences.MinFontSize)]
    [InlineData(13.5, 13.5)]
    public void The_size_setters_clamp(double requested, double expected)
    {
        var preferences = new AppPreferences { UiFontSize = requested, CodeFontSize = requested };

        Assert.Equal(expected, preferences.UiFontSize);
        Assert.Equal(expected, preferences.CodeFontSize);
    }

    [Fact]
    public void The_family_setters_fall_back_to_the_default()
    {
        var preferences = new AppPreferences
        {
            UiFontFamily = "   ",
            CodeFontFamily = string.Empty,
            UiFontSize = double.NaN,
        };

        Assert.Equal(AppPreferences.DefaultUiFontFamily, preferences.UiFontFamily);
        Assert.Equal(AppPreferences.DefaultCodeFontFamily, preferences.CodeFontFamily);
        Assert.Equal(AppPreferences.DefaultUiFontSize, preferences.UiFontSize);

        preferences.UiFontFamily = "  Georgia , serif  ";
        Assert.Equal("Georgia , serif", preferences.UiFontFamily);
    }

    [Fact]
    public async Task A_burst_of_edits_becomes_one_write()
    {
        await using var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);

        store.Current.UiFontSize = 12;
        store.Current.UiFontSize = 18;
        store.Current.CodeFontSize = 14;
        store.Current.UiFontSize = 24;

        await Task.Delay(InsideTheDebounceWindowMilliseconds);
        Assert.Equal(0, store.SaveCount);
        Assert.False(File.Exists(settingsPath));

        await WaitForSavesAsync(store, 1);

        // Give a stray second write the chance to land before the count is pinned.
        await Task.Delay(AfterTheDebounceWindowMilliseconds);
        Assert.Equal(1, store.SaveCount);

        var document = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(settingsPath)));
        var preferences = Assert.IsType<JsonObject>(document["preferences"]);
        Assert.Equal(24, preferences["uiFontSize"]?.GetValue<double>());
        Assert.Equal(14, preferences["codeFontSize"]?.GetValue<double>());
    }

    [Fact]
    public async Task Dispose_writes_the_pending_edit()
    {
        var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);

        store.Current.TerminalShell = "zsh";
        await store.DisposeAsync();

        Assert.Equal(1, store.SaveCount);
        var document = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(settingsPath)));
        Assert.Equal("zsh", Assert.IsType<JsonObject>(document["preferences"])["terminalShell"]?.GetValue<string>());
    }

    [Fact]
    public async Task Dispose_twice_writes_once()
    {
        var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);
        store.Current.UiFontSize = 20;

        await store.DisposeAsync();
        await store.DisposeAsync();

        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task Saving_after_dispose_throws()
    {
        var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);
        await store.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.SaveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_save_leaves_the_file_untouched()
    {
        var original = Encoding.UTF8.GetBytes(OtherSections);
        await File.WriteAllBytesAsync(settingsPath, original);

        await using var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);
        store.Current.UiFontSize = 20;

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(cancelled.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(settingsPath));
    }

    [Fact]
    public async Task An_unreadable_preferences_section_reports_the_problem_and_keeps_the_defaults()
    {
        await File.WriteAllTextAsync(settingsPath, """{ "preferences": { "language": "klingon", "uiFontSize": 14 } }""");

        await using var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);

        Assert.NotNull(store.LoadProblem);
        Assert.Equal(UiLanguage.System, store.Current.Language);
        Assert.Equal(14, store.Current.UiFontSize);
    }

    [Fact]
    public async Task A_preferences_section_that_is_not_an_object_reports_the_problem()
    {
        await File.WriteAllTextAsync(settingsPath, """{ "preferences": 42 }""");

        await using var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);

        Assert.NotNull(store.LoadProblem);
        Assert.Equal(AppPreferences.DefaultUiFontSize, store.Current.UiFontSize);
    }

    /// <summary>The write runs on the pool, so the test polls instead of asserting on a fixed delay.</summary>
    private static async Task WaitForSavesAsync(PreferencesStore store, int expected)
    {
        var stopwatch = Stopwatch.StartNew();
        while (store.SaveCount < expected)
        {
            Assert.True(stopwatch.Elapsed < PollBudget, "the debounced write did not happen");
            await Task.Delay(PollIntervalMilliseconds);
        }
    }
}
