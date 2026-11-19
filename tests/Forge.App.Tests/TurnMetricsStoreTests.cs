using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The turn-metrics store's persisted contract: what a row round-trips to, that the upsert cannot duplicate, that an
/// unmeasured column stays null instead of zero, and that a database from before the feature reads as the same nothing
/// a missing table would.
/// </summary>
public sealed class TurnMetricsStoreTests : IDisposable
{
    private const string Session = "s1";
    private readonly string dir = Path.Combine(Path.GetTempPath(), "forge-turn-metrics-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static TurnMetric Metric(
        int turn,
        TurnMetricStatus status,
        DateTimeOffset? started = null,
        DateTimeOffset? ended = null) =>
        new(
            Session,
            turn,
            started ?? new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            ended ?? new DateTimeOffset(2026, 1, 2, 3, 4, 40, TimeSpan.Zero),
            status);

    [Fact]
    public async Task Round_trips_every_field()
    {
        await using var store = new SqliteTurnMetricsStore(dir);
        var metric = new TurnMetric(
            Session,
            7,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 2, 3, 4, 40, TimeSpan.Zero),
            TurnMetricStatus.Completed,
            Provider: "anthropic",
            Model: "claude-sonnet",
            InputTokens: 100,
            OutputTokens: 50,
            CacheReadTokens: 20,
            CacheWriteTokens: 10,
            CacheWrite1hTokens: 5,
            ToolCalls: 3,
            StreamElapsed: TimeSpan.FromMilliseconds(2500));
        await store.WriteAsync(metric, CancellationToken.None);

        var row = Assert.Single(await store.ListAsync(Session, CancellationToken.None));
        Assert.Equal(metric, row);

        // The storage shape itself: lowercase status, the canonical round-trip timestamps every forge.db column holds.
        using var connection = Open();
        Assert.Equal("completed", Text(connection, "SELECT status FROM turn_metrics WHERE turn = 7"));
        Assert.Equal("2026-01-02T03:04:05.0000000+00:00", Text(connection, "SELECT started_at FROM turn_metrics WHERE turn = 7"));
        Assert.Equal("2026-01-02T03:04:40.0000000+00:00", Text(connection, "SELECT ended_at FROM turn_metrics WHERE turn = 7"));
        Assert.Equal(2500L, Number(connection, "SELECT stream_ms FROM turn_metrics WHERE turn = 7"));

        // The timestamp guards fire: a row that is not the canonical round-trip shape is refused by the database itself.
        Assert.Throws<SqliteException>(() =>
            Exec(connection, "INSERT INTO turn_metrics (session_id, turn, started_at, ended_at, status, stream_ms) " +
                             "VALUES ('s1', 9, '2026-01-02T03:04:05Z', '2026-01-02T03:04:40+00:00', 'completed', 7)"));
    }

    [Fact]
    public async Task Rewriting_the_same_turn_replaces_the_row()
    {
        await using var store = new SqliteTurnMetricsStore(dir);
        await store.WriteAsync(Metric(1, TurnMetricStatus.Completed) with { InputTokens = 10, ToolCalls = 1 }, CancellationToken.None);
        await store.WriteAsync(Metric(1, TurnMetricStatus.Stopped) with { InputTokens = 20, ToolCalls = 2 }, CancellationToken.None);

        var rows = await store.ListAsync(Session, CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal(TurnMetricStatus.Stopped, row.Status);
        Assert.Equal(20, row.InputTokens);
        Assert.Equal(2, row.ToolCalls);
    }

    [Fact]
    public async Task A_failed_turn_keeps_every_token_column_null()
    {
        await using var store = new SqliteTurnMetricsStore(dir);
        await store.WriteAsync(Metric(2, TurnMetricStatus.Failed) with { Error = "boom" }, CancellationToken.None);

        var row = Assert.Single(await store.ListAsync(Session, CancellationToken.None));
        Assert.Equal(TurnMetricStatus.Failed, row.Status);
        Assert.Equal("boom", row.Error);
        Assert.Null(row.Provider);
        Assert.Null(row.Model);
        Assert.Null(row.InputTokens);
        Assert.Null(row.OutputTokens);
        Assert.Null(row.CacheReadTokens);
        Assert.Null(row.CacheWriteTokens);
        Assert.Null(row.CacheWrite1hTokens);
        Assert.Null(row.ToolCalls);

        // The one column a failed turn still measures is never null: a turn that streamed nothing carries zero,
        // never an "unknown".
        Assert.Equal(TimeSpan.Zero, row.StreamElapsed);
        using var connection = Open();
        Assert.Equal(0L, Number(connection, "SELECT stream_ms FROM turn_metrics WHERE turn = 2"));
    }

    [Fact]
    public async Task Max_turn_is_zero_without_rows_and_the_highest_with_them()
    {
        await using var store = new SqliteTurnMetricsStore(dir);
        Assert.Equal(0, await store.MaxTurnAsync(Session, CancellationToken.None));

        foreach (var turn in new[] { 1, 5, 3 })
        {
            await store.WriteAsync(Metric(turn, TurnMetricStatus.Completed), CancellationToken.None);
        }

        Assert.Equal(5, await store.MaxTurnAsync(Session, CancellationToken.None));
    }

    [Fact]
    public async Task List_returns_turns_in_order()
    {
        await using var store = new SqliteTurnMetricsStore(dir);
        foreach (var turn in new[] { 3, 1, 2 })
        {
            await store.WriteAsync(Metric(turn, TurnMetricStatus.Completed), CancellationToken.None);
        }

        var rows = await store.ListAsync(Session, CancellationToken.None);
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(m => m.Turn));
    }

    [Fact]
    public async Task A_database_without_the_table_reads_as_empty_and_refuses_writes()
    {
        await using (var store = new SqliteTurnMetricsStore(dir))
        {
            await store.WriteAsync(Metric(1, TurnMetricStatus.Completed), CancellationToken.None);
        }

        // A database this build never created: schema 11 without the table. Dropping it here reproduces exactly that
        // state (the ladder only runs below version 11, so nothing recreates it).
        using (var connection = Open())
        {
            Exec(connection, "DROP TABLE turn_metrics");
        }

        await using var reopened = new SqliteTurnMetricsStore(dir);
        Assert.False(await reopened.ExistsAsync(CancellationToken.None));
        Assert.Empty(await reopened.ListAsync(Session, CancellationToken.None));
        Assert.Equal(0, await reopened.MaxTurnAsync(Session, CancellationToken.None));
        await Assert.ThrowsAsync<SqliteException>(() => reopened.WriteAsync(Metric(4, TurnMetricStatus.Completed), CancellationToken.None));
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Directory.EnumerateFiles(dir, "*.db").Single() }.ToString());
        connection.Open();
        return connection;
    }

    private static string Text(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (string)cmd.ExecuteScalar()!;
    }

    private static long Number(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
