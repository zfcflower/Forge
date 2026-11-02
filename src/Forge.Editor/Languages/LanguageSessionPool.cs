using Forge.Editor.Lsp;
using Forge.Editor.Text;

namespace Forge.Editor.Languages;

public sealed record LanguageSessionPoolOptions
{
    /// <summary>A session with no open documents, no active work and no activity for this long is shut down by the background sweep.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public RestartPolicy RestartPolicy { get; init; } = new();

    public Action<string>? Log { get; init; }
}

/// <summary>
/// Lazy per-workspace language server sessions keyed by (workspace, language). Sessions start on first use, are reused
/// afterwards and are shut down when they stay idle past <see cref="LanguageSessionPoolOptions.IdleTimeout"/>.
/// </summary>
public sealed class LanguageSessionPool : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<SessionKey, SessionSlot> _sessions = [];
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LanguageSessionPoolOptions _options;
    private readonly Task? _sweep;

    private int _disposed;

    public LanguageSessionPool(LanguageRegistry registry, LanguageSessionPoolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Registry = registry;
        _options = options ?? new LanguageSessionPoolOptions();
        if (_options.SweepInterval > TimeSpan.Zero && _options.IdleTimeout > TimeSpan.Zero)
        {
            _sweep = Task.Run(() => SweepAsync(_lifetime.Token));
        }
    }

    public LanguageRegistry Registry { get; }

    public IReadOnlyList<LanguageSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions.Values.Where(static slot => !slot.Retired).Select(static slot => slot.Session)];
            }
        }
    }

    public int SessionCount
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    public async Task<LanguageSession?> GetSessionAsync(string? languageId, string workspacePath, CancellationToken ct = default)
    {
        var language = Registry.GetById(languageId);
        return language is null ? null : await GetSessionAsync(language, workspacePath, ct).ConfigureAwait(false);
    }

    public async Task<LanguageSession?> GetSessionAsync(LanguageDefinition language, string workspacePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (language.Server is null || Volatile.Read(ref _disposed) != 0)
        {
            return null;
        }

        var key = new SessionKey(NormalizeWorkspace(workspacePath), language.Id);
        var existing = Find(key);
        if (existing is not null)
        {
            return existing;
        }

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return null;
            }

            existing = Find(key);
            if (existing is not null)
            {
                return existing;
            }

            var session = new LanguageSession(
                new LanguageServerLaunch(language.Server.Command, language.Server.Arguments, language.Server.Environment, key.Workspace),
                new LanguageSessionOptions
                {
                    WorkspacePath = key.Workspace,
                    LanguageId = language.Id,
                    InitializationOptions = language.InitializationOptions,
                    RestartPolicy = _options.RestartPolicy,
                    RequestTimeout = _options.RequestTimeout,
                    Log = _options.Log,
                });

            var started = await session.TryStartAsync(ct).ConfigureAwait(false);
            if (!started)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            var kept = false;
            lock (_gate)
            {
                if (_disposed == 0)
                {
                    _sessions[key] = new SessionSlot(session);
                    kept = true;
                }
            }

            if (!kept)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return session;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is LanguageServerNotFoundException or LanguageServerCrashedException or IOException)
        {
            _options.Log?.Invoke($"Language session for '{language.Id}' is unavailable: {ex.Message}");
            return null;
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Detects the language of a document, marks it and opens it in its session.</summary>
    public async Task<LanguageSession?> OpenDocumentAsync(string workspacePath, TextDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var languageId = document.LanguageId ?? Registry.DetectLanguageId(document.Path);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var session = await GetSessionAsync(languageId, workspacePath, ct).ConfigureAwait(false);
            if (session is null)
            {
                return null;
            }

            if (!session.TryLease())
            {
                continue;
            }

            try
            {
                document.LanguageId ??= languageId;
                await session.OpenAsync(document, ct).ConfigureAwait(false);
                return session;
            }
            finally
            {
                session.ReleaseLease();
            }
        }

        return null;
    }

    public async ValueTask CloseDocumentAsync(string workspacePath, TextDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var languageId = document.LanguageId ?? Registry.DetectLanguageId(document.Path);
        var key = new SessionKey(NormalizeWorkspace(workspacePath), languageId ?? string.Empty);
        LanguageSession? session;
        lock (_gate)
        {
            session = _sessions.TryGetValue(key, out var slot) && !slot.Retired ? slot.Session : null;
        }

        if (session is null || !session.TryLease())
        {
            return;
        }

        try
        {
            await session.CloseAsync(document, ct).ConfigureAwait(false);
        }
        finally
        {
            session.ReleaseLease();
        }
    }

    /// <summary>Shuts one session down and forgets it, e.g. when a workspace closes.</summary>
    public async ValueTask CloseSessionAsync(string? languageId, string workspacePath)
    {
        LanguageSession? session = null;
        var key = new SessionKey(NormalizeWorkspace(workspacePath), languageId ?? string.Empty);
        lock (_gate)
        {
            if (!_sessions.Remove(key, out var slot))
            {
                return;
            }

            slot.Retired = true;
            session = slot.Session;
        }

        await session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Stops every session of a workspace.</summary>
    public async ValueTask CloseWorkspaceAsync(string workspacePath)
    {
        var workspace = NormalizeWorkspace(workspacePath);
        List<LanguageSession> sessions = [];
        lock (_gate)
        {
            List<SessionKey>? keys = null;
            foreach (var (key, slot) in _sessions)
            {
                if (key.Workspace != workspace)
                {
                    continue;
                }

                slot.Retired = true;
                sessions.Add(slot.Session);
                (keys ??= []).Add(key);
            }

            if (keys is not null)
            {
                foreach (var key in keys)
                {
                    _sessions.Remove(key);
                }
            }
        }

        foreach (var session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        if (_sweep is not null)
        {
            try
            {
                await _sweep.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        List<LanguageSession> sessions;
        lock (_gate)
        {
            sessions = [.. _sessions.Values.Select(static slot => slot.Session)];
            foreach (var slot in _sessions.Values)
            {
                slot.Retired = true;
            }

            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _startGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await SweepIdleSessionsAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Shuts down sessions idle past the idle timeout; the pool also does this on its own schedule.</summary>
    public async Task SweepIdleSessionsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var cutoff = DateTimeOffset.UtcNow - _options.IdleTimeout;
        List<LanguageSession>? idle = null;
        lock (_gate)
        {
            List<SessionKey>? keys = null;
            foreach (var (key, slot) in _sessions)
            {
                if (slot.Retired)
                {
                    continue;
                }

                var session = slot.Session;
                if (session.HasActiveWork || session.LastUsedAt > cutoff || session.OpenDocuments.Count > 0)
                {
                    continue;
                }

                slot.Retired = true;
                (keys ??= []).Add(key);
                (idle ??= []).Add(session);
            }

            if (keys is not null)
            {
                foreach (var key in keys)
                {
                    _sessions.Remove(key);
                }
            }
        }

        if (idle is null)
        {
            return;
        }

        foreach (var session in idle)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is LspProtocolException or IOException or InvalidOperationException)
            {
                _options.Log?.Invoke($"Failed to dispose an idle language session: {ex.Message}");
            }
        }
    }

    private LanguageSession? Find(SessionKey key)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var slot))
            {
                return null;
            }

            if (!slot.Retired && slot.Session.State != LanguageSessionState.Disposed)
            {
                slot.Session.Touch();
                return slot.Session;
            }

            _sessions.Remove(key);
            return null;
        }
    }

    private static string NormalizeWorkspace(string workspacePath) =>
        string.IsNullOrWhiteSpace(workspacePath)
            ? Directory.GetCurrentDirectory()
            : System.IO.Path.GetFullPath(workspacePath);

    private sealed class SessionSlot(LanguageSession session)
    {
        public LanguageSession Session { get; } = session;

        public bool Retired { get; set; }
    }

    private readonly record struct SessionKey(string Workspace, string LanguageId)
    {
        public bool Equals(SessionKey other) =>
            string.Equals(Workspace, other.Workspace, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(LanguageId, other.LanguageId, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Workspace),
            StringComparer.OrdinalIgnoreCase.GetHashCode(LanguageId));
    }
}
