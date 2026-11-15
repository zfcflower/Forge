using Forge.Ipc;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary>
/// <c>extensions.install</c> client surface (wave W10a): the request carries the extracted source directory and the
/// origin, the answer is validated field by field, host business errors (including <c>builtin_blocked</c> for a
/// delete/disable of a built-in plugin) propagate unchanged, and local argument validation happens before anything
/// reaches the wire.
/// </summary>
public sealed class ExtensionHostInstallTests
{
    private static readonly Sdk.PluginStatusInfo ValidStatus =
        new("demo", "1.0.0", "draft", "s", "d", "sdk-1", [], null);

    private static readonly Sdk.ExtensionInstallResult ValidInstall = new("demo", "1.0.0", ValidStatus, []);

    [Fact]
    public async Task Install_sends_source_dir_and_origin_and_reads_the_result()
    {
        await using var host = new FakeHost();
        host.Answer(Sdk.ExtensionProtocol.ExtensionsInstall, ValidInstall);
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var result = await client.InstallExtensionAsync(
            new Sdk.ExtensionInstallRequest("/data/install/demo-1", Sdk.ExtensionOrigins.Installed),
            limit.Token);

        var sent = Assert.Single(host.Requests(Sdk.ExtensionProtocol.ExtensionsInstall))
            .Read<Sdk.ExtensionInstallRequest>();
        Assert.Equal("/data/install/demo-1", sent.SourceDir);
        Assert.Equal(Sdk.ExtensionOrigins.Installed, sent.Origin);

        Assert.Equal("demo", result.PluginId);
        Assert.Equal("1.0.0", result.Version);
        Assert.Equal("draft", result.Status.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(client.Diagnostics);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus")]
    public async Task Install_rejects_an_unknown_origin_before_the_wire(string origin)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.InstallExtensionAsync(new Sdk.ExtensionInstallRequest("/data/install/x", origin), limit.Token));

        Assert.Empty(host.Requests(Sdk.ExtensionProtocol.ExtensionsInstall));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Install_rejects_a_blank_source_dir(string? sourceDir)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => client.InstallExtensionAsync(
                new Sdk.ExtensionInstallRequest(sourceDir!, Sdk.ExtensionOrigins.Installed), limit.Token));

        Assert.Empty(host.Requests(Sdk.ExtensionProtocol.ExtensionsInstall));
    }

    [Theory]
    [InlineData("", "carries no plugin id")]
    [InlineData("   ", "carries no plugin id")]
    public async Task Install_rejects_an_answer_with_a_blank_plugin_id(string? pluginId, string problem)
    {
        await using var host = new FakeHost();
        host.Answer(Sdk.ExtensionProtocol.ExtensionsInstall, ValidInstall with { PluginId = pluginId! });
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.InstallExtensionAsync(
                new Sdk.ExtensionInstallRequest("/data/install/x", Sdk.ExtensionOrigins.Installed), limit.Token));

        Assert.Contains(problem, error.Message, StringComparison.Ordinal);
        Assert.Contains(Sdk.ExtensionProtocol.ExtensionsInstall, error.Message, StringComparison.Ordinal);
        Assert.Single(client.Diagnostics);
    }

    [Fact]
    public async Task Install_propagates_a_host_business_error()
    {
        await using var host = new FakeHost();
        host.On(
            Sdk.ExtensionProtocol.ExtensionsInstall,
            (_, _) => throw new FakeHostError("invalid_extension", "the package manifest is unusable"));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<IpcRemoteException>(
            () => client.InstallExtensionAsync(
                new Sdk.ExtensionInstallRequest("/data/install/x", Sdk.ExtensionOrigins.Installed), limit.Token));

        Assert.Equal("invalid_extension", error.Code);
        Assert.Equal(Sdk.ExtensionProtocol.ExtensionsInstall, error.Operation);
        Assert.Empty(client.Diagnostics);
    }

    [Fact]
    public async Task Delete_propagates_builtin_blocked()
    {
        await using var host = new FakeHost();
        host.On(
            Sdk.ExtensionProtocol.PluginDelete,
            (_, _) => throw new FakeHostError("builtin_blocked", "a built-in plugin cannot be deleted"));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<IpcRemoteException>(
            () => client.DeletePluginAsync("demo", "1.0.0", limit.Token));

        Assert.Equal("builtin_blocked", error.Code);
        Assert.Equal(Sdk.ExtensionProtocol.PluginDelete, error.Operation);
    }

    [Fact]
    public async Task Disable_propagates_builtin_blocked()
    {
        await using var host = new FakeHost();
        host.On(
            Sdk.ExtensionProtocol.PluginDisable,
            (_, _) => throw new FakeHostError("builtin_blocked", "a built-in plugin cannot be disabled"));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<IpcRemoteException>(
            () => client.DisablePluginAsync("demo", "1.0.0", limit.Token));

        Assert.Equal("builtin_blocked", error.Code);
        Assert.Equal(Sdk.ExtensionProtocol.PluginDisable, error.Operation);
    }
}
