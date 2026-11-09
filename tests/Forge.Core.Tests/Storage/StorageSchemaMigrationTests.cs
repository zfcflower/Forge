using System.Globalization;
using Forge.Abstractions;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>Pins the forge.db v0 → v1 migration ladder described in the storage contract.</summary>
public sealed class StorageSchemaMigrationTests
{
    /// <summary>The exact pre-versioning DDL (user_version 0): sessions/messages/summaries only.</summary>
    private const string V0SchemaSql = """
        CREATE TABLE sessions (
            id TEXT PRIMARY KEY,
            workspace_path TEXT NOT NULL,
            title TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        CREATE INDEX ix_sessions_workspace ON sessions (workspace_path, created_at DESC);
        CREATE TABLE messages (
            session_id TEXT NOT NULL,
            seq INTEGER NOT NULL,
            role TEXT NOT NULL,
            content_json TEXT NOT NULL,
            usage_json TEXT,
            at TEXT NOT NULL,
            PRIMARY KEY (session_id, seq)
        ) WITHOUT ROWID;
        CREATE TABLE summaries (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id TEXT NOT NULL,
            layer INTEGER NOT NULL,
            from_seq INTEGER NOT NULL,
            to_seq INTEGER NOT NULL,
            text TEXT NOT NULL,
            at TEXT NOT NULL
        );
        CREATE INDEX ix_summaries_session ON summaries (session_id, layer, from_seq);
        """;

    private const string SearchMessageJson =
        """{"role":"user","content":[{"type":"text","text":"the quick brown fox jumps"}]}""";

    [Fact]
    public async Task V0_database_migrates_to_v1_with_data_preserved()
    {
        using var directory = new TempDirectory();
        var createdAt = new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var createdAtText = createdAt.ToString("O", CultureInfo.InvariantCulture);
        await CreateV0DatabaseAsync(directory, "sess-v0", "D:/ws/old", "old title", createdAtText, SearchMessageJson);

        await using (var store = new SqliteSessionStore(directory.Path))
        {
            var loaded = await store.GetSessionAsync("sess-v0", default);
            Assert.NotNull(loaded);
            Assert.Equal("D:/ws/old", loaded.WorkspacePath);
            Assert.Equal("old title", loaded.Title);
            Assert.Equal(createdAt, loaded.CreatedAt);

            // Old sessions become Active with last activity backfilled to creation time.
            Assert.Equal(SessionStatus.Active, loaded.Status);
            Assert.Equal(loaded.CreatedAt, loaded.LastActiveAt);

            // Old messages stay readable and searchable after the migration.
            var stored = await StorageTestHelpers.CollectAsync(store.ReadAsync("sess-v0", 1, null, default));
            var message = Assert.Single(stored);
            Assert.Equal("the quick brown fox jumps", ((TextBlock)message.Message.Content[0]).Text);
            var hits = await store.SearchAsync("sess-v0", "quick brown", 10, default);
            Assert.NotEmpty(hits);
            Assert.Equal(1, hits[0].Seq);
        }

        Assert.Equal(4, await ReadUserVersionAsync(directory));
        foreach (var table in new[] { "projects", "todos", "ui_state", "turn_checkpoints" })
        {
            Assert.True(await TableExistsAsync(directory, table), $"table {table} should exist after migration");
        }

        Assert.True(
            await TableExistsAsync(directory, "messages_fts") || await TableExistsAsync(directory, "messages_text"),
            "a message search table should exist after migration");
    }

    [Fact]
    public async Task Migration_with_bad_timestamp_rolls_back_leaving_version_zero_and_rows_unchanged()
    {
        using var directory = new TempDirectory();
        await CreateV0DatabaseAsync(directory, "sess-bad", "D:/ws/old", "old title", "not-a-timestamp", SearchMessageJson);
        var rowsBefore = await ReadAllSessionsAsync(directory);

        await using (var store = new SqliteSessionStore(directory.Path))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => store.CreateSessionAsync("D:/ws", "new", default));
        }

        Assert.Equal(0, await ReadUserVersionAsync(directory));
        Assert.Equal(rowsBefore, await ReadAllSessionsAsync(directory));
        Assert.Equal(
            ["created_at", "id", "title", "workspace_path"],
            await ReadColumnNamesAsync(directory));
    }

    [Fact]
    public async Task Five_different_stores_opening_a_fresh_db_concurrently_migrate_once_and_all_writes_visible()
    {
        using var directory = new TempDirectory();
        await using var sessions = new SqliteSessionStore(directory.Path);
        await using var projects = new SqliteProjectRegistry(directory.Path);
        await using var todos = new SqliteTodoStore(directory.Path);
        await using var ui = new SqliteUiStateStore(directory.Path);
        await using var checkpoints = new SqliteTurnCheckpointStore(directory.Path);

        // Every store's first use runs the migration; they all race here on a fresh file.
        var sessionTask = sessions.CreateSessionAsync("D:/ws", "s", default);
        var projectTask = projects.AddAsync(directory.Combine("proj"), "P", ProjectKind.Repository, default);
        var todoTask = todos.AddAsync("sess-1", "T", null, null, default);
        var uiTask = ui.PutAsync("layout", 1, """{"panels":[]}""", default);
        var checkpointTask = checkpoints.BeginAsync("sess-1", 0, "D:/wt", new string('a', 40), DateTimeOffset.UtcNow, default);
        await Task.WhenAll(sessionTask, projectTask, todoTask, uiTask, checkpointTask)
            .WaitAsync(TimeSpan.FromSeconds(60));
        var session = await sessionTask;
        var project = await projectTask;
        await todoTask;
        await uiTask;
        await checkpointTask;

        Assert.Equal(4, await ReadUserVersionAsync(directory));

        Assert.Equal(session.Id, (await sessions.GetSessionAsync(session.Id, default))?.Id);
        Assert.Contains(await projects.ListAsync(default), p => p.Id == project.Id);
        Assert.Equal("T", Assert.Single(await todos.ListAsync("sess-1", default)).Title);
        Assert.Equal(1, (await ui.GetAsync("layout", default))?.Version);
        Assert.Equal(new string('a', 40), Assert.Single(await checkpoints.ListAsync("sess-1", default)).TreeBefore);
    }

    private static async Task CreateV0DatabaseAsync(
        TempDirectory directory,
        string sessionId,
        string workspacePath,
        string title,
        string createdAt,
        string? messageJson)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = directory.Combine("forge.db"),
            Pooling = false,
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = V0SchemaSql;
            await schema.ExecuteNonQueryAsync();
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO sessions (id, workspace_path, title, created_at) VALUES ($id, $ws, $title, $at)";
            insert.Parameters.AddWithValue("$id", sessionId);
            insert.Parameters.AddWithValue("$ws", workspacePath);
            insert.Parameters.AddWithValue("$title", title);
            insert.Parameters.AddWithValue("$at", createdAt);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }

        if (messageJson is not null)
        {
            await using var message = connection.CreateCommand();
            message.CommandText =
                "INSERT INTO messages (session_id, seq, role, content_json, usage_json, at) VALUES ($s, 1, 'user', $json, NULL, $at)";
            message.Parameters.AddWithValue("$s", sessionId);
            message.Parameters.AddWithValue("$json", messageJson);
            message.Parameters.AddWithValue("$at", createdAt);
            Assert.Equal(1, await message.ExecuteNonQueryAsync());
        }
    }

    private static async Task<long> ReadUserVersionAsync(TempDirectory directory)
    {
        await using var connection = new SqliteConnection(directory.ConnectionString.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<bool> TableExistsAsync(TempDirectory directory, string table)
    {
        await using var connection = new SqliteConnection(directory.ConnectionString.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        return (long)(await command.ExecuteScalarAsync() ?? 0L) > 0;
    }

    private static async Task<string[]> ReadAllSessionsAsync(TempDirectory directory)
    {
        await using var connection = new SqliteConnection(directory.ConnectionString.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, workspace_path, title, created_at FROM sessions ORDER BY id";
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join("|", reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return rows.ToArray();
    }

    private static async Task<string[]> ReadColumnNamesAsync(TempDirectory directory)
    {
        await using var connection = new SqliteConnection(directory.ConnectionString.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('sessions') ORDER BY name";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }
}
