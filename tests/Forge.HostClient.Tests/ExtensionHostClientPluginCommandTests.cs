using System.Text.Json;
using Forge.Ipc;
using Xunit;

namespace Forge.HostClient.Tests;

public sealed class ExtensionHostClientPluginCommandTests
{
    [Fact]
    public async Task Refresh_sends_plugins_refresh_with_an_empty_object()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await client.RefreshPluginsAsync(limit.Token);

        var request = Assert.Single(host.Requests("plugins.refresh"));
        Assert.Equal(JsonValueKind.Object, request.Arguments!.Value.ValueKind);
        Assert.Empty(request.Arguments.Value.EnumerateObject());
    }

    [Fact]
    public async Task Approve_sends_every_field_of_the_approval_under_the_documented_names()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await client.ApprovePluginAsync("alpha", "1.2.0", "src-hash", "dep-hash", "sdk-1.0", limit.Token);

        var request = Assert.Single(host.Requests("plugin.approve"));
        var args = request.Arguments!.Value;
        Assert.Equal(new[] { "pluginId", "version", "sourceHash", "dependencyHash", "sdkVersion" },
            args.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("alpha", args.GetProperty("pluginId").GetString());
        Assert.Equal("1.2.0", args.GetProperty("version").GetString());
        Assert.Equal("src-hash", args.GetProperty("sourceHash").GetString());
        Assert.Equal("dep-hash", args.GetProperty("dependencyHash").GetString());
        Assert.Equal("sdk-1.0", args.GetProperty("sdkVersion").GetString());
    }

    [Fact]
    public async Task Revoke_sends_plugin_id_and_version_under_the_documented_names()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await client.RevokePluginAsync("alpha", "1.2.0", limit.Token);

        var args = Assert.Single(host.Requests("plugin.revoke")).Arguments!.Value;
        Assert.Equal(new[] { "pluginId", "version" }, args.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("alpha", args.GetProperty("pluginId").GetString());
        Assert.Equal("1.2.0", args.GetProperty("version").GetString());
    }

    [Theory]
    [InlineData("", "1.0")]
    [InlineData("   ", "1.0")]
    [InlineData("alpha", "")]
    [InlineData("alpha", "  ")]
    public async Task Approve_rejects_blank_identity_locally(string pluginId, string version)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ApprovePluginAsync(pluginId, version, "s", "d", "sdk", limit.Token));

        Assert.Empty(host.Requests("plugin.approve"));
    }

    [Theory]
    [InlineData("", "1.0")]
    [InlineData("alpha", " ")]
    public async Task Revoke_rejects_blank_identity_locally(string pluginId, string version)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<ArgumentException>(() => client.RevokePluginAsync(pluginId, version, limit.Token));

        Assert.Empty(host.Requests("plugin.revoke"));
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
        host.On("plugin.approve", (_, _) => throw new FakeHostError("unknown_version", "version 1.0 is not installed"));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            client.ApprovePluginAsync("alpha", "1.0", "s", "d", "sdk", limit.Token));

        Assert.Equal("unknown_version", error.Code);
        Assert.Equal(HostStatus.Running, client.Status);
    }
}
