using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// Tool switches (<see cref="AgentHost.SetDisabledTools"/>): a disabled tool is neither offered nor callable, the set
/// survives an external replacement, re-applying an unchanged set publishes nothing (the prompt cache stays), and the
/// catalog still lists what is switched off.
/// </summary>
public sealed class ToolSwitchTests
{
    private static string Workspace => Path.GetTempPath();

    [Fact]
    public async Task A_disabled_external_and_built_in_tool_are_not_offered_to_the_model()
    {
        var provider = new FakeProvider((_, _) => Turn.Say("done"));
        var host = AgentTestHost.Create(provider, [FakeTool.Echo("keep"), FakeTool.Echo("drop")]);
        host.SetDisabledTools(["drop", SpawnAgentTool.Name]);
        var root = host.CreateRoot("session", Workspace);

        await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        var offered = provider.Requests[0].Tools.Select(tool => tool.Name).ToArray();
        Assert.Contains("keep", offered);
        Assert.DoesNotContain("drop", offered);
        Assert.DoesNotContain(SpawnAgentTool.Name, offered);
        Assert.False(host.Tools.TryGet("drop", out _));
    }

    [Fact]
    public async Task A_call_to_a_tool_disabled_mid_turn_is_refused_in_the_next_request()
    {
        AgentHost? hostRef = null;
        var dropCalls = 0;
        var switcher = new FakeTool(
            "switcher",
            ToolSideEffect.ReadOnly,
            (_, _, _) =>
            {
                hostRef!.SetDisabledTools(["drop"]);
                return new ValueTask<ToolResult>(new ToolResult("off"));
            });
        var drop = new FakeTool(
            "drop",
            ToolSideEffect.ReadOnly,
            (_, _, _) =>
            {
                Interlocked.Increment(ref dropCalls);
                return new ValueTask<ToolResult>(new ToolResult("ran"));
            });
        var provider = new FakeProvider((_, index) => index switch
        {
            0 => Turn.Call(("c1", "switcher", "{}")),
            1 => Turn.Call(("c2", "drop", "{}")),
            _ => Turn.Say("done"),
        });
        var host = AgentTestHost.Create(provider, [switcher, drop]);
        hostRef = host;
        var root = host.CreateRoot("session", Workspace);

        await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        Assert.DoesNotContain(provider.Requests[1].Tools, tool => tool.Name == "drop");
        Assert.Equal(0, dropCalls);
        var results = root.History.SelectMany(message => message.Content.OfType<ToolResultBlock>()).ToArray();
        Assert.Contains(results, result => result.IsError);
    }

    [Fact]
    public void Re_applying_the_same_set_publishes_nothing_and_a_change_publishes_once()
    {
        var surface = new ToolSurface([]);
        surface.ReplaceExternal([FakeTool.Echo("a"), FakeTool.Echo("b")]);
        var start = surface.Version;

        surface.SetDisabled([]);
        Assert.Equal(start, surface.Version);

        surface.SetDisabled(["a"]);
        var afterDisable = surface.Version;
        Assert.Equal(start + 1, afterDisable);

        surface.SetDisabled(["a"]);
        surface.SetDisabled(["a", "a"]);
        Assert.Equal(afterDisable, surface.Version);

        surface.SetDisabled([]);
        Assert.Equal(afterDisable + 1, surface.Version);
        Assert.True(surface.Tools.TryGet("a", out _));
    }

    [Fact]
    public void The_disabled_set_survives_an_external_replacement_and_covers_tools_registered_later()
    {
        var surface = new ToolSurface([]);
        surface.SetDisabled(["later"]);

        surface.ReplaceExternal([FakeTool.Echo("now"), FakeTool.Echo("later")]);

        Assert.True(surface.Tools.TryGet("now", out _));
        Assert.False(surface.Tools.TryGet("later", out _));
        Assert.Contains(surface.External, tool => tool.Definition.Name == "later");
    }

    [Fact]
    public void All_tools_lists_disabled_built_ins_and_external_tools()
    {
        var host = AgentTestHost.Create(new FakeProvider((_, _) => Turn.Say("x")), [FakeTool.Echo("ext")]);
        host.SetDisabledTools(["ext", SpawnAgentTool.Name]);

        var (builtIns, external) = host.AllTools;

        Assert.Contains(builtIns, tool => tool.Definition.Name == SpawnAgentTool.Name);
        Assert.Contains(external, tool => tool.Definition.Name == "ext");
        Assert.False(host.Tools.TryGet(SpawnAgentTool.Name, out _));
    }

    [Fact]
    public void Disabled_tools_from_the_options_apply_before_the_first_request()
    {
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => new FakeProvider((_, _) => Turn.Say("x")),
            Tools = [FakeTool.Echo("ext")],
            Layers = new PromptLayers("test system", null, null),
            DisabledTools = ["ext"],
        });

        Assert.False(host.Tools.TryGet("ext", out _));
    }

    [Fact]
    public async Task Concurrent_switches_and_replacements_leave_a_consistent_set()
    {
        var surface = new ToolSurface([]);
        var tools = Enumerable.Range(0, 8).Select(i => (ITool)FakeTool.Echo("t" + i)).ToArray();
        surface.ReplaceExternal(tools);

        await Task.WhenAll(
            Task.Run(() =>
            {
                for (var i = 0; i < 500; i++)
                {
                    surface.SetDisabled(i % 2 == 0 ? ["t1"] : ["t2"]);
                }
            }),
            Task.Run(() =>
            {
                for (var i = 0; i < 500; i++)
                {
                    surface.ReplaceExternal(tools);
                }
            }));

        // The last SetDisabled call was the odd one: t2 off, t1 on, whatever the interleaving of replacements.
        Assert.True(surface.Tools.TryGet("t1", out _));
        Assert.False(surface.Tools.TryGet("t2", out _));
        Assert.Equal(7, surface.Tools.Tools.Count);
    }
}
