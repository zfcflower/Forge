using System.Text.Json;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;
using Xunit;

namespace Forge.App.Tests.Settings;

/// <summary>
/// The <c>rate-limit</c> section's view model (wave W15): it edits the live preferences, offers exactly the range those
/// setters clamp to, and says so when a value changed so the composition can republish the policy. Nothing is cached here,
/// so the assertions are about the four preferences it writes.
/// </summary>
public sealed class RateLimitSettingsSectionTests
{
    [Fact]
    public void The_four_preferences_are_read_and_written_through_the_section()
    {
        var preferences = new AppPreferences();
        var section = new RateLimitSettingsSectionViewModel(preferences);

        // The section opens on the stored values, which are the limiter's own defaults.
        Assert.True(section.Enabled);
        Assert.Equal(600, section.RequestsPerMinute);
        Assert.Equal(4_000_000, section.TokensPerMinute);
        Assert.Equal(10, section.AutomationGapSeconds);
        Assert.True(preferences.RateLimitEnabled);
        Assert.Equal(600, preferences.RateLimitRequestsPerMinute);
        Assert.Equal(4_000_000, preferences.RateLimitTokensPerMinute);
        Assert.Equal(10, preferences.RateLimitAutomationGapSeconds);

        section.Enabled = false;
        section.RequestsPerMinute = 123;
        section.TokensPerMinute = 250_000;
        section.AutomationGapSeconds = 45;

        Assert.False(preferences.RateLimitEnabled);
        Assert.Equal(123, preferences.RateLimitRequestsPerMinute);
        Assert.Equal(250_000, preferences.RateLimitTokensPerMinute);
        Assert.Equal(45, preferences.RateLimitAutomationGapSeconds);

        // A value written to the preference by someone else reads back through the section: no cached copy.
        preferences.RateLimitRequestsPerMinute = 77;
        Assert.Equal(77, section.RequestsPerMinute);
    }

    [Fact]
    public void The_section_offers_the_range_the_preference_setters_clamp_to()
    {
        var section = new RateLimitSettingsSectionViewModel(new AppPreferences());

        Assert.Equal(AppPreferences.MinRateLimitRequestsPerMinute, section.MinRequestsPerMinute);
        Assert.Equal(AppPreferences.MaxRateLimitRequestsPerMinute, section.MaxRequestsPerMinute);
        Assert.Equal(AppPreferences.MinRateLimitTokensPerMinute, section.MinTokensPerMinute);
        Assert.Equal(AppPreferences.MaxRateLimitTokensPerMinute, section.MaxTokensPerMinute);
        Assert.Equal(AppPreferences.MinRateLimitAutomationGapSeconds, section.MinAutomationGapSeconds);
        Assert.Equal(AppPreferences.MaxRateLimitAutomationGapSeconds, section.MaxAutomationGapSeconds);

        // The documented ranges and defaults (forge-app spec, "本地限速分区 rate-limit").
        Assert.Equal((1, 100_000), (section.MinRequestsPerMinute, section.MaxRequestsPerMinute));
        Assert.Equal((1_000, 1_000_000_000), (section.MinTokensPerMinute, section.MaxTokensPerMinute));
        Assert.Equal((0, 600), (section.MinAutomationGapSeconds, section.MaxAutomationGapSeconds));
        Assert.Equal(PageDefaultRequests, AppPreferences.DefaultRateLimitRequestsPerMinute);
        Assert.Equal(PageDefaultTokens, AppPreferences.DefaultRateLimitTokensPerMinute);
        Assert.Equal(PageDefaultGap, AppPreferences.DefaultRateLimitAutomationGapSeconds);
        Assert.True(AppPreferences.DefaultRateLimitEnabled);
    }

    [Fact]
    public void An_out_of_range_write_reads_back_as_the_clamped_value()
    {
        var preferences = new AppPreferences();
        var section = new RateLimitSettingsSectionViewModel(preferences);

        section.RequestsPerMinute = AppPreferences.MinRateLimitRequestsPerMinute - 1;
        Assert.Equal(AppPreferences.MinRateLimitRequestsPerMinute, section.RequestsPerMinute);
        Assert.Equal(AppPreferences.MinRateLimitRequestsPerMinute, preferences.RateLimitRequestsPerMinute);

        section.RequestsPerMinute = AppPreferences.MaxRateLimitRequestsPerMinute + 1;
        Assert.Equal(AppPreferences.MaxRateLimitRequestsPerMinute, section.RequestsPerMinute);
        Assert.Equal(AppPreferences.MaxRateLimitRequestsPerMinute, preferences.RateLimitRequestsPerMinute);

        section.TokensPerMinute = AppPreferences.MinRateLimitTokensPerMinute - 1;
        Assert.Equal(AppPreferences.MinRateLimitTokensPerMinute, section.TokensPerMinute);

        section.TokensPerMinute = int.MaxValue;
        Assert.Equal(AppPreferences.MaxRateLimitTokensPerMinute, section.TokensPerMinute);

        section.AutomationGapSeconds = AppPreferences.MinRateLimitAutomationGapSeconds - 1;
        Assert.Equal(AppPreferences.MinRateLimitAutomationGapSeconds, section.AutomationGapSeconds);

        section.AutomationGapSeconds = AppPreferences.MaxRateLimitAutomationGapSeconds + 1;
        Assert.Equal(AppPreferences.MaxRateLimitAutomationGapSeconds, section.AutomationGapSeconds);
    }

    [Fact]
    public void Every_write_raises_the_notification_the_republish_listens_for()
    {
        var preferences = new AppPreferences();
        var section = new RateLimitSettingsSectionViewModel(preferences);
        var sectionRaised = PropertyChangeLog.For(section);
        var preferenceRaised = PropertyChangeLog.For(preferences);

        section.Enabled = false;
        section.RequestsPerMinute = 900;
        section.TokensPerMinute = 1_000_000;
        section.AutomationGapSeconds = 30;

        // The section re-reads from the preference, so it must notify its own binding…
        Assert.Equal(
            [nameof(RateLimitSettingsSectionViewModel.Enabled), nameof(RateLimitSettingsSectionViewModel.RequestsPerMinute),
             nameof(RateLimitSettingsSectionViewModel.TokensPerMinute), nameof(RateLimitSettingsSectionViewModel.AutomationGapSeconds)],
            sectionRaised);

        // …and the preference must notify the composition, which is what publishes the new policy.
        Assert.Equal(
            [nameof(AppPreferences.RateLimitEnabled), nameof(AppPreferences.RateLimitRequestsPerMinute),
             nameof(AppPreferences.RateLimitTokensPerMinute), nameof(AppPreferences.RateLimitAutomationGapSeconds)],
            preferenceRaised);
    }

    // ------------------------------------------------------------------ persistence

    [Fact]
    public void The_four_values_survive_ToSection_json_and_ApplyLoaded()
    {
        var source = new AppPreferences
        {
            RateLimitEnabled = false,
            RateLimitRequestsPerMinute = 321,
            RateLimitTokensPerMinute = 7_654_321,
            RateLimitAutomationGapSeconds = 42,
        };

        var loaded = RoundTrip(source);

        Assert.False(loaded.RateLimitEnabled);
        Assert.Equal(321, loaded.RateLimitRequestsPerMinute);
        Assert.Equal(7_654_321, loaded.RateLimitTokensPerMinute);
        Assert.Equal(42, loaded.RateLimitAutomationGapSeconds);
    }

    [Fact]
    public void A_stored_section_without_the_keys_gives_the_defaults()
    {
        var loaded = Load(new PreferencesSection());

        Assert.True(loaded.RateLimitEnabled);
        Assert.Equal(600, loaded.RateLimitRequestsPerMinute);
        Assert.Equal(4_000_000, loaded.RateLimitTokensPerMinute);
        Assert.Equal(10, loaded.RateLimitAutomationGapSeconds);
    }

    [Fact]
    public void Out_of_range_values_in_a_stored_section_are_clamped_on_load()
    {
        var loaded = Load(new PreferencesSection
        {
            RateLimitRequestsPerMinute = 0,
            RateLimitTokensPerMinute = 1,
            RateLimitAutomationGapSeconds = 100_000,
        });

        Assert.Equal(AppPreferences.MinRateLimitRequestsPerMinute, loaded.RateLimitRequestsPerMinute);
        Assert.Equal(AppPreferences.MinRateLimitTokensPerMinute, loaded.RateLimitTokensPerMinute);
        Assert.Equal(AppPreferences.MaxRateLimitAutomationGapSeconds, loaded.RateLimitAutomationGapSeconds);

        var negatives = Load(new PreferencesSection
        {
            RateLimitRequestsPerMinute = -20,
            RateLimitTokensPerMinute = -1,
            RateLimitAutomationGapSeconds = -5,
        });

        Assert.Equal(AppPreferences.MinRateLimitRequestsPerMinute, negatives.RateLimitRequestsPerMinute);
        Assert.Equal(AppPreferences.MinRateLimitTokensPerMinute, negatives.RateLimitTokensPerMinute);
        Assert.Equal(AppPreferences.MinRateLimitAutomationGapSeconds, negatives.RateLimitAutomationGapSeconds);
    }

    [Fact]
    public void Loading_a_stored_section_raises_no_change_notification()
    {
        // The composition publishes once after reading the file precisely because ApplyLoaded is silent.
        var preferences = new AppPreferences();
        var raised = PropertyChangeLog.For(preferences);

        preferences.ApplyLoaded(new PreferencesSection { RateLimitRequestsPerMinute = 900 }, UiLanguage.System);

        Assert.Empty(raised);
        Assert.Equal(900, preferences.RateLimitRequestsPerMinute);
    }

    private static AppPreferences RoundTrip(AppPreferences source)
    {
        var json = JsonSerializer.Serialize(source.ToSection(), PreferencesJsonContext.Default.PreferencesSection);
        var section = JsonSerializer.Deserialize(json, PreferencesJsonContext.Default.PreferencesSection);
        Assert.NotNull(section);
        return Load(section);
    }

    private static AppPreferences Load(PreferencesSection section)
    {
        var loaded = new AppPreferences();
        loaded.ApplyLoaded(section, UiLanguage.System);
        return loaded;
    }

    // The values the spec names for a fresh profile.
    private const int PageDefaultRequests = 600;
    private const int PageDefaultTokens = 4_000_000;
    private const int PageDefaultGap = 10;
}
