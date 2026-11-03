using Microsoft.Data.Sqlite;

namespace Forge.Integration.Tests;

/// <summary>
/// Read-only view of the approval database (<c>approvals.db</c>) the extension host keeps in its data directory. The
/// tests open their own connection, so every read observes what the host has committed rather than a cached snapshot.
/// </summary>
internal static class ApprovalDatabase
{
    public const string FileName = "approvals.db";

    private const int DefaultTimeoutSeconds = 5;

    private const string SelectRecordSql = """
        SELECT plugin_id, version, source_hash, dependency_hash, sdk_version, status
        FROM plugin_approval
        WHERE plugin_id = $pluginId AND version = $version
        """;

    public static string PathOf(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    /// <summary>The persisted record for one plugin version, or null when the database or the row does not exist yet.</summary>
    public static ApprovalRecord? ReadRecord(string dataDirectory, string pluginId, string version)
    {
        var path = PathOf(dataDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectRecordSql;
        command.Parameters.AddWithValue("$pluginId", pluginId);
        command.Parameters.AddWithValue("$version", version);
        try
        {
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new ApprovalRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1 && exception.Message.Contains("no such table", StringComparison.Ordinal))
        {
            return null;
        }
    }

    private static string ConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
        DefaultTimeout = DefaultTimeoutSeconds,
    }.ToString();
}
