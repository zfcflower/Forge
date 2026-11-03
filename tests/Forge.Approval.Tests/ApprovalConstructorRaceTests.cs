using Xunit;

namespace Forge.Approval.Tests;

#if DEBUG
public class ApprovalConstructorRaceTests
{
    [Fact]
    public void External_revoke_between_snapshot_read_and_version_capture_is_not_lost()
    {
        using var directory = new TempApprovalDirectory();
        using var interferer = new ApprovalStore(directory.Path);
        interferer.SetApproved("race.plugin", "1.0.0", "seed-source", "seed-dep", "10.0.100");

        int snapshotReads = 0;
        var options = new ApprovalStoreOptions
        {
            AfterInitialSnapshotRead = () => interferer.Revoke("race.plugin", "1.0.0"),
            SnapshotRead = () => Interlocked.Increment(ref snapshotReads),
        };

        using var victim = new ApprovalStore(directory.Path, options);
        var record = victim.Get("race.plugin", "1.0.0");

        Assert.NotNull(record);
        Assert.Equal(PluginStatus.Draft, record.Status);
        Assert.Null(record.ApprovedHash);
        Assert.False(record.HasApproval);
        Assert.Equal("seed-source", record.SourceHash);
        Assert.Equal(2, snapshotReads);
    }
}
#endif
