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

    private IReadOnlyList<ITool> Candidates() => [files.Read, files.List, files.Write];

    private static CodeModeSettings Settings(string tools) =>
        new(8, 40_000, 20_000, CodeModeSettings.ParseTools(tools));
}
