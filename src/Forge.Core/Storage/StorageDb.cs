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

    /// <summary>
    /// <see cref="TimestampFormat"/> as a SQLite <c>GLOB</c> pattern. The schema's v3 triggers match every timestamp
    /// write against it, so the shape this codebase reads is enforced by the database instead of only by its writers:
    /// a tool that opens <c>forge.db</c> on its own can no longer store a value every later read would refuse.
    /// </summary>
    public const string TimestampGlobPattern =
        "[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9]." +
        "[0-9][0-9][0-9][0-9][0-9][0-9][0-9][+-][0-9][0-9]:[0-9][0-9]";

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
        if (!TryParseCanonical(value, out var parsed))
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

    /// <summary>
    /// Rewrites a stored timestamp that names an unambiguous instant (any ISO-8601 spelling that carries its own
    /// numeric offset, or <c>Z</c>) but is not the shape this codebase reads — a narrower fraction written by another
    /// tool, a <c>Z</c> suffix, surrounding blanks. The instant and its offset are preserved; only the text is
    /// canonicalized. Returns null for anything else, and the caller leaves those rows alone: a value without an
    /// offset would be read in the local time zone, so repairing it would invent an instant rather than restore one.
    /// </summary>
    public static string? TryNormalizeTimestamp(string value)
    {
        var text = value.AsSpan().Trim();
        if (text.Length == 0 || !CarriesOffset(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? FormatTimestamp(parsed)
            : null;
    }

    private static bool TryParseCanonical(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParseExact(value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed) &&
        string.Equals(FormatTimestamp(parsed), value, StringComparison.Ordinal);

    /// <summary>True when the text ends in <c>Z</c> or a <c>±hh:mm</c> offset, i.e. it does not rely on the local zone.</summary>
    private static bool CarriesOffset(ReadOnlySpan<char> text)
    {
        if (text[^1] is 'Z' or 'z')
        {
            return true;
        }

        if (text.Length < 6)
        {
            return false;
        }

        var tail = text[^6..];
        return tail[0] is '+' or '-'
            && char.IsAsciiDigit(tail[1])
            && char.IsAsciiDigit(tail[2])
            && tail[3] == ':'
            && char.IsAsciiDigit(tail[4])
            && char.IsAsciiDigit(tail[5]);
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
