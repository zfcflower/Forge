using System.Globalization;
using Forge.Abstractions;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Storage;

/// <summary>
/// The per-turn state facts in <c>forge.db</c> (<c>turn_ledger</c>, schema step 16): one row per task turn, opened
/// <see cref="TurnLedgerStatus.Pending"/> when the turn's user message lands and settled in place at the turn's end.
/// Its own <see cref="SqliteWriter"/> - one long-lived write connection behind a semaphore, one <c>BEGIN IMMEDIATE</c>
/// per write - deliberately separate from the session store's history write queue: a ledger write is a side observation
/// and must never delay a conversation write (nor the other way round). <c>read_files_text</c>/<c>changed_files_text</c> and <c>specs_text</c> are
/// newline-separated plain text, one entry per line. The full-text surface is the same pair the messages use
/// (<c>turn_ledger_fts</c>, or <c>turn_ledger_text</c> on a build without FTS5), and every write keeps the search row in
/// the same transaction as the row it mirrors.
/// </summary>
public sealed class SqliteTurnLedgerStore : ITurnLedgerStore, IAsyncDisposable, IDisposable
{
    private const string Columns =
        "session_id, turn_sequence, status, task_text, answer_text, conclusion_text, " +
        "read_files_text, changed_files_text, specs_text, from_seq, to_seq, answer_seq, provider, model, agent_kind, "
        + "agent_depth, folded, created_at, updated_at";

    private readonly SqliteWriter _writer;
    private readonly string _databasePath;
    private readonly SemaphoreSlim initGate = new(1, 1);
    private int fts5 = -1;
    private int _disposed;

    public SqliteTurnLedgerStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        _databasePath = StorageDb.DatabasePath(directory);
        _writer = new SqliteWriter(StorageDb.ConnectionString(_databasePath), _databasePath);
    }

    public SqliteTurnLedgerStore(SqliteConnectionStringBuilder connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentException.ThrowIfNullOrEmpty(connectionString.DataSource, nameof(connectionString));
        _databasePath = Path.GetFullPath(connectionString.DataSource);
        _writer = new SqliteWriter(StorageDb.ConnectionString(connectionString), _databasePath);
    }

    /// <summary>Full path of the database file this store reads and writes.</summary>
    public string DatabasePath => _databasePath;

    /// <summary>
    /// Opens the turn's row and answers the number it was filed under. A caller with its own number gets that number - a
    /// row already present for it is kept as it is (one turn keeps one row) - and a caller with none has the session's next
    /// number allocated and reserved by this same insert, so two writers can never be handed the same one.
    /// </summary>
    public Task<long> BeginAsync(TurnLedgerBegin begin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(begin);
        StoragePaths.SafeSegment(begin.SessionId, nameof(begin));
        return _writer.WriteAsync(
            async (connection, transaction) =>
            {
                // The number and the insert share one transaction: a "read MAX+1, then insert" pair is exactly the
                // two-step two writers race, so the allocation happens only as part of the insert itself.
                var turn = begin.TurnSequence ?? await NextNumberAsync(connection, transaction, begin.SessionId, ct)
                    .ConfigureAwait(false);
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO turn_ledger (session_id, turn_sequence, status, task_text, answer_text, conclusion_text,
                        read_files_text, changed_files_text, specs_text, from_seq, to_seq, answer_seq, provider, model,
                        agent_kind, agent_depth, created_at, updated_at)
                    VALUES ($session, $turn, $pending, $task, '', '', '', '', '', $from, 0, 0, $provider, $model, $kind,
                        $depth, $at, $at)
                    ON CONFLICT (session_id, turn_sequence) DO NOTHING
                    """,
                    ct,
                    ("$session", begin.SessionId),
                    ("$turn", turn),
                    ("$pending", StorageSchema.LedgerPending),
                    ("$task", begin.TaskText),
                    ("$from", begin.FromSeq),
                    ("$provider", begin.ProviderId ?? (object)DBNull.Value),
                    ("$model", begin.Model ?? (object)DBNull.Value),
                    ("$kind", begin.AgentKind ?? (object)DBNull.Value),
                    ("$depth", begin.AgentDepth),
                    ("$at", StorageDb.FormatTimestamp(begin.StartedAt))).ConfigureAwait(false);
                return turn;
            },
            ct);
    }

    /// <summary>The session's next turn number: <c>MAX(turn_sequence) + 1</c>, inside the caller's write transaction.</summary>
    private static async ValueTask<long> NextNumberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        CancellationToken ct)
    {
        await using var command = StorageDb.Command(
            connection,
            transaction,
            "SELECT COALESCE(MAX(turn_sequence), 0) + 1 FROM turn_ledger WHERE session_id = $session");
        command.Parameters.AddWithValue("$session", sessionId);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is long next ? next : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Settles the row: every column of the terminal state in one write, creating the row when the turn somehow ended
    /// before its <see cref="BeginAsync"/> landed (a settle must never fail on a missing pending row). A completion with
    /// no number of its own has the session's next one allocated by the same write that files the settled row, and the
    /// <see cref="TurnLedgerCompletion.Opened"/> it carries - the begin record the loop kept - gives a created row its
    /// task text, owner and from-seq, so a turn whose every begin failed still files as itself and not anonymously.
    /// The search row is rewritten in the same transaction - deleted first, so a row whose searchable prose shrank
    /// cannot leave a stale match behind.
    /// </summary>
    public async Task CompleteAsync(TurnLedgerCompletion completion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(completion);
        StoragePaths.SafeSegment(completion.SessionId, nameof(completion));
        var fts = await Fts5Async(ct).ConfigureAwait(false);
        await _writer.WriteAsync(
            async (connection, transaction) =>
            {
                var turn = completion.TurnSequence ?? await NextNumberAsync(connection, transaction, completion.SessionId, ct)
                    .ConfigureAwait(false);
                var reads = string.Join('\n', completion.Evidence.ReadFiles);
                var changes = string.Join('\n', completion.Evidence.ChangedFiles);
                var specs = string.Join('\n', completion.Evidence.Specs);
                var updated = await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE turn_ledger SET status = $status, answer_text = $answer, conclusion_text = $conclusion,
                        read_files_text = $reads, changed_files_text = $changes, specs_text = $specs, to_seq = $to,
                        answer_seq = $answerSeq, updated_at = $at
                    WHERE session_id = $session AND turn_sequence = $turn
                    """,
                    ct,
                    ("$status", StatusName(completion.Status)),
                    ("$answer", completion.AnswerText),
                    ("$conclusion", completion.ConclusionText),
                    ("$reads", reads),
                    ("$changes", changes),
                    ("$specs", specs),
                    ("$to", completion.Evidence.ToSeq),
                    ("$answerSeq", completion.AnswerSeq),
                    ("$at", StorageDb.FormatTimestamp(completion.EndedAt)),
                    ("$session", completion.SessionId),
                    ("$turn", turn)).ConfigureAwait(false);
                if (updated == 0)
                {
                    // No pending row: the turn still gets exactly one, born settled. Its identity is the begin record
                    // the completion carries - the task text, owner and from-seq only the opener ever knew; without one,
                    // the task stays empty and the evidence's own opening message is the from-seq.
                    var opened = completion.Opened;
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO turn_ledger (session_id, turn_sequence, status, task_text,
                            answer_text, conclusion_text, read_files_text, changed_files_text, specs_text, from_seq, to_seq,
                            answer_seq, provider, model, agent_kind, agent_depth, created_at, updated_at)
                        VALUES ($session, $turn, $status, $task, $answer, $conclusion, $reads, $changes,
                            $specs, $from, $to, $answerSeq, $provider, $model, $kind, $depth, $at, $at)
                        """,
                        ct,
                        ("$session", completion.SessionId),
                        ("$turn", turn),
                        ("$status", StatusName(completion.Status)),
                        ("$task", opened?.TaskText ?? string.Empty),
                        ("$answer", completion.AnswerText),
                        ("$conclusion", completion.ConclusionText),
                        ("$reads", reads),
                        ("$changes", changes),
                        ("$specs", specs),
                        ("$from", opened?.FromSeq ?? completion.Evidence.FromSeq),
                        ("$to", completion.Evidence.ToSeq),
                        ("$answerSeq", completion.AnswerSeq),
                        ("$provider", opened?.ProviderId ?? (object)DBNull.Value),
                        ("$model", opened?.Model ?? (object)DBNull.Value),
                        ("$kind", opened?.AgentKind ?? (object)DBNull.Value),
                        ("$depth", opened?.AgentDepth ?? 0),
                        ("$at", StorageDb.FormatTimestamp(completion.EndedAt))).ConfigureAwait(false);
                }

                string? task;
                await using (var read = StorageDb.Command(
                    connection,
                    transaction,
                    "SELECT task_text FROM turn_ledger WHERE session_id = $session AND turn_sequence = $turn"))
                {
                    read.Parameters.AddWithValue("$session", completion.SessionId);
                    read.Parameters.AddWithValue("$turn", turn);
                    task = await read.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                }

                await WriteSearchRowAsync(connection, transaction, fts, completion, turn, task ?? string.Empty, ct).ConfigureAwait(false);
                return 0;
            },
            ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<TurnLedgerEntry>> ListAsync(string sessionId, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        return _writer.ReadAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {Columns} FROM turn_ledger WHERE session_id = $session ORDER BY turn_sequence";
                command.Parameters.AddWithValue("$session", sessionId);
                var entries = new List<TurnLedgerEntry>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    entries.Add(ReadEntry(reader, sessionId));
                }

                return (IReadOnlyList<TurnLedgerEntry>)entries;
            },
            ct);
    }

    public Task<TurnLedgerEntry?> GetAsync(string sessionId, long turnSequence, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        return _writer.ReadAsync<TurnLedgerEntry?>(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {Columns} FROM turn_ledger WHERE session_id = $session AND turn_sequence = $turn";
                command.Parameters.AddWithValue("$session", sessionId);
                command.Parameters.AddWithValue("$turn", turnSequence);
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadEntry(reader, sessionId) : null;
            },
            ct);
    }

    /// <summary>The row whose <see cref="TurnLedgerEntry.AnswerSeq"/> is this message - only when exactly one row of
    /// the session ends on it. None, or two (a store bug, never a position two turns share on purpose), answers null:
    /// the pairing key is a position, and an ambiguous position pins nothing.</summary>
    public Task<TurnLedgerEntry?> FindByAnswerSeqAsync(string sessionId, long answerSeq, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        return _writer.ReadAsync<TurnLedgerEntry?>(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                    SELECT {Columns} FROM turn_ledger
                    WHERE session_id = $session AND answer_seq = $answer
                    ORDER BY turn_sequence
                    LIMIT 2
                    """;
                command.Parameters.AddWithValue("$session", sessionId);
                command.Parameters.AddWithValue("$answer", answerSeq);
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }

                var only = ReadEntry(reader, sessionId);
                return await reader.ReadAsync(ct).ConfigureAwait(false) ? null : only;
            },
            ct);
    }

    public async Task<IReadOnlyList<TurnLedgerHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        if (limit <= 0 || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        return await Fts5Async(ct).ConfigureAwait(false)
            ? await SearchFullTextAsync(sessionId, query, limit, ct).ConfigureAwait(false)
            : await SearchLikeAsync(sessionId, query, limit, ct).ConfigureAwait(false);
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

    internal static string StatusName(TurnLedgerStatus status) => status switch
    {
        TurnLedgerStatus.Pending => StorageSchema.LedgerPending,
        TurnLedgerStatus.Settled => StorageSchema.LedgerSettled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown turn ledger status."),
    };

    internal static TurnLedgerStatus ParseStatus(string value, string databasePath, string row) => value switch
    {
        StorageSchema.LedgerPending => TurnLedgerStatus.Pending,
        // A row written by a build that still judged the reply's shape settles like any other: the shape is no longer read.
        StorageSchema.LedgerSettled or "received" or "missing" or "invalid" => TurnLedgerStatus.Settled,
        _ => throw StorageDb.Malformed(
            databasePath,
            "status",
            row,
            $"'{value}' is not one of pending, settled",
            null),
    };

    /// <summary>
    /// Whether this build searches the ledger through <c>turn_ledger_fts</c> or the fallback table, decided once on the
    /// store's first use by the same open-time logic that decides it for the messages.
    /// </summary>
    private async ValueTask<bool> Fts5Async(CancellationToken ct)
    {
        var read = Volatile.Read(ref fts5);
        if (read >= 0)
        {
            return read == 1;
        }

        await initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            read = Volatile.Read(ref fts5);
            if (read >= 0)
            {
                return read == 1;
            }

            await using var connection = await StorageDb.OpenAsync(_writer.ConnectionString, ct).ConfigureAwait(false);
            await StorageDb.ConfigureAsync(connection, ct).ConfigureAwait(false);
            await StorageSchema.EnsureAsync(connection, _databasePath, ct).ConfigureAwait(false);
            var available = await StorageSchema.EnsureLedgerSearchAsync(connection, ct).ConfigureAwait(false);
            Volatile.Write(ref fts5, available ? 1 : 0);
            return available;
        }
        finally
        {
            initGate.Release();
        }
    }

    /// <summary>Writes the search row of one settled turn; an empty prose leaves no row, and the old one is dropped first.</summary>
    /// <summary>Writes the search row of one settled turn; an empty prose leaves no row, and the old one is dropped first.</summary>
    private static async ValueTask WriteSearchRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        bool fts5,
        TurnLedgerCompletion completion,
        long turn,
        string taskText,
        CancellationToken ct)
    {
        var text = StorageSchema.LedgerSearchText(taskText, completion.ConclusionText);
        await ExecuteAsync(
            connection,
            transaction,
            fts5
                ? "DELETE FROM turn_ledger_fts WHERE session_id = $session AND turn_sequence = $turn"
                : "DELETE FROM turn_ledger_text WHERE session_id = $session AND turn_sequence = $turn",
            ct,
            ("$session", completion.SessionId),
            ("$turn", turn)).ConfigureAwait(false);
        if (text.Length == 0)
        {
            return;
        }

        await ExecuteAsync(
            connection,
            transaction,
            fts5
                ? "INSERT INTO turn_ledger_fts (session_id, turn_sequence, text) VALUES ($session, $turn, $text)"
                : "INSERT OR REPLACE INTO turn_ledger_text (session_id, turn_sequence, text) VALUES ($session, $turn, $text)",
            ct,
            ("$session", completion.SessionId),
            ("$turn", turn),
            ("$text", text)).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<TurnLedgerHit>> SearchFullTextAsync(string sessionId, string query, int limit, CancellationToken ct)
    {
        var match = BuildMatchQuery(query);
        if (match is null)
        {
            return [];
        }

        await using var connection = await StorageDb.OpenAsync(_writer.ConnectionString, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT turn_sequence, snippet(turn_ledger_fts, 2, '[', ']', '…', 16), bm25(turn_ledger_fts)
            FROM turn_ledger_fts
            WHERE turn_ledger_fts MATCH $q AND session_id = $id
            ORDER BY bm25(turn_ledger_fts), turn_sequence
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$q", match);
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$limit", limit);
        var hits = new List<TurnLedgerHit>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            hits.Add(new TurnLedgerHit(reader.GetInt64(0), -reader.GetDouble(2), reader.GetString(1)));
        }

        return hits;
    }

    private async Task<IReadOnlyList<TurnLedgerHit>> SearchLikeAsync(string sessionId, string query, int limit, CancellationToken ct)
    {
        var needle = query.Trim();
        await using var connection = await StorageDb.OpenAsync(_writer.ConnectionString, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT turn_sequence, text FROM turn_ledger_text
            WHERE session_id = $id AND text LIKE $pattern ESCAPE '\'
            ORDER BY turn_sequence
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$pattern", "%" + EscapeLike(needle) + "%");
        command.Parameters.AddWithValue("$limit", limit);
        var hits = new List<TurnLedgerHit>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var text = reader.GetString(1);
            var index = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            var start = Math.Max(0, index - 32);
            var end = Math.Min(text.Length, start + needle.Length + 64);
            hits.Add(new TurnLedgerHit(reader.GetInt64(0), 1, text[start..end]));
        }

        return hits;
    }

    /// <summary>
    /// Absorbs every row up to and including <paramref name="throughTurn"/> into the session's folded block: monotonic, so
    /// the fold range only grows and a resumed session renders the same shape the live one had. One UPDATE, no re-read.
    /// </summary>
    public Task MarkFoldedAsync(string sessionId, long throughTurn, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        if (throughTurn <= 0)
        {
            return Task.CompletedTask;
        }

        return _writer.WriteAsync(
            async (connection, transaction) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "UPDATE turn_ledger SET folded = 1 WHERE session_id = $session AND turn_sequence <= $turn AND folded = 0",
                    ct,
                    ("$session", sessionId),
                    ("$turn", throughTurn)).ConfigureAwait(false);
                return 0;
            },
            ct);
    }

    private TurnLedgerEntry ReadEntry(SqliteDataReader reader, string sessionId)
    {
        var row = $"turn ledger row {sessionId}/{ReadLong(reader, 1)}";
        var status = ParseStatus(StorageDb.ReadText(reader, 2, "status", _databasePath, row), _databasePath, row);
        return new TurnLedgerEntry(
            sessionId,
            ReadLong(reader, 1),
            status,
            StorageDb.ReadText(reader, 3, "task_text", _databasePath, row),
            StorageDb.ReadText(reader, 4, "answer_text", _databasePath, row),
            StorageDb.ReadText(reader, 5, "conclusion_text", _databasePath, row),
            Lines(StorageDb.ReadText(reader, 6, "read_files_text", _databasePath, row)),
            Lines(StorageDb.ReadText(reader, 7, "changed_files_text", _databasePath, row)),
            Lines(StorageDb.ReadText(reader, 8, "specs_text", _databasePath, row)),
            ReadLong(reader, 9),
            ReadLong(reader, 10),
            ReadLong(reader, 11),
            StorageDb.ReadOptionalText(reader, 12, "provider", _databasePath, row),
            StorageDb.ReadOptionalText(reader, 13, "model", _databasePath, row),
            StorageDb.ReadOptionalText(reader, 14, "agent_kind", _databasePath, row),
            (int)ReadLong(reader, 15),
            ReadLong(reader, 16) != 0,
            StorageDb.ParseCanonicalTimestamp(
                StorageDb.ReadText(reader, 17, "created_at", _databasePath, row),
                "created_at",
                _databasePath,
                row),
            StorageDb.ParseCanonicalTimestamp(
                StorageDb.ReadText(reader, 18, "updated_at", _databasePath, row),
                "updated_at",
                _databasePath,
                row));
    }

    private long ReadLong(SqliteDataReader reader, int ordinal) => reader.GetInt64(ordinal);

    /// <summary>The newline-separated text columns read back as the lists they were written from; no empty lines.</summary>
    private static IReadOnlyList<string> Lines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (line.Length > 0)
            {
                kept.Add(line);
            }
        }

        return kept;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? BuildMatchQuery(string query)
    {
        var tokens = new List<string>(4);
        var current = new System.Text.StringBuilder(16);
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
}
