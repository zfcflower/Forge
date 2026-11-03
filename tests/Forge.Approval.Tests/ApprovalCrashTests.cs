using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalCrashTests
{
    [Fact]
    public void Killed_writer_leaves_last_commit_visible_and_the_store_writable()
    {
        using var directory = new TempApprovalDirectory();
        using var child = ApprovalChildProcess.Start(ApprovalChildProcess.CrashMode, directory.Path);
        var armed = ApprovalStoreTestSupport.WaitForLine(child, TimeSpan.FromSeconds(60));
        if (!string.Equals(armed, ApprovalChildProcess.ArmedLine, StringComparison.Ordinal))
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(TimeSpan.FromSeconds(30));
            }

            var (_, lateOutput) = ApprovalStoreTestSupport.WaitForChild(child, TimeSpan.FromSeconds(30));
            Assert.Fail("the child never armed its uncommitted transaction; got '" + (armed ?? "<closed>") + "': " + lateOutput);
        }

        child.Kill(entireProcessTree: true);
        Assert.True(child.WaitForExit(TimeSpan.FromSeconds(30)), "the killed child exited");

        Assert.Equal("ok", ApprovalStoreTestSupport.IntegrityCheck(directory.Path));
        Assert.Equal(0, ApprovalStoreTestSupport.QueryCount(directory.Path, "SELECT COUNT(*) FROM plugin_approval WHERE plugin_id LIKE 'doomed-%'"));
        Assert.Equal(1, ApprovalStoreTestSupport.TableRowCount(directory.Path, "plugin_approval"));

        using var store = new ApprovalStore(directory.Path);
        Assert.Equal("revision-20", store.Get("victim.plugin", "1.0.0")?.SourceHash);
        Assert.Equal(1, store.Count);

        store.SetApproved("victim.plugin", "1.0.0", "after-crash", "dep", "10.0.100");
        Assert.Equal("after-crash", store.Get("victim.plugin", "1.0.0")?.SourceHash);
        SqliteConnection.ClearAllPools();
    }
}
