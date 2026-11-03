using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalDataVersionTests
{
    [Fact]
    public void Second_instance_commit_is_visible_without_reload()
    {
        using var directory = new TempApprovalDirectory();
        using var first = new ApprovalStore(directory.Path);
        using var second = new ApprovalStore(directory.Path);
        first.SetApproved("seed.plugin", "1.0.0", "s", "dep", "sdk");

        Assert.NotNull(second.Get("seed.plugin", "1.0.0"));

        second.SetApproved("other.plugin", "1.0.0", "s2", "dep", "sdk");
        Assert.NotNull(first.Get("other.plugin", "1.0.0"));

        second.Revoke("other.plugin", "1.0.0");
        Assert.Equal(PluginStatus.Draft, first.Get("other.plugin", "1.0.0")?.Status);

        Assert.Equal(2, first.Count);
        Assert.Equal(2, first.List().Count);
    }

    [Fact]
    public void Uncommitted_changes_stay_invisible_until_commit()
    {
        using var directory = new TempApprovalDirectory();
        using var store = new ApprovalStore(directory.Path);
        store.SetApproved("seed.plugin", "1.0.0", "s", "dep", "sdk");

        using var raw = ApprovalStoreTestSupport.OpenDatabase(directory.Path);
        using var transaction = raw.BeginTransaction(deferred: false);
        try
        {
            ApprovalStoreTestSupport.Execute(
                raw,
                """
                INSERT INTO plugin_approval
                    (plugin_id, version, status, source_hash, dependency_hash, sdk_version, approved_hash, created_at, updated_at, updated_utc_ticks)
                VALUES
                    ('pending.plugin', '1.0.0', 'Draft', 'p', 'd', 's', NULL, '2025-01-01T00:00:00.0000000+00:00', '2025-01-01T00:00:00.0000000+00:00', 0)
                """,
                transaction);

            Assert.Null(store.Get("pending.plugin", "1.0.0"));
            Assert.Equal(1, store.Count);
            transaction.Rollback();

            Assert.Null(store.Get("pending.plugin", "1.0.0"));
            Assert.Equal(1, store.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        using var committed = ApprovalStoreTestSupport.OpenDatabase(directory.Path);
        try
        {
            ApprovalStoreTestSupport.Execute(
                committed,
                """
                INSERT INTO plugin_approval
                    (plugin_id, version, status, source_hash, dependency_hash, sdk_version, approved_hash, created_at, updated_at, updated_utc_ticks)
                VALUES
                    ('pending.plugin', '1.0.0', 'Draft', 'p', 'd', 's', NULL, '2025-01-01T00:00:00.0000000+00:00', '2025-01-01T00:00:00.0000000+00:00', 0)
                """);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        Assert.NotNull(store.Get("pending.plugin", "1.0.0"));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Repeated_reads_without_external_writes_return_stable_results()
    {
        using var directory = new TempApprovalDirectory();
        using (var seed = new ApprovalStore(directory.Path))
        {
            for (var i = 0; i < 200; i++)
            {
                seed.SetApproved($"p{i}.plugin", "1.0.0", $"hash-{i}", "dep", "10.0.100");
            }
        }

        using var store = new ApprovalStore(directory.Path);
        for (var round = 0; round < 5; round++)
        {
            for (var i = 0; i < 200; i++)
            {
                Assert.Equal($"hash-{i}", store.Get($"p{i}.plugin", "1.0.0")?.SourceHash);
            }
        }

        Assert.Equal(200, store.Count);
        Assert.Equal(200, store.List().Count);
    }
#if DEBUG
    [Fact]
    public void Snapshot_is_read_once_until_an_external_commit_or_reload()
    {
        using var directory = new TempApprovalDirectory();
        int snapshotReads = 0;
        var options = new ApprovalStoreOptions { SnapshotRead = () => Interlocked.Increment(ref snapshotReads) };
        using var store = new ApprovalStore(directory.Path, options);
        Assert.Equal(1, snapshotReads);

        for (var i = 0; i < 50; i++)
        {
            Assert.Null(store.Get("missing.plugin", "1.0.0"));
        }

        Assert.Equal(0, store.Count);
        Assert.Empty(store.List());
        Assert.Equal(1, snapshotReads);

        var approved = store.SetApproved("p.plugin", "1.0.0", "s", "dep", "sdk");
        Assert.NotNull(approved);
        var revoked = store.Revoke("p.plugin", "1.0.0");
        Assert.NotNull(revoked);
        Assert.Equal(PluginStatus.Draft, store.Get("p.plugin", "1.0.0")?.Status);
        Assert.Equal(1, store.Count);
        Assert.Single(store.List());
        Assert.Equal(1, snapshotReads);

        using (var other = new ApprovalStore(directory.Path))
        {
            other.SetApproved("q.plugin", "1.0.0", "s", "dep", "sdk");
        }

        Assert.NotNull(store.Get("q.plugin", "1.0.0"));
        Assert.Equal(2, store.Count);
        Assert.Equal(2, store.List().Count);
        Assert.Equal(2, snapshotReads);

        store.Reload();
        Assert.Equal(2, store.Count);
        Assert.Equal(3, snapshotReads);

        Assert.NotNull(store.Get("q.plugin", "1.0.0"));
        Assert.Equal(2, store.Count);
        Assert.Equal(2, store.List().Count);
        Assert.Equal(3, snapshotReads);
    }
#endif
}
