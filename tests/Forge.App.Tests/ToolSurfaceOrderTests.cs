using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The tool array's order is part of the prompt: the model reads it top to bottom, and the prefix has to stay byte-stable for
/// the provider's cache. The fixed segment is the authored set (Core's own tools, then the host's file and shell tools), so it
/// is published in declaration order — <c>bash</c> last among the file operations — while the two assembled segments are sorted
/// by name, because only their set is meaningful and their arrival order is not.
/// </summary>
public sealed class ToolSurfaceOrderTests
{
    [Fact]
    public void The_shell_tool_is_declared_after_the_file_and_directory_tools()
    {
        // The host the app builds: Core registers no file tools of its own, the host supplies them as the fixed segment.
        var surface = new ToolSurface([]);
        surface.ReplaceExternal(new FileTools().All, [], []);

        Assert.Equal(
            ["read", "write", "edit", "ls", "find", "grep", "copy", "move", "delete", "bash"],
            surface.Tools.Definitions.Select(definition => definition.Name));
    }

    [Fact]
    public void The_conditional_and_external_segments_are_sorted_by_name()
    {
        var surface = new ToolSurface([new NamedTool("bash")]);
        surface.ReplaceExternal(
            [new NamedTool("skill")],
            [new NamedTool("web_search"), new NamedTool("web_fetch")],
            [new NamedTool("plugin_b"), new NamedTool("plugin_a")]);

        // The fixed segment keeps its declaration order (the built-in, then the host's tool); the other two are by name.
        Assert.Equal(
            ["bash", "skill", "web_fetch", "web_search", "plugin_a", "plugin_b"],
            surface.Tools.Definitions.Select(definition => definition.Name));
    }

    private sealed class NamedTool(string name) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, "d", "{}");

        public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
