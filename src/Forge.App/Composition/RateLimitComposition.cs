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

        // The provider table reads the live catalogue every time the section is selected, so a provider added after this module
        // was built appears there without a restart.
        RegisterSection(
            composition.Preferences,
            settings,
            () => [.. composition.Providers.Catalog.Providers.Select(static provider => (provider.Definition.Id, provider.Definition.DisplayName))]);
    }

    /// <summary>
    /// The section, its view template and the publish wiring, without the composition root: the entry point the module calls
    /// and the seam a test uses, so the registration and the published policy are provable without building the whole graph.
    /// </summary>
    internal static void RegisterSection(
        AppPreferences preferences,
        SettingsSections settings,
        Func<IReadOnlyList<(string Id, string Name)>>? providers = null)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(settings);

        settings.Add(new SettingsSection(
            SettingsSectionIds.RateLimit,
            RateLimitStrings.SettingsTitleKey,
            SettingsSectionIds.RateLimitOrder,
            new RateLimitSettingsSectionViewModel(preferences, providers),
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
    internal static void Publish(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ProviderRateLimiter.Shared.Publish(new ProviderRateLimitPolicy
        {
            Enabled = preferences.RateLimitEnabled,
            RequestsPerMinute = preferences.RateLimitRequestsPerMinute,
            TokensPerMinute = preferences.RateLimitTokensPerMinute,
            AutomationGap = TimeSpan.FromSeconds(preferences.RateLimitAutomationGapSeconds),
            Overrides = Overrides(preferences),
        });
    }

    /// <summary>
    /// The per-provider ceilings the table holds, keyed by provider id. Built on every publish (startup, a settings edit) from
    /// the normalized preference entries; the limiter's buckets are reconfigured in place from it, so an entry added, changed or
    /// cleared reaches the provider that is already running.
    /// </summary>
    private static IReadOnlyDictionary<string, ProviderRateLimitLimits> Overrides(AppPreferences preferences)
    {
        if (preferences.RateLimitOverrides.Count == 0)
        {
            return ProviderRateLimitPolicy.NoOverrides;
        }

        var overrides = new Dictionary<string, ProviderRateLimitLimits>(StringComparer.Ordinal);
        foreach (var entry in preferences.RateLimitOverrides)
        {
            var parts = entry.Split(AppPreferences.RateLimitOverrideSeparator);
            if (parts.Length < 3)
            {
                continue;
            }

            // The preference layer already normalized the numbers; the parse is only turning them back into the policy's shape
            // (an empty field stays null, which inherits).
            overrides[parts[0]] = new ProviderRateLimitLimits(
                Parse(parts[1]),
                Parse(parts[2]));
        }

        return overrides.Count == 0 ? ProviderRateLimitPolicy.NoOverrides : overrides;

        static int? Parse(string text) =>
            int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

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
        nameof(AppPreferences.RateLimitOverrides) => true,
        _ => false,
    };
}
