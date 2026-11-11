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
    public const int Version = 9;

    public const string StatusActive = "Active";
    public const string StatusArchived = "Archived";
    public const string StatusDeleted = "Deleted";
    public const string OriginCold = "cold";
    public const string OriginFull = "full";
    public const string OriginSummary = "summary";
    public const string OriginBlank = "blank";
    public const string OriginResume = "resume";
    public const string KindRepository = "Repository";
    public const string KindGeneral = "General";
    public const string TodoPending = "Pending";
    public const string TodoInProgress = "InProgress";
    public const string TodoPaused = "Paused";
    public const string TodoDone = "Done";
    public const string TodoOriginAgent = "agent";
    public const string TodoOriginUser = "user";

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

    private const string V2SchemaSql = """
        ALTER TABLE todos ADD COLUMN commit_subject TEXT NULL;
        ALTER TABLE todos ADD COLUMN summary TEXT NULL;
        ALTER TABLE todos ADD COLUMN verification TEXT NULL;
        ALTER TABLE todos ADD COLUMN commit_id TEXT NULL;
        ALTER TABLE todos ADD COLUMN commit_error TEXT NULL;

        CREATE TABLE IF NOT EXISTS plan_summaries (
            session_id TEXT PRIMARY KEY,
            text       TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        """;

    private const string V4SchemaSql = """
        CREATE TABLE IF NOT EXISTS todo_groups (
            id          TEXT PRIMARY KEY,
            session_id  TEXT NOT NULL,
            title       TEXT NOT NULL,
            order_index INTEGER NOT NULL,
            updated_at  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_todo_groups_session ON todo_groups (session_id, order_index);

        ALTER TABLE todos ADD COLUMN group_id TEXT NULL;
        ALTER TABLE todos ADD COLUMN origin TEXT NOT NULL DEFAULT 'agent';
        CREATE INDEX IF NOT EXISTS ix_todos_group ON todos (session_id, group_id, order_index);
        """;

    /// <summary>
    /// Every timestamp column of <c>forge.db</c>, with the columns that identify its row. The v3 step repairs stored
    /// values against this list and builds the write guards from it, so a column added to the schema later has one
    /// place to register.
    /// </summary>
    private static readonly (string Table, string[] Key, string[] Columns)[] TimestampColumns =
    [
        ("sessions", ["id"], ["created_at", "last_active_at", "deleted_at"]),
        ("messages", ["session_id", "seq"], ["at"]),
        ("summaries", ["id"], ["at"]),
        ("projects", ["id"], ["added_at", "closed_at"]),
        ("todos", ["id"], ["updated_at"]),
        ("ui_state", ["key"], ["updated_at"]),
        ("turn_checkpoints", ["session_id", "turn"], ["started_at", "ended_at"]),
        ("plan_summaries", ["session_id"], ["updated_at"]),
        ("todo_groups", ["id"], ["updated_at"]),
        ("storage_notices", ["id"], ["created_at", "acknowledged_at"]),
    ];

    private const string StorageNoticesSql = """
        CREATE TABLE IF NOT EXISTS storage_notices (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            kind            TEXT NOT NULL,
            payload_json    TEXT NOT NULL,
            created_at      TEXT NOT NULL,
            acknowledged_at TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_storage_notices_pending ON storage_notices (id) WHERE acknowledged_at IS NULL;
        """;

    private const string OccupancyIndexSql =
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_sessions_occupancy ON sessions (occupancy_key) " +
        "WHERE status = 'Active' AND parent_session_id IS NULL";

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
            case 1:
                await ExecuteAsync(connection, transaction, V2SchemaSql, ct).ConfigureAwait(false);
                break;
            case 2:
                await MigrateToV3Async(connection, transaction, ct).ConfigureAwait(false);
                break;
            case 3:
                await MigrateToV4Async(connection, transaction, ct).ConfigureAwait(false);
                break;
            case 4:
                await MigrateToV5Async(connection, transaction, ct).ConfigureAwait(false);
                break;
            case 5:
                await MigrateToV6Async(connection, transaction, ct).ConfigureAwait(false);
                break;
            case 6:
                await MigrateToV7Async(connection, transaction, ct).ConfigureAwait(false);
                break;
            case 7:
                await MigrateToV8Async(connection, transaction, ct).ConfigureAwait(false);
                break;
            case 8:
                await MigrateToV9Async(connection, transaction, ct).ConfigureAwait(false);
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
    /// The v3 step: repair timestamps a writer outside Forge stored in another legal spelling, then make the database
    /// itself refuse that spelling. The repair runs first, because the guards it installs afterwards would reject the
    /// very history being repaired; both halves share the step's transaction, so an interrupted upgrade leaves the
    /// data and the trigger set exactly as they were. Only the tables that exist at this point are touched: a table a
    /// later step adds (the v4 <c>todo_groups</c>) is normalized and guarded by the step that creates it.
    /// </summary>
    private static async Task MigrateToV3Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        var tables = await ExistingColumnsAsync(connection, transaction, ct).ConfigureAwait(false);
        foreach (var (table, key, columns) in TimestampColumns)
        {
            if (!tables.TryGetValue(table, out var existing))
            {
                continue;
            }

            foreach (var column in columns)
            {
                if (existing.Contains(column))
                {
                    await NormalizeTimestampColumnAsync(connection, transaction, table, key, column, ct).ConfigureAwait(false);
                }
            }
        }

        await ExecuteAsync(connection, transaction, TimestampGuardSql(tables), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The v4 step: named plans (<c>todo_groups</c>) and the two columns that place a todo item in one - <c>group_id</c>
    /// (null for a loose item of the session's queue) and its <c>origin</c>. Existing rows need no rewrite: their
    /// <c>group_id</c> stays null, which is exactly the loose queue, and their <c>order_index</c> already is the queue
    /// order. The new timestamp column is registered in <see cref="TimestampColumns"/>, so the guards are installed once
    /// more - every pair is dropped and recreated from that list, which is why the step is idempotent for v3's triggers.
    /// </summary>
    private static async Task MigrateToV4Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction, V4SchemaSql, ct).ConfigureAwait(false);
        await InstallTimestampGuardsAsync(connection, transaction, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The v5 step, occupancy: <c>sessions.occupancy_key</c> is computed in C# for every row (the same
    /// <see cref="OccupancyKey.Compute"/> the runtime writes use, never a SQL expression), duplicate Active root sessions
    /// of one key are reduced to the most recently active one (tie: the larger id, ordinal) by archiving the others -
    /// only their status changes - and then the unique partial index is created. The archived ids are recorded in
    /// <c>storage_notices</c> so the app can tell the user. Every part is re-runnable: the column is added only when
    /// missing, the key is recomputed, a second pass finds no duplicates and writes no notice.
    /// </summary>
    private static async Task MigrateToV5Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await AddColumnIfMissingAsync(connection, transaction, "sessions", "occupancy_key", "TEXT", ct).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, StorageNoticesSql, ct).ConfigureAwait(false);

        var rows = new List<(string Id, string Key)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id, worktree_path, workspace_path FROM sessions";
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var worktree = reader.IsDBNull(1) ? null : reader.GetString(1);
                var workspace = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                rows.Add((reader.GetString(0), ComputeKeyOrRaw(worktree, workspace)));
            }
        }

        foreach (var (id, key) in rows)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE sessions SET occupancy_key = $key WHERE id = $id";
            update.Parameters.AddWithValue("$key", key);
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var active = new List<(string Id, string Key, long Ticks)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                "SELECT id, occupancy_key, last_active_ticks FROM sessions WHERE status = 'Active' AND parent_session_id IS NULL";
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                active.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }

        var archived = new List<string>();
        foreach (var group in active.GroupBy(static row => row.Key, StringComparer.Ordinal))
        {
            var keep = group
                .OrderByDescending(static row => row.Ticks)
                .ThenByDescending(static row => row.Id, StringComparer.Ordinal)
                .First();
            foreach (var row in group)
            {
                if (!string.Equals(row.Id, keep.Id, StringComparison.Ordinal))
                {
                    archived.Add(row.Id);
                }
            }
        }

        archived.Sort(StringComparer.Ordinal);
        foreach (var id in archived)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE sessions SET status = 'Archived' WHERE id = $id";
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction, OccupancyIndexSql, ct).ConfigureAwait(false);
        if (archived.Count > 0)
        {
            await InsertNoticeAsync(connection, transaction, Forge.Abstractions.StorageNoticeKinds.DedupeArchived, archived, ct).ConfigureAwait(false);
        }

        await InstallTimestampGuardsAsync(connection, transaction, ct).ConfigureAwait(false);
    }

    /// <summary>The v6 step, soft delete: <c>sessions.deleted_at</c>. No backfill.</summary>
    private static async Task MigrateToV6Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await AddColumnIfMissingAsync(connection, transaction, "sessions", "deleted_at", "TEXT NULL", ct).ConfigureAwait(false);
        await InstallTimestampGuardsAsync(connection, transaction, ct).ConfigureAwait(false);
    }

    /// <summary>The v7 step, soft close of projects: <c>projects.closed_at</c>. No backfill.</summary>
    private static async Task MigrateToV7Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await AddColumnIfMissingAsync(connection, transaction, "projects", "closed_at", "TEXT NULL", ct).ConfigureAwait(false);
        await InstallTimestampGuardsAsync(connection, transaction, ct).ConfigureAwait(false);
    }

    /// <summary>The v8 step, parallel forks: <c>fork_of_branch</c> and <c>fork_base_sha</c>. No backfill.</summary>
    private static async Task MigrateToV8Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await AddColumnIfMissingAsync(connection, transaction, "sessions", "fork_of_branch", "TEXT NULL", ct).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "sessions", "fork_base_sha", "TEXT NULL", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The v9 step, inheritance and prompt-cache bookkeeping. Every existing session is its own lineage root and began
    /// cold.
    /// </summary>
    private static async Task MigrateToV9Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await AddColumnIfMissingAsync(connection, transaction, "sessions", "inherits_from_session_id", "TEXT NULL", ct).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "sessions", "inherits_up_to_seq", "INTEGER NULL", ct).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "sessions", "lineage_root_id", "TEXT NULL", ct).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "sessions", "origin_kind", "TEXT NULL", ct).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "sessions", "prefix_fingerprint", "TEXT NULL", ct).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE sessions SET lineage_root_id = id WHERE lineage_root_id IS NULL;
            UPDATE sessions SET origin_kind = 'cold' WHERE origin_kind IS NULL;
            CREATE INDEX IF NOT EXISTS ix_sessions_inherits ON sessions (inherits_from_session_id)
                WHERE inherits_from_session_id IS NOT NULL;
            """,
            ct).ConfigureAwait(false);
    }

    /// <summary>The key of a stored row; a path <see cref="Path.GetFullPath(string)"/> refuses keeps its raw text so the step never fails on it.</summary>
    private static string ComputeKeyOrRaw(string? worktreePath, string workspacePath)
    {
        try
        {
            return OccupancyKey.Compute(worktreePath, workspacePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.IsNullOrWhiteSpace(worktreePath) ? workspacePath : worktreePath;
        }
    }

    /// <summary>Writes one <c>storage_notices</c> row whose payload is <c>{"sessionIds":[...]}</c>.</summary>
    internal static async Task InsertNoticeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string kind,
        IReadOnlyList<string> sessionIds,
        CancellationToken ct)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO storage_notices (kind, payload_json, created_at) VALUES ($kind, $payload, $at)";
        insert.Parameters.AddWithValue("$kind", kind);
        insert.Parameters.AddWithValue("$payload", NoticePayload(sessionIds));
        insert.Parameters.AddWithValue("$at", StorageDb.FormatTimestamp(DateTimeOffset.UtcNow));
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary><c>{"sessionIds":[...]}</c>, written without reflection.</summary>
    internal static string NoticePayload(IReadOnlyList<string> sessionIds)
    {
        using var buffer = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("sessionIds");
            foreach (var id in sessionIds)
            {
                writer.WriteStringValue(id);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Parses <see cref="NoticePayload"/>; an unreadable payload yields an empty list rather than failing the read.</summary>
    internal static IReadOnlyList<string> ParseNoticePayload(string payload)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("sessionIds", out var ids) ||
                ids.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<string>(ids.GetArrayLength());
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind == System.Text.Json.JsonValueKind.String && id.GetString() is { } value)
                {
                    list.Add(value);
                }
            }

            return list;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string definition,
        CancellationToken ct)
    {
        var columns = await ExistingColumnsAsync(connection, transaction, ct).ConfigureAwait(false);
        if (columns.TryGetValue(table, out var existing) && existing.Contains(column))
        {
            return;
        }

        await ExecuteAsync(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {definition}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Rewrites the rows of one timestamp column whose text is a legal instant but not the shape this codebase reads.
    /// Rows whose text names no unambiguous instant are left exactly as they are: the strict read reports them with
    /// their row id, and inventing a value here would hide the only evidence of what went wrong.
    /// </summary>
    private static async Task NormalizeTimestampColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string[] key,
        string column,
        CancellationToken ct)
    {
        var stale = new List<(object[] Key, string Value)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                $"SELECT {string.Join(", ", key)}, {column} FROM {table} " +
                $"WHERE {column} IS NOT NULL AND NOT ({column} GLOB $pattern)";
            select.Parameters.AddWithValue("$pattern", StorageDb.TimestampGlobPattern);
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.GetValue(key.Length) is not string text || StorageDb.TryNormalizeTimestamp(text) is not { } normalized)
                {
                    continue;
                }

                var values = new object[key.Length];
                for (var index = 0; index < key.Length; index++)
                {
                    values[index] = reader.GetValue(index);
                }

                stale.Add((values, normalized));
            }
        }

        var predicate = string.Join(" AND ", key.Select(static name => $"{name} = ${name}"));
        foreach (var (values, value) in stale)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"UPDATE {table} SET {column} = $value WHERE {predicate}";
            update.Parameters.AddWithValue("$value", value);
            for (var index = 0; index < key.Length; index++)
            {
                update.Parameters.AddWithValue("$" + key[index], values[index]);
            }

            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Installs the guards of every table that exists at this point in the ladder. A table registered in
    /// <see cref="TimestampColumns"/> is only guarded once the step that creates it has run: the v3 step precedes the
    /// <c>todo_groups</c> table, the v4 step follows it and installs the whole set again (dropping a trigger and creating
    /// it again is idempotent).
    /// </summary>
    private static async Task InstallTimestampGuardsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        var tables = await ExistingColumnsAsync(connection, transaction, ct).ConfigureAwait(false);
        var sql = TimestampGuardSql(tables);
        if (sql.Length > 0)
        {
            await ExecuteAsync(connection, transaction, sql, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Existing tables with their column names; a column a later step adds is absent until that step ran.</summary>
    private static async Task<Dictionary<string, HashSet<string>>> ExistingColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        var tables = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                tables[reader.GetString(0)] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        foreach (var (table, columns) in tables)
        {
            await using var info = connection.CreateCommand();
            info.Transaction = transaction;
            info.CommandText = "SELECT name FROM pragma_table_info($table)";
            info.Parameters.AddWithValue("$table", table);
            await using var reader = await info.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(0));
            }
        }

        return tables;
    }

    /// <summary>
    /// One <c>BEFORE INSERT</c> and one <c>BEFORE UPDATE</c> trigger per timestamp column of <paramref name="tables"/>,
    /// each aborting any write whose text does not match <see cref="StorageDb.TimestampGlobPattern"/>. The database
    /// enforces the shape the readers require, so neither a newer build nor a script that opens <c>forge.db</c> on its
    /// own can store a value every later read would refuse; a nullable column still accepts NULL.
    /// </summary>
    private static string TimestampGuardSql(IReadOnlyDictionary<string, HashSet<string>> tables)
    {
        var sql = new System.Text.StringBuilder();
        foreach (var (table, _, columns) in TimestampColumns)
        {
            if (!tables.TryGetValue(table, out var existing))
            {
                continue;
            }

            foreach (var column in columns)
            {
                if (!existing.Contains(column))
                {
                    continue;
                }

                var condition =
                    $"NEW.{column} IS NOT NULL AND NOT (NEW.{column} GLOB '{StorageDb.TimestampGlobPattern}')";
                var message = $"{table}.{column} must hold the canonical forge.db timestamp (yyyy-MM-ddTHH:mm:ss.fffffff+HH:mm)";
                foreach (var (kind, suffix) in new[] { ("INSERT", "insert"), ("UPDATE", "update") })
                {
                    var name = $"trg_{table}_{column}_{suffix}";
                    sql.Append($"DROP TRIGGER IF EXISTS {name};\n");
                    sql.Append($"CREATE TRIGGER {name} BEFORE {kind} ON {table} FOR EACH ROW WHEN {condition}\n");
                    sql.Append($"BEGIN SELECT RAISE(ABORT, '{message}'); END;\n");
                }
            }
        }

        return sql.ToString();
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
