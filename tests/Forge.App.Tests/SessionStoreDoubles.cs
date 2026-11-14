using Forge.Abstractions;

namespace Forge.App.Tests;

/// <summary>
/// In-memory message log shared by the turn-routing suites: appends number a session's messages from 1, reads return
/// them in order, and every catalog write reports success. The checkpoints own the assertions, so the log only has to
/// behave like a store.
/// </summary>
internal sealed class MemorySessionStore : ISessionStore, ISessionCatalog
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, List<StoredMessage>> log = new(StringComparer.Ordinal);

    public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) =>
        Task.FromResult(new SessionInfo("created", workspacePath, title, DateTimeOffset.UnixEpoch));

    public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<SessionInfo?>(null);

    public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionInfo>>([]);

    public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct)
    {
        lock (gate)
        {
            if (!log.TryGetValue(sessionId, out var messages))
            {
                messages = [];
                log[sessionId] = messages;
            }

            messages.Add(new StoredMessage(messages.Count + 1, message, usage, DateTimeOffset.UnixEpoch));
            return Task.FromResult((long)messages.Count);
        }
    }

    public IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct) =>
        ReadCoreAsync(sessionId, fromSeq, toSeq);

    private async IAsyncEnumerable<StoredMessage> ReadCoreAsync(string sessionId, long fromSeq, long? toSeq)
    {
        List<StoredMessage> snapshot;
        lock (gate)
        {
            snapshot = log.TryGetValue(sessionId, out var messages) ? [.. messages] : [];
        }

        foreach (var message in snapshot)
        {
            if (message.Seq >= fromSeq && (toSeq is null || message.Seq <= toSeq))
            {
                yield return message;
            }
        }

        await Task.Yield();
    }

    public Task<long> LastSeqAsync(string sessionId, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult(log.TryGetValue(sessionId, out var messages) ? (long)messages.Count : 0L);
        }
    }

    public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SummaryRecord>>([]);

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SearchHit>>([]);

    public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) =>
        Task.FromResult(new SessionInfo("created", request.WorkspacePath, request.Title, DateTimeOffset.UnixEpoch));

    public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionInfo>>([]);

    public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) => Task.FromResult(true);

    public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => Task.FromResult(true);

    public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// A session catalog base for test doubles: the double declares the reads and creates its suite observes, while the
/// write members (status, title, touch) succeed without recording anything — the suites assert through the double's own
/// records, never through these.
/// </summary>
internal abstract class SessionCatalogDouble : ISessionCatalog
{
    public abstract Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct);

    public abstract Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct);

    public virtual Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) => Task.FromResult(true);

    public virtual Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => Task.FromResult(true);

    public virtual Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// A session catalog double that refuses everything loudly: every member throws, so a suite overrides exactly the
/// surface it observes and an unexpected call fails the test instead of passing silently. The members the interface
/// gives a default (activate, handover, soft-delete, purge, …) keep those defaults.
/// </summary>
internal abstract class ThrowingSessionCatalog : ISessionCatalog
{
    public virtual Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) => throw new NotSupportedException();

    public virtual Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct) => throw new NotSupportedException();

    public virtual Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) => throw new NotSupportedException();

    public virtual Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => throw new NotSupportedException();

    public virtual Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>
/// A session store double that refuses everything loudly, including the catalog writes (production implements the two
/// interfaces on the same store); a suite overrides exactly the members it observes and an unexpected call fails the
/// test instead of passing silently.
/// </summary>
internal abstract class ThrowingSessionStore : ThrowingSessionCatalog, ISessionStore
{
    public virtual Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) => throw new NotSupportedException();

    public virtual Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

    public virtual Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) => throw new NotSupportedException();

    public virtual Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct) => throw new NotSupportedException();

    public virtual IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct) => throw new NotSupportedException();

    public virtual Task<long> LastSeqAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

    public virtual Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => throw new NotSupportedException();

    public virtual Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

    public virtual Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) => throw new NotSupportedException();
}
