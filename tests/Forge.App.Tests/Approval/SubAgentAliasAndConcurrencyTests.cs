using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Approval;
using Forge.Core.Agents;
using Forge.Core.Tools;
using Forge.HostClient;
using Xunit;

namespace Forge.App.Tests.Approval;

/// <summary>
/// Sub-agent session aliases in the router, plugin command approvals that must not borrow a tool's rules, and several
/// concurrent cards of one session.
/// </summary>
public sealed class SubAgentAliasAndConcurrencyTests
{
    private sealed class FakeTool(string name) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, "d", "{}");

        public ToolSideEffect SideEffect => ToolSideEffect.Execute;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            ValueTask.FromResult(new ToolResult("ok"));
    }

    private sealed class FakeScope(IApprovalPolicy policy, IApprovalHandler? handler, params ITool[] tools) : ISessionToolScope
    {
        public IApprovalPolicy Policy { get; } = policy;

        public IApprovalHandler? ApprovalHandler { get; } = handler;

        public ITool? FindTool(string toolName) => tools.FirstOrDefault(t => t.Definition.Name == toolName);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness() =>
            Rules = new ApprovalRules(Path.Combine(Dir, ApprovalRules.FileName), debounceMilliseconds: 0);

        public string Dir { get; } = ApprovalFixtures.TempDir("forge-alias-");

        public ApprovalRules Rules { get; }

        public ApprovalQueue Queue { get; } = new(InlineDispatcher.Instance);

        public SessionToolCallRouter Router { get; } = new();

        public async ValueTask DisposeAsync()
        {
            await Rules.DisposeAsync();
            ApprovalFixtures.DeleteTree(Dir);
        }
    }

    private static SessionRuntime Runtime(SessionToolCallRouter router, string id) =>
        new(new AgentRuntimeServices(null!, null, null!, null!, null!, null!) { ToolRouter = router }, ApprovalFixtures.Session(id));

    private static AgentSubAgentStarted Started(string parentAgent, string childAgent, string childSession) =>
        new(parentAgent, childAgent, "Explore", "task", "m", childSession, DateTimeOffset.UtcNow);

    private static AgentSubAgentFinished Finished(string childAgent) =>
        new(childAgent, SubAgentStatus.Completed, new TokenUsage(0, 0), TimeSpan.Zero, null, DateTimeOffset.UtcNow);

    // A session is registered when the router reaches its scope. The runtime has no host here, so "reaches the scope" is
    // observed through the policy: the runtime's policy allows the todo tool without asking, an unknown id is denied.
    private static bool IsRegistered(SessionToolCallRouter router, string sessionId)
    {
        var probe = new ApprovalRequest(TodoTool.Name, "{}", ToolSideEffect.Write, Path.GetTempPath());
        return router.ApproveAsync(sessionId, probe, CancellationToken.None).AsTask().Result == ApprovalDecision.Allow;
    }

    [Fact]
    public void Child_session_is_registered_from_start_to_finish_and_unrelated_ids_stay_refused()
    {
        var router = new SessionToolCallRouter();
        using var runtime = Runtime(router, "root-s");
        router.Register("root-s", runtime);

        Assert.False(IsRegistered(router, "child-s"));
        runtime.TrackSubAgent(Started("root", "child-1", "child-s"));
        Assert.True(IsRegistered(router, "child-s"));
        Assert.False(IsRegistered(router, "unrelated-s"));

        runtime.TrackSubAgent(Finished("child-1"));
        Assert.False(IsRegistered(router, "child-s"));
        Assert.True(IsRegistered(router, "root-s"));
    }

    [Fact]
    public void Aliases_of_nested_children_are_removed_on_finish_and_on_dispose_and_not_added_after_dispose()
    {
        var router = new SessionToolCallRouter();
        var runtime = Runtime(router, "root-s");
        router.Register("root-s", runtime);

        runtime.TrackSubAgent(Started("root", "c1", "s-c1"));
        runtime.TrackSubAgent(Started("c1", "c2", "s-c2"));
        runtime.TrackSubAgent(Finished("c1"));

        Assert.False(IsRegistered(router, "s-c1"));
        Assert.True(IsRegistered(router, "s-c2"));

        runtime.Dispose();
        Assert.False(IsRegistered(router, "s-c2"));
        Assert.False(IsRegistered(router, "root-s"));

        runtime.TrackSubAgent(Started("root", "c3", "s-c3"));
        Assert.False(IsRegistered(router, "s-c3"));
    }

    [Fact]
    public async Task A_child_alias_resolves_and_approves_through_the_root_scope_and_the_card_names_the_root_session()
    {
        await using var harness = new Harness();
        var handler = new SessionApprovalHandler(harness.Queue, harness.Rules, ApprovalFixtures.Session("root-s"));
        var scope = new FakeScope(new DefaultApprovalPolicy(), handler, new FakeTool("shell_run"));
        harness.Router.Register("root-s", scope);
        harness.Router.Register("child-s", scope);
        var next = ApprovalFixtures.NextToolCardAsync(harness.Queue);

        Assert.NotNull(harness.Router.Resolve("child-s", "shell_run"));
        Assert.Null(harness.Router.Resolve("child-s", ExtensionToolNames.Create));
        var pending = harness.Router.ApproveAsync("child-s", ApprovalFixtures.ToolRequest(), CancellationToken.None).AsTask();
        var card = await next.WaitAsync(ApprovalFixtures.StepTimeout);

        Assert.Equal("root-s", card.SessionId);
        card.AllowCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Allow, await pending.WaitAsync(ApprovalFixtures.StepTimeout));

        harness.Router.Unregister("child-s", scope);
        Assert.Null(harness.Router.Resolve("child-s", "shell_run"));
        Assert.Equal(
            ApprovalDecision.Deny,
            await harness.Router.ApproveAsync("child-s", ApprovalFixtures.ToolRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task A_plugin_command_named_todo_still_gets_a_card_despite_a_policy_allow_and_a_remembered_rule()
    {
        await using var harness = new Harness();
        var handler = new SessionApprovalHandler(harness.Queue, harness.Rules, ApprovalFixtures.Session("s1"));
        var policy = new DefaultApprovalPolicy
        {
            ToolRules = new Dictionary<string, ApprovalMode> { ["todo"] = ApprovalMode.Allow },
        };
        harness.Router.Register("s1", new FakeScope(policy, handler));
        Assert.True(harness.Rules.Allow(ApprovalRules.KeyFor("todo", ToolSideEffect.Execute, ApprovalScope.Global, "s1", "p1")));
        var next = ApprovalFixtures.NextToolCardAsync(harness.Queue);

        var request = new ApprovalRequest(
            ExtensionHostClient.PluginCommandApprovalName("alpha", "todo"), "{}", ToolSideEffect.Execute, Path.GetTempPath());
        var pending = harness.Router.ApproveAsync("s1", request, CancellationToken.None).AsTask();
        var card = await next.WaitAsync(ApprovalFixtures.StepTimeout);

        Assert.True(card.IsPluginCommand);
        Assert.False(card.CanRemember);
        Assert.False(card.IsDelete);
        card.AllowCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Allow, await pending.WaitAsync(ApprovalFixtures.StepTimeout));
    }

    [Fact]
    public async Task A_plugin_command_decision_is_never_remembered()
    {
        await using var harness = new Harness();
        var name = ExtensionHostClient.PluginCommandApprovalName("alpha", "todo");

        Assert.False(ApprovalRules.IsRememberable(name));
        Assert.False(harness.Rules.Allow(ApprovalRules.KeyFor(name, ToolSideEffect.Execute, ApprovalScope.Global, "s1", "p1")));
        Assert.False(harness.Rules.IsAllowed(name, ToolSideEffect.Execute, "s1", "p1"));
    }

    [Fact]
    public async Task Two_concurrent_cards_of_one_session_are_decided_and_cancelled_independently()
    {
        await using var harness = new Harness();
        using var handler = new SessionApprovalHandler(harness.Queue, harness.Rules, ApprovalFixtures.Session("s1"));
        var cards = new List<ToolApprovalCardViewModel>();
        var seen = new SemaphoreSlim(0);
        harness.Queue.CardAdded += card =>
        {
            if (card is ToolApprovalCardViewModel tool)
            {
                lock (cards)
                {
                    cards.Add(tool);
                }

                seen.Release();
            }
        };

        ToolApprovalCardViewModel Find(string tool)
        {
            lock (cards)
            {
                return cards.Single(c => c.ToolName == tool);
            }
        }

        using var cancelSecond = new CancellationTokenSource();
        var first = handler.RequestAsync(ApprovalFixtures.ToolRequest("first_tool"), CancellationToken.None).AsTask();
        var second = handler.RequestAsync(ApprovalFixtures.ToolRequest("second_tool"), cancelSecond.Token).AsTask();
        var third = handler.RequestAsync(ApprovalFixtures.ToolRequest("third_tool"), CancellationToken.None).AsTask();
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await seen.WaitAsync(ApprovalFixtures.StepTimeout));
        }

        Find("third_tool").DenyCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Deny, await third.WaitAsync(ApprovalFixtures.StepTimeout));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        await cancelSecond.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(ApprovalFixtures.StepTimeout));
        await ApprovalFixtures.WaitUntilAsync(() => harness.Queue.Items.Count == 1, "only the first card is left");
        Assert.False(first.IsCompleted);
        Assert.Same(Find("first_tool"), harness.Queue.Items.Single());

        Find("first_tool").AllowCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Allow, await first.WaitAsync(ApprovalFixtures.StepTimeout));
    }
}
