using Forge.Abstractions;

namespace Forge.Core.Storage;

/// <summary>
/// Decorates an <see cref="ISessionStore"/> with a JSONL event log: the authoritative store is
/// written first and the log line mirrors the sequence number it assigned. Catalog members are forwarded
/// to the inner store when it implements <see cref="ISessionCatalog"/>; there is nothing to log for them.
/// </summary>
public sealed class LoggingSessionStore(ISessionStore inner, JsonlEventLog log) : ISessionStore, ISessionCatalog, IAsyncDisposable, IDisposable
{
    private int _disposed;

    public ISessionStore Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    public JsonlEventLog Log { get; } = log ?? throw new ArgumentNullException(nameof(log));

    /// <summary>
    /// The inner store's catalog surface. Reading this throws when the decorated store has no catalog, so a host
    /// that hands this decorator to a caller expecting metadata fails at composition time instead of silently.
    /// </summary>
    public ISessionCatalog Catalog => Inner as ISessionCatalog
        ?? throw new InvalidOperationException(
            $"The decorated session store ({Inner.GetType().Name}) does not implement {nameof(ISessionCatalog)}.");

    /// <summary>True when the decorated store exposes <see cref="ISessionCatalog"/>.</summary>
    public bool SupportsCatalog => Inner is ISessionCatalog;

    /// <summary>Raised when a log line could not be written; the message itself is already committed.</summary>
    public event Action<Exception>? LogFailed;

    public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) =>
        Inner.CreateSessionAsync(workspacePath, title, ct);

    public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) =>
        Inner.GetSessionAsync(sessionId, ct);

    public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) =>
        Inner.ListSessionsAsync(workspacePath, ct);

    public async Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct)
    {
        var at = DateTimeOffset.UtcNow;
        var seq = await Inner.AppendAsync(sessionId, message, usage, ct).ConfigureAwait(false);
        try
        {
            await Log.AppendAsync(sessionId, seq, message, usage, at, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            try
            {
                LogFailed?.Invoke(exception);
            }
            catch
            {
            }
        }

        return seq;
    }

    public IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct) =>
        Inner.ReadAsync(sessionId, fromSeq, toSeq, ct);

    public Task<long> LastSeqAsync(string sessionId, CancellationToken ct) => Inner.LastSeqAsync(sessionId, ct);

    public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => Inner.PutSummaryAsync(summary, ct);

    Task<SessionInfo> ISessionCatalog.CreateAsync(SessionCreate request, CancellationToken ct) =>
        Catalog.CreateAsync(request, ct);

    Task<IReadOnlyList<SessionInfo>> ISessionCatalog.QueryAsync(SessionQuery query, CancellationToken ct) =>
        Catalog.QueryAsync(query, ct);

    Task<bool> ISessionCatalog.SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) =>
        Catalog.SetStatusAsync(sessionId, status, ct);

    Task<bool> ISessionCatalog.RenameAsync(string sessionId, string title, CancellationToken ct) =>
        Catalog.RenameAsync(sessionId, title, ct);

    Task ISessionCatalog.TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) =>
        Catalog.TouchAsync(sessionId, at, ct);

    // Lifecycle members are forwarded explicitly: falling through to the interface defaults would turn every
    // occupancy / handover call on a decorated store into NotSupportedException.

    Task<bool> ISessionCatalog.ActivateAsync(string sessionId, CancellationToken ct) =>
        Catalog.ActivateAsync(sessionId, ct);

    Task<SessionInfo> ISessionCatalog.HandoverAsync(string oldSessionId, HandoverTarget target, CancellationToken ct) =>
        Catalog.HandoverAsync(oldSessionId, target, ct);

    Task<int> ISessionCatalog.SoftDeleteAsync(IReadOnlyList<string> sessionIds, CancellationToken ct) =>
        Catalog.SoftDeleteAsync(sessionIds, ct);

    Task<IReadOnlyList<string>> ISessionCatalog.PurgeAsync(string sessionId, CancellationToken ct) =>
        Catalog.PurgeAsync(sessionId, ct);

    Task<IReadOnlyList<SessionInfo>> ISessionCatalog.ListHistoryAsync(SessionQuery query, CancellationToken ct) =>
        Catalog.ListHistoryAsync(query, ct);

    Task<IReadOnlyList<string>> ISessionCatalog.HealMissingDirectoriesAsync(
        Func<string, bool> directoryExists,
        Func<string, bool> projectRootExists,
        CancellationToken ct) =>
        Catalog.HealMissingDirectoriesAsync(directoryExists, projectRootExists, ct);

    Task<bool> ISessionCatalog.SetPrefixFingerprintAsync(string sessionId, string fingerprint, CancellationToken ct) =>
        Catalog.SetPrefixFingerprintAsync(sessionId, fingerprint, ct);

    Task<int> ISessionCatalog.RenameBranchAsync(
        string projectId,
        string worktreePath,
        string oldBranch,
        string newBranch,
        CancellationToken ct) =>
        Catalog.RenameBranchAsync(projectId, worktreePath, oldBranch, newBranch, ct);

    Task<IReadOnlyList<StorageNotice>> ISessionCatalog.ReadNoticesAsync(CancellationToken ct) =>
        Catalog.ReadNoticesAsync(ct);

    Task ISessionCatalog.AcknowledgeNoticesAsync(IReadOnlyList<long> noticeIds, CancellationToken ct) =>
        Catalog.AcknowledgeNoticesAsync(noticeIds, ct);

    public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) =>
        Inner.GetSummariesAsync(sessionId, ct);

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
        Inner.SearchAsync(sessionId, query, limit, ct);

    /// <summary>Re-imports log lines that never reached the inner store, then flushes the log.</summary>
    public async Task<int> RecoverAsync(string sessionId, CancellationToken ct)
    {
        var recovered = await Log.RecoverAsync(sessionId, Inner, ct).ConfigureAwait(false);
        await Log.FlushAsync(sessionId, ct).ConfigureAwait(false);
        return recovered;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await Log.DisposeAsync().ConfigureAwait(false);
        switch (Inner)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Log.Dispose();
        (Inner as IDisposable)?.Dispose();
    }
}
