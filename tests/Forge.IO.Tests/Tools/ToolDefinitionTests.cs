using System.Text.Json;
using Forge.Abstractions;
using Forge.IO.Tools;
using Xunit;

namespace Forge.IO.Tests;

/// <summary>
/// The tool surface the built-in agents name: seven tools, each with a parseable JSON schema that declares its required
/// parameters, the side effect class the approval policy reads, and a schema string that does not change between
/// instances (a byte-stable schema protects the prompt cache).
/// <para>The names are repeated here as constants because this project references Forge.IO and not Forge.Core; they are
/// copied verbatim from <c>Forge.Core/Prompts/BuiltInAgents.cs</c>, whose implement and review agents whitelist
/// <c>["read", "grep", "find", "ls", "edit", "write", "bash"]</c>.</para>
/// </summary>
public class ToolDefinitionTests
{
    private static readonly string[] AgentWhitelist = ["read", "grep", "find", "ls", "edit", "write", "bash"];

    [Fact]
    public void FileTools_exposes_exactly_the_seven_tools_the_built_in_agents_name()
    {
        var tools = new FileTools();

        Assert.Equal(7, tools.All.Count);
        Assert.Equal(
            AgentWhitelist.Order(StringComparer.Ordinal),
            tools.All.Select(tool => tool.Definition.Name).Order(StringComparer.Ordinal));
        Assert.All(tools.All, tool => Assert.Equal(tool.Definition.Name.ToLowerInvariant(), tool.Definition.Name));
    }

    [Theory]
    [InlineData("read", new[] { "path" })]
    [InlineData("write", new[] { "path", "content" })]
    [InlineData("edit", new[] { "path", "old_string", "new_string" })]
    [InlineData("ls", new string[0])]
    [InlineData("find", new string[0])]
    [InlineData("grep", new[] { "pattern" })]
    [InlineData("bash", new[] { "command" })]
    public void Every_schema_parses_and_declares_its_required_parameters(string name, string[] required)
    {
        var tool = Tool(name);

        using var document = JsonDocument.Parse(tool.Definition.InputSchemaJson);

        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Equal("object", document.RootElement.GetProperty("type").GetString());
        var properties = document.RootElement.GetProperty("properties");
        Assert.True(properties.EnumerateObject().Any(), $"{name} declares no properties");
        Assert.False(string.IsNullOrWhiteSpace(tool.Definition.Description), $"{name} has no description");
        if (required.Length == 0)
        {
            Assert.False(
                document.RootElement.TryGetProperty("required", out _),
                $"{name} has no required parameter but declares a required list");
            return;
        }

        var declared = document.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(required, declared);
        Assert.All(required, parameter => Assert.True(properties.TryGetProperty(parameter, out _), $"{name} requires '{parameter}' but does not describe it"));
    }

    [Fact]
    public void The_side_effect_class_of_each_tool_is_the_one_the_approval_policy_expects()
    {
        // Four read-only tools (read, ls, find, grep), two that write (write, edit) and one that executes (bash).
        var tools = new FileTools();

        var readOnly = tools.All.Where(tool => tool.SideEffect == ToolSideEffect.ReadOnly).Select(tool => tool.Definition.Name);
        var writes = tools.All.Where(tool => tool.SideEffect == ToolSideEffect.Write).Select(tool => tool.Definition.Name);
        var executes = tools.All.Where(tool => tool.SideEffect == ToolSideEffect.Execute).Select(tool => tool.Definition.Name);

        Assert.Equal(["find", "grep", "ls", "read"], readOnly.Order(StringComparer.Ordinal));
        Assert.Equal(["edit", "write"], writes.Order(StringComparer.Ordinal));
        Assert.Equal(["bash"], executes);
    }

    [Fact]
    public void Two_tool_sets_carry_byte_identical_schemas()
    {
        var first = new FileTools();
        var second = new FileTools();
        var firstSchemas = first.All.Select(tool => tool.Definition.InputSchemaJson).ToArray();
        var secondSchemas = second.All.Select(tool => tool.Definition.InputSchemaJson).ToArray();

        Assert.Equal(firstSchemas, secondSchemas);
        Assert.All(firstSchemas, schema => Assert.Contains("\"type\":\"object\"", schema, StringComparison.Ordinal));
    }

    private static ITool Tool(string name) => new FileTools().All.First(tool => tool.Definition.Name == name);
}
