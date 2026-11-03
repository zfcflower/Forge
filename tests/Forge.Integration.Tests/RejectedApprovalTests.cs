using Forge.Approval;
using Forge.Ipc;
using Xunit;

namespace Forge.Integration.Tests;

public sealed class RejectedApprovalTests
{
    [Fact]
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

    [Fact]
    public async Task Rejected_approval_with_hashes_outside_the_record_leaves_a_valid_approval_byte_identical()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");
        var approved = fx.ReadRecord("alpha")!;
        Assert.Equal("Approved", approved.Status);
        var bytesBefore = File.ReadAllBytes(fx.ApprovalsPath);

        var rejected = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            fx.Client.ApprovePluginAsync("alpha", "1.0.0", new string('0', 64), approved.DependencyHash, approved.SdkVersion, fx.Ct));
        Assert.Equal("approval_mismatch", rejected.Code);

        Assert.Equal(bytesBefore, File.ReadAllBytes(fx.ApprovalsPath));
        var after = fx.ReadRecord("alpha")!;
        Assert.Equal("Approved", after.Status);
        Assert.Equal(approved.SourceHash, after.SourceHash);
        Assert.Equal(approved.DependencyHash, after.DependencyHash);
        Assert.Equal(PluginSources.AllTools.Length, fx.ToolsOf("alpha").Count);

        var echo = await fx.InvokeAsync("alpha", "echo", """{"text":"still"}""");
        Assert.False(echo.IsError, echo.Content);
        Assert.Equal("a:still", echo.Content);
    }
}
