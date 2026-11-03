using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Forge.Editor.Text;

namespace Forge.Editor.Lsp;

public enum LanguageSessionState
{
    Stopped,
    Starting,
    Running,

    /// <summary>The server could not be started (missing executable or repeated crashes); editing continues without LSP.</summary>
    Unavailable,
    Disposed,
}

public sealed record LanguageSessionOptions
{
    public string WorkspacePath { get; init; } = Directory.GetCurrentDirectory();

    public IReadOnlyList<string> WorkspaceFolders { get; init; } = [];

    public string? LanguageId { get; init; }

    public JsonNode? InitializationOptions { get; init; }

    public RestartPolicy RestartPolicy { get; init; } = new();

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public PositionEncoding PositionEncoding { get; init; } = PositionEncoding.Utf16;

    /// <summary>Supplies the session's server process; null uses <see cref="LanguageServerProcess.DefaultFactory"/>.</summary>
    public Func<LanguageServerLaunch, RestartPolicy, LanguageServerProcess>? ProcessFactory { get; init; }

    /// <summary>Diagnostics sink for server state messages; never throws.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// One language server bound to a workspace: lazy start with backoff restarts, document sync pushed from document
/// change events on a background pump, a stable diagnostics store and null-returning request helpers so callers can
/// degrade to lexer-only highlighting when the server is unavailable.
/// </summary>
public sealed class LanguageSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly LanguageServerProcess _process;
    private readonly LanguageSessionOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Channel<TextDocument> _syncQueue = Channel.CreateUnbounded<TextDocument>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<TextDocument, PendingSync> _pending = new();
    private readonly Dictionary<TextDocument, byte> _documents = new();
    private readonly List<IDisposable> _subscriptions = [];

    private LspClient? _client;
    private Task? _syncPump;
    private LanguageSessionState _state = LanguageSessionState.Stopped;
    private bool _crashed;
    private int _handshakeCompleted;
    private int _restartCount;
    private int _disposed;
    private int _leases;
    private long _lastUsedTicks;

    public LanguageSession(LanguageServerLaunch launch, LanguageSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(launch);
        Launch = launch;
        _options = options ?? new LanguageSessionOptions();
        _process = (_options.ProcessFactory ?? LanguageServerProcess.DefaultFactory) is { } factory
            ? factory(launch, _options.RestartPolicy)
            : new LanguageServerProcess(launch, _options.RestartPolicy, new StderrRing());
        _process.Exited += OnProcessExited;
        Touch();
    }

    public LanguageServerLaunch Launch { get; }

    public LanguageSessionOptions Options => _options;

    public string WorkspacePath => _options.WorkspacePath;

    public string? LanguageId => _options.LanguageId;

    public LanguageSessionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public LspClient? Client => Volatile.Read(ref _client);

    public DiagnosticsStore Diagnostics { get; } = new();

    public DateTimeOffset LastUsedAt => new(Volatile.Read(ref _lastUsedTicks), TimeSpan.Zero);

    /// <summary>True while a request, open, close or sync is running, so an idle sweep must not dispose the session.</summary>
    public bool HasActiveWork => Volatile.Read(ref _leases) > 0;

    public int RestartCount => Volatile.Read(ref _restartCount);

    public StderrRing Stderr => _process.Stderr;

    /// <summary>Message of the last start or request failure; null when the last operation succeeded.</summary>
    public string? LastError { get; private set; }

    public IReadOnlyList<TextDocument> OpenDocuments
    {
        get
        {
            lock (_gate)
            {
                return [.. _documents.Keys];
            }
        }
    }

    public event EventHandler? StateChanged;

    public event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged;

    /// <summary>Starts a unit of work that keeps the session out of the idle sweep; pair with <see cref="ReleaseLease"/>.</summary>
    public bool TryLease()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        Interlocked.Increment(ref _leases);
        if (Volatile.Read(ref _disposed) == 0)
        {
            return true;
        }

        ReleaseLease();
        return false;
    }

    public void ReleaseLease() => Interlocked.Decrement(ref _leases);

    public void Touch() => Volatile.Write(ref _lastUsedTicks, DateTimeOffset.UtcNow.UtcTicks);

    /// <summary>Starts the server if needed; false means LSP is unavailable and the editor keeps the lexer layer only.</summary>
    public async Task<bool> TryStartAsync(CancellationToken ct = default)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        return client is { IsAlive: true };
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        if (client is null)
        {
            throw new LanguageServerCrashedException(
                $"Language server '{Launch.Command}' is unavailable.{Environment.NewLine}{_process.Stderr.Text()}");
        }
    }

    public async ValueTask OpenAsync(TextDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        Touch();
        if (!TryLease())
        {
            return;
        }

        try
        {
            var client = await EnsureClientAsync(ct).ConfigureAwait(false);
            if (client is null)
            {
                return;
            }

            await OpenDocumentAsync(client, document, ct).ConfigureAwait(false);
        }
        finally
        {
            ReleaseLease();
        }
    }

    public async ValueTask CloseAsync(TextDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        PendingSync? pending;
        lock (_gate)
        {
            if (!_documents.Remove(document))
            {
                return;
            }

            _pending.TryRemove(document, out pending);
            document.Changed -= OnDocumentChanged;
        }

        if (!TryLease())
        {
            return;
        }

        try
        {
            var client = Client;
            if (client is null)
            {
                return;
            }

            if (pending is not null)
            {
                var acquired = false;
                try
                {
                    await pending.Gate.WaitAsync(ct).ConfigureAwait(false);
                    acquired = true;
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    if (acquired)
                    {
                        pending.Gate.Release();
                    }
                }
            }

            try
            {
                await client.DidCloseAsync(document, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is LspProtocolException or IOException or OperationCanceledException)
            {
            }
        }
        finally
        {
            ReleaseLease();
        }
    }

    public async ValueTask SaveAsync(TextDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!TryLease())
        {
            return;
        }

        try
        {
            var client = Client;
            if (client is null)
            {
                return;
            }

            try
            {
                await client.DidSaveAsync(document, includeText: false, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is LspProtocolException or IOException or OperationCanceledException)
            {
            }
        }
        finally
        {
            ReleaseLease();
        }
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        _crashed = true;
        Volatile.Write(ref _handshakeCompleted, 0);
        var client = Client;
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _client, null);
        }

        await _process.StopAsync(ct).ConfigureAwait(false);
        var streams = await _process.RestartAsync(ct).ConfigureAwait(false);
        await ConnectAsync(streams, ct).ConfigureAwait(false);
    }

    public async Task<SemanticTokensResult?> RequestSemanticTokensAsync(TextDocument document, CancellationToken ct = default) =>
        await InvokeAsync(document, client => client.RequestSemanticTokensAsync(document, ct), ct).ConfigureAwait(false);

    public async Task<CompletionResult?> RequestCompletionAsync(TextDocument document, int offset, CancellationToken ct = default) =>
        await InvokeAsync(document, client => client.RequestCompletionAsync(document, offset, ct), ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<DocumentSymbol>?> RequestDocumentSymbolsAsync(TextDocument document, CancellationToken ct = default) =>
        await InvokeAsync(document, client => client.RequestDocumentSymbolsAsync(document, ct), ct).ConfigureAwait(false);

    public Task<HoverInfo?> RequestHoverAsync(TextDocument document, int offset, CancellationToken ct = default) =>
        InvokeNullableAsync(document, client => client.RequestHoverAsync(document, offset, ct), ct);

    public async Task<IReadOnlyList<LspLocation>?> RequestDefinitionAsync(TextDocument document, int offset, CancellationToken ct = default) =>
        await InvokeAsync(document, client => client.RequestDefinitionAsync(document, offset, ct), ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<LspLocation>?> RequestReferencesAsync(
        TextDocument document,
        int offset,
        bool includeDeclaration = true,
        CancellationToken ct = default) =>
        await InvokeAsync(document, client => client.RequestReferencesAsync(document, offset, includeDeclaration, ct), ct).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        SetState(LanguageSessionState.Disposed);
        _syncQueue.Writer.TryComplete();

        var client = Client;
        if (client is not null)
        {
            try
            {
                await client.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is LspProtocolException or IOException or OperationCanceledException)
            {
            }
        }

        _lifetime.Cancel();

        lock (_gate)
        {
            foreach (var document in _documents.Keys)
            {
                document.Changed -= OnDocumentChanged;
            }

            _documents.Clear();
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }

        var activeClient = Client;
        if (activeClient is not null)
        {
            await activeClient.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _client, null);
        }

        await _process.DisposeAsync().ConfigureAwait(false);
        if (_syncPump is not null)
        {
            try
            {
                await _syncPump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _process.Exited -= OnProcessExited;
        _startGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task<T?> InvokeNullableAsync<T>(TextDocument document, Func<LspClient, Task<T?>> action, CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(document);
        Touch();
        if (!TryLease())
        {
            return null;
        }

        try
        {
            var client = await EnsureClientAsync(ct).ConfigureAwait(false);
            if (client is null)
            {
                return null;
            }

            await SyncDocumentAsync(document, ct).ConfigureAwait(false);
            try
            {
                return await action(client).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is LspProtocolException or LanguageServerCrashedException or IOException or InvalidOperationException)
            {
                LastError = ex.Message;
                _options.Log?.Invoke($"Language server request failed: {ex.Message}");
                return null;
            }
        }
        finally
        {
            ReleaseLease();
        }
    }

    private async Task<T?> InvokeAsync<T>(TextDocument document, Func<LspClient, Task<T>> action, CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(document);
        Touch();
        if (!TryLease())
        {
            return null;
        }

        try
        {
            var client = await EnsureClientAsync(ct).ConfigureAwait(false);
            if (client is null)
            {
                return null;
            }

            await SyncDocumentAsync(document, ct).ConfigureAwait(false);
            try
            {
                return await action(client).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is LspProtocolException or LanguageServerCrashedException or IOException or InvalidOperationException)
            {
                LastError = ex.Message;
                _options.Log?.Invoke($"Language server request failed: {ex.Message}");
                return null;
            }
        }
        finally
        {
            ReleaseLease();
        }
    }

    private async Task<LspClient?> EnsureClientAsync(CancellationToken ct)
    {
        var existing = Client;
        if (existing is { IsAlive: true })
        {
            return existing;
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return null;
        }

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            existing = Client;
            if (existing is { IsAlive: true })
            {
                return existing;
            }

            if (existing is not null)
            {
                await existing.DisposeAsync().ConfigureAwait(false);
                Volatile.Write(ref _client, null);
            }

            SetState(LanguageSessionState.Starting);
            Volatile.Write(ref _handshakeCompleted, 0);
            ProcessStreams streams;
            try
            {
                streams = _crashed || _process.IsRunning
                    ? await _process.RestartAsync(ct).ConfigureAwait(false)
                    : _process.Start();
            }
            catch (LanguageServerNotFoundException ex)
            {
                LastError = ex.Message;
                _options.Log?.Invoke(ex.Message);
                SetState(LanguageSessionState.Unavailable);
                return null;
            }
            catch (LanguageServerCrashedException ex)
            {
                LastError = ex.Message;
                _options.Log?.Invoke(ex.Message);
                SetState(LanguageSessionState.Unavailable);
                return null;
            }
            catch (InvalidOperationException ex)
            {
                LastError = ex.Message;
                await _process.StopAsync(ct).ConfigureAwait(false);
                streams = _process.Start();
            }

            return await ConnectAsync(streams, ct).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<LspClient?> ConnectAsync(ProcessStreams streams, CancellationToken ct)
    {
        LspClient client;
        try
        {
            client = await LspClient.ConnectAsync(
                _process,
                streams,
                new LspClientOptions
                {
                    WorkspacePath = _options.WorkspacePath,
                    WorkspaceFolders = _options.WorkspaceFolders,
                    LanguageId = _options.LanguageId,
                    InitializationOptions = _options.InitializationOptions,
                    RequestTimeout = _options.RequestTimeout,
                    PreferredPositionEncoding = _options.PositionEncoding,
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is LspProtocolException or IOException or OperationCanceledException or LanguageServerCrashedException)
        {
            LastError = ex.Message;
            _options.Log?.Invoke($"Language server '{Launch.Command}' failed to initialize: {ex.Message}");
            SetState(LanguageSessionState.Unavailable);
            _crashed = true;
            return null;
        }

        Volatile.Write(ref _handshakeCompleted, 1);
        Volatile.Write(ref _client, client);
        _crashed = false;
        LastError = null;
        client.Diagnostics.Changed += OnClientDiagnostics;
        client.Closed += OnClientClosed;
        client.LogMessage += OnServerLog;
        _syncPump ??= Task.Run(() => SyncLoopAsync(_lifetime.Token));

        SetState(LanguageSessionState.Running);
        await ReopenDocumentsAsync(client, ct).ConfigureAwait(false);
        return client;
    }

    private async Task ReopenDocumentsAsync(LspClient client, CancellationToken ct)
    {
        TextDocument[] documents;
        lock (_gate)
        {
            documents = [.. _documents.Keys];
        }

        foreach (var document in documents)
        {
            try
            {
                await ReopenDocumentAsync(client, document, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is LspProtocolException or IOException)
            {
                _options.Log?.Invoke($"Failed to reopen '{document.Path}': {ex.Message}");
            }
        }
    }

    private async ValueTask ReopenDocumentAsync(LspClient client, TextDocument document, CancellationToken ct)
    {
        if (!_pending.TryGetValue(document, out var pending))
        {
            return;
        }

        await pending.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = document.Snapshot;
            await client.DidOpenAsync(document, snapshot, ct).ConfigureAwait(false);
            lock (pending)
            {
                pending.ServerVersion = snapshot.Version;
                pending.Client = client;
                pending.Count = 0;
                pending.Single = null;
                pending.ForceFull = false;
                pending.Latest = snapshot;
            }

            if (document.Version != snapshot.Version)
            {
                RequestFullSync(document, pending);
            }
        }
        finally
        {
            pending.Gate.Release();
        }
    }

    private async ValueTask OpenDocumentAsync(LspClient client, TextDocument document, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_documents.ContainsKey(document))
            {
                return;
            }
        }

        var pending = new PendingSync();
        await pending.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_documents.ContainsKey(document))
                {
                    return;
                }

                _documents[document] = 0;
                _pending[document] = pending;
            }

            var snapshot = document.Snapshot;
            lock (pending)
            {
                pending.Latest = snapshot;
                pending.ServerVersion = -1;
            }

            document.Changed -= OnDocumentChanged;
            document.Changed += OnDocumentChanged;
            try
            {
                await client.DidOpenAsync(document, snapshot, ct).ConfigureAwait(false);
            }
            catch
            {
                lock (_gate)
                {
                    _documents.Remove(document);
                    _pending.TryRemove(document, out _);
                    document.Changed -= OnDocumentChanged;
                }

                throw;
            }

            lock (pending)
            {
                pending.ServerVersion = snapshot.Version;
                pending.Client = client;
            }

            if (document.Version != snapshot.Version)
            {
                RequestFullSync(document, pending);
            }
        }
        finally
        {
            pending.Gate.Release();
        }
    }

    /// <summary>Queues a full-text sync of the document's current snapshot, used when queued deltas no longer align.</summary>
    private void RequestFullSync(TextDocument document, PendingSync pending)
    {
        lock (pending)
        {
            if (pending.Count == 0)
            {
                pending.Count = 1;
                pending.Latest = document.Snapshot;
            }

            pending.ForceFull = true;
            pending.Single = null;
        }

        _syncQueue.Writer.TryWrite(document);
    }

    private void OnDocumentChanged(object? sender, TextChangedEventArgs change)
    {
        if (sender is not TextDocument document || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_gate)
        {
            if (!_documents.ContainsKey(document) || !_pending.TryGetValue(document, out var pending))
            {
                return;
            }

            lock (pending)
            {
                pending.Count++;
                pending.Latest = change.New;
                pending.Single = pending.Count == 1 ? change : null;
            }
        }

        Touch();
        _syncQueue.Writer.TryWrite(document);
    }

    private async ValueTask SyncDocumentAsync(TextDocument document, CancellationToken ct)
    {
        if (!_pending.TryGetValue(document, out var pending))
        {
            return;
        }

        var client = Client;
        if (client is null)
        {
            return;
        }

        if (!TryLease())
        {
            return;
        }

        try
        {
            await SendPendingAsync(document, pending, client, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is LspProtocolException or IOException or InvalidOperationException)
        {
            _options.Log?.Invoke($"Language server sync failed: {ex.Message}");
        }
        finally
        {
            ReleaseLease();
        }
    }

    /// <summary>Sends one immutable cutoff batch: exactly one delta when it still applies, otherwise the cutoff snapshot's full text.</summary>
    private async ValueTask SendPendingAsync(TextDocument document, PendingSync pending, LspClient client, CancellationToken ct)
    {
        await pending.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_pending.TryGetValue(document, out var current) || !ReferenceEquals(current, pending))
            {
                return;
            }

            TextChangedEventArgs? single;
            TextSnapshot snapshot;
            lock (pending)
            {
                if (pending.Count == 0 || !ReferenceEquals(pending.Client, client))
                {
                    return;
                }

                single = pending.Count == 1 && !pending.ForceFull && pending.Single is { } candidate &&
                    candidate.Old.Version == pending.ServerVersion
                    ? candidate
                    : null;
                snapshot = pending.Latest;
                pending.Count = 0;
                pending.Single = null;
                pending.ForceFull = false;
            }

            Touch();
            try
            {
                if (single is not null && client.Capabilities.SupportsIncrementalSync)
                {
                    await client.DidChangeAsync(document, single, ct).ConfigureAwait(false);
                    lock (pending)
                    {
                        pending.ServerVersion = single.New.Version;
                    }
                }
                else
                {
                    await client.DidChangeFullAsync(document, snapshot, ct).ConfigureAwait(false);
                    lock (pending)
                    {
                        pending.ServerVersion = snapshot.Version;
                    }
                }
            }
            catch
            {
                lock (pending)
                {
                    pending.ForceFull = true;
                }

                throw;
            }
        }
        finally
        {
            pending.Gate.Release();
        }
    }

    private async Task SyncLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var document in _syncQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (!_pending.TryGetValue(document, out var pending))
                {
                    continue;
                }

                try
                {
                    await SyncDocumentAsync(document, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnClientDiagnostics(object? sender, DiagnosticsChangedEventArgs e)
    {
        Diagnostics.Update(e.Snapshot.Uri, e.Snapshot.Version, e.Snapshot.Diagnostics);
        DiagnosticsChanged?.Invoke(this, e);
    }

    private void OnClientClosed(object? sender, LspClientClosedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (e.Reason is not null)
        {
            _crashed = true;
            Interlocked.Increment(ref _restartCount);
            _options.Log?.Invoke($"Language server '{Launch.Command}' stopped: {e.Reason.Message}");
            SetState(LanguageSessionState.Stopped);
        }
    }

    private void OnServerLog(string message) => _options.Log?.Invoke(message);

    private void OnProcessExited(object? sender, LanguageServerExitedEventArgs e)
    {
        if (e.Expected || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _crashed = true;
        if (Volatile.Read(ref _handshakeCompleted) == 0)
        {
            _options.Log?.Invoke(
                $"Language server '{Launch.Command}' failed to initialize: the server exited with code {e.ExitCode} before the initialize handshake completed.");
            SetState(LanguageSessionState.Unavailable);
            return;
        }

        SetState(LanguageSessionState.Stopped);
    }

    private void SetState(LanguageSessionState state)
    {
        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class PendingSync
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>The latest snapshot observed for the document; the cutoff of the next batch.</summary>
        public TextSnapshot Latest { get; set; } = TextSnapshot.Empty;

        /// <summary>Set only while exactly one change is queued; the delta that can follow <see cref="ServerVersion"/>.</summary>
        public TextChangedEventArgs? Single { get; set; }

        /// <summary>Version of the snapshot the server currently has for this document; -1 when unknown.</summary>
        public int ServerVersion { get; set; } = -1;

        /// <summary>The client the document is currently open on; deltas are never sent to another client.</summary>
        public LspClient? Client { get; set; }

        public int Count { get; set; }

        public bool ForceFull { get; set; }
    }
}
