using System.Globalization;
using Forge.Abstractions;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Storage;

/// <summary>
/// SQLite-backed <see cref="ITurnMetricsStore"/>: one <see cref="SqliteWriter"/> (a single long-lived write connection
/// serialized by a semaphore, one <c>BEGIN IMMEDIATE</c> transaction per write) exactly like
/// <see cref="SqliteTurnCheckpointStore"/>, but on its own connection and lock — metrics are auxiliary and must never
/// contend with the checkpoint or session-history paths. A database from before this feature carries no
/// <c>turn_metrics</c> table and is not migrated: reads answer the same nothing a missing table would (no table, empty
/// list, turn 0), and the App-side recorder answers the existence probe with false and bypasses the whole feature before
/// any write is attempted — a write that still meets a missing table fails with a <see cref="SqliteException"/> its
/// caller swallows. Every command runs with a short busy timeout — when the database is wedged, the row is dropped
/// rather than the turn delayed.
/// </summary>
public sealed class SqliteTurnMetricsStore : ITurnMetricsStore, IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Busy timeout for every command this store runs. The session-history default
    /// (<see cref="StorageDb.CommandTimeoutSeconds"/>, 30s) exists for the body path; metrics are auxiliary data, so a
    /// wedged database costs this store its row, never the caller's turn.
    /// </summary>
    private const int BusyTimeoutSeconds = 2;

    private const string Columns =
        "session_id, turn, started_at, ended_at, provider, model, status, error, " +
        "input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cache_write_1h_tokens, tool_calls, stream_ms, effort";

    private const string StatusCompleted = "completed";
    private const string StatusStopped = "stopped";
    private const string StatusFailed = "failed";
    private const string StatusLimit = "limit";

    private const string TableName = "turn_metrics";

    private readonly SqliteWriter _writer;
    private readonly string _databasePath;
    private int _disposed;

    public SqliteTurnMetricsStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        _databasePath = StorageDb.DatabasePath(directory);
        _writer = new SqliteWriter(BuildConnectionString(StorageDb.DatabasePath(directory)), _databasePath);
    }

    public SqliteTurnMetricsStore(SqliteConnectionStringBuilder connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentException.ThrowIfNullOrEmpty(connectionString.DataSource, nameof(connectionString));
        _databasePath = Path.GetFullPath(connectionString.DataSource);
        // A caller who set a timeout of their own keeps it; the default here is the store's short one, not the
        // session-history 30s the shared helper would force.
        if (connectionString.DefaultTimeout <= 0)
        {
            connectionString.DefaultTimeout = BusyTimeoutSeconds;
        }

        _writer = new SqliteWriter(connectionString.ToString(), _databasePath);
    }

    public string ConnectionString => _writer.ConnectionString;

    /// <summary>
    /// The store's own connection string: the shared defaults (<see cref="StorageDb.ConnectionString"/>) but with the
    /// short busy timeout instead of the session-history 30s — a wedged database costs this store its row, not the
    /// turn.
    /// </summary>
    private static string BuildConnectionString(string databasePath) => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Default,
        Pooling = true,
        DefaultTimeout = BusyTimeoutSeconds,
    }.ToString();

    /// <summary>Full path of the database file this store reads and writes.</summary>
    public string DatabasePath => _databasePath;

    public Task<bool> ExistsAsync(CancellationToken ct) =>
        _writer.ReadAsync(async connection => await TableExistsAsync(connection, ct).ConfigureAwait(false), ct);

    /// <summary>
    /// Upserts one turn's row: a retried write over the same <c>(session_id, turn)</c> replaces the row instead of
    /// duplicating it. On a database without the table this throws <see cref="SqliteException"/>; the background
    /// recorder swallows it. The timestamps are written in the canonical round-trip shape every store uses, which the
    /// schema's guards demand; the caller supplies UTC.
    /// </summary>
    public Task WriteAsync(TurnMetric metric, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(metric);
        StoragePaths.SafeSegment(metric.SessionId, nameof(metric.SessionId));
        ArgumentOutOfRangeException.ThrowIfNegative(metric.Turn);
        return _writer.WriteAsync(
            async (connection, transaction) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"""
                    INSERT INTO turn_metrics ({Columns})
                    VALUES ($session, $turn, $started, $ended, $provider, $model, $status, $error,
                            $input, $output, $cache_read, $cache_write, $cache_write_1h, $tool_calls, $stream_ms, $effort)
                    ON CONFLICT (session_id, turn) DO UPDATE SET
                        started_at = excluded.started_at,
                        ended_at = excluded.ended_at,
                        provider = excluded.provider,
                        model = excluded.model,
                        status = excluded.status,
                        error = excluded.error,
                        input_tokens = excluded.input_tokens,
                        output_tokens = excluded.output_tokens,
                        cache_read_tokens = excluded.cache_read_tokens,
                        cache_write_tokens = excluded.cache_write_tokens,
                        cache_write_1h_tokens = excluded.cache_write_1h_tokens,
                        tool_calls = excluded.tool_calls,
                        stream_ms = excluded.stream_ms,
                        effort = excluded.effort
                    """;
                command.Parameters.AddWithValue("$session", metric.SessionId);
                command.Parameters.AddWithValue("$turn", metric.Turn);
                command.Parameters.AddWithValue("$started", StorageDb.FormatTimestamp(metric.StartedAt));
                command.Parameters.AddWithValue("$ended", StorageDb.FormatTimestamp(metric.EndedAt));
                command.Parameters.AddWithValue("$provider", (object?)metric.Provider ?? DBNull.Value);
                command.Parameters.AddWithValue("$model", (object?)metric.Model ?? DBNull.Value);
                command.Parameters.AddWithValue("$status", StatusName(metric.Status));
                command.Parameters.AddWithValue("$error", (object?)metric.Error ?? DBNull.Value);
                command.Parameters.AddWithValue("$input", ToDb(metric.InputTokens));
                command.Parameters.AddWithValue("$output", ToDb(metric.OutputTokens));
                command.Parameters.AddWithValue("$cache_read", ToDb(metric.CacheReadTokens));
                command.Parameters.AddWithValue("$cache_write", ToDb(metric.CacheWriteTokens));
                command.Parameters.AddWithValue("$cache_write_1h", ToDb(metric.CacheWrite1hTokens));
                command.Parameters.AddWithValue("$tool_calls", ToDb(metric.ToolCalls));
                command.Parameters.AddWithValue("$stream_ms", ToStreamMs(metric.StreamElapsed));
                command.Parameters.AddWithValue("$effort", metric.Effort is { } effort ? EffortName(effort) : DBNull.Value);
                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            },
            ct);
    }

    public Task<IReadOnlyList<TurnMetric>> ListAsync(string sessionId, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        return _writer.ReadAsync(
            async connection =>
            {
                if (!await TableExistsAsync(connection, ct).ConfigureAwait(false))
                {
                    return Array.Empty<TurnMetric>();
                }

                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {Columns} FROM turn_metrics WHERE session_id = $session ORDER BY turn";
                command.Parameters.AddWithValue("$session", sessionId);
                var metrics = new List<TurnMetric>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    metrics.Add(ReadMetric(reader));
                }

                return (IReadOnlyList<TurnMetric>)metrics;
            },
            ct);
    }

    public Task<int> MaxTurnAsync(string sessionId, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        return _writer.ReadAsync(
            async connection =>
            {
                if (!await TableExistsAsync(connection, ct).ConfigureAwait(false))
                {
                    return 0;
                }

                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT MAX(turn) FROM turn_metrics WHERE session_id = $session";
                command.Parameters.AddWithValue("$session", sessionId);
                return (int)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as long? ?? 0);
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

    /// <summary>The lowercase text the status column holds; the enum member name is never written.</summary>
    internal static string StatusName(TurnMetricStatus status) => status switch
    {
        TurnMetricStatus.Completed => StatusCompleted,
        TurnMetricStatus.Stopped => StatusStopped,
        TurnMetricStatus.Failed => StatusFailed,
        TurnMetricStatus.Limit => StatusLimit,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown turn metric status."),
    };

    /// <summary>
    /// The same mapping the session store applies to its status column: a value this build does not know — a row a
    /// newer build wrote — is malformed data and reported as such, never silently read as a wrong outcome.
    /// </summary>
    private static TurnMetricStatus ParseStatus(string value, string databasePath, string row) => value switch
    {
        StatusCompleted => TurnMetricStatus.Completed,
        StatusStopped => TurnMetricStatus.Stopped,
        StatusFailed => TurnMetricStatus.Failed,
        StatusLimit => TurnMetricStatus.Limit,
        _ => throw StorageDb.Malformed(
            databasePath,
            "status",
            row,
            $"'{value}' is not one of {StatusCompleted}, {StatusStopped}, {StatusFailed}, {StatusLimit}",
            null),
    };

    /// <summary>
    /// The lowercase text the <c>effort</c> column holds — the same names <c>settings.json</c> and <c>composer.json</c> use, so a
    /// recorded level reads back as the step it was.
    /// </summary>
    internal static string EffortName(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Minimal => "minimal",
        ThinkingEffort.Low => "low",
        ThinkingEffort.Medium => "medium",
        ThinkingEffort.High => "high",
        ThinkingEffort.XHigh => "xhigh",
        ThinkingEffort.Max => "max",
        _ => "off",
    };

    /// <summary>A NULL column is a turn nobody recorded a level for (a sub-agent's, or one from before the column existed).</summary>
    private ThinkingEffort? ParseEffort(SqliteDataReader reader, int ordinal, string row)
    {
        var text = StorageDb.ReadOptionalText(reader, ordinal, "effort", _databasePath, row);
        return text switch
        {
            null => null,
            "off" => ThinkingEffort.Off,
            "minimal" => ThinkingEffort.Minimal,
            "low" => ThinkingEffort.Low,
            "medium" => ThinkingEffort.Medium,
            "high" => ThinkingEffort.High,
            "xhigh" => ThinkingEffort.XHigh,
            "max" => ThinkingEffort.Max,
            _ => throw StorageDb.Malformed(
                _databasePath,
                "effort",
                row,
                $"'{text}' is not one of off, minimal, low, medium, high, xhigh, max",
                null),
        };
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        // Both columns, not just the newest one: a table that already carried effort but not stream_ms (one an older build of
        // this feature created after the step above added the column) must read as "no table" too, or every insert and list
        // would fail on the column it still lacks instead of the feature quietly standing aside.
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($name) WHERE name IN ('stream_ms', 'effort')";
        command.Parameters.AddWithValue("$name", TableName);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is 2L;
    }

    /// <summary>A null token column binds as NULL — a column a turn never measured is never written as zero.</summary>
    private static object ToDb(int? value) => value is { } number ? number : DBNull.Value;

    /// <summary>
    /// The milliseconds the turn's model calls spent streaming, rounded half up and clamped at zero. The column is
    /// <c>NOT NULL</c> — every write carries it, failed turns included, because a turn that streamed nothing measures
    /// zero rather than "unknown"; a negative reading would be an adapter defect, clamped here so a bug cannot poison
    /// the row's other columns.
    /// </summary>
    private static long ToStreamMs(TimeSpan elapsed)
    {
        var milliseconds = elapsed.TotalMilliseconds;
        if (milliseconds <= 0)
        {
            return 0;
        }

        return (long)Math.Round(milliseconds, MidpointRounding.AwayFromZero);
    }

    private TurnMetric ReadMetric(SqliteDataReader reader)
    {
        var sessionId = StorageDb.ReadText(reader, 0, "session_id", _databasePath, "a row whose session cannot be read");
        var turn = StorageDb.ReadInt64(reader, 1, "turn", _databasePath, $"turn metric of session {sessionId}");
        var row = $"turn {turn.ToString(CultureInfo.InvariantCulture)} of session {sessionId}";
        return new TurnMetric(
            sessionId,
            (int)turn,
            StorageDb.ParseCanonicalTimestamp(
                StorageDb.ReadText(reader, 2, "started_at", _databasePath, row),
                "started_at",
                _databasePath,
                row),
            StorageDb.ParseCanonicalTimestamp(
                StorageDb.ReadText(reader, 3, "ended_at", _databasePath, row),
                "ended_at",
                _databasePath,
                row),
            ParseStatus(StorageDb.ReadText(reader, 6, "status", _databasePath, row), _databasePath, row))
        {
            Provider = StorageDb.ReadOptionalText(reader, 4, "provider", _databasePath, row),
            Model = StorageDb.ReadOptionalText(reader, 5, "model", _databasePath, row),
            Error = StorageDb.ReadOptionalText(reader, 7, "error", _databasePath, row),
            InputTokens = ReadOptionalInt32(reader, 8, "input_tokens", row),
            OutputTokens = ReadOptionalInt32(reader, 9, "output_tokens", row),
            CacheReadTokens = ReadOptionalInt32(reader, 10, "cache_read_tokens", row),
            CacheWriteTokens = ReadOptionalInt32(reader, 11, "cache_write_tokens", row),
            CacheWrite1hTokens = ReadOptionalInt32(reader, 12, "cache_write_1h_tokens", row),
            ToolCalls = ReadOptionalInt32(reader, 13, "tool_calls", row),
            StreamElapsed = TimeSpan.FromMilliseconds(
                StorageDb.ReadInt64(reader, 14, "stream_ms", _databasePath, row)),
            Effort = ParseEffort(reader, 15, row),
        };
    }

    private int? ReadOptionalInt32(SqliteDataReader reader, int ordinal, string column, string row) =>
        reader.IsDBNull(ordinal)
            ? null
            : (int)StorageDb.ReadInt64(reader, ordinal, column, _databasePath, row);
}
