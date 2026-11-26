using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Forge.Abstractions;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Storage;

/// <summary>
/// SQLite-backed session store. All writes funnel through one writer loop (a bounded channel of
/// write operations committed in small batches), so sequence assignment is serialized and a caller
/// only observes a sequence number after the transaction carrying it has committed. Reads use
/// short-lived pooled connections against the same WAL database and therefore never block the writer.
/// The same file is the session catalog: project/worktree bindings, parent links, archive state and
/// activity are columns of the <c>sessions</c> row, so there is no second place to keep in sync.
/// </summary>
public sealed partial class SqliteSessionStore : ISessionStore, ISessionCatalog, ISubAgentStateStore, IAsyncDisposable, IDisposable
{
    private const int WriterBatchLimit = 128;
    private const int WriterQueueCapacity = 4096;
    private const int LockRetryCount = 5;

    /// <summary>Columns <see cref="ReadSession"/> reads, in ordinal order; a missing lineage root reads as the row's own id.</summary>
    private const string SessionColumns =
        "id, workspace_path, title, created_at, project_id, worktree_path, branch, parent_session_id, agent_kind, task, status, " +
        "last_active_at, deleted_at, fork_of_branch, fork_base_sha, " +
        "COALESCE(lineage_root_id, id), origin_kind, prefix_fingerprint, automation_id, automation_run_id, child_driver, " +
        "title_source";

    /// <summary>Columns a new session row is written with; <c>occupancy_key</c> is always computed by the store.</summary>
    private const string InsertColumns =
        "id, workspace_path, title, created_at, project_id, worktree_path, branch, parent_session_id, agent_kind, task, status, " +
        "last_active_at, last_active_ticks, occupancy_key, fork_of_branch, fork_base_sha, " +
        "lineage_root_id, origin_kind, automation_id, automation_run_id, child_driver";

    /// <summary>Name of the savepoint each occupancy-changing / lifecycle write runs under inside a writer batch.</summary>
    private const string OperationSavepoint = "forge_op";

    /// <summary><c>SQLITE_CONSTRAINT_UNIQUE</c>: the extended result code of a unique-index violation.</summary>
    private const int SqliteConstraintUnique = 2067;

    private const string FtsInsertSql = "INSERT INTO messages_fts (session_id, seq, text) VALUES ($s, $q, $t)";
    private const string FallbackInsertSql = "INSERT OR REPLACE INTO messages_text (session_id, seq, text) VALUES ($s, $q, $t)";

    private readonly string? _dataDirectory;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly Channel<IWriteOp> _writes;
    private readonly ConcurrentDictionary<string, long> _lastSeq = new(StringComparer.Ordinal);
    private readonly Lazy<Task> _initialization;

    private Task? _writerLoop;
    private Exception? _writerFault;
    private string _searchInsertSql = FtsInsertSql;
    private int _fts5Available = 1;
    private int _disposed;

    public SqliteSessionStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _databasePath = StorageDb.DatabasePath(_dataDirectory);
        _connectionString = StorageDb.ConnectionString(_databasePath);
        _writes = CreateChannel();
        _initialization = new Lazy<Task>(InitializeAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Creates a store over an existing connection string; used by hosts that own the database file.</summary>
    public SqliteSessionStore(SqliteConnectionStringBuilder connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentException.ThrowIfNullOrEmpty(connectionString.DataSource, nameof(connectionString));
        _databasePath = ResolveDatabasePath(connectionString.DataSource);
        _connectionString = StorageDb.ConnectionString(connectionString);
        _writes = CreateChannel();
        _initialization = new Lazy<Task>(InitializeAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string ConnectionString => _connectionString;

    /// <summary>Full path of the database file this store reads and writes.</summary>
    public string DatabasePath => _databasePath;

    /// <summary>False when the bundled SQLite lacks the FTS5 module; search then falls back to <c>LIKE</c>.</summary>
    public bool Fts5Available => Volatile.Read(ref _fts5Available) != 0;

    /// <summary>Same as <see cref="CreateAsync"/> with only the workspace and a title.</summary>
    public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(title);
        return CreateAsync(new SessionCreate(workspacePath, title), ct);
    }

    /// <summary>
    /// Inserts a session. A root session (no parent) is a conditional insert: when another Active root session holds
    /// the same occupancy key it throws <see cref="WorktreeOccupiedException"/> without failing writes batched with it.
    /// </summary>
    public async Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct)
    {
        var (session, key) = PrepareCreate(request);
        return await EnqueueAsync(new CreateSessionOp(session, key, ct), ct).ConfigureAwait(false);
    }

    public async Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SessionColumns} FROM sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        await using var reader = await ExecuteSessionReadAsync(command, ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadSession(reader, sessionId) : null;
    }

    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = workspacePath is null
            ? $"SELECT {SessionColumns} FROM sessions WHERE status <> 'Deleted' ORDER BY created_at DESC, id"
            : $"SELECT {SessionColumns} FROM sessions WHERE workspace_path = $ws AND status <> 'Deleted' ORDER BY created_at DESC, id";
        if (workspacePath is not null)
        {
            command.Parameters.AddWithValue("$ws", workspacePath);
        }

        var sessions = new List<SessionInfo>();
        await using var reader = await ExecuteSessionReadAsync(command, ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            sessions.Add(ReadSession(reader, null));
        }

        return sessions;
    }

    /// <summary>
    /// Filters by the non-null members of <paramref name="query"/>; newest activity first. Deleted sessions are left out
    /// unless <see cref="SessionQuery.Status"/> asks for <see cref="SessionStatus.Deleted"/> explicitly.
    /// </summary>
    public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var status = query.Status is { } wanted
            ? $"status = '{StatusName(wanted)}'"
            : "status <> 'Deleted'";
        return QuerySessionsAsync(query, status, ct);
    }

    private async Task<IReadOnlyList<SessionInfo>> QuerySessionsAsync(SessionQuery query, string statusCondition, CancellationToken ct)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var conditions = new List<string>(4) { statusCondition };
        if (query.ProjectId is not null)
        {
            conditions.Add("project_id = $project");
            command.Parameters.AddWithValue("$project", query.ProjectId);
        }

        if (query.WorktreePath is not null)
        {
            conditions.Add("worktree_path = $worktree");
            command.Parameters.AddWithValue("$worktree", query.WorktreePath);
        }

        if (query.ParentSessionId is { } parent)
        {
            if (parent.Length == 0)
            {
                conditions.Add("parent_session_id IS NULL");
            }
            else
            {
                conditions.Add("parent_session_id = $parent");
                command.Parameters.AddWithValue("$parent", parent);
            }
        }

        command.CommandText =
            $"SELECT {SessionColumns} FROM sessions WHERE {string.Join(" AND ", conditions)} ORDER BY last_active_ticks DESC, id";

        var sessions = new List<SessionInfo>();
        await using var reader = await ExecuteSessionReadAsync(command, ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            sessions.Add(ReadSession(reader, null));
        }

        return sessions;
    }

    /// <summary>
    /// Raw status write (no cascade, no conditional check). Active and Archived clear <c>deleted_at</c>; Deleted stamps it
    /// when unset. Making a root session Active while another holds its key is refused by the unique occupancy index and
    /// surfaces as <see cref="WorktreeOccupiedException"/>; App code uses <see cref="ActivateAsync"/> instead.
    /// </summary>
    public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        _ = StatusName(status);
        return EnqueueAsync(new SetStatusOp(sessionId, status, ct), ct);
    }

    public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        ArgumentNullException.ThrowIfNull(title);
        return EnqueueAsync(new UpdateSessionOp(
            "UPDATE sessions SET title = $value, title_source = 'user' WHERE id = $id",
            title,
            sessionId,
            ct), ct);
    }

    public Task<bool> TryTitleAsync(string sessionId, string title, string source, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        ArgumentNullException.ThrowIfNull(title);
        // The reader refuses a source it does not know, so a wrong value written here would make the session unreadable for
        // good. Only the two replaceable sources are accepted; a person's name goes through RenameAsync, which stamps "user".
        if (source is not (SessionTitleSource.Derived or SessionTitleSource.Agent))
        {
            throw new ArgumentException(
                $"'{source}' is not a title source this write may set: pass '{SessionTitleSource.Derived}' or '{SessionTitleSource.Agent}', "
                + "or use RenameAsync for a name a person typed.",
                nameof(source));
        }

        return EnqueueAsync(new TryTitleOp(title, source, sessionId, ct), ct);
    }

    public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        return EnqueueAsync(new TouchSessionOp(sessionId, at, ct), ct);
    }

    public async Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        ArgumentNullException.ThrowIfNull(message);
        return await EnqueueAsync(new AppendOp(this, sessionId, message, usage, DateTimeOffset.UtcNow, ct), ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<StoredMessage> ReadAsync(
        string sessionId,
        long fromSeq,
        long? toSeq,
        [EnumeratorCancellation] CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false);
        await using var command = StorageDb.Command(
            connection,
            "SELECT seq, content_json, usage_json, at FROM messages WHERE session_id = $id AND seq >= $from AND seq <= $to ORDER BY seq");
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$from", fromSeq);
        command.Parameters.AddWithValue("$to", toSeq ?? long.MaxValue);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return ReadMessage(reader, sessionId);
        }
    }

    public Task<SessionPage> ReadTailAsync(string sessionId, long beforeSeq, int maxMessages, CancellationToken ct) =>
        ReadTailAsync(sessionId, beforeSeq, maxMessages, turnsAboveBeforeSeq: null, ct);

    /// <summary>
    /// The tail page. <paramref name="turnsAboveBeforeSeq"/> carries the count the paging reader already has (the user
    /// messages below <paramref name="beforeSeq"/>): numbering the page is then one pass over the page's own rows instead
    /// of a walk over the whole prefix through <c>json_extract</c> (10k messages: 21-45 ms of every page, measured). The
    /// "is there more" probe is unchanged, and null counts the prefix exactly as before.
    /// <para>A page is whole rounds: <paramref name="maxMessages"/> bounds it from below, and a boundary that lands inside a
    /// turn is moved back to that turn's opening user message (one extra read, only for such a page). A seam inside a turn
    /// draws that turn twice in the transcript - once with its question, once as a round without one - and the derived count
    /// stays exact either way, because every user message between the two boundaries ends up inside the page.</para>
    /// </summary>
    public async Task<SessionPage> ReadTailAsync(
        string sessionId, long beforeSeq, int maxMessages, long? turnsAboveBeforeSeq, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false);
        var messages = new List<StoredMessage>(Math.Min(maxMessages, 512));
        await using (var command = StorageDb.Command(
                         connection,
                         """
                         SELECT seq, content_json, usage_json, at FROM (
                             SELECT seq, content_json, usage_json, at FROM messages
                             WHERE session_id = $id AND seq < $before ORDER BY seq DESC LIMIT $limit)
                         ORDER BY seq
                         """))
        {
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$before", beforeSeq);
            command.Parameters.AddWithValue("$limit", maxMessages);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                messages.Add(ReadMessage(reader, sessionId));
            }
        }

        await ExtendToOpeningTurnAsync(connection, sessionId, messages, ct).ConfigureAwait(false);
        if (messages.Count == 0)
        {
            return new SessionPage(messages, 0, HasMore: false);
        }

        var oldest = messages[0].Seq;
        var turns = turnsAboveBeforeSeq is { } above
            ? above - CountUserMessages(messages)
            : await CountTurnsBeforeAsync(connection, sessionId, oldest, ct).ConfigureAwait(false);

        bool hasMore;
        await using (var command = StorageDb.Command(
                         connection,
                         "SELECT EXISTS(SELECT 1 FROM messages WHERE session_id = $id AND seq < $oldest)"))
        {
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$oldest", oldest);
            hasMore = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        }

        return new SessionPage(messages, turns, hasMore);
    }

    /// <summary>
    /// Moves a page's start back to the user message that opened the turn its oldest message belongs to, so a paged
    /// transcript never splits a turn over two pages. The extra read is bounded by one turn and happens only when the
    /// message boundary falls inside one.
    /// </summary>
    private async Task ExtendToOpeningTurnAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sessionId,
        List<StoredMessage> messages,
        CancellationToken ct)
    {
        if (messages.Count == 0 || messages[0].Message.Role == MessageRole.User)
        {
            return;
        }

        var boundary = messages[0].Seq;
        var prefix = new List<StoredMessage>();
        await using (var command = StorageDb.Command(
                         connection,
                         """
                         SELECT seq, content_json, usage_json, at FROM messages
                         WHERE session_id = $id AND seq < $boundary AND seq >= COALESCE((
                             SELECT MAX(seq) FROM messages
                             WHERE session_id = $id AND seq < $boundary AND json_extract(content_json, '$.role') = 'user'), 0)
                         ORDER BY seq
                         """))
        {
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$boundary", boundary);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                prefix.Add(ReadMessage(reader, sessionId));
            }
        }

        if (prefix.Count > 0)
        {
            messages.InsertRange(0, prefix);
        }
    }

    /// <summary>The user messages inside a page: the rounds the page itself opens.</summary>
    private static long CountUserMessages(List<StoredMessage> messages)
    {
        var count = 0L;
        foreach (var message in messages)
        {
            if (message.Message.Role == MessageRole.User)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The user messages stored above <paramref name="oldest"/>: the transcript's round count for the page below.</summary>
    private static async Task<long> CountTurnsBeforeAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection, string sessionId, long oldest, CancellationToken ct)
    {
        await using var command = StorageDb.Command(
            connection,
            """
            SELECT COUNT(*) FROM messages
            WHERE session_id = $id AND seq < $oldest AND json_extract(content_json, '$.role') = 'user'
            """);
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$oldest", oldest);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>One message row as the schema stores it; every read path maps it the same way.</summary>
    private StoredMessage ReadMessage(Microsoft.Data.Sqlite.SqliteDataReader reader, string sessionId)
    {
        var seq = reader.GetInt64(0);
        var message = MessageJson.Deserialize(reader.GetString(1));
        var usage = reader.IsDBNull(2) ? null : MessageJson.ParseUsage(reader.GetString(2));
        var row = $"message {seq.ToString(CultureInfo.InvariantCulture)} of session {sessionId}";
        var at = StorageDb.ParseCanonicalTimestamp(
            StorageDb.ReadText(reader, 3, "at", _databasePath, row),
            "at",
            _databasePath,
            row);
        return new StoredMessage(seq, message, usage, at);
    }

    public async Task<long> LastSeqAsync(string sessionId, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        if (_lastSeq.TryGetValue(sessionId, out var cached))
        {
            return cached;
        }

        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false);
        return await ReadLastSeqAsync(connection, null, sessionId, ct).ConfigureAwait(false);
    }

    public async Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(summary.Text);
        StoragePaths.SafeSegment(summary.SessionId, "summary");
        await EnqueueAsync(new PutSummaryOp(summary, ct), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false);
        await using var command = StorageDb.Command(
            connection,
            "SELECT session_id, layer, from_seq, to_seq, text, at FROM summaries WHERE session_id = $id ORDER BY from_seq, layer, id");
        command.Parameters.AddWithValue("$id", sessionId);
        var summaries = new List<SummaryRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var storedSessionId = reader.GetString(0);
            var layer = reader.GetInt32(1);
            var fromSeq = reader.GetInt64(2);
            var toSeq = reader.GetInt64(3);
            var row = $"layer-{layer.ToString(CultureInfo.InvariantCulture)} summary of session {storedSessionId} " +
                $"covering {fromSeq.ToString(CultureInfo.InvariantCulture)}-{toSeq.ToString(CultureInfo.InvariantCulture)}";
            summaries.Add(new SummaryRecord(
                storedSessionId,
                layer,
                fromSeq,
                toSeq,
                reader.GetString(4),
                StorageDb.ParseCanonicalTimestamp(
                    StorageDb.ReadText(reader, 5, "at", _databasePath, row),
                    "at",
                    _databasePath,
                    row)));
        }

        return summaries;
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        if (limit <= 0 || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        await EnsureInitializedAsync().ConfigureAwait(false);
        return Fts5Available
            ? await SearchFullTextAsync(sessionId, query, limit, ct).ConfigureAwait(false)
            : await SearchLikeAsync(sessionId, query, limit, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _writes.Writer.TryComplete();
        if (_writerLoop is not null)
        {
            await _writerLoop.ConfigureAwait(false);
        }

        SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _writes.Writer.TryComplete();
        try
        {
            _writerLoop?.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Shutdown must not throw; failures already surfaced through the write operations.
        }

        SqliteConnection.ClearAllPools();
    }

    internal static string StatusName(SessionStatus status) => status switch
    {
        SessionStatus.Active => StorageSchema.StatusActive,
        SessionStatus.Archived => StorageSchema.StatusArchived,
        SessionStatus.Deleted => StorageSchema.StatusDeleted,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown session status."),
    };

    private static SessionStatus ParseStatus(string value, string databasePath, string row) => value switch
    {
        StorageSchema.StatusActive => SessionStatus.Active,
        StorageSchema.StatusArchived => SessionStatus.Archived,
        StorageSchema.StatusDeleted => SessionStatus.Deleted,
        _ => throw StorageDb.Malformed(databasePath, "status", row, $"'{value}' is not one of Active, Archived, Deleted", null),
    };

    /// <summary><c>sessions.child_driver</c> for a create request; null and <c>"user"</c> are the only legal values.</summary>
    private static string? ChildDriverName(string? childDriver) => childDriver switch
    {
        null or StorageSchema.ChildDriverUser => childDriver,
        _ => throw new ArgumentException(
            $"Unknown child driver '{childDriver}': only null (a child the model spawned) and '{StorageSchema.ChildDriverUser}' are allowed.",
            nameof(childDriver)),
    };

    /// <summary>NULL reads as null (a child the model spawned, or a root session); any other text than <c>"user"</c> is malformed.</summary>
    private static string? ParseChildDriver(string? value, string databasePath, string row) => value switch
    {
        null or StorageSchema.ChildDriverUser => value,
        _ => throw StorageDb.Malformed(databasePath, "child_driver", row, $"'{value}' is neither NULL nor '{StorageSchema.ChildDriverUser}'", null),
    };

    private static string? ParseTitleSource(string? value, string databasePath, string row) => value switch
    {
        null or SessionTitleSource.Derived or SessionTitleSource.Agent or SessionTitleSource.User => value,
        _ => throw StorageDb.Malformed(databasePath, "title_source", row, $"'{value}' is not a title source this build writes", null),
    };

    private static string ResolveDatabasePath(string dataSource) =>
        dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
        dataSource.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase)
            ? dataSource
            : Path.GetFullPath(dataSource);

    /// <summary>
    /// Runs a <c>sessions</c> read. A database file written before <c>sessions.child_driver</c> existed has no such column
    /// and no ladder step adds one (the product is unreleased, so an old file is rebuilt rather than migrated); SQLite's
    /// bare "no such column" is replaced here by the one instruction that resolves it.
    /// </summary>
    private static async Task<SqliteDataReader> ExecuteSessionReadAsync(SqliteCommand command, CancellationToken ct)
    {
        try
        {
            return await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.Message.Contains("child_driver", StringComparison.Ordinal))
        {
            throw new InvalidDataException("本地 forge.db 缺少 sessions.child_driver，请删除并重建数据库。", exception);
        }
    }

    private SessionInfo ReadSession(SqliteDataReader reader, string? knownId)
    {
        var id = StorageDb.ReadText(reader, 0, "id", _databasePath, knownId ?? "a session whose id cannot be read");
        var row = $"session {id}";
        var createdAt = StorageDb.ParseCanonicalTimestamp(
            StorageDb.ReadText(reader, 3, "created_at", _databasePath, row),
            "created_at",
            _databasePath,
            row);
        return new SessionInfo(
            id,
            StorageDb.ReadText(reader, 1, "workspace_path", _databasePath, row),
            StorageDb.ReadText(reader, 2, "title", _databasePath, row),
            createdAt)
        {
            ProjectId = StorageDb.ReadOptionalText(reader, 4, "project_id", _databasePath, row),
            WorktreePath = StorageDb.ReadOptionalText(reader, 5, "worktree_path", _databasePath, row),
            Branch = StorageDb.ReadOptionalText(reader, 6, "branch", _databasePath, row),
            ParentSessionId = StorageDb.ReadOptionalText(reader, 7, "parent_session_id", _databasePath, row),
            AgentKind = StorageDb.ReadOptionalText(reader, 8, "agent_kind", _databasePath, row),
            Task = StorageDb.ReadOptionalText(reader, 9, "task", _databasePath, row),
            Status = ParseStatus(StorageDb.ReadText(reader, 10, "status", _databasePath, row), _databasePath, row),
            LastActiveAt = StorageDb.ParseCanonicalTimestamp(
                StorageDb.ReadText(reader, 11, "last_active_at", _databasePath, row),
                "last_active_at",
                _databasePath,
                row),
            DeletedAt = StorageDb.ReadOptionalText(reader, 12, "deleted_at", _databasePath, row) is { } deletedAt
                ? StorageDb.ParseCanonicalTimestamp(deletedAt, "deleted_at", _databasePath, row)
                : null,
            ForkOfBranch = StorageDb.ReadOptionalText(reader, 13, "fork_of_branch", _databasePath, row),
            ForkBaseSha = StorageDb.ReadOptionalText(reader, 14, "fork_base_sha", _databasePath, row),
            LineageRootId = StorageDb.ReadText(reader, 15, "lineage_root_id", _databasePath, row),
            Origin = ParseOrigin(StorageDb.ReadOptionalText(reader, 16, "origin_kind", _databasePath, row), _databasePath, row),
            PrefixFingerprint = StorageDb.ReadOptionalText(reader, 17, "prefix_fingerprint", _databasePath, row),
            AutomationId = StorageDb.ReadOptionalText(reader, 18, "automation_id", _databasePath, row),
            AutomationRunId = StorageDb.ReadOptionalText(reader, 19, "automation_run_id", _databasePath, row),
            ChildDriver = ParseChildDriver(
                StorageDb.ReadOptionalText(reader, 20, "child_driver", _databasePath, row),
                _databasePath,
                row),
            TitleSource = ParseTitleSource(
                StorageDb.ReadOptionalText(reader, 21, "title_source", _databasePath, row),
                _databasePath,
                row),
        };
    }

    private static Channel<IWriteOp> CreateChannel() => Channel.CreateBounded<IWriteOp>(
        new BoundedChannelOptions(WriterQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    private Task EnsureInitializedAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _initialization.Value;
    }

    private async Task InitializeAsync()
    {
        if (_dataDirectory is not null)
        {
            Directory.CreateDirectory(_dataDirectory);
        }

        await using (var connection = await StorageDb.OpenAsync(_connectionString, CancellationToken.None).ConfigureAwait(false))
        {
            await StorageDb.ConfigureAsync(connection, CancellationToken.None).ConfigureAwait(false);
            var fts5 = await StorageSchema.EnsureAsync(connection, _databasePath, CancellationToken.None).ConfigureAwait(false);
            await StorageSchema.EnsureSubAgentStateAsync(connection, CancellationToken.None).ConfigureAwait(false);
            _searchInsertSql = fts5 ? FtsInsertSql : FallbackInsertSql;
            Volatile.Write(ref _fts5Available, fts5 ? 1 : 0);
        }

        _writerLoop = Task.Run(RunWriterAsync, CancellationToken.None);
    }

    private async Task RunWriterAsync()
    {
        try
        {
            await using var connection = await StorageDb.OpenAsync(_connectionString, CancellationToken.None).ConfigureAwait(false);
            await StorageDb.ConfigureAsync(connection, CancellationToken.None).ConfigureAwait(false);
            var batch = new List<IWriteOp>(WriterBatchLimit);
            var sequences = new SeqAllocator(this);
            while (await _writes.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < WriterBatchLimit && _writes.Reader.TryRead(out var op))
                {
                    batch.Add(op);
                }

                await ExecuteBatchAsync(connection, batch, sequences).ConfigureAwait(false);
            }
        }
        catch (Exception fatal)
        {
            Volatile.Write(ref _writerFault, fatal);
            _writes.Writer.TryComplete(fatal);
            while (_writes.Reader.TryRead(out var pending))
            {
                pending.Fail(fatal);
            }
        }
    }

    private static async Task ExecuteBatchAsync(SqliteConnection connection, List<IWriteOp> batch, SeqAllocator sequences)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                foreach (var op in batch)
                {
                    op.BeginAttempt();
                }

                // BEGIN IMMEDIATE: the writer takes the write lock up front, so a busy database surfaces here (and is
                // retried) instead of on the first write statement halfway through the batch.
                await using var transaction = connection.BeginTransaction(deferred: false);
                foreach (var op in batch)
                {
                    if (op.Cancellation.IsCancellationRequested)
                    {
                        op.Fail(new OperationCanceledException(op.Cancellation));
                        continue;
                    }

                    if (op.UsesSavepoint)
                    {
                        await RunContainedAsync(transaction, op, connection, sequences).ConfigureAwait(false);
                    }
                    else
                    {
                        await op.RunAsync(connection, transaction, sequences).ConfigureAwait(false);
                    }
                }

                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                sequences.Commit();
                foreach (var op in batch)
                {
                    op.Complete();
                }

                return;
            }
            catch (SqliteException exception) when (attempt < LockRetryCount && IsTransient(exception))
            {
                sequences.Reset();
                await Task.Delay(5 * (attempt + 1), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                sequences.Reset();
                foreach (var op in batch)
                {
                    op.Fail(exception);
                }

                return;
            }
        }
    }

    /// <summary>
    /// Runs one op under its own savepoint (I1): a failure of the op - an occupancy conflict, a refused handover or
    /// purge, any statement error - rolls back only to that savepoint and fails only that op once the batch commits;
    /// the other writes of the batch go through. A transient lock error and a failed savepoint rollback still fail the
    /// whole batch (the transaction itself is no longer usable then).
    /// </summary>
    private static async Task RunContainedAsync(
        SqliteTransaction transaction,
        IWriteOp op,
        SqliteConnection connection,
        SeqAllocator sequences)
    {
        await transaction.SaveAsync(OperationSavepoint, CancellationToken.None).ConfigureAwait(false);
        try
        {
            await op.RunAsync(connection, transaction, sequences).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not SqliteException { SqliteErrorCode: 5 or 6 })
        {
            await transaction.RollbackAsync(OperationSavepoint, CancellationToken.None).ConfigureAwait(false);
            await transaction.ReleaseAsync(OperationSavepoint, CancellationToken.None).ConfigureAwait(false);
            op.Defer(exception);
            return;
        }

        await transaction.ReleaseAsync(OperationSavepoint, CancellationToken.None).ConfigureAwait(false);
    }

    private static bool IsTransient(SqliteException exception) => exception.SqliteErrorCode is 5 or 6;

    private async Task<T> EnqueueAsync<T>(WriteOp<T> op, CancellationToken ct)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _writerFault) is { } fault)
        {
            throw new InvalidOperationException("The session store writer has stopped.", fault);
        }
        if (!_writes.Writer.TryWrite(op))
        {
            await _writes.Writer.WriteAsync(op, ct).ConfigureAwait(false);
        }

        return await op.Task.ConfigureAwait(false);
    }

    private async Task<long> ReadLastSeqAsync(SqliteConnection connection, SqliteTransaction? transaction, string sessionId, CancellationToken ct)
    {
        await using var command = StorageDb.Command(
            connection,
            transaction,
            "SELECT COALESCE(MAX(seq), 0) FROM messages WHERE session_id = $id");
        command.Parameters.AddWithValue("$id", sessionId);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long value ? value : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private async Task InsertMessageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        long seq,
        ChatMessage message,
        TokenUsage? usage,
        DateTimeOffset at)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO messages (session_id, seq, role, content_json, usage_json, at)
                VALUES ($id, $seq, $role, $content, $usage, $at)
                """;
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$seq", seq);
            command.Parameters.AddWithValue("$role", MessageJson.RoleName(message.Role));
            command.Parameters.AddWithValue("$content", MessageJson.Serialize(message));
            command.Parameters.AddWithValue("$usage", (object?)MessageJson.SerializeUsage(usage) ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", StorageDb.FormatTimestamp(at));
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var text = MessageJson.ExtractSearchText(message);
        if (text.Length > 0)
        {
            await InsertSearchRowAsync(connection, transaction, sessionId, seq, text).ConfigureAwait(false);
        }
    }

    private async Task InsertSearchRowAsync(SqliteConnection connection, SqliteTransaction transaction, string sessionId, long seq, string text)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _searchInsertSql;
        command.Parameters.AddWithValue("$s", sessionId);
        command.Parameters.AddWithValue("$q", seq);
        command.Parameters.AddWithValue("$t", text);
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SearchHit>> SearchFullTextAsync(string sessionId, string query, int limit, CancellationToken ct)
    {
        var match = BuildMatchQuery(query);
        if (match is null)
        {
            return [];
        }

        await using var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT seq, snippet(messages_fts, 2, '[', ']', '…', 16), bm25(messages_fts)
            FROM messages_fts
            WHERE messages_fts MATCH $q AND session_id = $id
            ORDER BY bm25(messages_fts), seq
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$q", match);
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$limit", limit);
        var hits = new List<SearchHit>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            hits.Add(new SearchHit(reader.GetInt64(0), reader.GetString(1), -reader.GetDouble(2)));
        }

        return hits;
    }

    private async Task<IReadOnlyList<SearchHit>> SearchLikeAsync(string sessionId, string query, int limit, CancellationToken ct)
    {
        var needle = query.Trim();
        await using var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT seq, text FROM messages_text
            WHERE session_id = $id AND text LIKE $pattern ESCAPE '\'
            ORDER BY seq
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$pattern", "%" + EscapeLike(needle) + "%");
        command.Parameters.AddWithValue("$limit", limit);
        var hits = new List<SearchHit>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var text = reader.GetString(1);
            var index = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            var start = Math.Max(0, index - 32);
            var length = Math.Min(text.Length - start, Math.Max(needle.Length, 64));
            hits.Add(new SearchHit(reader.GetInt64(0), text.Substring(start, length), 1d / (1 + hits.Count)));
        }

        return hits;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? BuildMatchQuery(string query)
    {
        var tokens = new List<string>(4);
        var current = new StringBuilder(16);
        foreach (var ch in query)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens.Count == 0 ? null : string.Join(" AND ", tokens.ConvertAll(static token => '"' + token + '"'));
    }

    private interface IWriteOp
    {
        CancellationToken Cancellation { get; }

        /// <summary>True for an op that must not fail the rest of its batch; it runs under <see cref="OperationSavepoint"/>.</summary>
        bool UsesSavepoint { get; }

        ValueTask RunAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences);

        /// <summary>Clears a failure recorded by an earlier, retried attempt of the batch.</summary>
        void BeginAttempt();

        /// <summary>Records a contained failure; reported by <see cref="Complete"/> once the batch commits.</summary>
        void Defer(Exception exception);

        void Complete();

        void Fail(Exception exception);
    }

    private abstract class WriteOp<T> : IWriteOp
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private T _result = default!;
        private Exception? _deferred;

        protected WriteOp(CancellationToken cancellationToken) => Cancellation = cancellationToken;

        public CancellationToken Cancellation { get; }

        public virtual bool UsesSavepoint => false;

        public Task<T> Task => _completion.Task;

        public async ValueTask RunAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences) =>
            _result = await ExecuteAsync(connection, transaction, sequences).ConfigureAwait(false);

        public void BeginAttempt() => _deferred = null;

        public void Defer(Exception exception) => _deferred = exception;

        public void Complete()
        {
            if (_deferred is { } failure)
            {
                _completion.TrySetException(failure);
                return;
            }

            if (_completion.TrySetResult(_result))
            {
                OnCommitted();
            }
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);

        protected abstract ValueTask<T> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences);

        /// <summary>Runs after the op's transaction committed and its result was published.</summary>
        protected virtual void OnCommitted()
        {
        }
    }

    private sealed class SeqAllocator(SqliteSessionStore owner)
    {
        private readonly Dictionary<string, long> _pending = new(StringComparer.Ordinal);

        public async ValueTask<long> NextAsync(SqliteConnection connection, SqliteTransaction transaction, string sessionId)
        {
            if (!_pending.TryGetValue(sessionId, out var current))
            {
                current = owner._lastSeq.TryGetValue(sessionId, out var cached)
                    ? cached
                    : await owner.ReadLastSeqAsync(connection, transaction, sessionId, CancellationToken.None).ConfigureAwait(false);
            }

            var next = current + 1;
            _pending[sessionId] = next;
            return next;
        }

        public void Commit()
        {
            foreach (var pair in _pending)
            {
                owner._lastSeq[pair.Key] = pair.Value;
            }

            _pending.Clear();
        }

        public void Reset() => _pending.Clear();

        /// <summary>Records the last sequence an op wrote for a session itself (the handover's initial messages).</summary>
        public void Record(string sessionId, long lastSeq) => _pending[sessionId] = lastSeq;
    }

    private sealed class TouchSessionOp(string sessionId, DateTimeOffset at, CancellationToken ct) : WriteOp<bool>(ct)
    {
        protected override async ValueTask<bool> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE sessions SET last_active_at = $at, last_active_ticks = $ticks WHERE id = $id";
            command.Parameters.AddWithValue("$at", StorageDb.FormatTimestamp(at));
            command.Parameters.AddWithValue("$ticks", StorageDb.TimestampTicks(at));
            command.Parameters.AddWithValue("$id", sessionId);
            return await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false) > 0;
        }
    }

    private sealed class AppendOp(
        SqliteSessionStore store,
        string sessionId,
        ChatMessage message,
        TokenUsage? usage,
        DateTimeOffset at,
        CancellationToken ct) : WriteOp<long>(ct)
    {
        protected override async ValueTask<long> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences)
        {
            var seq = await sequences.NextAsync(connection, transaction, sessionId).ConfigureAwait(false);
            await store.InsertMessageAsync(connection, transaction, sessionId, seq, message, usage, at).ConfigureAwait(false);
            // A message is activity: the idle-archive sweep must see a session whose turn just started as active, not wait for the reply.
            await using var touch = connection.CreateCommand();
            touch.Transaction = transaction;
            touch.CommandText = "UPDATE sessions SET last_active_at = $at, last_active_ticks = $ticks WHERE id = $id";
            touch.Parameters.AddWithValue("$at", StorageDb.FormatTimestamp(at));
            touch.Parameters.AddWithValue("$ticks", StorageDb.TimestampTicks(at));
            touch.Parameters.AddWithValue("$id", sessionId);
            await touch.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            return seq;
        }
    }

    private sealed class PutSummaryOp(SummaryRecord summary, CancellationToken ct) : WriteOp<bool>(ct)
    {
        protected override async ValueTask<bool> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO summaries (session_id, layer, from_seq, to_seq, text, at)
                VALUES ($id, $layer, $from, $to, $text, $at)
                """;
            command.Parameters.AddWithValue("$id", summary.SessionId);
            command.Parameters.AddWithValue("$layer", summary.Layer);
            command.Parameters.AddWithValue("$from", summary.FromSeq);
            command.Parameters.AddWithValue("$to", summary.ToSeq);
            command.Parameters.AddWithValue("$text", summary.Text);
            command.Parameters.AddWithValue("$at", StorageDb.FormatTimestamp(summary.At));
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            return true;
        }
    }
}
