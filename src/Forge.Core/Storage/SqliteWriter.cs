using Microsoft.Data.Sqlite;

namespace Forge.Core.Storage;

/// <summary>One unit of work inside an already-open write transaction.</summary>
internal delegate ValueTask<T> SqliteWrite<T>(SqliteConnection connection, SqliteTransaction transaction);

/// <summary>One unit of work on a short-lived read connection.</summary>
internal delegate ValueTask<T> SqliteRead<T>(SqliteConnection connection);

/// <summary>
/// One store's access to <c>forge.db</c>: a single long-lived connection used only for writes, serialized by a
/// semaphore and wrapped in a <c>BEGIN IMMEDIATE</c> transaction per call, plus a short-lived pooled connection per
/// read so a reader never waits for the writer. The schema is brought up to date once, on first use.
/// </summary>
internal sealed class SqliteWriter : IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string databasePath;

    private SqliteConnection? connection;
    private int migrated;
    private int disposed;

    public SqliteWriter(string connectionString, string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ConnectionString = connectionString;
        this.databasePath = databasePath;
    }

    public string ConnectionString { get; }

    public async Task<T> ReadAsync<T>(SqliteRead<T> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        ThrowIfDisposed();
        await using var read = await StorageDb.OpenAsync(ConnectionString, ct).ConfigureAwait(false);
        await EnsureMigratedAsync(read, ct).ConfigureAwait(false);
        return await work(read).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one <c>BEGIN IMMEDIATE</c> transaction. The token is checked before the
    /// transaction starts; a cancellation mid-transaction rolls it back.
    /// </summary>
    public async Task<T> WriteAsync<T>(SqliteWrite<T> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        ct.ThrowIfCancellationRequested();
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var writer = await EnsureOpenAsync(ct).ConfigureAwait(false);
            using var transaction = writer.BeginTransaction(deferred: false);
            var result = await work(writer, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        var open = connection;
        connection = null;
        if (open is not null)
        {
            await open.DisposeAsync().ConfigureAwait(false);
        }

        gate.Dispose();
        SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        var open = connection;
        connection = null;
        open?.Dispose();
        gate.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private async Task<SqliteConnection> EnsureOpenAsync(CancellationToken ct)
    {
        if (connection is { } existing)
        {
            return existing;
        }

        var opened = await StorageDb.OpenAsync(ConnectionString, ct).ConfigureAwait(false);
        try
        {
            await StorageDb.ConfigureAsync(opened, ct).ConfigureAwait(false);
            await EnsureMigratedAsync(opened, ct).ConfigureAwait(false);
            connection = opened;
            return opened;
        }
        catch
        {
            await opened.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureMigratedAsync(SqliteConnection target, CancellationToken ct)
    {
        if (Volatile.Read(ref migrated) != 0)
        {
            return;
        }

        await StorageSchema.EnsureAsync(target, databasePath, ct).ConfigureAwait(false);
        Volatile.Write(ref migrated, 1);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
}
