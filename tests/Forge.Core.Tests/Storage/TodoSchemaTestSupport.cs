using System.Globalization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>
/// Raw SQLite access to <c>forge.db</c> for the step-commit tests: reading the schema as it really is on disk and
/// building the pre-v2 shapes that real users still have. Every connection is short-lived and unpooled, so nothing
/// here keeps the file locked.
/// </summary>
internal static class TodoSchemaRaw
{
    /// <summary>The seven columns of <c>todos</c> at schema version 1.</summary>
    public static readonly string[] V1TodoColumns =
        ["id", "session_id", "title", "status", "note", "order_index", "updated_at"];

    /// <summary>The fourteen columns of <c>todos</c> at the current schema (v4), in on-disk order.</summary>
    public static readonly string[] V2TodoColumns =
    [
        .. V1TodoColumns,
        "commit_subject",
        "summary",
        "verification",
        "commit_id",
        "commit_error",
        "group_id",
        "origin",
    ];

    public static async Task<long> UserVersionAsync(TempDirectory directory) =>
        await ScalarAsync<long>(directory, "PRAGMA user_version");

    public static async Task SetUserVersionAsync(TempDirectory directory, int version) =>
        await ExecuteAsync(directory, "PRAGMA user_version = " + version.ToString(CultureInfo.InvariantCulture));

    public static async Task ExecuteAsync(TempDirectory directory, string sql)
    {
        await using var connection = new SqliteConnection(directory.ConnectionString.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(TempDirectory directory, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(directory.ConnectionString.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);
    }

    public static async Task<string[]> ColumnsAsync(TempDirectory directory, string table)
    {
        await using var connection = new SqliteConnection(directory.ConnectionString.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table) ORDER BY cid";
        command.Parameters.AddWithValue("$table", table);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return [.. names];
    }

    public static async Task<bool> TableExistsAsync(TempDirectory directory, string table) =>
        await ScalarAsync<long>(
            directory,
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name",
            ("$name", table)) > 0;

    /// <summary>Every schema object (type, name, sql) in a stable order; equal snapshots mean nothing was altered.</summary>
    public static async Task<string[]> SchemaSnapshotAsync(TempDirectory directory)
    {
        await using var connection = new SqliteConnection(directory.ConnectionString.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, coalesce(sql, '') FROM sqlite_master ORDER BY type, name";
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join("|", reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return [.. rows];
    }

    /// <summary>The v1 columns of every todo row, as written, ordered by session and position.</summary>
    public static async Task<string[]> V1TodoRowsAsync(TempDirectory directory)
    {
        await using var connection = new SqliteConnection(directory.ConnectionString.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, session_id, title, status, coalesce(note, '<null>'), order_index, updated_at FROM todos ORDER BY session_id, order_index, id";
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join(
                "|",
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5).ToString(CultureInfo.InvariantCulture),
                reader.GetString(6)));
        }

        return [.. rows];
    }

    /// <summary>
    /// Turns a database written by this build back into the shape schema version 1 had: the v4 groups objects, the five
    /// step columns and the plan summary table are removed and <c>user_version</c> is 1. Callers must have disposed their
    /// stores first.
    /// </summary>
    public static async Task DowngradeToV1Async(TempDirectory directory)
    {
        SqliteConnection.ClearAllPools();
        await ExecuteAsync(
            directory,
            """
            DROP INDEX IF EXISTS ix_todos_group;
            ALTER TABLE todos DROP COLUMN group_id;
            ALTER TABLE todos DROP COLUMN origin;
            DROP TABLE IF EXISTS todo_groups;
            ALTER TABLE todos DROP COLUMN commit_subject;
            ALTER TABLE todos DROP COLUMN summary;
            ALTER TABLE todos DROP COLUMN verification;
            ALTER TABLE todos DROP COLUMN commit_id;
            ALTER TABLE todos DROP COLUMN commit_error;
            DROP TABLE plan_summaries;
            PRAGMA user_version = 1;
            """);
        SqliteConnection.ClearAllPools();

        // The downgrade must really produce the v1 shape, otherwise a migration test would pass vacuously.
        Assert.Equal(1L, await UserVersionAsync(directory));
        Assert.Equal(V1TodoColumns, await ColumnsAsync(directory, "todos"));
        Assert.False(await TableExistsAsync(directory, "plan_summaries"));
        Assert.False(await TableExistsAsync(directory, "todo_groups"));
    }
}
