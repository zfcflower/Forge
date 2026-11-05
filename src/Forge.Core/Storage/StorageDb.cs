using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Storage;

/// <summary>Connection and formatting primitives shared by the SQLite-backed stores.</summary>
internal static class StorageDb
{
    public const string FileName = "forge.db";
    public const int CommandTimeoutSeconds = 30;

    /// <summary>The round-trip timestamp shape every store in <c>forge.db</c> writes and the only one it accepts.</summary>
    public const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffzzz";

    public static string DatabasePath(string dataDirectory) =>
        Path.Combine(Path.GetFullPath(dataDirectory), FileName);

    public static string ConnectionString(string databasePath) => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Default,
        Pooling = true,
        DefaultTimeout = CommandTimeoutSeconds,
    }.ToString();

    /// <summary>Applies the same defaults to a connection string a caller supplied.</summary>
    public static string ConnectionString(SqliteConnectionStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (string.IsNullOrEmpty(builder.DataSource))
        {
            throw new ArgumentException("A data source is required.", nameof(builder));
        }

        if (builder.DefaultTimeout <= 0)
        {
            builder.DefaultTimeout = CommandTimeoutSeconds;
        }

        return builder.ToString();
    }

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

    /// <summary>Sort key for a timestamp: ISO text sorts by offset, ticks sort by instant.</summary>
    public static long TimestampTicks(DateTimeOffset value) => value.UtcTicks;

    /// <summary>
    /// Reads a timestamp column written by this codebase and rejects anything that is not byte-for-byte the
    /// <c>"O"</c> shape; a value without an offset would otherwise be read in the local time zone.
    /// </summary>
    public static DateTimeOffset ParseCanonicalTimestamp(string value, string column, string databasePath, string row)
    {
        if (!DateTimeOffset.TryParseExact(value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
            !string.Equals(FormatTimestamp(parsed), value, StringComparison.Ordinal))
        {
            throw Malformed(
                databasePath,
                column,
                row,
                $"'{value}' is not the offset-carrying round-trip timestamp Forge writes ('O')",
                null);
        }

        return parsed;
    }

    public static string ReadText(SqliteDataReader reader, int ordinal, string column, string databasePath, string row)
    {
        try
        {
            return reader.GetString(ordinal);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidCastException)
        {
            throw Malformed(databasePath, column, row, "the value is not readable text", exception);
        }
    }

    public static string? ReadOptionalText(SqliteDataReader reader, int ordinal, string column, string databasePath, string row) =>
        reader.IsDBNull(ordinal) ? null : ReadText(reader, ordinal, column, databasePath, row);

    public static long ReadInt64(SqliteDataReader reader, int ordinal, string column, string databasePath, string row)
    {
        try
        {
            return reader.GetInt64(ordinal);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidCastException)
        {
            throw Malformed(databasePath, column, row, "the value is not readable as a 64-bit integer", exception);
        }
    }

    public static InvalidDataException Malformed(string databasePath, string column, string row, string reason, Exception? innerException) =>
        new($"Database '{databasePath}' holds a malformed {column} value for {row}: {reason}.", innerException);
}
