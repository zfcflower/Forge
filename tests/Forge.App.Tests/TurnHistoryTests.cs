using Forge.Abstractions;
using Forge.App.Services;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The strip's turn history: the metrics table is the source (every turn, failures included), and a database without the
/// table falls back to the checkpoints exactly as it used to.
/// </summary>
public sealed class TurnHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 1, 0, 0, TimeSpan.Zero);

    private static TurnMetric Metric(int turn, TurnMetricStatus status = TurnMetricStatus.Completed, TimeSpan? duration = null) =>
        new("s1", turn, Start, Start + (duration ?? TimeSpan.Zero), status);

    private static TurnCheckpoint Checkpoint(int turn, TimeSpan? duration = null) => new(
        "s1",
        turn,
        "/work",
        "before",
        duration is null ? null : "after",
        Start,
        duration is null ? null : Start + duration);

    [Fact]
    public async Task A_failed_turn_counts_as_a_turn_but_adds_no_duration()
    {
        var metrics = new FakeTurnMetricsStore();
        metrics.Rows.Add(Metric(1, duration: TimeSpan.FromSeconds(4)));
        metrics.Rows.Add(Metric(2, TurnMetricStatus.Failed));
        metrics.Rows.Add(Metric(3, TurnMetricStatus.Stopped, TimeSpan.FromSeconds(6)));

        var history = await new TurnHistoryReader(metrics, new FakeTurnCheckpointStore()).ReadAsync("s1", CancellationToken.None);

        // Three turns ran, so the averages are spread over three; the failed one ended where it started and adds nothing.
        Assert.Equal(3, history.Turns);
        Assert.Equal(TimeSpan.FromSeconds(10), history.Duration);
    }

    [Fact]
    public async Task A_database_without_the_table_reads_the_checkpoints_and_asks_only_once()
    {
        var metrics = new FakeTurnMetricsStore { Exists = false };
        metrics.Rows.Add(Metric(1, duration: TimeSpan.FromSeconds(30)));
        var checkpoints = new FakeTurnCheckpointStore();
        checkpoints.Rows.Add(Checkpoint(1, TimeSpan.FromSeconds(4)));
        checkpoints.Rows.Add(Checkpoint(2));
        var reader = new TurnHistoryReader(metrics, checkpoints);

        var history = await reader.ReadAsync("s1", CancellationToken.None);
        var again = await reader.ReadAsync("s1", CancellationToken.None);

        Assert.Equal(2, history.Turns);
        Assert.Equal(TimeSpan.FromSeconds(4), history.Duration);
        Assert.Equal(history, again);

        // The metrics rows are ignored on the fallback path, and the existence probe is cached for the life of the reader.
        Assert.Equal(1, metrics.ProbeCount);
        Assert.Equal(2, checkpoints.ListCalls);
        Assert.True(reader.MetricsTableKnown);
    }

    [Fact]
    public async Task The_metrics_table_is_read_when_it_exists()
    {
        var metrics = new FakeTurnMetricsStore();
        metrics.Rows.Add(Metric(1, duration: TimeSpan.FromSeconds(5)));
        var checkpoints = new FakeTurnCheckpointStore();
        checkpoints.Rows.Add(Checkpoint(1, TimeSpan.FromSeconds(99)));

        var history = await new TurnHistoryReader(metrics, checkpoints).ReadAsync("s1", CancellationToken.None);

        Assert.Equal(1, history.Turns);
        Assert.Equal(TimeSpan.FromSeconds(5), history.Duration);
        Assert.Equal(0, checkpoints.ListCalls);
    }

    [Fact]
    public void The_cutoff_keeps_this_windows_turns_out_of_the_history_count()
    {
        var rows = new[]
        {
            Metric(1, duration: TimeSpan.FromMinutes(1)),
            Metric(2, duration: TimeSpan.FromMinutes(2)),
            Metric(3, duration: TimeSpan.FromMinutes(4)),
        };

        var full = SessionTurnHistory.From(rows);
        var cut = SessionTurnHistory.From(rows, maxTurnExclusive: 3);

        Assert.Equal(3, full.Turns);
        Assert.Equal(TimeSpan.FromMinutes(7), full.Duration);
        Assert.Equal(2, cut.Turns);
        Assert.Equal(TimeSpan.FromMinutes(3), cut.Duration);
    }
}
