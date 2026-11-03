using System.Text.Json;
using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalStorePersistenceTests
{
    [Fact]
    public void Round_trip_through_a_new_store_preserves_every_field()
    {
        using var directory = new TempApprovalDirectory();
        var original = new PluginRecord
        {
            PluginId = "acme.tools",
            Version = "2.0.0-rc.1",
            SourceHash = Hashing.SourceHash("round trip"),
            ApprovedHash = ApprovalFixtures.Sha256Hex("approved"),
            DependencyHash = ApprovalFixtures.ApprovedDependencyHash,
            SdkVersion = "10.0.100",
            Status = PluginStatus.Disabled,
            CreatedAt = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.FromHours(2)).AddTicks(9),
            UpdatedAt = new DateTimeOffset(2025, 3, 5, 1, 2, 3, TimeSpan.FromHours(-5)).AddTicks(1234567),
        };
        using (var store = new ApprovalStore(directory.Path))
        {
            store.Upsert(original);
        }

        using var reopened = new ApprovalStore(directory.Path);
        var reloaded = reopened.Get("acme.tools", "2.0.0-rc.1");

        Assert.NotNull(reloaded);
        Assert.Equal(original.PluginId, reloaded.PluginId);
        Assert.Equal(original.Version, reloaded.Version);
        Assert.Equal(original.SourceHash, reloaded.SourceHash);
        Assert.Equal(original.ApprovedHash, reloaded.ApprovedHash);
        Assert.Equal(original.DependencyHash, reloaded.DependencyHash);
        Assert.Equal(original.SdkVersion, reloaded.SdkVersion);
        Assert.Equal(original.Status, reloaded.Status);
        Assert.Equal(original.CreatedAt, reloaded.CreatedAt);
        Assert.Equal(original.CreatedAt.Offset, reloaded.CreatedAt.Offset);
        Assert.Equal(original.UpdatedAt, reloaded.UpdatedAt);
        Assert.Equal(original.UpdatedAt.Offset, reloaded.UpdatedAt.Offset);
        Assert.Equal(original.UpdatedAt.UtcTicks, reloaded.UpdatedAt.UtcTicks);
    }

    [Fact]
    public void Round_trip_keeps_a_null_approved_hash_as_null()
    {
        using var directory = new TempApprovalDirectory();
        using (var store = new ApprovalStore(directory.Path))
        {
            store.Upsert(ApprovalFixtures.ApprovedRecord() with { ApprovedHash = null, Status = PluginStatus.Draft });
        }

        using var reopened = new ApprovalStore(directory.Path);
        var reloaded = reopened.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        Assert.NotNull(reloaded);
        Assert.Null(reloaded.ApprovedHash);
        Assert.Equal(PluginStatus.Draft, reloaded.Status);
    }

    [Fact]
    public void Approval_survives_restart_and_still_matches_the_same_content()
    {
        using var directory = new TempApprovalDirectory();
        using (ApprovalFixtures.StoreWithApproval(directory.Path))
        {
        }

        using var restarted = new ApprovalStore(directory.Path);

        Assert.True(restarted.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.HasApproval);
        Assert.True(ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), restarted).IsAllowed);
    }

    [Fact]
    public void Missing_state_file_starts_an_empty_store()
    {
        using var directory = new TempApprovalDirectory();

        using var store = new ApprovalStore(Path.Combine(directory.Path, "not-created-yet"));

        Assert.Equal(0, store.Count);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Store_creates_a_missing_directory_on_first_mutation()
    {
        using var directory = new TempApprovalDirectory();
        var nested = Path.Combine(directory.Path, "nested", "approvals");
        using (var store = new ApprovalStore(nested))
        {
            store.SetApproved(ApprovalFixtures.PluginId, ApprovalFixtures.Version, Hashing.SourceHash("x"), ApprovalFixtures.ApprovedDependencyHash, ApprovalFixtures.Sdk);
        }

        Assert.True(File.Exists(Path.Combine(nested, ApprovalStore.FileName)));
        using var reopened = new ApprovalStore(nested);
        Assert.Equal(1, reopened.Count);
    }

    [Fact]
    public void Records_with_blank_identity_in_the_legacy_file_are_skipped()
    {
        using var directory = new TempApprovalDirectory();
        var path = Path.Combine(directory.Path, ApprovalStore.LegacyFileName);
        File.WriteAllText(path, """
            [
              { "pluginId": "", "version": "1.0.0", "sourceHash": "a", "dependencyHash": "b", "sdkVersion": "c", "status": "Draft" },
              { "pluginId": "acme.tools", "version": " ", "sourceHash": "a", "dependencyHash": "b", "sdkVersion": "c", "status": "Draft" },
              { "pluginId": "acme.tools", "version": "1.2.0", "sourceHash": "a", "dependencyHash": "b", "sdkVersion": "c", "status": "Draft" }
            ]
            """);

        using var store = new ApprovalStore(directory.Path);

        Assert.Equal(1, store.Count);
        Assert.Equal("1.2.0", store.Get("acme.tools")?.Version);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[{\"pluginId\":")]
    [InlineData("{}")]
    [InlineData("[{\"pluginId\":\"acme.tools\",\"version\":\"1.2.0\",\"status\":\"Bogus\"}]")]
    public void Corrupt_legacy_state_file_fails_construction_with_invalid_data(string contents)
    {
        using var directory = new TempApprovalDirectory();
        File.WriteAllText(Path.Combine(directory.Path, ApprovalStore.LegacyFileName), contents);

        var exception = Assert.Throws<InvalidDataException>(() => new ApprovalStore(directory.Path));

        Assert.IsType<JsonException>(exception.InnerException);
        Assert.Contains(ApprovalStore.LegacyFileName, exception.Message);
    }

    [Fact]
    public void Reload_picks_up_changes_written_by_another_store()
    {
        using var directory = new TempApprovalDirectory();
        using var first = ApprovalFixtures.StoreWithApproval(directory.Path);
        using var second = new ApprovalStore(directory.Path);
        second.Upsert(ApprovalFixtures.ApprovedRecord() with { PluginId = "other.plugin" });

        first.Reload();

        Assert.Equal(2, first.Count);
        Assert.NotNull(first.Get("other.plugin"));
    }
}
