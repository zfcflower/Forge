using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Storage;

/// <summary>
/// Ordered migrations for <c>forge.db</c>, recorded in <c>PRAGMA user_version</c>. Step 0 is the schema as it was
/// before versioning existed, so a database written by an earlier build is upgraded in place; each step runs in one
/// <c>BEGIN IMMEDIATE</c> transaction and the version is bumped inside it, so an interrupted upgrade leaves the
/// previous version behind. A file whose version is higher than this build knows is refused instead of downgraded.
/// </summary>
internal static class StorageSchema
{
    /// <summary>Schema version this build writes and reads.</summary>
    public const int Version = 1;

    public const string StatusActive = "Active";
    public const string StatusArchived = "Archived";
    public const string KindRepository = "Repository";
    public const string KindGeneral = "General";
    public const string TodoPending = "Pending";
    public const string TodoInProgress = "InProgress";
    public const string TodoDone = "Done";

    private const string BaseSchemaSql = """
        CREATE TABLE IF NOT EXISTS sessions (
            id TEXT PRIMARY KEY,
            workspace_path TEXT NOT NULL,
            title TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_sessions_workspace ON sessions (workspace_path, created_at DESC);

        CREATE TABLE IF NOT EXISTS messages (
            session_id TEXT NOT NULL,
            seq INTEGER NOT NULL,
            role TEXT NOT NULL,
            content_json TEXT NOT NULL,
            usage_json TEXT,
            at TEXT NOT NULL,
            PRIMARY KEY (session_id, seq)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS summaries (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id TEXT NOT NULL,
            layer INTEGER NOT NULL,
            from_seq INTEGER NOT NULL,
            to_seq INTEGER NOT NULL,
            text TEXT NOT NULL,
            at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_summaries_session ON summaries (session_id, layer, from_seq);
        """;

    private const string SessionColumnsSql = """
        ALTER TABLE sessions ADD COLUMN project_id TEXT;
        ALTER TABLE sessions ADD COLUMN worktree_path TEXT;
        ALTER TABLE sessions ADD COLUMN branch TEXT;
        ALTER TABLE sessions ADD COLUMN parent_session_id TEXT;
        ALTER TABLE sessions ADD COLUMN agent_kind TEXT;
        ALTER TABLE sessions ADD COLUMN task TEXT;
        ALTER TABLE sessions ADD COLUMN status TEXT NOT NULL DEFAULT 'Active';
        ALTER TABLE sessions ADD COLUMN last_active_at TEXT NOT NULL DEFAULT '';
        ALTER TABLE sessions ADD COLUMN last_active_ticks INTEGER NOT NULL DEFAULT 0;
        """;

    private const string V1SchemaSql = """
        CREATE INDEX IF NOT EXISTS ix_sessions_project ON sessions (project_id, last_active_ticks DESC);
        CREATE INDEX IF NOT EXISTS ix_sessions_worktree ON sessions (worktree_path, last_active_ticks DESC);
        CREATE INDEX IF NOT EXISTS ix_sessions_parent ON sessions (parent_session_id, last_active_ticks DESC);
        CREATE INDEX IF NOT EXISTS ix_sessions_status ON sessions (status, last_active_ticks DESC);
        CREATE INDEX IF NOT EXISTS ix_sessions_active ON sessions (last_active_ticks DESC);

        CREATE TABLE IF NOT EXISTS projects (
            id         TEXT PRIMARY KEY,
            name       TEXT NOT NULL,
            root_path  TEXT NOT NULL,
            root_key   TEXT NOT NULL,
            kind       TEXT NOT NULL,
            sort_order INTEGER NOT NULL,
            added_at   TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_projects_root_key ON projects (root_key);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_projects_general ON projects (kind) WHERE kind = 'General';
        CREATE INDEX IF NOT EXISTS ix_projects_order ON projects (sort_order, added_at);

        CREATE TABLE IF NOT EXISTS todos (
            id          TEXT PRIMARY KEY,
            session_id  TEXT NOT NULL,
            title       TEXT NOT NULL,
            status      TEXT NOT NULL,
            note        TEXT NULL,
            order_index INTEGER NOT NULL,
            updated_at  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_todos_session ON todos (session_id, order_index);

        CREATE TABLE IF NOT EXISTS ui_state (
            key        TEXT PRIMARY KEY,
            version    INTEGER NOT NULL,
            json       TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS turn_checkpoints (
            session_id    TEXT NOT NULL,
            turn          INTEGER NOT NULL,
            worktree_path TEXT NOT NULL,
            tree_before   TEXT NOT NULL,
            tree_after    TEXT NULL,
            started_at    TEXT NOT NULL,
            ended_at      TEXT NULL,
            PRIMARY KEY (session_id, turn)
        ) WITHOUT ROWID;
        """;

    private const string FtsSchemaSql =
        "CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(session_id UNINDEXED, seq UNINDEXED, text, tokenize='unicode61 remove_diacritics 2')";

    private const string FallbackSchemaSql =
        "CREATE TABLE IF NOT EXISTS messages_text (session_id TEXT NOT NULL, seq INTEGER NOT NULL, text TEXT NOT NULL, PRIMARY KEY (session_id, seq))";

    /// <summary>
    /// Brings the database to <see cref="Version"/> and makes sure the message search table exists. Returns true when
    /// FTS5 is available (searches then use it), false when the fallback table is used instead.
    /// </summary>
    public static async Task<bool> EnsureAsync(SqliteConnection connection, string databasePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var version = await ReadUserVersionAsync(connection, ct).ConfigureAwait(false);
        ThrowIfNewer(version, databasePath);
        while (version < Version)
        {
            version = await MigrateNextAsync(connection, databasePath, ct).ConfigureAwait(false);
        }

        return await EnsureSearchTableAsync(connection, ct).ConfigureAwait(false);
    }

    public static void ThrowIfNewer(int version, string databasePath)
    {
        if (version > Version)
        {
            throw new InvalidDataException(
                $"Database '{databasePath}' uses schema {version.ToString(CultureInfo.InvariantCulture)}; this version of Forge supports {Version.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    public static int ReadUserVersion(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        var value = command.ExecuteScalar();
        return (int)(value is long version ? version : Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    private static async Task<int> ReadUserVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return (int)(value is long version ? version : Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version = " + version.ToString(CultureInfo.InvariantCulture);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Applies exactly one pending step. The version is re-read inside the write transaction, so two processes
    /// opening the same new file cannot apply the same <c>ALTER TABLE</c> twice.
    /// </summary>
    private static async Task<int> MigrateNextAsync(SqliteConnection connection, string databasePath, CancellationToken ct)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadUserVersion(connection, transaction);
        ThrowIfNewer(current, databasePath);
        if (current >= Version)
        {
            transaction.Rollback();
            return current;
        }

        switch (current)
        {
            case 0:
                await MigrateToV1Async(connection, transaction, databasePath, ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"No migration from schema {current.ToString(CultureInfo.InvariantCulture)}.");
        }

        var next = current + 1;
        SetUserVersion(connection, transaction, next);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return next;
    }

    private static async Task MigrateToV1Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string databasePath,
        CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction, BaseSchemaSql, ct).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, SessionColumnsSql, ct).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, V1SchemaSql, ct).ConfigureAwait(false);
        await BackfillActivityAsync(connection, transaction, databasePath, ct).ConfigureAwait(false);
    }

    /// <summary>Existing sessions become active and their last activity is the moment they were created.</summary>
    private static async Task BackfillActivityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string databasePath,
        CancellationToken ct)
    {
        var rows = new List<(string Id, string CreatedAt)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id, created_at FROM sessions WHERE last_active_ticks = 0";
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (id, createdAt) in rows)
        {
            var at = StorageDb.ParseCanonicalTimestamp(createdAt, "created_at", databasePath, id);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE sessions SET status = $status, last_active_at = $at, last_active_ticks = $ticks WHERE id = $id";
            update.Parameters.AddWithValue("$status", StatusActive);
            update.Parameters.AddWithValue("$at", StorageDb.FormatTimestamp(at));
            update.Parameters.AddWithValue("$ticks", StorageDb.TimestampTicks(at));
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Makes sure a search table exists and is populated. The table is created and backfilled in one transaction, so an
    /// interrupted first run cannot leave an empty index behind: messages stored before this schema existed (or before
    /// the SQLite build had FTS5) stay findable.
    /// </summary>
    private static async Task<bool> EnsureSearchTableAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (await SearchTableExistsAsync(connection, "messages_fts", null, ct).ConfigureAwait(false))
        {
            return await CanReadFtsAsync(connection, ct).ConfigureAwait(false);
        }

        if (await SearchTableExistsAsync(connection, "messages_text", null, ct).ConfigureAwait(false))
        {
            return false;
        }

        using var transaction = connection.BeginTransaction(deferred: false);
        if (await SearchTableExistsAsync(connection, "messages_fts", transaction, ct).ConfigureAwait(false))
        {
            transaction.Rollback();
            return true;
        }

        if (await SearchTableExistsAsync(connection, "messages_text", transaction, ct).ConfigureAwait(false))
        {
            transaction.Rollback();
            return false;
        }

        var fts5 = true;
        await using (var search = connection.CreateCommand())
        {
            search.Transaction = transaction;
            search.CommandText = FtsSchemaSql;
            try
            {
                await search.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                fts5 = false;
                search.CommandText = FallbackSchemaSql;
                await search.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await BackfillSearchAsync(connection, transaction, fts5, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return fts5;
    }

    /// <summary>
    /// Prepares a statement against the FTS table. The module is resolved while preparing, so this reports whether this
    /// SQLite build can use a table an earlier build created, without touching the rows.
    /// </summary>
    private static async Task<bool> CanReadFtsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid FROM messages_fts LIMIT 0";
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task<bool> SearchTableExistsAsync(
        SqliteConnection connection,
        string name,
        SqliteTransaction? transaction,
        CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return (value is long count ? count : Convert.ToInt64(value, CultureInfo.InvariantCulture)) > 0;
    }

    private static async Task BackfillSearchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        bool fts5,
        CancellationToken ct)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT session_id, seq, content_json FROM messages";
        await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string text;
            try
            {
                text = MessageJson.ExtractSearchText(MessageJson.Deserialize(reader.GetString(2)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                continue;
            }

            if (text.Length == 0)
            {
                continue;
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = fts5
                ? "INSERT INTO messages_fts (session_id, seq, text) VALUES ($s, $q, $t)"
                : "INSERT OR REPLACE INTO messages_text (session_id, seq, text) VALUES ($s, $q, $t)";
            insert.Parameters.AddWithValue("$s", reader.GetString(0));
            insert.Parameters.AddWithValue("$q", reader.GetInt64(1));
            insert.Parameters.AddWithValue("$t", text);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
