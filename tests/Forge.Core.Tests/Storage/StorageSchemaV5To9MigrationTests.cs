using System.Globalization;
using Forge.Abstractions;
using Forge.Core.Storage;
using Forge.IO;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>
/// Pins the forge.db v4 → v9 ladder of the session / worktree lifecycle: occupancy key + one-Active-root-per-key
/// index with deduplication and a storage notice (v5), soft delete (v6), project soft close (v7), fork columns (v8)
/// and inheritance / cache columns (v9). Each step is re-runnable.
/// </summary>
public sealed class StorageSchemaV5To9MigrationTests
{
    private const string Seed = "2026-01-01T00:00:00.0000000+00:00";

    private static readonly string[] LifecycleSessionColumns =
    [
        "occupancy_key", "deleted_at", "fork_of_branch", "fork_base_sha", "inherits_from_session_id",
        "inherits_up_to_seq", "lineage_root_id", "origin_kind", "prefix_fingerprint",
    ];

    [Fact]
    public async Task A_v4_database_climbs_to_v9_with_every_lifecycle_column_and_backfill()
    {
        using var directory = new TempDirectory();
        await CreateAsVersion4Async(directory);
        var repo = directory.Combine("repo");
        await InsertSessionAsync(directory, "s1", repo, null, "Active", null, 10);

        await OpenAsync(directory);

        Assert.Equal(9L, await TodoSchemaRaw.UserVersionAsync(directory));
        var sessionColumns = await TodoSchemaRaw.ColumnsAsync(directory, "sessions");
        foreach (var column in LifecycleSessionColumns)
        {
            Assert.Contains(column, sessionColumns);
        }

        Assert.Contains("closed_at", await TodoSchemaRaw.ColumnsAsync(directory, "projects"));
        Assert.True(await TodoSchemaRaw.TableExistsAsync(directory, "storage_notices"));
        Assert.Equal(OccupancyKey.Compute(null, repo), await TextAsync(directory, "SELECT occupancy_key FROM sessions WHERE id = 's1'"));
        Assert.Equal("s1", await TextAsync(directory, "SELECT lineage_root_id FROM sessions WHERE id = 's1'"));
        Assert.Equal("cold", await TextAsync(directory, "SELECT origin_kind FROM sessions WHERE id = 's1'"));
        Assert.Equal(
            1L,
            await TodoSchemaRaw.ScalarAsync<long>(
                directory,
                "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'ux_sessions_occupancy'"));
    }

    [Fact]
    public async Task Duplicate_active_roots_are_archived_except_the_most_recent_and_reported_in_a_notice()
    {
        using var directory = new TempDirectory();
        await CreateAsVersion4Async(directory);
        var repo = directory.Combine("repo");
        var worktree = directory.Combine("repo.worktrees", "wt");

        // Same key through a NULL worktree_path (falls back to workspace_path) and an explicit one.
        await InsertSessionAsync(directory, "a-old", repo, null, "Active", null, 100);
        await InsertSessionAsync(directory, "a-new", directory.Combine("elsewhere"), repo, "Active", null, 200);

        // Same key spelled differently (trailing separator; on Windows also another case); equal activity: larger id wins.
        var variant = (OperatingSystem.IsWindows() ? worktree.ToUpperInvariant() : worktree) + Path.DirectorySeparatorChar;
        await InsertSessionAsync(directory, "b-1", worktree, worktree, "Active", null, 50);
        await InsertSessionAsync(directory, "b-2", variant, variant, "Active", null, 50);

        // Neither archived sessions nor sub-agent sessions take part.
        await InsertSessionAsync(directory, "c-archived", repo, null, "Archived", null, 999);
        await InsertSessionAsync(directory, "d-child", repo, null, "Active", "a-old", 999);

        await OpenAsync(directory);

        Assert.Equal("Archived", await StatusAsync(directory, "a-old"));
        Assert.Equal("Active", await StatusAsync(directory, "a-new"));
        Assert.Equal("Archived", await StatusAsync(directory, "b-1"));
        Assert.Equal("Active", await StatusAsync(directory, "b-2"));
        Assert.Equal("Archived", await StatusAsync(directory, "c-archived"));
        Assert.Equal("Active", await StatusAsync(directory, "d-child"));

        Assert.Equal(1L, await TodoSchemaRaw.ScalarAsync<long>(directory, "SELECT count(*) FROM storage_notices"));
        Assert.Equal(StorageNoticeKinds.DedupeArchived, await TextAsync(directory, "SELECT kind FROM storage_notices"));
        Assert.Equal(
            """{"sessionIds":["a-old","b-1"]}""",
            await TextAsync(directory, "SELECT payload_json FROM storage_notices"));
        Assert.Equal(1L, await TodoSchemaRaw.ScalarAsync<long>(directory, "SELECT count(*) FROM storage_notices WHERE acknowledged_at IS NULL"));
    }

    [Fact]
    public async Task No_duplicates_means_no_notice()
    {
        using var directory = new TempDirectory();
        await CreateAsVersion4Async(directory);
        await InsertSessionAsync(directory, "s1", directory.Combine("a"), null, "Active", null, 1);
        await InsertSessionAsync(directory, "s2", directory.Combine("b"), null, "Active", null, 1);

        await OpenAsync(directory);

        Assert.Equal(0L, await TodoSchemaRaw.ScalarAsync<long>(directory, "SELECT count(*) FROM storage_notices"));
    }

    [Fact]
    public async Task Running_the_v5_to_v9_steps_again_changes_nothing()
    {
        using var directory = new TempDirectory();
        await CreateAsVersion4Async(directory);
        var repo = directory.Combine("repo");
        await InsertSessionAsync(directory, "x-1", repo, null, "Active", null, 1);
        await InsertSessionAsync(directory, "x-2", repo, null, "Active", null, 2);
        await OpenAsync(directory);
        var schema = await TodoSchemaRaw.SchemaSnapshotAsync(directory);

        await TodoSchemaRaw.SetUserVersionAsync(directory, 4);
        SqliteConnection.ClearAllPools();
        await OpenAsync(directory);

        Assert.Equal(9L, await TodoSchemaRaw.UserVersionAsync(directory));
        Assert.Equal(schema, await TodoSchemaRaw.SchemaSnapshotAsync(directory));
        Assert.Equal(1L, await TodoSchemaRaw.ScalarAsync<long>(directory, "SELECT count(*) FROM storage_notices"));
        Assert.Equal("Archived", await StatusAsync(directory, "x-1"));
        Assert.Equal("Active", await StatusAsync(directory, "x-2"));
    }

    [Fact]
    public async Task The_unique_index_refuses_a_second_active_root_of_one_key_but_not_a_child_or_an_archived_row()
    {
        using var directory = new TempDirectory();
        await OpenAsync(directory);
        var key = OccupancyKey.Compute(null, directory.Combine("repo"));
        await InsertMigratedAsync(directory, "r1", key, "Active", null);
        await InsertMigratedAsync(directory, "r2", key, "Archived", null);
        await InsertMigratedAsync(directory, "r3", key, "Active", "r1");

        var refusal = await Assert.ThrowsAsync<SqliteException>(() => InsertMigratedAsync(directory, "r4", key, "Active", null));
        Assert.Equal(2067, refusal.SqliteExtendedErrorCode);
    }

    [Fact]
    public async Task New_timestamp_columns_are_guarded_like_the_old_ones()
    {
        using var directory = new TempDirectory();
        await OpenAsync(directory);
        await InsertMigratedAsync(directory, "g1", "K", "Archived", null);

        await Assert.ThrowsAsync<SqliteException>(() =>
            TodoSchemaRaw.ExecuteAsync(directory, "UPDATE sessions SET deleted_at = '2026-01-01 00:00:00' WHERE id = 'g1'"));
        await TodoSchemaRaw.ExecuteAsync(directory, $"UPDATE sessions SET deleted_at = '{Seed}' WHERE id = 'g1'");
        await Assert.ThrowsAsync<SqliteException>(() =>
            TodoSchemaRaw.ExecuteAsync(
                directory,
                "INSERT INTO storage_notices (kind, payload_json, created_at) VALUES ('k', '{}', 'yesterday')"));
    }

    [Theory]
    [InlineData("repo")]
    [InlineData("repo/")]
    [InlineData("repo/./sub/..")]
    [InlineData("Repo.Worktrees/Feature-X")]
    public void The_occupancy_key_normalizes_exactly_like_WorkspacePath(string relative)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, relative.Replace('/', Path.DirectorySeparatorChar));
        var expected = WorkspacePath.Normalize(path);
        Assert.Equal(expected, OccupancyKey.Normalize(path));
        Assert.Equal(OperatingSystem.IsWindows() ? expected.ToUpperInvariant() : expected, OccupancyKey.Compute(null, path));
        Assert.Equal(OccupancyKey.Compute(path, directory.Path), OccupancyKey.Compute(null, path));
    }

    [Fact]
    public void The_occupancy_key_folds_case_only_on_windows()
    {
        using var directory = new TempDirectory();
        var lower = directory.Combine("wt");
        var upper = directory.Combine("WT");
        Assert.Equal(OperatingSystem.IsWindows(), OccupancyKey.Compute(lower, lower) == OccupancyKey.Compute(upper, upper));
    }

    private static async Task OpenAsync(TempDirectory directory)
    {
        await using var store = new SqliteSessionStore(directory.ConnectionString);
        await store.QueryAsync(new SessionQuery(), default);
    }

    /// <summary>A fresh current file turned back into the exact v4 shape: no v5-v9 objects and no triggers.</summary>
    private static async Task CreateAsVersion4Async(TempDirectory directory)
    {
        await OpenAsync(directory);
        var triggers = new List<string>();
        await using (var connection = new SqliteConnection(directory.ConnectionString.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                triggers.Add(reader.GetString(0));
            }
        }

        var sql = string.Concat(triggers.Select(static name => $"DROP TRIGGER {name};\n"))
            + "DROP INDEX IF EXISTS ux_sessions_occupancy;\n"
            + "DROP INDEX IF EXISTS ix_sessions_inherits;\n"
            + "DROP TABLE IF EXISTS storage_notices;\n"
            + string.Concat(LifecycleSessionColumns.Select(static column => $"ALTER TABLE sessions DROP COLUMN {column};\n"))
            + "ALTER TABLE projects DROP COLUMN closed_at;\n"
            + "PRAGMA user_version = 4;";
        await TodoSchemaRaw.ExecuteAsync(directory, sql);
        SqliteConnection.ClearAllPools();
        Assert.DoesNotContain("occupancy_key", await TodoSchemaRaw.ColumnsAsync(directory, "sessions"));
    }

    private static Task InsertSessionAsync(
        TempDirectory directory,
        string id,
        string workspace,
        string? worktree,
        string status,
        string? parent,
        long ticks) =>
        TodoSchemaRaw.ExecuteAsync(
            directory,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                INSERT INTO sessions (id, workspace_path, title, created_at, worktree_path, parent_session_id, status, last_active_at, last_active_ticks)
                VALUES ('{id}', '{workspace}', 't', '{Seed}', {Quote(worktree)}, {Quote(parent)}, '{status}', '{Seed}', {ticks});
                """));

    private static Task InsertMigratedAsync(TempDirectory directory, string id, string key, string status, string? parent) =>
        TodoSchemaRaw.ExecuteAsync(
            directory,
            $"""
            INSERT INTO sessions (id, workspace_path, title, created_at, parent_session_id, status, last_active_at, last_active_ticks, occupancy_key)
            VALUES ('{id}', 'ws', 't', '{Seed}', {Quote(parent)}, '{status}', '{Seed}', 0, '{key}');
            """);

    private static string Quote(string? value) => value is null ? "NULL" : $"'{value}'";

    private static Task<string> StatusAsync(TempDirectory directory, string id) =>
        TextAsync(directory, $"SELECT status FROM sessions WHERE id = '{id}'");

    private static Task<string> TextAsync(TempDirectory directory, string sql) =>
        TodoSchemaRaw.ScalarAsync<string>(directory, sql);
}
