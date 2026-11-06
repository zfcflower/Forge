using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core.Tools;
using Xunit;

namespace Forge.Core.Tests.Tools;

/// <summary>Draft content: permission/dependency normalization, raw input schema, update flag, semver build metadata.</summary>
public sealed class ExtensionDraftContentTests
{
    [Fact]
    public async Task Permissions_normalize_to_enum_names_and_keep_scope()
    {
        var authoring = new FakeExtensionAuthoring();
        var tool = new ExtensionSubmitTool(authoring, isUpdate: false);
        const string args = """{"id":"demo","version":"1.0.0","kind":"plugin","files":{"A.cs":"x"},"permissions":[{"kind":"readfile"},{"kind":"WriteFile","scope":"/tmp/data"},{"kind":"NETWORK","scope":"  "}]}""";

        var result = await tool.InvokeAsync(ExtensionArgs.Call(tool, args), ExtensionArgs.Context, CancellationToken.None);

        Assert.False(result.IsError);
        var draft = Assert.Single(authoring.Submitted).Draft;
        Assert.Equal(["ReadFile", "WriteFile", "Network"], draft.Permissions.Select(p => p.Kind).ToArray());
        Assert.Null(draft.Permissions[0].Scope);
        Assert.Equal("/tmp/data", draft.Permissions[1].Scope);
        Assert.Null(draft.Permissions[2].Scope);
    }

    [Fact]
    public async Task Dependencies_pass_through_verbatim()
    {
        var authoring = new FakeExtensionAuthoring();
        var tool = new ExtensionSubmitTool(authoring, isUpdate: false);
        const string args = """{"id":"demo","version":"1.0.0","kind":"plugin","files":{"A.cs":"x"},"dependencies":[{"name":"YamlDotNet","version":"13.7.1"}]}""";

        var result = await tool.InvokeAsync(ExtensionArgs.Call(tool, args), ExtensionArgs.Context, CancellationToken.None);

        Assert.False(result.IsError);
        var draft = Assert.Single(authoring.Submitted).Draft;
        var dependency = Assert.Single(draft.Dependencies);
        Assert.Equal("YamlDotNet", dependency.Name);
        Assert.Equal("13.7.1", dependency.Version);
    }

    [Fact]
    public async Task Workflow_input_schema_is_kept_as_equivalent_json_text()
    {
        var authoring = new FakeExtensionAuthoring();
        var tool = new ExtensionSubmitTool(authoring, isUpdate: false);
        var schemaNode = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["q"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("q"),
        };
        var args = JsonSerializer.Serialize(new
        {
            id = "wf",
            version = "1.0.0",
            kind = "workflow",
            files = new Dictionary<string, string> { ["workflow.json"] = "{}" },
            tool = new { name = "wf_tool", description = "Runs it.", input_schema = schemaNode },
        });

        var result = await tool.InvokeAsync(ExtensionArgs.Call(tool, args), ExtensionArgs.Context, CancellationToken.None);

        Assert.False(result.IsError);
        var draftTool = Assert.Single(authoring.Submitted).Draft.Tool;
        Assert.NotNull(draftTool);
        Assert.Equal("wf_tool", draftTool.Name);
        Assert.Equal("Runs it.", draftTool.Description);
        Assert.True(JsonNode.DeepEquals(schemaNode, JsonNode.Parse(draftTool.InputSchemaJson)));
    }

    [Theory]
    [InlineData(false, "extension_create")]
    [InlineData(true, "extension_update")]
    public async Task IsUpdate_matches_the_tool(bool isUpdate, string expectedName)
    {
        var authoring = new FakeExtensionAuthoring();
        var tool = new ExtensionSubmitTool(authoring, isUpdate);

        var result = await tool.InvokeAsync(
            ExtensionArgs.Call(tool, ExtensionArgs.PluginSubmit()), ExtensionArgs.Context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(expectedName, tool.Definition.Name);
        Assert.Equal(isUpdate, Assert.Single(authoring.Submitted).Draft.IsUpdate);
    }

    [Theory]
    [InlineData("1.2.3+build.5")]
    [InlineData("2.0.0-alpha+001")]
    public async Task Semver_build_metadata_in_versions_is_accepted_verbatim(string version)
    {
        var authoring = new FakeExtensionAuthoring();
        var tool = new ExtensionSubmitTool(authoring, isUpdate: false);

        var result = await tool.InvokeAsync(
            ExtensionArgs.Call(tool, ExtensionArgs.PluginSubmit(version: version)), ExtensionArgs.Context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(version, Assert.Single(authoring.Submitted).Draft.Version);
    }

    [Fact]
    public async Task Tool_context_reaches_authoring()
    {
        var authoring = new FakeExtensionAuthoring();
        var tool = new ExtensionSubmitTool(authoring, isUpdate: false);

        await tool.InvokeAsync(ExtensionArgs.Call(tool, ExtensionArgs.PluginSubmit()), ExtensionArgs.Context, CancellationToken.None);

        var context = Assert.Single(authoring.Submitted).Context;
        Assert.Equal(ExtensionArgs.Context.SessionId, context.SessionId);
        Assert.Equal(ExtensionArgs.Context.WorkingDirectory, context.WorkingDirectory);
    }
}
