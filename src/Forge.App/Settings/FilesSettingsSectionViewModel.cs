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
        get => preferences.FilesCsvMaxRows;
        set
        {
            preferences.FilesCsvMaxRows = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Kilobytes kept from one <c>.ipynb</c> output (the preview counts 1,024 characters per kilobyte).</summary>
    public int NotebookOutputKilobytes
    {
        get => preferences.FilesNotebookOutputKilobytes;
        set
        {
            preferences.FilesNotebookOutputKilobytes = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The image preview limit in MiB; the preference itself is stored in megabytes, so no conversion is needed.</summary>
    public int ImageLimitMiB
    {
        get => preferences.FilesImagePreviewMaxMegabytes;
        set
        {
            preferences.FilesImagePreviewMaxMegabytes = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Bounds of the three controls, mirrored from the preferences' clamps so both stay one number.</summary>
    public decimal MinRows => AppPreferences.MinFilesCsvMaxRows;

    public decimal MaxRows => AppPreferences.MaxFilesCsvMaxRows;

    public decimal MinNotebookKilobytes => AppPreferences.MinFilesNotebookOutputKilobytes;

    public decimal MaxNotebookKilobytes => AppPreferences.MaxFilesNotebookOutputKilobytes;

    public decimal MinImageMegabytes => AppPreferences.MinFilesImagePreviewMaxMegabytes;

    public decimal MaxImageMegabytes => AppPreferences.MaxFilesImagePreviewMaxMegabytes;

    public string PreviewRowsLabel => Loc.Instance[PreviewStrings.SettingsRowsKey];

    public string NotebookOutputLabel => Loc.Instance[PreviewStrings.SettingsNotebookKilobytesKey];

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
        OnPropertyChanged(nameof(NotebookOutputLabel));
        OnPropertyChanged(nameof(ImageLimitLabel));
        OnPropertyChanged(nameof(Hint));
    }
}
