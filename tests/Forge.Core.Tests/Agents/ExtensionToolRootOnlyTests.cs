using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Tests.Tools;
using Forge.Core.Tools;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>The five extension tools belong to the root agent: sub-agents never see or run them, whatever their tool list says.</summary>
public class ExtensionToolRootOnlyTests
{
    private static readonly string[] ExtensionNames =
    [
        ExtensionToolNames.List,
        ExtensionToolNames.Create,
        ExtensionToolNames.Update,
        ExtensionToolNames.Disable,
        ExtensionToolNames.Delete,
    ];

    private static string Workspace => Path.GetTempPath();

    private static ToolRegistry Registry(FakeExtensionAuthoring authoring) =>
        new([FakeTool.Echo("read"), .. ExtensionTools.Create(authoring)]);

    private static string[] Names(ToolRegistry registry) =>
        [.. registry.Definitions.Select(definition => definition.Name)];

    [Fact]
    public void Root_with_null_tool_list_sees_all_five()
    {
        var selected = Registry(new FakeExtensionAuthoring()).Select(null, canSpawn: true, [], isRoot: true);

        foreach (var name in ExtensionNames)
        {
            Assert.Contains(name, Names(selected));
            Assert.True(selected.TryGet(name, out _));
        }
    }

    [Fact]
    public void Root_with_explicit_list_sees_the_named_ones()
    {
        var selected = Registry(new FakeExtensionAuthoring())
            .Select(["read", ExtensionToolNames.List], canSpawn: true, [], isRoot: true);

        Assert.Equal(new[] { ExtensionToolNames.List, "read" }, Names(selected));
    }

    [Fact]
    public void SubAgent_with_null_tool_list_loses_the_extension_tools_only()
    {
        var selected = Registry(new FakeExtensionAuthoring()).Select(null, canSpawn: true, [], isRoot: false);

        Assert.Equal(new[] { "read" }, Names(selected));
        foreach (var name in ExtensionNames)
        {
            Assert.False(selected.TryGet(name, out _));
        }
    }

    [Fact]
    public void SubAgent_with_explicit_list_naming_them_still_loses_them()
    {
        var selected = Registry(new FakeExtensionAuthoring())
            .Select(["read", .. ExtensionNames], canSpawn: false, [], isRoot: false);

        Assert.Equal(new[] { "read" }, Names(selected));
        Assert.False(selected.TryGet(ExtensionToolNames.Disable, out _));
    }

    [Fact]
    public void Select_defaults_to_root()
    {
        var selected = Registry(new FakeExtensionAuthoring()).Select(null, canSpawn: true, []);

        Assert.Equal(ExtensionNames.Length + 1, selected.Tools.Count);
    }

    [Fact]
    public async Task SubAgent_without_tool_list_neither_sees_nor_can_call_extension_disable()
    {
        var authoring = new FakeExtensionAuthoring();
        var child = new AgentDefinition("Open", "no tool list", "open role", new ModelSettings("open-model"));
        var provider = new FakeProvider((request, index) =>
        {
            if (request.Messages.Count == 1 && Turn.LastUserText(request) == "inspect")
            {
                return request.Messages.Any(message => message.Role == MessageRole.Tool)
                    ? Turn.Say("child done")
                    : Turn.Call(("c1", ExtensionToolNames.Disable, """{"id":"x","version":"1.0.0"}"""));
            }

            if (request.Messages.Any(message => message.Role == MessageRole.Tool) && Turn.LastUserText(request) != "inspect"
                && request.Messages.Count > 3)
            {
                return Turn.Say("parent done");
            }

            return index == 0
                ? Turn.Call(("call-1", "spawn_agent", """{"kind":"Open","task":"inspect"}"""))
                : Turn.Say(request.Messages.Count == 1 ? "child done" : "parent done");
        });
        var host = AgentTestHost.Create(
            provider,
            [FakeTool.Echo("read"), .. ExtensionTools.Create(authoring)],
            options: new AgentOptions { MaxSubAgentDepth = 3, MaxTurns = 10 },
            agents: [child]);
        var root = host.CreateRoot("session", Workspace);

        await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        Assert.Equal(0, authoring.DisableCalls);
        var childRequests = provider.Requests.Where(request => Turn.LastUserText(request) == "inspect").ToArray();
        Assert.NotEmpty(childRequests);
        foreach (var request in childRequests)
        {
            foreach (var name in ExtensionNames)
            {
                Assert.False(Turn.HasTool(request, name));
            }
        }

        var rootRequest = provider.Requests.First(request => Turn.LastUserText(request) == "go");
        foreach (var name in ExtensionNames)
        {
            Assert.True(Turn.HasTool(rootRequest, name));
        }

        var childInstance = Assert.Single(host.SubAgents.Instances);
        var result = Assert.Single(childInstance.History.SelectMany(message => message.Content.OfType<ToolResultBlock>()));
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Tools_refuse_at_depth_above_zero_without_calling_authoring()
    {
        var authoring = new FakeExtensionAuthoring();
        var context = new ToolContext("session", Workspace, AgentDepth: 1);
        const string IdVersion = """{"id":"x","version":"1.0.0"}""";

        foreach (var tool in ExtensionTools.Create(authoring))
        {
            var result = await tool.InvokeAsync(new ToolCall("c", tool.Definition.Name, IdVersion), context, CancellationToken.None);

            Assert.True(result.IsError, tool.Definition.Name);
            Assert.Contains("root agent", result.Content, StringComparison.Ordinal);
        }

        Assert.Equal(0, authoring.ListCalls);
        Assert.Equal(0, authoring.SubmitCalls);
        Assert.Equal(0, authoring.DisableCalls);
        Assert.Equal(0, authoring.DeleteCalls);
    }

    [Fact]
    public async Task Tools_still_work_at_depth_zero()
    {
        var authoring = new FakeExtensionAuthoring();
        var context = new ToolContext("session", Workspace, AgentDepth: 0);
        var disable = ExtensionTools.Create(authoring).Single(tool => tool.Definition.Name == ExtensionToolNames.Disable);

        var result = await disable.InvokeAsync(
            new ToolCall("c", ExtensionToolNames.Disable, """{"id":"x","version":"1.0.0"}"""),
            context,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(1, authoring.DisableCalls);
    }
}
