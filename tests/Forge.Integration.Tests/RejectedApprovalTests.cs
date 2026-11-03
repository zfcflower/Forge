using Forge.Approval;
using Forge.Ipc;
using Xunit;

namespace Forge.Integration.Tests;

public sealed class RejectedApprovalTests
{
    [Fact(Skip = "BUG: ApprovePluginAsync persists the supplied hashes with ApprovalStore.SetApproved before it checks them "
        + "against the current content, then answers approval_mismatch. A rejected approval therefore overwrites the record "
        + "with the stale source hash while keeping status Approved. Expected: a rejected approval leaves approvals.json "
        + "unchanged, so the record still describes the current content.")]
    public async Task Rejected_stale_approval_leaves_the_persisted_record_untouched()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "old"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");
        var original = fx.ReadRecord("alpha")!;

        fx.EditSource("alpha", PluginSources.Standard("alpha", echoPrefix: "new"));
        await fx.Client.RefreshPluginsAsync(fx.Ct);
        var current = fx.ReadRecord("alpha")!;
        Assert.NotEqual(original.SourceHash, current.SourceHash);

        var rejected = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            fx.Client.ApprovePluginAsync("alpha", "1.0.0", original.SourceHash, original.DependencyHash, original.SdkVersion, fx.Ct));
        Assert.Equal("approval_mismatch", rejected.Code);

        var after = fx.ReadRecord("alpha")!;
        Assert.Equal(current.SourceHash, after.SourceHash);
        Assert.Equal(current.DependencyHash, after.DependencyHash);
        Assert.Equal(current.SdkVersion, after.SdkVersion);
        Assert.Equal("Draft", after.Status);
        Assert.Empty(fx.Client.Tools);
    }
}
