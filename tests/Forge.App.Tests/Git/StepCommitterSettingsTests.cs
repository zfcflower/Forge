using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Git;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins the <c>gitStepCommits</c> preference end to end: the model default (on), the JSON contract of the
/// <c>preferences</c> section (key name, boolean token, a missing key means on, a value of the wrong type never turns it
/// off), the settings section row that edits it, and the committer that reads it again on every call.
/// </summary>
public sealed class StepCommitterSettingsTests : IDisposable
{
    private readonly string directory;
    private readonly string settingsPath;

    public StepCommitterSettingsTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "forge-step-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        settingsPath = Path.Combine(directory, "settings.json");
    }

    public void Dispose() => StepCommitRepo.DeleteTree(directory);

    // ------------------------------------------------------------ the model

    [Fact]
    public void Step_commits_are_on_by_default()
    {
        Assert.True(AppPreferences.DefaultGitStepCommits);
        Assert.True(new AppPreferences().GitStepCommits);
    }

    [Fact]
    public void The_setter_raises_PropertyChanged_only_for_a_real_change()
    {
        var preferences = new AppPreferences();
        var raised = new List<string?>();
        preferences.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        preferences.GitStepCommits = true;
        Assert.Empty(raised);

        preferences.GitStepCommits = false;
        Assert.Equal([nameof(AppPreferences.GitStepCommits)], raised);
        Assert.False(preferences.GitStepCommits);

        preferences.GitStepCommits = false;
        Assert.Single(raised);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ApplyLoaded_reads_the_flag_and_treats_a_missing_one_as_on(bool? stored, bool expected)
    {
        var preferences = new AppPreferences { GitStepCommits = !expected };
        var raised = new List<string?>();
        preferences.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        preferences.ApplyLoaded(new PreferencesSection { GitStepCommits = stored }, UiLanguage.English);

        Assert.Equal(expected, preferences.GitStepCommits);
        Assert.Empty(raised);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToSection_writes_the_live_value(bool value)
    {
        var preferences = new AppPreferences { GitStepCommits = value };

        Assert.Equal(value, preferences.ToSection().GitStepCommits);
    }

    // ------------------------------------------------------------ the JSON contract

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_section_serializes_the_flag_as_a_json_boolean_under_gitStepCommits(bool value)
    {
        var section = new AppPreferences { GitStepCommits = value }.ToSection();

        var json = JsonSerializer.Serialize(section, PreferencesJsonContext.Default.PreferencesSection);

        var node = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        Assert.Equal(value, node["gitStepCommits"]!.GetValue<bool>());
        Assert.Equal(value ? "true" : "false", node["gitStepCommits"]!.ToJsonString());
    }

    [Theory]
    [InlineData("""{ "gitStepCommits": false }""", false)]
    [InlineData("""{ "gitStepCommits": true }""", true)]
    [InlineData("""{ }""", null)]
    [InlineData("""{ "gitStepCommits": null }""", null)]
    public void The_section_deserializes_the_flag_from_its_key(string json, bool? expected)
    {
        var section = JsonSerializer.Deserialize(json, PreferencesJsonContext.Default.PreferencesSection);

        Assert.NotNull(section);
        Assert.Equal(expected, section.GitStepCommits);
    }

    [Fact]
    public async Task A_stored_false_survives_a_load_and_a_stored_missing_key_loads_as_on()
    {
        await File.WriteAllTextAsync(settingsPath, """{ "preferences": { "gitStepCommits": false, "uiFontSize": 14 } }""");
        await using (var store = new PreferencesStore(settingsPath))
        {
            await store.LoadAsync(CancellationToken.None);
            Assert.Null(store.LoadProblem);
            Assert.False(store.Current.GitStepCommits);
        }

        await File.WriteAllTextAsync(settingsPath, """{ "preferences": { "uiFontSize": 14 } }""");
        await using (var store = new PreferencesStore(settingsPath))
        {
            await store.LoadAsync(CancellationToken.None);
            Assert.Null(store.LoadProblem);
            Assert.True(store.Current.GitStepCommits);
        }
    }

    [Fact]
    public async Task A_file_without_any_settings_loads_as_on()
    {
        await using var store = new PreferencesStore(settingsPath);

        await store.LoadAsync(CancellationToken.None);

        Assert.True(store.Current.GitStepCommits);
    }

    [Fact]
    public async Task A_toggle_round_trips_through_the_file_and_keeps_other_sections_and_git_values()
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
            Assert.True(store.Current.GitStepCommits);

            store.Current.GitStepCommits = false;
            await store.SaveAsync(CancellationToken.None);
        }

        var document = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(settingsPath)));
        Assert.Equal("openai-compatible", Assert.IsType<JsonObject>(document["provider"])["kind"]?.GetValue<string>());
        Assert.True(Assert.IsType<JsonObject>(document["unknown"])["flag"]?.GetValue<bool>());
        var preferences = Assert.IsType<JsonObject>(document["preferences"]);
        Assert.False(preferences["gitStepCommits"]?.GetValue<bool>());
        Assert.Equal("rebase", preferences["gitPullStrategy"]?.GetValue<string>());
        Assert.Equal(45, preferences["gitNetworkTimeoutSeconds"]?.GetValue<int>());

        await using (var reloaded = new PreferencesStore(settingsPath))
        {
            await reloaded.LoadAsync(CancellationToken.None);
            Assert.False(reloaded.Current.GitStepCommits);
            reloaded.Current.GitStepCommits = true;
            await reloaded.SaveAsync(CancellationToken.None);
        }

        var again = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(settingsPath)));
        Assert.True(Assert.IsType<JsonObject>(again["preferences"])["gitStepCommits"]?.GetValue<bool>());
    }

    [Fact]
    public async Task A_value_of_the_wrong_type_never_switches_step_commits_off_and_does_not_throw()
    {
        await File.WriteAllTextAsync(settingsPath, """{ "preferences": { "gitStepCommits": "no" } }""");
        await using var store = new PreferencesStore(settingsPath);

        await store.LoadAsync(CancellationToken.None);

        Assert.True(store.Current.GitStepCommits);
    }

    // ------------------------------------------------------------ the settings row

    [Fact]
    public void The_section_reflects_and_edits_the_preference_without_touching_the_other_git_rows()
    {
        var preferences = new AppPreferences { GitPullStrategy = "rebase", GitNetworkTimeoutSeconds = 45 };
        using var section = new GitSettingsSectionViewModel(preferences);
        var raised = new List<string?>();
        section.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        Assert.True(section.StepCommits);

        section.StepCommits = false;

        Assert.False(preferences.GitStepCommits);
        Assert.False(section.StepCommits);
        Assert.Equal([nameof(GitSettingsSectionViewModel.StepCommits)], raised);
        Assert.Equal("rebase", preferences.GitPullStrategy);
        Assert.Equal(45, preferences.GitNetworkTimeoutSeconds);

        preferences.GitStepCommits = true;
        Assert.True(section.StepCommits);
    }

    [Fact]
    public async Task Toggling_the_section_changes_the_next_step_commit_with_no_restart()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var preferences = new AppPreferences();
        using var section = new GitSettingsSectionViewModel(preferences);
        var wired = new StepCommitter(
            StepCommitRepo.Cli,
            harness.Store,
            (_, _) => Task.FromResult(WorktreeResolution.Own(harness.Repo.Path)),
            () => preferences.GitStepCommits);
        var first = await harness.AddDoneAsync("first");
        var second = await harness.AddDoneAsync("second");
        var third = await harness.AddDoneAsync("third");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        section.StepCommits = false;
        harness.Repo.WriteFile("a.txt", "a\n");
        var off = await wired.CommitStepAsync(first, cts.Token);
        Assert.Equal(StepCommitOutcome.Skipped, off.Outcome);
        Assert.Equal("?? a.txt", await harness.Repo.StatusAsync());

        section.StepCommits = true;
        var on = await wired.CommitStepAsync(second, cts.Token);
        Assert.Equal(StepCommitOutcome.Committed, on.Outcome);
        Assert.Equal("second", await harness.Repo.LastMessageAsync());

        section.StepCommits = false;
        harness.Repo.WriteFile("b.txt", "b\n");
        var offAgain = await wired.CommitStepAsync(third, cts.Token);
        Assert.Equal(StepCommitOutcome.Skipped, offAgain.Outcome);
        Assert.Equal("?? b.txt", await harness.Repo.StatusAsync());
    }
}

/// <summary>The settings row as the user sees it: the check box bound to the section.</summary>
[Collection("forge-app-ui")]
public sealed class StepCommitterSettingsViewTests
{
    [Fact]
    public async Task The_checkbox_shows_the_preference_and_writes_it_back_when_clicked()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var observed = await session.Dispatch(
            () =>
            {
                var preferences = new AppPreferences();
                using var section = new GitSettingsSectionViewModel(preferences);
                var window = new Window { Width = 640, Height = 480, Content = new ContentControl { Content = section } };
                window.Show();
                try
                {
                    var box = CheckBoxOf(window);
                    var initial = box.IsChecked;
                    var name = AutomationProperties.GetName(box);

                    box.IsChecked = false;
                    var afterUntick = preferences.GitStepCommits;

                    section.StepCommits = true;
                    Settle();
                    var afterModelChange = box.IsChecked;

                    box.IsChecked = false;
                    section.StepCommits = false;
                    Settle();
                    return (initial, name, afterUntick, afterModelChange, boxAfterOff: box.IsChecked, pref: preferences.GitStepCommits);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        Assert.True(observed.initial);
        Assert.False(string.IsNullOrWhiteSpace(observed.name));
        Assert.NotEqual("[git.settings.stepCommits]", observed.name);
        Assert.False(observed.afterUntick);
        Assert.True(observed.afterModelChange);
        Assert.False(observed.boxAfterOff);
        Assert.False(observed.pref);
    }

    [Fact]
    public async Task The_checkbox_starts_unchecked_when_the_preference_is_off()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        var isChecked = await session.Dispatch(
            () =>
            {
                var preferences = new AppPreferences { GitStepCommits = false };
                using var section = new GitSettingsSectionViewModel(preferences);
                var window = new Window { Width = 640, Height = 480, Content = new ContentControl { Content = section } };
                window.Show();
                try
                {
                    return CheckBoxOf(window).IsChecked;
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        Assert.False(isChecked);
    }

    private static CheckBox CheckBoxOf(Window window)
    {
        Settle();
        return window.GetVisualDescendants()
            .OfType<CheckBox>()
            .Single(static box => string.Equals(AutomationProperties.GetAutomationId(box), "git-step-commits", StringComparison.Ordinal));
    }

    private static void Settle()
    {
        for (var pass = 0; pass < 2; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }
}
