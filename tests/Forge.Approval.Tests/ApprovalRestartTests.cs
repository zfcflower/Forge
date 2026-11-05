using System.Diagnostics;
using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalRestartTests
{
    // How many durable writes one approval write may cost; see DurableWriteBudget.
    private const int ProbeWritesPerBudget = 10;
    private const int ProbeWrites = 11;

    // The budget never drops below the old fixed 50 ms budget on a fast disk and never reaches the seconds this test
    // rules out, however slow the disk is.
    private static readonly TimeSpan MinimumBudget = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaximumBudget = TimeSpan.FromSeconds(1);

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
        var budget = DurableWriteBudget(directory.Path);
        using var store = new ApprovalStore(directory.Path);
        var samples = new List<TimeSpan>(200);
        for (var i = 0; i < 200; i++)
        {
            var elapsed = Stopwatch.StartNew();
            store.SetApproved($"p{i}.plugin", "1.0.0", $"hash-{i}", "dep", "10.0.100");
            elapsed.Stop();
            // The first write also creates the WAL file and runs the write path for the first time.
            if (i > 0)
            {
                samples.Add(elapsed.Elapsed);
            }
        }

        samples.Sort();
        var median = samples[samples.Count / 2];
        Assert.True(median < budget, $"median write took {median}, budget {budget}");
        Assert.Equal(200, store.Count);
    }

    /// <summary>
    /// Budget for the median write, derived from what one durable write costs on the machine running the test: a
    /// commit is one WAL append plus one fsync (<c>synchronous=FULL</c>), and a probe that appends one byte and flushes
    /// it to disk does the same physical work. A slow or contended disk therefore raises the budget instead of failing
    /// the suite, while a write path costing many times one fsync (extra flushes, rewriting the table, reopening the
    /// database per write) still fails.
    /// </summary>
    private static TimeSpan DurableWriteBudget(string directory)
    {
        var samples = new List<TimeSpan>(ProbeWrites);
        var path = Path.Combine(directory, "durable-write-probe.bin");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096))
        {
            for (var i = 0; i < ProbeWrites; i++)
            {
                var elapsed = Stopwatch.StartNew();
                stream.Write([(byte)i]);
                stream.Flush(flushToDisk: true);
                elapsed.Stop();
                // The first probe write also creates the file.
                if (i > 0)
                {
                    samples.Add(elapsed.Elapsed);
                }
            }
        }

        samples.Sort();
        return BudgetFor(samples[samples.Count / 2]);
    }

    private static TimeSpan BudgetFor(TimeSpan durableWrite) =>
        TimeSpan.FromTicks(Math.Clamp(durableWrite.Ticks * ProbeWritesPerBudget, MinimumBudget.Ticks, MaximumBudget.Ticks));

    [Theory]
    [InlineData(2, 50)]      // a fast disk keeps the historical 50 ms budget
    [InlineData(74, 740)]    // the hosted Windows runner that failed at 74 ms gets ten times that, so it passes
    [InlineData(400, 1000)]  // a very slow disk never pushes the budget into the seconds the test rules out
    public void Write_latency_budget_follows_the_disk_but_stays_within_bounds(double durableWriteMs, double expectedMs)
    {
        Assert.Equal(expectedMs, BudgetFor(TimeSpan.FromMilliseconds(durableWriteMs)).TotalMilliseconds, precision: 6);
    }
}
