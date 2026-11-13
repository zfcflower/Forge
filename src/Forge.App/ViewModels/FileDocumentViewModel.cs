using Forge.App.Localization;
using Forge.Editor.TextMate;

namespace Forge.App.ViewModels;

/// <summary>
/// File view model: the native editor renders and highlights the text. The file's content and path are data
/// and are never translated; only the tab title's leading mark and the placeholder before a file is loaded come from the
/// string table, and both are read when the property is read.
/// </summary>
internal sealed partial class FileDocumentViewModel : NotifyingObject
{
    private readonly GrammarRegistry? grammars;
    private string path = string.Empty;
    private string? fileName;
    private LocText error;
    private bool isLoading;

    private FileDocumentViewModel(GrammarRegistry? grammars)
    {
        this.grammars = grammars;
        LoadCommand = new AsyncRelayCommand(
            ct => LoadAsync(path, ct),
            () => path.Length > 0,
            exception => SetError(LocText.Literal(exception.Message)));
    }

    public AsyncRelayCommand LoadCommand { get; }

    public string Path
    {
        get => path;
        private set => SetAndNotify(ref path, value, nameof(PathDisplay));
    }

    public string PathDisplay => path;

    /// <summary>The file name, or the generic word until a file is loaded; the tab's leading mark is its icon, not a glyph.</summary>
    public string TabTitle => InspectorStrings.FileTabTitle((fileName ?? Loc.Instance[InspectorStrings.FilesName]) + (IsDirty ? " *" : string.Empty));

    public bool IsLoading
    {
        get => isLoading;
        private set => Set(ref isLoading, value);
    }

    /// <summary>The read problem (missing file, too large, truncated, an OS error), in the language of the moment it is read.</summary>
    public string? Error => error.IsEmpty ? null : error.Resolve();

    public bool HasError => !error.IsEmpty;

    private void SetError(LocText value)
    {
        error = value;
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
    }

}
