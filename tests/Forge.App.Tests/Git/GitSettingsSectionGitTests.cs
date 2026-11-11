using System.Text.Json.Nodes;
using Forge.App.Git;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins the settings the pull request and wait features add to the <c>git</c> section: <c>gitGhPath</c> (an executable
/// path, trimmed, empty meaning <c>gh</c> from PATH), <c>gitChecksPollSeconds</c> (clamped to
/// 10..300), <c>gitChecksMaxWaitMinutes</c> (clamped to 1..180) and <c>gitStepCommits</c> - their defaults, their
/// <see cref="AppPreferences"/> setters, the section view model that edits them (including the one property that
/// deliberately raises no change notification), their round trip through <see cref="PreferencesStore"/>, and the mapping
/// the git tools and the Git panel read on every use (<see cref="GitToolPreferences"/>).
/// <para>The pull strategy and the network timeout of the same section are covered by <c>GitSettingsSectionTests</c> and
/// <c>GitPreferencesTests</c>; this file covers the fields the pull request work added, plus the live mapping of all four
/// values into one <see cref="PrChecksPollerOptions"/>.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class GitSettingsSectionGitTests : IDisposable
{
    private readonly string directory;
    private readonly string settingsPath;

    public GitSettingsSectionGitTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "forge-git-settings-" + Guid.NewGuid().ToString("N"));
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

    // ------------------------------------------------------------ defaults

    [Fact]
    public void A_fresh_preferences_object_carries_the_documented_git_defaults()
    {
        var preferences = new AppPreferences();

        Assert.Equal(AppPreferences.DefaultGitPullStrategy, preferences.GitPullStrategy);
        Assert.Equal(120, preferences.GitNetworkTimeoutSeconds);
        Assert.True(preferences.GitStepCommits);
        Assert.Equal(30, preferences.GitChecksPollSeconds);
        Assert.Equal(30, preferences.GitChecksMaxWaitMinutes);
        Assert.True(preferences.Notifications);
        Assert.Equal(string.Empty, preferences.GitGhPath);
    }

    [Fact]
    public void The_section_shows_the_defaults()
    {
        using var section = new GitSettingsSectionViewModel(new AppPreferences());

        Assert.Equal(
            [GitPullStrategy.FastForwardOnly, GitPullStrategy.Rebase, GitPullStrategy.Merge],
            section.PullStrategies.Select(static option => option.Value).ToArray());

        Assert.Equal(GitPullStrategy.FastForwardOnly, section.SelectedPullStrategy.Value);
        Assert.Equal(120, section.NetworkTimeoutSeconds);
        Assert.True(section.StepCommits);
        Assert.Equal(30, section.ChecksPollSeconds);
        Assert.Equal(30, section.ChecksMaxWaitMinutes);
        Assert.Equal(string.Empty, section.GhPath);
    }

    [Fact]
    public void The_three_pull_strategy_options_have_localized_labels()
    {
        using var section = new GitSettingsSectionViewModel(new AppPreferences());

        Assert.Equal(
            [
                Loc.Instance[GitStrings.SettingsPullStrategyFastForward],
                Loc.Instance[GitStrings.SettingsPullStrategyRebase],
                Loc.Instance[GitStrings.SettingsPullStrategyMerge],
            ],
            section.PullStrategies.Select(static option => option.Label).ToArray());
    }

    // ------------------------------------------------------------ the clamps of the new numbers

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 10)]
    [InlineData(9, 10)]
    [InlineData(10, 10)]
    [InlineData(45, 45)]
    [InlineData(300, 300)]
    [InlineData(301, 300)]
    [InlineData(100000, 300)]
    public void The_poll_interval_setter_clamps_to_10_to_300_seconds(int requested, int expected)
    {
        var preferences = new AppPreferences { GitChecksPollSeconds = requested };

        Assert.Equal(expected, preferences.GitChecksPollSeconds);
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(45, 45)]
    [InlineData(180, 180)]
    [InlineData(181, 180)]
    [InlineData(9999, 180)]
    public void The_max_wait_setter_clamps_to_1_to_180_minutes(int requested, int expected)
    {
        var preferences = new AppPreferences { GitChecksMaxWaitMinutes = requested };

        Assert.Equal(expected, preferences.GitChecksMaxWaitMinutes);
    }

    // ------------------------------------------------------------ the section edits the preferences

    [Fact]
    public void The_gh_path_is_trimmed_and_an_empty_one_means_gh_from_PASH()
    {
        var preferences = new AppPreferences();
        using var section = new GitSettingsSectionViewModel(preferences);

        section.GhPath = @"  C:\Program Files\GitHub CLI\gh.exe  ";
        Assert.Equal(@"C:\Program Files\GitHub CLI\gh.exe", preferences.GitGhPath);
        Assert.Equal(@"C:\Program Files\GitHub CLI\gh.exe", section.GhPath);

        section.GhPath = "   ";
        Assert.Equal(string.Empty, preferences.GitGhPath);
        Assert.Equal(string.Empty, section.GhPath);

        section.GhPath = null!;
        Assert.Equal(string.Empty, preferences.GitGhPath);
    }

    [Fact]
    public void The_gh_path_raises_no_change_notification_because_the_box_is_edited_character_by_character()
    {
        using var section = new GitSettingsSectionViewModel(new AppPreferences());
        var raised = new List<string?>();
        section.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        section.GhPath = "C:\\tools\\gh.exe";

        Assert.Empty(raised);
    }

    [Fact]
    public void The_step_commits_switch_round_trips()
    {
        var preferences = new AppPreferences();
        using var section = new GitSettingsSectionViewModel(preferences);

        section.StepCommits = false;
        Assert.False(preferences.GitStepCommits);
        Assert.False(section.StepCommits);

        section.StepCommits = true;
        Assert.True(preferences.GitStepCommits);
    }

    [Fact]
    public void The_numbers_round_trip_through_the_section()
    {
        var preferences = new AppPreferences();
        using var section = new GitSettingsSectionViewModel(preferences);

        section.ChecksPollSeconds = 45;
        Assert.Equal(45, preferences.GitChecksPollSeconds);

        section.ChecksMaxWaitMinutes = 5;
        Assert.Equal(5, preferences.GitChecksMaxWaitMinutes);
    }

    [Fact]
    public void A_value_out_of_range_typed_into_the_section_is_clamped_in_the_preference_and_read_back()
    {
        var preferences = new AppPreferences();
        using var section = new GitSettingsSectionViewModel(preferences);

        section.ChecksPollSeconds = 1;
        Assert.Equal(AppPreferences.MinGitChecksPollSeconds, section.ChecksPollSeconds);

        section.ChecksPollSeconds = 9999;
        Assert.Equal(AppPreferences.MaxGitChecksPollSeconds, section.ChecksPollSeconds);

        section.ChecksMaxWaitMinutes = 0;
        Assert.Equal(AppPreferences.MinGitChecksMaxWaitMinutes, section.ChecksMaxWaitMinutes);

        section.ChecksMaxWaitMinutes = 9999;
        Assert.Equal(AppPreferences.MaxGitChecksMaxWaitMinutes, section.ChecksMaxWaitMinutes);
    }

    [Fact]
    public void Every_edited_property_raises_its_own_change_notification()
    {
        using var section = new GitSettingsSectionViewModel(new AppPreferences());
        var raised = new List<string?>();
        section.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        section.SelectedPullStrategy = section.PullStrategies[2];
        section.NetworkTimeoutSeconds = 45;
        section.ChecksPollSeconds = 45;
        section.ChecksMaxWaitMinutes = 5;
        section.StepCommits = false;

        Assert.Equal(
            [
                nameof(GitSettingsSectionViewModel.SelectedPullStrategy),
                nameof(GitSettingsSectionViewModel.NetworkTimeoutSeconds),
                nameof(GitSettingsSectionViewModel.ChecksPollSeconds),
                nameof(GitSettingsSectionViewModel.ChecksMaxWaitMinutes),
                nameof(GitSettingsSectionViewModel.StepCommits),
            ],
            raised);
    }

    [Fact]
    public void The_pull_strategy_picker_ignores_null_and_writes_the_documented_tokens()
    {
        var preferences = new AppPreferences();
        using var section = new GitSettingsSectionViewModel(preferences);

        section.SelectedPullStrategy = null!;
        Assert.Equal(AppPreferences.GitPullStrategyFastForwardOnly, preferences.GitPullStrategy);

        section.SelectedPullStrategy = section.PullStrategies[1];
        Assert.Equal(AppPreferences.GitPullStrategyRebase, preferences.GitPullStrategy);
        Assert.Equal(GitPullStrategy.Rebase, section.SelectedPullStrategy.Value);

        section.SelectedPullStrategy = section.PullStrategies[2];
        Assert.Equal(AppPreferences.GitPullStrategyMerge, preferences.GitPullStrategy);
        Assert.Equal(GitPullStrategy.Merge, section.SelectedPullStrategy.Value);

        section.SelectedPullStrategy = section.PullStrategies[0];
        Assert.Equal(AppPreferences.GitPullStrategyFastForwardOnly, preferences.GitPullStrategy);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unknown_strategy_token_shows_the_first_option(string token)
    {
        var preferences = new AppPreferences { GitPullStrategy = token };
        using var section = new GitSettingsSectionViewModel(preferences);

        Assert.Equal(GitPullStrategy.FastForwardOnly, section.SelectedPullStrategy.Value);
    }

    [Fact]
    public void A_section_built_over_a_null_preferences_object_is_rejected() =>
        Assert.Throws<ArgumentNullException>(() => new GitSettingsSectionViewModel(null!));

    [Fact]
    public void Dispose_is_idempotent()
    {
        var section = new GitSettingsSectionViewModel(new AppPreferences());

        section.Dispose();
        section.Dispose();

        // The values stay readable after the disposal; only the language subscription is gone.
        Assert.Equal(30, section.ChecksPollSeconds);
        Assert.True(section.StepCommits);
    }

    // ------------------------------------------------------------ the file

    [Fact]
    public async Task The_new_git_values_load_from_the_file_and_save_back()
    {
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "preferences": {
                "gitGhPath": "  /opt/homebrew/bin/gh  ",
                "notifications": false,
                "gitChecksPollSeconds": 45,
                "gitChecksMaxWaitMinutes": 5,
                "gitStepCommits": false
              }
            }
            """);

        await using (var store = new PreferencesStore(settingsPath))
        {
            await store.LoadAsync(CancellationToken.None);
            Assert.Null(store.LoadProblem);
            Assert.Equal("/opt/homebrew/bin/gh", store.Current.GitGhPath);
            Assert.False(store.Current.Notifications);
            Assert.Equal(45, store.Current.GitChecksPollSeconds);
            Assert.Equal(5, store.Current.GitChecksMaxWaitMinutes);
            Assert.False(store.Current.GitStepCommits);

            // An edit in the section is what the store persists on its next save.
            using var section = new GitSettingsSectionViewModel(store.Current);
            section.GhPath = "  C:\\tools\\gh.exe ";
            store.Current.Notifications = true;
            section.ChecksPollSeconds = 20;
            section.ChecksMaxWaitMinutes = 120;
            section.StepCommits = true;
            await store.SaveAsync(CancellationToken.None);
        }

        var document = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(settingsPath)));
        var preferences = Assert.IsType<JsonObject>(document["preferences"]);
        Assert.Equal(@"C:\tools\gh.exe", preferences["gitGhPath"]?.GetValue<string>());
        Assert.True(preferences["notifications"]?.GetValue<bool>());
        Assert.Equal(20, preferences["gitChecksPollSeconds"]?.GetValue<int>());
        Assert.Equal(120, preferences["gitChecksMaxWaitMinutes"]?.GetValue<int>());
        Assert.True(preferences["gitStepCommits"]?.GetValue<bool>());
    }

    [Fact]
    public async Task Missing_keys_and_out_of_range_values_load_as_defaults_or_clamped_values()
    {
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "preferences": {
                "gitChecksPollSeconds": 1,
                "gitChecksMaxWaitMinutes": 1000
              }
            }
            """);

        await using var store = new PreferencesStore(settingsPath);
        await store.LoadAsync(CancellationToken.None);

        Assert.Null(store.LoadProblem);
        Assert.Equal(AppPreferences.MinGitChecksPollSeconds, store.Current.GitChecksPollSeconds);
        Assert.Equal(AppPreferences.MaxGitChecksMaxWaitMinutes, store.Current.GitChecksMaxWaitMinutes);
        Assert.True(store.Current.Notifications);
        Assert.Equal(string.Empty, store.Current.GitGhPath);
    }

    [Fact]
    public async Task A_file_carrying_only_the_legacy_gitNotifications_key_loads_and_rewrites_as_notifications()
    {
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "preferences": {
                "gitNotifications": false
              }
            }
            """);

        await using (var store = new PreferencesStore(settingsPath))
        {
            await store.LoadAsync(CancellationToken.None);
            Assert.Null(store.LoadProblem);
            Assert.False(store.Current.Notifications);

            await store.SaveAsync(CancellationToken.None);
        }

        var document = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(settingsPath)));
        var preferences = Assert.IsType<JsonObject>(document["preferences"]);
        Assert.False(preferences["notifications"]?.GetValue<bool>());
        Assert.False(preferences.ContainsKey("gitNotifications"));
    }

    // ------------------------------------------------------------ what the tools and the panel read

    [Fact]
    public void The_wait_options_are_read_from_the_live_preferences()
    {
        var preferences = new AppPreferences();
        var options = GitToolPreferences.ChecksOptions(preferences);

        Assert.Equal(TimeSpan.FromSeconds(30), options.Interval);
        Assert.Equal(TimeSpan.FromMinutes(30), options.MaxWait);
        Assert.Equal(3, options.NoChecksConfirmations);

        using var section = new GitSettingsSectionViewModel(preferences);
        section.ChecksPollSeconds = 45;
        section.ChecksMaxWaitMinutes = 5;

        // A fresh read per wait: the next wait sees the new values.
        var updated = GitToolPreferences.ChecksOptions(preferences);
        Assert.Equal(TimeSpan.FromSeconds(45), updated.Interval);
        Assert.Equal(TimeSpan.FromMinutes(5), updated.MaxWait);
    }

    [Fact]
    public void The_tool_settings_read_the_live_strategy_and_timeout()
    {
        var preferences = new AppPreferences();
        var settings = GitToolPreferences.ToolSettings(preferences);

        Assert.Equal(GitPullStrategy.FastForwardOnly, settings.PullStrategy());
        Assert.Equal(TimeSpan.FromSeconds(120), settings.NetworkTimeout());

        using var section = new GitSettingsSectionViewModel(preferences);
        section.SelectedPullStrategy = section.PullStrategies[1];
        section.NetworkTimeoutSeconds = 45;

        Assert.Equal(GitPullStrategy.Rebase, settings.PullStrategy());
        Assert.Equal(TimeSpan.FromSeconds(45), settings.NetworkTimeout());
    }
}
