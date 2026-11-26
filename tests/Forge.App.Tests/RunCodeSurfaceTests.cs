using Forge.Abstractions;
using Forge.HostClient;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// What one script run may call. The rule is one intersection: the tools the calling agent holds (its allow-list after the
/// user's switches) AND the session's script surface. The session surface can be wider than the agent — plugin write tools
/// belong to the root, the Reviewer has no web tools by design — so the held list is what narrows it; and a caller with no
/// held list gets nothing, because a second rule (once "read-only tools only") handed the Reviewer a web fetch its own
/// allow-list withheld.
/// </summary>
public sealed class RunCodeSurfaceTests
{
    private static List<RunCodeToolEntry> Surface() =>
    [
        new("grep", "search", "{}", ToolSideEffect.ReadOnly),
        new("read", "read", "{}", ToolSideEffect.ReadOnly),
        new("web_fetch", "fetch", "{}", ToolSideEffect.ReadOnly),
        new("plugin_write", "writes", "{}", ToolSideEffect.Write),
    ];

    /// <summary>The caller's held tools: the names that answer, and nothing else about them.</summary>
    private sealed class Held(params string[] names) : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools => [];

        public bool TryGet(string name, out ITool tool)
        {
            tool = null!;
            return names.Contains(name, StringComparer.Ordinal);
        }
    }

    private static ToolContext Context(IToolRegistry? held) => new("session", "workspace") { Tools = held };

    [Fact]
    public void A_script_calls_what_the_calling_agent_holds()
    {
        var declarations = RunCodeToolSurface.Declarations(
            Surface(),
            Context(new Held("read", "plugin_write")));

        Assert.Equal(["read", "plugin_write"], declarations.Select(entry => entry.Name));
    }

    [Fact]
    public void A_sub_agent_does_not_get_the_sessions_plugin_or_web_tools_its_list_withholds()
    {
        var declarations = RunCodeToolSurface.Declarations(
            Surface(),
            Context(new Held("grep", "read")));

        Assert.Equal(["grep", "read"], declarations.Select(entry => entry.Name));
    }

    [Fact]
    public void A_caller_without_a_held_list_gets_nothing()
    {
        // Fail closed: a plugin bridge or a workflow step has no allow-list to intersect with, and guessing "read-only"
        // here is how the Reviewer would have gained a web fetch inside a script.
        var declarations = RunCodeToolSurface.Declarations(Surface(), Context(null));

        Assert.Empty(declarations);
    }
}
