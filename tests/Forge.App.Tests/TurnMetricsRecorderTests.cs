using System.Diagnostics;
using Forge.App.Agents;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The recorder's contract: a turn end never waits on, allocates for or fails because of its own metrics. Every test here
/// drives the one entry point the App uses and checks that the row either lands or is dropped — never that the caller hangs.
/// </summary>
public sealed class TurnMetricsRecorderTests
{
    private static TurnMetricsRow Row(int turn = 3) => new(
        "s1",
        turn,
        new DateTimeOffset(2026, 11, 18, 9, 30, 0, TimeSpan.FromHours(8)),
        new DateTimeOffset(2026, 11, 18, 1, 31, 0, TimeSpan.Zero),
        TurnMetricStatus.Completed,
        "anthropic",
        "claude",
        null,
        11,
        22,
        33,
        44,
        5,
        6)
    {
        StreamMs = 1_500,
    };

    [Fact]
    public async Task A_database_without_the_table_never_enqueues_a_row()
    {
        var store = new FakeTurnMetricsStore { Exists = false };
        var recorder = new TurnMetricsRecorder(store);
        await recorder.Probed;

        recorder.Record(Row());
        recorder.Record(Row(4));
        await recorder.DisposeAsync();

        Assert.False(recorder.Enabled);
        Assert.Equal(1, store.ProbeCount);
        Assert.Equal(0, store.WriteAttempts);
        Assert.Empty(store.Written);
    }

    [Fact]
    public async Task An_enabled_recorder_writes_the_row_it_was_handed()
    {
        var store = new FakeTurnMetricsStore();
        var recorder = new TurnMetricsRecorder(store);
        await recorder.Probed;
        Assert.True(recorder.Enabled);

        recorder.Record(Row());
        await recorder.DisposeAsync();

        var written = Assert.Single(store.Written);
        Assert.Equal("s1", written.SessionId);
        Assert.Equal(3, written.Turn);
        Assert.Equal(TurnMetricStatus.Completed, written.Status);
        Assert.Equal("anthropic", written.Provider);
        Assert.Equal("claude", written.Model);
        Assert.Equal(11, written.InputTokens);
        Assert.Equal(5, written.CacheWrite1hTokens);
        Assert.Equal(6, written.ToolCalls);
        Assert.Equal(TimeSpan.FromMilliseconds(1_500), written.StreamElapsed);
    }

    [Fact]
    public async Task A_failing_write_is_swallowed_and_the_next_row_is_still_tried()
    {
        var store = new FakeTurnMetricsStore
        {
            OnWrite = _ => throw new InvalidOperationException("database is gone"),
        };
        var recorder = new TurnMetricsRecorder(store);
        await recorder.Probed;

        recorder.Record(Row());
        recorder.Record(Row(4));
        await recorder.DisposeAsync();

        // Both rows reached the store and both failed; neither failure came back at the caller.
        Assert.Equal(2, store.WriteAttempts);
        Assert.Empty(store.Written);
    }

    [Fact]
    public async Task A_full_queue_drops_the_row_instead_of_waiting()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeTurnMetricsStore { OnWrite = _ => gate.Task };
        var recorder = new TurnMetricsRecorder(store);
        await recorder.Probed;

        for (var turn = 1; turn <= TurnMetricsRecorder.Capacity + 8; turn++)
        {
            recorder.Record(Row(turn));
        }

        // The writer is stopped inside the first row, so the queue cannot take them all: the surplus is dropped, counted,
        // and the caller never learned about it.
        Assert.True(recorder.Dropped > 0);

        gate.SetResult();
        await recorder.DisposeAsync();
    }

    [Fact]
    public async Task Closing_abandons_the_rows_a_stalled_database_still_holds()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeTurnMetricsStore { OnWrite = _ => gate.Task };
        var recorder = new TurnMetricsRecorder(store);
        await recorder.Probed;
        for (var turn = 1; turn <= 3; turn++)
        {
            recorder.Record(Row(turn));
        }

        var elapsed = Stopwatch.StartNew();
        await recorder.DisposeAsync();
        elapsed.Stop();

        // Bounded, not unbounded: the budget is two seconds plus the usual scheduling noise.
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"closing waited {elapsed.Elapsed}");
        Assert.Equal(1, store.WriteAttempts);
        Assert.Empty(store.Written);

        // Let the stalled write finish so the abandoned writer exits at its next row boundary.
        gate.SetResult();
    }

    [Fact]
    public async Task A_closed_recorder_swallows_late_rows()
    {
        var store = new FakeTurnMetricsStore();
        var recorder = new TurnMetricsRecorder(store);
        await recorder.Probed;
        await recorder.DisposeAsync();

        recorder.Record(Row());

        Assert.Equal(0, store.WriteAttempts);
    }

    [Fact]
    public async Task A_probe_that_cannot_answer_bypasses_the_feature()
    {
        var store = new ThrowingProbeStore();
        var recorder = new TurnMetricsRecorder(store);
        await recorder.Probed;

        recorder.Record(Row());
        await recorder.DisposeAsync();

        Assert.False(recorder.Enabled);
        Assert.Equal(0, store.WriteAttempts);
    }

    /// <summary>A store whose one existence probe fails: the recorder treats an unreadable database as one without the table.</summary>
    private sealed class ThrowingProbeStore : ITurnMetricsStore
    {
        public int WriteAttempts { get; private set; }

        public Task<bool> ExistsAsync(CancellationToken ct) => throw new InvalidOperationException("no database file");

        public Task WriteAsync(TurnMetric metric, CancellationToken ct)
        {
            WriteAttempts++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TurnMetric>> ListAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TurnMetric>>([]);

        public Task<int> MaxTurnAsync(string sessionId, CancellationToken ct) => Task.FromResult(0);

        public ValueTask DisposeAsync() => default;

        public void Dispose()
        {
        }
    }
}
