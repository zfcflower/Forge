using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Forge.App.ViewModels.Preview;

namespace Forge.App.Settings;

/// <summary>
/// The <c>files</c> settings section (<see cref="SettingsSectionIds.Files"/>): the three limits the file viewer reads.
/// Edits <see cref="AppPreferences"/> directly, so every change applies to the next preview and is persisted by the
/// preferences store's debounce. The image limit is edited in MiB: the byte preference is divided on the way out and
/// multiplied on the way in.
/// </summary>
internal sealed class FilesSettingsSectionViewModel : ObservableObject, IDisposable
{
    private readonly AppPreferences preferences;

    /// <summary>The thread the section was built on (the UI thread); a language notification from another one is not ours.</summary>
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    private bool disposed;

    public FilesSettingsSectionViewModel(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        this.preferences = preferences;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Drops the <see cref="Loc.LanguageChanged"/> subscription; idempotent.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>Rows a CSV/TSV preview parses before it stops.</summary>
    public int PreviewRows
    {
        get => preferences.FilePreviewRows;
        set
        {
            preferences.FilePreviewRows = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Characters kept from one <c>.ipynb</c> output.</summary>
    public int NotebookOutputChars
    {
        get => preferences.NotebookOutputChars;
        set
        {
            preferences.NotebookOutputChars = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The image preview limit in MiB; the byte preference is divided and multiplied on the way through.</summary>
    public int ImageLimitMiB
    {
        get => preferences.PreviewImageBytes / FilePreviewSettings.BytesPerMiB;
        set
        {
            // Multiply in 64-bit: a nonsense edit must clamp in the preference setter, not overflow first.
            var bytes = (long)value * FilePreviewSettings.BytesPerMiB;
            preferences.PreviewImageBytes = bytes > int.MaxValue ? int.MaxValue : (int)bytes;
            OnPropertyChanged();
        }
    }

    public string PreviewRowsLabel => Loc.Instance[PreviewStrings.SettingsRowsKey];

    public string NotebookOutputCharsLabel => Loc.Instance[PreviewStrings.SettingsNotebookCharsKey];

    public string ImageLimitLabel => Loc.Instance[PreviewStrings.SettingsImageLimitKey];

    public string Hint => Loc.Instance[PreviewStrings.SettingsHintKey];

    /// <summary>Re-reads the labels the view binds to, like every other section with localized text.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (disposed || Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        OnPropertyChanged(nameof(PreviewRowsLabel));
        OnPropertyChanged(nameof(NotebookOutputCharsLabel));
        OnPropertyChanged(nameof(ImageLimitLabel));
        OnPropertyChanged(nameof(Hint));
    }
}
