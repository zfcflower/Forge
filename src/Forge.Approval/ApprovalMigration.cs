using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Forge.Approval;

/// <summary>
/// Creates and upgrades the approval database. Schema version 0 (a new or empty file) creates the tables and performs
/// the one-time import of the legacy <c>approvals.json</c> file inside the same transaction, so an interrupted
/// migration leaves neither tables nor imported rows behind.
/// </summary>
internal static class ApprovalMigration
{
    public const string ImportMarkerKey = "legacy_import";

    private const string SelectMarkerSql = "SELECT value FROM approval_meta WHERE key = $key";

    private const string WriteMarkerSql =
        "INSERT INTO approval_meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value";

    private const string CountSql = "SELECT COUNT(*) FROM plugin_approval";

    /// <summary>Brings the schema up to <see cref="ApprovalDatabase.SchemaVersion"/>; true when legacy rows were imported.</summary>
    public static bool EnsureSchema(
        SqliteConnection connection,
        string databasePath,
        string legacyPath,
        ApprovalStoreOptions options)
    {
        if (IsCurrent(connection))
        {
            return false;
        }

        using var transaction = connection.BeginTransaction(deferred: false);
        var version = UserVersion(connection, transaction);
        if (version > ApprovalDatabase.SchemaVersion)
        {
            throw new InvalidDataException(
                $"Approval database '{databasePath}' uses schema {version}; this version of Forge supports {ApprovalDatabase.SchemaVersion}.");
        }

        var imported = false;
        if (version < ApprovalDatabase.SchemaVersion)
        {
            ApprovalDatabase.Execute(connection, transaction, ApprovalDatabase.SchemaSql);
            if (options.ImportLegacyJson)
            {
                imported = ImportLegacyJson(connection, transaction, databasePath, legacyPath);
            }

            SetUserVersion(connection, transaction, ApprovalDatabase.SchemaVersion);
        }
        else if (ReadMarker(connection, transaction) is null)
        {
            WriteMarker(connection, transaction);
        }

        transaction.Commit();
        return imported;
    }

    private static bool ImportLegacyJson(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string databasePath,
        string legacyPath)
    {
        if (ReadMarker(connection, transaction) is not null)
        {
            return false;
        }

        var imported = false;
        if (RowCount(connection, transaction) == 0 && File.Exists(legacyPath))
        {
            foreach (var record in ReadLegacyRecords(databasePath, legacyPath))
            {
                ApprovalDatabase.Write(connection, transaction, record);
                imported = true;
            }
        }

        WriteMarker(connection, transaction);
        return imported;
    }

    private static PluginRecord[] ReadLegacyRecords(string databasePath, string legacyPath)
    {
        try
        {
            using var stream = new FileStream(legacyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var records = JsonSerializer.Deserialize(stream, ApprovalJsonContext.Default.PluginRecordArray) ?? [];
            return [.. records.Where(record =>
                !string.IsNullOrWhiteSpace(record.PluginId) && !string.IsNullOrWhiteSpace(record.Version))];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Approval file '{legacyPath}' is not a valid plugin approval list; the approval database '{databasePath}' was left unchanged.",
                exception);
        }
    }

    /// <summary>
    /// True when the database is already at the current schema and carries its import marker, which is the steady state
    /// and needs no write lock at all.
    /// </summary>
    private static bool IsCurrent(SqliteConnection connection)
    {
        var version = ApprovalDatabase.Scalar(connection, "PRAGMA user_version", null);
        var current = version is long value ? value : Convert.ToInt64(version, CultureInfo.InvariantCulture);
        if (current != ApprovalDatabase.SchemaVersion)
        {
            return false;
        }

        try
        {
            return ReadMarker(connection, null) is not null;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static string? ReadMarker(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectMarkerSql;
        command.Parameters.AddWithValue("$key", ImportMarkerKey);
        return command.ExecuteScalar() as string;
    }

    private static long RowCount(SqliteConnection connection, SqliteTransaction transaction)
    {
        var count = ApprovalDatabase.Scalar(connection, CountSql, transaction);
        return count is long rows ? rows : Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }

    private static void WriteMarker(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = WriteMarkerSql;
        command.Parameters.AddWithValue("$key", ImportMarkerKey);
        command.Parameters.AddWithValue("$value", ApprovalDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    private static int UserVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        var version = ApprovalDatabase.Scalar(connection, "PRAGMA user_version", transaction);
        return (int)(version is long value ? value : Convert.ToInt64(version, CultureInfo.InvariantCulture));
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction transaction, int version) =>
        ApprovalDatabase.Execute(
            connection,
            transaction,
            "PRAGMA user_version = " + version.ToString(CultureInfo.InvariantCulture) + ";");
}
