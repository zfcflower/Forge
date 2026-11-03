using System.Diagnostics;
using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalRestartTests
{
    [Fact]
    public void Disable_enable_and_revoke_blocks_persist_across_reopen_for_exact_hash_only()
    {
        using var directory = new TempApprovalDirectory();
        var sourceA = Hashing.SourceHash("content-a");
        var sourceB = Hashing.SourceHash("content-b");
        using (var store = new ApprovalStore(directory.Path))
        {
            store.SetApproved(ApprovalFixtures.PluginId, ApprovalFixtures.Version, sourceA, ApprovalFixtures.ApprovedDependencyHash, ApprovalFixtures.Sdk);
            store.SetApproved(ApprovalFixtures.PluginId, ApprovalFixtures.Version, sourceB, ApprovalFixtures.ApprovedDependencyHash, ApprovalFixtures.Sdk);
            store.Disable(ApprovalFixtures.PluginId, ApprovalFixtures.Version);
        }

        using (var reopened = new ApprovalStore(directory.Path))
        {
            Assert.Equal(PluginStatus.Disabled, reopened.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.Status);
            var enabled = reopened.Enable(ApprovalFixtures.PluginId, ApprovalFixtures.Version);
            Assert.Equal(PluginStatus.Approved, enabled?.Status);
            Assert.False(ApprovalEngine.Evaluate(ApprovalFixtures.Candidate("content-a"), reopened).IsAllowed);
            Assert.True(ApprovalEngine.Evaluate(ApprovalFixtures.Candidate("content-b"), reopened).IsAllowed);
            reopened.Revoke(ApprovalFixtures.PluginId, ApprovalFixtures.Version);
        }

        using (var rereopened = new ApprovalStore(directory.Path))
        {
            Assert.Equal(PluginStatus.Draft, rereopened.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.Status);
            Assert.Equal(ApprovalOutcome.RequiresApproval, ApprovalEngine.Evaluate(ApprovalFixtures.Candidate("content-b"), rereopened).Outcome);
        }

        using (var blocked = new ApprovalStore(directory.Path))
        {
            blocked.Upsert(ApprovalFixtures.ApprovedRecord() with { Status = PluginStatus.Uninstalled });
        }

        using (var uninstalled = new ApprovalStore(directory.Path))
        {
            uninstalled.Enable(ApprovalFixtures.PluginId, ApprovalFixtures.Version);
            Assert.Equal(PluginStatus.Uninstalled, uninstalled.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.Status);
            Assert.Equal(ApprovalOutcome.Blocked, ApprovalEngine.Evaluate(ApprovalFixtures.Candidate(), uninstalled).Outcome);
        }
    }

    [Fact]
    public void Two_hundred_writes_stay_within_the_latency_budget()
    {
        using var directory = new TempApprovalDirectory();
        using var store = new ApprovalStore(directory.Path);
        var samples = new List<TimeSpan>(200);
        for (var i = 0; i < 200; i++)
        {
            var elapsed = Stopwatch.StartNew();
            store.SetApproved($"p{i}.plugin", "1.0.0", $"hash-{i}", "dep", "10.0.100");
            elapsed.Stop();
            samples.Add(elapsed.Elapsed);
        }

        samples.Sort();
        var median = samples[samples.Count / 2];
        Assert.True(median < TimeSpan.FromMilliseconds(50), $"median write took {median}");
        Assert.Equal(200, store.Count);
    }
}
