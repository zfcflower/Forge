using System.Collections.Concurrent;
using System.Security.Cryptography;
using Forge.Abstractions;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Storage;

/// <summary>
/// Stores artifact bytes under <c>&lt;directory&gt;/&lt;sessionId&gt;/&lt;id&gt;</c> and their metadata in the
/// SQLite <c>artifacts</c> table. Range reads go through <see cref="RandomAccess"/> so a large
/// artifact is never materialized just to serve a window of it.
/// </summary>
public sealed class FileArtifactStore : IArtifactStore, IAsyncDisposable, IDisposable
{
    private const int IdLength = 12;
    private const int WriteBufferSize = 64 * 1024;
    private const int InfoCacheLimit = 4096;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS artifacts (
            id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            name TEXT NOT NULL,
            size_bytes INTEGER NOT NULL,
            at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_artifacts_session ON artifacts (session_id, at);
        """;

    private readonly string _directory;
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, ArtifactInfo> _infoCache = new(StringComparer.Ordinal);
    private readonly Lazy<Task> _initialization;

    public FileArtifactStore(string directory, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _directory = Path.GetFullPath(directory);
        _connectionString = connectionString;
        _initialization = new Lazy<Task>(InitializeAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Artifacts and metadata living beside a <see cref="SqliteSessionStore"/> data directory.</summary>
    public static FileArtifactStore ForDataDirectory(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var root = Path.GetFullPath(dataDirectory);
        return new FileArtifactStore(
            Path.Combine(root, "artifacts"),
            StorageDb.ConnectionString(Path.Combine(root, StorageDb.FileName)));
    }

    public string DirectoryPath => _directory;

    public async Task<ArtifactInfo> PutAsync(string sessionId, string name, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        ArgumentNullException.ThrowIfNull(name);
        await EnsureInitializedAsync().ConfigureAwait(false);

        var id = NewId();
        var info = new ArtifactInfo(id, sessionId, name, content.Length, DateTimeOffset.UtcNow);
        var sessionDirectory = Path.Combine(_directory, sessionId);
        System.IO.Directory.CreateDirectory(sessionDirectory);
        var path = Path.Combine(sessionDirectory, id);
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                WriteBufferSize,
                FileOptions.Asynchronous))
            {
                await stream.WriteAsync(content, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(temporary, path);

            await using var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO artifacts (id, session_id, name, size_bytes, at)
                VALUES ($id, $session, $name, $size, $at)
                """;
            command.Parameters.AddWithValue("$id", info.Id);
            command.Parameters.AddWithValue("$session", info.SessionId);
            command.Parameters.AddWithValue("$name", info.Name);
            command.Parameters.AddWithValue("$size", info.SizeBytes);
            command.Parameters.AddWithValue("$at", StorageDb.FormatTimestamp(info.At));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(temporary);
            TryDelete(path);
            throw;
        }

        Cache(info);
        return info;
    }

    public async Task<ArtifactInfo?> GetInfoAsync(string artifactId, CancellationToken ct)
    {
        StoragePaths.SafeSegment(artifactId, nameof(artifactId));
        if (_infoCache.TryGetValue(artifactId, out var cached))
        {
            return cached;
        }

        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, session_id, name, size_bytes, at FROM artifacts WHERE id = $id";
        command.Parameters.AddWithValue("$id", artifactId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var info = new ArtifactInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            StorageDb.ParseTimestamp(reader.GetString(4)));
        Cache(info);
        return info;
    }

    public async Task<ReadOnlyMemory<byte>> ReadAsync(string artifactId, long offset, int? length, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (length is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Length cannot be negative.");
        }

        var info = await GetInfoAsync(artifactId, ct).ConfigureAwait(false)
                   ?? throw new KeyNotFoundException($"Artifact '{artifactId}' does not exist.");

        var available = Math.Max(0, info.SizeBytes - offset);
        var count = (int)Math.Min(length ?? available, available);
        if (count == 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var buffer = new byte[count];
        var handle = File.OpenHandle(
            Path.Combine(_directory, info.SessionId, artifactId),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.Asynchronous);
        try
        {
            var total = 0;
            while (total < count)
            {
                var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(total), offset + total, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total == count ? buffer : buffer.AsMemory(0, total);
        }
        finally
        {
            handle.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _infoCache.Clear();
        SqliteConnection.ClearAllPools();
    }

    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[IdLength];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    private void Cache(ArtifactInfo info)
    {
        if (_infoCache.Count >= InfoCacheLimit)
        {
            _infoCache.Clear();
        }

        _infoCache[info.Id] = info;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private Task EnsureInitializedAsync() => _initialization.Value;

    private async Task InitializeAsync()
    {
        System.IO.Directory.CreateDirectory(_directory);
        await using var connection = await StorageDb.OpenAsync(_connectionString, CancellationToken.None).ConfigureAwait(false);
        await StorageDb.ConfigureAsync(connection, CancellationToken.None).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
