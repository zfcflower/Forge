using Forge.Abstractions;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The 13th ladder step adds <c>turn_metrics.effort</c> to a database that already carries the table, and does nothing to one
/// that never had it. Getting this wrong is silent in the worst place: a missing column fails every metrics write (the writer
/// swallows it) and blanks every reopened session's per-turn line, while a step that assumes the table aborts the migration and
/// the whole database refuses to open.
/// </summary>
public sealed class TurnMetricsEffortMigrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-effort-v13-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle can outlive the test; the temp directory is the OS's problem then.
        }
    }

    [Fact]
    public async Task A_database_that_already_recorded_turns_gets_the_step_column_and_keeps_its_rows()
    {
        var sessionId = await SeedTurnWithEffortAsync();

        // A database written before the column existed: the same table, one column less, still at schema 12.
        await RewriteAsOlderSchemaAsync(connect => connect.ExecuteAsync("ALTER TABLE turn_metrics DROP COLUMN effort"), 12);

        await using var store = new SqliteSessionStore(directory);
        await using var metrics = new SqliteTurnMetricsStore(directory);

        // Opening the database (what the app does on start-up) runs the ladder. The row recorded before is still there; only
        // its step is unknown.
        await store.ListSessionsAsync(null, CancellationToken.None);
        var upgraded = Assert.Single(await metrics.ListAsync(sessionId, CancellationToken.None));
        Assert.Equal("glm-5.3", upgraded.Model);
        Assert.Null(upgraded.Effort);

        // And the column is writable: the next recorded turn keeps its step.
        await metrics.WriteAsync(
            upgraded with { Turn = 2, Effort = ThinkingEffort.XHigh },
            CancellationToken.None);
        Assert.Equal(ThinkingEffort.XHigh, (await metrics.ListAsync(sessionId, CancellationToken.None))[1].Effort);
        Assert.Equal(15, await ReadUserVersionAsync());
    }

    [Fact]
    public async Task A_database_without_the_metrics_table_is_left_alone_and_the_feature_stays_bypassed()
    {
        await SeedTurnWithEffortAsync();
        await RewriteAsOlderSchemaAsync(connect => connect.ExecuteAsync("DROP TABLE turn_metrics"), 12);

        // The step checks for the table first: a database that never used the feature must still open.
        await using var store = new SqliteSessionStore(directory);
        await using var metrics = new SqliteTurnMetricsStore(directory);
        await store.ListSessionsAsync(null, CancellationToken.None);

        Assert.Equal(15, await ReadUserVersionAsync());
        Assert.False(await metrics.ExistsAsync(CancellationToken.None));
        Assert.Empty(await metrics.ListAsync("any-session", CancellationToken.None));
    }

    /// <summary>One session with one recorded turn that names its step, at the schema this build writes.</summary>
    private async Task<string> SeedTurnWithEffortAsync()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);
        await using var metrics = new SqliteTurnMetricsStore(directory);
        await metrics.WriteAsync(
            new TurnMetric(
                root.Id,
                1,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(5),
                TurnMetricStatus.Completed,
                "probe",
                "glm-5.3",
                Effort: ThinkingEffort.High),
            CancellationToken.None);
        return root.Id;
    }

    /// <summary>Rewrites the database the way a build from before the column would have left it: same data, older shape.</summary>
    private async Task RewriteAsOlderSchemaAsync(Func<SqliteConnection, Task> change, int version)
    {
        // Every pooled handle has to go before the file can be changed underneath the stores that just wrote it.
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "forge.db"),
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await change(connection);
        await connection.ExecuteAsync($"PRAGMA user_version = {version}");
    }

    private async Task<int> ReadUserVersionAsync()
    {
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "forge.db"),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }
}

internal static class SqliteEffortMigrationExtensions
{
    public static async Task ExecuteAsync(this SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
