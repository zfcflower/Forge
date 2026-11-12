using System.IO;
using Xunit;
using static Forge.HostClient.Tests.AuthoringPayloads;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary>Field-by-field rejection of authoring answers. Every case throws <see cref="InvalidDataException"/>
/// naming the operation and the problem, records one <c>Diagnostics</c> entry, leaves the connection
/// <see cref="HostStatus.Running"/>, and the very next call with a usable answer succeeds.</summary>
public sealed class ExtensionAuthoringResponseTests
{
    private const string Operation = Sdk.ExtensionProtocol.ExtensionsStage;

    [Theory]
    [InlineData("null", "instead of a staged extension object")]
    [InlineData("42", "instead of a staged extension object")]
    [InlineData("\"oops\"", "instead of a staged extension object")]
    [InlineData("[1,2]", "instead of a staged extension object")]
    public async Task Stage_rejects_a_non_object_answer(string resultJson, string problem)
    {
        await AssertStageRejectedAsync(resultJson, problem, expectIdentity: false);
    }

    [Theory]
    [InlineData("", "has no plugin id")]
    [InlineData("   ", "has no plugin id")]
    public async Task Stage_rejects_a_blank_plugin_id(string pluginId, string problem)
    {
        await AssertStageRejectedAsync(
            Row([("pluginId", JsonString(pluginId)), .. RestOfValidRow("pluginId")]), problem, expectIdentity: false);
    }

    [Fact]
    public async Task Stage_rejects_a_missing_plugin_id()
    {
        await AssertStageRejectedAsync(Row([.. RestOfValidRow("pluginId")]), "has no plugin id", expectIdentity: false);
    }

    [Theory]
    [InlineData("", "has no version")]
    [InlineData("   ", "has no version")]
    public async Task Stage_rejects_a_blank_version(string version, string problem)
    {
        await AssertStageRejectedAsync(
            Row([("version", JsonString(version)), .. RestOfValidRow("version")]),
            problem);
    }

    [Fact]
    public async Task Stage_rejects_a_missing_version()
    {
        await AssertStageRejectedAsync(Row([.. RestOfValidRow("version")]), "has no version");
    }

    [Theory]
    [InlineData("gadget", "'gadget'")]
    [InlineData("", "has no kind")]
    [InlineData("   ", "has no kind")]
    public async Task Stage_rejects_an_unknown_or_blank_kind(string kind, string problem)
    {
        await AssertStageRejectedAsync(
            Row([("kind", JsonString(kind)), .. RestOfValidRow("kind")]),
            problem);
    }

    [Fact]
    public async Task Stage_rejects_a_missing_kind()
    {
        await AssertStageRejectedAsync(Row([.. RestOfValidRow("kind")]), "has no kind");
    }

    [Theory]
    [InlineData(null, "has no file list", true)]
    [InlineData("{}", "cannot be decoded", false)]
    [InlineData("\"x\"", "cannot be decoded", false)]
    [InlineData("[null]", "unusable file at files[0]", true)]
    [InlineData("[{\"path\":\"  \",\"text\":\"x\"}]", "without a path at files[0]", true)]
    [InlineData("[{\"text\":\"x\"}]", "without a path at files[0]", true)]
    [InlineData("[{\"path\":\"Plugin.cs\"}]", "has no text for files[0]", true)]
    public async Task Stage_rejects_unusable_files(string? filesJson, string problem, bool expectIdentity)
    {
        await AssertStageRejectedAsync(Row([.. ValidRow(exceptFiles: true), ("files", filesJson)]), problem, expectIdentity);
    }

    [Fact]
    public async Task Stage_accepts_an_empty_file_text()
    {
        var staged = await StageOnce(
            Row([.. ValidRow(exceptFiles: true), ("files", "[{\"path\":\"Plugin.cs\",\"text\":\"\"}]")]));

        Assert.Equal(string.Empty, Assert.Single(staged.Files).Text);
        Assert.Equal("s", staged.SourceHash);
    }

    [Theory]
    [InlineData(null, "has no permission list", true)]
    [InlineData("{}", "cannot be decoded", false)]
    [InlineData("[{}]", "unusable permission at permissions[0]", true)]
    [InlineData("[{\"scope\":\"x\"}]", "unusable permission at permissions[0]", true)]
    [InlineData("[{\"kind\":99}]", "unusable permission at permissions[0]", true)]
    [InlineData("[{\"kind\":1,\"scope\":\"\"}]", "blank scope at permissions[0]", true)]
    [InlineData("[{\"kind\":1,\"scope\":\"   \"}]", "blank scope at permissions[0]", true)]
    public async Task Stage_rejects_unusable_permissions(string? permissionsJson, string problem, bool expectIdentity)
    {
        await AssertStageRejectedAsync(Row([.. ValidRow(exceptPermissions: true), ("permissions", permissionsJson)]), problem, expectIdentity);
    }

    [Theory]
    [InlineData(null, "has no dependency list")]
    [InlineData("[null]", "unusable dependency at dependencies[0]")]
    public async Task Stage_rejects_unusable_dependencies(string? dependenciesJson, string problem)
    {
        await AssertStageRejectedAsync(
            Row([.. ValidRow(exceptDependencies: true), ("dependencies", dependenciesJson)]), problem);
    }

    [Theory]
    [InlineData(null, "has no diagnostic list")]
    [InlineData("[\"ok\",null]", "unusable diagnostic at diagnostics[1]")]
    public async Task Stage_rejects_unusable_diagnostics(string? diagnosticsJson, string problem)
    {
        await AssertStageRejectedAsync(
            Row([.. ValidRow(exceptDiagnostics: true), ("diagnostics", diagnosticsJson)]), problem);
    }

    [Theory]
    [InlineData("\"\"", "has no source hash while reporting no diagnostics")]
    [InlineData("\"s\"", "has no dependency hash while reporting no diagnostics", "\"\"")]
    [InlineData("\"s\"", "has no SDK version while reporting no diagnostics", "\"d\"", "\"\"")]
    public async Task Stage_rejects_a_missing_hash_without_diagnostics(
        string sourceHash, string problem, string dependencyHash = "\"d\"", string sdkVersion = "\"sdk-1\"")
    {
        await AssertStageRejectedAsync(
            Row([.. ValidRow(exceptHashes: true),
                ("sourceHash", sourceHash),
                ("dependencyHash", dependencyHash),
                ("sdkVersion", sdkVersion)]),
            problem);
    }

    [Fact]
    public async Task Stage_rejects_a_missing_source_hash_without_diagnostics()
    {
        await AssertStageRejectedAsync(
            Row([.. ValidRow(exceptHashes: true), ("dependencyHash", "\"d\""), ("sdkVersion", "\"sdk-1\"")]),
            "has no source hash while reporting no diagnostics");
    }

    [Fact]
    public async Task Stage_with_diagnostics_accepts_missing_hashes_as_empty_strings()
    {
        var staged = await StageOnce(
            Row([.. ValidRow(exceptHashes: true, exceptDiagnostics: true), ("diagnostics", "[\"boom\"]")]));

        Assert.Equal(["boom"], staged.Diagnostics);
        Assert.Equal(string.Empty, staged.SourceHash);
        Assert.Equal(string.Empty, staged.DependencyHash);
        Assert.Equal(string.Empty, staged.SdkVersion);
    }

    [Fact]
    public async Task Stage_with_diagnostics_and_hashes_keeps_both()
    {
        var staged = await StageOnce(
            Row([.. ValidRow(exceptDiagnostics: true), ("diagnostics", "[\"boom\"]")]));

        Assert.Equal(["boom"], staged.Diagnostics);
        Assert.Equal("s", staged.SourceHash);
        Assert.False(staged.IsUpdate);
    }

    [Fact]
    public async Task Stage_reports_an_update_once_a_live_version_exists()
    {
        var staged = await StageOnce(Row([.. ValidRow(), ("liveVersion", "\"0.9.0\"")]));

        Assert.Equal("0.9.0", staged.LiveVersion);
        Assert.True(staged.IsUpdate);
    }

    [Fact]
    public async Task Stage_decodes_the_workflow_tool()
    {
        var staged = await StageOnce(Row([
            .. ValidRow(),
            ("tool", "{\"name\":\"t\",\"description\":\"d\",\"inputSchemaJson\":\"{\\\"type\\\":\\\"object\\\"}\"}")]));

        Assert.Equal(new Sdk.ExtensionToolSpec("t", "d", "{\"type\":\"object\"}"), staged.Tool);
    }

    [Fact]
    public async Task Pending_with_two_usable_rows_returns_both()
    {
        await using var host = new FakeHost();
        host.On(Sdk.ExtensionProtocol.ExtensionsPending, (_, _) => Task.FromResult(Json.Element(
            "{\"items\":[" + ValidStagedJson + "," +
            Row([("pluginId", "\"second\""), .. RestOfValidRow("pluginId"), ("liveVersion", "\"0.9.0\"")]) + "]}")));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var pending = await client.GetPendingExtensionsAsync(limit.Token);

        Assert.Equal(2, pending.Count);
        Assert.Equal("demo", pending[0].PluginId);
        Assert.False(pending[0].IsUpdate);
        Assert.Equal("second", pending[1].PluginId);
        Assert.True(pending[1].IsUpdate);
        Assert.Equal("Plugin.cs", Assert.Single(pending[0].Files).Path);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Theory]
    [InlineData("{\"pluginId\":\"second\",\"version\":\"2.0.0\",\"kind\":\"plugin\",\"files\":[{\"path\":\"Plugin.cs\"}],\"permissions\":[],\"dependencies\":[],\"diagnostics\":[\"x\"]}", "items[1]", "has no text for files[0]")]
    [InlineData("null", "items[1]", "instead of a staged extension object")]
    [InlineData("{\"pluginId\":\"  \",\"version\":\"2.0.0\",\"kind\":\"plugin\",\"files\":[],\"permissions\":[],\"dependencies\":[],\"diagnostics\":[\"x\"]}", "items[1]", "has no plugin id")]
    public async Task Pending_with_a_broken_second_row_rejects_without_a_partial_list(
        string second, string label, string problem)
    {
        await using var host = new FakeHost();
        var calls = 0;
        host.On(Sdk.ExtensionProtocol.ExtensionsPending, (_, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            return Task.FromResult(Json.Element(call == 1
                ? "{\"items\":[" + ValidStagedJson + "," + second + "]}"
                : "{\"items\":[" + ValidStagedJson + "]}"));
        });
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetPendingExtensionsAsync(limit.Token));

        Assert.Contains(Sdk.ExtensionProtocol.ExtensionsPending, error.Message);
        Assert.Contains(label, error.Message);
        Assert.Contains(problem, error.Message);
        Assert.Equal(HostStatus.Running, client.Status);
        var diagnostic = Assert.Single(
            client.Diagnostics,
            entry => entry.Contains(Sdk.ExtensionProtocol.ExtensionsPending, StringComparison.Ordinal));
        Assert.Contains("unusable", diagnostic);
        Assert.Contains(label, diagnostic);

        var pending = await client.GetPendingExtensionsAsync(limit.Token);
        Assert.Equal(["demo"], pending.Select(row => row.PluginId));
        Assert.Equal(2, calls);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Pending_with_an_empty_list_returns_empty()
    {
        await using var host = new FakeHost();
        host.On(Sdk.ExtensionProtocol.ExtensionsPending, (_, _) => Task.FromResult(Json.Element("{\"items\":[]}")));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var pending = await client.GetPendingExtensionsAsync(limit.Token);

        Assert.Empty(pending);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"items\":{}}")]
    [InlineData("{\"items\":null}")]
    [InlineData("[]")]
    public async Task Pending_without_a_list_is_rejected(string payload)
    {
        await using var host = new FakeHost();
        var calls = 0;
        host.On(Sdk.ExtensionProtocol.ExtensionsPending, (_, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            return Task.FromResult(Json.Element(call == 1 ? payload : "{\"items\":[]}"));
        });
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetPendingExtensionsAsync(limit.Token));

        Assert.Contains(Sdk.ExtensionProtocol.ExtensionsPending, error.Message);
        Assert.Equal(HostStatus.Running, client.Status);
        var diagnostic = Assert.Single(
            client.Diagnostics,
            entry => entry.Contains(Sdk.ExtensionProtocol.ExtensionsPending, StringComparison.Ordinal));
        Assert.Contains("unusable", diagnostic);

        Assert.Empty(await client.GetPendingExtensionsAsync(limit.Token));
        Assert.Equal(2, calls);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Commit_decodes_status_and_tool_names()
    {
        await using var host = new FakeHost();
        host.On(Sdk.ExtensionProtocol.ExtensionsCommit, (_, _) => Task.FromResult(Json.Element(ValidCommitJson)));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var result = await client.CommitExtensionAsync(
            new Sdk.ExtensionCommitRequest("demo", "s", "d", "sdk-1"), limit.Token);

        Assert.Equal("demo", result.Status.PluginId);
        Assert.Equal("1.0.0", result.Status.Version);
        Assert.Equal("approved", result.Status.Status);
        Assert.Equal("s", result.Status.SourceHash);
        Assert.Equal(["demo-tool"], result.ToolNames);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Theory]
    [InlineData("{\"pluginId\":\"demo\",\"status\":\"approved\",\"sourceHash\":\"s\",\"dependencyHash\":\"d\",\"sdkVersion\":\"sdk-1\",\"permissions\":[]}", "has no version")]
    [InlineData("{\"pluginId\":\"demo\",\"version\":\"1.0.0\",\"status\":\"approved\",\"sourceHash\":\"s\",\"dependencyHash\":\"d\",\"sdkVersion\":\"sdk-1\"}", "has no permission list")]
    [InlineData("\"approved\"", "cannot be decoded")]
    [InlineData("[1]", "cannot be decoded")]
    public async Task Commit_rejects_an_unusable_status_naming_it(string statusJson, string problem)
    {
        await AssertCommitRejectedAsync(
            "{\"status\":" + statusJson + ",\"toolNames\":[\"demo-tool\"]}", problem, expectStatusWord: true);
    }

    [Fact]
    public async Task Commit_rejects_a_missing_status_naming_it()
    {
        await AssertCommitRejectedAsync("{\"toolNames\":[\"demo-tool\"]}", "carries no plugin status", expectStatusWord: true);
    }

    [Fact]
    public async Task Commit_accepts_an_invalid_row_without_approval_metadata()
    {
        await using var host = new FakeHost();
        host.On(Sdk.ExtensionProtocol.ExtensionsCommit, (_, _) => Task.FromResult(Json.Element(
            "{\"status\":{\"pluginId\":\"demo\",\"status\":\"invalid\",\"permissions\":[],\"error\":\"boom\"}," +
            "\"toolNames\":[]}")));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var result = await client.CommitExtensionAsync(
            new Sdk.ExtensionCommitRequest("demo", "s", "d", "sdk-1"), limit.Token);

        Assert.Equal("invalid", result.Status.Status);
        Assert.Equal(string.Empty, result.Status.Version);
        Assert.Equal(string.Empty, result.Status.SourceHash);
        Assert.Equal(string.Empty, result.Status.DependencyHash);
        Assert.Equal(string.Empty, result.Status.SdkVersion);
        Assert.Equal("boom", result.Status.Error);
        Assert.Empty(result.ToolNames);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Theory]
    [InlineData(null, "carries no tool name list")]
    [InlineData("[\" \"]", "unusable tool name at toolNames[0]")]
    [InlineData("[null]", "unusable tool name at toolNames[0]")]
    [InlineData("[1]", "cannot be decoded")]
    public async Task Commit_rejects_unusable_tool_names(string? toolNamesJson, string problem)
    {
        var payload = toolNamesJson is null
            ? "{\"status\":" + ValidStatusJson() + "}"
            : "{\"status\":" + ValidStatusJson() + ",\"toolNames\":" + toolNamesJson + "}";
        await AssertCommitRejectedAsync(payload, problem, expectStatusWord: false);
    }

    private static async Task AssertStageRejectedAsync(string resultJson, string problem, bool expectIdentity = true)
    {
        await using var host = new FakeHost();
        var calls = 0;
        host.On(Operation, (_, _) => Task.FromResult(
            Json.Element(Interlocked.Increment(ref calls) == 1 ? resultJson : ValidStagedJson)));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.StageExtensionAsync(ExtensionAuthoringPayloadTests.ValidStageRequest(), limit.Token));

        Assert.Contains(Operation, error.Message);
        Assert.Contains(problem, error.Message);
        if (expectIdentity)
        {
            Assert.Contains("('demo')", error.Message);
        }

        Assert.Equal(HostStatus.Running, client.Status);
        var diagnostic = Assert.Single(
            client.Diagnostics, entry => entry.Contains(Operation, StringComparison.Ordinal));
        Assert.Contains("unusable", diagnostic);

        var staged = await client.StageExtensionAsync(
            ExtensionAuthoringPayloadTests.ValidStageRequest(), limit.Token);
        Assert.Equal("demo", staged.PluginId);
        Assert.Equal(2, calls);
        Assert.Equal(HostStatus.Running, client.Status);
        Assert.Single(client.Diagnostics);
    }

    private static async Task<Sdk.StagedExtension> StageOnce(string resultJson)
    {
        await using var host = new FakeHost();
        host.On(Operation, (_, _) => Task.FromResult(Json.Element(resultJson)));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var staged = await client.StageExtensionAsync(
            ExtensionAuthoringPayloadTests.ValidStageRequest(), limit.Token);

        Assert.Equal(HostStatus.Running, client.Status);
        return staged;
    }

    private static async Task AssertCommitRejectedAsync(string payload, string problem, bool expectStatusWord)
    {
        const string operation = Sdk.ExtensionProtocol.ExtensionsCommit;
        await using var host = new FakeHost();
        var calls = 0;
        host.On(operation, (_, _) => Task.FromResult(
            Json.Element(Interlocked.Increment(ref calls) == 1 ? payload : ValidCommitJson)));
        await using var client = await ClientFactory.StartAsync(host);
        using var limit = TestLimit.Create();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.CommitExtensionAsync(ExtensionAuthoringPayloadTests.ValidCommitRequest(), limit.Token));

        Assert.Contains(operation, error.Message);
        Assert.Contains(problem, error.Message);
        if (expectStatusWord)
        {
            Assert.Contains("status", error.Message);
        }

        Assert.Equal(HostStatus.Running, client.Status);
        var diagnostic = Assert.Single(
            client.Diagnostics, entry => entry.Contains(operation, StringComparison.Ordinal));
        Assert.Contains("unusable", diagnostic);

        var result = await client.CommitExtensionAsync(
            ExtensionAuthoringPayloadTests.ValidCommitRequest(), limit.Token);
        Assert.Equal(["demo-tool"], result.ToolNames);
        Assert.Equal(2, calls);
        Assert.Equal(HostStatus.Running, client.Status);
        Assert.Single(client.Diagnostics);
    }

    private static string ValidStatusJson() =>
        "{\"pluginId\":\"demo\",\"version\":\"1.0.0\",\"status\":\"approved\"," +
        "\"sourceHash\":\"s\",\"dependencyHash\":\"d\",\"sdkVersion\":\"sdk-1\"," +
        "\"permissions\":[],\"error\":null}";

    private static string JsonString(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Row(List<(string Name, string? Json)> properties) =>
        "{" + string.Join(",", properties
            .Where(property => property.Json is not null)
            .Select(property => "\"" + property.Name + "\":" + property.Json)) + "}";

    private static (string Name, string? Json)[] ValidRow(
        bool exceptFiles = false,
        bool exceptPermissions = false,
        bool exceptDependencies = false,
        bool exceptHashes = false,
        bool exceptDiagnostics = false) => RestOfValidRow().Where(property =>
            (!exceptFiles || property.Name != "files") &&
            (!exceptPermissions || property.Name != "permissions") &&
            (!exceptDependencies || property.Name != "dependencies") &&
            (!exceptHashes || (property.Name != "sourceHash" && property.Name != "dependencyHash" && property.Name != "sdkVersion")) &&
            (!exceptDiagnostics || property.Name != "diagnostics")).ToArray();

    private static (string Name, string? Json)[] RestOfValidRow(params string[] except) => new (string Name, string? Json)[]
        {
            ("pluginId", "\"demo\""),
            ("version", "\"1.0.0\""),
            ("kind", "\"plugin\""),
            ("files", "[{\"path\":\"Plugin.cs\",\"text\":\"// code\"}]"),
            ("permissions", "[{\"kind\":0}]"),
            ("dependencies", "[{\"name\":\"Dep\",\"version\":\"1.0.0\"}]"),
            ("sourceHash", "\"s\""),
            ("dependencyHash", "\"d\""),
            ("sdkVersion", "\"sdk-1\""),
            ("diagnostics", "[]"),
        }.Where(property => !except.Contains(property.Name)).ToArray();
}
