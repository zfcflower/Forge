using Forge.Abstractions;
using Forge.App.Agents;
using Forge.Core.Storage;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Turn numbering over two tables: a reopened general session continues its numbering from the metrics table, because it
/// has no git worktree and therefore no checkpoints — the defect the metrics table was there to fix.
/// </summary>
public sealed class TurnNumberingTests
{
    private static TurnCheckpoint Checkpoint(int turn) => new(
        "s1",
        turn,
        "/work",
        "tree" + turn.ToString(System.Globalization.CultureInfo.InvariantCulture),
        null,
        DateTimeOffset.UnixEpoch,
        null);

    private static TurnMetric Metric(int turn) => new(
        "s1",
        turn,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        TurnMetricStatus.Completed);

    private static TurnCheckpointService Service(FakeTurnCheckpointStore checkpoints, FakeTurnMetricsStore metrics) =>
        new(new GitCli(), checkpoints, metrics);

    [Fact]
    public async Task The_next_number_is_the_higher_of_the_two_tables_plus_one()
    {
        var checkpoints = new FakeTurnCheckpointStore();
        for (var turn = 1; turn <= 7; turn++)
        {
            checkpoints.Rows.Add(Checkpoint(turn));
        }

        var metrics = new FakeTurnMetricsStore();
        for (var turn = 1; turn <= 9; turn++)
        {
            metrics.Rows.Add(Metric(turn));
        }

        Assert.Equal(10, await Service(checkpoints, metrics).NextTurnNumberAsync("s1", CancellationToken.None));
    }

    [Fact]
    public async Task Checkpoints_alone_continue_where_they_stopped()
    {
        var checkpoints = new FakeTurnCheckpointStore();
        for (var turn = 1; turn <= 7; turn++)
        {
            checkpoints.Rows.Add(Checkpoint(turn));
        }

        Assert.Equal(8, await Service(checkpoints, new FakeTurnMetricsStore()).NextTurnNumberAsync("s1", CancellationToken.None));
    }

    [Fact]
    public async Task A_general_session_without_checkpoints_continues_from_its_metrics()
    {
        // No git worktree means no checkpoint at all: before the metrics table this reopened session numbered every new turn 1.
        var metrics = new FakeTurnMetricsStore();
        for (var turn = 1; turn <= 4; turn++)
        {
            metrics.Rows.Add(Metric(turn));
        }

        Assert.Equal(5, await Service(new FakeTurnCheckpointStore(), metrics).NextTurnNumberAsync("s1", CancellationToken.None));
    }

    [Fact]
    public async Task A_session_that_recorded_nothing_starts_at_one()
    {
        Assert.Equal(
            1,
            await Service(new FakeTurnCheckpointStore(), new FakeTurnMetricsStore()).NextTurnNumberAsync("s1", CancellationToken.None));
    }
}
