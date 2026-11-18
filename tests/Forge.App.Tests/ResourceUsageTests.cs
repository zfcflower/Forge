using Forge.Abstractions;
using Forge.App.Agents;
using Xunit;

namespace Forge.App.Tests;

public sealed class ResourceUsageTests
{
    private sealed class FakeTool(string name, string description = "", string schema = "{}") : ITool
    {
        public ToolDefinition Definition { get; } = new(name, description, schema);

        public ToolSideEffect SideEffect => default;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void Tools_are_sized_by_their_definition_text_and_grouped_by_where_they_belong()
    {
        var descriptors = new[]
        {
            new ToolDescriptor("read", ToolOrigin.BuiltIn, ToolGroups.Files),
            new ToolDescriptor("edit", ToolOrigin.BuiltIn, ToolGroups.Files),
            new ToolDescriptor("grep", ToolOrigin.BuiltIn, ToolGroups.Retrieval),
            new ToolDescriptor("gh__pr", ToolOrigin.Mcp, "github", "github", CanDisable: false),
            new ToolDescriptor("gh__ci", ToolOrigin.Mcp, "github", "github", CanDisable: false),
            new ToolDescriptor("plug", ToolOrigin.Extension, "some-plugin", CanDisable: false),
        };
        ITool[] registered =
        [
            new FakeTool("read", new string('a', 36), "{}"),
            new FakeTool("edit", new string('a', 36), "{}"),
            new FakeTool("grep", "", ""),
            new FakeTool("gh__pr", new string('b', 90), "{}"),
            new FakeTool("gh__ci", new string('b', 10), "{}"),
            new FakeTool("plug", "", "{}"),
            new FakeTool("unknown", new string('c', 400), "{}"),
        ];

        var (groups, servers) = ResourceUsageBuilder.Tools(descriptors, registered);

        // "read" + 36 + "{}" = 4 + 36 + 2 = 42 characters, 11 tokens each, and a tool nobody described is not counted at all.
        var files = Assert.Single(groups, row => row.Group == ToolGroups.Files);
        Assert.Equal(2, files.Tools);
        Assert.Equal(42 + 42, files.Size.Chars);
        Assert.Equal(11 + 11, files.Size.Tokens);
        Assert.DoesNotContain(groups, row => row.Group == "unknown");

        // A plugin tool lands in the extensions group whatever name its group carries; MCP tools never reach the group table.
        Assert.Contains(groups, row => row.Group == ToolGroups.Extensions && row.Tools == 1);
        Assert.DoesNotContain(groups, row => row.Group == "github");

        var github = Assert.Single(servers);
        Assert.Equal("github", github.Server);
        Assert.Equal(2, github.Tools);
        Assert.Equal(6 + 90 + 2 + 6 + 10 + 2, github.Size.Chars);
    }

    [Fact]
    public void Prompts_leave_out_the_sources_that_are_empty()
    {
        var layers = new PromptLayers("system text", null, "workspace rules");

        var rows = ResourceUsageBuilder.Prompts(layers, string.Empty, "render");

        Assert.Equal(
            [PromptUsageKind.System, PromptUsageKind.WorkspaceAgents, PromptUsageKind.Rendering],
            rows.Select(row => row.Kind));
        Assert.Equal("system text".Length, rows[0].Size.Chars);
        Assert.Empty(ResourceUsageBuilder.Prompts(null, string.Empty, " "));
    }
}
