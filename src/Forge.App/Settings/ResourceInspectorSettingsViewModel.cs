using Forge.App.Preferences;
using Forge.App.ViewModels;

namespace Forge.App.Settings;

/// <summary>
/// The <c>inspector</c> settings section (<see cref="SettingsSectionIds.Inspector"/>): how many tail lines the resource
/// inspector's output view keeps, and how often that view re-reads a running command's output. It edits
/// <see cref="AppPreferences"/> directly; both values are picked up on the next read — the tail length on every refresh,
/// the interval when the panel next builds its timer — so a change reaches an open overlay without restarting it. The
/// elapsed times are not driven by this interval: they follow the session clock, which ticks once a second. The range shown
/// by each field is the range the preference setter clamps to, so the two cannot disagree.
/// </summary>
internal sealed class ResourceInspectorSettingsViewModel : ObservableObject
{
    private readonly AppPreferences preferences;

    public ResourceInspectorSettingsViewModel(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        this.preferences = preferences;
    }

    /// <summary>Smallest tail the field offers; a shorter tail could not show a full command's recent output.</summary>
    public int MinTailLines => AppPreferences.MinInspectorLogTailLines;

    /// <summary>Largest tail the field offers.</summary>
    public int MaxTailLines => AppPreferences.MaxInspectorLogTailLines;

    /// <summary>Lines of a running command's output the inspector keeps (20 to 1000). Re-read after writing, so the field shows the clamped value.</summary>
    public int TailLines
    {
        get => preferences.InspectorLogTailLines;
        set
        {
            preferences.InspectorLogTailLines = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Shortest refresh the field offers; faster ticking would spin the UI for nothing.</summary>
    public int MinRefreshMilliseconds => AppPreferences.MinInspectorRefreshMilliseconds;

    /// <summary>Longest refresh the field offers.</summary>
    public int MaxRefreshMilliseconds => AppPreferences.MaxInspectorRefreshMilliseconds;

    /// <summary>How often the inspector re-reads its running commands' output, in milliseconds (250 to 10000). Re-read after writing, so the field shows the clamped value.</summary>
    public int RefreshMilliseconds
    {
        get => preferences.InspectorRefreshMilliseconds;
        set
        {
            preferences.InspectorRefreshMilliseconds = value;
            OnPropertyChanged();
        }
    }
}
