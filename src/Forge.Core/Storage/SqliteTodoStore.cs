using Forge.Abstractions;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Storage;

/// <summary>
/// Per-session checklist in <c>forge.db</c>. <see cref="TodoItem.Order"/> is dense and zero-based inside one session:
/// every insert, removal and move rewrites the affected orders in the same transaction, so callers never have to
/// compact gaps and two items never share an order. <see cref="Changed"/> is raised after the commit, outside the
/// writer lock, so a subscriber cannot deadlock the store or observe a row that is not durable yet.
/// </summary>
public sealed class SqliteTodoStore : ITodoStore, IAsyncDisposable, IDisposable
{
    private const string InsertColumns = "id, session_id, title, status, note, order_index, updated_at";

    private const string Columns = InsertColumns + ", commit_subject, summary, verification, commit_id, commit_error";

    private readonly SqliteWriter _writer;
    private readonly string _databasePath;
    private int _disposed;

    public SqliteTodoStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        _databasePath = StorageDb.DatabasePath(directory);
        _writer = new SqliteWriter(StorageDb.ConnectionString(_databasePath), _databasePath);
    }

    public SqliteTodoStore(SqliteConnectionStringBuilder connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentException.ThrowIfNullOrEmpty(connectionString.DataSource, nameof(connectionString));
        _databasePath = Path.GetFullPath(connectionString.DataSource);
        _writer = new SqliteWriter(StorageDb.ConnectionString(connectionString), _databasePath);
    }

    public event Action<string>? Changed;

    public string ConnectionString => _writer.ConnectionString;

    /// <summary>Full path of the database file this store reads and writes.</summary>
    public string DatabasePath => _databasePath;

    public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        return _writer.ReadAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {Columns} FROM todos WHERE session_id = $session ORDER BY order_index, id";
                command.Parameters.AddWithValue("$session", sessionId);
                var items = new List<TodoItem>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    items.Add(ReadItem(reader));
                }

                return (IReadOnlyList<TodoItem>)items;
            },
            ct);
    }

    public async Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var item = await _writer.WriteAsync(
            async (connection, transaction) =>
            {
                var count = await CountAsync(connection, transaction, sessionId, ct).ConfigureAwait(false);
                var order = count;
                if (afterId is not null)
                {
                    var after = await FindAsync(connection, transaction, afterId, ct).ConfigureAwait(false);
                    if (after is null || !string.Equals(after.SessionId, sessionId, StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            $"'{afterId}' is not a todo of session '{sessionId}'.",
                            nameof(afterId));
                    }

                    order = after.Order + 1;
                    await ShiftAsync(connection, transaction, sessionId, order, 1, ct).ConfigureAwait(false);
                }

                var created = new TodoItem(
                    Guid.NewGuid().ToString("n"),
                    sessionId,
                    title,
                    TodoStatus.Pending,
                    note,
                    order,
                    DateTimeOffset.UtcNow);
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"INSERT INTO todos ({InsertColumns}) VALUES ($id, $session, $title, $status, $note, $order, $at)",
                    ct,
                    ("$id", created.Id),
                    ("$session", created.SessionId),
                    ("$title", created.Title),
                    ("$status", StatusName(created.Status)),
                    ("$note", created.Note ?? (object)DBNull.Value),
                    ("$order", created.Order),
                    ("$at", StorageDb.FormatTimestamp(created.UpdatedAt))).ConfigureAwait(false);
                return created;
            },
            ct);

        RaiseChanged(item.SessionId);
        return item;
    }

    public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct) =>
        UpdateAsync(id, title, status, note, null, ct);

    /// <summary>
    /// The plain update plus the agent-written step fields in the same write. A null property of <paramref name="step"/>
    /// leaves that column unchanged; an empty string clears it (stored as NULL).
    /// </summary>
    public async Task<TodoItem?> UpdateAsync(
        string id,
        string? title,
        TodoStatus? status,
        string? note,
        TodoStepFields? step,
        CancellationToken ct)
    {
        StoragePaths.SafeSegment(id, nameof(id));
        if (title is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
        }

        var updated = await _writer.WriteAsync<TodoItem?>(
            async (connection, transaction) =>
            {
                var existing = await FindAsync(connection, transaction, id, ct).ConfigureAwait(false);
                if (existing is null)
                {
                    return null;
                }

                var item = existing with
                {
                    Title = title ?? existing.Title,
                    Status = status ?? existing.Status,
                    Note = note ?? existing.Note,
                    CommitSubject = MergeStepField(step?.CommitSubject, existing.CommitSubject),
                    Summary = MergeStepField(step?.Summary, existing.Summary),
                    Verification = MergeStepField(step?.Verification, existing.Verification),
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                await ExecuteAsync(
                    connection,
                    transaction,
                    "UPDATE todos SET title = $title, status = $status, note = $note, commit_subject = $subject, summary = $summary, verification = $verification, updated_at = $at WHERE id = $id",
                    ct,
                    ("$title", item.Title),
                    ("$status", StatusName(item.Status)),
                    ("$note", item.Note ?? (object)DBNull.Value),
                    ("$subject", item.CommitSubject ?? (object)DBNull.Value),
                    ("$summary", item.Summary ?? (object)DBNull.Value),
                    ("$verification", item.Verification ?? (object)DBNull.Value),
                    ("$at", StorageDb.FormatTimestamp(item.UpdatedAt)),
                    ("$id", item.Id)).ConfigureAwait(false);
                return item;
            },
            ct);

        if (updated is null)
        {
            return null;
        }

        RaiseChanged(updated.SessionId);
        return updated;
    }

    /// <summary>
    /// Stores the outcome of a step commit: the commit id or the failure reason, never both (an empty string counts as
    /// absent). Touches no other column except <c>updated_at</c>.
    /// </summary>
    public async Task<TodoItem?> SetCommitResultAsync(string id, string? commitId, string? commitError, CancellationToken ct)
    {
        StoragePaths.SafeSegment(id, nameof(id));
        var sha = NullIfEmpty(commitId);
        var error = NullIfEmpty(commitError);
        if (sha is not null && error is not null)
        {
            throw new ArgumentException("A step commit result is either a commit id or an error, not both.", nameof(commitError));
        }

        var updated = await _writer.WriteAsync<TodoItem?>(
            async (connection, transaction) =>
            {
                var existing = await FindAsync(connection, transaction, id, ct).ConfigureAwait(false);
                if (existing is null)
                {
                    return null;
                }

                var item = existing with { CommitId = sha, CommitError = error, UpdatedAt = DateTimeOffset.UtcNow };
                await ExecuteAsync(
                    connection,
                    transaction,
                    "UPDATE todos SET commit_id = $commit, commit_error = $error, updated_at = $at WHERE id = $id",
                    ct,
                    ("$commit", item.CommitId ?? (object)DBNull.Value),
                    ("$error", item.CommitError ?? (object)DBNull.Value),
                    ("$at", StorageDb.FormatTimestamp(item.UpdatedAt)),
                    ("$id", item.Id)).ConfigureAwait(false);
                return item;
            },
            ct);

        if (updated is null)
        {
            return null;
        }

        RaiseChanged(updated.SessionId);
        return updated;
    }

    public Task<PlanSummary?> GetPlanSummaryAsync(string sessionId, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        return _writer.ReadAsync<PlanSummary?>(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT session_id, text, updated_at FROM plan_summaries WHERE session_id = $session";
                command.Parameters.AddWithValue("$session", sessionId);
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }

                var row = $"plan summary of session {sessionId}";
                return new PlanSummary(
                    StorageDb.ReadText(reader, 0, "session_id", _databasePath, row),
                    StorageDb.ReadText(reader, 1, "text", _databasePath, row),
                    StorageDb.ParseCanonicalTimestamp(
                        StorageDb.ReadText(reader, 2, "updated_at", _databasePath, row),
                        "updated_at",
                        _databasePath,
                        row));
            },
            ct);
    }

    /// <summary>Upserts the session's plan summary and raises <see cref="Changed"/> after the commit.</summary>
    public async Task<PlanSummary> SetPlanSummaryAsync(string sessionId, string text, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var summary = await _writer.WriteAsync(
            async (connection, transaction) =>
            {
                var written = new PlanSummary(sessionId, text, DateTimeOffset.UtcNow);
                await ExecuteAsync(
                    connection,
                    transaction,
                    "INSERT INTO plan_summaries (session_id, text, updated_at) VALUES ($session, $text, $at) ON CONFLICT (session_id) DO UPDATE SET text = excluded.text, updated_at = excluded.updated_at",
                    ct,
                    ("$session", written.SessionId),
                    ("$text", written.Text),
                    ("$at", StorageDb.FormatTimestamp(written.UpdatedAt))).ConfigureAwait(false);
                return written;
            },
            ct);

        RaiseChanged(summary.SessionId);
        return summary;
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken ct)
    {
        StoragePaths.SafeSegment(id, nameof(id));
        var sessionId = await _writer.WriteAsync<string?>(
            async (connection, transaction) =>
            {
                var existing = await FindAsync(connection, transaction, id, ct).ConfigureAwait(false);
                if (existing is null)
                {
                    return null;
                }

                await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM todos WHERE id = $id",
                    ct,
                    ("$id", id)).ConfigureAwait(false);
                await DensifyAsync(connection, transaction, existing.SessionId, ct).ConfigureAwait(false);
                return existing.SessionId;
            },
            ct);

        if (sessionId is null)
        {
            return false;
        }

        RaiseChanged(sessionId);
        return true;
    }

    public async Task<bool> MoveAsync(string id, int order, CancellationToken ct)
    {
        StoragePaths.SafeSegment(id, nameof(id));
        ArgumentOutOfRangeException.ThrowIfNegative(order);
        var sessionId = await _writer.WriteAsync<string?>(
            async (connection, transaction) =>
            {
                var existing = await FindAsync(connection, transaction, id, ct).ConfigureAwait(false);
                if (existing is null)
                {
                    return null;
                }

                var ids = await OrderedIdsAsync(connection, transaction, existing.SessionId, ct).ConfigureAwait(false);
                if (!ids.Remove(id))
                {
                    return null;
                }

                ids.Insert(Math.Min(order, ids.Count), id);
                for (var index = 0; index < ids.Count; index++)
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        "UPDATE todos SET order_index = $order WHERE id = $id",
                        ct,
                        ("$order", index),
                        ("$id", ids[index])).ConfigureAwait(false);
                }

                return existing.SessionId;
            },
            ct);

        if (sessionId is null)
        {
            return false;
        }

        RaiseChanged(sessionId);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _writer.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _writer.Dispose();
    }

    internal static string StatusName(TodoStatus status) => status switch
    {
        TodoStatus.Pending => StorageSchema.TodoPending,
        TodoStatus.InProgress => StorageSchema.TodoInProgress,
        TodoStatus.Done => StorageSchema.TodoDone,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown todo status."),
    };

    internal static TodoStatus ParseStatus(string value, string databasePath, string row) => value switch
    {
        StorageSchema.TodoPending => TodoStatus.Pending,
        StorageSchema.TodoInProgress => TodoStatus.InProgress,
        StorageSchema.TodoDone => TodoStatus.Done,
        _ => throw StorageDb.Malformed(databasePath, "status", row, $"'{value}' is not one of Pending, InProgress, Done", null),
    };

    private TodoItem ReadItem(SqliteDataReader reader)
    {
        var id = StorageDb.ReadText(reader, 0, "id", _databasePath, "a todo whose id cannot be read");
        var row = $"todo {id}";
        return new TodoItem(
            id,
            StorageDb.ReadText(reader, 1, "session_id", _databasePath, row),
            StorageDb.ReadText(reader, 2, "title", _databasePath, row),
            ParseStatus(StorageDb.ReadText(reader, 3, "status", _databasePath, row), _databasePath, row),
            StorageDb.ReadOptionalText(reader, 4, "note", _databasePath, row),
            (int)StorageDb.ReadInt64(reader, 5, "order_index", _databasePath, row),
            StorageDb.ParseCanonicalTimestamp(
                StorageDb.ReadText(reader, 6, "updated_at", _databasePath, row),
                "updated_at",
                _databasePath,
                row),
            StorageDb.ReadOptionalText(reader, 7, "commit_subject", _databasePath, row),
            StorageDb.ReadOptionalText(reader, 8, "summary", _databasePath, row),
            StorageDb.ReadOptionalText(reader, 9, "verification", _databasePath, row),
            StorageDb.ReadOptionalText(reader, 10, "commit_id", _databasePath, row),
            StorageDb.ReadOptionalText(reader, 11, "commit_error", _databasePath, row));
    }

    /// <summary>Null keeps the stored value, an empty string clears it, anything else replaces it.</summary>
    private static string? MergeStepField(string? incoming, string? existing) =>
        incoming is null ? existing : incoming.Length == 0 ? null : incoming;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private async Task<TodoItem?> FindAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM todos WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadItem(reader) : null;
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM todos WHERE session_id = $session";
        command.Parameters.AddWithValue("$session", sessionId);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return (int)(value is long count ? count : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<List<string>> OrderedIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM todos WHERE session_id = $session ORDER BY order_index, id";
        command.Parameters.AddWithValue("$session", sessionId);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task DensifyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        CancellationToken ct)
    {
        var ids = await OrderedIdsAsync(connection, transaction, sessionId, ct).ConfigureAwait(false);
        for (var index = 0; index < ids.Count; index++)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "UPDATE todos SET order_index = $order WHERE id = $id AND order_index <> $order",
                ct,
                ("$order", index),
                ("$id", ids[index])).ConfigureAwait(false);
        }
    }

    private static Task<int> ShiftAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        int fromOrder,
        int delta,
        CancellationToken ct) =>
        ExecuteAsync(
            connection,
            transaction,
            "UPDATE todos SET order_index = order_index + $delta WHERE session_id = $session AND order_index >= $from",
            ct,
            ("$delta", delta),
            ("$session", sessionId),
            ("$from", fromOrder));

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (parameterName, value) in parameters)
        {
            command.Parameters.AddWithValue(parameterName, value);
        }

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private void RaiseChanged(string sessionId)
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<string>)handler)(sessionId);
            }
            catch (Exception)
            {
                // A subscriber failure must not fail a write that already committed.
            }
        }
    }
}
