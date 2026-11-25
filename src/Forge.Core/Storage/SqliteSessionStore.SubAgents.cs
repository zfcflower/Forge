using Forge.Abstractions;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Storage;

/// <summary>
/// The <c>sub_agent_state</c> half of the store: a spawned child's agent id, kind, model and last run end, which is what
/// lets the next process continue it instead of forgetting it (forge-core "子 Agent 复用"). A child the user drives has no
/// row here: its own session row already restores it.
/// <para>The write rides the store's writer queue like every other write, so it is serialized with appends and drained by
/// <see cref="DisposeAsync"/> - a graceful close never loses the row a finished child was given.</para>
/// </summary>
public sealed partial class SqliteSessionStore
{
    /// <inheritdoc />
    public Task SaveSubAgentStateAsync(SubAgentState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        return EnqueueAsync(new SubAgentStateOp(state, ct), ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RestorableSubAgent>> LoadSubAgentStatesAsync(
        string rootSessionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSessionId);
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // The whole tree below the root: a child of a child is as continuable as a direct child, and the walk gives its depth
        // (the one a resumed instance needs for its own spawning) and the directory it ran in.
        command.CommandText = """
            WITH RECURSIVE tree(id, depth) AS (
                SELECT $root, 0
                UNION
                SELECT sessions.id, tree.depth + 1
                FROM sessions JOIN tree ON sessions.parent_session_id = tree.id
                WHERE tree.depth < 1024)
            SELECT state.session_id, state.agent_id, state.kind, state.model, state.provider, state.thinking,
                   state.finished_at, state.reusable, state.max_output, sessions.workspace_path, tree.depth
            FROM sub_agent_state state
            JOIN tree ON tree.id = state.session_id
            JOIN sessions ON sessions.id = state.session_id
            WHERE tree.depth > 0 AND sessions.status <> 'Deleted'
            ORDER BY tree.depth, state.session_id
            """;
        command.Parameters.AddWithValue("$root", rootSessionId);
        var states = new List<RestorableSubAgent>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var sessionId = StorageDb.ReadText(reader, 0, "session_id", _databasePath, "a sub-agent state row");
            var row = $"sub-agent state {sessionId}";
            var thinking = reader.IsDBNull(5)
                ? ThinkingEffort.Off
                : Enum.TryParse<ThinkingEffort>(reader.GetString(5), ignoreCase: true, out var parsed)
                    ? parsed
                    : ThinkingEffort.Off;
            var model = new ModelSettings(
                StorageDb.ReadText(reader, 3, "model", _databasePath, row),
                thinking,
                reader.IsDBNull(8) ? null : (int)reader.GetInt64(8),
                reader.IsDBNull(4) ? null : reader.GetString(4));
            var state = new SubAgentState(
                sessionId,
                StorageDb.ReadText(reader, 1, "agent_id", _databasePath, row),
                StorageDb.ReadText(reader, 2, "kind", _databasePath, row),
                model,
                reader.IsDBNull(6)
                    ? null
                    : StorageDb.ParseCanonicalTimestamp(reader.GetString(6), "finished_at", _databasePath, row))
            {
                Reusable = reader.GetInt64(7) != 0,
            };
            states.Add(new RestorableSubAgent(
                state,
                StorageDb.ReadText(reader, 9, "workspace_path", _databasePath, row),
                (int)reader.GetInt64(10)));
        }

        return states;
    }

    /// <summary>One child's state, upserted by session id: the spawn writes a row without an end, each run end rewrites it.</summary>
    private sealed class SubAgentStateOp(SubAgentState state, CancellationToken ct) : WriteOp<bool>(ct)
    {
        protected override async ValueTask<bool> ExecuteAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SeqAllocator sequences)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sub_agent_state
                    (session_id, agent_id, kind, model, provider, thinking, max_output, finished_at, reusable)
                VALUES ($session, $agent, $kind, $model, $provider, $thinking, $maxOutput, $finished, $reusable)
                ON CONFLICT(session_id) DO UPDATE SET
                    agent_id    = excluded.agent_id,
                    kind        = excluded.kind,
                    model       = excluded.model,
                    provider    = excluded.provider,
                    thinking    = excluded.thinking,
                    max_output  = excluded.max_output,
                    finished_at = excluded.finished_at,
                    reusable    = excluded.reusable
                """;
            command.Parameters.AddWithValue("$session", state.SessionId);
            command.Parameters.AddWithValue("$agent", state.AgentId);
            command.Parameters.AddWithValue("$kind", state.Kind);
            command.Parameters.AddWithValue("$model", state.Model.Model);
            command.Parameters.AddWithValue("$provider", (object?)state.Model.ProviderId ?? DBNull.Value);
            command.Parameters.AddWithValue("$thinking", state.Model.Thinking.ToString());
            command.Parameters.AddWithValue("$maxOutput", (object?)state.Model.MaxOutputTokens ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$finished",
                state.FinishedAt is { } finishedAt ? StorageDb.FormatTimestamp(finishedAt) : DBNull.Value);
            command.Parameters.AddWithValue("$reusable", state.Reusable ? 1 : 0);
            return await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false) > 0;
        }
    }
}
