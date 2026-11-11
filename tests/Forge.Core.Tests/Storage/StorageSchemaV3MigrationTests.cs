using System.Globalization;
using Forge.Abstractions;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>
/// Pins the forge.db v2 → v3 step: a timestamp another writer stored in a legal but foreign spelling is rewritten in
/// place (same instant, canonical text) instead of poisoning every read, and from then on the database itself refuses
/// that spelling on every timestamp column, so the failure that left the project tree empty cannot come back. A value
/// that names no unambiguous instant is deliberately left alone — the strict read keeps naming it.
/// </summary>
public sealed class StorageSchemaV3MigrationTests
{
    /// <summary>The exact value found in a real database: <c>datetime.isoformat()</c> writes six fractional digits.</summary>
    private const string ForeignTimestamp = "2026-11-04T07:44:28.766342+00:00";

    private const string CanonicalTimestamp = "2026-11-04T07:44:28.7663420+00:00";

    private const string SeedTimestamp = "2026-01-01T00:00:00.0000000+00:00";

    [Fact]
    public async Task A_foreign_project_timestamp_left_by_another_writer_is_repaired_and_the_registry_reads_it()
    {
        using var directory = new TempDirectory();
        await using (var registry = new SqliteProjectRegistry(directory.Path))
        {
            await registry.AddAsync(directory.Combine("Repo"), "Repo", ProjectKind.Repository, default);
        }

        await ReopenAsVersion2Async(directory);
        await TodoSchemaRaw.ExecuteAsync(directory, $"UPDATE projects SET added_at = '{ForeignTimestamp}'");

        await using var reopened = new SqliteProjectRegistry(directory.Path);
        var project = Assert.Single(await reopened.ListAsync(default));
        Assert.Equal("Repo", project.Name);
        Assert.Equal(DateTimeOffset.Parse(ForeignTimestamp, CultureInfo.InvariantCulture), project.AddedAt);

        // The step rewrote the stored text itself; the read that used to fail works without any repair on the read path.
        Assert.Equal(CanonicalTimestamp, await TextAsync(directory, "SELECT added_at FROM projects"));
        Assert.Equal(9L, await TodoSchemaRaw.UserVersionAsync(directory));
    }

    [Fact]
    public async Task The_repair_keeps_the_instant_and_the_offset_of_a_foreign_spelling()
    {
        using var directory = new TempDirectory();
        await using (var registry = new SqliteProjectRegistry(directory.Path))
        {
            await registry.AddAsync(directory.Combine("Repo"), "Repo", ProjectKind.Repository, default);
        }

        await ReopenAsVersion2Async(directory);
        await TodoSchemaRaw.ExecuteAsync(directory, "UPDATE projects SET added_at = '2026-11-04T07:44:28.766342+02:00'");

        await using var reopened = new SqliteProjectRegistry(directory.Path);
        var project = Assert.Single(await reopened.ListAsync(default));
        Assert.Equal(TimeSpan.FromHours(2), project.AddedAt.Offset);
        Assert.Equal("2026-11-04T07:44:28.7663420+02:00", await TextAsync(directory, "SELECT added_at FROM projects"));
    }

    [Fact]
    public async Task A_timestamp_without_an_offset_is_left_untouched_and_the_read_still_names_the_row()
    {
        using var directory = new TempDirectory();
        await using (var registry = new SqliteProjectRegistry(directory.Path))
        {
            await registry.AddAsync(directory.Combine("Repo"), "Repo", ProjectKind.Repository, default);
        }

        await ReopenAsVersion2Async(directory);
        await TodoSchemaRaw.ExecuteAsync(directory, "UPDATE projects SET added_at = '2026-11-04 07:44:28'");

        await using var reopened = new SqliteProjectRegistry(directory.Path);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => reopened.ListAsync(default));
        Assert.Contains("malformed added_at value", failure.Message, StringComparison.Ordinal);

        // Repairing it would have to invent an instant, so the value stays exactly as the other tool wrote it.
        Assert.Equal("2026-11-04 07:44:28", await TextAsync(directory, "SELECT added_at FROM projects"));
        Assert.Equal(9L, await TodoSchemaRaw.UserVersionAsync(directory));
    }

    [Fact]
    public async Task Every_timestamp_column_gets_an_insert_and_an_update_guard()
    {
        using var directory = new TempDirectory();
        await EnsureCurrentSchemaAsync(directory);

        Assert.Equal(
            [
                "trg_messages_at_insert", "trg_messages_at_update",
                "trg_plan_summaries_updated_at_insert", "trg_plan_summaries_updated_at_update",
                "trg_projects_added_at_insert", "trg_projects_added_at_update",
                "trg_projects_closed_at_insert", "trg_projects_closed_at_update",
                "trg_sessions_created_at_insert", "trg_sessions_created_at_update",
                "trg_sessions_deleted_at_insert", "trg_sessions_deleted_at_update",
                "trg_sessions_last_active_at_insert", "trg_sessions_last_active_at_update",
                "trg_storage_notices_acknowledged_at_insert", "trg_storage_notices_acknowledged_at_update",
                "trg_storage_notices_created_at_insert", "trg_storage_notices_created_at_update",
                "trg_summaries_at_insert", "trg_summaries_at_update",
                "trg_todo_groups_updated_at_insert", "trg_todo_groups_updated_at_update",
                "trg_todos_updated_at_insert", "trg_todos_updated_at_update",
                "trg_turn_checkpoints_ended_at_insert", "trg_turn_checkpoints_ended_at_update",
                "trg_turn_checkpoints_started_at_insert", "trg_turn_checkpoints_started_at_update",
                "trg_ui_state_updated_at_insert", "trg_ui_state_updated_at_update",
            ],
            await TriggerNamesAsync(directory));
    }

    public static TheoryData<string, string> RefusedStatements => new()
    {
        { "sessions.created_at", "INSERT INTO sessions (id, workspace_path, title, created_at, status, last_active_at, last_active_ticks) VALUES ('s-new', 'D:/ws', 't', $at, 'Active', $seed, 0)" },
        { "sessions.last_active_at", "INSERT INTO sessions (id, workspace_path, title, created_at, status, last_active_at, last_active_ticks) VALUES ('s-new', 'D:/ws', 't', $seed, 'Active', $at, 0)" },
        { "messages.at", "INSERT INTO messages (session_id, seq, role, content_json, at) VALUES ('s-guard', 2, 'user', '[]', $at)" },
        { "summaries.at", "INSERT INTO summaries (session_id, layer, from_seq, to_seq, text, at) VALUES ('s-guard', 0, 2, 2, 'x', $at)" },
        { "projects.added_at", "INSERT INTO projects (id, name, root_path, root_key, kind, sort_order, added_at) VALUES ('p-new', 'P', 'D:/r2', 'D:/R2', 'Repository', 1, $at)" },
        { "todos.updated_at", "INSERT INTO todos (id, session_id, title, status, order_index, updated_at) VALUES ('t-new', 's-guard', 'T', 'Pending', 1, $at)" },
        { "ui_state.updated_at", "INSERT INTO ui_state (key, version, json, updated_at) VALUES ('k-new', 1, '{}', $at)" },
        { "turn_checkpoints.started_at", "INSERT INTO turn_checkpoints (session_id, turn, worktree_path, tree_before, started_at) VALUES ('s-guard', 1, 'D:/wt', 'abc', $at)" },
        { "turn_checkpoints.ended_at", "INSERT INTO turn_checkpoints (session_id, turn, worktree_path, tree_before, started_at, ended_at) VALUES ('s-guard', 2, 'D:/wt', 'abc', $seed, $at)" },
        { "plan_summaries.updated_at", "INSERT INTO plan_summaries (session_id, text, updated_at) VALUES ('s-new', 'x', $at)" },
    };

    public static TheoryData<string, string> RefusedUpdates => new()
    {
        { "sessions.created_at", "UPDATE sessions SET created_at = $at WHERE id = 's-guard'" },
        { "sessions.last_active_at", "UPDATE sessions SET last_active_at = $at WHERE id = 's-guard'" },
        { "messages.at", "UPDATE messages SET at = $at WHERE session_id = 's-guard' AND seq = 1" },
        { "summaries.at", "UPDATE summaries SET at = $at WHERE session_id = 's-guard'" },
        { "projects.added_at", "UPDATE projects SET added_at = $at WHERE id = 'p-guard'" },
        { "todos.updated_at", "UPDATE todos SET updated_at = $at WHERE id = 't-guard'" },
        { "ui_state.updated_at", "UPDATE ui_state SET updated_at = $at WHERE key = 'k-guard'" },
        { "turn_checkpoints.started_at", "UPDATE turn_checkpoints SET started_at = $at WHERE session_id = 's-guard' AND turn = 0" },
        { "turn_checkpoints.ended_at", "UPDATE turn_checkpoints SET ended_at = $at WHERE session_id = 's-guard' AND turn = 0" },
        { "plan_summaries.updated_at", "UPDATE plan_summaries SET updated_at = $at WHERE session_id = 's-guard'" },
    };

    [Theory]
    [MemberData(nameof(RefusedStatements))]
    public async Task The_database_refuses_a_foreign_spelling_on_insert(string column, string sql)
    {
        using var directory = new TempDirectory();
        await EnsureCurrentSchemaAsync(directory);
        await SeedCanonicalRowsAsync(directory);

        var failure = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(directory, sql, ForeignTimestamp));
        Assert.Contains(column, failure.Message, StringComparison.Ordinal);
        Assert.Contains("canonical", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(RefusedUpdates))]
    public async Task The_database_refuses_a_foreign_spelling_on_update(string column, string sql)
    {
        using var directory = new TempDirectory();
        await EnsureCurrentSchemaAsync(directory);
        await SeedCanonicalRowsAsync(directory);

        var failure = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(directory, sql, ForeignTimestamp));
        Assert.Contains(column, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canonical_values_in_another_offset_and_a_null_ended_at_are_still_accepted()
    {
        using var directory = new TempDirectory();
        await EnsureCurrentSchemaAsync(directory);

        await ExecuteAsync(
            directory,
            "INSERT INTO ui_state (key, version, json, updated_at) VALUES ('k-plus8', 1, '{}', '2026-01-01T00:00:00.0000000+08:00');" +
            "INSERT INTO turn_checkpoints (session_id, turn, worktree_path, tree_before, started_at) " +
            "VALUES ('s-open', 0, 'D:/wt', 'abc', '2026-01-01T00:00:00.0000000+00:00');");
        await ExecuteAsync(
            directory,
            "UPDATE turn_checkpoints SET ended_at = '2026-01-01T00:00:01.0000000+08:00' WHERE session_id = 's-open'");

        Assert.Equal(
            "2026-01-01T00:00:01.0000000+08:00",
            await TextAsync(directory, "SELECT ended_at FROM turn_checkpoints WHERE session_id = 's-open'"));

        await using var ui = new SqliteUiStateStore(directory.Path);
        Assert.Equal(1, (await ui.GetAsync("k-plus8", default))!.Version);
    }

    [Fact]
    public async Task A_foreign_session_timestamp_is_repaired_and_the_catalog_reads_it()
    {
        using var directory = new TempDirectory();
        await using (var store = new SqliteSessionStore(directory.Path))
        {
            await store.CreateSessionAsync("D:/ws", "t", default);
        }

        await ReopenAsVersion2Async(directory);
        await TodoSchemaRaw.ExecuteAsync(
            directory,
            $"UPDATE sessions SET created_at = '{ForeignTimestamp}', last_active_at = '{ForeignTimestamp}'");

        await using var reopened = new SqliteSessionStore(directory.Path);
        var session = Assert.Single(await reopened.ListSessionsAsync("D:/ws", default));
        Assert.Equal(DateTimeOffset.Parse(ForeignTimestamp, CultureInfo.InvariantCulture), session.CreatedAt);
        Assert.Equal(
            CanonicalTimestamp,
            await TextAsync(directory, "SELECT created_at FROM sessions"));
        Assert.Equal(CanonicalTimestamp, await TextAsync(directory, "SELECT last_active_at FROM sessions"));
    }

    /// <summary>Creates the current schema by opening one store, so the guards are installed by the real migration.</summary>
    private static async Task EnsureCurrentSchemaAsync(TempDirectory directory)
    {
        await using var registry = new SqliteProjectRegistry(directory.Path);
        await registry.ListAsync(default);
    }

    /// <summary>One canonical row (or two) per guarded table, so the update theories have something to change.</summary>
    private static Task SeedCanonicalRowsAsync(TempDirectory directory) =>
        TodoSchemaRaw.ExecuteAsync(
            directory,
            $$"""
            INSERT INTO sessions (id, workspace_path, title, created_at, status, last_active_at, last_active_ticks)
                VALUES ('s-guard', 'D:/ws', 't', '{{SeedTimestamp}}', 'Active', '{{SeedTimestamp}}', 0);
            INSERT INTO messages (session_id, seq, role, content_json, at)
                VALUES ('s-guard', 1, 'user', '[]', '{{SeedTimestamp}}');
            INSERT INTO summaries (session_id, layer, from_seq, to_seq, text, at)
                VALUES ('s-guard', 0, 1, 1, 'x', '{{SeedTimestamp}}');
            INSERT INTO projects (id, name, root_path, root_key, kind, sort_order, added_at)
                VALUES ('p-guard', 'P', 'D:/r', 'D:/R', 'Repository', 0, '{{SeedTimestamp}}');
            INSERT INTO todos (id, session_id, title, status, order_index, updated_at)
                VALUES ('t-guard', 's-guard', 'T', 'Pending', 0, '{{SeedTimestamp}}');
            INSERT INTO ui_state (key, version, json, updated_at)
                VALUES ('k-guard', 1, '{}', '{{SeedTimestamp}}');
            INSERT INTO turn_checkpoints (session_id, turn, worktree_path, tree_before, started_at)
                VALUES ('s-guard', 0, 'D:/wt', 'abc', '{{SeedTimestamp}}');
            INSERT INTO plan_summaries (session_id, text, updated_at)
                VALUES ('s-guard', 'x', '{{SeedTimestamp}}');
            """);

    /// <summary>
    /// Turns a current file back into the shape a v2 build left: no v4 groups objects, no triggers (v3 introduces them)
    /// and user_version 2, so the next open runs the real v3+v4 steps over it.
    /// </summary>
    private static async Task ReopenAsVersion2Async(TempDirectory directory)
    {
        var names = await TriggerNamesAsync(directory);
        var sql = string.Concat(names.Select(static name => $"DROP TRIGGER {name};\n"))
            + "DROP INDEX IF EXISTS ix_todos_group;\n"
            + "ALTER TABLE todos DROP COLUMN group_id;\n"
            + "ALTER TABLE todos DROP COLUMN origin;\n"
            + "DROP TABLE IF EXISTS todo_groups;\n"
            + "PRAGMA user_version = 2;";
        await TodoSchemaRaw.ExecuteAsync(directory, sql);
        SqliteConnection.ClearAllPools();
    }

    private static async Task<string[]> TriggerNamesAsync(TempDirectory directory)
    {
        await using var connection = new SqliteConnection(directory.ConnectionString.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger' ORDER BY name";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        SqliteConnection.ClearAllPools();
        return [.. names];
    }

    private static async Task ExecuteAsync(TempDirectory directory, string sql, string? at = null)
    {
        await using var connection = new SqliteConnection(directory.ConnectionString.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (at is not null)
        {
            if (sql.Contains("$at", StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue("$at", at);
            }

            if (sql.Contains("$seed", StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue("$seed", SeedTimestamp);
            }
        }

        await command.ExecuteNonQueryAsync();
        SqliteConnection.ClearAllPools();
    }

    private static async Task<string> TextAsync(TempDirectory directory, string sql)
    {
        await using var connection = new SqliteConnection(directory.ConnectionString.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        SqliteConnection.ClearAllPools();
        return Assert.IsType<string>(value);
    }
}
