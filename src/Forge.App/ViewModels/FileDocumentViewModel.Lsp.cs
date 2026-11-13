using System.Diagnostics;
using Avalonia.Threading;
using Forge.App.Editing;
using Forge.App.Localization;
using Forge.App.Notifications;
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

    /// <summary>Quiet period after an edit before the editor asks for diagnostics again.</summary>
    private const int PullDebounceMilliseconds = 500;

    /// <summary>Second pull after a document opens, so a server that needed to load a project still reports.</summary>
    private const int PullCatchUpMilliseconds = 2_500;

    internal event EventHandler<EditorLspDiagnosticsEventArgs>? EditorDiagnosticsChanged;
    internal event EventHandler<EditorLspDefinitionTarget>? DefinitionRequested;
    internal event EventHandler? RevealRequested;
    internal EditorLspDiagnosticSet EditorDiagnostics => editorDiagnostics;
    internal string? EditorWorkspace => openWorkspace ?? Workspace;

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

    internal async Task<EditorLspCompletion?> RequestCompletionAsync(int offset, CancellationToken ct)
    {
        if (await PrepareEditorRequestAsync(offset, ct, silent: false).ConfigureAwait(true) is not { } request)
        {
            return null;
        }

        if (request.Session.Client?.Capabilities is { SupportsCompletion: false })
        {
            ReportUnsupported(InspectorStrings.EditorLspFeatureCompletion);
            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, request.Token);
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
        if (await PrepareEditorRequestAsync(offset, ct, silent: false).ConfigureAwait(true) is not { } request)
        {
            return null;
        }

        if (request.Session.Client?.Capabilities is { SupportsDefinition: false })
        {
            ReportUnsupported(InspectorStrings.EditorLspFeatureDefinition);
            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, request.Token);
        IReadOnlyList<LspLocation>? locations;
        try
        {
            locations = await request.Session.RequestDefinitionAsync(request.Document, offset, linked.Token).ConfigureAwait(true);
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
