using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Approval.Tests;

/// <summary>
/// Pins the read-only list contract of <see cref="ApprovalStore.ListReadOnlyAsync"/>: it is the settings
/// page's only data source for the approvals section, so it must show the committed rows in the documented
/// order without ever writing, and must fail loudly on an unusable database instead of showing "no plugins".
/// </summary>
public sealed class ApprovalListReadOnlyTests
{
    [Fact]
    public async Task Missing_file_returns_an_empty_list_and_creates_nothing()
    {
        using var directory = new TempApprovalDirectory();
        var databasePath = Path.Combine(directory.Path, ApprovalStore.FileName);

        var records = await ApprovalStore.ListReadOnlyAsync(databasePath);

        Assert.Empty(records);
        Assert.False(File.Exists(databasePath));
        Assert.False(File.Exists(databasePath + "-wal"));
        Assert.False(File.Exists(databasePath + "-shm"));
    }

    [Fact]
    public async Task Database_without_the_plugin_approval_table_returns_an_empty_list()
    {
        using var directory = new TempApprovalDirectory();
        var databasePath = Path.Combine(directory.Path, ApprovalStore.FileName);
        using (var connection = OpenDatabase(databasePath))
        {
            Execute(connection, "CREATE TABLE unrelated (id TEXT PRIMARY KEY)");
        }

        var records = await ApprovalStore.ListReadOnlyAsync(databasePath);

        Assert.Empty(records);
    }

    [Fact]
    public async Task Zero_byte_file_returns_an_empty_list()
    {
        using var directory = new TempApprovalDirectory();
        var databasePath = Path.Combine(directory.Path, ApprovalStore.FileName);
        await File.WriteAllBytesAsync(databasePath, []);

        var records = await ApprovalStore.ListReadOnlyAsync(databasePath);

        Assert.Empty(records);
    }

    [Fact]
    public async Task Populated_database_returns_committed_records_ordered_by_plugin_id_then_version()
    {
        using var directory = new TempApprovalDirectory();
        using (var store = new ApprovalStore(directory.Path))
        {
            store.Upsert(ApprovalFixtures.ApprovedRecord() with { PluginId = "b.plugin", Version = "1.0.0" });
            store.Upsert(ApprovalFixtures.ApprovedRecord() with { PluginId = "a.plugin", Version = "2.0.0" });
            store.Upsert(ApprovalFixtures.ApprovedRecord() with { PluginId = "a.plugin", Version = "10.0.0" });
            store.Upsert(ApprovalFixtures.ApprovedRecord() with { PluginId = "a.plugin", Version = "1.0.0" });
        }

        var records = await ApprovalStore.ListReadOnlyAsync(Path.Combine(directory.Path, ApprovalStore.FileName));

        Assert.Equal(
            ["a.plugin 1.0.0", "a.plugin 10.0.0", "a.plugin 2.0.0", "b.plugin 1.0.0"],
            records.Select(record => $"{record.PluginId} {record.Version}"));
    }

    [Fact]
    public async Task Returned_records_carry_the_committed_fields_of_every_version()
    {
        using var directory = new TempApprovalDirectory();
        var approved = ApprovalFixtures.ApprovedRecord() with { PluginId = "acme.one", Version = "1.0" };
        using (var store = new ApprovalStore(directory.Path))
        {
            store.Upsert(approved);
            store.Upsert(ApprovalFixtures.ApprovedRecord() with { PluginId = "acme.two", Version = "2.0" });
            store.Revoke("acme.one", "1.0");
            store.Disable("acme.two", "2.0");
        }

        var records = await ApprovalStore.ListReadOnlyAsync(Path.Combine(directory.Path, ApprovalStore.FileName));

        Assert.Equal(["acme.one", "acme.two"], records.Select(record => record.PluginId));
        var revoked = records.Single(record => record.PluginId == "acme.one");
        Assert.Equal(PluginStatus.Draft, revoked.Status);
        Assert.Null(revoked.ApprovedHash);
        Assert.Equal(approved.SourceHash, revoked.SourceHash);
        Assert.Equal(approved.DependencyHash, revoked.DependencyHash);
        Assert.Equal(approved.SdkVersion, revoked.SdkVersion);
        Assert.True(revoked.CreatedAt != default);
        Assert.True(revoked.UpdatedAt >= revoked.CreatedAt);
        var disabled = records.Single(record => record.PluginId == "acme.two");
        Assert.Equal(PluginStatus.Disabled, disabled.Status);
        Assert.Equal(approved.ApprovedHash, disabled.ApprovedHash);
    }

    [Fact]
    public async Task Open_write_transaction_returns_only_the_committed_records_without_blocking()
    {
        using var directory = new TempApprovalDirectory();
        using (var store = new ApprovalStore(directory.Path))
        {
            store.Upsert(ApprovalFixtures.ApprovedRecord() with { PluginId = "acme.one", Version = "1.0" });
            store.Upsert(ApprovalFixtures.ApprovedRecord() with { PluginId = "acme.two", Version = "1.0" });
        }

        var databasePath = Path.Combine(directory.Path, ApprovalStore.FileName);
        using var writer = OpenDatabase(databasePath);
        Execute(writer, "BEGIN IMMEDIATE");
        try
        {
            Execute(
                writer,
                "INSERT INTO plugin_approval (plugin_id, version, status, source_hash, dependency_hash, sdk_version, " +
                "approved_hash, created_at, updated_at, updated_utc_ticks) VALUES " +
                "('zzz.uncommitted', '9.9', 'Draft', 's', 'd', 'sdk', NULL, " +
                "'2025-01-01T00:00:00.0000000+00:00', '2025-01-01T00:00:00.0000000+00:00', 0)");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var records = await ApprovalStore.ListReadOnlyAsync(databasePath);
            stopwatch.Stop();

            Assert.Equal(["acme.one", "acme.two"], records.Select(record => record.PluginId));
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(15),
                $"List blocked on the writer for {stopwatch.Elapsed}.");
        }
        finally
        {
            Execute(writer, "ROLLBACK");
        }
    }

    [Fact]
    public async Task Corrupt_file_throws_instead_of_returning_an_empty_list()
    {
        using var directory = new TempApprovalDirectory();
        var databasePath = Path.Combine(directory.Path, ApprovalStore.FileName);
        await File.WriteAllBytesAsync(
            databasePath,
            "this is not a sqlite database, it is plain text"u8.ToArray());

        await Assert.ThrowsAsync<SqliteException>(() => ApprovalStore.ListReadOnlyAsync(databasePath));
    }

    private static SqliteConnection OpenDatabase(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
