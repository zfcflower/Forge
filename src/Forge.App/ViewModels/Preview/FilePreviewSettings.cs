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
    /// <summary>Bytes per MiB: the image limit is a megabyte preference and the preview works in bytes.</summary>
    public const int BytesPerMiB = 1024 * 1024;

    /// <summary>Characters per kilobyte: the notebook limit is a kilobyte preference and truncation counts characters.</summary>
    public const int CharsPerKilobyte = 1024;

    /// <summary>
    /// Advance of one character of the code font as a fraction of its size. The table preview lays cells out at a width
    /// computed from the character count, so this is what keeps its columns aligned; Consolas and Cascadia Mono sit near
    /// 0.55–0.6 em, and the slightly generous value below keeps a cell from clipping at the sizes the settings offer.
    /// </summary>
    public const double MonoAdvanceEm = 0.62;

    /// <summary>What a preview shows before the composition root binds the real preferences.</summary>
    private static FilePreviewSettings current = new();

    private readonly AppPreferences? preferences;
    private readonly int filePreviewRows = AppPreferences.DefaultFilesCsvMaxRows;
    private readonly int notebookOutputChars = AppPreferences.DefaultFilesNotebookOutputKilobytes * CharsPerKilobyte;
    private readonly int previewImageBytes = AppPreferences.DefaultFilesImagePreviewMaxMegabytes * BytesPerMiB;

    public FilePreviewSettings(AppPreferences? preferences = null)
    {
        this.preferences = preferences;
        if (preferences is null)
        {
            return;
        }

        filePreviewRows = preferences.FilesCsvMaxRows;
        notebookOutputChars = preferences.FilesNotebookOutputKilobytes * CharsPerKilobyte;
        previewImageBytes = preferences.FilesImagePreviewMaxMegabytes * BytesPerMiB;
    }

    /// <summary>The instance every preview reads; replaced once by the composition root.</summary>
    public static FilePreviewSettings Current
    {
        get => current;
        set => current = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Rows a CSV/TSV preview parses before it stops.</summary>
    public int PreviewRows => preferences?.FilesCsvMaxRows ?? filePreviewRows;

    /// <summary>Characters kept from one <c>.ipynb</c> output (the preference is in kilobytes).</summary>
    public int NotebookOutputChars =>
        preferences is { } live ? live.FilesNotebookOutputKilobytes * CharsPerKilobyte : notebookOutputChars;

    /// <summary>
    /// Advance of one monospace character in device-independent pixels, for the table preview's column widths: it follows
    /// the code font size, so a table stays aligned when the size changes in the appearance section.
    /// </summary>
    public double TableCharWidth =>
        (preferences?.CodeFontSize ?? AppPreferences.DefaultCodeFontSize) * MonoAdvanceEm;

    /// <summary>Largest image (in bytes, on disk) the image preview decodes (the preference is in megabytes).</summary>
    public int PreviewImageBytes =>
        preferences is { } live ? live.FilesImagePreviewMaxMegabytes * BytesPerMiB : previewImageBytes;
}
