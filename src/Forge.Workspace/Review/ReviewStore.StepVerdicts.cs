using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Forge.Workspace;

/// <summary>One persisted step review verdict; <see cref="Verdict"/> is the numeric value of Core's verdict enum.</summary>
public sealed record StepVerdictRow(
    string SessionId,
    string StepId,
    int Verdict,
    string? CommitId,
    DateTimeOffset At,
    string? Tree,
    int Rounds);

public sealed partial class ReviewStore
{
    /// <summary>Every step verdict of one session, in no particular order.</summary>
    public async Task<IReadOnlyList<StepVerdictRow>> GetStepVerdictsAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT step_id, verdict, commit_id, at, tree, rounds FROM step_review_verdicts WHERE session_id = $session";
        command.Parameters.AddWithValue("$session", sessionId);
        var rows = new List<StepVerdictRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new StepVerdictRow(
                sessionId,
                reader.GetString(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                ParseTimestamp(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5)));
        }

        return rows;
    }

    /// <summary>Inserts or replaces the verdict of one (session, step).</summary>
    public async Task SaveStepVerdictAsync(StepVerdictRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.StepId);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO step_review_verdicts (session_id, step_id, verdict, commit_id, at, tree, rounds)
            VALUES ($session, $step, $verdict, $commit, $at, $tree, $rounds)
            ON CONFLICT (session_id, step_id) DO UPDATE SET
                verdict = excluded.verdict, commit_id = excluded.commit_id, at = excluded.at,
                tree = excluded.tree, rounds = excluded.rounds;
            """;
        command.Parameters.AddWithValue("$session", row.SessionId);
        command.Parameters.AddWithValue("$step", row.StepId);
        command.Parameters.AddWithValue("$verdict", row.Verdict);
        command.Parameters.AddWithValue("$commit", (object?)row.CommitId ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", row.At.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$tree", (object?)row.Tree ?? DBNull.Value);
        command.Parameters.AddWithValue("$rounds", row.Rounds);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Removes the verdict of one (session, step); a missing row is not an error.</summary>
    public async Task DeleteStepVerdictAsync(string sessionId, string stepId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM step_review_verdicts WHERE session_id = $session AND step_id = $step";
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$step", stepId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
