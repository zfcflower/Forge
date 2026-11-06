using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Tools;
using Xunit;

namespace Forge.Core.Tests.Tools;

/// <summary>Surface of <see cref="ExtensionTools.Create"/>: count, names, side effects, schemas, approval allow-list.</summary>
public sealed class ExtensionToolsSurfaceTests
{
    [Fact]
    public void Create_returns_exactly_five_tools_with_the_documented_names()
    {
        var tools = ExtensionTools.Create(new FakeExtensionAuthoring());

        Assert.Equal(5, tools.Count);
        Assert.Equal(
            new[] { "extension_list", "extension_create", "extension_update", "extension_disable", "extension_delete" },
            tools.Select(tool => tool.Definition.Name).ToArray());
    }

    [Fact]
    public void SideEffects_are_readonly_for_list_and_write_for_the_rest()
    {
        var tools = ExtensionTools.Create(new FakeExtensionAuthoring());

        var effects = tools.ToDictionary(tool => tool.Definition.Name, tool => tool.SideEffect, StringComparer.Ordinal);
        Assert.Equal(ToolSideEffect.ReadOnly, effects["extension_list"]);
        Assert.Equal(ToolSideEffect.Write, effects["extension_create"]);
        Assert.Equal(ToolSideEffect.Write, effects["extension_update"]);
        Assert.Equal(ToolSideEffect.Write, effects["extension_disable"]);
        Assert.Equal(ToolSideEffect.Write, effects["extension_delete"]);
    }

    [Fact]
    public void AllowedWithoutAsking_is_exactly_list_create_update_disable()
    {
        Assert.Equal(
            new[] { "extension_list", "extension_create", "extension_update", "extension_disable" },
            ExtensionToolNames.AllowedWithoutAsking.ToArray());
    }

    [Fact]
    public void Schemas_are_valid_json_and_byte_stable_across_reads()
    {
        var tools = ExtensionTools.Create(new FakeExtensionAuthoring());

        foreach (var tool in tools)
        {
            var first = Encoding.UTF8.GetBytes(tool.Definition.InputSchemaJson);
            var second = Encoding.UTF8.GetBytes(tool.Definition.InputSchemaJson);
            Assert.Equal(first, second);

            using var document = JsonDocument.Parse(tool.Definition.InputSchemaJson);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
    }

    [Fact]
    public void Create_rejects_a_null_authoring()
    {
        Assert.Throws<ArgumentNullException>(() => ExtensionTools.Create(null!));
    }
}
