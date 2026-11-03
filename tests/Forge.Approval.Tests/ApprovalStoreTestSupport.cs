using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Forge.Approval.Tests;

internal static class ApprovalStoreTestSupport
{
    internal const string LegacySource = "legacy source";
    internal const string LegacyDependencyHash = "dep";
    internal const string LegacySdkVersion = "10.0.100";

    internal static SqliteConnection OpenDatabase(string directory, SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, ApprovalStore.FileName),
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        connection.Open();
        return connection;
    }

    internal static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static string? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    internal static long UserVersion(string directory)
    {
        var databasePath = Path.Combine(directory, ApprovalStore.FileName);
        if (!File.Exists(databasePath))
        {
            return 0;
        }

        using var connection = OpenDatabase(directory);
        try
        {
            return long.Parse(Scalar(connection, "PRAGMA user_version") ?? "0", CultureInfo.InvariantCulture);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    internal static string IntegrityCheck(string directory)
    {
        using var connection = OpenDatabase(directory);
        try
        {
            return Scalar(connection, "PRAGMA integrity_check") ?? string.Empty;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    internal static long TableRowCount(string directory, string table)
    {
        using var connection = OpenDatabase(directory);
        try
        {
            return long.Parse(Scalar(connection, $"SELECT COUNT(*) FROM {table}") ?? "0", CultureInfo.InvariantCulture);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    internal static long QueryCount(string directory, string sql)
    {
        using var connection = OpenDatabase(directory);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    internal static long SchemaTableCount(string directory)
    {
        using var connection = OpenDatabase(directory);
        try
        {
            return long.Parse(
                Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('plugin_approval', 'approval_meta')") ?? "0",
                CultureInfo.InvariantCulture);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    internal static string? ImportMarker(string directory)
    {
        using var connection = OpenDatabase(directory);
        try
        {
            return Scalar(connection, "SELECT value FROM approval_meta WHERE key = 'legacy_import'");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    internal static void EnsureUncheckedDatabase(string directory, IEnumerable<PluginRecord> rows)
    {
        Directory.CreateDirectory(directory);
        using var connection = OpenDatabase(directory, SqliteOpenMode.ReadWriteCreate);
        try
        {
            Execute(connection, """
                CREATE TABLE plugin_approval (
                    plugin_id         TEXT NOT NULL,
                    version           TEXT NOT NULL,
                    status            TEXT NOT NULL,
                    source_hash       TEXT NOT NULL,
                    dependency_hash   TEXT NOT NULL,
                    sdk_version       TEXT NOT NULL,
                    approved_hash     TEXT NULL,
                    created_at        TEXT NOT NULL,
                    updated_at        TEXT NOT NULL,
                    updated_utc_ticks INTEGER NOT NULL,
                    PRIMARY KEY (plugin_id, version)
                ) WITHOUT ROWID;

                CREATE TABLE approval_meta (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                ) WITHOUT ROWID;
                """);
            Execute(connection, "INSERT INTO approval_meta (key, value) VALUES ('legacy_import', '2025-01-01T00:00:00.0000000+00:00')");
            foreach (var record in rows)
            {
                InsertRow(connection, record.PluginId, record.Version, record.Status.ToString(), record);
            }

            Execute(connection, "PRAGMA user_version = 1");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    internal static void InsertRawRow(
        string directory,
        string pluginId,
        string version,
        string status,
        string sourceHash = "s",
        string dependencyHash = "d",
        string sdkVersion = "sdk")
    {
        using var connection = OpenDatabase(directory);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO plugin_approval
                    (plugin_id, version, status, source_hash, dependency_hash, sdk_version, approved_hash, created_at, updated_at, updated_utc_ticks)
                VALUES
                    ($pluginId, $version, $status, $sourceHash, $dependencyHash, $sdkVersion, NULL, '2025-01-01T00:00:00.0000000+00:00', '2025-01-01T00:00:00.0000000+00:00', 0)
                """;
            command.Parameters.AddWithValue("$pluginId", pluginId);
            command.Parameters.AddWithValue("$version", version);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$sourceHash", sourceHash);
            command.Parameters.AddWithValue("$dependencyHash", dependencyHash);
            command.Parameters.AddWithValue("$sdkVersion", sdkVersion);
            command.ExecuteNonQuery();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    internal static string LegacyJson(params (string Id, string Version, PluginStatus Status)[] rows)
    {
        var sourceHash = Hashing.SourceHash(LegacySource);
        var payload = rows.Select(row => new
        {
            pluginId = row.Id,
            version = row.Version,
            sourceHash,
            dependencyHash = LegacyDependencyHash,
            sdkVersion = LegacySdkVersion,
            approvedHash = row.Status == PluginStatus.Approved
                ? Hashing.ApprovalFingerprint(sourceHash, LegacyDependencyHash, LegacySdkVersion)
                : null,
            status = row.Status.ToString(),
            createdAt = "2025-01-01T00:00:00.0000000+00:00",
            updatedAt = "2025-01-01T00:00:00.0000000+00:00",
        });
        return JsonSerializer.Serialize(payload);
    }

    internal static PluginRecord LegacyCandidate(string pluginId, string version) => new()
    {
        PluginId = pluginId,
        Version = version,
        SourceHash = Hashing.SourceHash(LegacySource),
        DependencyHash = LegacyDependencyHash,
        SdkVersion = LegacySdkVersion,
    };

    internal static void SetReadOnly(string path, bool readOnly)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, readOnly ? FileAttributes.ReadOnly : FileAttributes.Normal);
            return;
        }

        File.SetUnixFileMode(
            path,
            readOnly
                ? UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    internal static string? WaitForLine(Process process, TimeSpan timeout)
    {
        var pending = Task.Run(process.StandardOutput.ReadLine);
        return pending.Wait(timeout) ? pending.Result : null;
    }

    internal static (int ExitCode, string Output) WaitForChild(Process process, TimeSpan timeout)
    {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }

            process.WaitForExit(TimeSpan.FromSeconds(30));
            return (-1, "timed out waiting for the child process; " + (stdout.Result + stderr.Result).Trim());
        }

        return (process.ExitCode, (stdout.Result + stderr.Result).Trim());
    }

    private static void InsertRow(SqliteConnection connection, string pluginId, string version, string status, PluginRecord record)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO plugin_approval
                (plugin_id, version, status, source_hash, dependency_hash, sdk_version, approved_hash, created_at, updated_at, updated_utc_ticks)
            VALUES
                ($pluginId, $version, $status, $sourceHash, $dependencyHash, $sdkVersion, $approvedHash, $createdAt, $updatedAt, $updatedTicks)
            """;
        command.Parameters.AddWithValue("$pluginId", pluginId);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$sourceHash", record.SourceHash);
        command.Parameters.AddWithValue("$dependencyHash", record.DependencyHash);
        command.Parameters.AddWithValue("$sdkVersion", record.SdkVersion);
        command.Parameters.AddWithValue("$approvedHash", (object?)record.ApprovedHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", record.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedAt", record.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedTicks", record.UpdatedAt.UtcTicks);
        command.ExecuteNonQuery();
    }

}
