using Forge.Ipc;
using Forge.Ipc.Protocol;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

public sealed class ExtensionHostClientPluginCommandTests
{
    private const string Refresh = Sdk.ExtensionProtocol.PluginsRefresh;
    private const string Approve = Sdk.ExtensionProtocol.PluginApprove;
    private const string Revoke = Sdk.ExtensionProtocol.PluginRevoke;

    [Fact]
    public async Task Refresh_sends_plugins_refresh_with_an_empty_object()
    {
        await using var harness = await HostClientHarness.StartAsync();

        await harness.Client.RefreshPluginsAsync(harness.Limit.Token);

        var request = Assert.Single(harness.Host.Requests(Refresh));
        Assert.Equal(IpcSerializer.Serialize(IpcEmpty.Instance), request.Payload.ToArray());
        Assert.Equal(IpcEmpty.Instance, request.Read<IpcEmpty>());
    }

    [Fact]
    public async Task Approve_sends_every_field_of_the_approval_under_the_documented_names()
    {
        await using var harness = await HostClientHarness.StartAsync();

        await harness.Client.ApprovePluginAsync("alpha", "1.2.0", "src-hash", "dep-hash", "sdk-1.0", harness.Limit.Token);

        var request = Assert.Single(harness.Host.Requests(Approve));
        // Exactly the five documented fields, in their documented order: a fixarray of five, nothing appended.
        Assert.Equal(0x95, request.Payload.Span[0]);
        Assert.Equal(
            new PluginApproveRequest("alpha", "1.2.0", "src-hash", "dep-hash", "sdk-1.0"),
            request.Read<PluginApproveRequest>());
    }

    [Fact]
    public async Task Revoke_sends_plugin_id_and_version_under_the_documented_names()
    {
        await using var harness = await HostClientHarness.StartAsync();

        await harness.Client.RevokePluginAsync("alpha", "1.2.0", harness.Limit.Token);

        var request = Assert.Single(harness.Host.Requests(Revoke));
        Assert.Equal(0x92, request.Payload.Span[0]);
        Assert.Equal(new Sdk.PluginVersionRequest("alpha", "1.2.0"), request.Read<Sdk.PluginVersionRequest>());
    }

    [Theory]
    [InlineData("", "1.0")]
    [InlineData("   ", "1.0")]
    [InlineData("alpha", "")]
    [InlineData("alpha", "  ")]
    public async Task Approve_rejects_blank_identity_locally(string pluginId, string version)
    {
        await using var harness = await HostClientHarness.StartAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Client.ApprovePluginAsync(pluginId, version, "s", "d", "sdk", harness.Limit.Token));

        Assert.Empty(harness.Host.Requests(Approve));
    }

    [Theory]
    [InlineData("", "1.0")]
    [InlineData("alpha", " ")]
    public async Task Revoke_rejects_blank_identity_locally(string pluginId, string version)
    {
        await using var harness = await HostClientHarness.StartAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Client.RevokePluginAsync(pluginId, version, harness.Limit.Token));

        Assert.Empty(harness.Host.Requests(Revoke));
    }

    [Fact]
    public async Task Plugin_commands_before_start_fail_with_InvalidOperationException_and_send_nothing()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RefreshPluginsAsync(limit.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ApprovePluginAsync("alpha", "1.0", "s", "d", "sdk", limit.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RevokePluginAsync("alpha", "1.0", limit.Token));
    }

    [Fact]
    public async Task A_host_error_on_approve_propagates_as_IpcRemoteException()
    {
        await using var host = new FakeHost();
        host.On(Approve, (_, _) => throw new FakeHostError("unknown_version", "version 1.0 is not installed"));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            client.ApprovePluginAsync("alpha", "1.0", "s", "d", "sdk", limit.Token));

        Assert.Equal("unknown_version", error.Code);
        Assert.Equal(HostStatus.Running, client.Status);
    }
}
