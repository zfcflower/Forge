using System.IO;
using Forge.Ipc;
using Xunit;
using static Forge.HostClient.Tests.AuthoringPayloads;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary>Wire payloads of the authoring surface: <c>extensions.stage</c> (<see cref="Sdk.ExtensionStageRequest"/>
/// with its files dictionary, permissions, dependencies and tool), <c>extensions.commit</c>
/// (<see cref="Sdk.ExtensionCommitRequest"/>), <c>extensions.discard</c> (<see cref="Sdk.ExtensionIdRequest"/>),
/// <c>plugin.disable</c>/<c>plugin.enable</c>/<c>plugin.delete</c> (<see cref="Sdk.PluginVersionRequest"/>) and
/// <c>extensions.pending</c> (<see cref="IpcEmpty"/>). Plus local argument validation: invalid arguments throw
/// <see cref="ArgumentException"/>/<see cref="ArgumentNullException"/> before anything reaches the wire.</summary>
public sealed class ExtensionAuthoringPayloadTests
{
    [Fact]
    public async Task Stage_sends_files_dictionary_permissions_dependencies_and_tool()
    {
        await using var host = new FakeHost();
        host.Answer(Sdk.ExtensionProtocol.ExtensionsStage, ValidStaged);
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var staged = await client.StageExtensionAsync(ValidStageRequest(), limit.Token);

        var sent = Assert.Single(host.Requests(Sdk.ExtensionProtocol.ExtensionsStage))
            .Read<Sdk.ExtensionStageRequest>();
        Assert.Equal("demo", sent.PluginId);
        Assert.Equal("1.0.0", sent.Version);
        Assert.Equal("plugin", sent.Kind);
        var sentFile = Assert.Single(sent.Files);
        Assert.Equal("Plugin.cs", sentFile.Key);
        Assert.Equal("// code", sentFile.Value);
        Assert.Equal(new Sdk.Permission(Sdk.PermissionKind.ExecuteCommand, "review"), Assert.Single(sent.Permissions));
        Assert.Equal(new Sdk.ExtensionDependency("Dep", "1.0.0"), Assert.Single(sent.Dependencies));
        Assert.Equal(new Sdk.ExtensionToolSpec("demo-tool", "Does demo.", "{\"type\":\"object\"}"), sent.Tool);

        Assert.Equal("demo", staged.PluginId);
        Assert.Equal("1.0.0", staged.Version);
        Assert.Equal("plugin", staged.Kind);
        var file = Assert.Single(staged.Files);
        Assert.Equal("Plugin.cs", file.Path);
        Assert.Equal("// code", file.Text);
        Assert.Equal([new Sdk.Permission(Sdk.PermissionKind.ReadFile)], staged.Permissions);
        Assert.Equal([new Sdk.ExtensionDependency("Dep", "1.0.0")], staged.Dependencies);
        Assert.Equal("s", staged.SourceHash);
        Assert.Equal("d", staged.DependencyHash);
        Assert.Equal("sdk-1", staged.SdkVersion);
        Assert.Empty(staged.Diagnostics);
        Assert.False(staged.IsUpdate);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Stage_sends_workflow_kind_verbatim()
    {
        await using var host = new FakeHost();
        host.Answer(Sdk.ExtensionProtocol.ExtensionsStage, ValidStaged with { Kind = Sdk.ExtensionKinds.Workflow });
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var staged = await client.StageExtensionAsync(
            ValidStageRequest() with { Kind = Sdk.ExtensionKinds.Workflow }, limit.Token);

        var sent = Assert.Single(host.Requests(Sdk.ExtensionProtocol.ExtensionsStage))
            .Read<Sdk.ExtensionStageRequest>();
        Assert.Equal("workflow", sent.Kind);
        Assert.Equal("workflow", staged.Kind);
    }

    [Fact]
    public async Task Commit_sends_hashes_and_decodes_status_with_tool_names()
    {
        await using var host = new FakeHost();
        host.Answer(Sdk.ExtensionProtocol.ExtensionsCommit, ValidCommit);
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var result = await client.CommitExtensionAsync(
            new Sdk.ExtensionCommitRequest("demo", "s", "d", "sdk-1"), limit.Token);

        var sent = Assert.Single(host.Requests(Sdk.ExtensionProtocol.ExtensionsCommit));
        Assert.Equal(new Sdk.ExtensionCommitRequest("demo", "s", "d", "sdk-1"), sent.Read<Sdk.ExtensionCommitRequest>());

        Assert.Equal("demo", result.Status.PluginId);
        Assert.Equal("approved", result.Status.Status);
        Assert.Equal(["demo-tool"], result.ToolNames);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Discard_sends_only_the_plugin_id()
    {
        await using var host = new FakeHost();
        host.Answer(Sdk.ExtensionProtocol.ExtensionsDiscard, IpcEmpty.Instance);
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await client.DiscardExtensionAsync("demo", limit.Token);

        var sent = Assert.Single(host.Requests(Sdk.ExtensionProtocol.ExtensionsDiscard));
        Assert.Equal(new Sdk.ExtensionIdRequest("demo"), sent.Read<Sdk.ExtensionIdRequest>());
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Theory]
    [InlineData(Sdk.ExtensionProtocol.PluginDisable)]
    [InlineData(Sdk.ExtensionProtocol.PluginEnable)]
    [InlineData(Sdk.ExtensionProtocol.PluginDelete)]
    public async Task Plugin_version_calls_send_plugin_id_and_version(string operation)
    {
        await using var host = new FakeHost();
        host.Answer(operation, IpcEmpty.Instance);
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var call = operation switch
        {
            Sdk.ExtensionProtocol.PluginDisable => client.DisablePluginAsync("demo", "1.0.0", limit.Token),
            Sdk.ExtensionProtocol.PluginEnable => client.EnablePluginAsync("demo", "1.0.0", limit.Token),
            _ => client.DeletePluginAsync("demo", "1.0.0", limit.Token),
        };
        await call;

        var sent = Assert.Single(host.Requests(operation));
        Assert.Equal(new Sdk.PluginVersionRequest("demo", "1.0.0"), sent.Read<Sdk.PluginVersionRequest>());
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Pending_sends_an_empty_payload_and_decodes_both_rows()
    {
        await using var host = new FakeHost();
        host.Answer(Sdk.ExtensionProtocol.ExtensionsPending,
            new Sdk.StagedExtensionList([ValidStaged, ValidStaged with { PluginId = "second" }]));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var pending = await client.GetPendingExtensionsAsync(limit.Token);

        var sent = Assert.Single(host.Requests(Sdk.ExtensionProtocol.ExtensionsPending));
        Assert.Equal(IpcSerializer.Serialize(IpcEmpty.Instance), sent.Payload.ToArray());
        Assert.Equal(["demo", "second"], pending.Select(row => row.PluginId));
        Assert.All(pending, row => Assert.False(row.IsUpdate));
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Stage_rejects_a_null_request_without_ipc()
    {
        await using var harness = await HostClientHarness.StartAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Client.StageExtensionAsync(null!, harness.Limit.Token));

        AssertNoAuthoringTraffic(harness.Host);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Stage_rejects_a_blank_plugin_id_without_ipc(string? pluginId)
    {
        await using var harness = await HostClientHarness.StartAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => harness.Client.StageExtensionAsync(ValidStageRequest() with { PluginId = pluginId! }, harness.Limit.Token));

        AssertNoAuthoringTraffic(harness.Host);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Stage_rejects_a_blank_version_without_ipc(string? version)
    {
        await using var harness = await HostClientHarness.StartAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => harness.Client.StageExtensionAsync(ValidStageRequest() with { Version = version! }, harness.Limit.Token));

        AssertNoAuthoringTraffic(harness.Host);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gadget")]
    [InlineData("PLUGIN")]
    public async Task Stage_rejects_an_unknown_kind_without_ipc(string? kind)
    {
        await using var harness = await HostClientHarness.StartAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Client.StageExtensionAsync(ValidStageRequest() with { Kind = kind! }, harness.Limit.Token));

        Assert.Contains("kind", error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoAuthoringTraffic(harness.Host);
    }

    [Fact]
    public async Task Stage_rejects_null_collections_without_ipc()
    {
        await using var harness = await HostClientHarness.StartAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Client.StageExtensionAsync(ValidStageRequest() with { Files = null! }, harness.Limit.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Client.StageExtensionAsync(ValidStageRequest() with { Permissions = null! }, harness.Limit.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Client.StageExtensionAsync(ValidStageRequest() with { Dependencies = null! }, harness.Limit.Token));

        AssertNoAuthoringTraffic(harness.Host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Stage_rejects_a_blank_file_key_without_ipc(string key)
    {
        await using var harness = await HostClientHarness.StartAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Client.StageExtensionAsync(
                ValidStageRequest() with { Files = new Dictionary<string, string> { [key] = "x" } },
                harness.Limit.Token));

        AssertNoAuthoringTraffic(harness.Host);
    }

    [Fact]
    public async Task Stage_rejects_null_entries_without_ipc()
    {
        await using var harness = await HostClientHarness.StartAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Client.StageExtensionAsync(
                ValidStageRequest() with { Files = new Dictionary<string, string> { ["Plugin.cs"] = null! } },
                harness.Limit.Token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Client.StageExtensionAsync(
                ValidStageRequest() with { Permissions = new List<Sdk.Permission> { null! } },
                harness.Limit.Token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Client.StageExtensionAsync(
                ValidStageRequest() with { Dependencies = new List<Sdk.ExtensionDependency> { null! } },
                harness.Limit.Token));

        AssertNoAuthoringTraffic(harness.Host);
    }

    [Fact]
    public async Task Commit_rejects_a_null_request_without_ipc()
    {
        await using var harness = await HostClientHarness.StartAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Client.CommitExtensionAsync(null!, harness.Limit.Token));

        AssertNoAuthoringTraffic(harness.Host);
    }

    [Theory]
    [InlineData("plugin")]
    [InlineData("source")]
    [InlineData("dependency")]
    [InlineData("sdk")]
    public async Task Commit_rejects_blank_identity_and_hashes_without_ipc(string field)
    {
        await using var harness = await HostClientHarness.StartAsync();

        foreach (var blank in new string?[] { null, "", "   " })
        {
            var request = field switch
            {
                "plugin" => ValidCommitRequest() with { PluginId = blank! },
                "source" => ValidCommitRequest() with { SourceHash = blank! },
                "dependency" => ValidCommitRequest() with { DependencyHash = blank! },
                _ => ValidCommitRequest() with { SdkVersion = blank! },
            };
            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => harness.Client.CommitExtensionAsync(request, harness.Limit.Token));
        }

        AssertNoAuthoringTraffic(harness.Host);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Discard_rejects_a_blank_plugin_id_without_ipc(string? pluginId)
    {
        await using var harness = await HostClientHarness.StartAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => harness.Client.DiscardExtensionAsync(pluginId!, harness.Limit.Token));

        AssertNoAuthoringTraffic(harness.Host);
    }

    [Theory]
    [InlineData("disable", null, "1.0.0")]
    [InlineData("disable", "", "1.0.0")]
    [InlineData("disable", "   ", "1.0.0")]
    [InlineData("disable", "demo", null)]
    [InlineData("disable", "demo", "")]
    [InlineData("disable", "demo", "  ")]
    [InlineData("enable", null, "1.0.0")]
    [InlineData("enable", "demo", "  ")]
    [InlineData("delete", "   ", "1.0.0")]
    [InlineData("delete", "demo", "")]
    public async Task Plugin_version_calls_reject_blank_arguments_without_ipc(
        string operation, string? pluginId, string? version)
    {
        await using var harness = await HostClientHarness.StartAsync();

        var call = operation switch
        {
            "disable" => harness.Client.DisablePluginAsync(pluginId!, version!, harness.Limit.Token),
            "enable" => harness.Client.EnablePluginAsync(pluginId!, version!, harness.Limit.Token),
            _ => harness.Client.DeletePluginAsync(pluginId!, version!, harness.Limit.Token),
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => call);

        AssertNoAuthoringTraffic(harness.Host);
    }

    internal static Sdk.ExtensionStageRequest ValidStageRequest() => new(
        "demo",
        "1.0.0",
        Sdk.ExtensionKinds.Plugin,
        new Dictionary<string, string> { ["Plugin.cs"] = "// code" },
        new List<Sdk.Permission> { new(Sdk.PermissionKind.ExecuteCommand, "review") },
        new List<Sdk.ExtensionDependency> { new("Dep", "1.0.0") },
        new Sdk.ExtensionToolSpec("demo-tool", "Does demo.", "{\"type\":\"object\"}"));

    internal static Sdk.ExtensionCommitRequest ValidCommitRequest() =>
        new("demo", "s", "d", "sdk-1");

    internal static void AssertNoAuthoringTraffic(FakeHost host)
    {
        Assert.Empty(host.Requests(Sdk.ExtensionProtocol.ExtensionsStage));
        Assert.Empty(host.Requests(Sdk.ExtensionProtocol.ExtensionsPending));
        Assert.Empty(host.Requests(Sdk.ExtensionProtocol.ExtensionsCommit));
        Assert.Empty(host.Requests(Sdk.ExtensionProtocol.ExtensionsDiscard));
        Assert.Empty(host.Requests(Sdk.ExtensionProtocol.PluginDisable));
        Assert.Empty(host.Requests(Sdk.ExtensionProtocol.PluginEnable));
        Assert.Empty(host.Requests(Sdk.ExtensionProtocol.PluginDelete));
    }
}
