using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Storage;

/// <summary>
/// Session / worktree lifecycle of <see cref="SqliteSessionStore"/> (TASK-session-worktree-lifecycle, 4.1-4.4, 7.5-7.8).
/// Occupancy: at most one Active root session per <c>occupancy_key</c>, enforced by the unique partial index
/// <c>ux_sessions_occupancy</c> and, before it, by conditional statements. Every op that can change occupancy or that
/// refuses on a business rule runs under its own savepoint inside the writer batch, so its failure never fails the
/// unrelated writes it was batched with (I1).
/// </summary>
public sealed partial class SqliteSessionStore
{
    /// <summary>Holder of a key: the Active root session that occupies it.</summary>
    private const string HolderSql =
        "SELECT id FROM sessions WHERE occupancy_key = $key AND status = 'Active' AND parent_session_id IS NULL LIMIT 1";

    /// <summary>Same predicate as the unique index, as a guard on a conditional statement.</summary>
    private const string KeyFreeSql =
        "NOT EXISTS (SELECT 1 FROM sessions o WHERE o.occupancy_key = $key AND o.status = 'Active' AND o.parent_session_id IS NULL)";

    /// <summary>The rows of <c>$ids</c> (a JSON array of session ids) and all their descendants, through <c>parent_session_id</c>.</summary>
    private const string TreeCte = """
        WITH RECURSIVE tree(id) AS (
            SELECT value FROM json_each($ids)
            UNION
            SELECT s.id FROM sessions s JOIN tree t ON s.parent_session_id = t.id)
        """;

    /// <summary>Descendants of <c>$id</c> only (not the row itself).</summary>
    private const string DescendantsCte = """
        WITH RECURSIVE tree(id) AS (
            SELECT id FROM sessions WHERE parent_session_id = $id
            UNION
            SELECT s.id FROM sessions s JOIN tree t ON s.parent_session_id = t.id)
        """;

    /// <inheritdoc />
    public Task<bool> ActivateAsync(string sessionId, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        return EnqueueAsync(new ActivateOp(sessionId, ct), ct);
    }

    /// <inheritdoc />
    public async Task<SessionInfo> HandoverAsync(string oldSessionId, HandoverTarget target, CancellationToken ct)
    {
        StoragePaths.SafeSegment(oldSessionId, nameof(oldSessionId));
        ArgumentNullException.ThrowIfNull(target);
        switch (target)
        {
            case HandoverTarget.NewSession create:
                ArgumentNullException.ThrowIfNull(create.InitialMessages, nameof(target));
                foreach (var message in create.InitialMessages)
                {
                    ArgumentNullException.ThrowIfNull(message, nameof(target));
                }

                var (session, key) = PrepareCreate(create.Create);
                return await EnqueueAsync(new HandoverOp(this, oldSessionId, session, key, create, null, ct), ct).ConfigureAwait(false);
            case HandoverTarget.Resume resume:
                StoragePaths.SafeSegment(resume.SessionId, nameof(target));
                if (string.Equals(resume.SessionId, oldSessionId, StringComparison.Ordinal))
                {
                    throw new ArgumentException("A session cannot be handed over to itself.", nameof(target));
                }

                return await EnqueueAsync(new HandoverOp(this, oldSessionId, null, null, null, resume.SessionId, ct), ct).ConfigureAwait(false);
            default:
                throw new ArgumentException($"Unknown handover target {target.GetType().Name}.", nameof(target));
        }
    }

    /// <inheritdoc />
    public Task<int> SoftDeleteAsync(IReadOnlyList<string> sessionIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        foreach (var id in sessionIds)
        {
            StoragePaths.SafeSegment(id, nameof(sessionIds));
        }

        return sessionIds.Count == 0
            ? Task.FromResult(0)
            : EnqueueAsync(new SoftDeleteOp(JsonArray(sessionIds), ct), ct);
    }

    /// <inheritdoc />
    /// <remarks>An id with no row purges nothing and returns an empty list.</remarks>
    public Task<IReadOnlyList<string>> PurgeAsync(string sessionId, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        return EnqueueAsync(new PurgeOp(this, sessionId, ct), ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionInfo>> ListHistoryAsync(SessionQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return QuerySessionsAsync(query, "status IN ('Archived', 'Deleted')", ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The directory probes run on the caller's thread before the write, never inside the writer transaction; the write
    /// re-checks that each candidate is still an Active / Archived root, so a session resumed or removed in between is
    /// left alone. A root whose stored <c>worktree_path</c> is blank falls back to <c>workspace_path</c>, like the key.
    /// </remarks>
    public async Task<IReadOnlyList<string>> HealMissingDirectoriesAsync(
        Func<string, bool> directoryExists,
        Func<string, bool> projectRootExists,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(projectRootExists);
        await EnsureInitializedAsync().ConfigureAwait(false);
        var rows = new List<(string Id, string Directory, string ProjectRoot)>();
        await using (var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.id, COALESCE(NULLIF(TRIM(s.worktree_path), ''), s.workspace_path), p.root_path
                FROM sessions s JOIN projects p ON p.id = s.project_id
                WHERE s.parent_session_id IS NULL AND s.status IN ('Active', 'Archived')
                ORDER BY s.id
                """;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var roots = new Dictionary<string, bool>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var (id, directory, projectRoot) in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (!roots.TryGetValue(projectRoot, out var rootExists))
            {
                rootExists = projectRootExists(projectRoot);
                roots[projectRoot] = rootExists;
            }

            if (rootExists && !directoryExists(directory))
            {
                missing.Add(id);
            }
        }

        return missing.Count == 0
            ? []
            : await EnqueueAsync(new HealOp(JsonArray(missing), ct), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> SetPrefixFingerprintAsync(string sessionId, string fingerprint, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        return EnqueueAsync(new SetFingerprintOp(sessionId, fingerprint, ct), ct);
    }

    /// <inheritdoc />
    public Task<int> RenameBranchAsync(string projectId, string worktreePath, string oldBranch, string newBranch, CancellationToken ct)
    {
        StoragePaths.SafeSegment(projectId, nameof(projectId));
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(newBranch);
        var key = OccupancyKey.Compute(worktreePath, worktreePath);
        return EnqueueAsync(new RenameBranchOp(projectId, key, oldBranch, newBranch, ct), ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StorageNotice>> ReadNoticesAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, kind, payload_json, created_at FROM storage_notices WHERE acknowledged_at IS NULL ORDER BY id";
        var notices = new List<StorageNotice>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var row = $"storage notice {id}";
            var payload = StorageDb.ReadText(reader, 2, "payload_json", _databasePath, row);
            notices.Add(new StorageNotice(
                id,
                StorageDb.ReadText(reader, 1, "kind", _databasePath, row),
                StorageSchema.ParseNoticePayload(payload),
                payload,
                StorageDb.ParseCanonicalTimestamp(
                    StorageDb.ReadText(reader, 3, "created_at", _databasePath, row),
                    "created_at",
                    _databasePath,
                    row)));
        }

        return notices;
    }

    /// <inheritdoc />
    public Task AcknowledgeNoticesAsync(IReadOnlyList<long> noticeIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(noticeIds);
        return noticeIds.Count == 0
            ? Task.CompletedTask
            : EnqueueAsync(new AcknowledgeNoticesOp(JsonArray(noticeIds), ct), ct);
    }

    private static (SessionInfo Session, string Key) PrepareCreate(SessionCreate request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspacePath);
        ArgumentNullException.ThrowIfNull(request.Title);
        if (request.ParentSessionId is { } parent)
        {
            StoragePaths.SafeSegment(parent, nameof(request));
        }

        _ = OriginName(request.Origin);
        var key = OccupancyKey.Compute(request.WorktreePath, request.WorkspacePath);
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("n");
        var session = new SessionInfo(id, request.WorkspacePath, request.Title, now)
        {
            ProjectId = request.ProjectId,
            WorktreePath = request.WorktreePath,
            Branch = request.Branch,
            ParentSessionId = request.ParentSessionId,
            AgentKind = request.AgentKind,
            Task = request.Task,
            Status = SessionStatus.Active,
            LastActiveAt = now,
            ForkOfBranch = request.ForkOfBranch,
            ForkBaseSha = request.ForkBaseSha,
            InheritsFromSessionId = request.InheritsFromSessionId,
            InheritsUpToSeq = request.InheritsUpToSeq,
            LineageRootId = request.LineageRootId ?? id,
            Origin = request.Origin,
            AutomationId = request.AutomationId,
            AutomationRunId = request.AutomationRunId,
        };
        return (session, key);
    }

    internal static string OriginName(SessionOrigin origin) => origin switch
    {
        SessionOrigin.Cold => StorageSchema.OriginCold,
        SessionOrigin.Full => StorageSchema.OriginFull,
        SessionOrigin.Summary => StorageSchema.OriginSummary,
        SessionOrigin.Blank => StorageSchema.OriginBlank,
        SessionOrigin.Resume => StorageSchema.OriginResume,
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown session origin."),
    };

    /// <summary>NULL reads as <see cref="SessionOrigin.Cold"/> (the v9 backfill value); unknown text is malformed.</summary>
    private static SessionOrigin ParseOrigin(string? value, string databasePath, string row) => value switch
    {
        null or StorageSchema.OriginCold => SessionOrigin.Cold,
        StorageSchema.OriginFull => SessionOrigin.Full,
        StorageSchema.OriginSummary => SessionOrigin.Summary,
        StorageSchema.OriginBlank => SessionOrigin.Blank,
        StorageSchema.OriginResume => SessionOrigin.Resume,
        _ => throw StorageDb.Malformed(databasePath, "origin_kind", row, $"'{value}' is not one of cold, full, summary, blank, resume", null),
    };

    private static string JsonArray(IReadOnlyList<string> values)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string JsonArray(IReadOnlyList<long> values)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var value in values)
            {
                writer.WriteNumberValue(value);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql) =>
        StorageDb.Command(connection, transaction, sql);

    private static async Task<string?> FindHolderAsync(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        await using var command = Command(connection, transaction, HolderSql);
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false) as string;
    }

    /// <summary>
    /// Runs a conditional occupancy statement. Zero rows (the guard saw a holder) and the unique-index backstop
    /// (<c>SQLITE_CONSTRAINT_UNIQUE</c> on <c>occupancy_key</c>) both become <see cref="WorktreeOccupiedException"/>.
    /// </summary>
    private static async Task<int> ExecuteOccupyingAsync(
        SqliteCommand command,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        bool zeroRowsMeansOccupied)
    {
        int changed;
        try
        {
            changed = await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsOccupancyViolation(exception))
        {
            throw await OccupiedAsync(connection, transaction, key, exception).ConfigureAwait(false);
        }

        if (changed == 0 && zeroRowsMeansOccupied && await FindHolderAsync(connection, transaction, key).ConfigureAwait(false) is { } holder)
        {
            throw new WorktreeOccupiedException(holder);
        }

        return changed;
    }

    private static bool IsOccupancyViolation(SqliteException exception) =>
        exception.SqliteExtendedErrorCode == SqliteConstraintUnique &&
        exception.Message.Contains("occupancy_key", StringComparison.Ordinal);

    private static async Task<Exception> OccupiedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        SqliteException exception) =>
        await FindHolderAsync(connection, transaction, key).ConfigureAwait(false) is { } holder
            ? new WorktreeOccupiedException(holder)
            : new InvalidOperationException("The workspace occupancy index refused the write.", exception);

    private static void BindNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    /// <summary>
    /// Inserts <paramref name="session"/>. A root row is conditional on the key being free (0 rows → occupied); a child
    /// row is never constrained.
    /// </summary>
    private static async Task InsertSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionInfo session,
        string key)
    {
        var root = session.ParentSessionId is null;
        var sql = $"""
            INSERT INTO sessions ({InsertColumns})
            SELECT $id, $ws, $title, $at, $project, $worktree, $branch, $parent, $kind, $task, 'Active', $active, $ticks, $key,
                   $fork, $forkSha, $inherits, $inheritsSeq, $lineage, $origin, $automation, $automationRun
            {(root ? "WHERE " + KeyFreeSql : string.Empty)}
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$ws", session.WorkspacePath);
        command.Parameters.AddWithValue("$title", session.Title);
        command.Parameters.AddWithValue("$at", StorageDb.FormatTimestamp(session.CreatedAt));
        BindNullable(command, "$project", session.ProjectId);
        BindNullable(command, "$worktree", session.WorktreePath);
        BindNullable(command, "$branch", session.Branch);
        BindNullable(command, "$parent", session.ParentSessionId);
        BindNullable(command, "$kind", session.AgentKind);
        BindNullable(command, "$task", session.Task);
        command.Parameters.AddWithValue("$active", StorageDb.FormatTimestamp(session.LastActiveAt));
        command.Parameters.AddWithValue("$ticks", StorageDb.TimestampTicks(session.LastActiveAt));
        command.Parameters.AddWithValue("$key", key);
        BindNullable(command, "$fork", session.ForkOfBranch);
        BindNullable(command, "$forkSha", session.ForkBaseSha);
        BindNullable(command, "$inherits", session.InheritsFromSessionId);
        BindNullable(command, "$inheritsSeq", session.InheritsUpToSeq);
        command.Parameters.AddWithValue("$lineage", session.LineageRootId ?? session.Id);
        command.Parameters.AddWithValue("$origin", OriginName(session.Origin));
        BindNullable(command, "$automation", session.AutomationId);
        BindNullable(command, "$automationRun", session.AutomationRunId);
        var changed = await ExecuteOccupyingAsync(command, connection, transaction, key, zeroRowsMeansOccupied: root).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new InvalidOperationException($"Session {session.Id} could not be inserted.");
        }
    }

    /// <summary>
    /// Conditional <c>Archived|Deleted → Active</c> of one row (clears <c>deleted_at</c>), then Deleted descendants →
    /// Archived. Returns null when there is no such row; throws when the key is held or the row is in another state
    /// than <paramref name="allowActive"/> permits.
    /// </summary>
    private static async Task<bool?> ActivateRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        bool allowActive)
    {
        string status;
        string key;
        bool root;
        await using (var read = Command(connection, transaction, "SELECT status, occupancy_key, parent_session_id IS NULL FROM sessions WHERE id = $id"))
        {
            read.Parameters.AddWithValue("$id", sessionId);
            await using var reader = await read.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
            if (!await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                return null;
            }

            status = reader.GetString(0);
            key = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            root = reader.GetInt64(2) != 0;
        }

        if (status == StorageSchema.StatusActive)
        {
            return allowActive
                ? true
                : throw new InvalidOperationException($"Session {sessionId} is already active.");
        }

        await using (var update = Command(
            connection,
            transaction,
            $"""
            UPDATE sessions SET status = 'Active', deleted_at = NULL
            WHERE id = $id AND status IN ('Archived', 'Deleted') {(root ? "AND " + KeyFreeSql : string.Empty)}
            """))
        {
            update.Parameters.AddWithValue("$id", sessionId);
            update.Parameters.AddWithValue("$key", key);
            var changed = await ExecuteOccupyingAsync(update, connection, transaction, key, zeroRowsMeansOccupied: root).ConfigureAwait(false);
            if (changed != 1)
            {
                throw new InvalidOperationException($"Session {sessionId} could not be activated.");
            }
        }

        await using (var children = Command(
            connection,
            transaction,
            DescendantsCte + "\nUPDATE sessions SET status = 'Archived', deleted_at = NULL WHERE id IN (SELECT id FROM tree) AND status = 'Deleted'"))
        {
            children.Parameters.AddWithValue("$id", sessionId);
            await children.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return true;
    }

    private static async Task<int> SoftDeleteTreeAsync(SqliteConnection connection, SqliteTransaction transaction, string idsJson)
    {
        await using var command = Command(
            connection,
            transaction,
            TreeCte + "\nUPDATE sessions SET status = 'Deleted', deleted_at = $at WHERE id IN (SELECT id FROM tree) AND status <> 'Deleted'");
        command.Parameters.AddWithValue("$ids", idsJson);
        command.Parameters.AddWithValue("$at", StorageDb.FormatTimestamp(DateTimeOffset.UtcNow));
        return await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<SessionInfo> ReadSessionAsync(SqliteConnection connection, SqliteTransaction transaction, string sessionId)
    {
        await using var command = Command(connection, transaction, $"SELECT {SessionColumns} FROM sessions WHERE id = $id");
        command.Parameters.AddWithValue("$id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
        return await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false)
            ? ReadSession(reader, sessionId)
            : throw new InvalidOperationException($"Session {sessionId} does not exist.");
    }

    private sealed class CreateSessionOp(SessionInfo session, string key, CancellationToken ct)
        : WriteOp<SessionInfo>(ct)
    {
        public override bool UsesSavepoint => session.ParentSessionId is null;

        protected override async ValueTask<SessionInfo> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences)
        {
            await InsertSessionAsync(connection, transaction, session, key).ConfigureAwait(false);
            return session;
        }
    }

    private sealed class UpdateSessionOp(string sql, string value, string sessionId, CancellationToken ct) : WriteOp<bool>(ct)
    {
        protected override async ValueTask<bool> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences)
        {
            await using var command = Command(connection, transaction, sql);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$id", sessionId);
            return await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false) > 0;
        }
    }

    /// <summary>The raw status write; its only occupancy protection is the unique index (translated, contained).</summary>
    private sealed class SetStatusOp(string sessionId, SessionStatus status, CancellationToken ct) : WriteOp<bool>(ct)
    {
        public override bool UsesSavepoint => status == SessionStatus.Active;

        protected override async ValueTask<bool> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences)
        {
            var sql = status == SessionStatus.Deleted
                ? "UPDATE sessions SET status = 'Deleted', deleted_at = COALESCE(deleted_at, $at) WHERE id = $id"
                : "UPDATE sessions SET status = $status, deleted_at = NULL WHERE id = $id";
            await using var command = Command(connection, transaction, sql);
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$status", StatusName(status));
            command.Parameters.AddWithValue("$at", StorageDb.FormatTimestamp(DateTimeOffset.UtcNow));
            try
            {
                return await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false) > 0;
            }
            catch (SqliteException exception) when (IsOccupancyViolation(exception))
            {
                await using var keyRead = Command(connection, transaction, "SELECT occupancy_key FROM sessions WHERE id = $id");
                keyRead.Parameters.AddWithValue("$id", sessionId);
                var key = await keyRead.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false) as string ?? string.Empty;
                throw await OccupiedAsync(connection, transaction, key, exception).ConfigureAwait(false);
            }
        }
    }

    private sealed class ActivateOp(string sessionId, CancellationToken ct) : WriteOp<bool>(ct)
    {
        public override bool UsesSavepoint => true;

        protected override async ValueTask<bool> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences) =>
            await ActivateRowAsync(connection, transaction, sessionId, allowActive: true).ConfigureAwait(false) ?? false;
    }

    /// <summary>Section 4.4: archive old (must be Active) → create / activate the target, all under one savepoint.</summary>
    private sealed class HandoverOp(
        SqliteSessionStore store,
        string oldSessionId,
        SessionInfo? session,
        string? key,
        HandoverTarget.NewSession? create,
        string? resumeSessionId,
        CancellationToken ct) : WriteOp<SessionInfo>(ct)
    {
        public override bool UsesSavepoint => true;

        protected override async ValueTask<SessionInfo> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences)
        {
            // 1. Archive the old session; it must still be Active.
            await using (var archive = Command(connection, transaction, "UPDATE sessions SET status = 'Archived' WHERE id = $old AND status = 'Active'"))
            {
                archive.Parameters.AddWithValue("$old", oldSessionId);
                if (await archive.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false) != 1)
                {
                    throw new SessionNotActiveException(oldSessionId);
                }
            }

            if (resumeSessionId is not null)
            {
                // 2b. Resume: conditional Archived|Deleted → Active of the same row; nothing is copied.
                var activated = await ActivateRowAsync(connection, transaction, resumeSessionId, allowActive: false).ConfigureAwait(false);
                if (activated is null)
                {
                    throw new InvalidOperationException($"Session {resumeSessionId} does not exist.");
                }

                return await store.ReadSessionAsync(connection, transaction, resumeSessionId).ConfigureAwait(false);
            }

            // 2a. New session: fork / lineage bookkeeping comes from the old row.
            string? forkOf;
            string? forkSha;
            string lineage;
            await using (var read = Command(
                connection,
                transaction,
                "SELECT fork_of_branch, fork_base_sha, COALESCE(lineage_root_id, id) FROM sessions WHERE id = $old"))
            {
                read.Parameters.AddWithValue("$old", oldSessionId);
                await using var reader = await read.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
                await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                forkOf = reader.IsDBNull(0) ? null : reader.GetString(0);
                forkSha = reader.IsDBNull(1) ? null : reader.GetString(1);
                lineage = reader.GetString(2);
            }

            var created = session! with { ForkOfBranch = forkOf, ForkBaseSha = forkSha, LineageRootId = lineage };
            await InsertSessionAsync(connection, transaction, created, key!).ConfigureAwait(false);

            var messages = create!.InitialMessages;
            var at = DateTimeOffset.UtcNow;
            for (var index = 0; index < messages.Count; index++)
            {
                await store.InsertMessageAsync(connection, transaction, created.Id, index + 1, messages[index], null, at).ConfigureAwait(false);
            }

            if (create.CopyTodosAndPlans)
            {
                await CopyTodosAndPlansAsync(connection, transaction, oldSessionId, created.Id).ConfigureAwait(false);
            }

            // Only now, with every sub-step done, does the writer learn the new session's last sequence.
            sequences.Record(created.Id, messages.Count);
            return created;
        }

        private static async Task CopyTodosAndPlansAsync(SqliteConnection connection, SqliteTransaction transaction, string from, string to)
        {
            var groups = new List<string>();
            await using (var read = Command(connection, transaction, "SELECT id FROM todo_groups WHERE session_id = $from ORDER BY order_index, id"))
            {
                read.Parameters.AddWithValue("$from", from);
                await using var reader = await read.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
                while (await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    groups.Add(reader.GetString(0));
                }
            }

            // Old group id → new group id, as a JSON object the todo copy looks group ids up in.
            using var map = new MemoryStream();
            await using (var writer = new Utf8JsonWriter(map))
            {
                writer.WriteStartObject();
                if (groups.Count > 0)
                {
                    await using var insert = Command(
                        connection,
                        transaction,
                        """
                        INSERT INTO todo_groups (id, session_id, title, order_index, updated_at)
                        SELECT $new, $to, title, order_index, updated_at FROM todo_groups WHERE id = $old
                        """);
                    var newId = insert.Parameters.Add("$new", SqliteType.Text);
                    var oldId = insert.Parameters.Add("$old", SqliteType.Text);
                    insert.Parameters.AddWithValue("$to", to);
                    foreach (var group in groups)
                    {
                        var copy = Guid.NewGuid().ToString("n");
                        newId.Value = copy;
                        oldId.Value = group;
                        await insert.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                        writer.WriteString(group, copy);
                    }
                }

                writer.WriteEndObject();
            }

            await using (var todos = Command(
                connection,
                transaction,
                """
                INSERT INTO todos (id, session_id, title, status, note, order_index, updated_at, commit_subject, summary,
                                   verification, commit_id, commit_error, group_id, origin)
                SELECT lower(hex(randomblob(16))), $to, title, status, note, order_index, updated_at, commit_subject, summary,
                       verification, commit_id, commit_error,
                       CASE WHEN group_id IS NULL THEN NULL ELSE json_extract($map, '$."' || group_id || '"') END,
                       origin
                FROM todos WHERE session_id = $from
                """))
            {
                todos.Parameters.AddWithValue("$to", to);
                todos.Parameters.AddWithValue("$from", from);
                todos.Parameters.AddWithValue("$map", Encoding.UTF8.GetString(map.GetBuffer(), 0, (int)map.Length));
                await todos.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await using var plans = Command(
                connection,
                transaction,
                "INSERT INTO plan_summaries (session_id, text, updated_at) SELECT $to, text, updated_at FROM plan_summaries WHERE session_id = $from");
            plans.Parameters.AddWithValue("$to", to);
            plans.Parameters.AddWithValue("$from", from);
            await plans.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class SoftDeleteOp(string idsJson, CancellationToken ct) : WriteOp<int>(ct)
    {
        public override bool UsesSavepoint => true;

        protected override async ValueTask<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences) =>
            await SoftDeleteTreeAsync(connection, transaction, idsJson).ConfigureAwait(false);
    }

    private sealed class PurgeOp(SqliteSessionStore store, string sessionId, CancellationToken ct) : WriteOp<IReadOnlyList<string>>(ct)
    {
        private List<string> _purged = [];

        public override bool UsesSavepoint => true;

        protected override async ValueTask<IReadOnlyList<string>> ExecuteAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SeqAllocator sequences)
        {
            _purged = [];
            await using (var status = Command(connection, transaction, "SELECT status FROM sessions WHERE id = $id"))
            {
                status.Parameters.AddWithValue("$id", sessionId);
                switch (await status.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    case null:
                        return [];
                    case string value when value != StorageSchema.StatusDeleted:
                        throw new InvalidOperationException($"Session {sessionId} is {value}; only a Deleted session can be purged.");
                }
            }

            // Self first, then descendants by depth (the depth cap only guards against a corrupt parent cycle).
            var ids = new List<string>();
            await using (var tree = Command(
                connection,
                transaction,
                """
                WITH RECURSIVE tree(id, depth) AS (
                    SELECT $id, 0
                    UNION
                    SELECT s.id, t.depth + 1 FROM sessions s JOIN tree t ON s.parent_session_id = t.id WHERE t.depth < 1024)
                SELECT id FROM tree GROUP BY id ORDER BY MIN(depth), id
                """))
            {
                tree.Parameters.AddWithValue("$id", sessionId);
                await using var reader = await tree.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
                while (await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    ids.Add(reader.GetString(0));
                }
            }

            var json = JsonArray(ids);
            var inheriting = new List<string>();
            await using (var guard = Command(
                connection,
                transaction,
                """
                SELECT id FROM sessions
                WHERE inherits_from_session_id IN (SELECT value FROM json_each($ids))
                  AND id NOT IN (SELECT value FROM json_each($ids))
                ORDER BY id
                """))
            {
                guard.Parameters.AddWithValue("$ids", json);
                await using var reader = await guard.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
                while (await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    inheriting.Add(reader.GetString(0));
                }
            }

            if (inheriting.Count > 0)
            {
                throw new SessionPurgeRefusedException(inheriting);
            }

            var search = store.Fts5Available ? "messages_fts" : "messages_text";
            foreach (var table in new[] { "messages", search, "summaries", "todos", "todo_groups", "plan_summaries", "turn_checkpoints" })
            {
                await using var delete = Command(connection, transaction, $"DELETE FROM {table} WHERE session_id IN (SELECT value FROM json_each($ids))");
                delete.Parameters.AddWithValue("$ids", json);
                await delete.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await using (var sessions = Command(connection, transaction, "DELETE FROM sessions WHERE id IN (SELECT value FROM json_each($ids))"))
            {
                sessions.Parameters.AddWithValue("$ids", json);
                await sessions.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }

            _purged = ids;
            return ids;
        }

        protected override void OnCommitted()
        {
            foreach (var id in _purged)
            {
                store._lastSeq.TryRemove(id, out _);
            }
        }
    }

    private sealed class HealOp(string candidatesJson, CancellationToken ct) : WriteOp<IReadOnlyList<string>>(ct)
    {
        public override bool UsesSavepoint => true;

        protected override async ValueTask<IReadOnlyList<string>> ExecuteAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SeqAllocator sequences)
        {
            var healed = new List<string>();
            await using (var still = Command(
                connection,
                transaction,
                """
                SELECT id FROM sessions
                WHERE id IN (SELECT value FROM json_each($ids)) AND parent_session_id IS NULL AND status IN ('Active', 'Archived')
                ORDER BY id
                """))
            {
                still.Parameters.AddWithValue("$ids", candidatesJson);
                await using var reader = await still.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
                while (await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    healed.Add(reader.GetString(0));
                }
            }

            if (healed.Count == 0)
            {
                return [];
            }

            await SoftDeleteTreeAsync(connection, transaction, JsonArray(healed)).ConfigureAwait(false);
            await StorageSchema.InsertNoticeAsync(connection, transaction, StorageNoticeKinds.HealDeleted, healed, CancellationToken.None).ConfigureAwait(false);
            return healed;
        }
    }

    private sealed class SetFingerprintOp(string sessionId, string fingerprint, CancellationToken ct) : WriteOp<bool>(ct)
    {
        protected override async ValueTask<bool> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences)
        {
            await using var command = Command(
                connection,
                transaction,
                "UPDATE sessions SET prefix_fingerprint = $value WHERE id = $id AND prefix_fingerprint IS NOT $value");
            command.Parameters.AddWithValue("$value", fingerprint);
            command.Parameters.AddWithValue("$id", sessionId);
            return await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false) > 0;
        }
    }

    private sealed class RenameBranchOp(string projectId, string key, string oldBranch, string newBranch, CancellationToken ct) : WriteOp<int>(ct)
    {
        public override bool UsesSavepoint => true;

        protected override async ValueTask<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences)
        {
            int changed;
            await using (var branch = Command(
                connection,
                transaction,
                "UPDATE sessions SET branch = $new WHERE project_id = $project AND occupancy_key = $key AND branch IS NOT $new"))
            {
                branch.Parameters.AddWithValue("$new", newBranch);
                branch.Parameters.AddWithValue("$project", projectId);
                branch.Parameters.AddWithValue("$key", key);
                changed = await branch.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await using var forks = Command(
                connection,
                transaction,
                "UPDATE sessions SET fork_of_branch = $new WHERE project_id = $project AND fork_of_branch = $old");
            forks.Parameters.AddWithValue("$new", newBranch);
            forks.Parameters.AddWithValue("$old", oldBranch);
            forks.Parameters.AddWithValue("$project", projectId);
            return changed + await forks.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class AcknowledgeNoticesOp(string idsJson, CancellationToken ct) : WriteOp<bool>(ct)
    {
        protected override async ValueTask<bool> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences)
        {
            await using var command = Command(
                connection,
                transaction,
                "UPDATE storage_notices SET acknowledged_at = $at WHERE id IN (SELECT value FROM json_each($ids)) AND acknowledged_at IS NULL");
            command.Parameters.AddWithValue("$ids", idsJson);
            command.Parameters.AddWithValue("$at", StorageDb.FormatTimestamp(DateTimeOffset.UtcNow));
            return await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false) > 0;
        }
    }
}
