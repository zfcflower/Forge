using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Approval.Tests;

/// <summary>
/// Pins the read-only count contract of <see cref="ApprovalStore.CountReadOnlyAsync"/>: a caller that only
/// shows the number must never create an approval database, must see committed rows, must not block on a
/// writer, and must fail loudly on a corrupt file instead of reporting zero.
/// </summary>
public sealed class ApprovalCountReadOnlyTests
{
    [Fact]
    public async Task Missing_file_returns_zero_and_creates_nothing()
    {
        using var directory = new TempApprovalDirectory();
        var databasePath = Path.Combine(directory.Path, ApprovalStore.FileName);

        var count = await ApprovalStore.CountReadOnlyAsync(databasePath);

        Assert.Equal(0, count);
        Assert.False(File.Exists(databasePath));
        Assert.False(File.Exists(databasePath + "-wal"));
        Assert.False(File.Exists(databasePath + "-shm"));
    }

    [Fact]
    public async Task Database_without_the_plugin_approval_table_returns_zero()
    {
        using var directory = new TempApprovalDirectory();
        var databasePath = Path.Combine(directory.Path, ApprovalStore.FileName);
        using (var connection = OpenDatabase(databasePath))
        {
            Execute(connection, "CREATE TABLE unrelated (id TEXT PRIMARY KEY)");
        }

        var count = await ApprovalStore.CountReadOnlyAsync(databasePath);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Zero_byte_file_returns_zero()
    {
        using var directory = new TempApprovalDirectory();
        var databasePath = Path.Combine(directory.Path, ApprovalStore.FileName);
        await File.WriteAllBytesAsync(databasePath, []);

        var count = await ApprovalStore.CountReadOnlyAsync(databasePath);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Populated_database_returns_the_committed_row_count()
    {
        using var directory = new TempApprovalDirectory();
        using (var store = new ApprovalStore(directory.Path))
        {
            store.Upsert(new PluginRecord { PluginId = "acme.one", Version = "1.0", SourceHash = "s", DependencyHash = "d", SdkVersion = "sdk" });
            store.Upsert(new PluginRecord { PluginId = "acme.two", Version = "1.0", SourceHash = "s", DependencyHash = "d", SdkVersion = "sdk" });
            store.Upsert(new PluginRecord { PluginId = "acme.two", Version = "2.0", SourceHash = "s", DependencyHash = "d", SdkVersion = "sdk" });
        }

        var count = await ApprovalStore.CountReadOnlyAsync(Path.Combine(directory.Path, ApprovalStore.FileName));

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task Open_write_transaction_returns_the_last_committed_count_without_blocking()
    {
        using var directory = new TempApprovalDirectory();
        using (var store = new ApprovalStore(directory.Path))
        {
            store.Upsert(new PluginRecord { PluginId = "acme.one", Version = "1.0", SourceHash = "s", DependencyHash = "d", SdkVersion = "sdk" });
            store.Upsert(new PluginRecord { PluginId = "acme.two", Version = "1.0", SourceHash = "s", DependencyHash = "d", SdkVersion = "sdk" });
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

            var stopwatch = Stopwatch.StartNew();
            var count = await ApprovalStore.CountReadOnlyAsync(databasePath);
            stopwatch.Stop();

            Assert.Equal(2, count);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(15),
                $"Count blocked on the writer for {stopwatch.Elapsed}.");
        }
        finally
        {
            Execute(writer, "ROLLBACK");
        }
    }

    [Fact]
    public async Task Corrupt_file_throws_instead_of_returning_zero()
    {
        using var directory = new TempApprovalDirectory();
        var databasePath = Path.Combine(directory.Path, ApprovalStore.FileName);
        await File.WriteAllBytesAsync(
            databasePath,
            "this is not a sqlite database, it is plain text"u8.ToArray());

        await Assert.ThrowsAsync<SqliteException>(() => ApprovalStore.CountReadOnlyAsync(databasePath));
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
