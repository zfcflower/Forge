using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Forge.Workspace;

/// <summary>
/// Review comments for every workspace in one SQLite database (WAL, outside the repository). Writes are short
/// transactions on pooled connections, so concurrent sessions do not serialize on a shared writer.
/// </summary>
public sealed class ReviewStore
{
    public const string DatabaseFileName = "reviews.db";
    private const int CommandTimeoutSeconds = 30;
    private const int MaxQueryLimit = 5000;

    private const string Columns =
        "id, workspace_path, session_id, file_path, new_line_start, new_line_end, body, status, is_resolved, created_at, updated_at, sent_at, base_commit, anchor_json";

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS review_comments (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            workspace_path TEXT NOT NULL,
            session_id TEXT NOT NULL,
            file_path TEXT NOT NULL,
            new_line_start INTEGER NOT NULL,
            new_line_end INTEGER NOT NULL,
            body TEXT NOT NULL,
            status TEXT NOT NULL,
            is_resolved INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            sent_at TEXT NULL,
            base_commit TEXT NULL,
            anchor_json TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_review_comments_scope ON review_comments (workspace_path, session_id, status);
        CREATE INDEX IF NOT EXISTS ix_review_comments_file ON review_comments (workspace_path, session_id, file_path);
        """;

    private readonly string dataDirectory;
    private readonly SemaphoreSlim initGate = new(1, 1);
    private readonly string connectionString;
    private bool initialized;

    public ReviewStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        DatabasePath = Path.Combine(this.dataDirectory, DatabaseFileName);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
            DefaultTimeout = CommandTimeoutSeconds,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task<ReviewComment> AddAsync(NewReviewComment comment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentException.ThrowIfNullOrWhiteSpace(comment.WorkspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(comment.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(comment.FilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(comment.Body);
        ArgumentOutOfRangeException.ThrowIfLessThan(comment.NewLineStart, 1);
        var end = comment.NewLineEnd ?? comment.NewLineStart;
        ArgumentOutOfRangeException.ThrowIfLessThan(end, comment.NewLineStart);

        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO review_comments
                (workspace_path, session_id, file_path, new_line_start, new_line_end, body, status, is_resolved,
                 created_at, updated_at, sent_at, base_commit, anchor_json)
            VALUES ($workspace, $session, $file, $start, $end, $body, $status, 0, $now, $now, NULL, $base, $anchor)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$workspace", comment.WorkspacePath);
        command.Parameters.AddWithValue("$session", comment.SessionId);
        command.Parameters.AddWithValue("$file", comment.FilePath);
        command.Parameters.AddWithValue("$start", comment.NewLineStart);
        command.Parameters.AddWithValue("$end", end);
        command.Parameters.AddWithValue("$body", comment.Body);
        command.Parameters.AddWithValue("$status", StatusText(ReviewCommentStatus.Draft));
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$base", (object?)comment.BaseCommit ?? DBNull.Value);
        command.Parameters.AddWithValue("$anchor", comment.Anchor is null ? DBNull.Value : JsonSerializer.Serialize(comment.Anchor, WorkspaceJsonContext.Default.DiffAnchor));

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return (await GetAsync(id, ct).ConfigureAwait(false))!;
    }

    public async Task<ReviewComment?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM review_comments WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadComment(reader) : null;
    }

    /// <summary>Queries comments by workspace, session, file, status and resolution state; newest first.</summary>
    public async Task<IReadOnlyList<ReviewComment>> QueryAsync(ReviewQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var where = new List<string>();
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (!string.IsNullOrWhiteSpace(query.WorkspacePath))
        {
            where.Add("workspace_path = $workspace");
            command.Parameters.AddWithValue("$workspace", query.WorkspacePath);
        }

        if (!string.IsNullOrWhiteSpace(query.SessionId))
        {
            where.Add("session_id = $session");
            command.Parameters.AddWithValue("$session", query.SessionId);
        }

        if (!string.IsNullOrWhiteSpace(query.FilePath))
        {
            where.Add("file_path = $file");
            command.Parameters.AddWithValue("$file", query.FilePath);
        }

        if (query.Status is { } status)
        {
            where.Add("status = $status");
            command.Parameters.AddWithValue("$status", StatusText(status));
        }

        if (query.IsResolved is { } resolved)
        {
            where.Add("is_resolved = $resolved");
            command.Parameters.AddWithValue("$resolved", resolved ? 1 : 0);
        }

        var limit = Math.Clamp(query.Limit, 1, MaxQueryLimit);
        command.CommandText =
            $"SELECT {Columns} FROM review_comments" +
            (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty) +
            " ORDER BY created_at DESC, id DESC LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));

        var comments = new List<ReviewComment>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            comments.Add(ReadComment(reader));
        }

        return comments;
    }

    public Task<IReadOnlyList<ReviewComment>> GetDraftsAsync(string workspacePath, string sessionId, CancellationToken ct = default) =>
        QueryAsync(new ReviewQuery
        {
            WorkspacePath = workspacePath,
            SessionId = sessionId,
            Status = ReviewCommentStatus.Draft,
            Limit = MaxQueryLimit,
        }, ct);

    public async Task<bool> UpdateBodyAsync(long id, string body, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE review_comments SET body = $body, updated_at = $now WHERE id = $id";
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$now", FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<bool> SetResolvedAsync(long id, bool resolved, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE review_comments SET is_resolved = $resolved, updated_at = $now WHERE id = $id";
        command.Parameters.AddWithValue("$resolved", resolved ? 1 : 0);
        command.Parameters.AddWithValue("$now", FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM review_comments WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <summary>Marks drafts as sent in one statement and returns the updated rows, in the order given.</summary>
    public async Task<IReadOnlyList<ReviewComment>> MarkSentAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var names = new List<string>(ids.Count);
        var order = new Dictionary<long, int>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            var name = "$id" + i.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            command.Parameters.AddWithValue(name, ids[i]);
            order[ids[i]] = i;
        }

        command.CommandText =
            $"UPDATE review_comments SET status = $sent, sent_at = $now, updated_at = $now WHERE status = $draft AND id IN ({string.Join(", ", names)})";
        command.Parameters.AddWithValue("$sent", StatusText(ReviewCommentStatus.Sent));
        command.Parameters.AddWithValue("$draft", StatusText(ReviewCommentStatus.Draft));
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var updated = new List<ReviewComment>(ids.Count);
        command.CommandText = $"SELECT {Columns} FROM review_comments WHERE id IN ({string.Join(", ", names)})";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            updated.Add(ReadComment(reader));
        }

        return updated.OrderBy(comment => order.TryGetValue(comment.Id, out var position) ? position : int.MaxValue).ToArray();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (initialized)
        {
            return;
        }

        await initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            Directory.CreateDirectory(dataDirectory);
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            initGate.Release();
        }
    }

    private static ReviewComment ReadComment(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt32(4),
        reader.GetInt32(5),
        reader.GetString(6),
        ParseStatus(reader.GetString(7)),
        reader.GetInt64(8) != 0,
        ParseTimestamp(reader.GetString(9)),
        ParseTimestamp(reader.GetString(10)),
        reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11)),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : JsonSerializer.Deserialize(reader.GetString(13), WorkspaceJsonContext.Default.DiffAnchor));

    private static string StatusText(ReviewCommentStatus status) => status == ReviewCommentStatus.Draft ? "draft" : "sent";

    private static ReviewCommentStatus ParseStatus(string text) =>
        string.Equals(text, "sent", StringComparison.Ordinal) ? ReviewCommentStatus.Sent : ReviewCommentStatus.Draft;

    private static string FormatTimestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
