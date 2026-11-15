using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalOriginTests
{
    [Fact]
    public void Version_1_database_adds_the_origin_column_and_old_rows_read_as_Installed()
    {
        using var directory = new TempApprovalDirectory();
        ApprovalStoreTestSupport.EnsureUncheckedDatabase(
            directory.Path,
            [ApprovalFixtures.ApprovedRecord()]);
        Assert.Equal(1, ApprovalStoreTestSupport.UserVersion(directory.Path));

        using var store = new ApprovalStore(directory.Path);

        Assert.Equal(2, ApprovalStoreTestSupport.UserVersion(directory.Path));
        var record = store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version);
        Assert.NotNull(record);
        Assert.Equal(PluginOrigin.Installed, record.Origin);
        Assert.Equal(PluginStatus.Approved, record.Status);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Version_1_upgrade_keeps_every_row_and_status_while_adding_the_origin_column()
    {
        using var directory = new TempApprovalDirectory();
        var rows = new[]
        {
            ApprovalFixtures.ApprovedRecord(),
            ApprovalFixtures.Candidate(status: PluginStatus.Draft) with { PluginId = "acme.draft", Version = "2.0.0" },
            ApprovalFixtures.Candidate(status: PluginStatus.Disabled) with { PluginId = "acme.paused", Version = "3.0.0" },
            ApprovalFixtures.Candidate(status: PluginStatus.Uninstalled) with { PluginId = "acme.gone", Version = "4.0.0" },
        };
        ApprovalStoreTestSupport.EnsureUncheckedDatabase(directory.Path, rows);

        using (var store = new ApprovalStore(directory.Path))
        {
            Assert.Equal(2, ApprovalStoreTestSupport.UserVersion(directory.Path));
            var migrated = store.List();

            Assert.Equal(rows.Length, migrated.Count);
            foreach (var expected in rows)
            {
                var actual = Assert.Single(migrated, record => record.PluginId == expected.PluginId);
                Assert.Equal(expected.Version, actual.Version);
                Assert.Equal(expected.Status, actual.Status);
                Assert.Equal(expected.SourceHash, actual.SourceHash);
                Assert.Equal(expected.DependencyHash, actual.DependencyHash);
                Assert.Equal(expected.SdkVersion, actual.SdkVersion);
                Assert.Equal(expected.ApprovedHash, actual.ApprovedHash);
                Assert.Equal(expected.CreatedAt, actual.CreatedAt);
                Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
                // The upgrade invents nothing about where the row came from: every pre-origin row is Installed.
                Assert.Equal(PluginOrigin.Installed, actual.Origin);
            }

            // The status column keeps deciding whether a version may run; migration must not launder an approval.
            var draftDecision = ApprovalEngine.Evaluate(
                ApprovalFixtures.Candidate(status: PluginStatus.Draft) with { PluginId = "acme.draft", Version = "2.0.0" },
                store);
            Assert.False(draftDecision.IsAllowed);
            var approvedDecision = ApprovalEngine.Evaluate(
                ApprovalFixtures.Candidate() with { PluginId = ApprovalFixtures.PluginId, Version = ApprovalFixtures.Version },
                store);
            Assert.True(approvedDecision.IsAllowed);
        }

        // A second open is the steady state: no second ALTER, no lost row.
        using (var reopened = new ApprovalStore(directory.Path))
        {
            Assert.Equal(rows.Length, reopened.Count);
            Assert.Equal(PluginStatus.Approved, reopened.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.Status);
        }

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Concurrent_opens_of_a_version_1_database_upgrade_once_and_keep_every_row()
    {
        using var directory = new TempApprovalDirectory();
        ApprovalStoreTestSupport.EnsureUncheckedDatabase(
            directory.Path,
            [
                ApprovalFixtures.ApprovedRecord(),
                ApprovalFixtures.Candidate(status: PluginStatus.Draft) with { PluginId = "acme.draft", Version = "2.0.0" },
                ApprovalFixtures.Candidate(status: PluginStatus.Disabled) with { PluginId = "acme.paused", Version = "3.0.0" },
            ]);

        using var first = ApprovalChildProcess.Start(ApprovalChildProcess.ImportMode, directory.Path);
        using var second = ApprovalChildProcess.Start(ApprovalChildProcess.ImportMode, directory.Path);
        var firstResult = ApprovalStoreTestSupport.WaitForChild(first, TimeSpan.FromMinutes(2));
        var secondResult = ApprovalStoreTestSupport.WaitForChild(second, TimeSpan.FromMinutes(2));

        // The upgrade runs under BEGIN IMMEDIATE and re-reads user_version inside it, so the loser of the race must not
        // report "duplicate column name: origin" - it opens the already-upgraded schema.
        Assert.True(firstResult.ExitCode == 0, "first opener: " + firstResult.Output);
        Assert.True(secondResult.ExitCode == 0, "second opener: " + secondResult.Output);
        Assert.Contains("count=3", firstResult.Output, StringComparison.Ordinal);
        Assert.Contains("count=3", secondResult.Output, StringComparison.Ordinal);

        Assert.Equal(2, ApprovalStoreTestSupport.UserVersion(directory.Path));
        using var store = new ApprovalStore(directory.Path);
        Assert.Equal(3, store.Count);
        Assert.All(store.List(), static record => Assert.Equal(PluginOrigin.Installed, record.Origin));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Migrated_origin_column_refuses_an_unknown_origin_in_sql()
    {
        using var directory = new TempApprovalDirectory();
        ApprovalStoreTestSupport.EnsureUncheckedDatabase(
            directory.Path,
            [ApprovalFixtures.ApprovedRecord()]);

        using (var store = new ApprovalStore(directory.Path))
        {
            // The upgrade added the origin column; a value read out of it cannot be an unknown enum name, because the
            // column carries the same CHECK as a freshly created database (not just the C# row mapping).
            Assert.Equal(2, ApprovalStoreTestSupport.UserVersion(directory.Path));
            Assert.Equal(PluginOrigin.Installed, store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.Origin);
        }

        SqliteConnection.ClearAllPools();
        using var raw = ApprovalStoreTestSupport.OpenDatabase(directory.Path);
        var exception = Assert.Throws<SqliteException>(
            () => ApprovalStoreTestSupport.Execute(
                raw,
                $"UPDATE plugin_approval SET origin = 'Bogus' WHERE plugin_id = '{ApprovalFixtures.PluginId}';"));

        Assert.Equal(19, exception.SqliteErrorCode); // SQLITE_CONSTRAINT (19), extended CHECK (275)
        Assert.Equal(
            nameof(PluginOrigin.Installed),
            ApprovalStoreTestSupport.Scalar(raw, "SELECT origin FROM plugin_approval LIMIT 1"));
        SqliteConnection.ClearAllPools();
    }

    [Theory]
    [InlineData(PluginOrigin.Installed)]
    [InlineData(PluginOrigin.Bundled)]
    [InlineData(PluginOrigin.BuiltIn)]
    public void Origin_round_trips_through_a_new_store(PluginOrigin origin)
    {
        using var directory = new TempApprovalDirectory();
        using (var store = new ApprovalStore(directory.Path))
        {
            store.Upsert(new PluginRecord
            {
                PluginId = "origin.plugin",
                Version = "1.0.0",
                SourceHash = "s",
                DependencyHash = "d",
                SdkVersion = "sdk",
                Status = PluginStatus.Draft,
                Origin = origin,
            });
        }

        using var reopened = new ApprovalStore(directory.Path);
        var record = reopened.Get("origin.plugin", "1.0.0");

        Assert.NotNull(record);
        Assert.Equal(origin, record.Origin);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Legacy_json_import_records_read_as_Installed()
    {
        using var directory = new TempApprovalDirectory();
        var legacyPath = Path.Combine(directory.Path, ApprovalStore.LegacyFileName);
        File.WriteAllText(
            legacyPath,
            ApprovalStoreTestSupport.LegacyJson(("acme.legacy", "1.0.0", PluginStatus.Approved)));

        using var store = new ApprovalStore(directory.Path);

        var record = store.Get("acme.legacy", "1.0.0");
        Assert.NotNull(record);
        Assert.Equal(PluginOrigin.Installed, record.Origin);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Legacy_json_import_and_a_later_origin_change_coexist_and_round_trip()
    {
        using var directory = new TempApprovalDirectory();
        var legacyPath = Path.Combine(directory.Path, ApprovalStore.LegacyFileName);
        File.WriteAllText(
            legacyPath,
            ApprovalStoreTestSupport.LegacyJson(
                ("acme.legacy", "1.0.0", PluginStatus.Approved),
                ("acme.bundled", "2.0.0", PluginStatus.Draft)));

        using (var store = new ApprovalStore(directory.Path))
        {
            // The import creates rows in the origin-carrying schema, so an imported row is Installed by default...
            Assert.Equal(PluginOrigin.Installed, store.Get("acme.legacy", "1.0.0")?.Origin);

            // ...and a later origin assignment is a normal update, not a second migration.
            var bundled = store.Get("acme.bundled", "2.0.0");
            Assert.NotNull(bundled);
            store.Upsert(bundled with { Origin = PluginOrigin.Bundled });
        }

        Assert.True(File.Exists(legacyPath + ApprovalStore.MigratedSuffix));
        Assert.False(File.Exists(legacyPath));
        Assert.Equal(1, ApprovalStoreTestSupport.QueryCount(
            directory.Path,
            "SELECT COUNT(*) FROM approval_meta WHERE key = 'legacy_import'"));

        using var reopened = new ApprovalStore(directory.Path);
        Assert.Equal(2, reopened.Count);
        Assert.Equal(PluginOrigin.Installed, reopened.Get("acme.legacy", "1.0.0")?.Origin);
        Assert.Equal(PluginOrigin.Bundled, reopened.Get("acme.bundled", "2.0.0")?.Origin);
        Assert.Equal(PluginStatus.Approved, reopened.Get("acme.legacy", "1.0.0")?.Status);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Schema_3_is_rejected_before_any_upgrade_step_runs()
    {
        using var directory = new TempApprovalDirectory();
        var databasePath = Path.Combine(Path.GetFullPath(directory.Path), ApprovalStore.FileName);
        ApprovalStoreTestSupport.EnsureUncheckedDatabase(directory.Path, [ApprovalFixtures.ApprovedRecord()]);
        using (var raw = ApprovalStoreTestSupport.OpenDatabase(directory.Path))
        {
            ApprovalStoreTestSupport.Execute(raw, "PRAGMA user_version = 3;");
        }

        SqliteConnection.ClearAllPools();
        var exception = Assert.Throws<InvalidDataException>(() => new ApprovalStore(directory.Path));

        // fail-closed must also say which file and which two versions disagree, or the user has no way to act on it.
        Assert.Contains(databasePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("3", exception.Message, StringComparison.Ordinal);
        Assert.Contains("2", exception.Message, StringComparison.Ordinal);

        // Nothing of the rejected migration may have landed: not the version bump, not the origin column.
        Assert.Equal(3, ApprovalStoreTestSupport.UserVersion(directory.Path));
        Assert.Equal(0, ApprovalStoreTestSupport.QueryCount(
            directory.Path,
            "SELECT COUNT(*) FROM pragma_table_info('plugin_approval') WHERE name = 'origin'"));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Unknown_origin_fails_construction_naming_the_value()
    {
        using var directory = new TempApprovalDirectory();
        var databasePath = Path.Combine(Path.GetFullPath(directory.Path), ApprovalStore.FileName);
        using (var seed = new ApprovalStore(directory.Path))
        {
            seed.Upsert(new PluginRecord
            {
                PluginId = "good.plugin",
                Version = "1.0.0",
                SourceHash = "s",
                DependencyHash = "d",
                SdkVersion = "sdk",
                Status = PluginStatus.Draft,
                Origin = PluginOrigin.Installed,
            });
        }

        ApprovalStoreTestSupport.InsertRawOrigin(directory.Path, "odd.plugin", "1.0.0", "Bogus");
        SqliteConnection.ClearAllPools();

        var exception = Assert.Throws<InvalidDataException>(() => new ApprovalStore(directory.Path));

        // Fail-closed, and diagnosable: the file, the row and the column, not just the offending value.
        Assert.Contains("Bogus", exception.Message, StringComparison.Ordinal);
        Assert.Contains("origin", exception.Message, StringComparison.Ordinal);
        Assert.Contains(databasePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("odd.plugin 1.0.0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Installed, Bundled, BuiltIn", exception.Message, StringComparison.Ordinal);
        SqliteConnection.ClearAllPools();
    }
}
