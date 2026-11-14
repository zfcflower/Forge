using System.Collections.ObjectModel;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;

namespace Forge.App.Settings;

/// <summary>
/// One provider's row in the <c>rate-limit</c> section: its own request and token ceilings, or blank to inherit the global
/// numbers. The fields write through to <see cref="AppPreferences.SetRateLimitOverride"/> on every change, so the next
/// published policy carries them and the next request obeys them; nothing is cached here.
/// </summary>
internal sealed class RateLimitOverrideRowViewModel : ObservableObject
{
    private readonly AppPreferences preferences;
    private int? requestsPerMinute;
    private int? tokensPerMinute;

    public RateLimitOverrideRowViewModel(string providerId, string providerName, AppPreferences preferences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(providerName);
        ArgumentNullException.ThrowIfNull(preferences);
        ProviderId = providerId;
        ProviderName = providerName;
        this.preferences = preferences;
        (requestsPerMinute, tokensPerMinute) = preferences.RateLimitOverrideFor(providerId) ?? (null, null);
    }

    /// <summary>The provider's id, as the budget is keyed by it; also the automation id of the row's fields.</summary>
    public string ProviderId { get; }

    /// <summary>The provider's display name, the row's label (provider names are not translated).</summary>
    public string ProviderName { get; }

    /// <summary>Smallest value the request field offers; 0 means "no limit for this provider".</summary>
    public int MinRequestsPerMinute => 0;

    public int MaxRequestsPerMinute => AppPreferences.MaxRateLimitRequestsPerMinute;

    /// <summary>This provider's requests per minute, or null to inherit the global number.</summary>
    public int? RequestsPerMinute
    {
        get => requestsPerMinute;
        set
        {
            if (Set(ref requestsPerMinute, Clamp(value, MaxRequestsPerMinute)))
            {
                Write();
            }
        }
    }

    /// <summary>Smallest value the token field offers; 0 means "no limit for this provider".</summary>
    public int MinTokensPerMinute => 0;

    public int MaxTokensPerMinute => AppPreferences.MaxRateLimitTokensPerMinute;

    /// <summary>This provider's tokens per minute, or null to inherit the global number.</summary>
    public int? TokensPerMinute
    {
        get => tokensPerMinute;
        set
        {
            if (Set(ref tokensPerMinute, Clamp(value, MaxTokensPerMinute)))
            {
                Write();
            }
        }
    }

    /// <summary>Clears both fields, which removes the provider's entry entirely.</summary>
    public void Clear()
    {
        requestsPerMinute = null;
        tokensPerMinute = null;
        OnPropertyChanged(nameof(RequestsPerMinute));
        OnPropertyChanged(nameof(TokensPerMinute));
        Write();
    }

    private void Write() => preferences.SetRateLimitOverride(ProviderId, requestsPerMinute, tokensPerMinute);

    /// <summary>The range the field offers is the range the preference accepts, so the two cannot disagree.</summary>
    private static int? Clamp(int? value, int maximum) => value is { } number ? Math.Clamp(number, 0, maximum) : null;
}

/// <summary>
/// The <c>rate-limit</c> settings section (<see cref="SettingsSectionIds.RateLimit"/>): whether the local per-provider budget
/// is enforced, the ceilings every provider starts with, the spacing of automated resumes, and a table of the connected
/// providers for the ones whose real limits are tighter than those defaults (a self-hosted gateway, usually).
/// <para>
/// It edits <see cref="AppPreferences"/> directly and caches no numbers: <see cref="Composition.RateLimitComposition"/>
/// republishes the policy on every change, and the limiter reads it on the next request. The provider table is rebuilt in
/// <see cref="Reload"/> only — the settings page calls that when the section is selected, never on a timer — so the catalogue
/// is read exactly when it is shown.
/// </para>
/// </summary>
internal sealed class RateLimitSettingsSectionViewModel : ObservableObject, IReloadableSettingsSection
{
    private readonly AppPreferences preferences;
    private readonly Func<IReadOnlyList<(string Id, string Name)>>? providers;

    public RateLimitSettingsSectionViewModel(
        AppPreferences preferences,
        Func<IReadOnlyList<(string Id, string Name)>>? providers = null)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        this.preferences = preferences;
        this.providers = providers;
    }

    /// <summary>Whether the local budget is enforced; off makes the limiter authorize every request without arithmetic, from the next one.</summary>
    public bool Enabled
    {
        get => preferences.RateLimitEnabled;
        set
        {
            preferences.RateLimitEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Smallest request ceiling the field offers; one request per minute is the strictest budget that still lets a turn finish.</summary>
    public int MinRequestsPerMinute => AppPreferences.MinRateLimitRequestsPerMinute;

    /// <summary>Largest request ceiling the field offers.</summary>
    public int MaxRequestsPerMinute => AppPreferences.MaxRateLimitRequestsPerMinute;

    /// <summary>Requests per minute one provider may receive (1 to 100000); the next request already waits for it. Re-read after writing, so the field shows the clamped value.</summary>
    public int RequestsPerMinute
    {
        get => preferences.RateLimitRequestsPerMinute;
        set
        {
            preferences.RateLimitRequestsPerMinute = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Smallest token ceiling the field offers; a smaller budget than one long prompt would make every request exceed capacity.</summary>
    public int MinTokensPerMinute => AppPreferences.MinRateLimitTokensPerMinute;

    /// <summary>Largest token ceiling the field offers.</summary>
    public int MaxTokensPerMinute => AppPreferences.MaxRateLimitTokensPerMinute;

    /// <summary>Tokens per minute one provider may receive (1000 to 1000000000); charged on the estimate before the request and reconciled with the real usage after it.</summary>
    public int TokensPerMinute
    {
        get => preferences.RateLimitTokensPerMinute;
        set
        {
            preferences.RateLimitTokensPerMinute = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Shortest gap the field offers; zero is a legal choice and means automated resumes are not spaced at all.</summary>
    public int MinAutomationGapSeconds => AppPreferences.MinRateLimitAutomationGapSeconds;

    /// <summary>Longest gap the field offers; ten minutes is already far past any provider's window.</summary>
    public int MaxAutomationGapSeconds => AppPreferences.MaxRateLimitAutomationGapSeconds;

    /// <summary>Seconds between two automated resumes of the same provider (0 to 600); the next resume already waits for it.</summary>
    public int AutomationGapSeconds
    {
        get => preferences.RateLimitAutomationGapSeconds;
        set
        {
            preferences.RateLimitAutomationGapSeconds = value;
            OnPropertyChanged();
        }
    }

    /// <summary>One row per connected provider, in catalogue order; empty when no provider is connected.</summary>
    public ObservableCollection<RateLimitOverrideRowViewModel> Rows { get; } = [];

    /// <summary>True when there is a provider to give its own ceilings to; the table is hidden otherwise.</summary>
    public bool HasProviders => Rows.Count > 0;

    /// <summary>Rebuilds the provider table from the live catalogue.</summary>
    public void Reload()
    {
        Rows.Clear();
        if (providers is not null)
        {
            foreach (var (id, name) in providers())
            {
                Rows.Add(new RateLimitOverrideRowViewModel(id, name, preferences));
            }
        }

        OnPropertyChanged(nameof(HasProviders));
    }

    public string OverridesHeading => RateLimitStrings.SettingsOverrides;

    public string OverridesHint => RateLimitStrings.SettingsOverridesHint;

    public string NoProviders => RateLimitStrings.SettingsNoProviders;

    public string RequestsColumn => RateLimitStrings.SettingsRequestsColumn;

    public string TokensColumn => RateLimitStrings.SettingsTokensColumn;

    public string InheritHint => RateLimitStrings.SettingsInheritHint;
}
