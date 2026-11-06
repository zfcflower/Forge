using Forge.Abstractions;
using Forge.App.Approval;
using Forge.Core.Agents;
using Forge.Core.Tools;
using ToolSideEffect = Forge.Abstractions.ToolSideEffect;
using Xunit;

namespace Forge.App.Tests.Approval;

public sealed class SessionToolCallRouterTests
{
    private sealed class FakeTool(string name, ToolSideEffect sideEffect = ToolSideEffect.Execute) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, "d", "{}");

        public ToolSideEffect SideEffect { get; } = sideEffect;

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

        public string Dir { get; } = ApprovalFixtures.TempDir("forge-router-");

        public ApprovalRules Rules { get; }

        public ApprovalQueue Queue { get; } = new(InlineDispatcher.Instance);

        public SessionToolCallRouter Router { get; } = new();

        public SessionApprovalHandler Handler(string sessionId) => new(Queue, Rules, ApprovalFixtures.Session(sessionId));

        public async ValueTask DisposeAsync()
        {
            await Rules.DisposeAsync();
            ApprovalFixtures.DeleteTree(Dir);
        }
    }

    private static DefaultApprovalPolicy AskPolicy() => new();

    private static DefaultApprovalPolicy PolicyWith(string tool, ApprovalMode mode) =>
        new() { ToolRules = new Dictionary<string, ApprovalMode> { [tool] = mode } };

    private static async Task<ApprovalDecision> WaitAsync(ValueTask<ApprovalDecision> task) =>
        await task.AsTask().WaitAsync(ApprovalFixtures.StepTimeout);

    [Fact]
    public async Task Ask_lands_a_card_in_the_calling_session_and_only_that_one()
    {
        await using var harness = new Harness();
        harness.Router.Register("A", new FakeScope(AskPolicy(), harness.Handler("A"), new FakeTool("shell_run")));
        harness.Router.Register("B", new FakeScope(AskPolicy(), harness.Handler("B"), new FakeTool("shell_run")));
        var next = ApprovalFixtures.NextToolCardAsync(harness.Queue);

        var pending = harness.Router.ApproveAsync("B", ApprovalFixtures.ToolRequest(), CancellationToken.None).AsTask();
        var card = await next.WaitAsync(ApprovalFixtures.StepTimeout);

        Assert.Equal("B", card.SessionId);
        card.AllowCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Allow, await pending.WaitAsync(ApprovalFixtures.StepTimeout));
    }

    [Fact]
    public async Task A_rule_remembered_for_session_A_does_not_apply_to_session_B()
    {
        await using var harness = new Harness();
        harness.Router.Register("A", new FakeScope(AskPolicy(), harness.Handler("A"), new FakeTool("shell_run")));
        harness.Router.Register("B", new FakeScope(AskPolicy(), harness.Handler("B"), new FakeTool("shell_run")));
        var request = ApprovalFixtures.ToolRequest();
        harness.Rules.Allow(ApprovalRules.KeyFor(request.ToolName, request.SideEffect, ApprovalScope.Session, "A", null));

        Assert.Equal(ApprovalDecision.Allow, await WaitAsync(harness.Router.ApproveAsync("A", request, CancellationToken.None)));
        Assert.Equal(0, await harness.Queue.CountAsync());

        var next = ApprovalFixtures.NextToolCardAsync(harness.Queue);
        var pending = harness.Router.ApproveAsync("B", request, CancellationToken.None).AsTask();
        var card = await next.WaitAsync(ApprovalFixtures.StepTimeout);
        Assert.Equal("B", card.SessionId);
        card.DenyCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Deny, await pending.WaitAsync(ApprovalFixtures.StepTimeout));
    }

    [Fact]
    public async Task Resolve_uses_the_tool_set_of_that_session()
    {
        await using var harness = new Harness();
        harness.Router.Register("A", new FakeScope(AskPolicy(), null, new FakeTool("only_in_a")));
        harness.Router.Register("B", new FakeScope(AskPolicy(), null, new FakeTool("only_in_b")));

        Assert.NotNull(harness.Router.Resolve("A", "only_in_a"));
        Assert.Null(harness.Router.Resolve("A", "only_in_b"));
        Assert.NotNull(harness.Router.Resolve("B", "only_in_b"));
    }

    [Fact]
    public async Task Unknown_or_unregistered_session_fails_closed()
    {
        await using var harness = new Harness();
        var scope = new FakeScope(PolicyWith("shell_run", ApprovalMode.Allow), null, new FakeTool("shell_run"));
        harness.Router.Register("A", scope);

        Assert.Null(harness.Router.Resolve("nope", "shell_run"));
        Assert.Equal(ApprovalDecision.Deny, await WaitAsync(harness.Router.ApproveAsync("nope", ApprovalFixtures.ToolRequest(), CancellationToken.None)));
        Assert.Equal(ApprovalDecision.Deny, await WaitAsync(harness.Router.ApproveAsync(string.Empty, ApprovalFixtures.ToolRequest(), CancellationToken.None)));

        Assert.Equal(ApprovalDecision.Allow, await WaitAsync(harness.Router.ApproveAsync("A", ApprovalFixtures.ToolRequest(), CancellationToken.None)));
        harness.Router.Unregister("A", scope);
        Assert.Null(harness.Router.Resolve("A", "shell_run"));
        Assert.Equal(ApprovalDecision.Deny, await WaitAsync(harness.Router.ApproveAsync("A", ApprovalFixtures.ToolRequest(), CancellationToken.None)));
    }

    [Fact]
    public async Task A_stale_unregister_does_not_remove_a_newer_scope()
    {
        await using var harness = new Harness();
        var old = new FakeScope(AskPolicy(), null, new FakeTool("t"));
        var current = new FakeScope(AskPolicy(), null, new FakeTool("t"));
        harness.Router.Register("A", old);
        harness.Router.Register("A", current);

        harness.Router.Unregister("A", old);

        Assert.NotNull(harness.Router.Resolve("A", "t"));
    }

    [Theory]
    [InlineData(ExtensionToolNames.List)]
    [InlineData(ExtensionToolNames.Create)]
    [InlineData(ExtensionToolNames.Update)]
    [InlineData(ExtensionToolNames.Disable)]
    [InlineData(ExtensionToolNames.Delete)]
    public async Task Extension_authoring_tools_are_never_reachable_even_when_the_policy_allows_them(string name)
    {
        await using var harness = new Harness();
        harness.Router.Register("A", new FakeScope(PolicyWith(name, ApprovalMode.Allow), null, new FakeTool(name)));

        Assert.Null(harness.Router.Resolve("A", name));
        Assert.Equal(
            ApprovalDecision.Deny,
            await WaitAsync(harness.Router.ApproveAsync("A", ApprovalFixtures.ToolRequest(name), CancellationToken.None)));
    }

    [Fact]
    public async Task Deny_policy_is_refused_without_a_card_and_missing_handler_denies_an_ask()
    {
        await using var harness = new Harness();
        harness.Router.Register("A", new FakeScope(PolicyWith("shell_run", ApprovalMode.Deny), harness.Handler("A"), new FakeTool("shell_run")));
        harness.Router.Register("B", new FakeScope(AskPolicy(), null, new FakeTool("shell_run")));

        Assert.Equal(ApprovalDecision.Deny, await WaitAsync(harness.Router.ApproveAsync("A", ApprovalFixtures.ToolRequest(), CancellationToken.None)));
        Assert.Equal(ApprovalDecision.Deny, await WaitAsync(harness.Router.ApproveAsync("B", ApprovalFixtures.ToolRequest(), CancellationToken.None)));
        Assert.Equal(0, await harness.Queue.CountAsync());
    }

    [Fact]
    public async Task Releasing_the_session_handler_while_asking_denies_the_waiting_call()
    {
        await using var harness = new Harness();
        var handler = harness.Handler("A");
        harness.Router.Register("A", new FakeScope(AskPolicy(), handler, new FakeTool("shell_run")));
        var next = ApprovalFixtures.NextToolCardAsync(harness.Queue);
        var pending = harness.Router.ApproveAsync("A", ApprovalFixtures.ToolRequest(), CancellationToken.None).AsTask();
        await next.WaitAsync(ApprovalFixtures.StepTimeout);

        handler.Dispose();

        Assert.Equal(ApprovalDecision.Deny, await pending.WaitAsync(ApprovalFixtures.StepTimeout));
    }
}
