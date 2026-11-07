using System.Diagnostics;
using Forge.App.Editing;
using Forge.App.Localization;
using Forge.Editor.Languages;
using Forge.Editor.Lsp;
using Forge.Editor.Text;
using Forge.Editor.TextMate;

namespace Forge.App.ViewModels;

internal sealed partial class FileDocumentViewModel
{
    public const long MaxFileBytes = 8L * 1024 * 1024;
    private readonly record struct FileRead(TextDocument? Document, bool Missing, long TooLargeBytes, string? Text = null);
    private LanguageSessionPool? sessions;
    private LanguageSession? session;
    private string? openWorkspace;
    private TextDocument? document;
    private ForgeEditorMirror? editorMirror;
    private AsyncRelayCommand? saveCommand;
    private CancellationTokenSource? loadCts;
    private int loadVersion;
    private CancellationTokenSource? lspCts;
    private Task lspOpening = Task.CompletedTask;
    private bool isEditing;
    private bool wrapLines;
    private bool isSaving;
    private string editText = string.Empty;
    private LocText saveError;
    private LocText lspStatus;
    private LocText editorNotice;
    private LocText copyError;
    private RelayCommand? toggleWrapCommand;
    private RelayCommand? toggleEditCommand;

    public string? Workspace { get; set; }
    public Task LspOpening => lspOpening;
    public bool HasDocument => document is not null;
    internal TextDocument? EditorDocument => document;
    internal ForgeEditorMirror? EditorMirror => editorMirror;
    public bool IsEditorFaulted => editorMirror?.IsFaulted ?? false;
    internal GrammarRegistry? Grammars => grammars;
    private string CurrentText => document is { } current ? current.Snapshot.GetText() : editText;
    public bool IsDirty => IsEditorFaulted || (document?.IsDirty ?? false);
    public bool CanSave => document is not null && IsDirty && !isSaving && !IsEditorFaulted;
    public string NewLineText => document?.FileFormat?.LineEnding switch
    {
        LineEnding.CrLf => "\r\n",
        LineEnding.Cr => "\r",
        _ => "\n",
    };

    public bool WrapLines
    {
        get => wrapLines;
        set => Set(ref wrapLines, value);
    }

    public bool IsEditing
    {
        get => isEditing;
        set
        {
            if (value && document is null)
            {
                OnPropertyChanged();
                return;
            }

            Set(ref isEditing, value);
        }
    }

    public string EditText
    {
        get => CurrentText;
        set => ApplyEdit(value ?? string.Empty);
    }

    public bool IsSaving
    {
        get => isSaving;
        private set
        {
            if (Set(ref isSaving, value))
            {
                NotifyDirty();
            }
        }
    }

    public string? SaveError => saveError.IsEmpty ? null : saveError.Resolve();
    public bool HasSaveError => !saveError.IsEmpty;
    public string? LspStatus => lspStatus.IsEmpty ? null : lspStatus.Resolve();
    public bool HasLspStatus => !lspStatus.IsEmpty;
    public string? EditorNotice => editorNotice.IsEmpty ? null : editorNotice.Resolve();
    public bool HasEditorNotice => !editorNotice.IsEmpty;
    public string? CopyError => copyError.IsEmpty ? null : copyError.Resolve();
    public bool HasCopyError => !copyError.IsEmpty;
    public RelayCommand ToggleWrapCommand => toggleWrapCommand ??= new RelayCommand(() => WrapLines = !WrapLines);
    public RelayCommand ToggleEditCommand => toggleEditCommand ??= new RelayCommand(() => IsEditing = !IsEditing);
    public AsyncRelayCommand SaveCommand => saveCommand ??= new AsyncRelayCommand(
        SaveCoreAsync,
        () => CanSave,
        exception =>
        {
            Trace.WriteLine("[file] save failed: " + exception.Message);
            SetSaveError(new LocText(InspectorStrings.FilesSaveFailed, LocText.Literal(exception.Message)));
        });

    public static FileDocumentViewModel Create(GrammarRegistry? grammars = null, LanguageSessionPool? sessions = null) =>
        new(grammars) { sessions = sessions };

    public async Task LoadAsync(string filePath, CancellationToken ct, bool discardEdits = false)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        if (isSaving)
        {
            SetSaveError(new LocText(InspectorStrings.FilesBusy));
            return;
        }

        if (IsDirty && !discardEdits)
        {
            SetError(new LocText(InspectorStrings.FilesUnsaved));
            return;
        }

        loadCts?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        loadCts = cts;
        var version = ++loadVersion;
        var token = cts.Token;
        try
        {
            var full = System.IO.Path.GetFullPath(filePath);
            await ReleaseCoreAsync().ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            Path = full;
            fileName = System.IO.Path.GetFileName(full);
            OnPropertyChanged(nameof(TabTitle));
            SetError(default);
            SetSaveError(default);
            SetLspStatus(default);
            ReportEditorNotice(default);
            IsLoading = true;
            var read = await Task.Run(() => ReadFileAsync(full, token), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (read.Missing)
            {
                SetError(InspectorStrings.FileMissing(filePath));
                return;
            }

            if (read.Document is null)
            {
                SetError(InspectorStrings.FileTooLarge(read.TooLargeBytes));
                return;
            }

            AttachDocument(read.Document, read.Text ?? string.Empty);
        }
        catch (OperationCanceledException) when (version != loadVersion && !ct.IsCancellationRequested)
        {
            // The newer load owns the state.
        }
        catch (Exception exception) when (version != loadVersion && exception is not OperationCanceledException)
        {
            Trace.WriteLine("[file] superseded load failed: " + exception.Message);
        }
        catch (NotSupportedException)
        {
            SetError(InspectorStrings.FileBinary(Path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.WriteLine("[file] load failed: " + exception.Message);
            SetError(LocText.Literal(exception.Message));
        }
        finally
        {
            if (version == loadVersion)
            {
                IsLoading = false;
            }

            if (ReferenceEquals(loadCts, cts))
            {
                loadCts = null;
            }
        }
    }

    public async Task ReleaseDocumentAsync()
    {
        loadCts?.Cancel();
        loadVersion++;
        IsLoading = false;
        await ReleaseCoreAsync().ConfigureAwait(true);
    }

    private async Task ReleaseCoreAsync()
    {
        CancelLsp();
        var doc = document;
        if (doc is null)
        {
            return;
        }

        var pool = sessions;
        var workspace = openWorkspace;
        document = null;
        editorMirror?.Dispose();
        editorMirror = null;
        OnPropertyChanged(nameof(IsEditorFaulted));
        session = null;
        openWorkspace = null;
        isEditing = false;
        editText = string.Empty;
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(EditText));
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(EditorDocument));
        NotifyDirty();
        if (pool is not null && workspace is not null)
        {
            await CloseInPoolAsync(pool, workspace, doc).ConfigureAwait(true);
        }
    }

    private static async Task<FileRead> ReadFileAsync(string full, CancellationToken ct)
    {
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            return new FileRead(null, true, 0);
        }

        if (info.Length > MaxFileBytes)
        {
            return new FileRead(null, false, info.Length);
        }

        var loaded = await TextDocument.LoadAsync(full, ct).ConfigureAwait(false);
        return new FileRead(loaded, false, 0, loaded.Snapshot.GetText());
    }

    private static bool IsLspFailure(Exception exception) =>
        exception is LspProtocolException or LanguageServerCrashedException or LanguageServerNotFoundException
            or IOException or InvalidOperationException;

    private static async Task CloseInPoolAsync(LanguageSessionPool pool, string workspace, TextDocument doc)
    {
        try
        {
            await pool.CloseDocumentAsync(workspace, doc).ConfigureAwait(true);
        }
        catch (Exception exception) when (IsLspFailure(exception) || exception is OperationCanceledException)
        {
            Trace.WriteLine("[file] closing the document in the language server failed: " + exception.Message);
        }
    }

    private void CancelLsp()
    {
        var cts = lspCts;
        lspCts = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
    }

    private void AttachDocument(TextDocument doc, string text)
    {
        document = doc;
        editText = text;
        isEditing = true;
        editorMirror?.Dispose();
        editorMirror = new ForgeEditorMirror(this, doc);
        OnPropertyChanged(nameof(IsEditorFaulted));
        OnPropertyChanged(nameof(EditText));
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(NewLineText));
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(EditorDocument));
        NotifyDirty();
        var pool = sessions;
        var workspace = Workspace;
        if (pool is null || string.IsNullOrEmpty(workspace))
        {
            return;
        }

        openWorkspace = workspace;
        CancelLsp();
        var cts = new CancellationTokenSource();
        lspCts = cts;
        lspOpening = OpenInLspAsync(pool, workspace, doc, cts);
    }

    private async Task OpenInLspAsync(LanguageSessionPool pool, string workspace, TextDocument doc, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            var opened = await pool.OpenDocumentAsync(workspace, doc, ct).ConfigureAwait(true);
            if (!ReferenceEquals(document, doc))
            {
                await CloseInPoolAsync(pool, workspace, doc).ConfigureAwait(true);
                return;
            }

            session = opened;
            SetLspStatus(opened is { State: LanguageSessionState.Unavailable } ? new LocText(InspectorStrings.FilesLspStopped) : default);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CloseInPoolAsync(pool, workspace, doc).ConfigureAwait(true);
        }
        catch (Exception exception) when (IsLspFailure(exception))
        {
            Trace.WriteLine("[file] language server unavailable: " + exception.Message);
            if (ReferenceEquals(document, doc))
            {
                session = null;
                SetLspStatus(new LocText(InspectorStrings.FilesLspUnavailable, LocText.Literal(exception.Message)));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.WriteLine("[file] unexpected language server open failure: " + exception.Message);
            if (ReferenceEquals(document, doc))
            {
                session = null;
                SetLspStatus(new LocText(InspectorStrings.FilesLspUnavailable, LocText.Literal(exception.Message)));
            }
        }
        finally
        {
            if (ReferenceEquals(lspCts, cts))
            {
                lspCts = null;
            }

            cts.Dispose();
        }
    }

    private void ApplyEdit(string value)
    {
        var doc = document;
        var old = CurrentText;
        if (doc is null || string.Equals(old, value, StringComparison.Ordinal))
        {
            return;
        }

        var max = Math.Min(old.Length, value.Length);
        var prefix = 0;
        while (prefix < max && old[prefix] == value[prefix])
        {
            prefix++;
        }

        if (prefix > 0 && char.IsHighSurrogate(old[prefix - 1]))
        {
            prefix--;
        }

        var suffix = 0;
        var limit = max - prefix;
        while (suffix < limit && old[old.Length - 1 - suffix] == value[value.Length - 1 - suffix])
        {
            suffix++;
        }

        if (suffix > 0 && char.IsLowSurrogate(old[old.Length - suffix]))
        {
            suffix--;
        }

        editText = value;
        doc.ApplyChange(new TextChange(prefix, old.Length - prefix - suffix, value.Substring(prefix, value.Length - prefix - suffix)));
        OnPropertyChanged(nameof(EditText));
        NotifyDirty();
    }

    private async Task SaveCoreAsync(CancellationToken ct)
    {
        var doc = document;
        if (doc is null || isSaving)
        {
            return;
        }

        if (IsEditorFaulted)
        {
            SetSaveError(new LocText(InspectorStrings.FilesEditorDesynced));
            return;
        }

        SetSaveError(default);
        IsSaving = true;
        try
        {
            try
            {
                await Task.Run(() => doc.SaveAsync(null, ct).AsTask(), ct).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Trace.WriteLine("[file] save failed: " + exception.Message);
                SetSaveError(new LocText(InspectorStrings.FilesSaveFailed, LocText.Literal(exception.Message)));
                return;
            }

            if (ReferenceEquals(document, doc) && session is { } active)
            {
                try
                {
                    await active.SaveAsync(doc, ct).ConfigureAwait(true);
                }
                catch (Exception exception) when (IsLspFailure(exception))
                {
                    Trace.WriteLine("[file] language server save notification failed: " + exception.Message);
                    SetLspStatus(new LocText(InspectorStrings.FilesLspUnavailable, LocText.Literal(exception.Message)));
                }
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    public string GetLinesText(int firstLine, int lastLine)
    {
        var text = CurrentText;
        var line = 1;
        var start = firstLine <= 1 ? 0 : -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is not ('\n' or '\r'))
            {
                continue;
            }

            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            if (line == lastLine)
            {
                return start < 0 ? string.Empty : text.Substring(start, i + 1 - start);
            }

            line++;
            if (line == firstLine)
            {
                start = i + 1;
            }
        }

        return start < 0 ? string.Empty : text.Substring(start);
    }

    internal void NotifyDocumentEdited() => NotifyDirty();

    internal void ReportEditorFault(LocText notice)
    {
        ReportEditorNotice(notice);
        OnPropertyChanged(nameof(IsEditorFaulted));
        NotifyDirty();
    }

    internal void ReportEditorNotice(LocText notice)
    {
        editorNotice = notice;
        OnPropertyChanged(nameof(EditorNotice));
        OnPropertyChanged(nameof(HasEditorNotice));
    }

    public void ReportCopyError(string message)
    {
        copyError = message.Length == 0 ? default : new LocText(InspectorStrings.FilesCopyFailed, LocText.Literal(message));
        OnPropertyChanged(nameof(CopyError));
        OnPropertyChanged(nameof(HasCopyError));
    }

    private void SetSaveError(LocText value)
    {
        saveError = value;
        OnPropertyChanged(nameof(SaveError));
        OnPropertyChanged(nameof(HasSaveError));
    }

    private void SetLspStatus(LocText value)
    {
        lspStatus = value;
        OnPropertyChanged(nameof(LspStatus));
        OnPropertyChanged(nameof(HasLspStatus));
    }

    private void NotifyDirty()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(TabTitle));
        saveCommand?.NotifyCanExecuteChanged();
    }

}
