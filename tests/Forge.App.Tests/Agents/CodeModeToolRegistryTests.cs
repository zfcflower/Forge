using Forge.App.Agents;
using Forge.Abstractions;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// The registry a <c>run_code</c> script resolves its inner calls against (wave W14): the candidate snapshot with a
/// live filter. A blank list hides nothing and costs nothing (the snapshot itself is handed out), a non-blank one
/// narrows, a hidden name reads exactly like an unknown one, and a changed list applies at once without rebuilding.
/// </summary>
public sealed class CodeModeToolRegistryTests
{
    private readonly FileTools files = new();

    [Fact]
    public void A_blank_list_returns_the_candidate_snapshot_itself()
    {
        IReadOnlyList<ITool> candidates = [files.Read, files.List, files.Write];
        var registry = new CodeModeToolRegistry(candidates, () => CodeModeSettings.Default);

        Assert.Same(candidates, registry.Tools);
    }

    [Fact]
    public void A_non_blank_list_keeps_only_the_matching_candidates_in_snapshot_order()
    {
        var registry = new CodeModeToolRegistry(Candidates(), () => Settings("read, ls"));

        Assert.Equal(["read", "ls"], registry.Tools.Select(static tool => tool.Definition.Name).ToArray());
    }

    [Fact]
    public void A_hidden_name_resolves_as_unknown_and_a_visible_one_resolves()
    {
        var registry = new CodeModeToolRegistry(Candidates(), () => Settings("read"));

        Assert.False(registry.TryGet("ls", out var hidden));
        Assert.Null(hidden);
        Assert.True(registry.TryGet("read", out var visible));
        Assert.Equal("read", visible.Definition.Name);

        // A name outside the candidates gets the same answer as a hidden one.
        Assert.False(registry.TryGet("bash", out var absent));
        Assert.Null(absent);
    }

    [Fact]
    public void A_changed_list_applies_immediately_without_rebuilding_the_registry()
    {
        var visible = "read";
        var registry = new CodeModeToolRegistry(Candidates(), () => Settings(visible));

        Assert.Equal(["read"], registry.Tools.Select(static tool => tool.Definition.Name).ToArray());

        visible = "ls";

        Assert.Equal(["ls"], registry.Tools.Select(static tool => tool.Definition.Name).ToArray());
        Assert.True(registry.TryGet("ls", out _));
        Assert.False(registry.TryGet("read", out _));
    }

    [Fact]
    public void A_switched_off_tool_is_missing_from_both_the_listing_and_the_resolution()
    {
        IReadOnlyList<string> disabled = ["ls"];
        var registry = new CodeModeToolRegistry(Candidates(), () => CodeModeSettings.Default, () => disabled);

        Assert.Equal(["read", "write"], registry.Tools.Select(static tool => tool.Definition.Name).ToArray());
        Assert.False(registry.TryGet("ls", out _));
        Assert.True(registry.TryGet("read", out _));

        // Read live: switching it back on needs no new registry.
        disabled = [];
        Assert.True(registry.TryGet("ls", out _));
        Assert.Equal(3, registry.Tools.Count);
    }

    [Fact]
    public void Switches_and_the_visible_list_both_apply_and_a_required_tool_cannot_be_switched_off()
    {
        var required = new StubTool("read_artifact");
        IReadOnlyList<ITool> candidates = [files.Read, files.List, required];
        var registry = new CodeModeToolRegistry(
            candidates, () => Settings("read, ls, read_artifact"), () => ["ls", "read_artifact", "gone"]);

        // ls is switched off (the list would admit it); read_artifact's switch is ignored, as for the session's registry.
        Assert.Equal(["read", "read_artifact"], registry.Tools.Select(static tool => tool.Definition.Name).ToArray());
        Assert.False(registry.TryGet("ls", out _));
        Assert.True(registry.TryGet("read_artifact", out _));
    }

    [Fact]
    public void A_name_listed_before_the_list_narrowed_is_refused_at_resolution()
    {
        var visible = "read, ls";
        var registry = new CodeModeToolRegistry(Candidates(), () => Settings(visible));
        var listed = registry.Tools.Select(static tool => tool.Definition.Name).ToArray();
        Assert.Equal(["read", "ls"], listed);

        // The script was started with these names; the user narrows the list while it runs.
        visible = "read";

        Assert.True(registry.TryGet("read", out _));
        Assert.False(registry.TryGet("ls", out _));
    }

    private sealed class StubTool(string name) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, name, "{}");

        public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            throw new NotSupportedException("A stub is never invoked.");
    }

    private IReadOnlyList<ITool> Candidates() => [files.Read, files.List, files.Write];

    private static CodeModeSettings Settings(string tools) =>
        new(8, 40_000, 20_000, CodeModeSettings.ParseTools(tools));
}
