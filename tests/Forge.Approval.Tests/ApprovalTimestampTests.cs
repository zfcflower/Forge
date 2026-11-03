using System.Globalization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalTimestampTests
{
    [Theory]
    [InlineData("created_at", "not-a-date")]
    [InlineData("created_at", "2025-01-01T00:00:00.0000000")]
    [InlineData("created_at", "2025-01-01T00:00:00.0000000Z")]
    [InlineData("created_at", "2025-01-01T00:00:00+00:00")]
    [InlineData("created_at", "2025-01-01T00:00:00.0000000-00:00")]
    [InlineData("created_at", "2025-01-01T00:00:00.0000000+25:00")]
    [InlineData("created_at", "")]
    [InlineData("updated_at", "not-a-date")]
    [InlineData("updated_at", "2025-01-01T00:00:00.0000000")]
    [InlineData("updated_at", "2025-01-01T00:00:00.0000000Z")]
    [InlineData("updated_at", "2025-01-01T00:00:00+00:00")]
    [InlineData("updated_at", "2025-01-01T00:00:00.0000000-00:00")]
    [InlineData("updated_at", "2025-01-01T00:00:00.0000000+25:00")]
    [InlineData("updated_at", "")]
    public void Corrupt_timestamp_fails_construction_naming_path_row_and_column(string column, string value)
    {
        using var directory = new TempApprovalDirectory();
        using (var seed = new ApprovalStore(directory.Path))
        {
            seed.SetApproved(ApprovalFixtures.PluginId, ApprovalFixtures.Version, "s", "dep", "sdk");
        }

        OverwriteColumn(directory.Path, column, value);
        var databasePath = Path.Combine(Path.GetFullPath(directory.Path), ApprovalStore.FileName);

        var exception = Assert.Throws<InvalidDataException>(() => new ApprovalStore(directory.Path));

        Assert.Contains(databasePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(ApprovalFixtures.PluginId, exception.Message, StringComparison.Ordinal);
        Assert.Contains(ApprovalFixtures.Version, exception.Message, StringComparison.Ordinal);
        Assert.Contains(column, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_timestamp_with_a_non_utc_offset_round_trips()
    {
        using var directory = new TempApprovalDirectory();
        var createdAt = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.FromHours(5.5)).AddTicks(1234567);
        var updatedAt = new DateTimeOffset(2025, 3, 5, 1, 2, 3, TimeSpan.FromHours(-3.5)).AddTicks(7654321);
        using (var store = new ApprovalStore(directory.Path))
        {
            store.Upsert(ApprovalFixtures.ApprovedRecord() with { CreatedAt = createdAt, UpdatedAt = updatedAt });
        }

        using var reopened = new ApprovalStore(directory.Path);
        var reloaded = reopened.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        Assert.NotNull(reloaded);
        Assert.Equal(createdAt, reloaded.CreatedAt);
        Assert.Equal(createdAt.Offset, reloaded.CreatedAt.Offset);
        Assert.Equal(updatedAt, reloaded.UpdatedAt);
        Assert.Equal(updatedAt.Offset, reloaded.UpdatedAt.Offset);

        using var raw = ApprovalStoreTestSupport.OpenDatabase(directory.Path);
        try
        {
            Assert.Equal(
                createdAt.ToString("O", CultureInfo.InvariantCulture),
                ApprovalStoreTestSupport.Scalar(raw, "SELECT created_at FROM plugin_approval"),
                StringComparer.Ordinal);
            Assert.Equal(
                updatedAt.ToString("O", CultureInfo.InvariantCulture),
                ApprovalStoreTestSupport.Scalar(raw, "SELECT updated_at FROM plugin_approval"),
                StringComparer.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Reload_on_a_corrupt_timestamp_throws_and_repair_restores_the_snapshot()
    {
        using var directory = new TempApprovalDirectory();
        using var store = new ApprovalStore(directory.Path);
        store.SetApproved(ApprovalFixtures.PluginId, ApprovalFixtures.Version, "s", "dep", "sdk");
        var original = store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version);
        Assert.NotNull(original);

        OverwriteColumn(directory.Path, "updated_at", "not-a-date");

        var exception = Assert.Throws<InvalidDataException>(() => store.Reload());
        Assert.Contains("updated_at", exception.Message, StringComparison.Ordinal);
        Assert.Contains(ApprovalFixtures.PluginId, exception.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version));

        OverwriteColumn(directory.Path, "updated_at", original.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        store.Reload();

        var recovered = store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version);
        Assert.NotNull(recovered);
        Assert.Equal(original, recovered);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Mutating_a_row_with_a_corrupt_timestamp_fails_without_changing_the_table()
    {
        using var directory = new TempApprovalDirectory();
        using var store = new ApprovalStore(directory.Path);
        store.SetApproved(ApprovalFixtures.PluginId, ApprovalFixtures.Version, "s", "dep", "sdk");
        var original = store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version);
        Assert.NotNull(original);

        OverwriteColumn(directory.Path, "created_at", "not-a-date");

        var exception = Assert.Throws<InvalidDataException>(() =>
            store.SetApproved(ApprovalFixtures.PluginId, ApprovalFixtures.Version, "changed", "dep", "sdk"));
        Assert.Contains("created_at", exception.Message, StringComparison.Ordinal);

        using (var raw = ApprovalStoreTestSupport.OpenDatabase(directory.Path))
        {
            try
            {
                Assert.Equal("s", ApprovalStoreTestSupport.Scalar(raw, "SELECT source_hash FROM plugin_approval"), StringComparer.Ordinal);
                Assert.Equal("dep", ApprovalStoreTestSupport.Scalar(raw, "SELECT dependency_hash FROM plugin_approval"), StringComparer.Ordinal);
                Assert.Equal("sdk", ApprovalStoreTestSupport.Scalar(raw, "SELECT sdk_version FROM plugin_approval"), StringComparer.Ordinal);
                Assert.Equal(original.ApprovedHash, ApprovalStoreTestSupport.Scalar(raw, "SELECT approved_hash FROM plugin_approval"), StringComparer.Ordinal);
                Assert.Equal("Approved", ApprovalStoreTestSupport.Scalar(raw, "SELECT status FROM plugin_approval"), StringComparer.Ordinal);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        OverwriteColumn(directory.Path, "created_at", original.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        store.Reload();

        Assert.Equal(original, store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version));
    }

    private static void OverwriteColumn(string directory, string column, string value)
    {
        if (column is not "created_at" and not "updated_at")
        {
            throw new ArgumentOutOfRangeException(nameof(column), column, "Only timestamp columns can be overwritten.");
        }

        using var raw = ApprovalStoreTestSupport.OpenDatabase(directory);
        try
        {
            using var command = raw.CreateCommand();
            command.CommandText =
                $"UPDATE plugin_approval SET {column} = $value WHERE plugin_id = $pluginId AND version = $version";
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$pluginId", ApprovalFixtures.PluginId);
            command.Parameters.AddWithValue("$version", ApprovalFixtures.Version);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }
}
