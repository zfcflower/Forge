using System.IO;
using Forge.Ipc;
using Xunit;
using static Forge.HostClient.Tests.AuthoringPayloads;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary>Wire payloads of the authoring surface: <c>extensions.stage</c> (camelCase keys, <c>files</c> as a
/// dictionary, <c>permissions</c> as numeric enums, <c>dependencies</c>, <c>tool.inputSchemaJson</c>),
/// <c>extensions.commit</c>, <c>extensions.discard</c> (<c>{"pluginId":…}</c>),
/// <c>plugin.disable</c>/<c>plugin.enable</c>/<c>plugin.delete</c> (<c>{"pluginId","version"}</c>) and
/// <c>extensions.pending</c> (<c>{}</c>). Plus local argument validation: invalid arguments throw
/// <see cref="ArgumentException"/>/<see cref="ArgumentNullException"/> before anything reaches the wire.</summary>
public sealed class ExtensionAuthoringPayloadTests
{
    [Fact]
    public async Task Stage_sends_camelCase_files_dictionary_permissions_enum_dependencies_and_tool()
    {
        await using var host = new FakeHost();
        host.Answer(Sdk.ExtensionProtocol.ExtensionsStage, ValidStagedJson);
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var staged = await client.StageExtensionAsync(ValidStageRequest(), limit.Token);

        var sent = Assert.Single(host.Requests(Sdk.ExtensionProtocol.ExtensionsStage)).Arguments!.Value;
        Assert.Equal("demo", sent.GetProperty("pluginId").GetString());
        Assert.Equal("1.0.0", sent.GetProperty("version").GetString());
        Assert.Equal("plugin", sent.GetProperty("kind").GetString());
        Assert.Equal("// code", sent.GetProperty("files").GetProperty("Plugin.cs").GetString());
        var permission = Assert.Single(sent.GetProperty("permissions").EnumerateArray());
        Assert.Equal((int)Sdk.PermissionKind.ExecuteCommand, permission.GetProperty("kind").GetInt32());
        Assert.Equal("review", permission.GetProperty("scope").GetString());
        var dependency = Assert.Single(sent.GetProperty("dependencies").EnumerateArray());
        Assert.Equal("Dep", dependency.GetProperty("name").GetString());
        Assert.Equal("1.0.0", dependency.GetProperty("version").GetString());
        var tool = sent.GetProperty("tool");
        Assert.Equal("demo-tool", tool.GetProperty("name").GetString());
        Assert.Equal("Does demo.", tool.GetProperty("description").GetString());
        Assert.Equal("{\"type\":\"object\"}", tool.GetProperty("inputSchemaJson").GetString());

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
        host.Answer(Sdk.ExtensionProtocol.ExtensionsStage,
            ValidStagedJson.Replace("\"kind\":\"plugin\"", "\"kind\":\"workflow\""));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var staged = await client.StageExtensionAsync(
            ValidStageRequest() with { Kind = Sdk.ExtensionKinds.Workflow }, limit.Token);

        var sent = Assert.Single(host.Requests(Sdk.ExtensionProtocol.ExtensionsStage)).Arguments!.Value;
        Assert.Equal("workflow", sent.GetProperty("kind").GetString());
        Assert.Equal("workflow", staged.Kind);
    }

    [Fact]
    public async Task Commit_sends_hashes_and_decodes_status_with_tool_names()
    {
        await using var host = new FakeHost();
        host.Answer(Sdk.ExtensionProtocol.ExtensionsCommit, ValidCommitJson);
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var result = await client.CommitExtensionAsync(
            new Sdk.ExtensionCommitRequest("demo", "s", "d", "sdk-1"), limit.Token);

        var sent = Assert.Single(host.Requests(Sdk.ExtensionProtocol.ExtensionsCommit)).Arguments!.Value;
        Assert.Equal("demo", sent.GetProperty("pluginId").GetString());
        Assert.Equal("s", sent.GetProperty("sourceHash").GetString());
        Assert.Equal("d", sent.GetProperty("dependencyHash").GetString());
        Assert.Equal("sdk-1", sent.GetProperty("sdkVersion").GetString());
        Assert.Equal(4, sent.EnumerateObject().Count());

        Assert.Equal("demo", result.Status.PluginId);
        Assert.Equal("approved", result.Status.Status);
        Assert.Equal(["demo-tool"], result.ToolNames);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Discard_sends_only_the_plugin_id()
    {
        await using var host = new FakeHost();
        host.Answer(Sdk.ExtensionProtocol.ExtensionsDiscard, "{}");
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await client.DiscardExtensionAsync("demo", limit.Token);

        var sent = Assert.Single(host.Requests(Sdk.ExtensionProtocol.ExtensionsDiscard)).Arguments!.Value;
        Assert.Equal("demo", sent.GetProperty("pluginId").GetString());
        Assert.Equal("{\"pluginId\":\"demo\"}", sent.GetRawText());
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Theory]
    [InlineData(Sdk.ExtensionProtocol.PluginDisable)]
    [InlineData(Sdk.ExtensionProtocol.PluginEnable)]
    [InlineData(Sdk.ExtensionProtocol.PluginDelete)]
    public async Task Plugin_version_calls_send_plugin_id_and_version(string operation)
    {
        await using var host = new FakeHost();
        host.Answer(operation, "{}");
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var call = operation switch
        {
            Sdk.ExtensionProtocol.PluginDisable => client.DisablePluginAsync("demo", "1.0.0", limit.Token),
            Sdk.ExtensionProtocol.PluginEnable => client.EnablePluginAsync("demo", "1.0.0", limit.Token),
            _ => client.DeletePluginAsync("demo", "1.0.0", limit.Token),
        };
        await call;

        var sent = Assert.Single(host.Requests(operation)).Arguments!.Value;
        Assert.Equal("demo", sent.GetProperty("pluginId").GetString());
        Assert.Equal("1.0.0", sent.GetProperty("version").GetString());
        Assert.Equal(2, sent.EnumerateObject().Count());
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Pending_sends_an_empty_object_and_decodes_both_rows()
    {
        await using var host = new FakeHost();
        host.Answer(Sdk.ExtensionProtocol.ExtensionsPending,
            "{\"items\":[" + ValidStagedJson + "," +
            ValidStagedJson.Replace("\"pluginId\":\"demo\"", "\"pluginId\":\"second\"") + "]}");
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var pending = await client.GetPendingExtensionsAsync(limit.Token);

        var sent = Assert.Single(host.Requests(Sdk.ExtensionProtocol.ExtensionsPending)).Arguments!.Value;
        Assert.Equal("{}", sent.GetRawText());
        Assert.Equal(["demo", "second"], pending.Select(row => row.PluginId));
        Assert.All(pending, row => Assert.False(row.IsUpdate));
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Stage_rejects_a_null_request_without_ipc()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.StageExtensionAsync(null!, limit.Token));

        AssertNoAuthoringTraffic(host);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Stage_rejects_a_blank_plugin_id_without_ipc(string? pluginId)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => client.StageExtensionAsync(ValidStageRequest() with { PluginId = pluginId! }, limit.Token));

        AssertNoAuthoringTraffic(host);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Stage_rejects_a_blank_version_without_ipc(string? version)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => client.StageExtensionAsync(ValidStageRequest() with { Version = version! }, limit.Token));

        AssertNoAuthoringTraffic(host);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gadget")]
    [InlineData("PLUGIN")]
    public async Task Stage_rejects_an_unknown_kind_without_ipc(string? kind)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => client.StageExtensionAsync(ValidStageRequest() with { Kind = kind! }, limit.Token));

        Assert.Contains("kind", error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoAuthoringTraffic(host);
    }

    [Fact]
    public async Task Stage_rejects_null_collections_without_ipc()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.StageExtensionAsync(ValidStageRequest() with { Files = null! }, limit.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.StageExtensionAsync(ValidStageRequest() with { Permissions = null! }, limit.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.StageExtensionAsync(ValidStageRequest() with { Dependencies = null! }, limit.Token));

        AssertNoAuthoringTraffic(host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Stage_rejects_a_blank_file_key_without_ipc(string key)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.StageExtensionAsync(
                ValidStageRequest() with { Files = new Dictionary<string, string> { [key] = "x" } },
                limit.Token));

        AssertNoAuthoringTraffic(host);
    }

    [Fact]
    public async Task Stage_rejects_null_entries_without_ipc()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.StageExtensionAsync(
                ValidStageRequest() with { Files = new Dictionary<string, string> { ["Plugin.cs"] = null! } },
                limit.Token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.StageExtensionAsync(
                ValidStageRequest() with { Permissions = new List<Sdk.Permission> { null! } },
                limit.Token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.StageExtensionAsync(
                ValidStageRequest() with { Dependencies = new List<Sdk.ExtensionDependency> { null! } },
                limit.Token));

        AssertNoAuthoringTraffic(host);
    }

    [Fact]
    public async Task Commit_rejects_a_null_request_without_ipc()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.CommitExtensionAsync(null!, limit.Token));

        AssertNoAuthoringTraffic(host);
    }

    [Theory]
    [InlineData("plugin")]
    [InlineData("source")]
    [InlineData("dependency")]
    [InlineData("sdk")]
    public async Task Commit_rejects_blank_identity_and_hashes_without_ipc(string field)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

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
                () => client.CommitExtensionAsync(request, limit.Token));
        }

        AssertNoAuthoringTraffic(host);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Discard_rejects_a_blank_plugin_id_without_ipc(string? pluginId)
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => client.DiscardExtensionAsync(pluginId!, limit.Token));

        AssertNoAuthoringTraffic(host);
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
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var call = operation switch
        {
            "disable" => client.DisablePluginAsync(pluginId!, version!, limit.Token),
            "enable" => client.EnablePluginAsync(pluginId!, version!, limit.Token),
            _ => client.DeletePluginAsync(pluginId!, version!, limit.Token),
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => call);

        AssertNoAuthoringTraffic(host);
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
