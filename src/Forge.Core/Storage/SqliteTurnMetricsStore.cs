using Microsoft.Data.Sqlite;

namespace Forge.Core.Storage;

/// <summary>
/// STUB — a deliberate no-op bypass so the App-side composition and recorder compile and behave safely before the real
/// store lands. It answers <see cref="ExistsAsync"/> with false, so the App-side recorder bypasses the whole feature;
/// every other method returns the same nothing a database without the table would (empty list, turn 0, a swallowed
/// write). Nothing here may throw: wiring this stub before the real implementation can never break a turn, a purge or a
/// shutdown. The real implementation follows <see cref="SqliteTurnCheckpointStore"/>: one long-lived connection, one
/// semaphore, one <c>BEGIN IMMEDIATE</c> transaction per write, a short busy timeout, and upserts keyed on
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

    public Task<bool> ExistsAsync(CancellationToken ct) => Task.FromResult(false);

    public Task WriteAsync(TurnMetric metric, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<TurnMetric>> ListAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TurnMetric>>(Array.Empty<TurnMetric>());

    public Task<int> MaxTurnAsync(string sessionId, CancellationToken ct) => Task.FromResult(0);

    public ValueTask DisposeAsync() => default;

    public void Dispose()
    {
    }
}
