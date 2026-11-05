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
public sealed class SqliteSessionStore : ISessionStore, ISessionCatalog, IAsyncDisposable, IDisposable
{
    private const int WriterBatchLimit = 128;
    private const int WriterQueueCapacity = 4096;
    private const int LockRetryCount = 5;

    private const string SessionColumns =
        "id, workspace_path, title, created_at, project_id, worktree_path, branch, parent_session_id, agent_kind, task, status, last_active_at";

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

    /// <summary>Creates a store over an existing connection string; used by tests and by hosts that own the database file.</summary>
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

    public async Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspacePath);
        ArgumentNullException.ThrowIfNull(request.Title);
        var now = DateTimeOffset.UtcNow;
        var session = new SessionInfo(Guid.NewGuid().ToString("n"), request.WorkspacePath, request.Title, now)
        {
            ProjectId = request.ProjectId,
            WorktreePath = request.WorktreePath,
            Branch = request.Branch,
            ParentSessionId = request.ParentSessionId,
            AgentKind = request.AgentKind,
            Task = request.Task,
            Status = SessionStatus.Active,
            LastActiveAt = now,
        };
        return await EnqueueAsync(new CreateSessionOp(session, ct), ct).ConfigureAwait(false);
    }

    public async Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SessionColumns} FROM sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadSession(reader, sessionId) : null;
    }

    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = workspacePath is null
            ? $"SELECT {SessionColumns} FROM sessions ORDER BY created_at DESC, id"
            : $"SELECT {SessionColumns} FROM sessions WHERE workspace_path = $ws ORDER BY created_at DESC, id";
        if (workspacePath is not null)
        {
            command.Parameters.AddWithValue("$ws", workspacePath);
        }

        var sessions = new List<SessionInfo>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            sessions.Add(ReadSession(reader, null));
        }

        return sessions;
    }

    /// <summary>Filters by the non-null members of <paramref name="query"/>; newest activity first.</summary>
    public async Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var conditions = new List<string>(4);
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

        if (query.Status is { } status)
        {
            conditions.Add("status = $status");
            command.Parameters.AddWithValue("$status", StatusName(status));
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

        command.CommandText = conditions.Count == 0
            ? $"SELECT {SessionColumns} FROM sessions ORDER BY last_active_ticks DESC, id"
            : $"SELECT {SessionColumns} FROM sessions WHERE {string.Join(" AND ", conditions)} ORDER BY last_active_ticks DESC, id";

        var sessions = new List<SessionInfo>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            sessions.Add(ReadSession(reader, null));
        }

        return sessions;
    }

    public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        return EnqueueAsync(new UpdateSessionOp(
            "UPDATE sessions SET status = $value WHERE id = $id",
            StatusName(status),
            sessionId,
            ct), ct);
    }

    public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        ArgumentNullException.ThrowIfNull(title);
        return EnqueueAsync(new UpdateSessionOp(
            "UPDATE sessions SET title = $value WHERE id = $id",
            title,
            sessionId,
            ct), ct);
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
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT seq, content_json, usage_json, at FROM messages WHERE session_id = $id AND seq >= $from AND seq <= $to ORDER BY seq";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$from", fromSeq);
        command.Parameters.AddWithValue("$to", toSeq ?? long.MaxValue);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var seq = reader.GetInt64(0);
            var message = MessageJson.Deserialize(reader.GetString(1));
            var usage = reader.IsDBNull(2) ? null : MessageJson.ParseUsage(reader.GetString(2));
            var at = StorageDb.ParseTimestamp(reader.GetString(3));
            yield return new StoredMessage(seq, message, usage, at);
        }
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
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT session_id, layer, from_seq, to_seq, text, at FROM summaries WHERE session_id = $id ORDER BY from_seq, layer, id";
        command.Parameters.AddWithValue("$id", sessionId);
        var summaries = new List<SummaryRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            summaries.Add(new SummaryRecord(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetString(4),
                StorageDb.ParseTimestamp(reader.GetString(5))));
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
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown session status."),
    };

    private static SessionStatus ParseStatus(string value, string databasePath, string row) => value switch
    {
        StorageSchema.StatusActive => SessionStatus.Active,
        StorageSchema.StatusArchived => SessionStatus.Archived,
        _ => throw StorageDb.Malformed(databasePath, "status", row, $"'{value}' is not one of Active, Archived", null),
    };

    private static string ResolveDatabasePath(string dataSource) =>
        dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
        dataSource.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase)
            ? dataSource
            : Path.GetFullPath(dataSource);

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
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
                foreach (var op in batch)
                {
                    if (op.Cancellation.IsCancellationRequested)
                    {
                        op.Fail(new OperationCanceledException(op.Cancellation));
                        continue;
                    }

                    await op.RunAsync(connection, transaction, sequences).ConfigureAwait(false);
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
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM messages WHERE session_id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long value ? value : Convert.ToInt64(result, CultureInfo.InvariantCulture);
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
        ValueTask RunAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences);
        void Complete();
        void Fail(Exception exception);
    }

    private abstract class WriteOp<T> : IWriteOp
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private T _result = default!;

        protected WriteOp(CancellationToken cancellationToken) => Cancellation = cancellationToken;

        public CancellationToken Cancellation { get; }

        public Task<T> Task => _completion.Task;

        public async ValueTask RunAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences) =>
            _result = await ExecuteAsync(connection, transaction, sequences).ConfigureAwait(false);

        public void Complete() => _completion.TrySetResult(_result);

        public void Fail(Exception exception) => _completion.TrySetException(exception);

        protected abstract ValueTask<T> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences);
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
    }

    private sealed class CreateSessionOp(SessionInfo session, CancellationToken ct) : WriteOp<SessionInfo>(ct)
    {
        protected override async ValueTask<SessionInfo> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO sessions ({SessionColumns}, last_active_ticks)
                VALUES ($id, $ws, $title, $at, $project, $worktree, $branch, $parent, $kind, $task, $status, $active, $ticks)
                """;
            command.Parameters.AddWithValue("$id", session.Id);
            command.Parameters.AddWithValue("$ws", session.WorkspacePath);
            command.Parameters.AddWithValue("$title", session.Title);
            command.Parameters.AddWithValue("$at", StorageDb.FormatTimestamp(session.CreatedAt));
            command.Parameters.AddWithValue("$project", (object?)session.ProjectId ?? DBNull.Value);
            command.Parameters.AddWithValue("$worktree", (object?)session.WorktreePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$branch", (object?)session.Branch ?? DBNull.Value);
            command.Parameters.AddWithValue("$parent", (object?)session.ParentSessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$kind", (object?)session.AgentKind ?? DBNull.Value);
            command.Parameters.AddWithValue("$task", (object?)session.Task ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", StatusName(session.Status));
            command.Parameters.AddWithValue("$active", StorageDb.FormatTimestamp(session.LastActiveAt));
            command.Parameters.AddWithValue("$ticks", StorageDb.TimestampTicks(session.LastActiveAt));
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            return session;
        }
    }

    private sealed class UpdateSessionOp(string sql, string value, string sessionId, CancellationToken ct) : WriteOp<bool>(ct)
    {
        protected override async ValueTask<bool> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, SeqAllocator sequences)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$id", sessionId);
            return await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false) > 0;
        }
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
                await store.InsertSearchRowAsync(connection, transaction, sessionId, seq, text).ConfigureAwait(false);
            }

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
