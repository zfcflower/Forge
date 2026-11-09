using Microsoft.Data.Sqlite;

namespace Forge.Workspace;

public enum ReviewMarkKind { Viewed = 0, Kept = 1 }

/// <summary>
/// A persisted per-session review mark. <see cref="ItemHash"/> is content-addressed: for <see cref="ReviewMarkKind.Viewed"/>
/// it is <see cref="DiffBlocks.Fingerprint"/> of the file diff, for <see cref="ReviewMarkKind.Kept"/> the
/// <see cref="ChangeBlock.Hash"/> of the kept block. A changed diff simply no longer matches any stored mark.
/// </summary>
public sealed record ReviewMark(string WorkspacePath, string SessionId, ReviewMarkKind Kind, string FilePath, string ItemHash, DateTimeOffset CreatedAt);

public sealed partial class ReviewStore
{
    /// <summary>All marks of one session. File paths are workspace-relative with '/' separators.</summary>
    public async Task<IReadOnlyList<ReviewMark>> GetMarksAsync(string workspacePath, string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind, file_path, item_hash, created_at FROM review_marks
            WHERE workspace_path = $workspace AND session_id = $session
            ORDER BY created_at, file_path, item_hash
            """;
        command.Parameters.AddWithValue("$workspace", workspacePath);
        command.Parameters.AddWithValue("$session", sessionId);
        var marks = new List<ReviewMark>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            marks.Add(new ReviewMark(
                workspacePath,
                sessionId,
                (ReviewMarkKind)reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseTimestamp(reader.GetString(3))));
        }

        return marks;
    }

    /// <summary>Idempotent insert of one mark (same workspace/session/kind/path/hash = no-op).</summary>
    public Task SetMarkAsync(string workspacePath, string sessionId, ReviewMarkKind kind, string filePath, string itemHash, CancellationToken ct = default) =>
        SetMarksAsync(workspacePath, sessionId, kind, [(filePath, itemHash)], ct);

    /// <summary>Inserts many marks in one transaction (used by "mark all viewed").</summary>
    public async Task SetMarksAsync(string workspacePath, string sessionId, ReviewMarkKind kind, IReadOnlyList<(string FilePath, string ItemHash)> items, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return;
        }

        foreach (var (path, hash) in items)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        }

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO review_marks (workspace_path, session_id, kind, file_path, item_hash, created_at)
            VALUES ($workspace, $session, $kind, $file, $hash, $now)
            """;
        command.Parameters.AddWithValue("$workspace", workspacePath);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$kind", (int)kind);
        var file = command.Parameters.Add("$file", SqliteType.Text);
        var hashParameter = command.Parameters.Add("$hash", SqliteType.Text);
        command.Parameters.AddWithValue("$now", FormatTimestamp(DateTimeOffset.UtcNow));
        foreach (var (path, hash) in items)
        {
            file.Value = NormalizeMarkPath(path);
            hashParameter.Value = hash;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Removes one mark; returns false when it did not exist.</summary>
    public async Task<bool> ClearMarkAsync(string workspacePath, string sessionId, ReviewMarkKind kind, string filePath, string itemHash, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemHash);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM review_marks
            WHERE workspace_path = $workspace AND session_id = $session AND kind = $kind AND file_path = $file AND item_hash = $hash
            """;
        command.Parameters.AddWithValue("$workspace", workspacePath);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$file", NormalizeMarkPath(filePath));
        command.Parameters.AddWithValue("$hash", itemHash);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    private static string NormalizeMarkPath(string path) => path.Replace('\\', '/');
}
