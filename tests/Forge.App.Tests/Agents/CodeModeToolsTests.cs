using Forge.Abstractions;
using Forge.App.Agents;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// The rule that decides which of a session's tools a <c>run_code</c> script may call (wave W14): plugins are taken
/// wholesale, the other three sources keep only read-only tools, and a name that a plugin already claimed stays the
/// plugin's. These tests pin exactly that, against a minimal stand-in tool so no extension host is needed.
/// </summary>
public sealed class CodeModeToolsTests
{
    [Fact]
    public void The_file_source_keeps_only_its_read_only_tools_in_all_order()
    {
        var collected = CodeModeTools.Collect([], new FileTools().All, [], []);

        Assert.Equal(["read", "ls", "find", "grep"], collected.Select(static tool => tool.Definition.Name).ToArray());
        Assert.DoesNotContain(collected, static tool => tool.Definition.Name is "write" or "edit" or "bash");
    }

    [Fact]
    public void The_web_source_keeps_read_only_tools_and_drops_execute_ones()
    {
        var readOnly = new StubTool("web_read", ToolSideEffect.ReadOnly);
        var execute = new StubTool("web_run", ToolSideEffect.Execute);

        var collected = CodeModeTools.Collect([], [], [readOnly, execute], []);

        Assert.Equal(["web_read"], collected.Select(static tool => tool.Definition.Name).ToArray());
        Assert.Same(readOnly, collected[0]);
    }

    [Fact]
    public void The_mcp_source_keeps_read_only_tools_and_drops_write_ones()
    {
        var readOnly = new StubTool("mcp_read", ToolSideEffect.ReadOnly);
        var write = new StubTool("mcp_write", ToolSideEffect.Write);

        var collected = CodeModeTools.Collect([], [], [], [readOnly, write]);

        Assert.Equal(["mcp_read"], collected.Select(static tool => tool.Definition.Name).ToArray());
        Assert.Same(readOnly, collected[0]);
    }

    [Fact]
    public void The_plugin_source_takes_write_and_execute_tools_wholesale()
    {
        var write = new StubTool("plugin_write", ToolSideEffect.Write);
        var execute = new StubTool("plugin_run", ToolSideEffect.Execute);

        var collected = CodeModeTools.Collect([write, execute], [], [], []);

        Assert.Equal(["plugin_write", "plugin_run"], collected.Select(static tool => tool.Definition.Name).ToArray());
        Assert.Same(write, collected[0]);
        Assert.Same(execute, collected[1]);
    }

    [Fact]
    public void A_plugin_write_tool_wins_the_name_over_the_files_read_only_one()
    {
        var pluginGrep = new StubTool("grep", ToolSideEffect.Write);
        var fileGrep = new FileTools().Grep;

        var collected = CodeModeTools.Collect([pluginGrep], new FileTools().All, [], []);

        var greps = collected.Where(static tool => tool.Definition.Name == "grep").ToArray();
        Assert.Single(greps);
        Assert.Equal(ToolSideEffect.Write, greps[0].SideEffect);
        Assert.Same(pluginGrep, greps[0]);
    }

    [Fact]
    public void The_plugin_order_is_kept_whatever_the_input_order_is()
    {
        var a = new StubTool("a", ToolSideEffect.Write);
        var b = new StubTool("b", ToolSideEffect.Execute);
        var c = new StubTool("c", ToolSideEffect.Write);

        var forward = CodeModeTools.Collect([a, b, c], [], [], []);
        var backward = CodeModeTools.Collect([c, b, a], [], [], []);

        Assert.Equal(["a", "b", "c"], forward.Select(static tool => tool.Definition.Name).ToArray());
        Assert.Equal(["c", "b", "a"], backward.Select(static tool => tool.Definition.Name).ToArray());
    }

    [Fact]
    public void Four_empty_sources_collect_nothing()
    {
        Assert.Empty(CodeModeTools.Collect([], [], [], []));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_null_source_is_rejected(int which)
    {
        var tools = new[] { new StubTool("t", ToolSideEffect.ReadOnly) };
        IReadOnlyList<ITool>?[] sources = [tools, tools, tools, tools];
        sources[which] = null;

        Assert.Throws<ArgumentNullException>(() =>
            CodeModeTools.Collect(sources[0]!, sources[1]!, sources[2]!, sources[3]!));
    }

    /// <summary>A tool with a name and a declared side effect; <see cref="Collect"/> never invokes one, so neither does this.</summary>
    private sealed class StubTool(string name, ToolSideEffect sideEffect) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, name, "{}");

        public ToolSideEffect SideEffect { get; } = sideEffect;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            throw new NotSupportedException("A stub is never invoked.");
    }
}
