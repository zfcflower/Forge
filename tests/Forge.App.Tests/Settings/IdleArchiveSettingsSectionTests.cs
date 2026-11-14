using Forge.App.Automations;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.Tests.Localization;
using Xunit;

namespace Forge.App.Tests.Settings;

/// <summary>
/// The <c>idle-archive</c> settings section: the switch, the idle window and the scope picker edit the live
/// <see cref="AppPreferences"/> directly, the window is clamped to the range the field offers, the picker stores the scope
/// token (never the label) and re-reads what the preference accepted, and the two labels follow a language switch while the
/// selection stays. The section drops its <see cref="Loc.LanguageChanged"/> subscription on disposal, because the string
/// table outlives the composition that owns it.
/// </summary>
public sealed class IdleArchiveSettingsSectionTests
{
    [Fact]
    public void The_switch_round_trips_and_enables_the_body()
    {
        var preferences = new AppPreferences();
        using var section = new IdleArchiveSettingsSectionViewModel(preferences);
        var raised = PropertyChangeLog.For(section);
        Assert.True(section.Enabled);
        Assert.True(section.CanConfigure);

        section.Enabled = false;

        Assert.False(preferences.IdleArchiveEnabled);
        Assert.False(section.CanConfigure);
        // The body greys itself out with the switch, so both properties are announced.
        Assert.Equal([nameof(IdleArchiveSettingsSectionViewModel.Enabled), nameof(IdleArchiveSettingsSectionViewModel.CanConfigure)], raised);

        // Written from elsewhere, it is read back without a reload: the runner reads the live value once per run.
        preferences.IdleArchiveEnabled = true;
        Assert.True(section.Enabled);
        Assert.True(section.CanConfigure);
    }

    [Theory]
    [InlineData(0, AppPreferences.MinIdleArchiveDays)]
    [InlineData(-3, AppPreferences.MinIdleArchiveDays)]
    [InlineData(400, AppPreferences.MaxIdleArchiveDays)]
    public void The_window_is_clamped_and_re_read(int written, int expected)
    {
        var preferences = new AppPreferences();
        using var section = new IdleArchiveSettingsSectionViewModel(preferences);

        section.Days = written;

        Assert.Equal(expected, preferences.IdleArchiveDays);
        Assert.Equal(expected, section.Days);
    }

    [Fact]
    public void The_field_range_is_the_preference_range()
    {
        using var section = new IdleArchiveSettingsSectionViewModel(new AppPreferences());

        Assert.Equal(AppPreferences.MinIdleArchiveDays, section.MinDays);
        Assert.Equal(AppPreferences.MaxIdleArchiveDays, section.MaxDays);
        Assert.InRange(section.Days, section.MinDays, section.MaxDays);
    }

    [Theory]
    [InlineData(IdleArchiveSettings.AllScopeToken)]
    [InlineData(IdleArchiveSettings.AutomationScopeToken)]
    public void The_picker_writes_the_scope_token(string token)
    {
        var preferences = new AppPreferences();
        using var section = new IdleArchiveSettingsSectionViewModel(preferences);
        var option = section.Scopes.Single(candidate => string.Equals(candidate.Token, token, StringComparison.Ordinal));
        var raised = PropertyChangeLog.For(section);

        section.SelectedScope = option;

        Assert.Equal(token, preferences.IdleArchiveScope);
        Assert.Equal(token, section.SelectedScope.Token);
        Assert.Equal([nameof(IdleArchiveSettingsSectionViewModel.SelectedScope)], raised);
    }

    [Fact]
    public void An_empty_selection_is_ignored()
    {
        var preferences = new AppPreferences { IdleArchiveScope = IdleArchiveSettings.AllScopeToken };
        using var section = new IdleArchiveSettingsSectionViewModel(preferences);
        var raised = PropertyChangeLog.For(section);

        section.SelectedScope = null!;

        // A picker that clears its selection must not write a token the store would reject.
        Assert.Equal(IdleArchiveSettings.AllScopeToken, preferences.IdleArchiveScope);
        Assert.Equal(IdleArchiveSettings.AllScopeToken, section.SelectedScope.Token);
        Assert.Empty(raised);
    }

    [Fact]
    public void An_unknown_token_shows_the_default_option()
    {
        var preferences = new AppPreferences();
        using var section = new IdleArchiveSettingsSectionViewModel(preferences);

        // A token the store does not know: the preference keeps the default one, and the picker re-reads that value instead of
        // showing the option that was handed to it.
        section.SelectedScope = new IdleArchiveScopeOption("everything", "everything");

        Assert.Equal(IdleArchiveSettings.AutomationScopeToken, preferences.IdleArchiveScope);
        Assert.Equal(IdleArchiveSettings.AutomationScopeToken, section.SelectedScope.Token);
        Assert.Same(section.Scopes[0], section.SelectedScope);
    }

    [Fact]
    public void The_labels_follow_a_language_switch_and_the_selection_stays()
    {
        using var chinese = new LanguageScope(UiLanguage.Chinese);
        var preferences = new AppPreferences { IdleArchiveScope = IdleArchiveSettings.AllScopeToken };
        using var section = new IdleArchiveSettingsSectionViewModel(preferences);
        var raised = PropertyChangeLog.For(section);
        var before = section.Scopes.Select(static option => option.Label).ToList();
        Assert.Equal(
            [AgentSettingsStrings.IdleArchiveScopeAutomation, AgentSettingsStrings.IdleArchiveScopeAll],
            before);

        using (new LanguageScope(UiLanguage.English))
        {
            // The list is replaced rather than mutated, so both the items and the selection of the picker are re-read.
            Assert.Equal(
                [nameof(IdleArchiveSettingsSectionViewModel.Scopes), nameof(IdleArchiveSettingsSectionViewModel.SelectedScope)],
                raised);
            Assert.Equal(
                [AgentSettingsStrings.IdleArchiveScopeAutomation, AgentSettingsStrings.IdleArchiveScopeAll],
                section.Scopes.Select(static option => option.Label));
            Assert.NotEqual(before, section.Scopes.Select(static option => option.Label));
            Assert.Equal(IdleArchiveSettings.AllScopeToken, section.SelectedScope.Token);
        }
    }

    [Fact]
    public void Dispose_drops_the_language_subscription()
    {
        using var chinese = new LanguageScope(UiLanguage.Chinese);
        var section = new IdleArchiveSettingsSectionViewModel(new AppPreferences());
        var raised = PropertyChangeLog.For(section);

        section.Dispose();
        section.Dispose();

        // The string table is process-wide: a disposed section that still listened would outlive the composition with it.
        using var english = new LanguageScope(UiLanguage.English);
        Assert.Empty(raised);
    }
}
