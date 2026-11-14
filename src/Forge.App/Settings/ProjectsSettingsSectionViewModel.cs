using Forge.App.Preferences;
using Forge.App.ViewModels;

namespace Forge.App.Settings;

/// <summary>
/// The <c>projects</c> settings section: where a cloned or created project goes, and how long a clone may run. Both write
/// the live preferences, so the next add-project dialog uses them without a restart; the folder picker itself belongs to the
/// view (it needs a window), which answers by setting <see cref="CloneDirectory"/>.
/// </summary>
internal sealed class ProjectsSettingsSectionViewModel : NotifyingObject
{
    private readonly AppPreferences preferences;
    private readonly string fallbackParentDirectory;

    /// <param name="preferences">The live preferences the section edits.</param>
    /// <param name="fallbackParentDirectory">
    /// Where projects go while <see cref="CloneDirectory"/> is empty (the parent of the general workspace); shown as the
    /// effective value so an empty field never reads as "nowhere".
    /// </param>
    public ProjectsSettingsSectionViewModel(AppPreferences preferences, string fallbackParentDirectory)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackParentDirectory);
        this.preferences = preferences;
        this.fallbackParentDirectory = fallbackParentDirectory;
        BrowseCommand = new RelayCommand(() => BrowseRequested?.Invoke(this, EventArgs.Empty));
        ClearCommand = new RelayCommand(() => CloneDirectory = string.Empty, () => CloneDirectory.Length > 0);
    }

    /// <summary>The view opens the folder picker for this request; the answer arrives through <see cref="CloneDirectory"/>.</summary>
    public event EventHandler? BrowseRequested;

    /// <summary>Longest a clone may run in seconds; <c>0</c> means no timeout at all.</summary>
    public int CloneTimeoutSeconds
    {
        get => preferences.ProjectsCloneTimeoutSeconds;
        set
        {
            if (preferences.ProjectsCloneTimeoutSeconds == value)
            {
                return;
            }

            preferences.ProjectsCloneTimeoutSeconds = value;
            OnPropertyChanged();
        }
    }

    public int MinCloneTimeoutSeconds => AppPreferences.MinProjectsCloneTimeoutSeconds;

    public int MaxCloneTimeoutSeconds => AppPreferences.MaxProjectsCloneTimeoutSeconds;

    /// <summary>Where new project folders are created; empty means <see cref="EffectiveDirectoryText"/>.</summary>
    public string CloneDirectory
    {
        get => preferences.ProjectsCloneDirectory;
        set
        {
            var text = (value ?? string.Empty).Trim();
            if (string.Equals(preferences.ProjectsCloneDirectory, text, StringComparison.Ordinal))
            {
                return;
            }

            preferences.ProjectsCloneDirectory = text;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveDirectoryText));
            OnPropertyChanged(nameof(UsesDefaultDirectory));
            ClearCommand.NotifyCanExecuteChanged();
        }
    }

    public bool UsesDefaultDirectory => CloneDirectory.Length == 0;

    /// <summary>The directory the next dialog actually starts in, so an empty field shows what it falls back to.</summary>
    public string EffectiveDirectoryText => UsesDefaultDirectory ? fallbackParentDirectory : CloneDirectory;

    /// <summary>Opens the folder picker; the view subscribes and answers by setting <see cref="CloneDirectory"/>.</summary>
    public RelayCommand BrowseCommand { get; }

    /// <summary>Empties the field, so clones follow the fallback directory again.</summary>
    public RelayCommand ClearCommand { get; }
}
