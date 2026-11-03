using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalSchemaTests
{
    [Fact]
    public void Fresh_store_creates_schema_version_1_wal_and_marker()
    {
        using var directory = new TempApprovalDirectory();
        using var store = new ApprovalStore(directory.Path);

        Assert.Equal("wal", store.JournalMode);
        Assert.Equal(0, store.Count);
        Assert.Equal(1, ApprovalStoreTestSupport.UserVersion(directory.Path));
        Assert.Equal(2, ApprovalStoreTestSupport.SchemaTableCount(directory.Path));
        Assert.NotNull(ApprovalStoreTestSupport.ImportMarker(directory.Path));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Schema_newer_than_the_build_fails_construction_naming_the_path()
    {
        using var directory = new TempApprovalDirectory();
        var databasePath = Path.Combine(Path.GetFullPath(directory.Path), ApprovalStore.FileName);
        using (var raw = ApprovalStoreTestSupport.OpenDatabase(directory.Path, SqliteOpenMode.ReadWriteCreate))
        {
            ApprovalStoreTestSupport.Execute(raw, "PRAGMA user_version = 7;");
        }

        SqliteConnection.ClearAllPools();
        var exception = Assert.Throws<InvalidDataException>(() => new ApprovalStore(directory.Path));

        Assert.Contains(databasePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("7", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Garbage_database_file_fails_construction()
    {
        using var directory = new TempApprovalDirectory();
        var databasePath = Path.Combine(directory.Path, ApprovalStore.FileName);
        File.WriteAllBytes(databasePath, Enumerable.Repeat((byte)0x5A, 4096).ToArray());

        var exception = Assert.Throws<InvalidDataException>(() => new ApprovalStore(directory.Path));

        Assert.Contains(ApprovalStore.FileName, exception.Message, StringComparison.Ordinal);
        var inner = Assert.IsType<SqliteException>(exception.InnerException);
        Assert.Equal(26, inner.SqliteErrorCode);
    }

    [Fact]
    public void Database_path_that_is_a_directory_fails_construction()
    {
        using var directory = new TempApprovalDirectory();
        var databasePath = Path.Combine(directory.Path, ApprovalStore.FileName);
        Directory.CreateDirectory(databasePath);

        var exception = Assert.Throws<InvalidDataException>(() => new ApprovalStore(directory.Path));

        Assert.Contains(databasePath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_status_fails_construction()
    {
        using var directory = new TempApprovalDirectory();
        ApprovalStoreTestSupport.EnsureUncheckedDatabase(
            directory.Path,
            [ApprovalFixtures.ApprovedRecord()]);
        ApprovalStoreTestSupport.InsertRawRow(directory.Path, "odd.plugin", "1.0.0", "Bogus");

        var exception = Assert.Throws<InvalidDataException>(() => new ApprovalStore(directory.Path));

        Assert.Contains("Bogus", exception.Message, StringComparison.Ordinal);
        Assert.Contains(ApprovalStore.FileName, exception.Message, StringComparison.Ordinal);
    }
}
