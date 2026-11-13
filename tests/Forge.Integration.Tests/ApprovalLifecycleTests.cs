using System.Text.Json;
using Forge.Abstractions;
using Forge.Approval;
using Forge.HostClient;
using Forge.Ipc;
using Forge.Plugin.Sdk;
using Xunit;

namespace Forge.Integration.Tests;

public sealed class ApprovalLifecycleTests
{
    [Fact]
    public async Task Unapproved_plugin_registers_no_tools_and_approval_publishes_them()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));

        var changes = 0;
        fx.Client.ToolsChanged += () => Interlocked.Increment(ref changes);

        await fx.StartAsync();
        Assert.Equal(HostStatus.Running, fx.Client.Status);

        var draft = await fx.WaitForRecordAsync("alpha");
        Assert.Equal("Draft", draft.Status);
        Assert.Empty(fx.Client.Tools);
        Assert.Empty(fx.ToolsOf("alpha"));

        await fx.ApproveAsync("alpha");

        await fx.WaitForToolsAsync("alpha", PluginSources.AllTools.Length, "six tools of alpha");
        Assert.True(Volatile.Read(ref changes) >= 1);
        Assert.Equal(PluginSources.AllTools.OrderBy(name => name, StringComparer.Ordinal),
            fx.Client.Tools.Select(tool => tool.Definition.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.All(fx.Client.Tools, tool => Assert.Equal("alpha", ((ExtensionHostTool)tool).PluginId));

        var approved = fx.ReadRecord("alpha");
        Assert.NotNull(approved);
        Assert.Equal("Approved", approved.Status);

        await fx.AssertToolContentAsync("alpha", "echo", """{"text":"hello"}""", "a:hello");
    }

    [Fact]
    public async Task Unapproved_plugin_rejects_plugin_execute_over_the_protocol()
    {
        var layout = RawHostLayout.Create();
        var plugin = Path.Combine(layout.Plugins, "alpha");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "manifest.json"), """{"id":"alpha","version":"1.0.0","source":"Plugin.cs"}""");
        File.WriteAllText(Path.Combine(plugin, "Plugin.cs"), PluginSources.Standard("alpha", echoPrefix: "a"));

        using var budget = new CancellationTokenSource(TestBudgets.Safety(TimeSpan.FromSeconds(120), TimeSpan.FromMinutes(10)));
        var ct = budget.Token;
        await using var host = RawHost.Start(layout);
        await using var transport = await RawHost.ConnectTransportAsync(layout.Pipe, ct);
        await using var peer = new IpcPeer(transport, new IpcPeerOptions { DefaultTimeout = TimeSpan.FromSeconds(60) });
        var registrations = new List<IpcMessage>();
        var registered = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.NotificationReceived += message =>
        {
            if (message.Operation == ExtensionProtocol.ToolsRegister)
            {
                registrations.Add(message);
                registered.TrySetResult(message);
            }
        };
        await peer.StartAsync(ct);
        try
        {
            await peer.RequestAsync(ExtensionProtocol.PluginsRefresh, IpcEmpty.Instance, ct);

            var executeArgs = WireRequests.Execute("alpha", "echo", """{"text":"hi"}""", layout.Root);
            var rejected = await Assert.ThrowsAsync<IpcRemoteException>(
                () => peer.RequestAsync(ExtensionProtocol.PluginExecute, executeArgs, ct));
            Assert.Equal("plugin_not_approved", rejected.Code);

            Assert.Empty(registrations);

            var record = ApprovalDatabase.ReadRecord(layout.Data, "alpha", "1.0.0")
                ?? throw new InvalidOperationException("the host persisted no approval record for alpha 1.0.0");
            await peer.RequestAsync(
                ExtensionProtocol.PluginApprove,
                WireRequests.Approve("alpha", "1.0.0", record.SourceHash, record.DependencyHash, record.SdkVersion),
                ct);

            var notification = await registered.Task.WaitAsync(ct);
            var (registeredId, declared) = WireRequests.Registered(notification);
            Assert.Equal("alpha", registeredId);
            Assert.Equal(PluginSources.AllTools.Length, declared.Count);
            var echoDeclaration = declared.Single(tool => tool.Name == "echo");
            Assert.Equal(Forge.Plugin.Sdk.ToolSideEffect.ReadOnly, echoDeclaration.SideEffect);

            var executed = await peer.RequestAsync(ExtensionProtocol.PluginExecute, executeArgs, ct);
            var (content, isError) = WireRequests.ToolResultOf(executed);
            Assert.Equal("a:hi", content);
            Assert.False(isError);
        }
        finally
        {
            await peer.StopAsync(CancellationToken.None);
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Source_edit_blocks_execution_until_reapproved_and_revoke_blocks_again()
    {
        await using var fx = new E2eFixture();
        await fx.StartWithApprovedPluginAsync("alpha", "old", PluginSources.AllTools.Length);

        var oldEcho = fx.ToolOf("alpha", "echo");
        var before = await fx.InvokeAsync(oldEcho, """{"text":"x"}""");
        Assert.Equal("old:x", before.Content);
        var originalRecord = fx.ReadRecord("alpha")!;

        var edited = PluginSources.Standard("alpha", echoPrefix: "new");
        fx.EditSource("alpha", edited);

        var blockedBeforeRefresh = await fx.InvokeAsync(oldEcho, """{"text":"x"}""");
        Assert.True(blockedBeforeRefresh.IsError);
        Assert.Contains("changed on disk", blockedBeforeRefresh.Content, StringComparison.Ordinal);

        await fx.Client.RefreshPluginsAsync(fx.Ct);
        await fx.WaitForToolsAsync("alpha", 0, "alpha tools to be unregistered");
        Assert.Empty(fx.Client.Tools);

        var changed = fx.ReadRecord("alpha")!;
        Assert.NotEqual(originalRecord.SourceHash, changed.SourceHash);
        Assert.Equal(Hashing.SourceHash(edited), changed.SourceHash);
        Assert.Equal("Approved", changed.Status);

        var blockedAfterRefresh = await fx.InvokeAsync(oldEcho, """{"text":"x"}""");
        Assert.True(blockedAfterRefresh.IsError);
        Assert.Contains("must not run", blockedAfterRefresh.Content, StringComparison.Ordinal);

        var stale = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            fx.Client.ApprovePluginAsync("alpha", "1.0.0", originalRecord.SourceHash, originalRecord.DependencyHash, originalRecord.SdkVersion, fx.Ct));
        Assert.Equal("approval_mismatch", stale.Code);
        Assert.Empty(fx.Client.Tools);

        await fx.Client.ApprovePluginAsync("alpha", "1.0.0", changed.SourceHash, changed.DependencyHash, changed.SdkVersion, fx.Ct);
        await fx.WaitForToolsAsync("alpha", PluginSources.AllTools.Length, "alpha tools after re-approval");

        await fx.AssertToolContentAsync("alpha", "echo", """{"text":"x"}""", "new:x");

        var reapprovedEcho = fx.ToolOf("alpha", "echo");
        await fx.Client.RevokePluginAsync("alpha", "1.0.0", fx.Ct);
        await fx.WaitForToolsAsync("alpha", 0, "alpha tools to be unregistered after revoke");
        Assert.Equal("Draft", fx.ReadRecord("alpha")!.Status);

        var revoked = await fx.InvokeAsync(reapprovedEcho, """{"text":"x"}""");
        Assert.True(revoked.IsError);
        Assert.Contains("must not run", revoked.Content, StringComparison.Ordinal);
    }
}
