using System.Globalization;
using System.Text;
using Forge.Abstractions;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Storage;

/// <summary>
/// Automation run records and the small automation key/value state, in <c>forge.db</c> (schema v10). Timestamps use the
/// canonical <c>forge.db</c> text shape; <c>started_ticks</c> orders runs across offsets. The unique index on
/// <c>occurrence</c> is the "already ran" guard: <see cref="TryInsertAsync"/> reports a conflict as false.
/// </summary>
public sealed class SqliteAutomationRunStore : IAutomationRunStore, IAsyncDisposable, IDisposable
{
    private const string Columns =
        "id, automation_id, automation_name, source, occurrence, status, started_at, ended_at, detail, session_id, " +
        "target_session_id, cause_run_id, chain_depth, target_key, resume_at, input_tokens, output_tokens, " +
        "cache_read_tokens, cache_write_tokens, message";

    private const string ActiveStatusesSql = "('Running', 'WaitingIdle', 'WaitingQuota')";

    private readonly SqliteWriter _writer;
    private readonly string _databasePath;
    private int _disposed;

    public SqliteAutomationRunStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        _databasePath = StorageDb.DatabasePath(directory);
        _writer = new SqliteWriter(StorageDb.ConnectionString(_databasePath), _databasePath);
    }

    public SqliteAutomationRunStore(SqliteConnectionStringBuilder connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentException.ThrowIfNullOrEmpty(connectionString.DataSource, nameof(connectionString));
        _databasePath = Path.GetFullPath(connectionString.DataSource);
        _writer = new SqliteWriter(StorageDb.ConnectionString(connectionString), _databasePath);
    }

    public string DatabasePath => _databasePath;

    public Task<bool> TryInsertAsync(AutomationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        return _writer.WriteAsync(
            async (connection, transaction) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    $"INSERT INTO automation_runs ({Columns}, started_ticks) " +
                    "VALUES ($id, $automation, $name, $source, $occurrence, $status, $started, $ended, $detail, $session, " +
                    "$target, $cause, $depth, $key, $resume, $in, $out, $cacheRead, $cacheWrite, $message, $ticks) " +
                    "ON CONFLICT (occurrence) DO NOTHING";
                Bind(command, run);
                command.Parameters.AddWithValue("$ticks", StorageDb.TimestampTicks(run.StartedAt));
                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
            },
            ct);
    }

    /// <summary>Rewrites every field except the id, the occurrence and the start of the run; an unknown id changes nothing.</summary>
    public Task UpdateAsync(AutomationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        return _writer.WriteAsync(
            async (connection, transaction) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE automation_runs SET automation_id = $automation, automation_name = $name, source = $source, " +
                    "status = $status, ended_at = $ended, detail = $detail, session_id = $session, " +
                    "target_session_id = $target, cause_run_id = $cause, chain_depth = $depth, target_key = $key, " +
                    "resume_at = $resume, input_tokens = $in, output_tokens = $out, cache_read_tokens = $cacheRead, " +
                    "cache_write_tokens = $cacheWrite, message = $message WHERE id = $id";
                Bind(command, run, includeImmutable: false);
                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            },
            ct);
    }

    public Task<AutomationRun?> GetAsync(string runId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        return _writer.ReadAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {Columns} FROM automation_runs WHERE id = $id";
                command.Parameters.AddWithValue("$id", runId);
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRun(reader) : null;
            },
            ct);
    }

    public Task<IReadOnlyList<AutomationRun>> QueryAsync(AutomationRunQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _writer.ReadAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                var sql = new StringBuilder($"SELECT {Columns} FROM automation_runs WHERE 1 = 1");
                if (query.AutomationId is { } automationId)
                {
                    sql.Append(" AND automation_id = $automation");
                    command.Parameters.AddWithValue("$automation", automationId);
                }

                if (query.TargetSessionId is { } target)
                {
                    sql.Append(" AND target_session_id = $target");
                    command.Parameters.AddWithValue("$target", target);
                }

                if (query.Statuses is { } statuses)
                {
                    if (statuses.Count == 0)
                    {
                        return (IReadOnlyList<AutomationRun>)[];
                    }

                    sql.Append(" AND status IN (");
                    for (var i = 0; i < statuses.Count; i++)
                    {
                        var name = "$s" + i.ToString(CultureInfo.InvariantCulture);
                        sql.Append(i == 0 ? name : ", " + name);
                        command.Parameters.AddWithValue(name, statuses[i].ToString());
                    }

                    sql.Append(')');
                }

                sql.Append(" ORDER BY started_ticks DESC, id DESC LIMIT $limit");
                command.Parameters.AddWithValue("$limit", query.Limit <= 0 ? -1 : query.Limit);
                command.CommandText = sql.ToString();
                var runs = new List<AutomationRun>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    runs.Add(ReadRun(reader));
                }

                return (IReadOnlyList<AutomationRun>)runs;
            },
            ct);
    }

    public Task<int> MarkInterruptedAsync(DateTimeOffset endedAt, string message, CancellationToken ct) =>
        _writer.WriteAsync(
            async (connection, transaction) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE automation_runs SET status = 'Interrupted', ended_at = $ended, message = $message " +
                    "WHERE status IN ('Running', 'WaitingIdle')";
                command.Parameters.AddWithValue("$ended", StorageDb.FormatTimestamp(endedAt));
                command.Parameters.AddWithValue("$message", message);
                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            },
            ct);

    /// <summary>
    /// Deletes finished runs beyond the newest <paramref name="perAutomation"/> of each automation, then beyond the
    /// newest <paramref name="total"/> overall (ranks count all runs, active ones included). Active runs are never deleted.
    /// </summary>
    public Task<int> TrimAsync(int perAutomation, int total, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(perAutomation);
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        return _writer.WriteAsync(
            async (connection, transaction) =>
            {
                var deleted = 0;
                foreach (var partition in new[] { "PARTITION BY automation_id ", string.Empty })
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        "DELETE FROM automation_runs WHERE status NOT IN " + ActiveStatusesSql + " AND id IN (" +
                        "SELECT id FROM (SELECT id, ROW_NUMBER() OVER (" + partition +
                        "ORDER BY started_ticks DESC, id DESC) AS rank FROM automation_runs) WHERE rank > $limit)";
                    command.Parameters.AddWithValue("$limit", partition.Length > 0 ? perAutomation : total);
                    deleted += await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                return deleted;
            },
            ct);
    }

    public Task<string?> GetStateAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return _writer.ReadAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM automation_state WHERE key = $key";
                command.Parameters.AddWithValue("$key", key);
                return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            },
            ct);
    }

    public Task SetStateAsync(string key, string? value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return _writer.WriteAsync(
            async (connection, transaction) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = value is null
                    ? "DELETE FROM automation_state WHERE key = $key"
                    : "INSERT INTO automation_state (key, value) VALUES ($key, $value) " +
                      "ON CONFLICT (key) DO UPDATE SET value = excluded.value";
                command.Parameters.AddWithValue("$key", key);
                if (value is not null)
                {
                    command.Parameters.AddWithValue("$value", value);
                }

                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return 0;
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

    private static void Bind(SqliteCommand command, AutomationRun run, bool includeImmutable = true)
    {
        var p = command.Parameters;
        p.AddWithValue("$id", run.Id);
        p.AddWithValue("$automation", run.AutomationId);
        p.AddWithValue("$name", run.AutomationName);
        p.AddWithValue("$source", run.Source.ToString());
        p.AddWithValue("$status", run.Status.ToString());
        p.AddWithValue("$ended", OrNull(run.EndedAt is { } ended ? StorageDb.FormatTimestamp(ended) : null));
        p.AddWithValue("$detail", OrNull(run.Detail));
        p.AddWithValue("$session", OrNull(run.SessionId));
        p.AddWithValue("$target", OrNull(run.TargetSessionId));
        p.AddWithValue("$cause", OrNull(run.CauseRunId));
        p.AddWithValue("$depth", run.ChainDepth);
        p.AddWithValue("$key", OrNull(run.TargetKey));
        p.AddWithValue("$resume", OrNull(run.ResumeAt is { } resume ? StorageDb.FormatTimestamp(resume) : null));
        p.AddWithValue("$in", run.Usage is { } u ? u.InputTokens : DBNull.Value);
        p.AddWithValue("$out", run.Usage is { } u2 ? u2.OutputTokens : DBNull.Value);
        p.AddWithValue("$cacheRead", run.Usage is { } u3 ? u3.CacheReadTokens : DBNull.Value);
        p.AddWithValue("$cacheWrite", run.Usage is { } u4 ? u4.CacheWriteTokens : DBNull.Value);
        p.AddWithValue("$message", OrNull(run.Message));
        if (includeImmutable)
        {
            p.AddWithValue("$occurrence", run.Occurrence);
            p.AddWithValue("$started", StorageDb.FormatTimestamp(run.StartedAt));
        }
    }

    private static object OrNull(string? value) => value is null ? DBNull.Value : value;

    private AutomationRun ReadRun(SqliteDataReader reader)
    {
        var id = StorageDb.ReadText(reader, 0, "id", _databasePath, "an automation run");
        var row = $"automation run {id}";
        var run = new AutomationRun(
            id,
            StorageDb.ReadText(reader, 1, "automation_id", _databasePath, row),
            StorageDb.ReadText(reader, 2, "automation_name", _databasePath, row),
            ParseEnum<AutomationTriggerSource>(StorageDb.ReadText(reader, 3, "source", _databasePath, row), "source", row),
            StorageDb.ReadText(reader, 4, "occurrence", _databasePath, row),
            ParseEnum<AutomationRunStatus>(StorageDb.ReadText(reader, 5, "status", _databasePath, row), "status", row),
            StorageDb.ParseCanonicalTimestamp(StorageDb.ReadText(reader, 6, "started_at", _databasePath, row), "started_at", _databasePath, row))
        {
            EndedAt = OptionalTimestamp(reader, 7, "ended_at", row),
            Detail = StorageDb.ReadOptionalText(reader, 8, "detail", _databasePath, row),
            SessionId = StorageDb.ReadOptionalText(reader, 9, "session_id", _databasePath, row),
            TargetSessionId = StorageDb.ReadOptionalText(reader, 10, "target_session_id", _databasePath, row),
            CauseRunId = StorageDb.ReadOptionalText(reader, 11, "cause_run_id", _databasePath, row),
            ChainDepth = (int)StorageDb.ReadInt64(reader, 12, "chain_depth", _databasePath, row),
            TargetKey = StorageDb.ReadOptionalText(reader, 13, "target_key", _databasePath, row),
            ResumeAt = OptionalTimestamp(reader, 14, "resume_at", row),
            Message = StorageDb.ReadOptionalText(reader, 19, "message", _databasePath, row),
        };
        if (!reader.IsDBNull(15))
        {
            run = run with
            {
                Usage = new TokenUsage(
                    (int)StorageDb.ReadInt64(reader, 15, "input_tokens", _databasePath, row),
                    (int)StorageDb.ReadInt64(reader, 16, "output_tokens", _databasePath, row),
                    (int)StorageDb.ReadInt64(reader, 17, "cache_read_tokens", _databasePath, row),
                    (int)StorageDb.ReadInt64(reader, 18, "cache_write_tokens", _databasePath, row)),
            };
        }

        return run;
    }

    private DateTimeOffset? OptionalTimestamp(SqliteDataReader reader, int ordinal, string column, string row) =>
        StorageDb.ReadOptionalText(reader, ordinal, column, _databasePath, row) is { } text
            ? StorageDb.ParseCanonicalTimestamp(text, column, _databasePath, row)
            : null;

    private T ParseEnum<T>(string value, string column, string row)
        where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw StorageDb.Malformed(_databasePath, column, row, $"'{value}' is not a known {typeof(T).Name}", null);
}
