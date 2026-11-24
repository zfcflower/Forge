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
    /// <summary>
    /// The write gate. Deliberately never disposed: a write that was already waiting when the store went away still has to
    /// run out its own <c>finally</c> and hand the connection back, and disposing the semaphore under it turns that hand-back
    /// into an <see cref="ObjectDisposedException"/> from the release.
    /// </summary>
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
            // The writer was disposed while this call held the gate: the connection and the transaction just closed are this
            // call's to hand back, so the disposing thread never closes a transaction the write still owns.
            if (Volatile.Read(ref disposed) != 0)
            {
                await CloseAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        // Closing the connection is only safe with no write inside the gate: a write has its transaction open on that
        // connection, and closing it from here rolls the transaction back under the write that still owns it (see WriteAsync).
        if (!await gate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (!gate.Wait(0))
        {
            return;
        }

        try
        {
            Close();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The synchronous half of <see cref="CloseAsync"/>, for a store disposed without an await.</summary>
    private void Close()
    {
        Interlocked.Exchange(ref connection, null)?.Dispose();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Closes the write connection and releases the file handles the read pool still holds.</summary>
    private async ValueTask CloseAsync()
    {
        var open = Interlocked.Exchange(ref connection, null);
        if (open is not null)
        {
            await open.DisposeAsync().ConfigureAwait(false);
        }

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
