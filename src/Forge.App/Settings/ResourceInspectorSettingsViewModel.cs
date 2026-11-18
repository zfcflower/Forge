using Forge.App.Preferences;
using Forge.App.ViewModels;

namespace Forge.App.Settings;

/// <summary>
/// The <c>inspector</c> settings section (<see cref="SettingsSectionIds.Inspector"/>): how many tail lines a running
/// command's output keeps in its transcript card. It edits <see cref="AppPreferences"/> directly; the value is picked up on
/// the session clock's next tick, so a change reaches an open card without restarting it. The range shown by the field is
/// the range the preference setter clamps to, so the two cannot disagree.
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

    /// <summary>Lines of a running command's output its card keeps (20 to 1000). Re-read after writing, so the field shows the clamped value.</summary>
    public int TailLines
    {
        get => preferences.InspectorLogTailLines;
        set
        {
            preferences.InspectorLogTailLines = value;
            OnPropertyChanged();
        }
    }
}
