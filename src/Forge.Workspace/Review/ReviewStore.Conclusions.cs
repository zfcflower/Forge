using Microsoft.Data.Sqlite;

namespace Forge.Workspace;

/// <summary>
/// How a recorded review ended (wave W17b). The order matches Core's <c>ReviewConclusionOutcome</c> one for one; the
/// application maps between the two, because Workspace sits below Core and cannot name its types.
/// </summary>
public enum ReviewConclusionOutcome
{
    /// <summary>The reviewer answered without a verdict line: never an approval.</summary>
    Unstructured = 0,

    /// <summary>The reviewer approved.</summary>
    Approve = 1,

    /// <summary>The reviewer asked for changes.</summary>
    RequestChanges = 2,

    /// <summary>The reviewer was stopped before it finished.</summary>
    Stopped = 3,

    /// <summary>The review could not be started, or the reviewer returned no text.</summary>
    Failed = 4,
}

/// <summary>One recorded review of a session's change set, as the store read it back.</summary>
public sealed record ReviewConclusion(
    long Id,
    string WorkspacePath,
    string SessionId,
    string? StepId,
    string? BaseCommit,
    string? HeadCommit,
    ReviewConclusionOutcome Outcome,
    string Text,
    DateTimeOffset CreatedAt);

/// <summary>A review to record; the store assigns <see cref="ReviewConclusion.Id"/> and the timestamp.</summary>
public sealed record NewReviewConclusion
{
    /// <summary>The session's working directory the review read its change set from.</summary>
    public required string WorkspacePath { get; init; }

    public required string SessionId { get; init; }

    /// <summary>The checklist step whose own commit was reviewed; null for a branch review.</summary>
    public string? StepId { get; init; }

    /// <summary>The From-side revision of the reviewed diff: a step review stores the commit's first parent
    /// (null for a root commit), a branch review stores the merge-base; null when unknown.</summary>
    public string? BaseCommit { get; init; }

    /// <summary>The commit the review read, when the caller could name it.</summary>
    public string? HeadCommit { get; init; }

    public required ReviewConclusionOutcome Outcome { get; init; }

    /// <summary>The reviewer's text, or the reason a failed review did not run.</summary>
    public required string Text { get; init; }
}

/// <summary>
/// The <c>review_conclusions</c> half of <c>reviews.db</c> (wave W17b): one row per finished <c>request_review</c>, so a
/// verdict survives the turn that produced it. Rows belong to a session like the comments and marks do, so a session copy
/// carries them over and a purge removes them.
/// </summary>
public sealed partial class ReviewStore
{
    private const string ConclusionColumns =
        "id, workspace_path, session_id, step_id, base_commit, head_commit, outcome, body, created_at";

    /// <summary>Records one finished review and returns it with the id and timestamp the database assigned.</summary>
    public async Task<ReviewConclusion> AddConclusionAsync(NewReviewConclusion conclusion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conclusion);
        ArgumentException.ThrowIfNullOrWhiteSpace(conclusion.WorkspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(conclusion.SessionId);

        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO review_conclusions
                (workspace_path, session_id, step_id, base_commit, head_commit, outcome, body, created_at)
            VALUES ($workspace, $session, $step, $base, $head, $outcome, $body, $now)
            RETURNING id, created_at;
            """;
        command.Parameters.AddWithValue("$workspace", conclusion.WorkspacePath);
        command.Parameters.AddWithValue("$session", conclusion.SessionId);
        command.Parameters.AddWithValue("$step", (object?)conclusion.StepId ?? DBNull.Value);
        command.Parameters.AddWithValue("$base", (object?)conclusion.BaseCommit ?? DBNull.Value);
        command.Parameters.AddWithValue("$head", (object?)conclusion.HeadCommit ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", (int)conclusion.Outcome);
        command.Parameters.AddWithValue("$body", conclusion.Text);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // The insert either wrote a row or threw; a reader with no row cannot happen, and saying so is better than an
            // IndexOutOfRangeException from the conversion below.
            throw new InvalidOperationException("The review conclusion was not stored.");
        }

        return new ReviewConclusion(
            reader.GetInt64(0),
            conclusion.WorkspacePath,
            conclusion.SessionId,
            conclusion.StepId,
            conclusion.BaseCommit,
            conclusion.HeadCommit,
            conclusion.Outcome,
            conclusion.Text,
            ParseTimestamp(reader.GetString(1)));
    }

    /// <summary>A session's conclusions across every workspace, newest first.</summary>
    public async Task<IReadOnlyList<ReviewConclusion>> GetConclusionsAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {ConclusionColumns} FROM review_conclusions WHERE session_id = $session ORDER BY created_at DESC, id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$limit", MaxQueryLimit);

        var conclusions = new List<ReviewConclusion>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            conclusions.Add(ReadConclusion(reader));
        }

        return conclusions;
    }

    /// <summary>
    /// Copies a session's conclusions to another session in one transaction, changing only <c>session_id</c> (the rows get
    /// new ids, the source rows stay) and returns how many were copied. A session id is unique, so the copy does not need
    /// the workspace filter the comment copy uses.
    /// </summary>
    private static async Task<int> CopyConclusionsAsync(SqliteConnection connection, SqliteTransaction transaction, string from, string to, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO review_conclusions
                (workspace_path, session_id, step_id, base_commit, head_commit, outcome, body, created_at)
            SELECT workspace_path, $to, step_id, base_commit, head_commit, outcome, body, created_at
            FROM review_conclusions WHERE session_id = $from ORDER BY id;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static ReviewConclusion ReadConclusion(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        (ReviewConclusionOutcome)reader.GetInt32(6),
        reader.GetString(7),
        ParseTimestamp(reader.GetString(8)));
}
