using Forge.Abstractions;
using Forge.App.Approval;
using Xunit;

namespace Forge.App.Tests.Approval;

public sealed class UserQuestionTests
{
    private static UserQuestionRequest Request(string session = "s1", bool multi = false, bool freeText = true) =>
        new(
            session,
            "a1",
            "scout",
            [
                new UserQuestion("q1", "Storage", "Which store?", [new("SQLite", "embedded"), new("Files"), new("Cloud")], multi, freeText),
                new UserQuestion("q2", "Scope", "How far?", [new("Core"), new("All")]),
            ]);

    private static (ApprovalQueue Queue, SessionQuestionHandler Handler) Harness(string session = "s1")
    {
        var queue = new ApprovalQueue(InlineDispatcher.Instance);
        return (queue, new SessionQuestionHandler(queue, ApprovalFixtures.Session(session)));
    }

    private static async Task<UserQuestionCardViewModel> NextCardAsync(ApprovalQueue queue, int expectedCount = 1)
    {
        var deadline = DateTimeOffset.UtcNow + ApprovalFixtures.StepTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await queue.CountAsync() >= expectedCount)
            {
                return queue.Items.OfType<UserQuestionCardViewModel>().Last();
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("the question card never appeared");
    }

    [Fact]
    public void Submit_is_enabled_only_when_every_question_is_answered()
    {
        var card = new UserQuestionCardViewModel(Request(), "S");

        Assert.False(card.SubmitCommand.CanExecute(null));
        card.Questions[0].Options[0].IsSelected = true;
        Assert.False(card.SubmitCommand.CanExecute(null));
        card.Questions[1].FreeText = "  ";
        Assert.False(card.SubmitCommand.CanExecute(null));
        card.Questions[1].FreeText = "custom";
        Assert.True(card.SubmitCommand.CanExecute(null));
        card.Questions[1].FreeText = "";
        Assert.False(card.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public void Free_text_does_not_count_when_the_question_forbids_it()
    {
        var card = new UserQuestionCardViewModel(Request(freeText: false), "S");

        card.Questions[0].FreeText = "typed";

        Assert.False(card.Questions[0].IsAnswered);
    }

    [Fact]
    public void Single_select_keeps_one_option_and_multi_select_keeps_several()
    {
        var single = new UserQuestionCardViewModel(Request(multi: false), "S");
        single.Questions[0].Options[0].IsSelected = true;
        single.Questions[0].Options[1].IsSelected = true;
        Assert.Equal(["Files"], single.Questions[0].Options.Where(o => o.IsSelected).Select(o => o.Label));

        var multi = new UserQuestionCardViewModel(Request(multi: true), "S");
        multi.Questions[0].Options[0].IsSelected = true;
        multi.Questions[0].Options[2].IsSelected = true;
        Assert.Equal(["SQLite", "Cloud"], multi.Questions[0].Options.Where(o => o.IsSelected).Select(o => o.Label));
    }

    [Fact]
    public async Task Submit_completes_with_answers_in_option_order_and_trimmed_free_text()
    {
        var card = new UserQuestionCardViewModel(Request(multi: true), "S");
        card.Questions[0].Options[2].IsSelected = true;
        card.Questions[0].Options[0].IsSelected = true;
        card.Questions[0].FreeText = " extra ";
        card.Questions[1].Options[1].IsSelected = true;

        card.SubmitCommand.Execute(null);

        var outcome = await card.Decision.WaitAsync(ApprovalFixtures.StepTimeout);
        Assert.True(outcome.IsAllowed);
        var response = card.Response!;
        Assert.Equal(UserQuestionOutcome.Answered, response.Outcome);
        Assert.Equal(["SQLite", "Cloud"], response.Answers[0].Selected);
        Assert.Equal("extra", response.Answers[0].FreeText);
        Assert.Equal(["All"], response.Answers[1].Selected);
        Assert.Null(response.Answers[1].FreeText);
    }

    [Fact]
    public void Submit_does_nothing_while_unanswered()
    {
        var card = new UserQuestionCardViewModel(Request(), "S");

        card.SubmitCommand.Execute(null);

        Assert.False(card.IsDecided);
        Assert.Null(card.Response);
    }

    [Fact]
    public async Task Dismiss_completes_as_a_denial_and_blocks_a_later_submit()
    {
        var card = new UserQuestionCardViewModel(Request(), "S");

        card.DismissCommand.Execute(null);
        card.Questions[0].Options[0].IsSelected = true;
        card.Questions[1].Options[0].IsSelected = true;
        card.SubmitCommand.Execute(null);

        var outcome = await card.Decision.WaitAsync(ApprovalFixtures.StepTimeout);
        Assert.Equal(ApprovalCardDecision.Deny, outcome.Decision);
        Assert.Null(card.Response);
    }

    [Fact]
    public async Task Handler_returns_the_submitted_answers_and_removes_the_card()
    {
        var (queue, handler) = Harness();
        var ask = handler.AskAsync(Request(), CancellationToken.None).AsTask();
        var card = await NextCardAsync(queue);
        card.Questions[0].Options[1].IsSelected = true;
        card.Questions[1].Options[0].IsSelected = true;

        card.SubmitCommand.Execute(null);
        var response = await ask.WaitAsync(ApprovalFixtures.StepTimeout);

        Assert.Equal(UserQuestionOutcome.Answered, response.Outcome);
        Assert.Equal(["Files"], response.Answers[0].Selected);
        Assert.Equal(0, await queue.CountAsync());
    }

    [Fact]
    public async Task Handler_reports_declined_on_dismiss()
    {
        var (queue, handler) = Harness();
        var ask = handler.AskAsync(Request(), CancellationToken.None).AsTask();
        var card = await NextCardAsync(queue);

        card.DismissCommand.Execute(null);

        Assert.Equal(UserQuestionOutcome.Declined, (await ask.WaitAsync(ApprovalFixtures.StepTimeout)).Outcome);
        Assert.Equal(0, await queue.CountAsync());
    }

    [Fact]
    public async Task Cancelling_the_wait_withdraws_the_card_and_reports_cancelled()
    {
        var (queue, handler) = Harness();
        using var cts = new CancellationTokenSource();
        var ask = handler.AskAsync(Request(), cts.Token).AsTask();
        await NextCardAsync(queue);

        await cts.CancelAsync();

        Assert.Equal(UserQuestionOutcome.Cancelled, (await ask.WaitAsync(ApprovalFixtures.StepTimeout)).Outcome);
        Assert.Equal(0, await queue.CountAsync());
    }

    [Fact]
    public async Task Releasing_the_session_withdraws_its_card_as_cancelled()
    {
        var (queue, handler) = Harness();
        var ask = handler.AskAsync(Request(), CancellationToken.None).AsTask();
        await NextCardAsync(queue);

        handler.Dispose();

        Assert.Equal(UserQuestionOutcome.Cancelled, (await ask.WaitAsync(ApprovalFixtures.StepTimeout)).Outcome);
        Assert.Equal(0, await queue.CountAsync());
    }

    [Fact]
    public async Task Questions_are_routed_to_their_own_session_and_withdrawn_per_session()
    {
        var queue = new ApprovalQueue(InlineDispatcher.Instance);
        var first = new SessionQuestionHandler(queue, ApprovalFixtures.Session("s1"));
        var second = new SessionQuestionHandler(queue, ApprovalFixtures.Session("s2"));
        var askFirst = first.AskAsync(Request("ignored"), CancellationToken.None).AsTask();
        await NextCardAsync(queue);
        var askSecond = second.AskAsync(Request("ignored"), CancellationToken.None).AsTask();
        await NextCardAsync(queue, 2);

        Assert.Equal(["s1", "s2"], queue.Items.Select(card => card.SessionId));

        first.Dispose();
        Assert.Equal(UserQuestionOutcome.Cancelled, (await askFirst.WaitAsync(ApprovalFixtures.StepTimeout)).Outcome);
        Assert.False(askSecond.IsCompleted);
        Assert.Equal(["s2"], queue.Items.Select(card => card.SessionId));

        second.Dispose();
        await askSecond.WaitAsync(ApprovalFixtures.StepTimeout);
    }

    [Fact]
    public async Task A_disposed_handler_or_cancelled_token_answers_cancelled_without_a_card()
    {
        var (queue, handler) = Harness();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.Equal(UserQuestionOutcome.Cancelled, (await handler.AskAsync(Request(), cts.Token)).Outcome);
        handler.Dispose();
        Assert.Equal(UserQuestionOutcome.Cancelled, (await handler.AskAsync(Request(), CancellationToken.None)).Outcome);
        Assert.Equal(0, await queue.CountAsync());
    }

    [Fact]
    public void The_card_names_the_asking_agent()
    {
        var card = new UserQuestionCardViewModel(Request(), "S");

        Assert.Equal("scout", card.AgentName);
        Assert.Contains("scout", card.AskedByText);
    }
}
