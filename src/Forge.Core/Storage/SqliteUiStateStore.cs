using Forge.Abstractions;

namespace Forge.Core.Storage;

/// <summary>
/// Opaque UI state blobs (layout snapshot, open tabs) in <c>forge.db</c>, one row per key, written with an
/// <c>INSERT ... ON CONFLICT DO UPDATE</c> so <see cref="PutAsync"/> is a single statement and last writer wins.
/// Each blob carries its own format version; the reader decides whether a version it does not know is usable.
/// </summary>
public sealed class SqliteUiStateStore : IUiStateStore, IAsyncDisposable, IDisposable
{
    private readonly SqliteWriter _writer;
    private readonly string _databasePath;
    private int _disposed;

    public SqliteUiStateStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        _databasePath = StorageDb.DatabasePath(directory);
        _writer = new SqliteWriter(StorageDb.ConnectionString(_databasePath), _databasePath);
    }

    public SqliteUiStateStore(Microsoft.Data.Sqlite.SqliteConnectionStringBuilder connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentException.ThrowIfNullOrEmpty(connectionString.DataSource, nameof(connectionString));
        _databasePath = Path.GetFullPath(connectionString.DataSource);
        _writer = new SqliteWriter(StorageDb.ConnectionString(connectionString), _databasePath);
    }

    public string ConnectionString => _writer.ConnectionString;

    /// <summary>Full path of the database file this store reads and writes.</summary>
    public string DatabasePath => _databasePath;

    public Task<UiStateEntry?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _writer.ReadAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT key, version, json, updated_at FROM ui_state WHERE key = $key";
                command.Parameters.AddWithValue("$key", key);
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadEntry(reader) : null;
            },
            ct);
    }

    public Task PutAsync(string key, int version, string json, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(json);
        return _writer.WriteAsync(
            async (connection, transaction) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO ui_state (key, version, json, updated_at) VALUES ($key, $version, $json, $at)
                    ON CONFLICT (key) DO UPDATE SET
                        version = excluded.version,
                        json = excluded.json,
                        updated_at = excluded.updated_at
                    """;
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$version", version);
                command.Parameters.AddWithValue("$json", json);
                command.Parameters.AddWithValue("$at", StorageDb.FormatTimestamp(DateTimeOffset.UtcNow));
                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            },
            ct);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _writer.WriteAsync(
            async (connection, transaction) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM ui_state WHERE key = $key";
                command.Parameters.AddWithValue("$key", key);
                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
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

    private UiStateEntry ReadEntry(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var key = StorageDb.ReadText(reader, 0, "key", _databasePath, "a row whose key cannot be read");
        var row = $"ui_state '{key}'";
        return new UiStateEntry(
            key,
            (int)StorageDb.ReadInt64(reader, 1, "version", _databasePath, row),
            StorageDb.ReadText(reader, 2, "json", _databasePath, row),
            StorageDb.ParseCanonicalTimestamp(
                StorageDb.ReadText(reader, 3, "updated_at", _databasePath, row),
                "updated_at",
                _databasePath,
                row));
    }
}
