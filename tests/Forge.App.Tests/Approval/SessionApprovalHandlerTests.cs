using Forge.Abstractions;
using Forge.App.Approval;
using Forge.App.Localization;
using Forge.Core.Tools;
using Xunit;

namespace Forge.App.Tests.Approval;

public sealed class SessionApprovalHandlerTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private Harness(string dir, ApprovalRules rules, ApprovalQueue queue)
        {
            Dir = dir;
            Rules = rules;
            Queue = queue;
        }

        public string Dir { get; }

        public ApprovalRules Rules { get; }

        public ApprovalQueue Queue { get; }

        public static Harness Create()
        {
            var dir = ApprovalFixtures.TempDir("forge-handler-");
            return new Harness(
                dir,
                new ApprovalRules(Path.Combine(dir, ApprovalRules.FileName), debounceMilliseconds: 0),
                new ApprovalQueue(InlineDispatcher.Instance));
        }

        public SessionApprovalHandler Handler(string sessionId = "s1", string? projectId = "p1") =>
            new(Queue, Rules, ApprovalFixtures.Session(sessionId, projectId));

        public async ValueTask DisposeAsync() => await Rules.DisposeAsync();

        public void DisposeDir() => ApprovalFixtures.DeleteTree(Dir);
    }

    private static async Task<ApprovalDecision> WaitDecisionAsync(ValueTask<ApprovalDecision> task) =>
        await task.AsTask().WaitAsync(ApprovalFixtures.StepTimeout);

    [Fact]
    public async Task Remembered_rule_allows_without_a_card()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            using var handler = harness.Handler();
            harness.Rules.Allow(ApprovalRuleKey.ForSession("shell_run", ToolSideEffect.Execute, "s1"));

            var decision = await WaitDecisionAsync(handler.RequestAsync(
                ApprovalFixtures.ToolRequest(), CancellationToken.None));

            Assert.Equal(ApprovalDecision.Allow, decision);
            Assert.Equal(0, await harness.Queue.CountAsync());
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Allow_and_deny_answer_without_recording_a_rule()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            using var handler = harness.Handler();
            var cardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
            var pending = handler.RequestAsync(ApprovalFixtures.ToolRequest(), CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            card.AllowCommand.Execute(null);
            Assert.Equal(ApprovalDecision.Allow, await WaitDecisionAsync(pending));
            Assert.Equal(0, await harness.Queue.CountAsync());
            Assert.Empty(harness.Rules.Rules);

            var denyCardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
            var denyPending = handler.RequestAsync(ApprovalFixtures.ToolRequest(), CancellationToken.None);
            var denyCard = await denyCardTask.WaitAsync(ApprovalFixtures.StepTimeout);
            denyCard.DenyCommand.Execute(null);
            Assert.Equal(ApprovalDecision.Deny, await WaitDecisionAsync(denyPending));
            Assert.Empty(harness.Rules.Rules);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Remember_session_scopes_the_rule_to_this_session()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            using var handler = harness.Handler("s1", "p1");
            var cardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
            var pending = handler.RequestAsync(ApprovalFixtures.ToolRequest(), CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            card.RememberSessionCommand.Execute(null);

            Assert.Equal(ApprovalDecision.Allow, await WaitDecisionAsync(pending));
            Assert.True(harness.Rules.IsAllowed("shell_run", ToolSideEffect.Execute, "s1", "p1"));
            Assert.False(harness.Rules.IsAllowed("shell_run", ToolSideEffect.Execute, "s2", "p1"));
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Remember_project_matches_the_same_project_from_another_session()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            using var handler = harness.Handler("s1", "p1");
            var cardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
            var pending = handler.RequestAsync(ApprovalFixtures.ToolRequest(), CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            Assert.True(card.RememberProjectCommand.CanExecute(null));
            card.RememberProjectCommand.Execute(null);

            Assert.Equal(ApprovalDecision.Allow, await WaitDecisionAsync(pending));
            Assert.True(harness.Rules.IsAllowed("shell_run", ToolSideEffect.Execute, "s2", "p1"));
            Assert.False(harness.Rules.IsAllowed("shell_run", ToolSideEffect.Execute, "s2", "p2"));
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Remember_global_matches_everywhere()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            using var handler = harness.Handler();
            var cardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
            var pending = handler.RequestAsync(ApprovalFixtures.ToolRequest(), CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            card.RememberGlobalCommand.Execute(null);

            Assert.Equal(ApprovalDecision.Allow, await WaitDecisionAsync(pending));
            Assert.True(harness.Rules.IsAllowed("shell_run", ToolSideEffect.Execute, "any", "any"));
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Without_a_project_the_project_button_is_disabled_and_writes_nothing()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            using var handler = harness.Handler("s1", projectId: null);
            Assert.Empty(handler.ProjectId);

            var cardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
            var pending = handler.RequestAsync(ApprovalFixtures.ToolRequest(), CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            Assert.False(card.CanRememberProject);
            Assert.False(card.RememberProjectCommand.CanExecute(null));
            Assert.NotEmpty(card.RememberProjectHint);

            card.RememberProjectCommand.Execute(null);
            Assert.False(pending.IsCompleted);

            card.AllowCommand.Execute(null);
            Assert.Equal(ApprovalDecision.Allow, await WaitDecisionAsync(pending));
            Assert.Empty(harness.Rules.Rules);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task An_already_cancelled_token_throws_before_a_card_is_ever_queued()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            using var handler = harness.Handler();
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await handler.RequestAsync(ApprovalFixtures.ToolRequest(), cts.Token));

            Assert.Equal(0, await harness.Queue.CountAsync());
            Assert.Empty(harness.Rules.Rules);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Cancellation_throws_withdraws_the_card_and_records_nothing()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            using var handler = harness.Handler();
            using var cts = new CancellationTokenSource();
            var pending = handler.RequestAsync(ApprovalFixtures.ToolRequest(), cts.Token);
            await ApprovalFixtures.WaitUntilAsync(async () => await harness.Queue.CountAsync() == 1, "the card to be queued");

            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            Assert.Equal(0, await harness.Queue.CountAsync());
            Assert.Empty(harness.Rules.Rules);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Dispose_withdraws_the_wait_as_deny()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            var handler = harness.Handler();
            var pending = handler.RequestAsync(ApprovalFixtures.ToolRequest(), CancellationToken.None);
            await ApprovalFixtures.WaitUntilAsync(async () => await harness.Queue.CountAsync() == 1, "the card to be queued");

            handler.Dispose();

            Assert.Equal(ApprovalDecision.Deny, await WaitDecisionAsync(pending));
            Assert.Equal(0, await harness.Queue.CountAsync());
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Extension_delete_has_no_remember_buttons_and_reads_as_a_deletion_confirmation()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            using var handler = harness.Handler();
            var cardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
            var pending = handler.RequestAsync(
                ApprovalFixtures.ToolRequest(ExtensionToolNames.Delete), CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            Assert.True(card.IsDelete);
            Assert.Equal(Loc.Instance[ExtensionsStrings.ToolDeleteTitle], card.Title);
            Assert.True(card.HasNote);
            Assert.NotEmpty(card.Note);
            Assert.False(card.CanRemember);
            Assert.False(card.RememberSessionCommand.CanExecute(null));
            Assert.False(card.RememberProjectCommand.CanExecute(null));
            Assert.False(card.RememberGlobalCommand.CanExecute(null));

            card.AllowCommand.Execute(null);
            Assert.Equal(ApprovalDecision.Allow, await WaitDecisionAsync(pending));
            Assert.Empty(harness.Rules.Rules);
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Rule_from_another_project_does_not_apply()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Rules.Allow(ApprovalRuleKey.ForProject("shell_run", ToolSideEffect.Execute, "other"));
            using var handler = harness.Handler("s1", "p1");

            var cardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
            var pending = handler.RequestAsync(ApprovalFixtures.ToolRequest(), CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            card.DenyCommand.Execute(null);
            Assert.Equal(ApprovalDecision.Deny, await WaitDecisionAsync(pending));
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Side_effect_mismatch_does_not_apply()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            harness.Rules.Allow(ApprovalRuleKey.ForSession("shell_run", ToolSideEffect.Write, "s1"));
            using var handler = harness.Handler();

            var cardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
            var pending = handler.RequestAsync(ApprovalFixtures.ToolRequest(), CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            card.DenyCommand.Execute(null);
            Assert.Equal(ApprovalDecision.Deny, await WaitDecisionAsync(pending));
        }
        finally
        {
            harness.DisposeDir();
        }
    }

    [Fact]
    public async Task Two_sessions_wait_side_by_side_and_decide_independently()
    {
        var harness = Harness.Create();
        try
        {
            await using var _ = harness;
            using var first = harness.Handler("s1", "p1");
            using var second = harness.Handler("s2", "p2");

            var firstCardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
            var firstPending = first.RequestAsync(ApprovalFixtures.ToolRequest(), CancellationToken.None);
            var firstCard = await firstCardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            var secondCardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
            var secondPending = second.RequestAsync(ApprovalFixtures.ToolRequest(), CancellationToken.None);
            var secondCard = await secondCardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            Assert.Equal(2, await harness.Queue.CountAsync());
            Assert.NotEqual(firstCard.Id, secondCard.Id);

            firstCard.AllowCommand.Execute(null);
            secondCard.DenyCommand.Execute(null);

            Assert.Equal(ApprovalDecision.Allow, await WaitDecisionAsync(firstPending));
            Assert.Equal(ApprovalDecision.Deny, await WaitDecisionAsync(secondPending));
            Assert.Equal(0, await harness.Queue.CountAsync());
        }
        finally
        {
            harness.DisposeDir();
        }
    }
}
