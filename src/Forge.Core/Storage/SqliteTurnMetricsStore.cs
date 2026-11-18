using Microsoft.Data.Sqlite;

namespace Forge.Core.Storage;

/// <summary>
/// STUB — placeholder so the App-side composition and recorder compile and unit-test against
/// <see cref="ITurnMetricsStore"/> before the real store lands. Every method throws
/// <see cref="NotImplementedException"/>; the App-side recorder swallows every store failure, so the stub can never
/// break a turn. The real implementation follows <see cref="SqliteTurnCheckpointStore"/>: one long-lived connection,
/// one semaphore, one <c>BEGIN IMMEDIATE</c> transaction per write, a short busy timeout, and upserts keyed on
/// <c>(session_id, turn)</c>.
/// </summary>
public sealed class SqliteTurnMetricsStore : ITurnMetricsStore
{
    public SqliteTurnMetricsStore(string dataDirectory)
    {
    }

    public SqliteTurnMetricsStore(SqliteConnectionStringBuilder connectionString)
    {
    }

    public Task<bool> ExistsAsync(CancellationToken ct) => throw new NotImplementedException();

    public Task WriteAsync(TurnMetric metric, CancellationToken ct) => throw new NotImplementedException();

    public Task<IReadOnlyList<TurnMetric>> ListAsync(string sessionId, CancellationToken ct) => throw new NotImplementedException();

    public Task<int> MaxTurnAsync(string sessionId, CancellationToken ct) => throw new NotImplementedException();

    public ValueTask DisposeAsync() => default;

    public void Dispose()
    {
    }
}
