using Forge.Core.Tools;
using Xunit;

namespace Forge.Core.Tests.Tools;

/// <summary>create/update argument validation: every malformed draft is an error result and never reaches authoring.</summary>
public sealed class ExtensionSubmitValidationTests
{
    private const string ValidTool =
        ""","tool":{"name":"wf_tool","description":"Runs the workflow.","input_schema":{"type":"object"}}""";

    public static IEnumerable<object[]> InvalidCases()
    {
        (string Name, string Args) Plugin(string name, string files, string? extra = null) =>
            (name, $$"""{"id":"demo","version":"1.0.0","kind":"plugin","files":{{files}}{{extra ?? string.Empty}}}""");

        (string Name, string Args) Workflow(string name, string files, bool withTool) =>
            (name, $$"""{"id":"wf","version":"1.0.0","kind":"workflow","files":{{files}}{{(withTool ? ValidTool : string.Empty)}}}""");

        var cases = new List<(string, string)>
        {
            ("missing-id", """{"version":"1.0.0","kind":"plugin","files":{"A.cs":"x"}}"""),
            ("missing-version", """{"id":"demo","kind":"plugin","files":{"A.cs":"x"}}"""),
            ("missing-files", """{"id":"demo","version":"1.0.0","kind":"plugin"}"""),
            ("missing-kind", """{"id":"demo","version":"1.0.0","files":{"A.cs":"x"}}"""),
            ("empty-id", """{"id":"","version":"1.0.0","kind":"plugin","files":{"A.cs":"x"}}"""),
            ("empty-version", """{"id":"demo","version":"","kind":"plugin","files":{"A.cs":"x"}}"""),
            ("unknown-kind", """{"id":"demo","version":"1.0.0","kind":"native","files":{"A.cs":"x"}}"""),
            Plugin("plugin-zero-cs", """{"readme.md":"hi"}"""),
            Plugin("plugin-two-cs", """{"A.cs":"x","B.cs":"y"}"""),
            Plugin(
                "plugin-with-tool",
                """{"A.cs":"x"}""",
                ""","tool":{"name":"t","description":"d","input_schema":{"type":"object"}}"""),
            Workflow("workflow-without-tool", """{"workflow.json":"{}"}""", withTool: false),
            Workflow("workflow-without-workflow-json", """{"other.json":"{}"}""", withTool: true),
            Workflow("workflow-plugin-kind-mismatch", """{"A.cs":"x"}""", withTool: true),
            Plugin("manifest-forbidden", """{"manifest.json":"{}","A.cs":"x"}"""),
            Plugin("manifest-case-insensitive", """{"Manifest.JSON":"{}","A.cs":"x"}"""),
            Plugin("absolute-path", """{"/abs.cs":"x"}"""),
            Plugin("parent-segment", """{"sub/../evil.cs":"x"}"""),
            Plugin("dot-segment", """{"sub/./evil.cs":"x"}"""),
            Plugin("drive-colon", """{"C:/evil.cs":"x"}"""),
            Plugin("backslash-separator", """{"sub\\evil.cs":"x"}"""),
            Plugin("empty-segment", """{"sub//evil.cs":"x"}"""),
            Plugin(
                "unknown-permission-kind",
                """{"A.cs":"x"}""",
                ""","permissions":[{"kind":"Root"}]"""),
            Plugin("non-string-file-content", """{"A.cs":42}"""),
            Plugin("empty-files", """{}"""),
            ("id-with-dotdot", """{"id":"a..b","version":"1.0.0","kind":"plugin","files":{"A.cs":"x"}}"""),
            ("not-json", "{not json"),
            ("not-object", "[]"),
        };

        foreach (var isUpdate in new[] { false, true })
        {
            foreach (var (name, args) in cases)
            {
                yield return new object[] { isUpdate, name, args };
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public async Task Invalid_arguments_are_errors_and_authoring_is_never_called(bool isUpdate, string name, string args)
    {
        var authoring = new FakeExtensionAuthoring();
        var tool = new ExtensionSubmitTool(authoring, isUpdate);
        var expectedName = isUpdate ? "extension_update" : "extension_create";

        var result = await tool.InvokeAsync(ExtensionArgs.Call(tool, args), ExtensionArgs.Context, CancellationToken.None);

        Assert.True(result.IsError, $"case '{name}' should fail but returned: {result.Content}");
        Assert.StartsWith(expectedName + ": ", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, authoring.SubmitCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Valid_plugin_and_workflow_drafts_reach_authoring(bool isUpdate)
    {
        var authoring = new FakeExtensionAuthoring();
        var plugin = new ExtensionSubmitTool(authoring, isUpdate);
        var workflow = new ExtensionSubmitTool(authoring, isUpdate);

        var pluginResult = await plugin.InvokeAsync(
            ExtensionArgs.Call(plugin, ExtensionArgs.PluginSubmit()), ExtensionArgs.Context, CancellationToken.None);
        var workflowResult = await workflow.InvokeAsync(
            ExtensionArgs.Call(workflow, ExtensionArgs.WorkflowSubmit()), ExtensionArgs.Context, CancellationToken.None);

        Assert.False(pluginResult.IsError);
        Assert.False(workflowResult.IsError);
        Assert.Equal(2, authoring.SubmitCalls);
    }
}
