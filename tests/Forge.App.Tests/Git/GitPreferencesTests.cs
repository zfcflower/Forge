using System.Text.Json.Nodes;
using Forge.App.Git;
using Forge.App.Preferences;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins the git preferences contract (GitSettingsSectionViewModel docs): <c>gitPullStrategy</c> is a token
/// string "ff-only" (default) | "rebase" | "merge" with unknown tokens falling back to the default and the
/// setter case/whitespace tolerant; <c>gitNetworkTimeoutSeconds</c> is clamped to 10..600 with default 120.
/// Covers setter validation, the PreferencesStore round-trip (provider section and unknown fields preserved),
/// ApplyLoaded raising no notifications, and PanelSettings reading live values on every call.
/// </summary>
public sealed class GitPreferencesTests : IDisposable
{
    private readonly string directory;
    private readonly string settingsPath;

    public GitPreferencesTests()
    {
        directory = TestTempFiles.NewDirectory("forge-git-preferences-");
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

    // ------------------------------------------------------------ setter validation

    [Theory]
    [InlineData("ff-only", GitPullStrategy.FastForwardOnly)]
    [InlineData("rebase", GitPullStrategy.Rebase)]
    [InlineData("merge", GitPullStrategy.Merge)]
    public void ParseStrategy_maps_the_documented_tokens(string token, GitPullStrategy expected) =>
        Assert.Equal(expected, GitPreferences.ParseStrategy(token));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus")]
    [InlineData("fast-forward")]
    public void ParseStrategy_falls_back_to_the_default_for_unknown_tokens(string? token) =>
        Assert.Equal(GitPullStrategy.FastForwardOnly, GitPreferences.ParseStrategy(token));

    [Fact]
    public void StrategyToken_round_trips()
    {
        Assert.Equal(AppPreferences.GitPullStrategyFastForwardOnly, GitPreferences.StrategyToken(GitPullStrategy.FastForwardOnly));
        Assert.Equal(AppPreferences.GitPullStrategyRebase, GitPreferences.StrategyToken(GitPullStrategy.Rebase));
        Assert.Equal(AppPreferences.GitPullStrategyMerge, GitPreferences.StrategyToken(GitPullStrategy.Merge));
    }

    [Theory]
    [InlineData("REBASE", "rebase")]
    [InlineData(" Merge ", "merge")]
    [InlineData("FF-ONLY", "ff-only")]
    [InlineData("  ReBase  ", "rebase")]
    public void The_strategy_setter_is_case_and_whitespace_tolerant(string input, string expected)
    {
        var preferences = new AppPreferences { GitPullStrategy = input };

        Assert.Equal(expected, preferences.GitPullStrategy);
    }

    [Theory]
    [InlineData("bogus", "ff-only")]
    [InlineData("", "ff-only")]
    [InlineData("   ", "ff-only")]
    public void The_strategy_setter_falls_back_to_the_default_for_unknown_tokens(string input, string expected)
    {
        var preferences = new AppPreferences { GitPullStrategy = input };

        Assert.Equal(expected, preferences.GitPullStrategy);
    }

    [Fact]
    public void The_strategy_setter_tolerates_null()
    {
        var preferences = new AppPreferences { GitPullStrategy = null! };

        Assert.Equal(AppPreferences.DefaultGitPullStrategy, preferences.GitPullStrategy);
    }

    [Theory]
    [InlineData(5, 10)]
    [InlineData(9, 10)]
    [InlineData(10, 10)]
    [InlineData(45, 45)]
    [InlineData(600, 600)]
    [InlineData(601, 600)]
    [InlineData(100000, 600)]
    [InlineData(-30, 10)]
    public void The_timeout_setter_clamps_to_10_to_600(int requested, int expected)
    {
        var preferences = new AppPreferences { GitNetworkTimeoutSeconds = requested };

        Assert.Equal(expected, preferences.GitNetworkTimeoutSeconds);
    }

    [Fact]
    public void The_timeout_default_is_120() =>
        Assert.Equal(120, new AppPreferences().GitNetworkTimeoutSeconds);

    // ------------------------------------------------------------ store round-trip

    [Fact]
    public async Task Round_trip_keeps_the_provider_section_and_unknown_fields_with_git_values()
    {
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "provider": { "kind": "openai-compatible", "model": "model-x" },
              "unknown": { "nested": [1, 2], "flag": true },
              "preferences": { "gitPullStrategy": "rebase", "gitNetworkTimeoutSeconds": 45 }
            }
            """);

        await using (var store = new PreferencesStore(settingsPath))
        {
            await store.LoadAsync(CancellationToken.None);
            Assert.Null(store.LoadProblem);
            Assert.Equal("rebase", store.Current.GitPullStrategy);
            Assert.Equal(45, store.Current.GitNetworkTimeoutSeconds);

            store.Current.GitPullStrategy = "MERGE";
            store.Current.GitNetworkTimeoutSeconds = 5;
            await store.SaveAsync(CancellationToken.None);
        }

        var document = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(settingsPath)));
        Assert.Equal("openai-compatible", Assert.IsType<JsonObject>(document["provider"])["kind"]?.GetValue<string>());
        Assert.True(Assert.IsType<JsonObject>(document["unknown"])["flag"]?.GetValue<bool>());
        var preferences = Assert.IsType<JsonObject>(document["preferences"]);
        Assert.Equal("merge", preferences["gitPullStrategy"]?.GetValue<string>());
        Assert.Equal(10, preferences["gitNetworkTimeoutSeconds"]?.GetValue<int>());
    }

    [Fact]
    public async Task Unknown_strategy_token_in_the_file_falls_back_to_the_default()
    {
        await File.WriteAllTextAsync(
            settingsPath,
            """{ "preferences": { "gitPullStrategy": "legendary", "gitNetworkTimeoutSeconds": 9999 } }""");

        await using var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);

        Assert.Null(store.LoadProblem);
        Assert.Equal(AppPreferences.DefaultGitPullStrategy, store.Current.GitPullStrategy);
        Assert.Equal(AppPreferences.MaxGitNetworkTimeoutSeconds, store.Current.GitNetworkTimeoutSeconds);
    }

    [Fact]
    public async Task Missing_git_values_load_as_defaults()
    {
        await File.WriteAllTextAsync(settingsPath, """{ "preferences": { "uiFontSize": 14 } }""");

        await using var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);

        Assert.Null(store.LoadProblem);
        Assert.Equal(AppPreferences.DefaultGitPullStrategy, store.Current.GitPullStrategy);
        Assert.Equal(AppPreferences.DefaultGitNetworkTimeoutSeconds, store.Current.GitNetworkTimeoutSeconds);
    }

    // ------------------------------------------------------------ ApplyLoaded and live settings

    [Fact]
    public void ApplyLoaded_applies_git_values_without_raising_PropertyChanged()
    {
        var preferences = new AppPreferences();
        var raised = PropertyChangeLog.For(preferences);

        preferences.ApplyLoaded(
            new PreferencesSection { GitPullStrategy = "rebase", GitNetworkTimeoutSeconds = 33 },
            UiLanguage.English);

        Assert.Empty(raised);
        Assert.Equal("rebase", preferences.GitPullStrategy);
        Assert.Equal(33, preferences.GitNetworkTimeoutSeconds);
    }

    [Fact]
    public void ApplyLoaded_validates_git_values_like_the_setters()
    {
        var preferences = new AppPreferences();
        preferences.ApplyLoaded(
            new PreferencesSection { GitPullStrategy = "REBASE", GitNetworkTimeoutSeconds = 4 },
            UiLanguage.English);

        Assert.Equal("rebase", preferences.GitPullStrategy);
        Assert.Equal(AppPreferences.MinGitNetworkTimeoutSeconds, preferences.GitNetworkTimeoutSeconds);
    }

    [Fact]
    public void PanelSettings_reads_live_values_on_every_call()
    {
        var preferences = new AppPreferences();
        var settings = GitPreferences.PanelSettings(preferences);

        Assert.Equal(GitPullStrategy.FastForwardOnly, settings.PullStrategy());
        Assert.Equal(TimeSpan.FromSeconds(120), settings.NetworkTimeout());

        preferences.GitPullStrategy = "rebase";
        preferences.GitNetworkTimeoutSeconds = 45;

        Assert.Equal(GitPullStrategy.Rebase, settings.PullStrategy());
        Assert.Equal(TimeSpan.FromSeconds(45), settings.NetworkTimeout());
    }
}
