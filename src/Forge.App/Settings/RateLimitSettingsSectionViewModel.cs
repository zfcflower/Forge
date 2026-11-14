using Forge.App.Preferences;
using Forge.App.ViewModels;

namespace Forge.App.Settings;

/// <summary>
/// The <c>rate-limit</c> settings section (<see cref="SettingsSectionIds.RateLimit"/>): whether the local per-provider budget
/// is enforced, how many requests and tokens one provider may spend per minute, and how far apart two automated resumes of
/// the same provider are kept. It edits <see cref="AppPreferences"/> directly; nothing is cached here — the limiter is
/// republished from those preferences on every change and reads the policy on the next request, so the fields need no apply
/// step and no restart. The range each field offers is the range the preference setter clamps to, so the two cannot disagree.
/// </summary>
internal sealed class RateLimitSettingsSectionViewModel : ObservableObject
{
    private readonly AppPreferences preferences;

    public RateLimitSettingsSectionViewModel(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        this.preferences = preferences;
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
}
