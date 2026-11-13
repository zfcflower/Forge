using System.Diagnostics;
using Avalonia.Media.Imaging;
using Forge.App.Editing;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.ViewModels.Preview;
using Forge.Editor.Languages;
using Forge.Editor.Lsp;
using Forge.Editor.Text;
using Forge.Editor.TextMate;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.ViewModels;

internal sealed partial class FileDocumentViewModel
{
    public const long MaxFileBytes = 8L * 1024 * 1024;
    private readonly record struct FileRead(TextDocument? Document, FilePreviewViewModel? Preview, bool Missing, long TooLargeBytes, string? Text = null);
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
    private LocText foldingNotice;
    private LocText actionNotice;
    private LocText copyError;
    private RelayCommand? toggleWrapCommand;
    private RelayCommand? toggleEditCommand;
    private RelayCommand? openExternallyCommand;
    private FilePreviewViewModel? preview;
    private FilePreviewSettings? previewSettings;
    private Func<byte[], Bitmap?>? imageDecoder;

    public string? Workspace { get; set; }

    /// <summary>The live editor options the view's toolbar and context menu bind to (compiled bindings need a property on the view model).</summary>
    public EditorSettings EditorSettings => EditorSettings.Current;

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
    public string? FoldingNotice => foldingNotice.IsEmpty ? null : foldingNotice.Resolve();
    public bool HasFoldingNotice => !foldingNotice.IsEmpty;
    public string? EditorActionNotice => actionNotice.IsEmpty ? null : actionNotice.Resolve();
    public bool HasEditorActionNotice => !actionNotice.IsEmpty;
    public string? CopyError => copyError.IsEmpty ? null : copyError.Resolve();
    public bool HasCopyError => !copyError.IsEmpty;
    public RelayCommand ToggleWrapCommand => toggleWrapCommand ??= new RelayCommand(() => WrapLines = !WrapLines);
    public RelayCommand ToggleEditCommand => toggleEditCommand ??= new RelayCommand(() => IsEditing = !IsEditing);
    public AsyncRelayCommand SaveCommand => saveCommand ??= new AsyncRelayCommand(
        SaveCoreAsync,
        () => CanSave,
        exception =>
        {
            AppLogs.File.ZLogError(exception, $"save failed");
            SetSaveError(new LocText(InspectorStrings.FilesSaveFailed, LocText.Literal(exception.Message)));
        });

    /// <summary>
    /// <paramref name="imageDecoder"/> gates the bitmap decoding of the image and notebook previews (the real Avalonia
    /// decoder by default) so headless tests can skip decoding; <paramref name="previewSettings"/> pins the preview
    /// limits instead of the ambient <see cref="FilePreviewSettings.Current"/>.
    /// </summary>
    public static FileDocumentViewModel Create(
        GrammarRegistry? grammars = null,
        LanguageSessionPool? sessions = null,
        Func<byte[], Bitmap?>? imageDecoder = null,
        FilePreviewSettings? previewSettings = null) =>
        new(grammars) { sessions = sessions, imageDecoder = imageDecoder, previewSettings = previewSettings };

    public async Task LoadAsync(string filePath, CancellationToken ct, bool discardEdits = false, bool forceText = false)
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
            var read = await Task.Run(() => ReadFileAsync(full, forceText, token), token).ConfigureAwait(true);
            try
            {
                token.ThrowIfCancellationRequested();
                if (read.Missing)
                {
                    SetError(InspectorStrings.FileMissing(filePath));
                    return;
                }

                if (read.Preview is not null)
                {
                    AttachPreview(read.Preview);
                    return;
                }

                if (read.Document is null)
                {
                    SetError(InspectorStrings.FileTooLarge(read.TooLargeBytes));
                    return;
                }

                AttachDocument(read.Document, read.Text ?? string.Empty);
            }
            finally
            {
                // A preview that lost the race (cancellation or a superseded load) must not keep its bitmaps.
                if (read.Preview is not null && !ReferenceEquals(preview, read.Preview))
                {
                    read.Preview.Release();
                }
            }
        }
        catch (OperationCanceledException) when (version != loadVersion && !ct.IsCancellationRequested)
        {
            // The newer load owns the state.
        }
        catch (Exception exception) when (version != loadVersion && exception is not OperationCanceledException)
        {
            AppLogs.File.ZLogWarning($"superseded load failed: {exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLogs.File.ZLogWarning($"load failed: {exception.Message}");
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
        var oldPreview = preview;
        if (oldPreview is not null)
        {
            preview = null;
            OnPropertyChanged(nameof(Preview));
            OnPropertyChanged(nameof(HasPreview));
            oldPreview.Release();
        }

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

    private async Task<FileRead> ReadFileAsync(string full, bool forceText, CancellationToken ct)
    {
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            return new FileRead(null, null, true, 0);
        }

        if (!forceText)
        {
            var firstBytes = ReadFirstBytes(full);
            switch (FilePreviewClassifier.Classify(full, firstBytes))
            {
                case FilePreviewKind.Lfs:
                    return new FileRead(null, PlaceholderFilePreviewViewModel.ForLfs(full, info.Length, firstBytes), false, 0);
                case FilePreviewKind.Image:
                    return ReadImagePreview(full, info);
                case FilePreviewKind.Table:
                    return new FileRead(
                        null,
                        TableFilePreviewViewModel.Build(full, info.Length, PreviewSettings.PreviewRows, FilePreviewClassifier.TableSeparator(full), ct),
                        false,
                        0);
                case FilePreviewKind.Notebook:
                    return new FileRead(
                        null,
                        NotebookFilePreviewViewModel.Build(full, info.Length, PreviewSettings.NotebookOutputChars, imageDecoder, ct),
                        false,
                        0);
                case FilePreviewKind.Pdf:
                    return new FileRead(null, PlaceholderFilePreviewViewModel.ForPdf(full, info.Length), false, 0);
                case FilePreviewKind.Html:
                    return new FileRead(null, PlaceholderFilePreviewViewModel.ForHtml(full, info.Length, ShowPreviewSourceAsync), false, 0);
            }
        }

        if (info.Length > MaxFileBytes)
        {
            return new FileRead(null, null, false, info.Length);
        }

        try
        {
            var loaded = await TextDocument.LoadAsync(full, ct).ConfigureAwait(false);
            return new FileRead(loaded, null, false, 0, loaded.Snapshot.GetText());
        }
        catch (NotSupportedException)
        {
            return new FileRead(null, PlaceholderFilePreviewViewModel.ForBinary(full, info.Length), false, 0);
        }
    }

    /// <summary>The first bytes of a file, enough for the LFS pointer marker; empty when the peek failed.</summary>
    private static byte[] ReadFirstBytes(string full)
    {
        try
        {
            using var stream = new FileStream(
                full,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.SequentialScan);
            var count = (int)Math.Min(FilePreviewClassifier.PeekBytes, stream.Length);
            var buffer = new byte[count];
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Classification falls back to the extension; the text path will surface the error if it persists.
            AppLogs.File.ZLogWarning($"peek failed: {exception.Message}");
            return [];
        }
    }

    private FileRead ReadImagePreview(string full, FileInfo info)
    {
        var limit = PreviewSettings.PreviewImageBytes;
        if (info.Length > limit)
        {
            return new FileRead(null, PlaceholderFilePreviewViewModel.ForImageTooLarge(full, info.Length, limit), false, 0);
        }

        return new FileRead(null, ImageFilePreviewViewModel.Build(full, info.Length, imageDecoder), false, 0);
    }

    /// <summary>The live preview limits: the instance pinned by <see cref="Create"/>, or the ambient projection.</summary>
    private FilePreviewSettings PreviewSettings => previewSettings ?? FilePreviewSettings.Current;

    /// <summary>The read-only preview of the current file, or null while the editor document is open.</summary>
    public FilePreviewViewModel? Preview => preview;

    public bool HasPreview => preview is not null;

    /// <summary>
    /// The toolbar's permanently visible open-externally button: hands the current file to the OS default program for
    /// any loaded file, preview or editor document alike.
    /// </summary>
    public RelayCommand OpenExternallyCommand => openExternallyCommand ??= new RelayCommand(OpenExternally);

    private void OpenExternally()
    {
        if (path.Length == 0)
        {
            return;
        }

        if (PreviewOpener.TryOpen(path) is { } failure)
        {
            ReportEditorActionNotice(failure);
        }
    }

    private void AttachPreview(FilePreviewViewModel value)
    {
        preview = value;
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(HasPreview));
    }

    /// <summary>The HTML placeholder's "show the source": the same file, but classified as text.</summary>
    internal Task ShowPreviewSourceAsync(CancellationToken ct) => LoadAsync(path, ct, forceText: true);

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
            AppLogs.File.ZLogWarning($"closing the document in the language server failed: {exception.Message}");
        }
    }

    private void CancelLsp()
    {
        StopDiagnosticsPull();
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
        WrapLines = EditorSettings.Current.WordWrap;
        ReportEditorActionNotice(default);
        ReportFoldingNotice(default);
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
            SetLspStatus(StatusFor(opened));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CloseInPoolAsync(pool, workspace, doc).ConfigureAwait(true);
        }
        catch (Exception exception) when (IsLspFailure(exception))
        {
            AppLogs.File.ZLogWarning($"language server unavailable: {exception.Message}");
            if (ReferenceEquals(document, doc))
            {
                session = null;
                SetLspStatus(new LocText(InspectorStrings.FilesLspUnavailable, LocText.Literal(exception.Message)));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.File.ZLogWarning($"unexpected language server open failure: {exception.Message}");
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

    /// <summary>
    /// The line under the editor: why no server runs, or which already-installed server serves this file. A server the
    /// user configured is not announced — it is what they asked for — while a discovered one is, so a feature that
    /// suddenly works has a visible reason.
    /// </summary>
    private LocText StatusFor(LanguageSession? opened)
    {
        if (opened is { State: LanguageSessionState.Unavailable })
        {
            return new LocText(InspectorStrings.FilesLspStopped);
        }

        if (opened is null ||
            sessions?.Registry.GetById(opened.LanguageId) is not { ServerOrigin: { } origin, Server: { } server })
        {
            return default;
        }

        return new LocText(
            InspectorStrings.FilesLspServer,
            LocText.Literal(ServerName(server)),
            LocText.Literal(origin));
    }

    /// <summary>
    /// What to call the server: a launcher hides the real one (<c>dotnet Microsoft.CodeAnalysis.LanguageServer.dll</c> and
    /// <c>node jsonServerMain.js</c> both read as "dotnet"/"node"), so the launched file names the server.
    /// </summary>
    private static string ServerName(LanguageServerConfig server)
    {
        var command = System.IO.Path.GetFileName(server.Command);
        if (server.Arguments.Count == 0 || command is not ("dotnet" or "dotnet.exe" or "node" or "node.exe"))
        {
            return command;
        }

        var launched = System.IO.Path.GetFileName(server.Arguments[0]);
        return launched.Length == 0 ? command : launched;
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
                AppLogs.File.ZLogError(exception, $"save failed");
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
                    AppLogs.File.ZLogWarning($"language server save notification failed: {exception.Message}");
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

    /// <summary>Folding's own line: a size cap here must not overwrite the syntax-highlighting notice.</summary>
    internal void ReportFoldingNotice(LocText notice)
    {
        foldingNotice = notice;
        OnPropertyChanged(nameof(FoldingNotice));
        OnPropertyChanged(nameof(HasFoldingNotice));
    }

    /// <summary>A transient result of an editor action (a link that could not be opened).</summary>
    internal void ReportEditorActionNotice(LocText notice)
    {
        actionNotice = notice;
        OnPropertyChanged(nameof(EditorActionNotice));
        OnPropertyChanged(nameof(HasEditorActionNotice));
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

        // The editor's own banner is the record; the toast is what reaches a user whose focus is on another panel or window.
        if (!value.IsEmpty)
        {
            NotificationCenter.Current?.Notify(
                NotificationLevel.Error,
                NotificationsStrings.SaveFailedTitle,
                value,
                "save-failed:" + path);
        }
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
