using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Storage;

/// <summary>Connection and formatting primitives shared by the SQLite-backed stores.</summary>
internal static class StorageDb
{
    public const string FileName = "forge.db";
    public const int CommandTimeoutSeconds = 30;

    public static string ConnectionString(string databasePath) => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Default,
        Pooling = true,
        DefaultTimeout = CommandTimeoutSeconds,
    }.ToString();

    public static async Task<SqliteConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Applies the per-connection pragmas; WAL mode itself is persisted in the database file by this call.</summary>
    public static async Task ConfigureAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
