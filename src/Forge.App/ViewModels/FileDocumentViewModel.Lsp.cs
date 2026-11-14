using System.Diagnostics;
using Avalonia.Threading;
using Forge.App.Editing;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.Editor.Highlighting;
using Forge.Editor.Languages;
using Forge.Editor.Lsp;
using Forge.Editor.Text;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.ViewModels;

internal sealed partial class FileDocumentViewModel
{
    private readonly record struct EditorLspRequest(
        TextDocument Document, LanguageSession Session, TextSnapshot Snapshot, int Generation, CancellationToken Token);
    private CancellationTokenSource? editorLspCts;
    private LanguageSession? diagnosticsSession;
    private EventHandler<DiagnosticsChangedEventArgs>? diagnosticsHandler;
    private EventHandler<bool>? busyHandler;
    private int editorLspGeneration;
    private long editorDiagnosticsSequence = -1;
    private bool unsupportedHoverReported;
    private EditorLspDiagnosticSet editorDiagnostics = EditorLspDiagnosticSet.Empty;
    private EditorLspDefinitionTarget? pendingReveal;
    private PullLoop? pullLoop;
    private SemanticLoop? semanticLoop;
    private SemanticLineIndex semanticIndex = SemanticLineIndex.Empty;

    /// <summary>Quiet period after an edit before the semantic classification is asked for again.</summary>
    private const int SemanticDebounceMilliseconds = 400;

    /// <summary>Second classification request after a document opens, for a server that analyses the project first.</summary>
    private const int SemanticCatchUpMilliseconds = 2_500;


    /// <summary>Quiet period after an edit before the editor asks for diagnostics again.</summary>
    private const int PullDebounceMilliseconds = 500;

    /// <summary>Second pull after a document opens, so a server that needed to load a project still reports.</summary>
    private const int PullCatchUpMilliseconds = 2_500;

    internal event EventHandler<EditorLspDiagnosticsEventArgs>? EditorDiagnosticsChanged;
    internal event EventHandler<EditorLspDefinitionTarget>? DefinitionRequested;
    internal event EventHandler? RevealRequested;

    /// <summary>Raised on the UI thread with the lines the server newly classified.</summary>
    internal event EventHandler<EditorLspSemanticTokensEventArgs>? SemanticTokensChanged;

    internal EditorLspDiagnosticSet EditorDiagnostics => editorDiagnostics;

    /// <summary>The semantic classification in use: per line, stamped with the document version it maps against.</summary>
    internal SemanticLineIndex EditorSemanticTokens => semanticIndex;

    internal string? EditorWorkspace => openWorkspace ?? Workspace;

    /// <summary>Whether a path is this document's file, so navigation stays in the open buffer instead of reopening it.</summary>
    internal bool IsEditorPath(string path) =>
        path is { Length: > 0 } && Path is { Length: > 0 } &&
        string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(Path), StringComparison.OrdinalIgnoreCase);

    internal void BeginEditorLsp()
    {
        EndEditorLsp();
        unsupportedHoverReported = false;
        if (document is not { } doc)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        editorLspCts = cts;
        _ = ConnectEditorLspAsync(doc, editorLspGeneration, cts.Token);
    }

    internal void EndEditorLsp()
    {
        StopDiagnosticsPull();
        StopSemanticTokens();
        editorLspGeneration++;
        var cts = editorLspCts;
        editorLspCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (diagnosticsSession is { } active)
        {
            if (diagnosticsHandler is { } handler)
            {
                active.DiagnosticsChanged -= handler;
            }

            if (busyHandler is { } busy)
            {
                active.BusyChanged -= busy;
            }
        }

        diagnosticsSession = null;
        diagnosticsHandler = null;
        busyHandler = null;
        editorDiagnosticsSequence = -1;
        editorDiagnostics = EditorLspDiagnosticSet.Empty;
    }
    /// <summary>
    /// Writes the editor's status line only: an empty but valid answer (no references, no definition) is information, and
    /// raising a warning notification for it would sit in the same slot as a real editor failure.
    /// </summary>
    internal void ReportEditorLspStatus(LocText notice) => SetLspStatus(notice);

    internal void ReportEditorLspNotice(LocText notice)
    {
        SetLspStatus(notice);
        if (!notice.IsEmpty)
        {
            NotificationCenter.Current?.Notify(
                NotificationLevel.Warning,
                NotificationsStrings.EditorFailedTitle,
                notice,
                "editor-lsp:" + path);
        }
    }
    internal void OpenDefinition(EditorLspDefinitionTarget target) => DefinitionRequested?.Invoke(this, target);

    internal void RevealEditorPosition(EditorLspDefinitionTarget target)
    {
        pendingReveal = target;
        RevealRequested?.Invoke(this, EventArgs.Empty);
    }

    internal int? TakePendingRevealOffset()
    {
        if (pendingReveal is not { } target || document is not { } doc)
        {
            return null;
        }

        pendingReveal = null;
        if (!LspUri.SameFile(LspUri.FromPath(target.Path), Path))
        {
            return null;
        }

        if (!EditorLspMapping.TryStrictOffset(doc.Snapshot, target.Position, target.Encoding, out var offset))
        {
            SetLspStatus(new LocText(InspectorStrings.EditorLspBadLocation));
            return null;
        }

        return offset;
    }

    /// <summary>
    /// The prelude every capability-gated document request shares: a request that is still current, a server that declares the
    /// capability (otherwise the status line names the missing feature), and one cancellation source that follows both the
    /// caller's token and the editor's. Null means the caller returns null; the caller disposes <c>Linked</c>.
    /// </summary>
    private async Task<(EditorLspRequest Request, CancellationTokenSource Linked)?> BeginEditorRequestAsync(
        int offset,
        CancellationToken ct,
        bool silent,
        Func<EditorLspRequest, string?> missingFeature)
    {
        if (await PrepareEditorRequestAsync(offset, ct, silent).ConfigureAwait(true) is not { } request)
        {
            return null;
        }

        if (missingFeature(request) is { } feature)
        {
            ReportUnsupported(feature);
            return null;
        }

        return (request, CancellationTokenSource.CreateLinkedTokenSource(ct, request.Token));
    }

    /// <summary>
    /// Asks the server for document locations the way every navigation request has to: a cancelled request is silence, a
    /// protocol failure is a notice, both are a null answer the caller treats as "nothing to show".
    /// </summary>
    private async Task<IReadOnlyList<LspLocation>?> RequestLocationsAsync(
        EditorLspRequest request,
        Func<CancellationToken, Task<IReadOnlyList<LspLocation>?>> ask,
        CancellationToken token)
    {
        try
        {
            return await ask(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception) when (IsLspFailure(exception))
        {
            ReportEditorLspFailure(exception, request, token);
            return null;
        }
    }

    private static string? MissingCompletion(EditorLspRequest request) =>
        request.Session.Client?.Capabilities is { SupportsCompletion: false } ? InspectorStrings.EditorLspFeatureCompletion : null;

    private static string? MissingDefinition(EditorLspRequest request) =>
        request.Session.Client?.Capabilities is { SupportsDefinition: false } ? InspectorStrings.EditorLspFeatureDefinition : null;

    private static string? MissingReferences(EditorLspRequest request) =>
        request.Session.Client?.Capabilities is { SupportsReferences: false } ? InspectorStrings.EditorLspFeatureReferences : null;

    internal async Task<EditorLspCompletion?> RequestCompletionAsync(int offset, CancellationToken ct)
    {
        if (await BeginEditorRequestAsync(offset, ct, silent: false, MissingCompletion).ConfigureAwait(true) is not { } scope)
        {
            return null;
        }

        var request = scope.Request;
        using var linked = scope.Linked;
        CompletionResult? result;
        try
        {
            result = await request.Session.RequestCompletionAsync(request.Document, offset, linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception) when (IsLspFailure(exception))
        {
            ReportEditorLspFailure(exception, request, linked.Token);
            return null;
        }

        if (linked.IsCancellationRequested || !IsEditorRequestCurrent(request))
        {
            return null;
        }

        if (result is null)
        {
            SetLspStatus(new LocText(InspectorStrings.EditorLspNoResponse));
            return null;
        }

        var encoding = request.Session.Client?.PositionEncoding ?? request.Snapshot.PositionEncoding;
        var newLine = NewLineText;
        EditorLspCompletionMap mapped;
        try
        {
            mapped = await Task.Run(
                () => EditorLspMapping.MapCompletion(result, request.Snapshot, offset, encoding, newLine),
                linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            ReportEditorLspFailure(exception, request, linked.Token);
            return null;
        }

        if (linked.IsCancellationRequested || !IsEditorRequestCurrent(request))
        {
            return null;
        }

        if (mapped.InvalidItems > 0)
        {
            SetLspStatus(new LocText(InspectorStrings.EditorLspInvalidItems, TextFormat.Number(mapped.InvalidItems)));
        }
        else if (mapped.ConvertedSnippets > 0)
        {
            SetLspStatus(new LocText(InspectorStrings.EditorLspSnippetsConverted, TextFormat.Number(mapped.ConvertedSnippets)));
        }
        else
        {
            ClearEditorLspNotice();
        }

        return mapped.Completion.Items.Count == 0 ? null : mapped.Completion;
    }

    internal async Task<EditorLspHover?> RequestHoverAsync(int offset, CancellationToken ct)
    {
        if (await PrepareEditorRequestAsync(offset, ct, silent: true).ConfigureAwait(true) is not { } request)
        {
            return null;
        }

        if (request.Session.Client?.Capabilities is { SupportsHover: false })
        {
            if (!unsupportedHoverReported)
            {
                unsupportedHoverReported = true;
                ReportUnsupported(InspectorStrings.EditorLspFeatureHover);
            }

            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, request.Token);
        HoverInfo? info;
        try
        {
            info = await request.Session.RequestHoverAsync(request.Document, offset, linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception) when (IsLspFailure(exception))
        {
            if (!linked.IsCancellationRequested && IsEditorRequestCurrent(request))
            {
                ReportEditorLspFailure(exception);
            }

            return null;
        }

        if (info is null || linked.IsCancellationRequested || !IsEditorRequestCurrent(request))
        {
            return null;
        }

        var text = EditorLspMarkup.ToPlain(info.Markup);
        if (text is null)
        {
            return null;
        }

        var encoding = request.Session.Client?.PositionEncoding ?? request.Snapshot.PositionEncoding;
        var start = offset;
        var end = offset;
        if (info.Range is { } range &&
            EditorLspMapping.TryStrictOffset(request.Snapshot, range.Start, encoding, out var rangeStart) &&
            EditorLspMapping.TryStrictOffset(request.Snapshot, range.End, encoding, out var rangeEnd) &&
            rangeStart <= offset && offset <= rangeEnd)
        {
            start = rangeStart;
            end = rangeEnd;
        }

        return new EditorLspHover(request.Snapshot.Version, start, end, text);
    }

    internal async Task<EditorLspDefinition?> RequestDefinitionAsync(int offset, CancellationToken ct)
    {
        if (await BeginEditorRequestAsync(offset, ct, silent: false, MissingDefinition).ConfigureAwait(true) is not { } scope)
        {
            return null;
        }

        var request = scope.Request;
        using var linked = scope.Linked;
        var locations = await RequestLocationsAsync(
            request,
            token => request.Session.RequestDefinitionAsync(request.Document, offset, token),
            linked.Token).ConfigureAwait(true);

        if (linked.IsCancellationRequested || !IsEditorRequestCurrent(request))
        {
            return null;
        }

        if (locations is null)
        {
            SetLspStatus(new LocText(InspectorStrings.EditorLspNoResponse));
            return null;
        }

        if (locations.Count == 0)
        {
            SetLspStatus(new LocText(InspectorStrings.EditorLspDefinitionNone));
            return null;
        }

        var first = locations[0];
        var targetPath = LspUri.ToPath(first.Uri);
        if (targetPath is null)
        {
            SetLspStatus(new LocText(InspectorStrings.EditorLspDefinitionRemote, LocText.Literal(first.Uri)));
            return null;
        }

        var encoding = request.Session.Client?.PositionEncoding ?? request.Snapshot.PositionEncoding;
        var target = new EditorLspDefinitionTarget(targetPath, first.Range.Start, encoding);
        EditorLspDefinition definition;
        if (LspUri.SameFile(first.Uri, Path))
        {
            if (!EditorLspMapping.TryStrictOffset(request.Snapshot, first.Range.Start, encoding, out var targetOffset))
            {
                SetLspStatus(new LocText(InspectorStrings.EditorLspBadLocation));
                return null;
            }

            definition = new EditorLspDefinition(EditorLspDefinitionKind.SameFile, targetOffset, target, locations.Count);
        }
        else
        {
            definition = new EditorLspDefinition(EditorLspDefinitionKind.OtherFile, -1, target, locations.Count);
        }

        if (locations.Count > 1)
        {
            SetLspStatus(new LocText(InspectorStrings.EditorLspDefinitionMultiple, TextFormat.Number(locations.Count)));
        }
        else
        {
            ClearEditorLspNotice();
        }

        return definition;
    }

    /// <summary>
    /// Every place the symbol at <paramref name="offset"/> is used, from the server's <c>textDocument/references</c>. A
    /// null result means no answer (no session, cancelled, or a server that does not declare the capability); an empty list
    /// is a real answer — nothing uses the symbol.
    /// </summary>
    internal async Task<EditorLspReferenceList?> RequestReferencesAsync(int offset, CancellationToken ct)
    {
        if (await BeginEditorRequestAsync(offset, ct, silent: false, MissingReferences).ConfigureAwait(true) is not { } scope)
        {
            return null;
        }

        var request = scope.Request;
        using var linked = scope.Linked;
        var locations = await RequestLocationsAsync(
            request,
            token => request.Session.RequestReferencesAsync(request.Document, offset, true, token),
            linked.Token).ConfigureAwait(true);

        if (locations is null || linked.IsCancellationRequested || !IsEditorRequestCurrent(request))
        {
            return null;
        }

        if (locations.Count == 0)
        {
            return new EditorLspReferenceList(EditorLspMapping.WordAt(request.Snapshot, offset), []);
        }

        var symbol = EditorLspMapping.WordAt(request.Snapshot, offset);
        var encoding = request.Session.Client?.PositionEncoding ?? request.Snapshot.PositionEncoding;
        var ownPath = System.IO.Path.GetFullPath(Path);
        IReadOnlyList<EditorLspReference> references;
        try
        {
            references = await Task.Run(() => MapReferences(locations, ownPath, encoding), linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        return linked.IsCancellationRequested || !IsEditorRequestCurrent(request)
            ? null
            : new EditorLspReferenceList(symbol, references);
    }

    /// <summary>
    /// Turns locations into list rows off the UI thread: remote URIs are dropped (nothing to open), duplicates are folded
    /// away because servers routinely report the same range twice, and the order is the one a reader expects — this file
    /// first, then path, line, column.
    /// </summary>
    private static IReadOnlyList<EditorLspReference> MapReferences(IReadOnlyList<LspLocation> locations, string ownPath, PositionEncoding encoding)
    {
        var items = new List<EditorLspReference>(locations.Count);
        var seen = new HashSet<(string Path, int Line, int Character)>(locations.Count);
        foreach (var location in locations)
        {
            if (LspUri.ToPath(location.Uri) is not { Length: > 0 } path)
            {
                continue;
            }

            var start = location.Range.Start;
            if (start.Line < 0 || start.Character < 0 || !seen.Add((path, start.Line, start.Character)))
            {
                continue;
            }

            items.Add(new EditorLspReference(
                path,
                start.Line + 1,
                start.Character + 1,
                new EditorLspDefinitionTarget(path, start, encoding)));
        }

        items.Sort((left, right) =>
        {
            bool Own(EditorLspReference reference) => string.Equals(reference.Path, ownPath, StringComparison.OrdinalIgnoreCase);
            var leftOwn = Own(left);
            var rightOwn = Own(right);
            if (leftOwn != rightOwn)
            {
                return leftOwn ? -1 : 1;
            }

            var byPath = string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
            if (byPath != 0)
            {
                return byPath;
            }

            var byLine = left.Line.CompareTo(right.Line);
            return byLine != 0 ? byLine : left.Column.CompareTo(right.Column);
        });

        return items;
    }

    /// <summary>
    /// The characters that open the completion list by themselves: the server's declared
    /// <c>triggerCharacters</c> plus <c>.</c>, which every server means even when it forgets to declare it. Empty when
    /// there is no session, so an unserved file opens completion only with Ctrl+Space.
    /// </summary>
    internal IReadOnlyList<string> CompletionTriggers
    {
        get
        {
            var declared = session?.Client?.Capabilities.CompletionTriggerCharacters;
            if (declared is null || declared.Count == 0)
            {
                return [];
            }

            return declared.Contains(".") ? declared : [.. declared, "."];
        }
    }

    /// <summary>
    /// The server is loading a project: say so, because until it finishes nothing answers — a pressed F12 or a hover looks
    /// like a broken feature rather than a wait.
    /// </summary>
    private void OnServerBusy(LanguageSession active, bool busy)
    {
        if (!ReferenceEquals(session, active))
        {
            return;
        }

        if (busy)
        {
            SetLspStatus(new LocText(InspectorStrings.FilesLspLoading));
        }
        else
        {
            SetLspStatus(StatusFor(active));
        }
    }

    /// <summary>
    /// Pull diagnostics (LSP 3.17): a server that only answers <c>textDocument/diagnostic</c> — Roslyn is one; it hides its
    /// provider capability from clients that cannot ask — never pushes anything, so the editor asks on open, after edits
    /// and whenever the server sends <c>workspace/diagnostic/refresh</c>. Push notifications keep working for servers that
    /// use them, and both paths end in the same mapping.
    /// </summary>
    private void StartDiagnosticsPull(TextDocument doc, LanguageSession? active)
    {
        StopDiagnosticsPull();
        if (active?.Client?.Capabilities is not { SupportsDiagnosticPull: true } client)
        {
            return;
        }

        var generation = editorLspGeneration;
        var path = doc.Path ?? Path;
        var cts = new CancellationTokenSource();
        var loop = new PullLoop(active, doc, cts);
        pullLoop = loop;

        void OnRefresh(object? sender, EventArgs e) => SchedulePull(loop, path, generation, immediate: false);
        void OnChanged(object? sender, Forge.Editor.Text.TextChangedEventArgs e) => SchedulePull(loop, path, generation, immediate: false);
        active.DiagnosticsRefreshRequested += OnRefresh;
        doc.Changed += OnChanged;
        loop.Subscriptions = () =>
        {
            active.DiagnosticsRefreshRequested -= OnRefresh;
            doc.Changed -= OnChanged;
        };

        // Once now (a file that is already analysed answers immediately), then again shortly after: a server that had to
        // load the project first sends workspace/diagnostic/refresh, but the second pull makes the common case reliable.
        SchedulePull(loop, path, generation, immediate: true);
        _ = PullLaterAsync(loop, path, generation, client);
    }

    private async Task PullLaterAsync(PullLoop loop, string path, int generation, LspServerCapabilities client)
    {
        try
        {
            await Task.Delay(PullCatchUpMilliseconds, loop.Token).ConfigureAwait(true);
            SchedulePull(loop, path, generation, immediate: true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SchedulePull(PullLoop loop, string path, int generation, bool immediate)
    {
        if (!ReferenceEquals(pullLoop, loop) || loop.Token.IsCancellationRequested)
        {
            return;
        }

        loop.CancelPending();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(loop.Token);
        loop.Pending = cts;
        _ = PullAfterDelayAsync(loop, path, generation, cts, immediate ? 0 : PullDebounceMilliseconds);
    }

    private async Task PullAfterDelayAsync(PullLoop loop, string path, int generation, CancellationTokenSource cts, int delay)
    {
        try
        {
            if (delay > 0)
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(true);
            }

            var active = loop.Session;
            var doc = loop.Document;
            var snapshot = doc.Snapshot;
            var pulled = await active.RequestDiagnosticsAsync(doc, null, cts.Token).ConfigureAwait(true);
            if (pulled is null || pulled.Unchanged || cts.IsCancellationRequested || generation != editorLspGeneration ||
                !ReferenceEquals(pullLoop, loop) || !ReferenceEquals(document, doc) || IsEditorFaulted)
            {
                return;
            }

            // Landing the pulled set in the session's store means the normal diagnostics pipeline renders it: the same
            // mapping as a server push, the same events, and a view that attaches later replays it instead of staying blank.
            active.PublishDiagnostics(LspUri.FromPath(path), snapshot.Version, pulled.Items);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsLspFailure(exception))
        {
            AppLogs.File.ZLogWarning($"pulling diagnostics failed: {exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(loop.Pending, cts))
            {
                loop.Pending = null;
            }

            cts.Dispose();
        }
    }

    private void StopDiagnosticsPull()
    {
        var loop = pullLoop;
        pullLoop = null;
        loop?.Dispose();
    }

    /// <summary>One document's pull state: the session, the cancellation root and the debounce in flight.</summary>
    private sealed class PullLoop(LanguageSession session, TextDocument document, CancellationTokenSource lifetime) : IDisposable
    {
        private bool disposed;

        public LanguageSession Session { get; } = session;

        public TextDocument Document { get; } = document;

        public CancellationToken Token => lifetime.Token;

        public Action? Subscriptions { get; set; }

        public CancellationTokenSource? Pending { get; set; }

        public void CancelPending()
        {
            var pending = Pending;
            Pending = null;
            pending?.Cancel();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            CancelPending();
            Subscriptions?.Invoke();
            Subscriptions = null;
            lifetime.Cancel();
            lifetime.Dispose();
        }
    }

    /// <summary>
    /// Semantic classification (LSP <c>textDocument/semanticTokens</c>): the lexical colours say what a word looks like, the
    /// server says what it is. Asked for when the editor attaches, after every edit (debounced), whenever the server reports
    /// that its analysis moved, and immediately again when an answer was mapped against text that has already changed. The
    /// tokens are bucketed per line off the UI thread, so drawing a line reads an array instead of scanning the document.
    /// </summary>
    private void StartSemanticTokens(TextDocument doc, LanguageSession active)
    {
        StopSemanticTokens();
        if (active.Client?.Capabilities.SemanticTokens is not { Full: true })
        {
            return;
        }

        var generation = editorLspGeneration;
        var loop = new SemanticLoop(active, doc);
        semanticLoop = loop;

        void OnRefresh(object? sender, EventArgs e) => loop.Request();
        void OnChanged(object? sender, Forge.Editor.Text.TextChangedEventArgs e) => loop.Request();
        active.SemanticTokensRefreshRequested += OnRefresh;
        doc.Changed += OnChanged;
        loop.Subscriptions = () =>
        {
            active.SemanticTokensRefreshRequested -= OnRefresh;
            doc.Changed -= OnChanged;
        };

        loop.Request();
        _ = PumpSemanticTokensAsync(loop, doc, generation);
        _ = CatchUpSemanticTokensAsync(loop, doc, generation);
    }

    /// <summary>
    /// One request after the quiet period, then another as soon as the pump is signalled again: an edit that arrives while
    /// a request is in flight is not lost, it becomes the next round.
    /// </summary>
    private async Task PumpSemanticTokensAsync(SemanticLoop loop, TextDocument doc, int generation)
    {
        while (!loop.Token.IsCancellationRequested)
        {
            try
            {
                await loop.WaitForRequestAsync().ConfigureAwait(true);
                await Task.Delay(SemanticDebounceMilliseconds, loop.Token).ConfigureAwait(true);
                await RequestSemanticTokensAsync(loop, doc, generation).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                // The loop was stopped between the wait waking up and the next line; nothing left to pump.
                return;
            }
        }
    }

    private async Task CatchUpSemanticTokensAsync(SemanticLoop loop, TextDocument doc, int generation)
    {
        try
        {
            await Task.Delay(SemanticCatchUpMilliseconds, loop.Token).ConfigureAwait(true);
            if (generation == editorLspGeneration && ReferenceEquals(document, doc))
            {
                loop.Request();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RequestSemanticTokensAsync(SemanticLoop loop, TextDocument doc, int generation)
    {
        var snapshot = doc.Snapshot;
        if (snapshot.Length > ForgeEditorBridge.HighlightLimit)
        {
            // Over the ceiling the overlay is dropped rather than kept from the last small version of the file.
            if (!semanticIndex.IsEmpty)
            {
                semanticIndex = SemanticLineIndex.Empty;
                SemanticTokensChanged?.Invoke(this, new EditorLspSemanticTokensEventArgs(semanticIndex));
            }

            return;
        }

        var encoding = loop.Session.Client?.PositionEncoding ?? snapshot.PositionEncoding;
        SemanticTokensResult? result;
        try
        {
            result = await loop.Session.RequestSemanticTokensAsync(doc, loop.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (IsLspFailure(exception))
        {
            AppLogs.File.ZLogWarning($"semantic tokens failed: {exception.Message}");
            return;
        }

        if (result is null || loop.Token.IsCancellationRequested || generation != editorLspGeneration ||
            !ReferenceEquals(document, doc) || !ReferenceEquals(semanticLoop, loop))
        {
            return;
        }

        var previous = semanticIndex;
        SemanticLineIndex index;
        try
        {
            index = await Task.Run(
                () => SemanticTokenMap.ToLineIndex(result, snapshot, encoding, previous),
                loop.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            AppLogs.File.ZLogError(exception, $"mapping semantic tokens failed");
            return;
        }

        if (generation != editorLspGeneration || !ReferenceEquals(document, doc) || !ReferenceEquals(semanticLoop, loop))
        {
            return;
        }

        semanticIndex = index;
        SemanticTokensChanged?.Invoke(this, new EditorLspSemanticTokensEventArgs(index));

        // The server classified text that no longer exists: ask again rather than leave those ranges painted until the next
        // keystroke happens to ask for them.
        if (index.Version != doc.Snapshot.Version)
        {
            loop.Request();
        }
    }

    private void StopSemanticTokens()
    {
        var loop = semanticLoop;
        semanticLoop = null;
        loop?.Dispose();
        if (!semanticIndex.IsEmpty)
        {
            semanticIndex = SemanticLineIndex.Empty;
            SemanticTokensChanged?.Invoke(this, new EditorLspSemanticTokensEventArgs(semanticIndex));
        }
    }

    /// <summary>One document's semantic-token pump: the session, the request signal and the cancellation root.</summary>
    private sealed class SemanticLoop(LanguageSession session, TextDocument document) : IDisposable
    {
        private readonly SemaphoreSlim signal = new(0, 1);
        private readonly CancellationTokenSource lifetime = new();
        private bool disposed;

        public LanguageSession Session { get; } = session;

        public TextDocument Document { get; } = document;

        public CancellationToken Token => lifetime.Token;

        public Action? Subscriptions { get; set; }

        /// <summary>Marks the classification for another round; several requests in a row collapse into one round.</summary>
        public void Request()
        {
            if (disposed)
            {
                return;
            }

            try
            {
                signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // A round is already pending; the signal is a flag, not a count of edits.
            }
            catch (ObjectDisposedException)
            {
                // Disposed between the flag read and the release: the document is going away, so the request is moot.
            }
        }

        public Task WaitForRequestAsync() => signal.WaitAsync(lifetime.Token);

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Subscriptions?.Invoke();
            Subscriptions = null;
            lifetime.Cancel();
            lifetime.Dispose();
            signal.Dispose();
        }
    }

    /// <summary>
    /// Foldable ranges from the language server, or null when there is no session, the session is not ready, or the
    /// server did not declare <c>foldingRangeProvider</c> — in every such case the editor falls back to the structural
    /// heuristic instead of reporting a missing capability.
    /// </summary>
    internal async Task<IReadOnlyList<Forge.Editor.Folding.FoldingRange>?> RequestFoldingRangesAsync(CancellationToken ct)
    {
        if (await PrepareEditorRequestAsync(0, ct, silent: true).ConfigureAwait(true) is not { } request)
        {
            return null;
        }

        if (request.Session.Client?.Capabilities is not { SupportsFoldingRanges: true })
        {
            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, request.Token);
        try
        {
            var ranges = await request.Session.RequestFoldingRangesAsync(request.Document, linked.Token).ConfigureAwait(true);
            return linked.IsCancellationRequested || !IsEditorRequestCurrent(request) ? null : ranges;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception) when (IsLspFailure(exception))
        {
            AppLogs.File.ZLogWarning($"folding ranges failed: {exception.Message}");
            return null;
        }
    }

    private async Task ConnectEditorLspAsync(TextDocument doc, int generation, CancellationToken ct)
    {
        try
        {
            try
            {
                await lspOpening.ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AppLogs.File.ZLogWarning($"language server open failed before diagnostics attach: {exception.Message}");
                ReportEditorLspError(exception, doc, generation);
            }

            if (ct.IsCancellationRequested || generation != editorLspGeneration || !ReferenceEquals(document, doc))
            {
                return;
            }

            if (session is not { } active)
            {
                ReportNoLanguageServer(requireKnownLanguage: true);
                return;
            }

            var path = doc.Path ?? Path;
            EventHandler<DiagnosticsChangedEventArgs> handler =
                (_, e) => OnEditorSessionDiagnostics(e.Snapshot, active, doc, path, generation);
            diagnosticsSession = active;
            diagnosticsHandler = handler;
            active.DiagnosticsChanged += handler;
            busyHandler = (_, isBusy) => Dispatcher.UIThread.Post(() => OnServerBusy(active, isBusy));
            active.BusyChanged += busyHandler;
            if (active.Diagnostics.GetForPath(path) is { } current)
            {
                _ = Task.Run(() => OnEditorSessionDiagnostics(current, active, doc, path, generation), CancellationToken.None);
            }

            // Pull diagnostics belong to the attached view: the server answers per request, so the editor asks here, on
            // every edit and whenever the server says its analysis moved.
            StartDiagnosticsPull(doc, active);

            // Semantic classification rides the same session and the same document events, but its own debounce: the
            // server re-analyses on its own schedule and reports it through workspace/semanticTokens/refresh.
            StartSemanticTokens(doc, active);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppLogs.File.ZLogError(exception, $"attaching editor diagnostics failed");
            ReportEditorLspError(exception, doc, generation);
        }
    }

    private void OnEditorSessionDiagnostics(DiagnosticSnapshot published, LanguageSession active, TextDocument doc, string path, int generation)
    {
        try
        {
            if (!LspUri.SameFile(published.Uri, path))
            {
                return;
            }

            var snapshot = doc.Snapshot;
            if (published.Version is { } version && version != snapshot.Version)
            {
                return;
            }

            var encoding = active.Client?.PositionEncoding ?? snapshot.PositionEncoding;
            var set = EditorLspMapping.MapDiagnostics(published, snapshot, encoding);
            var sequence = published.Sequence;
            Dispatcher.UIThread.Post(() => PublishEditorDiagnostics(set, sequence, doc, generation));
        }
        catch (Exception exception)
        {
            AppLogs.File.ZLogError(exception, $"mapping diagnostics failed");
            ReportEditorLspError(exception, doc, generation);
        }
    }

    private void PublishEditorDiagnostics(EditorLspDiagnosticSet set, long sequence, TextDocument doc, int generation)
    {
        if (generation != editorLspGeneration || !ReferenceEquals(document, doc) || doc.Version != set.Version ||
            sequence <= editorDiagnosticsSequence)
        {
            return;
        }

        editorDiagnosticsSequence = sequence;
        editorDiagnostics = set;
        if (set.Malformed > 0)
        {
            SetLspStatus(new LocText(InspectorStrings.EditorLspInvalidDiagnostics, TextFormat.Number(set.Malformed)));
        }

        EditorDiagnosticsChanged?.Invoke(this, new EditorLspDiagnosticsEventArgs(set));
    }

    private async Task<EditorLspRequest?> PrepareEditorRequestAsync(int offset, CancellationToken ct, bool silent)
    {
        if (document is not { } doc || IsEditorFaulted || editorLspCts is not { } own)
        {
            return null;
        }

        var snapshot = doc.Snapshot;
        if ((uint)offset > (uint)snapshot.Length)
        {
            return null;
        }

        var generation = editorLspGeneration;
        var token = own.Token;
        if (!lspOpening.IsCompleted)
        {
            try
            {
                await lspOpening.WaitAsync(ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception exception)
            {
                AppLogs.File.ZLogWarning($"language server open failed: {exception.Message}");
                return null;
            }
        }

        if (ct.IsCancellationRequested || token.IsCancellationRequested || generation != editorLspGeneration ||
            !ReferenceEquals(document, doc) || doc.Version != snapshot.Version || IsEditorFaulted)
        {
            return null;
        }

        if (session is not { } active)
        {
            if (!silent)
            {
                ReportNoLanguageServer(requireKnownLanguage: false);
            }

            return null;
        }

        return new EditorLspRequest(doc, active, snapshot, generation, token);
    }

    private bool IsEditorRequestCurrent(EditorLspRequest request) =>
        request.Generation == editorLspGeneration && ReferenceEquals(document, request.Document) &&
        ReferenceEquals(session, request.Session) && request.Document.Version == request.Snapshot.Version && !IsEditorFaulted;

    private void ReportUnsupported(string featureKey) =>
        SetLspStatus(new LocText(InspectorStrings.EditorLspUnsupported, new LocText(featureKey)));

    private void ReportNoLanguageServer(bool requireKnownLanguage)
    {
        if (requireKnownLanguage &&
            (string.IsNullOrEmpty(openWorkspace ?? Workspace) || sessions?.Registry.DetectLanguageId(Path) is null))
        {
            return;
        }

        if (!lspStatus.IsEmpty && !IsEditorLspNotice(lspStatus))
        {
            return;
        }

        SetLspStatus(new LocText(InspectorStrings.EditorLspNone));
    }

    private void ReportEditorLspFailure(Exception exception)
    {
        AppLogs.File.ZLogWarning($"language server request failed: {exception.Message}");
        SetLspStatus(new LocText(InspectorStrings.FilesLspUnavailable, LocText.Literal(exception.Message)));
    }

    private void ReportEditorLspFailure(Exception exception, EditorLspRequest request, CancellationToken token)
    {
        if (!token.IsCancellationRequested && IsEditorRequestCurrent(request))
        {
            ReportEditorLspFailure(exception);
        }
    }

    private void ReportEditorLspError(Exception exception, TextDocument doc, int generation)
    {
        void Report()
        {
            if (generation == editorLspGeneration && ReferenceEquals(document, doc))
            {
                ReportEditorLspFailure(exception);
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Report();
        }
        else
        {
            Dispatcher.UIThread.Post(Report);
        }
    }

    private void ClearEditorLspNotice()
    {
        if (IsEditorLspNotice(lspStatus))
        {
            SetLspStatus(default);
        }
    }

    private static bool IsEditorLspNotice(LocText value) =>
        value.Key is { } key && key.StartsWith(InspectorStrings.EditorLspPrefix, StringComparison.Ordinal);
}
