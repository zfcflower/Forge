using Forge.Abstractions;
using Forge.Core.Storage;

namespace Forge.App.Tests;

/// <summary>
/// A <c>turn_metrics</c> store with no database behind it: the existence answer, the rows it was handed and the timing of
/// every write are all the test's to decide. Nothing is mocked — the point of the recorder is what it does when a store
/// says no, fails or stalls, and those are three lines here.
/// </summary>
internal sealed class FakeTurnMetricsStore : ITurnMetricsStore
{
    private readonly List<TurnMetric> written = [];

    /// <summary>What the one existence probe answers; a database from before the feature says false.</summary>
    public bool Exists { get; init; } = true;

    /// <summary>Rows <see cref="ListAsync"/> answers with.</summary>
    public List<TurnMetric> Rows { get; } = [];

    public int ProbeCount { get; private set; }

    public int WriteAttempts { get; private set; }

    /// <summary>Replaces the write: throw to fail it, await a task to stall it.</summary>
    public Func<TurnMetric, Task>? OnWrite { get; set; }

    public IReadOnlyList<TurnMetric> Written => written;

    public Task<bool> ExistsAsync(CancellationToken ct)
    {
        ProbeCount++;
        return Task.FromResult(Exists);
    }

    public async Task WriteAsync(TurnMetric metric, CancellationToken ct)
    {
        WriteAttempts++;
        if (OnWrite is { } write)
        {
            await write(metric).ConfigureAwait(false);
        }

        written.Add(metric);
    }

    public Task<IReadOnlyList<TurnMetric>> ListAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TurnMetric>>([.. Rows.Where(row => row.SessionId == sessionId)]);

    public Task<int> MaxTurnAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult(Rows.Where(row => row.SessionId == sessionId).Select(row => row.Turn).DefaultIfEmpty(0).Max());

    public ValueTask DisposeAsync() => default;

    public void Dispose()
    {
    }
}

/// <summary>A <c>turn_checkpoints</c> store holding what the test put in it.</summary>
internal sealed class FakeTurnCheckpointStore : ITurnCheckpointStore
{
    public List<TurnCheckpoint> Rows { get; } = [];

    public int ListCalls { get; private set; }

    public Task BeginAsync(
        string sessionId,
        int turn,
        string worktreePath,
        string treeBefore,
        DateTimeOffset at,
        CancellationToken ct) => Task.CompletedTask;

    public Task<bool> CompleteAsync(string sessionId, int turn, string treeAfter, DateTimeOffset at, CancellationToken ct) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<TurnCheckpoint>> ListAsync(string sessionId, CancellationToken ct)
    {
        ListCalls++;
        return Task.FromResult<IReadOnlyList<TurnCheckpoint>>([.. Rows.Where(row => row.SessionId == sessionId)]);
    }
}
