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
        await using var command = StorageDb.Command(
            connection, "SELECT id, session_id, name, size_bytes, at FROM artifacts WHERE id = $id");
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

    /// <inheritdoc />
    /// <remarks>
    /// The rows go first, by <c>session_id</c> only, in one statement; then the session's own folder
    /// <c>&lt;directory&gt;/&lt;sessionId&gt;</c>, whose name is a single validated path segment, so nothing outside it
    /// can be reached. The <c>artifacts</c> table is not created here: a database that never stored an artifact has no
    /// rows to delete. The in-memory metadata of the session is dropped whatever happens, so a later read goes back to
    /// the database instead of serving a deleted artifact from the cache.
    /// </remarks>
    public async Task<int> DeleteSessionAsync(string sessionId, CancellationToken ct)
    {
        StoragePaths.SafeSegment(sessionId, nameof(sessionId));
        ct.ThrowIfCancellationRequested();
        var failures = new List<Exception>(2);
        var deleted = 0;
        try
        {
            deleted = await DeleteRowsAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // Any database failure: the folder is still deleted below, and the failure is reported with it.
            failures.Add(exception);
        }
        finally
        {
            foreach (var (id, info) in _infoCache)
            {
                if (string.Equals(info.SessionId, sessionId, StringComparison.Ordinal))
                {
                    _infoCache.TryRemove(id, out _);
                }
            }
        }

        try
        {
            DeleteFolder(Path.Combine(_directory, sessionId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(exception);
        }

        return failures.Count switch
        {
            0 => deleted,
            1 => throw new IOException(failures[0].Message, failures[0]),
            _ => throw new IOException(string.Join("; ", failures.Select(static f => f.Message)), new AggregateException(failures)),
        };
    }

    private async Task<int> DeleteRowsAsync(string sessionId, CancellationToken ct)
    {
        await using var connection = await StorageDb.OpenAsync(_connectionString, ct).ConfigureAwait(false);
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'artifacts'";
            if (await probe.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
            {
                return 0;
            }
        }

        await using var command = StorageDb.Command(connection, "DELETE FROM artifacts WHERE session_id = $session");
        command.Parameters.AddWithValue("$session", sessionId);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the session folder. A link in its place is removed as a link, never followed: the content behind it does not
    /// belong to the session. A folder already gone is not an error.
    /// </summary>
    private static void DeleteFolder(string folder)
    {
        var info = new DirectoryInfo(folder);
        if (!info.Exists)
        {
            if (File.Exists(folder))
            {
                throw new IOException($"'{folder}' is a file, not an artifact folder.");
            }

            return;
        }

        if (info.LinkTarget is not null)
        {
            info.Delete();
            return;
        }

        info.Delete(recursive: true);
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
