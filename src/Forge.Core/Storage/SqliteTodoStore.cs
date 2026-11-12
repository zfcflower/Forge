using Forge.Abstractions;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Storage;

/// <summary>
/// Per-session checklist in <c>forge.db</c>. <see cref="TodoItem.Order"/> is dense and zero-based inside the item's own
/// layer - the session's loose queue (a null <see cref="TodoItem.GroupId"/>) or one group - and every insert, removal and
/// move rewrites the orders of that layer only, in the same transaction: callers never have to compact gaps, two items of
/// one layer never share an order, and editing one layer never renumbers another. <see cref="TodoGroup.Order"/> is dense
/// and zero-based across the session's groups. Writing <see cref="TodoStatus.InProgress"/> also returns the session's
/// other in-progress item to <see cref="TodoStatus.Pending"/> in the same transaction, so one step runs at a time
/// whichever entry wrote it. <see cref="Changed"/> is raised after the commit, outside the writer lock, so a subscriber
/// cannot deadlock the store or observe a row that is not durable yet.
/// </summary>
public sealed class SqliteTodoStore : ITodoStore, IAsyncDisposable, IDisposable
{
    private const string InsertColumns =
        "id, session_id, title, status, note, order_index, updated_at, group_id, origin";

    private const string Columns = InsertColumns + ", commit_subject, summary, verification, commit_id, commit_error";

    private const string GroupColumns = "id, session_id, title, order_index, updated_at";

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

    /// <summary>Every item of the session - loose queue and group steps alike - ordered by <c>order_index</c>, then id.</summary>
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

    /// <summary>
    /// The plain add of the pre-group contract: appends to the session's loose queue, or right after
    /// <paramref name="afterId"/> (a loose item) when given, and records the agent as the origin.
    /// </summary>
    public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct) =>
        AddAsync(new TodoAddRequest(sessionId, title, note, afterId), ct);

    /// <summary>
    /// Appends to the request's layer - the loose queue, or the group it names - or inserts right after
    /// <paramref name="request"/>.<see cref="TodoAddRequest.AfterId"/> when given. The group must exist in the same
    /// session and the anchor item must live in the same layer, otherwise nothing is written and an
    /// <see cref="ArgumentException"/> is thrown.
    /// </summary>
    public async Task<TodoItem> AddAsync(TodoAddRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        StoragePaths.SafeSegment(request.SessionId, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        var item = await _writer.WriteAsync(
            async (connection, transaction) =>
            {
                await EnsureGroupAsync(connection, transaction, request.SessionId, request.GroupId, nameof(request), ct)
                    .ConfigureAwait(false);
                var count = await CountLayerAsync(connection, transaction, request.SessionId, request.GroupId, ct)
                    .ConfigureAwait(false);
                var order = count;
                if (request.AfterId is not null)
                {
                    var after = await FindAsync(connection, transaction, request.AfterId, ct).ConfigureAwait(false);
                    if (after is null || !string.Equals(after.SessionId, request.SessionId, StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            $"'{request.AfterId}' is not a todo of session '{request.SessionId}'.",
                            nameof(request));
                    }

                    if (!string.Equals(after.GroupId, request.GroupId, StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            $"'{request.AfterId}' is not in the same layer as the new item.",
                            nameof(request));
                    }

                    order = after.Order + 1;
                    await ShiftAsync(connection, transaction, request.SessionId, request.GroupId, order, 1, ct)
                        .ConfigureAwait(false);
                }

                var created = new TodoItem(
                    Guid.NewGuid().ToString("n"),
                    request.SessionId,
                    request.Title,
                    TodoStatus.Pending,
                    request.Note,
                    order,
                    DateTimeOffset.UtcNow,
                    GroupId: request.GroupId,
                    Origin: request.Origin);
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"INSERT INTO todos ({InsertColumns}) VALUES ($id, $session, $title, $status, $note, $order, $at, $group, $origin)",
                    ct,
                    ("$id", created.Id),
                    ("$session", created.SessionId),
                    ("$title", created.Title),
                    ("$status", StatusName(created.Status)),
                    ("$note", created.Note ?? (object)DBNull.Value),
                    ("$order", created.Order),
                    ("$at", StorageDb.FormatTimestamp(created.UpdatedAt)),
                    ("$group", created.GroupId ?? (object)DBNull.Value),
                    ("$origin", OriginName(created.Origin))).ConfigureAwait(false);
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
    /// leaves that column unchanged; an empty string clears it (stored as NULL). Moving a step from Done to any other
    /// status also clears its commit result (<see cref="TodoItem.CommitId"/> and <see cref="TodoItem.CommitError"/>) in
    /// the same write: a reopened step has no commit outcome any more, and finishing it again commits it afresh. A status
    /// that stays Done, or is not given, leaves the commit result alone. An item that ends up
    /// <see cref="TodoStatus.InProgress"/> sends the session's other in-progress item back to
    /// <see cref="TodoStatus.Pending"/> in the same transaction.
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

                var reopened = existing.Status == TodoStatus.Done && status is { } next && next != TodoStatus.Done;
                var item = existing with
                {
                    Title = title ?? existing.Title,
                    Status = status ?? existing.Status,
                    Note = note ?? existing.Note,
                    CommitSubject = MergeStepField(step?.CommitSubject, existing.CommitSubject),
                    Summary = MergeStepField(step?.Summary, existing.Summary),
                    Verification = MergeStepField(step?.Verification, existing.Verification),
                    CommitId = reopened ? null : existing.CommitId,
                    CommitError = reopened ? null : existing.CommitError,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                await ExecuteAsync(
                    connection,
                    transaction,
                    "UPDATE todos SET title = $title, status = $status, note = $note, commit_subject = $subject, summary = $summary, verification = $verification, commit_id = $commit, commit_error = $error, updated_at = $at WHERE id = $id",
                    ct,
                    ("$title", item.Title),
                    ("$status", StatusName(item.Status)),
                    ("$note", item.Note ?? (object)DBNull.Value),
                    ("$subject", item.CommitSubject ?? (object)DBNull.Value),
                    ("$summary", item.Summary ?? (object)DBNull.Value),
                    ("$verification", item.Verification ?? (object)DBNull.Value),
                    ("$commit", item.CommitId ?? (object)DBNull.Value),
                    ("$error", item.CommitError ?? (object)DBNull.Value),
                    ("$at", StorageDb.FormatTimestamp(item.UpdatedAt)),
                    ("$id", item.Id)).ConfigureAwait(false);
                if (item.Status == TodoStatus.InProgress)
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        "UPDATE todos SET status = $pending, updated_at = $at WHERE session_id = $session AND status = $running AND id <> $id",
                        ct,
                        ("$pending", StorageSchema.TodoPending),
                        ("$running", StorageSchema.TodoInProgress),
                        ("$at", StorageDb.FormatTimestamp(item.UpdatedAt)),
                        ("$session", item.SessionId),
                        ("$id", item.Id)).ConfigureAwait(false);
                }

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
                await using var command = StorageDb.Command(
                    connection, "SELECT session_id, text, updated_at FROM plan_summaries WHERE session_id = $session");
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

    /// <summary>The session's groups - the named plans the steps belong to - ordered by <c>order_index</c>, then id.</summary>
    public Task<IReadOnlyList<TodoGroup>> ListGroupsAsync(string sessionId, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        return _writer.ReadAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT {GroupColumns} FROM todo_groups WHERE session_id = $session ORDER BY order_index, id";
                command.Parameters.AddWithValue("$session", sessionId);
                var groups = new List<TodoGroup>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    groups.Add(ReadGroup(reader));
                }

                return (IReadOnlyList<TodoGroup>)groups;
            },
            ct);
    }

    /// <summary>
    /// The session's group whose title is <paramref name="title"/> after trimming (compared byte for byte, i.e. SQLite's
    /// BINARY collation), or a new empty group appended after the existing ones. Raising <see cref="Changed"/> happens
    /// only when a group was created: looking an existing one up writes nothing.
    /// </summary>
    public async Task<TodoGroup> FindOrCreateGroupAsync(string sessionId, string title, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var wanted = title.Trim();
        var (group, created) = await _writer.WriteAsync(
            async (connection, transaction) =>
            {
                var existing = await FindGroupByTitleAsync(connection, transaction, sessionId, wanted, ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    return (existing, false);
                }

                var order = await CountGroupsAsync(connection, transaction, sessionId, ct).ConfigureAwait(false);
                var appended = new TodoGroup(
                    Guid.NewGuid().ToString("n"),
                    sessionId,
                    wanted,
                    order,
                    DateTimeOffset.UtcNow);
                await InsertGroupAsync(connection, transaction, appended, ct).ConfigureAwait(false);
                return (appended, true);
            },
            ct);

        if (created)
        {
            RaiseChanged(group.SessionId);
        }

        return group;
    }

    /// <summary>
    /// Turns a loose item of the session's queue into a group of its own: the group takes over the item's id, and its
    /// title unless <paramref name="title"/> (trimmed) replaces it; the item itself leaves the queue, which is renumbered.
    /// A missing item, or one that already is a step of a group, returns null and writes nothing.
    /// </summary>
    public async Task<TodoGroup?> PromoteAsync(string itemId, string? title, CancellationToken ct)
    {
        StoragePaths.SafeSegment(itemId, nameof(itemId));
        if (title is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
        }

        var promoted = await _writer.WriteAsync<TodoGroup?>(
            async (connection, transaction) =>
            {
                var existing = await FindAsync(connection, transaction, itemId, ct).ConfigureAwait(false);
                if (existing is null || existing.GroupId is not null)
                {
                    return null;
                }

                var order = await CountGroupsAsync(connection, transaction, existing.SessionId, ct).ConfigureAwait(false);
                var group = new TodoGroup(
                    existing.Id,
                    existing.SessionId,
                    title?.Trim() ?? existing.Title,
                    order,
                    DateTimeOffset.UtcNow);
                await InsertGroupAsync(connection, transaction, group, ct).ConfigureAwait(false);
                await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM todos WHERE id = $id",
                    ct,
                    ("$id", existing.Id)).ConfigureAwait(false);
                await DensifyAsync(connection, transaction, existing.SessionId, null, ct).ConfigureAwait(false);
                return group;
            },
            ct);

        if (promoted is null)
        {
            return null;
        }

        RaiseChanged(promoted.SessionId);
        return promoted;
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
                await DensifyAsync(connection, transaction, existing.SessionId, existing.GroupId, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Reorders the item inside its own layer - its group, or the loose queue - clamping <paramref name="order"/> to the
    /// layer, and leaves every other layer (and the order of the groups) untouched. Returns false for a missing item.
    /// </summary>
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

                var ids = await OrderedIdsAsync(connection, transaction, existing.SessionId, existing.GroupId, ct)
                    .ConfigureAwait(false);
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
        TodoStatus.Paused => StorageSchema.TodoPaused,
        TodoStatus.Done => StorageSchema.TodoDone,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown todo status."),
    };

    internal static TodoStatus ParseStatus(string value, string databasePath, string row) => value switch
    {
        StorageSchema.TodoPending => TodoStatus.Pending,
        StorageSchema.TodoInProgress => TodoStatus.InProgress,
        StorageSchema.TodoPaused => TodoStatus.Paused,
        StorageSchema.TodoDone => TodoStatus.Done,
        _ => throw StorageDb.Malformed(
            databasePath,
            "status",
            row,
            $"'{value}' is not one of Pending, InProgress, Paused, Done",
            null),
    };

    internal static string OriginName(TodoOrigin origin) => origin switch
    {
        TodoOrigin.Agent => StorageSchema.TodoOriginAgent,
        TodoOrigin.User => StorageSchema.TodoOriginUser,
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown todo origin."),
    };

    /// <summary>A row written before the origin column existed, or by a script that left it alone, belongs to the agent.</summary>
    internal static TodoOrigin ParseOrigin(string? value, string databasePath, string row) => value switch
    {
        null or StorageSchema.TodoOriginAgent => TodoOrigin.Agent,
        StorageSchema.TodoOriginUser => TodoOrigin.User,
        _ => throw StorageDb.Malformed(
            databasePath,
            "origin",
            row,
            $"'{value}' is not one of agent, user",
            null),
    };

    private TodoItem ReadItem(SqliteDataReader reader)
    {
        // Ordinals follow Columns: the eight insert columns, then the five step columns; the record takes the step
        // fields before its group id and origin, which is why those two are read last.
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
            StorageDb.ReadOptionalText(reader, 9, "commit_subject", _databasePath, row),
            StorageDb.ReadOptionalText(reader, 10, "summary", _databasePath, row),
            StorageDb.ReadOptionalText(reader, 11, "verification", _databasePath, row),
            StorageDb.ReadOptionalText(reader, 12, "commit_id", _databasePath, row),
            StorageDb.ReadOptionalText(reader, 13, "commit_error", _databasePath, row),
            StorageDb.ReadOptionalText(reader, 7, "group_id", _databasePath, row),
            ParseOrigin(StorageDb.ReadOptionalText(reader, 8, "origin", _databasePath, row), _databasePath, row));
    }

    private TodoGroup ReadGroup(SqliteDataReader reader)
    {
        var id = StorageDb.ReadText(reader, 0, "id", _databasePath, "a todo group whose id cannot be read");
        var row = $"todo group {id}";
        return new TodoGroup(
            id,
            StorageDb.ReadText(reader, 1, "session_id", _databasePath, row),
            StorageDb.ReadText(reader, 2, "title", _databasePath, row),
            (int)StorageDb.ReadInt64(reader, 3, "order_index", _databasePath, row),
            StorageDb.ParseCanonicalTimestamp(
                StorageDb.ReadText(reader, 4, "updated_at", _databasePath, row),
                "updated_at",
                _databasePath,
                row));
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

    /// <summary>Rejects a group id that names no group of <paramref name="sessionId"/>; a null id is the loose queue.</summary>
    private static async Task EnsureGroupAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string? groupId,
        string paramName,
        CancellationToken ct)
    {
        if (groupId is null)
        {
            return;
        }

        await using var command = StorageDb.Command(connection, transaction, "SELECT session_id FROM todo_groups WHERE id = $id");
        command.Parameters.AddWithValue("$id", groupId);
        var owner = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (owner is not string stored || !string.Equals(stored, sessionId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{groupId}' is not a todo group of session '{sessionId}'.", paramName);
        }
    }

    private async Task<TodoGroup?> FindGroupByTitleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string title,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT {GroupColumns} FROM todo_groups WHERE session_id = $session AND title = $title ORDER BY order_index, id LIMIT 1";
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$title", title);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadGroup(reader) : null;
    }

    private static async Task InsertGroupAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TodoGroup group,
        CancellationToken ct) =>
        await ExecuteAsync(
            connection,
            transaction,
            $"INSERT INTO todo_groups ({GroupColumns}) VALUES ($id, $session, $title, $order, $at)",
            ct,
            ("$id", group.Id),
            ("$session", group.SessionId),
            ("$title", group.Title),
            ("$order", group.Order),
            ("$at", StorageDb.FormatTimestamp(group.UpdatedAt))).ConfigureAwait(false);

    private static Task<int> CountLayerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string? groupId,
        CancellationToken ct) =>
        CountRowsAsync(
            connection,
            transaction,
            "todos",
            "session_id = $session AND group_id IS $group",
            ct,
            ("$session", sessionId),
            ("$group", groupId ?? (object)DBNull.Value));

    private static Task<int> CountGroupsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        CancellationToken ct) =>
        CountRowsAsync(connection, transaction, "todo_groups", "session_id = $session", ct, ("$session", sessionId));

    private static async Task<int> CountRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string predicate,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {predicate}";
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var counted = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return (int)(counted is long count ? count : Convert.ToInt64(counted, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<List<string>> OrderedIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string? groupId,
        CancellationToken ct)
    {
        await using var command = StorageDb.Command(
            connection,
            transaction,
            "SELECT id FROM todos WHERE session_id = $session AND group_id IS $group ORDER BY order_index, id");
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$group", groupId ?? (object)DBNull.Value);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>Renumbers one layer 0..n-1, leaving every other layer and the group order alone.</summary>
    private static async Task DensifyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string? groupId,
        CancellationToken ct)
    {
        var ids = await OrderedIdsAsync(connection, transaction, sessionId, groupId, ct).ConfigureAwait(false);
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
        string? groupId,
        int fromOrder,
        int delta,
        CancellationToken ct) =>
        ExecuteAsync(
            connection,
            transaction,
            "UPDATE todos SET order_index = order_index + $delta WHERE session_id = $session AND group_id IS $group AND order_index >= $from",
            ct,
            ("$delta", delta),
            ("$session", sessionId),
            ("$group", groupId ?? (object)DBNull.Value),
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
