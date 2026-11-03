using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalStoreTests
{
    [Fact]
    public void Upsert_then_get_returns_the_stored_record()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        var record = ApprovalFixtures.ApprovedRecord() with { Status = PluginStatus.Draft, ApprovedHash = null };

        var stored = store.Upsert(record);

        Assert.Equal(stored, store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Upsert_replaces_the_record_of_the_same_version_without_adding_one()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        store.Upsert(ApprovalFixtures.ApprovedRecord() with { Status = PluginStatus.Draft });

        store.Upsert(ApprovalFixtures.ApprovedRecord() with { Status = PluginStatus.Disabled });

        Assert.Equal(1, store.Count);
        Assert.Equal(PluginStatus.Disabled, store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.Status);
    }

    [Fact]
    public void Upsert_uses_the_supplied_created_at()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        var createdAt = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.FromHours(2));

        var stored = store.Upsert(ApprovalFixtures.ApprovedRecord() with { CreatedAt = createdAt });

        Assert.Equal(createdAt, stored.CreatedAt);
    }

    [Fact]
    public void Upsert_keeps_the_existing_created_at_when_the_new_record_has_none()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        var createdAt = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        store.Upsert(ApprovalFixtures.ApprovedRecord() with { CreatedAt = createdAt });

        var replaced = store.Upsert(ApprovalFixtures.ApprovedRecord() with { CreatedAt = default });

        Assert.Equal(createdAt, replaced.CreatedAt);
    }

    [Fact]
    public void Upsert_stamps_default_updated_at_with_the_current_time()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        var before = DateTimeOffset.UtcNow;

        var stored = store.Upsert(ApprovalFixtures.ApprovedRecord() with { UpdatedAt = default });

        Assert.InRange(stored.UpdatedAt, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Upsert_keeps_a_supplied_updated_at()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        var updatedAt = new DateTimeOffset(2024, 7, 4, 9, 30, 0, TimeSpan.Zero);

        var stored = store.Upsert(ApprovalFixtures.ApprovedRecord() with { UpdatedAt = updatedAt });

        Assert.Equal(updatedAt, stored.UpdatedAt);
    }

    [Theory]
    [InlineData("", "1.2.0")]
    [InlineData("   ", "1.2.0")]
    [InlineData("acme.tools", "")]
    [InlineData("acme.tools", "\t")]
    public void Upsert_rejects_blank_identity(string pluginId, string version)
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);

        Assert.Throws<ArgumentException>(() => store.Upsert(ApprovalFixtures.ApprovedRecord() with { PluginId = pluginId, Version = version }));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Upsert_rejects_null_record()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);

        Assert.Throws<ArgumentNullException>(() => store.Upsert(null!));
    }

    [Fact]
    public void Get_returns_null_for_unknown_plugin_or_version()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path);

        Assert.Null(store.Get("missing.plugin"));
        Assert.Null(store.Get(ApprovalFixtures.PluginId, "9.9.9"));
    }

    [Fact]
    public void Get_without_version_does_not_match_a_plugin_whose_id_only_shares_a_prefix()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        store.Upsert(ApprovalFixtures.ApprovedRecord() with { PluginId = "acme.tools.extra" });

        Assert.Null(store.Get("acme.tools"));
        Assert.Null(store.Get("acme.tools", ApprovalFixtures.Version));
    }

    [Fact]
    public void Get_without_version_returns_the_most_recently_updated_version()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        var older = new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var newer = older.AddDays(1);
        store.Upsert(ApprovalFixtures.ApprovedRecord() with { Version = "1.0.0", UpdatedAt = newer });
        store.Upsert(ApprovalFixtures.ApprovedRecord() with { Version = "2.0.0", UpdatedAt = older });

        Assert.Equal("1.0.0", store.Get(ApprovalFixtures.PluginId)?.Version);
    }

    [Fact]
    public void Get_without_version_breaks_update_time_ties_by_the_greater_version()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        store.Upsert(ApprovalFixtures.ApprovedRecord() with { Version = "1.0.0", UpdatedAt = ApprovalFixtures.Epoch });
        store.Upsert(ApprovalFixtures.ApprovedRecord() with { Version = "2.0.0", UpdatedAt = ApprovalFixtures.Epoch });

        Assert.Equal("2.0.0", store.Get(ApprovalFixtures.PluginId)?.Version);
    }

    [Fact]
    public void Get_rejects_blank_plugin_id()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);

        Assert.Throws<ArgumentException>(() => store.Get(" "));
    }

    [Fact]
    public void List_orders_by_plugin_id_then_ordinal_version()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        store.Upsert(ApprovalFixtures.ApprovedRecord() with { PluginId = "b.plugin", Version = "1.0.0" });
        store.Upsert(ApprovalFixtures.ApprovedRecord() with { PluginId = "a.plugin", Version = "2.0.0" });
        store.Upsert(ApprovalFixtures.ApprovedRecord() with { PluginId = "a.plugin", Version = "10.0.0" });
        store.Upsert(ApprovalFixtures.ApprovedRecord() with { PluginId = "a.plugin", Version = "1.0.0" });

        var order = store.List().Select(record => $"{record.PluginId} {record.Version}").ToArray();

        Assert.Equal(["a.plugin 1.0.0", "a.plugin 10.0.0", "a.plugin 2.0.0", "b.plugin 1.0.0"], order);
    }

    [Fact]
    public void List_is_a_snapshot_that_later_mutations_do_not_change()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        var snapshot = store.List();

        store.Upsert(ApprovalFixtures.ApprovedRecord());

        Assert.Empty(snapshot);
        Assert.Single(store.List());
    }

    [Fact]
    public void Set_approved_creates_an_approved_record_for_an_unknown_version()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        var sourceHash = Hashing.SourceHash(ApprovalFixtures.Source);

        var record = store.SetApproved(
            ApprovalFixtures.PluginId,
            ApprovalFixtures.Version,
            sourceHash,
            ApprovalFixtures.ApprovedDependencyHash,
            ApprovalFixtures.Sdk);

        Assert.NotNull(record);
        Assert.Equal(PluginStatus.Approved, record.Status);
        Assert.Equal(sourceHash, record.SourceHash);
        Assert.Equal(ApprovalFixtures.ApprovedDependencyHash, record.DependencyHash);
        Assert.Equal(ApprovalFixtures.Sdk, record.SdkVersion);
        Assert.Equal(
            ApprovalFixtures.Sha256Hex($"{sourceHash}\n{ApprovalFixtures.ApprovedDependencyHash}\n{ApprovalFixtures.Sdk}"),
            record.ApprovedHash);
        Assert.True(record.HasApproval);
        Assert.Equal(record, store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version));
        Assert.Equal(record.CreatedAt, record.UpdatedAt);
    }

    [Fact]
    public void Set_approved_promotes_an_existing_draft_and_keeps_its_created_at()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        var createdAt = new DateTimeOffset(2024, 3, 3, 3, 3, 3, TimeSpan.Zero);
        store.Upsert(ApprovalFixtures.ApprovedRecord() with { Status = PluginStatus.Draft, ApprovedHash = null, CreatedAt = createdAt });

        var approved = store.SetApproved(
            ApprovalFixtures.PluginId,
            ApprovalFixtures.Version,
            Hashing.SourceHash(ApprovalFixtures.Source),
            ApprovalFixtures.ApprovedDependencyHash,
            ApprovalFixtures.Sdk);

        Assert.NotNull(approved);
        Assert.Equal(PluginStatus.Approved, approved.Status);
        Assert.Equal(createdAt, approved.CreatedAt);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Set_approved_again_replaces_the_approved_hash_with_the_new_content()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        var first = store.SetApproved(ApprovalFixtures.PluginId, ApprovalFixtures.Version, Hashing.SourceHash("v1"), ApprovalFixtures.ApprovedDependencyHash, ApprovalFixtures.Sdk);

        var second = store.SetApproved(ApprovalFixtures.PluginId, ApprovalFixtures.Version, Hashing.SourceHash("v2"), ApprovalFixtures.ApprovedDependencyHash, ApprovalFixtures.Sdk);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.ApprovedHash, second.ApprovedHash);
        Assert.Equal(Hashing.ApprovalFingerprint(Hashing.SourceHash("v2"), ApprovalFixtures.ApprovedDependencyHash, ApprovalFixtures.Sdk), second.ApprovedHash);
        Assert.Equal(1, store.Count);
    }

    [Theory]
    [InlineData("", "dependency", "10.0.100")]
    [InlineData("source", " ", "10.0.100")]
    [InlineData("source", "dependency", "")]
    public void Set_approved_rejects_blank_hashes_or_sdk(string sourceHash, string dependencyHash, string sdkVersion)
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);

        Assert.Throws<ArgumentException>(() => store.SetApproved(ApprovalFixtures.PluginId, ApprovalFixtures.Version, sourceHash, dependencyHash, sdkVersion));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Revoke_returns_the_version_to_draft_and_clears_the_approved_hash()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path);
        var approved = store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        var revoked = store.Revoke(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        Assert.NotNull(revoked);
        Assert.Equal(PluginStatus.Draft, revoked.Status);
        Assert.Null(revoked.ApprovedHash);
        Assert.False(revoked.HasApproval);
        Assert.Equal(approved?.SourceHash, revoked.SourceHash);
        Assert.Equal(revoked, store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version));
    }

    [Fact]
    public void Revoke_of_an_unknown_version_returns_null_and_adds_nothing()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);

        Assert.Null(store.Revoke(ApprovalFixtures.PluginId, ApprovalFixtures.Version));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Disable_marks_the_version_disabled_and_keeps_the_approved_hash()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path);
        var approvedHash = store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.ApprovedHash;

        var disabled = store.Disable(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        Assert.NotNull(disabled);
        Assert.Equal(PluginStatus.Disabled, disabled.Status);
        Assert.Equal(approvedHash, disabled.ApprovedHash);
        Assert.False(disabled.HasApproval);
    }

    [Fact]
    public void Enable_after_disable_restores_approved_when_the_hash_is_still_recorded()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path);
        store.Disable(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        var enabled = store.Enable(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        Assert.NotNull(enabled);
        Assert.Equal(PluginStatus.Approved, enabled.Status);
        Assert.True(enabled.HasApproval);
    }

    [Fact]
    public void Enable_of_a_disabled_record_without_an_approved_hash_returns_it_to_draft()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        store.Upsert(ApprovalFixtures.ApprovedRecord() with { Status = PluginStatus.Disabled, ApprovedHash = null });

        var enabled = store.Enable(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        Assert.NotNull(enabled);
        Assert.Equal(PluginStatus.Draft, enabled.Status);
    }

    [Fact]
    public void Enable_of_a_draft_record_with_an_old_approved_hash_keeps_it_draft()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        store.Upsert(ApprovalFixtures.ApprovedRecord() with { Status = PluginStatus.Draft });

        var enabled = store.Enable(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        Assert.NotNull(enabled);
        Assert.Equal(PluginStatus.Draft, enabled.Status);
        Assert.False(enabled.HasApproval);
        Assert.Equal(enabled, store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version));
        Assert.Equal(ApprovalOutcome.RequiresApproval, ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), store).Outcome);
    }

    [Theory]
    [InlineData(PluginStatus.Draft)]
    [InlineData(PluginStatus.Approved)]
    [InlineData(PluginStatus.Uninstalled)]
    public void Enable_leaves_any_status_other_than_disabled_unchanged(PluginStatus status)
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        var record = ApprovalFixtures.ApprovedRecord() with { Status = status };
        store.Upsert(record);

        var enabled = store.Enable(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        Assert.Equal(record, enabled);
        Assert.Equal(record, store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version));
    }

    [Fact]
    public void Enable_of_an_uninstalled_version_does_not_make_it_runnable()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        store.Upsert(ApprovalFixtures.ApprovedRecord() with { Status = PluginStatus.Uninstalled });

        store.Enable(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        Assert.False(ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), store).IsAllowed);
    }

    [Fact]
    public void Revoke_of_a_disabled_version_keeps_it_blocked()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path);
        store.Disable(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        store.Revoke(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        Assert.Equal(ApprovalOutcome.Blocked, ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), store).Outcome);
    }

    [Fact]
    public void Set_approved_on_a_disabled_version_does_not_lift_the_block()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path);
        store.Disable(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        store.SetApproved(ApprovalFixtures.PluginId, ApprovalFixtures.Version, Hashing.SourceHash(ApprovalFixtures.Source), ApprovalFixtures.ApprovedDependencyHash, ApprovalFixtures.Sdk);

        Assert.Equal(ApprovalOutcome.Blocked, ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), store).Outcome);
    }

    [Fact]
    public void Set_approved_on_an_uninstalled_version_does_not_make_it_runnable()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        store.Upsert(ApprovalFixtures.ApprovedRecord() with { Status = PluginStatus.Uninstalled });

        store.SetApproved(ApprovalFixtures.PluginId, ApprovalFixtures.Version, Hashing.SourceHash(ApprovalFixtures.Source), ApprovalFixtures.ApprovedDependencyHash, ApprovalFixtures.Sdk);

        Assert.False(ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), store).IsAllowed);
    }

    [Fact]
    public void Disable_enable_and_revoke_of_unknown_versions_return_null()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);

        Assert.Null(store.Disable(ApprovalFixtures.PluginId, ApprovalFixtures.Version));
        Assert.Null(store.Enable(ApprovalFixtures.PluginId, ApprovalFixtures.Version));
        Assert.Null(store.Revoke(ApprovalFixtures.PluginId, ApprovalFixtures.Version));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Lifecycle_mutations_reject_blank_identity()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);

        Assert.Throws<ArgumentException>(() => store.Revoke("", ApprovalFixtures.Version));
        Assert.Throws<ArgumentException>(() => store.Disable(ApprovalFixtures.PluginId, " "));
        Assert.Throws<ArgumentException>(() => store.Enable(" ", ApprovalFixtures.Version));
    }

    [Fact]
    public void File_path_is_approvals_json_inside_the_full_directory_path()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);

        Assert.Equal(Path.Combine(Path.GetFullPath(directory.Path), ApprovalStore.FileName), store.FilePath);
        Assert.Equal("approvals.json", Path.GetFileName(store.FilePath));
    }

    [Fact]
    public void Constructor_rejects_blank_directory()
    {
        Assert.Throws<ArgumentException>(() => new ApprovalStore(" "));
        Assert.Throws<ArgumentNullException>(() => new ApprovalStore(null!));
    }
}
