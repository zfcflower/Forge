using System.Net;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.Services;
using Forge.App.Tests.Services;
using Xunit;

namespace Forge.App.Tests.Settings;

/// <summary>
/// The <c>updates</c> section's view model (wave W10b): it edits the three live preferences, reports the running version,
/// runs a manual check with a readable result, and reads the persisted last-check time when the section is shown.
/// </summary>
public sealed class UpdatesSettingsSectionTests : IDisposable
{
    private readonly UiLanguage previousLanguage = Loc.Instance.Language;

    public void Dispose() => Loc.Instance.Language = previousLanguage;
    [Fact]
    public void The_preferences_are_read_and_written_through_the_section()
    {
        var preferences = new AppPreferences();
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        using var section = new UpdatesSettingsSectionViewModel(preferences, sandbox.Service);

        Assert.Equal(AppPreferences.DefaultUpdatesManifestUrl, section.ManifestUrl);
        Assert.Equal(AppPreferences.DefaultUpdatesCheckFrequency, section.SelectedFrequency.Value);
        Assert.Equal(AppPreferences.DefaultUpdatesChannel, section.SelectedChannel.Value);
        Assert.Equal("1.0.0", section.CurrentVersion);

        section.ManifestUrl = "https://example.test/custom-updates.json";
        section.SelectedFrequency = section.Frequencies[1]; // weekly
        section.SelectedChannel = section.Channels[1];      // beta

        Assert.Equal("https://example.test/custom-updates.json", preferences.UpdatesManifestUrl);
        Assert.Equal("weekly", preferences.UpdatesCheckFrequency);
        Assert.Equal("beta", preferences.UpdatesChannel);
    }

    [Fact]
    public async Task A_manual_check_shows_a_result_and_the_release_notes()
    {
        var preferences = new AppPreferences();
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", "win-x64", out _);
        using var section = new UpdatesSettingsSectionViewModel(preferences, sandbox.Service);

        Assert.False(section.CanOpenDownloadLocation);

        await section.CheckNowAsync();

        Assert.True(section.HasResult);
        Assert.False(string.IsNullOrEmpty(section.ResultText));
        Assert.True(section.CanOpenDownloadLocation);
        Assert.Equal("# Notes", section.Notes);
        Assert.True(section.HasNotes);
        Assert.False(section.IsChecking);
    }

    [Fact]
    public async Task Reload_reads_the_persisted_last_check_time()
    {
        var preferences = new AppPreferences();
        using var sandbox = UpdateTestService.Build(currentVersion: "1.0.0");
        sandbox.UiState.Seed("updates.lastCheck", 1, "{\"checkedAt\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\"}");
        using var section = new UpdatesSettingsSectionViewModel(preferences, sandbox.Service);

        Assert.Equal(UpdatesStrings.SettingsLastCheckNever, section.LastCheckText);

        await section.ReloadAsync();

        Assert.NotEqual(UpdatesStrings.SettingsLastCheckNever, section.LastCheckText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_manifest_url_disables_the_check(string url)
    {
        // The hint tells the user "leave it empty to disable update checks"; the check must then report that, and must not
        // fall back to the default GitHub URL or to another stored value.
        var preferences = new AppPreferences();
        using var sandbox = UpdateTestService.BuildForPreferences(preferences, currentVersion: "1.0.0");
        preferences.UpdatesManifestUrl = UpdateTestManifest.ManifestUrl;
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", "win-x64", out _);
        using var section = new UpdatesSettingsSectionViewModel(preferences, sandbox.Service);

        section.ManifestUrl = url;
        await section.CheckNowAsync();

        Assert.Equal(UpdatesStrings.ErrorManifestUrlEmpty.Resolve(), section.ResultText);
        Assert.Empty(sandbox.Handler.Requests);
        Assert.False(section.CanOpenDownloadLocation);
        Assert.False(section.HasNotes);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("https://")]
    [InlineData("ftp://example.test/updates.json")]
    public async Task A_manifest_url_that_is_not_a_usable_url_shows_a_readable_result(string url)
    {
        // DOCUMENTED DEFECT D1 (see the W10b test report): the manifest URL is free text, and anything that is not an absolute
        // http(s) URI used to throw out of UpdateService.CheckForUpdateAsync. CheckNowAsync only caught
        // OperationCanceledException and AsyncRelayCommand swallows the rest, so clicking "Check now" on a typo left the
        // section with no result at all. The service now returns a readable failure, and the section shows it.
        var preferences = new AppPreferences();
        using var sandbox = UpdateTestService.BuildForPreferences(preferences, currentVersion: "1.0.0");
        preferences.UpdatesManifestUrl = UpdateTestManifest.ManifestUrl;
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", "win-x64", out _);
        using var section = new UpdatesSettingsSectionViewModel(preferences, sandbox.Service);

        section.ManifestUrl = url;
        await section.CheckNowAsync();

        Assert.Equal(UpdatesStrings.ErrorManifestUrlInvalid(url).Resolve(), section.ResultText);
        Assert.True(section.HasResult);
        Assert.False(section.IsChecking);
        Assert.False(section.CanOpenDownloadLocation);
        Assert.Empty(sandbox.Handler.Requests);
    }

    [Fact]
    public async Task The_channel_choice_applies_to_the_next_check_immediately()
    {
        // The service reads the channel through a live func (AppComposition wires it that way); a switch in the section must
        // route the very next check without rebuilding the service or restarting.
        var preferences = new AppPreferences();
        using var sandbox = UpdateTestService.BuildForPreferences(preferences, currentVersion: "1.0.0");
        preferences.UpdatesManifestUrl = UpdateTestManifest.ManifestUrl;
        var stablePayload = System.Text.Encoding.UTF8.GetBytes("stable payload");
        var betaPayload = System.Text.Encoding.UTF8.GetBytes("beta payload");
        var stableUrl = "https://example.test/package/Forge-1.2.0-win-x64.zip";
        var betaUrl = "https://example.test/package/Forge-1.3.0-win-x64.zip";
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl,
            UpdateTestManifest.TwoChannels("1.2.0", "1.3.0", "win-x64", stableUrl, betaUrl, stablePayload, betaPayload));
        sandbox.Handler.Package(stableUrl, stablePayload);
        sandbox.Handler.Package(betaUrl, betaPayload);
        using var section = new UpdatesSettingsSectionViewModel(preferences, sandbox.Service);

        section.SelectedChannel = section.Channels[1];
        await section.CheckNowAsync();

        Assert.Equal("beta", preferences.UpdatesChannel);
        Assert.Contains(betaUrl, sandbox.Handler.Requests);
        Assert.DoesNotContain(stableUrl, sandbox.Handler.Requests);
        Assert.True(section.CanOpenDownloadLocation);
    }

    [Fact]
    public async Task The_frequency_choice_applies_to_the_next_pump_tick_immediately()
    {
        // "never" must stop the pump at once; switching back must let the next tick check (nothing has been recorded yet, so
        // the first check is due).
        var preferences = new AppPreferences { UpdatesCheckFrequency = UpdateSchedule.Never };
        using var sandbox = UpdateTestService.BuildForPreferences(preferences, currentVersion: "1.0.0");
        preferences.UpdatesManifestUrl = UpdateTestManifest.ManifestUrl;
        UpdateTestService.SetStableOffer(sandbox.Handler, "1.2.0", "win-x64", out _);
        using var section = new UpdatesSettingsSectionViewModel(preferences, sandbox.Service);

        await sandbox.Service.RunAutoCheckIfDueAsync(CancellationToken.None);
        Assert.Empty(sandbox.Handler.Requests);

        section.SelectedFrequency = section.Frequencies[0];
        await sandbox.Service.RunAutoCheckIfDueAsync(CancellationToken.None);

        Assert.Equal(UpdateSchedule.Daily, preferences.UpdatesCheckFrequency);
        Assert.Contains(UpdateTestManifest.ManifestUrl, sandbox.Handler.Requests);
    }

    [Fact]
    public async Task A_cancelled_check_reports_cancelled_and_the_next_check_recovers()
    {
        // The cancel button cancels the section's own token; the section must show "cancelled", leave no staged file and
        // remain usable for a retry (a state left behind here would disable Check now for the rest of the session).
        var preferences = new AppPreferences();
        using var sandbox = UpdateTestService.BuildForPreferences(preferences, currentVersion: "1.0.0");
        preferences.UpdatesManifestUrl = UpdateTestManifest.ManifestUrl;
        var payload = System.Text.Encoding.UTF8.GetBytes("payload that will be cancelled");
        var packageUrl = "https://example.test/package/Forge-1.2.0-win-x64.zip";
        sandbox.Handler.Manifest(UpdateTestManifest.ManifestUrl,
            UpdateTestManifest.Stable("1.2.0", "win-x64", packageUrl, payload));
        using var section = new UpdatesSettingsSectionViewModel(preferences, sandbox.Service);
        sandbox.Handler.Respond(packageUrl, () =>
        {
            section.CancelCommand.Execute(null);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
        });

        await section.CheckNowAsync();

        Assert.Equal(UpdatesStrings.ResultCancelled.Resolve(), section.ResultText);
        Assert.False(section.IsChecking);
        Assert.False(section.HasNotes);
        Assert.False(section.CanOpenDownloadLocation);
        Assert.Empty(Directory.EnumerateFiles(sandbox.Root, "*", SearchOption.AllDirectories));

        sandbox.Handler.Package(packageUrl, payload);
        await section.CheckNowAsync();

        Assert.True(section.CanOpenDownloadLocation);
        Assert.Equal("# Notes", section.Notes);
        Assert.True(section.HasNotes);
    }

    [Fact]
    public void The_section_stops_listening_to_language_changes_after_dispose()
    {
        // Settings sections are rebuilt every time the window opens; a leaked LanguageChanged handler keeps the closed
        // section (and its tree) alive for the life of the process.
        var preferences = new AppPreferences();
        using var sandbox = UpdateTestService.BuildForPreferences(preferences, currentVersion: "1.0.0");
        var section = new UpdatesSettingsSectionViewModel(preferences, sandbox.Service);
        var changed = new List<string?>();
        section.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Loc.Instance.Language = Other(Loc.Instance.Language);
        Assert.Contains(nameof(UpdatesSettingsSectionViewModel.Frequencies), changed);

        section.Dispose();
        changed.Clear();

        Loc.Instance.Language = Other(Loc.Instance.Language);
        Assert.DoesNotContain(nameof(UpdatesSettingsSectionViewModel.Frequencies), changed);
    }

    private static UiLanguage Other(UiLanguage language) =>
        language == UiLanguage.Chinese ? UiLanguage.English : UiLanguage.Chinese;
}
