using Forge.Abstractions;
using Forge.App.Approval;
using Forge.Core.Tools;
using Forge.Plugin.Sdk;
using ToolSideEffect = Forge.Abstractions.ToolSideEffect;
using Xunit;

namespace Forge.App.Tests.Approval;

public sealed class ExtensionWithdrawalAndRulesTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public Harness(Func<string, IReadOnlyList<string>>? toolNames = null)
        {
            Rules = new ApprovalRules(Path.Combine(Dir, ApprovalRules.FileName), debounceMilliseconds: 0);
            Service = new ExtensionAuthoringService(
                Authoring, Contributions, toolNames ?? (_ => []), Queue, Rules, new ApprovalSessionDirectory());
        }

        public string Dir { get; } = ApprovalFixtures.TempDir("forge-withdraw-");

        public ApprovalRules Rules { get; }

        public ApprovalQueue Queue { get; } = new(InlineDispatcher.Instance);

        public FakeAuthoringClient Authoring { get; } = new();

        public FakeContributionsClient Contributions { get; } = new();

        public ExtensionAuthoringService Service { get; }

        public async ValueTask DisposeAsync()
        {
            await Rules.DisposeAsync();
            ApprovalFixtures.DeleteTree(Dir);
        }
    }

    private static ToolContext Context(string sessionId = "s1") => new(sessionId, Path.GetTempPath());

    [Fact]
    public async Task Session_release_withdraws_the_card_without_discarding_the_draft()
    {
        await using var harness = new Harness();
        var next = ApprovalFixtures.NextExtensionCardAsync(harness.Queue);
        var submit = harness.Service.SubmitAsync(ApprovalFixtures.PluginDraft("plug"), Context("s1"), CancellationToken.None);
        var card = await next.WaitAsync(ApprovalFixtures.StepTimeout);

        await harness.Queue.WithdrawSessionAsync("s1");
        var result = await submit.WaitAsync(ApprovalFixtures.StepTimeout);

        Assert.Equal(ApprovalCardDecision.Withdrawn, (await card.Decision).Decision);
        Assert.NotEqual(ExtensionSubmitOutcome.Rejected, result.Outcome);
        Assert.NotEqual(ExtensionSubmitOutcome.Approved, result.Outcome);
        Assert.Contains("withdrawn", result.Message);
        Assert.Contains("not a rejection", result.Message);
        Assert.Contains("pending", result.Message);
        Assert.Empty(harness.Authoring.DiscardCalls);
        Assert.Empty(harness.Authoring.CommitCalls);
    }

    [Fact]
    public async Task Shutdown_withdraw_all_keeps_the_draft_and_a_user_reject_still_discards()
    {
        await using var harness = new Harness();
        var next = ApprovalFixtures.NextExtensionCardAsync(harness.Queue);
        var submit = harness.Service.SubmitAsync(ApprovalFixtures.PluginDraft("plug"), Context(), CancellationToken.None);
        await next.WaitAsync(ApprovalFixtures.StepTimeout);
        await harness.Queue.WithdrawAllAsync();
        await submit.WaitAsync(ApprovalFixtures.StepTimeout);
        Assert.Empty(harness.Authoring.DiscardCalls);

        var next2 = ApprovalFixtures.NextExtensionCardAsync(harness.Queue);
        var submit2 = harness.Service.SubmitAsync(ApprovalFixtures.PluginDraft("plug2"), Context(), CancellationToken.None);
        var card = await next2.WaitAsync(ApprovalFixtures.StepTimeout);
        card.RejectCommand.Execute(null);
        var result = await submit2.WaitAsync(ApprovalFixtures.StepTimeout);

        Assert.Equal(ExtensionSubmitOutcome.Rejected, result.Outcome);
        Assert.Equal(["plug2"], harness.Authoring.DiscardCalls);
    }

    [Fact]
    public async Task Disposing_the_extension_card_is_a_withdrawal_not_a_denial()
    {
        var card = new ExtensionApprovalCardViewModel(
            ApprovalFixtures.Staged("plug"), "s1", "S", fromPreviousRun: false, InlineDispatcher.Instance);

        card.Dispose();

        var outcome = await card.Decision;
        Assert.Equal(ApprovalCardDecision.Withdrawn, outcome.Decision);
        Assert.True(outcome.IsUndecided);
    }

    [Fact]
    public async Task A_restored_card_withdrawn_at_shutdown_leaves_the_draft_staged()
    {
        await using var harness = new Harness();
        Assert.True(await harness.Service.EnqueueRestoredCardAsync(ApprovalFixtures.Staged("plug"), CancellationToken.None));

        await harness.Queue.WithdrawAllAsync();
        await Task.Delay(150);

        Assert.Empty(harness.Authoring.DiscardCalls);
        Assert.Empty(harness.Authoring.CommitCalls);
    }

    [Fact]
    public async Task Remembered_rules_are_cleared_before_the_commit_runs_and_stay_cleared_when_it_fails()
    {
        await using var harness = new Harness(_ => ["old_tool"]);
        harness.Rules.Allow(ApprovalRuleKey.Global("old_tool", ToolSideEffect.Execute));
        harness.Rules.Allow(ApprovalRuleKey.Global("new_tool", ToolSideEffect.Execute));
        bool? presentAtCommit = null;
        harness.Authoring.StageHandler = request => ApprovalFixtures.Staged(request.PluginId) with
        {
            Kind = ExtensionKinds.Workflow,
            Tool = new ExtensionToolSpec("new_tool", "d", "{}"),
        };
        harness.Authoring.CommitHandler = _ =>
        {
            presentAtCommit =
                harness.Rules.IsAllowed("old_tool", ToolSideEffect.Execute, "s1", null) ||
                harness.Rules.IsAllowed("new_tool", ToolSideEffect.Execute, "s1", null);
            throw new InvalidOperationException("commit blew up");
        };
        var next = ApprovalFixtures.NextExtensionCardAsync(harness.Queue);
        var submit = harness.Service.SubmitAsync(ApprovalFixtures.PluginDraft("plug"), Context(), CancellationToken.None);
        var card = await next.WaitAsync(ApprovalFixtures.StepTimeout);

        card.ApproveCommand.Execute(null);
        var result = await submit.WaitAsync(ApprovalFixtures.StepTimeout);

        Assert.Equal(ExtensionSubmitOutcome.Invalid, result.Outcome);
        Assert.Equal(false, presentAtCommit);
        Assert.False(harness.Rules.IsAllowed("old_tool", ToolSideEffect.Execute, "s1", null));
        Assert.False(harness.Rules.IsAllowed("new_tool", ToolSideEffect.Execute, "s1", null));
    }

    [Fact]
    public async Task Rules_for_the_actually_registered_names_are_cleared_after_the_commit_too()
    {
        await using var harness = new Harness();
        harness.Authoring.CommitHandler = request =>
        {
            // A rule for a name only known once the commit returned (granted while it ran).
            harness.Rules.Allow(ApprovalRuleKey.Global("registered_tool", ToolSideEffect.Execute));
            return new ExtensionCommitResult(
                new PluginStatusInfo(request.PluginId, "1.0.0", "Approved", "s", "d", "sdk", []), ["registered_tool"]);
        };
        var next = ApprovalFixtures.NextExtensionCardAsync(harness.Queue);
        var submit = harness.Service.SubmitAsync(ApprovalFixtures.PluginDraft("plug"), Context(), CancellationToken.None);
        var card = await next.WaitAsync(ApprovalFixtures.StepTimeout);

        card.ApproveCommand.Execute(null);
        var result = await submit.WaitAsync(ApprovalFixtures.StepTimeout);

        Assert.Equal(ExtensionSubmitOutcome.Approved, result.Outcome);
        Assert.False(harness.Rules.IsAllowed("registered_tool", ToolSideEffect.Execute, "s1", null));
    }

    [Theory]
    [InlineData("{\"version\":1,\"rules\":null}")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":1,\"rules\":[null]}")]
    public async Task A_rules_file_with_null_rules_does_not_wedge_approval(string json)
    {
        await using var harness = new Harness();
        await File.WriteAllTextAsync(Path.Combine(harness.Dir, ApprovalRules.FileName), json);
        var handler = new SessionApprovalHandler(harness.Queue, harness.Rules, ApprovalFixtures.Session("s1"));

        var next = ApprovalFixtures.NextToolCardAsync(harness.Queue);
        var pending = handler.RequestAsync(ApprovalFixtures.ToolRequest(), CancellationToken.None).AsTask();
        var card = await next.WaitAsync(ApprovalFixtures.StepTimeout);
        card.AllowCommand.Execute(null);

        Assert.Equal(ApprovalDecision.Allow, await pending.WaitAsync(ApprovalFixtures.StepTimeout));
        await harness.Rules.LoadAsync(CancellationToken.None);
        Assert.True(harness.Rules.IsLoaded);
    }

    [Fact]
    public async Task Rules_null_is_reported_as_a_load_problem()
    {
        await using var harness = new Harness();
        await File.WriteAllTextAsync(Path.Combine(harness.Dir, ApprovalRules.FileName), "{\"version\":1,\"rules\":null}");

        await harness.Rules.LoadAsync(CancellationToken.None);

        Assert.NotNull(harness.Rules.LoadProblem);
    }

    [Fact]
    public void The_workflow_tool_is_shown_on_the_card()
    {
        var staged = ApprovalFixtures.Staged("flow") with
        {
            Kind = ExtensionKinds.Workflow,
            Tool = new ExtensionToolSpec("do_flow", "Does the flow", "{\"type\":\"object\"}"),
        };
        var card = new ExtensionApprovalCardViewModel(staged, "s1", "S", fromPreviousRun: false, InlineDispatcher.Instance);

        Assert.True(card.HasTool);
        Assert.Equal("do_flow", card.ToolName);
        Assert.Equal("Does the flow", card.ToolDescription);
        Assert.Equal("{\"type\":\"object\"}", card.ToolSchema);
        Assert.False(new ExtensionApprovalCardViewModel(
            ApprovalFixtures.Staged("p"), "s1", "S", fromPreviousRun: false, InlineDispatcher.Instance).HasTool);
    }
}
