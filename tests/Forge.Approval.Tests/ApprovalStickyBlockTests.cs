using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalStickyBlockTests
{
    [Fact]
    public void Concurrent_reapproval_never_lifts_a_concurrent_disable()
    {
        using var directory = new TempApprovalDirectory();
        using (var seed = new ApprovalStore(directory.Path))
        {
            seed.SetApproved("race.plugin", "1.0.0", "seed-source", "seed-dep", "10.0.100");
        }

        using var approver = ApprovalChildProcess.Start(ApprovalChildProcess.BlockRaceMode, directory.Path, "approver", 200);
        using var disabler = ApprovalChildProcess.Start(ApprovalChildProcess.BlockRaceMode, directory.Path, "disabler", 200);
        WaitForReady(approver);
        WaitForReady(disabler);
        approver.StandardInput.WriteLine();
        approver.StandardInput.Flush();
        disabler.StandardInput.WriteLine();
        disabler.StandardInput.Flush();

        var approved = ApprovalStoreTestSupport.WaitForChild(approver, TimeSpan.FromMinutes(3));
        var disabled = ApprovalStoreTestSupport.WaitForChild(disabler, TimeSpan.FromMinutes(3));

        Assert.True(approved.ExitCode == 0, "approver child: " + approved.Output);
        Assert.True(disabled.ExitCode == 0, "disabler child: " + disabled.Output);
        Assert.Contains("busy=0", approved.Output, StringComparison.Ordinal);
        Assert.Contains("fail=0", approved.Output, StringComparison.Ordinal);
        Assert.Contains("busy=0", disabled.Output, StringComparison.Ordinal);
        Assert.Contains("fail=0", disabled.Output, StringComparison.Ordinal);

        using (var check = new ApprovalStore(directory.Path))
        {
            Assert.Equal(PluginStatus.Disabled, check.Get("race.plugin", "1.0.0")?.Status);
            Assert.Equal(ApprovalOutcome.Blocked, ApprovalEngine.Evaluate(RaceCandidate(), check).Outcome);
        }

        using (var reopened = new ApprovalStore(directory.Path))
        {
            Assert.Equal(PluginStatus.Disabled, reopened.Get("race.plugin", "1.0.0")?.Status);
            Assert.Equal(ApprovalOutcome.Blocked, ApprovalEngine.Evaluate(RaceCandidate(), reopened).Outcome);
        }

        SqliteConnection.ClearAllPools();
    }

    private static PluginRecord RaceCandidate() => new()
    {
        PluginId = "race.plugin",
        Version = "1.0.0",
        SourceHash = Hashing.SourceHash("race-content"),
        DependencyHash = "dep",
        SdkVersion = "10.0.100",
    };

    private static void WaitForReady(Process process)
    {
        var line = ApprovalStoreTestSupport.WaitForLine(process, TimeSpan.FromSeconds(60));
        if (string.Equals(line, ApprovalChildProcess.ReadyLine, StringComparison.Ordinal))
        {
            return;
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(30));
        }

        var (_, lateOutput) = ApprovalStoreTestSupport.WaitForChild(process, TimeSpan.FromSeconds(30));
        Assert.Fail("the block-race child never signalled ready; got '" + (line ?? "<closed>") + "': " + lateOutput);
    }
}
