using Forge.Abstractions;
using Forge.App.Agents;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Forge.Core.Storage;
using Forge.Core.Tools;
using Forge.Core.Tools.Web;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// What a session's role gives it: a root runs its own runtime (a Depth 0 agent, the whole tool surface, the extension tools
/// included) while an agent session runs one Depth 1 instance of its root's host. Both halves are pinned here because the
/// role lives in the store (<c>sessions.parent_session_id</c>) and a tab that keeps the other role's runtime or tool surface
/// is the whole class of defect this covers.
/// </summary>
public sealed class SessionRoleRuntimeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-role-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle can outlive the test; the temp directory is the OS's problem then.
        }
    }

    private sealed class ProbeTool(string name) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, "probe", "{}");

        public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            ValueTask.FromResult(new ToolResult("ok"));
    }

    private static string[] Names(ToolRegistry registry) =>
        [.. registry.Definitions.Select(static definition => definition.Name)];

    [Fact]
    public async Task An_agent_session_never_owns_a_runtime()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        using var runtimes = new AgentRuntimeManager(
            new AgentRuntimeServices(
                ProviderFactory: _ => throw new InvalidOperationException("no provider is built in this test"),
                HostClient: null,
                Store: store,
                Catalog: store,
                Todos: new SqliteTodoStore(directory),
                Model: new ModelSettings("test-model")));

        var agent = new SessionInfo("child", directory, "child", DateTimeOffset.UnixEpoch)
        {
            ParentSessionId = "root",
            ChildDriver = "user",
        };

        // A runtime of its own would give the child a Depth 0 agent: its own turn gate, its own approval handler and the
        // root's tool surface - exactly what a demoted root must not keep.
        Assert.Throws<InvalidOperationException>(() => runtimes.Get(agent));

        var root = new SessionInfo("root", directory, "root", DateTimeOffset.UnixEpoch);
        using var runtime = runtimes.Get(root);
        Assert.Equal(root.Id, runtime.SessionId);
    }

    [Fact]
    public void The_extension_tools_reach_the_root_agent_and_no_agent_session()
    {
        var tools = new List<ITool>
        {
            new ProbeTool(ExtensionToolNames.List),
            new ProbeTool(ExtensionToolNames.Create),
            new ProbeTool(ExtensionToolNames.Update),
            new ProbeTool(ExtensionToolNames.Disable),
            new ProbeTool(ExtensionToolNames.Delete),
            new ProbeTool("read"),
            new ProbeTool("todo"),
        };
        var registry = new ToolRegistry(tools);

        var root = registry.Select(toolNames: null, canSpawn: true, subAgentTools: [], isRoot: true, canMessage: true);
        foreach (var name in ExtensionToolNames.AllowedWithoutAsking.Concat([ExtensionToolNames.Delete]))
        {
            Assert.Contains(name, Names(root));
        }

        Assert.Contains("todo", Names(root));

        // An agent session (the user-driven child) keeps what its kind may call; all five authoring tools are the root's alone.
        var agent = registry.Select(toolNames: null, canSpawn: true, subAgentTools: [], isRoot: false, canMessage: true);
        foreach (var name in ExtensionToolNames.AllowedWithoutAsking.Concat([ExtensionToolNames.Delete]))
        {
            Assert.DoesNotContain(name, Names(agent));
        }

        Assert.Contains("read", Names(agent));
        Assert.Contains("todo", Names(agent));
    }

    [Fact]
    public void The_built_in_kinds_that_work_against_the_web_hold_its_tools()
    {
        var registry = new ToolRegistry(
        [
            new ProbeTool("read"),
            new ProbeTool("bash"),
            new ProbeTool("git_diff"),
            new ProbeTool(WebSearchTool.Name),
            new ProbeTool(WebFetchTool.Name),
        ]);

        foreach (var kind in new[] { BuiltInAgents.Scout, BuiltInAgents.Worker, BuiltInAgents.Plan })
        {
            var selected = registry.Select(kind.ToolNames, canSpawn: true, subAgentTools: [], isRoot: false, canMessage: false);
            Assert.Contains(WebSearchTool.Name, Names(selected));
            Assert.Contains(WebFetchTool.Name, Names(selected));
        }

        // The reviewer judges a change that lives in the repository; fetched web content is untrusted input and must never
        // reach the verdict.
        var reviewer = registry.Select(BuiltInAgents.Reviewer.ToolNames, canSpawn: true, subAgentTools: [], isRoot: false, canMessage: false);
        Assert.DoesNotContain(WebSearchTool.Name, Names(reviewer));
        Assert.DoesNotContain(WebFetchTool.Name, Names(reviewer));
    }
}
