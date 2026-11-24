using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A store's disposal has to leave a write that is already running alone. <c>SqliteWriter</c> keeps one long-lived connection
/// for writes, and a write holds an open <c>BEGIN IMMEDIATE</c> transaction on it: closing that connection from the disposing
/// thread rolls the transaction back while the write's own <c>using</c> still owns it, and Microsoft.Data.Sqlite completes the
/// same transaction twice — a NullReferenceException inside the library, which surfaced once as a rare full-suite failure in
/// <c>ReopenRoundStatsTests</c> (the composer's background save was still running when the store it writes through was
/// disposed). The store whose disposal races a save is the shape of the bug, so the invariant is asserted at that level: the
/// in-flight write finishes, and only the writer takes no further work.
/// </summary>
public sealed class SqliteWriterDisposalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-sqlite-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // The pool may still hand the file back a moment late; the tree is only this test's temp folder.
        }
    }

    [Fact]
    public async Task Disposing_a_writer_lets_a_write_that_is_already_running_finish()
    {
        Directory.CreateDirectory(directory);
        var path = StorageDb.DatabasePath(directory);
        var writer = new SqliteWriter(StorageDb.ConnectionString(path), path);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = Task.Run(() => writer.WriteAsync(
            async (connection, transaction) =>
            {
                entered.SetResult();
                await release.Task;
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "CREATE TABLE kept (x INTEGER);";
                await command.ExecuteNonQueryAsync();
                return 42;
            },
            CancellationToken.None));

        // The write is inside its transaction; the store goes away under it (a background save outliving the view that
        // started it), and only then does the write get to finish.
        await entered.Task;
        await writer.DisposeAsync();
        release.SetResult();

        Assert.Equal(42, await write);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => writer.WriteAsync((_, _) => ValueTask.FromResult(0), CancellationToken.None));
    }
}
