using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalMigrationTests
{
    [Fact]
    public void Import_skips_blank_identity_and_renames_only_after_commit()
    {
        using var directory = new TempApprovalDirectory();
        var legacyPath = Path.Combine(directory.Path, ApprovalStore.LegacyFileName);
        File.WriteAllText(
            legacyPath,
            ApprovalStoreTestSupport.LegacyJson(
                ("", "1.0.0", PluginStatus.Draft),
                ("acme.tools", " ", PluginStatus.Draft),
                ("acme.tools", "1.0.0", PluginStatus.Approved),
                ("acme.tools", "2.0.0", PluginStatus.Draft)));

        using var store = new ApprovalStore(directory.Path);

        Assert.Equal(2, store.Count);
        Assert.Equal(PluginStatus.Approved, store.Get("acme.tools", "1.0.0")?.Status);
        Assert.False(File.Exists(legacyPath));
        Assert.True(File.Exists(legacyPath + ApprovalStore.MigratedSuffix));
        Assert.NotNull(ApprovalStoreTestSupport.ImportMarker(directory.Path));
        Assert.True(ApprovalEngine.Evaluate(ApprovalStoreTestSupport.LegacyCandidate("acme.tools", "1.0.0"), store).IsAllowed);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Rewritten_stale_legacy_file_is_not_reimported()
    {
        using var directory = new TempApprovalDirectory();
        var legacyPath = Path.Combine(directory.Path, ApprovalStore.LegacyFileName);
        File.WriteAllText(
            legacyPath,
            ApprovalStoreTestSupport.LegacyJson(
                ("acme.tools", "1.0.0", PluginStatus.Approved),
                ("acme.tools", "2.0.0", PluginStatus.Draft)));
        using (var store = new ApprovalStore(directory.Path))
        {
            store.Revoke("acme.tools", "1.0.0");
        }

        File.WriteAllText(
            legacyPath,
            ApprovalStoreTestSupport.LegacyJson(
                ("acme.tools", "1.0.0", PluginStatus.Approved),
                ("acme.tools", "2.0.0", PluginStatus.Draft)));

        using var reopened = new ApprovalStore(directory.Path);
        Assert.Equal(2, reopened.Count);
        Assert.Equal(PluginStatus.Draft, reopened.Get("acme.tools", "1.0.0")?.Status);
        Assert.True(File.Exists(legacyPath));
        SqliteConnection.ClearAllPools();
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[{\"pluginId\":")]
    [InlineData("{}")]
    [InlineData("[{\"pluginId\":\"acme.tools\",\"version\":\"1.2.0\",\"sourceHash\":\"s\",\"dependencyHash\":\"d\",\"sdkVersion\":\"sdk\",\"status\":\"Bogus\"}]")]
    public void Corrupt_legacy_json_fails_construction_and_stays_retryable(string contents)
    {
        using var directory = new TempApprovalDirectory();
        var legacyPath = Path.Combine(directory.Path, ApprovalStore.LegacyFileName);
        File.WriteAllText(legacyPath, contents);

        var exception = Assert.Throws<InvalidDataException>(() => new ApprovalStore(directory.Path));

        Assert.IsType<JsonException>(exception.InnerException);
        Assert.Equal(contents, File.ReadAllText(legacyPath));
        Assert.Equal(0, ApprovalStoreTestSupport.UserVersion(directory.Path));
        Assert.Equal(0, ApprovalStoreTestSupport.SchemaTableCount(directory.Path));

        File.WriteAllText(legacyPath, ApprovalStoreTestSupport.LegacyJson(("fixed.plugin", "1.0.0", PluginStatus.Draft)));
        using var store = new ApprovalStore(directory.Path);
        Assert.Equal(1, store.Count);
        Assert.NotNull(store.Get("fixed.plugin", "1.0.0"));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Racing_imports_leave_exactly_the_expected_rows()
    {
        using var directory = new TempApprovalDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, ApprovalStore.LegacyFileName),
            ApprovalStoreTestSupport.LegacyJson(
                ("r1.plugin", "1.0.0", PluginStatus.Draft),
                ("r2.plugin", "1.0.0", PluginStatus.Approved)));

        using var first = ApprovalChildProcess.Start(ApprovalChildProcess.ImportMode, directory.Path);
        using var second = ApprovalChildProcess.Start(ApprovalChildProcess.ImportMode, directory.Path);
        var firstResult = ApprovalStoreTestSupport.WaitForChild(first, TimeSpan.FromMinutes(2));
        var secondResult = ApprovalStoreTestSupport.WaitForChild(second, TimeSpan.FromMinutes(2));

        Assert.True(firstResult.ExitCode == 0, "first importer: " + firstResult.Output);
        Assert.True(secondResult.ExitCode == 0, "second importer: " + secondResult.Output);
        Assert.Contains("count=2", firstResult.Output, StringComparison.Ordinal);
        Assert.Contains("count=2", secondResult.Output, StringComparison.Ordinal);

        Assert.Equal(2, ApprovalStoreTestSupport.TableRowCount(directory.Path, "plugin_approval"));
        Assert.Equal(1, ApprovalStoreTestSupport.QueryCount(directory.Path, "SELECT COUNT(*) FROM approval_meta WHERE key = 'legacy_import'"));
        Assert.True(File.Exists(Path.Combine(directory.Path, ApprovalStore.LegacyFileName + ApprovalStore.MigratedSuffix)));

        using var store = new ApprovalStore(directory.Path);
        Assert.Equal(2, store.Count);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void ImportLegacyJson_false_skips_the_import_and_stays_off()
    {
        using var directory = new TempApprovalDirectory();
        var legacyPath = Path.Combine(directory.Path, ApprovalStore.LegacyFileName);
        File.WriteAllText(legacyPath, ApprovalStoreTestSupport.LegacyJson(("skip.plugin", "1.0.0", PluginStatus.Approved)));

        using (var store = new ApprovalStore(directory.Path, new ApprovalStoreOptions { ImportLegacyJson = false }))
        {
            Assert.Equal(0, store.Count);
        }

        Assert.True(File.Exists(legacyPath));

        using (var reopened = new ApprovalStore(directory.Path))
        {
            Assert.Equal(0, reopened.Count);
        }

        Assert.True(File.Exists(legacyPath));
        SqliteConnection.ClearAllPools();
    }
}
