using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalFailureTests
{
    /// <summary>
    /// Makes the database and every SQLite sidecar writable again. SQLite gives a sidecar the mode of the database
    /// file, so the sidecars a read-only connection created (-wal/-shm/-journal) are read-only too and the retry
    /// below would fail with SQLITE_READONLY even after the database itself is writable.
    /// </summary>
    private static void RestoreDatabaseWritable(string directory)
    {
        foreach (var path in Directory.GetFiles(directory, ApprovalStore.FileName + "*"))
        {
            ApprovalStoreTestSupport.SetReadOnly(path, false);
        }
    }

    [Fact]
    public void Write_to_a_read_only_database_throws_and_leaves_the_snapshot_unchanged()
    {
        using var directory = new TempApprovalDirectory();
        using (var seed = new ApprovalStore(directory.Path))
        {
            seed.SetApproved("ro.keep", "1.0.0", "keep", "dep", "sdk");
        }

        var databasePath = Path.Combine(directory.Path, ApprovalStore.FileName);
        ApprovalStoreTestSupport.SetReadOnly(databasePath, true);
        try
        {
            using var store = new ApprovalStore(directory.Path);
            var before = store.Get("ro.keep", "1.0.0");
            var exception = Assert.Throws<SqliteException>(() => store.SetApproved("ro.keep", "1.0.0", "changed", "dep", "sdk"));

            Assert.Equal(8, exception.SqliteErrorCode);
            Assert.Equal(before, store.Get("ro.keep", "1.0.0"));
            Assert.Equal("keep", store.Get("ro.keep", "1.0.0")?.SourceHash);
            Assert.Equal(1, store.Count);
        }
        finally
        {
            RestoreDatabaseWritable(directory.Path);
        }

        using var retry = new ApprovalStore(directory.Path);
        retry.SetApproved("ro.keep", "1.0.0", "changed", "dep", "sdk");
        Assert.Equal("changed", retry.Get("ro.keep", "1.0.0")?.SourceHash);
    }

    [Fact]
    public void Locked_database_fails_within_the_busy_budget_and_retries_cleanly()
    {
        using var directory = new TempApprovalDirectory();
        using var store = new ApprovalStore(directory.Path, new ApprovalStoreOptions { BusyTimeout = TimeSpan.FromSeconds(2.5) });
        store.SetApproved("seed.plugin", "1.0.0", "s", "dep", "sdk");

        using var locker = ApprovalStoreTestSupport.OpenDatabase(directory.Path);
        ApprovalStoreTestSupport.Execute(locker, "BEGIN IMMEDIATE;");
        try
        {
            var elapsed = Stopwatch.StartNew();
            var exception = Assert.Throws<SqliteException>(() => store.SetApproved("seed.plugin", "1.0.0", "changed", "dep", "sdk"));
            elapsed.Stop();

            Assert.Equal(5, exception.SqliteErrorCode);
            Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(2.5), $"timed out after {elapsed.Elapsed}");
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2.5) * 2 + TimeSpan.FromSeconds(15), $"took {elapsed.Elapsed}");
            Assert.Equal("s", store.Get("seed.plugin", "1.0.0")?.SourceHash);
            Assert.Equal(1, store.Count);
        }
        finally
        {
            ApprovalStoreTestSupport.Execute(locker, "ROLLBACK;");
            SqliteConnection.ClearAllPools();
        }

        store.SetApproved("seed.plugin", "1.0.0", "changed", "dep", "sdk");
        Assert.Equal("changed", store.Get("seed.plugin", "1.0.0")?.SourceHash);
    }

    [Fact]
    public void Cancelled_write_throws_and_writes_nothing()
    {
        using var directory = new TempApprovalDirectory();
        using var store = new ApprovalStore(directory.Path);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => store.SetApproved("c.plugin", "1.0.0", "s", "dep", "sdk", cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => store.Upsert(ApprovalFixtures.ApprovedRecord(), cancelled.Token));

        Assert.Equal(0, store.Count);
        Assert.Null(store.Get("c.plugin", "1.0.0"));
        Assert.Null(store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version));
    }

    [Fact]
    public void Dispose_removes_journal_files_and_frees_the_directory()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        store.SetApproved("a.plugin", "1.0.0", "s", "dep", "sdk");
        store.Dispose();

        Assert.False(File.Exists(Path.Combine(directory.Path, ApprovalStore.FileName + "-wal")));
        Assert.False(File.Exists(Path.Combine(directory.Path, ApprovalStore.FileName + "-shm")));
        Directory.Delete(directory.Path, recursive: true);
        Assert.False(Directory.Exists(directory.Path));
    }

    [Fact]
    public void Use_after_dispose_throws_and_dispose_is_idempotent()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        store.SetApproved("a.plugin", "1.0.0", "s", "dep", "sdk");
        store.Dispose();

        Assert.Throws<ObjectDisposedException>(() => store.Count);
        Assert.Throws<ObjectDisposedException>(() => store.Get("a.plugin", "1.0.0"));
        Assert.Throws<ObjectDisposedException>(() => store.List());
        Assert.Throws<ObjectDisposedException>(() => store.Reload());
        Assert.Throws<ObjectDisposedException>(() => store.Upsert(ApprovalFixtures.ApprovedRecord()));
        Assert.Throws<ObjectDisposedException>(() => store.SetApproved("a.plugin", "1.0.0", "s", "dep", "sdk"));
        Assert.Throws<ObjectDisposedException>(() => store.Revoke("a.plugin", "1.0.0"));
        Assert.Throws<ObjectDisposedException>(() => store.Disable("a.plugin", "1.0.0"));
        Assert.Throws<ObjectDisposedException>(() => store.Enable("a.plugin", "1.0.0"));

        store.Dispose();
    }
}
