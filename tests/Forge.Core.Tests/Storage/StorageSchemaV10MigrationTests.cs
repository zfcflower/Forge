using Forge.Abstractions;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>Pins the forge.db v10 step: the automation tables and the two session columns.</summary>
public sealed class StorageSchemaV10MigrationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task A_fresh_database_is_at_v10_with_the_automation_schema()
    {
        using var directory = new TempDirectory();
        await using (var store = new SqliteAutomationRunStore(directory.Path))
        {
            await store.GetStateAsync("x", Ct);
        }

        Assert.Equal(11L, await TodoSchemaRaw.UserVersionAsync(directory));
        Assert.True(await TodoSchemaRaw.TableExistsAsync(directory, "automation_runs"));
        Assert.True(await TodoSchemaRaw.TableExistsAsync(directory, "automation_state"));
        var sessionColumns = await TodoSchemaRaw.ColumnsAsync(directory, "sessions");
        Assert.Contains("automation_id", sessionColumns);
        Assert.Contains("automation_run_id", sessionColumns);
        Assert.Equal(
            1L,
            await TodoSchemaRaw.ScalarAsync<long>(
                directory,
                "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'ux_automation_runs_occurrence' AND sql LIKE '%UNIQUE%'"));
        Assert.Equal(
            2L,
            await TodoSchemaRaw.ScalarAsync<long>(
                directory,
                "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name IN ('ix_automation_runs_automation', 'ix_automation_runs_status')"));
    }

    [Fact]
    public async Task A_v9_database_keeps_its_rows_and_gets_null_automation_columns()
    {
        using var directory = new TempDirectory();
        string sessionId;
        await using (var store = new SqliteSessionStore(directory.Path))
        {
            ISessionCatalog catalog = store;
            sessionId = (await catalog.CreateAsync(new SessionCreate("D:/ws", "kept") { ProjectId = "p1", Branch = "main" }, Ct)).Id;
            await store.AppendAsync(sessionId, ChatMessage.User("hello"), null, Ct);
        }

        await DowngradeToV9Async(directory);
        Assert.Equal(9L, await TodoSchemaRaw.UserVersionAsync(directory));
        Assert.DoesNotContain("automation_id", await TodoSchemaRaw.ColumnsAsync(directory, "sessions"));

        await using (var store = new SqliteAutomationRunStore(directory.Path))
        {
            Assert.True(await store.TryInsertAsync(AutomationRuns.Make("r1"), Ct));
        }

        Assert.Equal(11L, await TodoSchemaRaw.UserVersionAsync(directory));
        await using var sessions = new SqliteSessionStore(directory.Path);
        var kept = await sessions.GetSessionAsync(sessionId, Ct);
        Assert.NotNull(kept);
        Assert.Equal("kept", kept.Title);
        Assert.Equal("p1", kept.ProjectId);
        Assert.Null(kept.AutomationId);
        Assert.Null(kept.AutomationRunId);
        Assert.Equal(1L, await TodoSchemaRaw.ScalarAsync<long>(directory, "SELECT count(*) FROM messages"));
        Assert.Equal(
            1L,
            await TodoSchemaRaw.ScalarAsync<long>(directory, "SELECT count(*) FROM sessions WHERE automation_id IS NULL AND automation_run_id IS NULL"));
    }

    [Fact]
    public async Task The_v10_step_is_re_runnable_after_the_pieces_already_exist()
    {
        using var directory = new TempDirectory();
        await using (var store = new SqliteAutomationRunStore(directory.Path))
        {
            await store.TryInsertAsync(AutomationRuns.Make("r1"), Ct);
        }

        // Only the version is rolled back: columns and tables of v10 are already there, as after a partial earlier attempt.
        await TodoSchemaRaw.SetUserVersionAsync(directory, 9);
        SqliteConnection.ClearAllPools();

        await using var reopened = new SqliteAutomationRunStore(directory.Path);
        Assert.NotNull(await reopened.GetAsync("r1", Ct));
        Assert.Equal(11L, await TodoSchemaRaw.UserVersionAsync(directory));
    }

    [Fact]
    public async Task A_failing_v10_step_rolls_back_to_v9()
    {
        using var directory = new TempDirectory();
        await using (var store = new SqliteAutomationRunStore(directory.Path))
        {
            await store.GetStateAsync("x", Ct);
        }

        await DowngradeToV9Async(directory);
        // A same-named view makes CREATE TABLE fail after the session columns were added, inside the same transaction.
        await TodoSchemaRaw.ExecuteAsync(directory, "CREATE VIEW automation_runs AS SELECT 1 AS id;");
        SqliteConnection.ClearAllPools();

        await using (var broken = new SqliteAutomationRunStore(directory.Path))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => broken.GetStateAsync("x", Ct));
        }

        Assert.Equal(9L, await TodoSchemaRaw.UserVersionAsync(directory));
        Assert.DoesNotContain("automation_id", await TodoSchemaRaw.ColumnsAsync(directory, "sessions"));

        await TodoSchemaRaw.ExecuteAsync(directory, "DROP VIEW automation_runs;");
        SqliteConnection.ClearAllPools();
        await using var repaired = new SqliteAutomationRunStore(directory.Path);
        await repaired.GetStateAsync("x", Ct);
        Assert.Equal(11L, await TodoSchemaRaw.UserVersionAsync(directory));
    }

    [Fact]
    public async Task The_automation_timestamp_columns_are_guarded_on_insert()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteAutomationRunStore(directory.Path);
        await store.GetStateAsync("x", Ct);
        await Assert.ThrowsAnyAsync<SqliteException>(() => TodoSchemaRaw.ExecuteAsync(
            directory,
            "INSERT INTO automation_runs (id, automation_id, automation_name, source, occurrence, status, started_at, started_ticks) " +
            "VALUES ('x', 'a', 'n', 'Manual', 'o', 'Running', 'yesterday', 0)"));
    }

    private static async Task DowngradeToV9Async(TempDirectory directory)
    {
        SqliteConnection.ClearAllPools();
        await TodoSchemaRaw.ExecuteAsync(
            directory,
            "DROP TABLE IF EXISTS automation_runs;\nDROP TABLE IF EXISTS automation_state;\n" +
            "ALTER TABLE sessions DROP COLUMN automation_id;\nALTER TABLE sessions DROP COLUMN automation_run_id;\n" +
            "PRAGMA user_version = 9;");
        SqliteConnection.ClearAllPools();
    }
}
