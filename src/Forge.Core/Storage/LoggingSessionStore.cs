using Forge.Abstractions;

namespace Forge.Core.Storage;

/// <summary>
/// Decorates an <see cref="ISessionStore"/> with a JSONL event log: the authoritative store is
/// written first and the log line mirrors the sequence number it assigned.
/// </summary>
public sealed class LoggingSessionStore(ISessionStore inner, JsonlEventLog log) : ISessionStore, IAsyncDisposable, IDisposable
{
    private int _disposed;

    public ISessionStore Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    public JsonlEventLog Log { get; } = log ?? throw new ArgumentNullException(nameof(log));

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
