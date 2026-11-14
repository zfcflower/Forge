using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.Tests.Localization;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Composition;

/// <summary>
/// The wave's composition module (forge-app spec, "本地限速分区 rate-limit"): it registers the section, and it is the one place
/// that turns the four preferences into the policy <see cref="ProviderRateLimiter.Shared"/> enforces — at registration and on
/// every change of those four values, and on nothing else.
/// <para>Every test here restores the shared limiter's policy, because it outlives the test class and the rest of the suite
/// keeps using it.</para>
/// </summary>
public sealed class RateLimitCompositionTests
{
    [Fact]
    public void The_section_is_registered_with_its_id_order_group_and_view_model()
    {
        try
        {
            var settings = new SettingsSections();

            RateLimitComposition.RegisterSection(new AppPreferences(), settings);

            var section = Assert.Single(settings.Items);
            Assert.Equal(SettingsSectionIds.RateLimit, section.Id);
            Assert.Equal("rate-limit", section.Id);
            Assert.Equal(SettingsSectionIds.RateLimitOrder, section.Order);
            Assert.Equal(115, section.Order);
            Assert.Equal(SettingsGroups.Agent, section.Group);
            Assert.Equal(RateLimitStrings.SettingsTitleKey, section.TitleKey);
            Assert.Equal("IconLock", section.IconKey);
            Assert.IsType<RateLimitSettingsSectionViewModel>(section.ViewModel);

            // The title is a key the string table really resolves, in both languages.
            using (new LanguageScope(UiLanguage.English))
            {
                Assert.Equal("Rate limits", section.Title);
            }

            using (new LanguageScope(UiLanguage.Chinese))
            {
                Assert.Equal("按提供商限速", section.Title);
            }
        }
        finally
        {
            Restore();
        }
    }

    [Fact]
    public void Registering_publishes_the_stored_preferences()
    {
        try
        {
            var preferences = new AppPreferences
            {
                RateLimitEnabled = false,
                RateLimitRequestsPerMinute = 123,
                RateLimitTokensPerMinute = 456_000,
                RateLimitAutomationGapSeconds = 7,
            };

            RateLimitComposition.RegisterSection(preferences, new SettingsSections());

            Assert.Equal(
                new ProviderRateLimitPolicy
                {
                    Enabled = false,
                    RequestsPerMinute = 123,
                    TokensPerMinute = 456_000,
                    AutomationGap = TimeSpan.FromSeconds(7),
                },
                ProviderRateLimiter.Shared.Policy);
        }
        finally
        {
            Restore();
        }
    }

    [Fact]
    public void Changing_any_of_the_four_preferences_republishes_the_policy()
    {
        try
        {
            var preferences = new AppPreferences();
            RateLimitComposition.RegisterSection(preferences, new SettingsSections());

            // Each edit is asserted on its own: a value that only follows a *different* preference's change would fail here.
            preferences.RateLimitEnabled = false;
            Assert.False(ProviderRateLimiter.Shared.Policy.Enabled);

            preferences.RateLimitRequestsPerMinute = 500;
            Assert.Equal(500, ProviderRateLimiter.Shared.Policy.RequestsPerMinute);

            preferences.RateLimitTokensPerMinute = 900_000;
            Assert.Equal(900_000, ProviderRateLimiter.Shared.Policy.TokensPerMinute);

            preferences.RateLimitAutomationGapSeconds = 25;
            Assert.Equal(TimeSpan.FromSeconds(25), ProviderRateLimiter.Shared.Policy.AutomationGap);

            // And the earlier edits are still there, so every change published the whole policy and not just its own field.
            Assert.False(ProviderRateLimiter.Shared.Policy.Enabled);
            Assert.Equal(500, ProviderRateLimiter.Shared.Policy.RequestsPerMinute);
            Assert.Equal(900_000, ProviderRateLimiter.Shared.Policy.TokensPerMinute);
        }
        finally
        {
            Restore();
        }
    }

    [Fact]
    public void An_unrelated_preference_does_not_republish()
    {
        try
        {
            var preferences = new AppPreferences();
            RateLimitComposition.RegisterSection(preferences, new SettingsSections());
            preferences.RateLimitRequestsPerMinute = 640;
            var published = ProviderRateLimiter.Shared.Policy;
            Assert.Equal(640, published.RequestsPerMinute);

            // A change the budget has nothing to do with must not reach the limiter: publishing builds a new policy object,
            // so an untouched preference leaves the very instance in place.
            preferences.UiFontSize = AppPreferences.DefaultUiFontSize + 3;

            Assert.Same(published, ProviderRateLimiter.Shared.Policy);
            Assert.Equal(640, ProviderRateLimiter.Shared.Policy.RequestsPerMinute);
        }
        finally
        {
            Restore();
        }
    }

    /// <summary>The second of the three publish moments: <c>InitializeWorkbenchAsync</c> after <c>ApplyLoaded</c>.</summary>
    [Fact]
    public void Publish_alone_reaches_the_shared_limiter_after_the_file_was_loaded()
    {
        try
        {
            var settings = new SettingsSections();
            var preferences = new AppPreferences { RateLimitTokensPerMinute = 1_234_000 };

            RateLimitComposition.Publish(preferences);

            Assert.Empty(settings.Items);
            Assert.Equal(1_234_000, ProviderRateLimiter.Shared.Policy.TokensPerMinute);
        }
        finally
        {
            Restore();
        }
    }

    /// <summary>Puts the process-wide limiter back on the policy a test that does not publish expects to find.</summary>
    private static void Restore() => ProviderRateLimiter.Shared.Publish(ProviderRateLimitPolicy.Default);
}
