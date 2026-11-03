using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalReloadTests
{
    [Fact]
    public void Reload_picks_up_external_inserts_updates_and_deletes()
    {
        using var directory = new TempApprovalDirectory();
        using var store = new ApprovalStore(directory.Path);
        store.SetApproved("keep.plugin", "1.0.0", "old", "dep", "sdk");
        store.SetApproved("gone.plugin", "1.0.0", "gone", "dep", "sdk");

        using (var raw = ApprovalStoreTestSupport.OpenDatabase(directory.Path))
        {
            ApprovalStoreTestSupport.Execute(
                raw,
                """
                INSERT INTO plugin_approval
                    (plugin_id, version, status, source_hash, dependency_hash, sdk_version, approved_hash, created_at, updated_at, updated_utc_ticks)
                VALUES
                    ('fresh.plugin', '1.0.0', 'Draft', 'fresh', 'd', 's', NULL, '2025-01-01T00:00:00.0000000+00:00', '2025-01-01T00:00:00.0000000+00:00', 0)
                """);
            ApprovalStoreTestSupport.Execute(
                raw,
                "UPDATE plugin_approval SET source_hash = 'new' WHERE plugin_id = 'keep.plugin' AND version = '1.0.0'");
            ApprovalStoreTestSupport.Execute(
                raw,
                "DELETE FROM plugin_approval WHERE plugin_id = 'gone.plugin' AND version = '1.0.0'");
        }

        SqliteConnection.ClearAllPools();
        store.Reload();

        Assert.Equal("fresh", store.Get("fresh.plugin", "1.0.0")?.SourceHash);
        Assert.Equal("new", store.Get("keep.plugin", "1.0.0")?.SourceHash);
        Assert.Null(store.Get("gone.plugin", "1.0.0"));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Failed_reload_keeps_the_previous_snapshot()
    {
        using var directory = new TempApprovalDirectory();
        ApprovalStoreTestSupport.EnsureUncheckedDatabase(
            directory.Path,
            [ApprovalFixtures.ApprovedRecord()]);
        using var store = new ApprovalStore(directory.Path);
        Assert.Equal(1, store.Count);

        ApprovalStoreTestSupport.InsertRawRow(directory.Path, "odd.plugin", "1.0.0", "Bogus");
        var exception = Assert.Throws<InvalidDataException>(() => store.Reload());
        Assert.Contains("Bogus", exception.Message, StringComparison.Ordinal);

        using (var raw = ApprovalStoreTestSupport.OpenDatabase(directory.Path))
        {
            ApprovalStoreTestSupport.Execute(
                raw,
                "DELETE FROM plugin_approval WHERE plugin_id = 'odd.plugin' AND version = '1.0.0'");
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(1, store.Count);
        Assert.NotNull(store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version));
    }
}
