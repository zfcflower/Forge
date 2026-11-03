using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Forge.Approval;

/// <summary>Connection, pragma, schema and row mapping of the approval database.</summary>
internal static class ApprovalDatabase
{
    public const int SchemaVersion = 1;
    public const string WalJournalMode = "wal";
    public const string RollbackJournalMode = "delete";

    public const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS plugin_approval (
            plugin_id         TEXT NOT NULL,
            version           TEXT NOT NULL,
            status            TEXT NOT NULL CHECK (status IN ('Draft','Approved','Disabled','Uninstalled')),
            source_hash       TEXT NOT NULL,
            dependency_hash   TEXT NOT NULL,
            sdk_version       TEXT NOT NULL,
            approved_hash     TEXT NULL,
            created_at        TEXT NOT NULL,
            updated_at        TEXT NOT NULL,
            updated_utc_ticks INTEGER NOT NULL,
            PRIMARY KEY (plugin_id, version)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS approval_meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        ) WITHOUT ROWID;
        """;

    private const string Columns =
        "plugin_id, version, status, source_hash, dependency_hash, sdk_version, approved_hash, created_at, updated_at";

    private const string SelectAllSql = "SELECT " + Columns + " FROM plugin_approval";

    private const string SelectRowSql = SelectAllSql + " WHERE plugin_id = $pluginId AND version = $version";

    private const string UpsertSql = """
        INSERT INTO plugin_approval
            (plugin_id, version, status, source_hash, dependency_hash, sdk_version, approved_hash, created_at, updated_at, updated_utc_ticks)
        VALUES
            ($pluginId, $version, $status, $sourceHash, $dependencyHash, $sdkVersion, $approvedHash, $createdAt, $updatedAt, $updatedTicks)
        ON CONFLICT (plugin_id, version) DO UPDATE SET
            status = excluded.status,
            source_hash = excluded.source_hash,
            dependency_hash = excluded.dependency_hash,
            sdk_version = excluded.sdk_version,
            approved_hash = excluded.approved_hash,
            created_at = excluded.created_at,
            updated_at = excluded.updated_at,
            updated_utc_ticks = excluded.updated_utc_ticks
        """;

    /// <summary>
    /// Opens the database, applies the per-connection pragmas in the order that matters (<c>busy_timeout</c> before
    /// the journal switch, which itself takes a lock) and reports the journal mode SQLite actually selected.
    /// </summary>
    public static SqliteConnection Open(string databasePath, ApprovalStoreOptions options, out string journalMode)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = (int)Math.Ceiling(options.BusyTimeout.TotalSeconds),
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            var busyMilliseconds = ((long)options.BusyTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            Execute(connection, null, "PRAGMA busy_timeout = " + busyMilliseconds + ";");

            journalMode = Scalar(connection, "PRAGMA journal_mode = WAL", null) as string ?? RollbackJournalMode;
            Execute(connection, null, "PRAGMA synchronous = FULL;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public static long DataVersion(SqliteConnection connection)
    {
        var value = Scalar(connection, "PRAGMA data_version", null);
        return value is long ticks ? ticks : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static void Checkpoint(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        command.ExecuteNonQuery();
    }

    public static Dictionary<(string PluginId, string Version), PluginRecord> ReadAll(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string databasePath)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectAllSql;
        using var reader = command.ExecuteReader();
        var records = new Dictionary<(string PluginId, string Version), PluginRecord>();
        while (reader.Read())
        {
            var record = ToRecord(reader, databasePath);
            records[(record.PluginId, record.Version)] = record;
        }

        return records;
    }

    public static PluginRecord? ReadRow(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string pluginId,
        string version,
        string databasePath)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectRowSql;
        command.Parameters.AddWithValue("$pluginId", pluginId);
        command.Parameters.AddWithValue("$version", version);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ToRecord(reader, databasePath) : null;
    }

    public static void Write(SqliteConnection connection, SqliteTransaction transaction, PluginRecord record)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertSql;
        command.Parameters.AddWithValue("$pluginId", record.PluginId);
        command.Parameters.AddWithValue("$version", record.Version);
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        command.Parameters.AddWithValue("$sourceHash", record.SourceHash);
        command.Parameters.AddWithValue("$dependencyHash", record.DependencyHash);
        command.Parameters.AddWithValue("$sdkVersion", record.SdkVersion);
        command.Parameters.AddWithValue("$approvedHash", (object?)record.ApprovedHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(record.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(record.UpdatedAt));
        command.Parameters.AddWithValue("$updatedTicks", record.UpdatedAt.UtcTicks);
        command.ExecuteNonQuery();
    }

    public const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffzzz";

    public static string FormatTimestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTimestamp(
        string value,
        string column,
        string pluginId,
        string version,
        string databasePath)
    {
        if (!DateTimeOffset.TryParseExact(value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
            !string.Equals(FormatTimestamp(parsed), value, StringComparison.Ordinal))
        {
            throw Malformed(
                databasePath,
                column,
                pluginId,
                version,
                $"'{value}' is not the offset-carrying round-trip timestamp Forge writes ('O')",
                null);
        }

        return parsed;
    }

    private static string Text(
        SqliteDataReader reader,
        int ordinal,
        string column,
        string databasePath,
        string? pluginId,
        string? version)
    {
        try
        {
            return reader.GetString(ordinal);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidCastException)
        {
            throw Malformed(databasePath, column, pluginId, version, "the value is not readable text", exception);
        }
    }

    private static InvalidDataException Malformed(
        string databasePath,
        string column,
        string? pluginId,
        string? version,
        string reason,
        Exception? innerException)
    {
        var row = pluginId is null ? "a row whose identity cannot be read" : $"{pluginId} {version}";
        return new InvalidDataException(
            $"Approval database '{databasePath}' holds a malformed {column} value for {row}: {reason}.",
            innerException);
    }

    private static PluginRecord ToRecord(SqliteDataReader reader, string databasePath)
    {
        var pluginId = Text(reader, 0, "plugin_id", databasePath, null, null);
        var version = Text(reader, 1, "version", databasePath, pluginId, null);
        return new PluginRecord
        {
            PluginId = pluginId,
            Version = version,
            Status = ParseStatus(Text(reader, 2, "status", databasePath, pluginId, version), pluginId, version, databasePath),
            SourceHash = Text(reader, 3, "source_hash", databasePath, pluginId, version),
            DependencyHash = Text(reader, 4, "dependency_hash", databasePath, pluginId, version),
            SdkVersion = Text(reader, 5, "sdk_version", databasePath, pluginId, version),
            ApprovedHash = reader.IsDBNull(6)
                ? null
                : Text(reader, 6, "approved_hash", databasePath, pluginId, version),
            CreatedAt = ParseTimestamp(
                Text(reader, 7, "created_at", databasePath, pluginId, version),
                "created_at",
                pluginId,
                version,
                databasePath),
            UpdatedAt = ParseTimestamp(
                Text(reader, 8, "updated_at", databasePath, pluginId, version),
                "updated_at",
                pluginId,
                version,
                databasePath),
        };
    }

    private static PluginStatus ParseStatus(string value, string pluginId, string version, string databasePath) => value switch
    {
        nameof(PluginStatus.Draft) => PluginStatus.Draft,
        nameof(PluginStatus.Approved) => PluginStatus.Approved,
        nameof(PluginStatus.Disabled) => PluginStatus.Disabled,
        nameof(PluginStatus.Uninstalled) => PluginStatus.Uninstalled,
        _ => throw Malformed(
            databasePath,
            "status",
            pluginId,
            version,
            $"'{value}' is not one of Draft, Approved, Disabled, Uninstalled",
            null),
    };
}
