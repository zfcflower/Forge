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
        new ApprovalStore(directory.Path).Upsert(original);

        var reloaded = new ApprovalStore(directory.Path).Get("acme.tools", "2.0.0-rc.1");

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
        new ApprovalStore(directory.Path).Upsert(ApprovalFixtures.ApprovedRecord() with { ApprovedHash = null, Status = PluginStatus.Draft });

        var reloaded = new ApprovalStore(directory.Path).Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        Assert.NotNull(reloaded);
        Assert.Null(reloaded.ApprovedHash);
        Assert.Equal(PluginStatus.Draft, reloaded.Status);
    }

    [Fact]
    public void Persisted_file_uses_camel_case_names_and_string_status()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path);
        store.Disable(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        using var document = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        var entry = document.RootElement[0];

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(1, document.RootElement.GetArrayLength());
        Assert.Equal(ApprovalFixtures.PluginId, entry.GetProperty("pluginId").GetString());
        Assert.Equal(ApprovalFixtures.Version, entry.GetProperty("version").GetString());
        Assert.Equal("Disabled", entry.GetProperty("status").GetString());
        Assert.Equal(ApprovalFixtures.Sdk, entry.GetProperty("sdkVersion").GetString());
        Assert.Equal(store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.ApprovedHash, entry.GetProperty("approvedHash").GetString());
    }

    [Fact]
    public void Approval_survives_restart_and_still_matches_the_same_content()
    {
        using var directory = new TempApprovalDirectory();
        ApprovalFixtures.StoreWithApproval(directory.Path);

        var restarted = new ApprovalStore(directory.Path);

        Assert.True(restarted.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.HasApproval);
        Assert.True(ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), restarted).IsAllowed);
    }

    [Fact]
    public void Missing_state_file_starts_an_empty_store()
    {
        using var directory = new TempApprovalDirectory();

        var store = new ApprovalStore(Path.Combine(directory.Path, "not-created-yet"));

        Assert.Equal(0, store.Count);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Store_creates_a_missing_directory_on_first_mutation()
    {
        using var directory = new TempApprovalDirectory();
        var nested = Path.Combine(directory.Path, "nested", "approvals");
        var store = new ApprovalStore(nested);

        store.SetApproved(ApprovalFixtures.PluginId, ApprovalFixtures.Version, Hashing.SourceHash("x"), ApprovalFixtures.ApprovedDependencyHash, ApprovalFixtures.Sdk);

        Assert.True(File.Exists(store.FilePath));
        Assert.Equal(1, new ApprovalStore(nested).Count);
    }

    [Fact]
    public void Records_with_blank_identity_in_the_file_are_skipped()
    {
        using var directory = new TempApprovalDirectory();
        var path = Path.Combine(directory.Path, ApprovalStore.FileName);
        File.WriteAllText(path, """
            [
              { "pluginId": "", "version": "1.0.0", "sourceHash": "a", "dependencyHash": "b", "sdkVersion": "c", "status": "Draft" },
              { "pluginId": "acme.tools", "version": " ", "sourceHash": "a", "dependencyHash": "b", "sdkVersion": "c", "status": "Draft" },
              { "pluginId": "acme.tools", "version": "1.2.0", "sourceHash": "a", "dependencyHash": "b", "sdkVersion": "c", "status": "Draft" }
            ]
            """);

        var store = new ApprovalStore(directory.Path);

        Assert.Equal(1, store.Count);
        Assert.Equal("1.2.0", store.Get("acme.tools")?.Version);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[{\"pluginId\":")]
    [InlineData("{}")]
    [InlineData("[{\"pluginId\":\"acme.tools\",\"version\":\"1.2.0\",\"status\":\"Bogus\"}]")]
    public void Corrupt_state_file_fails_construction_with_invalid_data(string contents)
    {
        using var directory = new TempApprovalDirectory();
        File.WriteAllText(Path.Combine(directory.Path, ApprovalStore.FileName), contents);

        var exception = Assert.Throws<InvalidDataException>(() => new ApprovalStore(directory.Path));

        Assert.IsType<JsonException>(exception.InnerException);
        Assert.Contains(ApprovalStore.FileName, exception.Message);
    }

    [Fact]
    public void Reload_picks_up_changes_written_by_another_store()
    {
        using var directory = new TempApprovalDirectory();
        var first = ApprovalFixtures.StoreWithApproval(directory.Path);
        var second = new ApprovalStore(directory.Path);
        second.Upsert(ApprovalFixtures.ApprovedRecord() with { PluginId = "other.plugin" });
        Assert.Equal(1, first.Count);

        first.Reload();

        Assert.Equal(2, first.Count);
        Assert.NotNull(first.Get("other.plugin"));
    }

    [Fact]
    public void Reload_drops_records_that_were_removed_from_the_file()
    {
        using var directory = new TempApprovalDirectory();
        var first = ApprovalFixtures.StoreWithApproval(directory.Path);
        File.WriteAllText(first.FilePath, "[]");

        first.Reload();

        Assert.Equal(0, first.Count);
        Assert.Null(first.Get(ApprovalFixtures.PluginId));
    }

    [Fact]
    public void Reload_on_a_corrupt_file_fails_with_invalid_data()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path);
        File.WriteAllText(store.FilePath, "{ corrupt");

        Assert.Throws<InvalidDataException>(() => store.Reload());
    }

    [Fact]
    public void Failed_reload_keeps_the_previously_loaded_records()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path);
        File.WriteAllText(store.FilePath, "{ corrupt");

        Assert.Throws<InvalidDataException>(() => store.Reload());

        Assert.Equal(1, store.Count);
    }
}
