using Forge.App.Preferences;

namespace Forge.App.ViewModels.Preview;

/// <summary>
/// The live limits every file preview reads: a projection of <see cref="AppPreferences"/> with a settable static
/// instance, mirroring <see cref="Forge.App.Editing.EditorSettings.Current"/>. The composition root replaces
/// <see cref="Current"/> with an instance bound to the loaded preferences before the first window opens; every getter
/// reads through to the preferences, so a change in the settings page applies to the next preview without a restart.
/// <see cref="FileDocumentViewModel.Create"/> can also pin one instance (e.g. for a test).
/// </summary>
internal sealed class FilePreviewSettings
{
    /// <summary>Bytes per MiB: the unit the image limit is edited in.</summary>
    public const int BytesPerMiB = 1024 * 1024;

    /// <summary>What a preview shows before the composition root binds the real preferences.</summary>
    private static FilePreviewSettings current = new();

    private readonly AppPreferences? preferences;
    private readonly int filePreviewRows = AppPreferences.DefaultFilePreviewRows;
    private readonly int notebookOutputChars = AppPreferences.DefaultNotebookOutputChars;
    private readonly int previewImageBytes = AppPreferences.DefaultPreviewImageBytes;

    public FilePreviewSettings(AppPreferences? preferences = null)
    {
        this.preferences = preferences;
        if (preferences is null)
        {
            return;
        }

        filePreviewRows = preferences.FilePreviewRows;
        notebookOutputChars = preferences.NotebookOutputChars;
        previewImageBytes = preferences.PreviewImageBytes;
    }

    /// <summary>The instance every preview reads; replaced once by the composition root.</summary>
    public static FilePreviewSettings Current
    {
        get => current;
        set => current = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Rows a CSV/TSV preview parses before it stops.</summary>
    public int PreviewRows => preferences?.FilePreviewRows ?? filePreviewRows;

    /// <summary>Characters kept from one <c>.ipynb</c> output.</summary>
    public int NotebookOutputChars => preferences?.NotebookOutputChars ?? notebookOutputChars;

    /// <summary>Largest image (in bytes, on disk) the image preview decodes.</summary>
    public int PreviewImageBytes => preferences?.PreviewImageBytes ?? previewImageBytes;
}
