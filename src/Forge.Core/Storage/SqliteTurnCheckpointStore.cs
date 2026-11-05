using System.Globalization;
using Forge.Abstractions;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Storage;

/// <summary>
/// One row per main-session turn holding the worktree tree hashes taken at the start and end of the turn. The pair is
/// what "changes of this turn" is computed from, so the row is created before the turn runs and completed after it;
/// a turn that never completes keeps a null <see cref="TurnCheckpoint.TreeAfter"/> and therefore no diff.
/// </summary>
public sealed class SqliteTurnCheckpointStore : ITurnCheckpointStore, IAsyncDisposable, IDisposable
{
    private const string Columns = "session_id, turn, worktree_path, tree_before, tree_after, started_at, ended_at";

    private readonly SqliteWriter _writer;
    private readonly string _databasePath;
    private int _disposed;

    public SqliteTurnCheckpointStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        _databasePath = StorageDb.DatabasePath(directory);
        _writer = new SqliteWriter(StorageDb.ConnectionString(_databasePath), _databasePath);
    }

    public SqliteTurnCheckpointStore(SqliteConnectionStringBuilder connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentException.ThrowIfNullOrEmpty(connectionString.DataSource, nameof(connectionString));
        _databasePath = Path.GetFullPath(connectionString.DataSource);
        _writer = new SqliteWriter(StorageDb.ConnectionString(connectionString), _databasePath);
    }

    public string ConnectionString => _writer.ConnectionString;

    /// <summary>Full path of the database file this store reads and writes.</summary>
    public string DatabasePath => _databasePath;

    /// <summary>
    /// Records the tree the turn started from. Re-beginning an unfinished turn replaces that tree, which is what the
    /// caller means when it retries a snapshot; a completed turn is left untouched so its recorded diff stays true.
    /// </summary>
    public Task BeginAsync(
        string sessionId,
        int turn,
        string worktreePath,
        string treeBefore,
        DateTimeOffset at,
        CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        ArgumentOutOfRangeException.ThrowIfNegative(turn);
        ArgumentException.ThrowIfNullOrWhiteSpace(treeBefore);
        ArgumentNullException.ThrowIfNull(worktreePath);
        return _writer.WriteAsync(
            async (connection, transaction) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO turn_checkpoints (session_id, turn, worktree_path, tree_before, started_at)
                    VALUES ($session, $turn, $worktree, $before, $at)
                    ON CONFLICT (session_id, turn) DO UPDATE SET tree_before = excluded.tree_before
                    WHERE turn_checkpoints.tree_after IS NULL
                    """;
                command.Parameters.AddWithValue("$session", sessionId);
                command.Parameters.AddWithValue("$turn", turn);
                command.Parameters.AddWithValue("$worktree", worktreePath);
                command.Parameters.AddWithValue("$before", treeBefore);
                command.Parameters.AddWithValue("$at", StorageDb.FormatTimestamp(at));
                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            },
            ct);
    }

    /// <summary>Records the tree the turn ended at; false when there was no unfinished begin for this turn.</summary>
    public Task<bool> CompleteAsync(string sessionId, int turn, string treeAfter, DateTimeOffset at, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        ArgumentOutOfRangeException.ThrowIfNegative(turn);
        ArgumentException.ThrowIfNullOrWhiteSpace(treeAfter);
        return _writer.WriteAsync(
            async (connection, transaction) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE turn_checkpoints SET tree_after = $after, ended_at = $at
                    WHERE session_id = $session AND turn = $turn AND tree_after IS NULL
                    """;
                command.Parameters.AddWithValue("$after", treeAfter);
                command.Parameters.AddWithValue("$at", StorageDb.FormatTimestamp(at));
                command.Parameters.AddWithValue("$session", sessionId);
                command.Parameters.AddWithValue("$turn", turn);
                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
            },
            ct);
    }

    public Task<IReadOnlyList<TurnCheckpoint>> ListAsync(string sessionId, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        return _writer.ReadAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT {Columns} FROM turn_checkpoints WHERE session_id = $session ORDER BY turn";
                command.Parameters.AddWithValue("$session", sessionId);
                var checkpoints = new List<TurnCheckpoint>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    checkpoints.Add(ReadCheckpoint(reader));
                }

                return (IReadOnlyList<TurnCheckpoint>)checkpoints;
            },
            ct);
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

    private TurnCheckpoint ReadCheckpoint(SqliteDataReader reader)
    {
        var sessionId = StorageDb.ReadText(reader, 0, "session_id", _databasePath, "a row whose session cannot be read");
        var turn = StorageDb.ReadInt64(reader, 1, "turn", _databasePath, $"turn checkpoint of session {sessionId}");
        var row = $"turn {turn.ToString(CultureInfo.InvariantCulture)} of session {sessionId}";
        return new TurnCheckpoint(
            sessionId,
            (int)turn,
            StorageDb.ReadText(reader, 2, "worktree_path", _databasePath, row),
            StorageDb.ReadText(reader, 3, "tree_before", _databasePath, row),
            StorageDb.ReadOptionalText(reader, 4, "tree_after", _databasePath, row),
            StorageDb.ParseCanonicalTimestamp(
                StorageDb.ReadText(reader, 5, "started_at", _databasePath, row),
                "started_at",
                _databasePath,
                row),
            ReadOptionalTimestamp(reader, 6, "ended_at", row));
    }

    private DateTimeOffset? ReadOptionalTimestamp(SqliteDataReader reader, int ordinal, string column, string row)
    {
        var value = StorageDb.ReadOptionalText(reader, ordinal, column, _databasePath, row);
        return value is null ? null : StorageDb.ParseCanonicalTimestamp(value, column, _databasePath, row);
    }
}
