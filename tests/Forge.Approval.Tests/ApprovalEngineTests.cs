using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalEngineTests
{
    private const string EditedSource = "public sealed class Tool { public int Value; }";

    [Fact]
    public void Exact_match_against_the_approved_record_is_allowed()
    {
        var decision = ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), ApprovalFixtures.ApprovedRecord());

        Assert.Equal(ApprovalOutcome.Allowed, decision.Outcome);
        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void Candidate_status_approved_does_not_change_an_exact_match()
    {
        var decision = ApprovalEngine.Evaluate(
            ApprovalFixtures.Candidate(status: PluginStatus.Approved),
            ApprovalFixtures.ApprovedRecord());

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void Exact_match_through_the_store_is_allowed()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path);

        Assert.True(ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), store).IsAllowed);
    }

    [Fact]
    public void Missing_record_requires_approval()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);

        Assert.Equal(ApprovalOutcome.RequiresApproval, ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), store).Outcome);
        Assert.Equal(ApprovalOutcome.RequiresApproval, ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), (PluginRecord?)null).Outcome);
    }

    [Fact]
    public void Approval_of_another_version_does_not_cover_the_candidate()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path);
        var newerVersion = ApprovalFixtures.Candidate() with { Version = "1.2.1" };

        var decision = ApprovalEngine.Evaluate(newerVersion, store);

        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
    }

    [Fact]
    public void Draft_record_requires_approval()
    {
        var record = ApprovalFixtures.ApprovedRecord() with { Status = PluginStatus.Draft };

        var decision = ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), record);

        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
        Assert.Contains("is not approved", decision.Reason);
    }

    [Fact]
    public void Revoked_version_requires_approval_through_the_store()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path);
        store.Revoke(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        Assert.Equal(ApprovalOutcome.RequiresApproval, ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), store).Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Approved_record_without_an_approved_hash_requires_approval(string? approvedHash)
    {
        var record = ApprovalFixtures.ApprovedRecord() with { ApprovedHash = approvedHash };

        var decision = ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), record);

        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
        Assert.Contains("has no approved hash", decision.Reason);
    }

    [Fact]
    public void Source_change_requires_approval()
    {
        var decision = ApprovalEngine.Evaluate(
            ApprovalFixtures.Candidate(source: EditedSource),
            ApprovalFixtures.ApprovedRecord());

        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
        Assert.Contains("source changed", decision.Reason);
    }

    [Fact]
    public void Dependency_change_requires_approval()
    {
        var decision = ApprovalEngine.Evaluate(
            ApprovalFixtures.Candidate(dependencyHash: Hashing.DependencyHash([new Dependency("Newtonsoft.Json", "13.0.4")])),
            ApprovalFixtures.ApprovedRecord());

        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
        Assert.Contains("dependencies changed", decision.Reason);
    }

    [Fact]
    public void Sdk_version_change_requires_approval()
    {
        var decision = ApprovalEngine.Evaluate(
            ApprovalFixtures.Candidate(sdkVersion: "10.0.101"),
            ApprovalFixtures.ApprovedRecord());

        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
        Assert.Contains("SDK version changed", decision.Reason);
    }

    [Fact]
    public void Approved_hash_that_does_not_match_the_recorded_hashes_requires_approval()
    {
        var record = ApprovalFixtures.ApprovedRecord() with { ApprovedHash = ApprovalFixtures.Sha256Hex("forged") };

        var decision = ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), record);

        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
        Assert.Contains("invalidated", decision.Reason);
    }

    [Fact]
    public void Tampered_record_fails_the_fingerprint_even_when_the_candidate_matches_it()
    {
        var record = ApprovalFixtures.ApprovedRecord() with { SourceHash = Hashing.SourceHash(EditedSource) };

        var decision = ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(source: EditedSource), record);

        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
        Assert.Contains("invalidated", decision.Reason);
    }

    [Fact]
    public void Candidate_without_a_source_hash_requires_approval()
    {
        var candidate = ApprovalFixtures.Candidate() with { SourceHash = "" };

        var decision = ApprovalEngine.Evaluate(candidate, ApprovalFixtures.ApprovedRecord());

        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
        Assert.Contains("is missing", decision.Reason);
    }

    [Theory]
    [InlineData(PluginStatus.Disabled)]
    [InlineData(PluginStatus.Uninstalled)]
    public void Disabled_or_uninstalled_candidate_is_blocked_even_with_an_exact_approval(PluginStatus status)
    {
        var decision = ApprovalEngine.Evaluate(
            ApprovalFixtures.Candidate(status: status),
            ApprovalFixtures.ApprovedRecord());

        Assert.Equal(ApprovalOutcome.Blocked, decision.Outcome);
    }

    [Theory]
    [InlineData(PluginStatus.Disabled)]
    [InlineData(PluginStatus.Uninstalled)]
    public void Disabled_or_uninstalled_candidate_is_blocked_without_any_record(PluginStatus status)
    {
        var decision = ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(status: status), (PluginRecord?)null);

        Assert.Equal(ApprovalOutcome.Blocked, decision.Outcome);
    }

    [Theory]
    [InlineData(PluginStatus.Disabled)]
    [InlineData(PluginStatus.Uninstalled)]
    public void Disabled_or_uninstalled_record_is_blocked_even_when_hashes_match(PluginStatus status)
    {
        var record = ApprovalFixtures.ApprovedRecord() with { Status = status };

        var decision = ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), record);

        Assert.Equal(ApprovalOutcome.Blocked, decision.Outcome);
    }

    [Fact]
    public void Disabled_version_in_the_store_is_blocked()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path);
        store.Disable(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        Assert.Equal(ApprovalOutcome.Blocked, ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), store).Outcome);
    }

    [Theory]
    [InlineData("", "1.2.0")]
    [InlineData("   ", "1.2.0")]
    [InlineData("acme.tools", "")]
    [InlineData("acme.tools", "  ")]
    public void Missing_plugin_id_or_version_is_blocked(string pluginId, string version)
    {
        var candidate = ApprovalFixtures.Candidate() with { PluginId = pluginId, Version = version };

        var decision = ApprovalEngine.Evaluate(candidate, ApprovalFixtures.ApprovedRecord());

        Assert.Equal(ApprovalOutcome.Blocked, decision.Outcome);
        Assert.Contains("required", decision.Reason);
    }

    [Theory]
    [InlineData("", "1.2.0")]
    [InlineData("acme.tools", " ")]
    public void Missing_plugin_id_or_version_is_blocked_through_the_store_overload(string pluginId, string version)
    {
        using var directory = new TempApprovalDirectory();
        var candidate = ApprovalFixtures.Candidate() with { PluginId = pluginId, Version = version };

        var decision = ApprovalEngine.Evaluate(candidate, new ApprovalStore(directory.Path));

        Assert.Equal(ApprovalOutcome.Blocked, decision.Outcome);
    }

    [Fact]
    public void Record_for_a_different_version_is_blocked()
    {
        var record = ApprovalFixtures.ApprovedRecord() with { Version = "1.2.1" };

        var decision = ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), record);

        Assert.Equal(ApprovalOutcome.Blocked, decision.Outcome);
        Assert.Contains("does not belong", decision.Reason);
    }

    [Fact]
    public void Record_for_a_different_plugin_is_blocked()
    {
        var record = ApprovalFixtures.ApprovedRecord() with { PluginId = "other.plugin" };

        var decision = ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), record);

        Assert.Equal(ApprovalOutcome.Blocked, decision.Outcome);
    }

    [Fact]
    public void Record_with_an_unknown_status_is_blocked()
    {
        var record = ApprovalFixtures.ApprovedRecord() with { Status = (PluginStatus)99 };

        var decision = ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), record);

        Assert.Equal(ApprovalOutcome.Blocked, decision.Outcome);
        Assert.Contains("unknown state", decision.Reason);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);

        Assert.Throws<ArgumentNullException>(() => ApprovalEngine.Evaluate(null!, store));
        Assert.Throws<ArgumentNullException>(() => ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), (ApprovalStore)null!));
        Assert.Throws<ArgumentNullException>(() => ApprovalEngine.Evaluate(null!, (PluginRecord?)null));
    }

    [Theory]
    [InlineData("public sealed class Tool { }\r\n")]
    [InlineData("public sealed class Tool { }\r")]
    [InlineData("public sealed class Tool { }")]
    public void Line_ending_only_edit_of_an_approved_plugin_stays_allowed(string source)
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path, "public sealed class Tool { }\n");

        var decision = ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(source: source), store);

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void Edit_that_adds_a_second_trailing_newline_requires_approval()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path, "public sealed class Tool { }\n");

        var decision = ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(source: "public sealed class Tool { }\n\n"), store);

        Assert.Equal(ApprovalOutcome.RequiresApproval, decision.Outcome);
    }

    [Fact]
    public void Restarted_store_allows_only_the_exact_approved_content()
    {
        using var directory = new TempApprovalDirectory();
        ApprovalFixtures.StoreWithApproval(directory.Path);
        var restarted = new ApprovalStore(directory.Path);

        Assert.True(ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), restarted).IsAllowed);
        Assert.Equal(ApprovalOutcome.RequiresApproval, ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(source: EditedSource), restarted).Outcome);
        Assert.Equal(ApprovalOutcome.RequiresApproval, ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(sdkVersion: "10.0.101"), restarted).Outcome);
    }
}
