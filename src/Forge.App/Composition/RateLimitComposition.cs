using System.ComponentModel;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.Views;
using Forge.Core.Providers;

namespace Forge.App.Composition;

/// <summary>
/// Wave W15's composition module: the <c>rate-limit</c> settings section, its view, and the one runtime wiring the whole wave
/// needs — publishing the user's numbers into <see cref="ProviderRateLimiter.Shared"/>, which every model request is acquired
/// from (the retry middleware already defaults to it). <see cref="FeatureComposition.RegisterAll"/> calls this, so the wave
/// never edits the settings page or <c>App.axaml</c>. Section ids and orders are in <see cref="SettingsSectionIds"/>.
/// <para>
/// Publishing happens at three moments, all of them needed: here, so a session that starts before the preferences have been
/// read is not given the loose defaults as its policy; from
/// <see cref="AppComposition.InitializeWorkbenchAsync"/> once the file has been read, because
/// <see cref="AppPreferences.ApplyLoaded"/> deliberately raises no change notification (the same pattern the appearance
/// feature uses); and on every change of one of the four values, so an edit reaches running sessions on their next request.
/// </para>
/// </summary>
internal static class RateLimitComposition
{
    public static void Register(AppComposition composition, SettingsSections settings)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(settings);

        RegisterSection(composition.Preferences, settings);
    }

    /// <summary>
    /// The section, its view template and the publish wiring, without the composition root: the entry point the module calls
    /// and the seam a test uses, so the registration and the published policy are provable without building the whole graph.
    /// </summary>
    internal static void RegisterSection(AppPreferences preferences, SettingsSections settings)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(settings);

        settings.Add(new SettingsSection(
            SettingsSectionIds.RateLimit,
            RateLimitStrings.SettingsTitleKey,
            SettingsSectionIds.RateLimitOrder,
            new RateLimitSettingsSectionViewModel(preferences),
            SettingsGroups.Agent,
            "IconLock"));
        SectionViewTemplate.Register<RateLimitSettingsSectionViewModel>(static () => new RateLimitSettingsSectionView());

        preferences.PropertyChanged += OnPreferenceChanged;
        Publish(preferences);
    }

    /// <summary>
    /// Publishes the policy the limiter should enforce. <see cref="ProviderRateLimiter.Publish"/> reconfigures the buckets that
    /// already exist in place, so a lowered ceiling takes effect on the next request without rebuilding a provider; a policy
    /// change can therefore arrive from any thread and needs no dispatcher.
    /// </summary>
    internal static void Publish(AppPreferences preferences) =>
        ProviderRateLimiter.Shared.Publish(new ProviderRateLimitPolicy
        {
            Enabled = preferences.RateLimitEnabled,
            RequestsPerMinute = preferences.RateLimitRequestsPerMinute,
            TokensPerMinute = preferences.RateLimitTokensPerMinute,
            AutomationGap = TimeSpan.FromSeconds(preferences.RateLimitAutomationGapSeconds),
        });

    private static void OnPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is AppPreferences preferences && IsRateLimitProperty(e.PropertyName))
        {
            Publish(preferences);
        }
    }

    private static bool IsRateLimitProperty(string? name) => name switch
    {
        nameof(AppPreferences.RateLimitEnabled) => true,
        nameof(AppPreferences.RateLimitRequestsPerMinute) => true,
        nameof(AppPreferences.RateLimitTokensPerMinute) => true,
        nameof(AppPreferences.RateLimitAutomationGapSeconds) => true,
        _ => false,
    };
}
