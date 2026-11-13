using Forge.Abstractions;
using Forge.App.Approval;
using Xunit;

namespace Forge.App.Tests.Approval;

public sealed class UserQuestionBoundsSkipQueueTests
{
    private static UserQuestionRequest Req(string session = "s1", int? min = null, int? max = null, bool multi = true, bool freeText = true, int questions = 1)
    {
        var list = new List<UserQuestion>();
        for (var i = 1; i <= questions; i++)
        {
            list.Add(new UserQuestion($"q{i}", "H" + i, "Q?", [new("a"), new("b"), new("c"), new("d")], multi, freeText, min, max));
        }

        return new UserQuestionRequest(session, null, "main", list);
    }

    private static UserQuestionCardViewModel Card(int? min = null, int? max = null, bool multi = true, bool freeText = true, int questions = 1) =>
        new(Req("s1", min, max, multi, freeText, questions), "S");

    private static (ApprovalQueue Queue, SessionQuestionHandler Handler) Harness(string session = "s1")
    {
        var queue = new ApprovalQueue(InlineDispatcher.Instance);
        return (queue, new SessionQuestionHandler(queue, ApprovalFixtures.Session(session)));
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + ApprovalFixtures.StepTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("condition never became true");
    }

    private static Task<bool> Shown(ApprovalQueue queue, int count) => Task.FromResult(queue.Items.Count == count);

    // ---- bounds ----

    [Fact]
    public void Below_min_is_unanswered_at_min_is_answered()
    {
        var card = Card(min: 2, max: 3);
        var q = card.Questions[0];

        q.Options[0].IsSelected = true;
        Assert.False(q.IsAnswered);
        Assert.False(card.CanSubmit);
        q.Options[1].IsSelected = true;
        Assert.True(q.IsAnswered);
        Assert.True(card.CanSubmit);
    }

    [Fact]
    public void Max_refuses_further_picks_and_disables_unchecked_options()
    {
        var card = Card(min: 1, max: 2, freeText: false);
        var q = card.Questions[0];
        q.Options[0].IsSelected = true;
        q.Options[1].IsSelected = true;

        Assert.True(q.IsAtMax);
        Assert.False(q.Options[2].IsEnabled);
        Assert.True(q.Options[0].IsEnabled);
        q.Options[2].IsSelected = true;
        Assert.False(q.Options[2].IsSelected);
        Assert.Equal(2, q.PickCount);

        q.Options[0].IsSelected = false;
        Assert.False(q.IsAtMax);
        Assert.True(q.Options[2].IsEnabled);
    }

    [Fact]
    public void Free_text_counts_as_a_pick()
    {
        var card = Card(min: 2, max: 2);
        var q = card.Questions[0];
        q.Options[0].IsSelected = true;
        Assert.False(q.IsAnswered);

        q.FreeText = "mine";
        Assert.Equal(2, q.PickCount);
        Assert.True(q.IsAnswered);
        Assert.True(q.IsAtMax);
        Assert.False(q.Options[1].IsEnabled);

        q.FreeText = "   ";
        Assert.Equal(1, q.PickCount);
        Assert.False(q.IsAnswered);
    }

    [Fact]
    public void A_full_question_disables_an_empty_free_text_box()
    {
        var card = Card(min: 1, max: 1);
        var q = card.Questions[0];

        Assert.True(q.IsFreeTextEnabled);
        q.Options[0].IsSelected = true;
        Assert.False(q.IsFreeTextEnabled);
    }

    [Fact]
    public void Default_bounds_are_one_to_all_and_single_select_ignores_them()
    {
        var multi = Card().Questions[0];
        Assert.Equal(1, multi.MinSelect);
        Assert.Equal(5, multi.MaxSelect);

        var single = Card(min: 3, max: 4, multi: false).Questions[0];
        Assert.Equal(1, single.MinSelect);
        Assert.Equal(1, single.MaxSelect);
        single.Options[0].IsSelected = true;
        single.FreeText = "also";
        Assert.True(single.IsAnswered);
    }

    [Fact]
    public void The_bounds_hint_is_localized_text()
    {
        var q = Card(min: 2, max: 3).Questions[0];

        Assert.Contains("2", q.BoundsHint);
        Assert.Contains("3", q.BoundsHint);
        Assert.Equal(string.Empty, Card(multi: false).Questions[0].BoundsHint);
    }

    // ---- skip ----

    [Fact]
    public void Skipped_question_resolves_and_returns_a_skipped_answer()
    {
        var card = Card(questions: 2);
        card.Questions[0].Options[0].IsSelected = true;
        card.Questions[1].IsSkipped = true;

        Assert.True(card.CanSubmit);
        var skipped = card.Questions[1].ToAnswer();
        Assert.True(skipped.Skipped);
        Assert.Empty(skipped.Selected);
        Assert.Null(skipped.FreeText);
        Assert.False(card.Questions[1].Options[0].IsEnabled);
        Assert.False(card.Questions[1].IsFreeTextEnabled);
    }

    [Fact]
    public void Skipping_discards_earlier_input_in_the_answer()
    {
        var card = Card(questions: 2);
        card.Questions[0].Options[0].IsSelected = true;
        card.Questions[0].FreeText = "x";
        card.Questions[1].Options[0].IsSelected = true;
        card.Questions[0].IsSkipped = true;

        var answer = card.Questions[0].ToAnswer();
        Assert.True(answer.Skipped);
        Assert.Empty(answer.Selected);
        Assert.Null(answer.FreeText);
    }

    [Fact]
    public void All_skipped_cannot_be_submitted_and_unskipping_restores()
    {
        var card = Card(questions: 2);
        card.Questions[0].IsSkipped = true;
        card.Questions[1].IsSkipped = true;

        Assert.True(card.AllSkipped);
        Assert.False(card.CanSubmit);
        Assert.False(card.SubmitCommand.CanExecute(null));
        Assert.NotEqual(string.Empty, card.AllSkippedHint);
        card.SubmitCommand.Execute(null);
        Assert.False(card.IsDecided);

        card.Questions[1].IsSkipped = false;
        Assert.False(card.AllSkipped);
        Assert.False(card.CanSubmit);
        card.Questions[1].Options[0].IsSelected = true;
        Assert.True(card.CanSubmit);
        Assert.Equal(string.Empty, card.AllSkippedHint);
    }

    [Fact]
    public void Unskipping_keeps_the_previous_selection()
    {
        var card = Card();
        var q = card.Questions[0];
        q.Options[1].IsSelected = true;
        q.IsSkipped = true;
        Assert.False(q.IsAnswered);

        q.IsSkipped = false;

        Assert.True(q.IsAnswered);
        Assert.False(q.ToAnswer().Skipped);
    }

    [Fact]
    public async Task Submit_with_a_skipped_question_completes_with_the_skipped_flag()
    {
        var card = Card(questions: 2);
        card.Questions[0].Options[2].IsSelected = true;
        card.Questions[1].IsSkipped = true;

        card.SubmitCommand.Execute(null);

        Assert.True((await card.Decision.WaitAsync(ApprovalFixtures.StepTimeout)).IsAllowed);
        Assert.False(card.Response!.Answers[0].Skipped);
        Assert.True(card.Response.Answers[1].Skipped);
    }

    // ---- queue ----

    [Fact]
    public async Task Parallel_requests_show_one_card_and_the_next_after_an_answer()
    {
        var (queue, handler) = Harness();
        var first = handler.AskAsync(Req(), CancellationToken.None).AsTask();
        await WaitForAsync(() => Shown(queue, 1));
        var second = handler.AskAsync(Req(), CancellationToken.None).AsTask();
        var third = handler.AskAsync(Req(), CancellationToken.None).AsTask();
        await Task.Delay(50);

        Assert.Single(queue.Items);
        var firstCard = (UserQuestionCardViewModel)queue.Items[0];
        await WaitForAsync(() => Task.FromResult(firstCard.WaitingCount == 2));
        Assert.True(firstCard.HasWaiting);

        firstCard.Questions[0].Options[0].IsSelected = true;
        firstCard.SubmitCommand.Execute(null);
        Assert.Equal(UserQuestionOutcome.Answered, (await first.WaitAsync(ApprovalFixtures.StepTimeout)).Outcome);

        await WaitForAsync(() => Task.FromResult(queue.Items.Count == 1 && !ReferenceEquals(queue.Items[0], firstCard)));
        var secondCard = (UserQuestionCardViewModel)queue.Items[0];
        Assert.Equal(1, secondCard.WaitingCount);
        Assert.False(second.IsCompleted);

        secondCard.DismissCommand.Execute(null);
        Assert.Equal(UserQuestionOutcome.Declined, (await second.WaitAsync(ApprovalFixtures.StepTimeout)).Outcome);
        await WaitForAsync(() => Task.FromResult(queue.Items.Count == 1 && !ReferenceEquals(queue.Items[0], secondCard)));
        var thirdCard = (UserQuestionCardViewModel)queue.Items[0];
        Assert.Equal(0, thirdCard.WaitingCount);

        thirdCard.DismissCommand.Execute(null);
        await third.WaitAsync(ApprovalFixtures.StepTimeout);
    }

    [Fact]
    public async Task Cancelling_a_queued_request_completes_cancelled_without_showing_it()
    {
        var (queue, handler) = Harness();
        var first = handler.AskAsync(Req(), CancellationToken.None).AsTask();
        await WaitForAsync(() => Shown(queue, 1));
        var shownCard = queue.Items[0];
        using var cts = new CancellationTokenSource();
        var queued = handler.AskAsync(Req(), cts.Token).AsTask();
        await Task.Delay(30);
        var follower = handler.AskAsync(Req(), CancellationToken.None).AsTask();
        await Task.Delay(30);

        await cts.CancelAsync();

        Assert.Equal(UserQuestionOutcome.Cancelled, (await queued.WaitAsync(ApprovalFixtures.StepTimeout)).Outcome);
        Assert.Single(queue.Items);
        Assert.Same(shownCard, queue.Items[0]);
        await WaitForAsync(() => Task.FromResult(((UserQuestionCardViewModel)shownCard).WaitingCount == 1));

        ((UserQuestionCardViewModel)shownCard).DismissCommand.Execute(null);
        await first.WaitAsync(ApprovalFixtures.StepTimeout);
        await WaitForAsync(() => Task.FromResult(queue.Items.Count == 1 && !ReferenceEquals(queue.Items[0], shownCard)));
        ((UserQuestionCardViewModel)queue.Items[0]).DismissCommand.Execute(null);
        await follower.WaitAsync(ApprovalFixtures.StepTimeout);
        Assert.Empty(queue.Items);
    }

    [Fact]
    public async Task Cancelling_the_shown_request_shows_the_next()
    {
        var (queue, handler) = Harness();
        using var cts = new CancellationTokenSource();
        var first = handler.AskAsync(Req(), cts.Token).AsTask();
        await WaitForAsync(() => Shown(queue, 1));
        var firstCard = queue.Items[0];
        var second = handler.AskAsync(Req(), CancellationToken.None).AsTask();
        await Task.Delay(30);

        await cts.CancelAsync();

        Assert.Equal(UserQuestionOutcome.Cancelled, (await first.WaitAsync(ApprovalFixtures.StepTimeout)).Outcome);
        await WaitForAsync(() => Task.FromResult(queue.Items.Count == 1 && !ReferenceEquals(queue.Items[0], firstCard)));
        Assert.False(second.IsCompleted);
        ((UserQuestionCardViewModel)queue.Items[0]).DismissCommand.Execute(null);
        await second.WaitAsync(ApprovalFixtures.StepTimeout);
    }

    [Fact]
    public async Task Different_sessions_do_not_block_each_other()
    {
        var queue = new ApprovalQueue(InlineDispatcher.Instance);
        var one = new SessionQuestionHandler(queue, ApprovalFixtures.Session("s1"));
        var two = new SessionQuestionHandler(queue, ApprovalFixtures.Session("s2"));
        var a = one.AskAsync(Req(), CancellationToken.None).AsTask();
        var b = two.AskAsync(Req(), CancellationToken.None).AsTask();

        await WaitForAsync(() => Shown(queue, 2));

        Assert.Equal(["s1", "s2"], queue.Items.Select(c => c.SessionId).Order());
        one.Dispose();
        two.Dispose();
        await Task.WhenAll(a, b).WaitAsync(ApprovalFixtures.StepTimeout);
    }

    [Fact]
    public async Task Releasing_the_session_cancels_shown_and_queued_questions()
    {
        var (queue, handler) = Harness();
        var first = handler.AskAsync(Req(), CancellationToken.None).AsTask();
        await WaitForAsync(() => Shown(queue, 1));
        var second = handler.AskAsync(Req(), CancellationToken.None).AsTask();
        await Task.Delay(30);

        handler.Dispose();

        Assert.Equal(UserQuestionOutcome.Cancelled, (await first.WaitAsync(ApprovalFixtures.StepTimeout)).Outcome);
        Assert.Equal(UserQuestionOutcome.Cancelled, (await second.WaitAsync(ApprovalFixtures.StepTimeout)).Outcome);
        Assert.Empty(queue.Items);
    }

    [Fact]
    public async Task Withdraw_all_cancels_queued_questions_too()
    {
        var (queue, handler) = Harness();
        var first = handler.AskAsync(Req(), CancellationToken.None).AsTask();
        await WaitForAsync(() => Shown(queue, 1));
        var second = handler.AskAsync(Req(), CancellationToken.None).AsTask();
        await Task.Delay(30);

        await queue.WithdrawAllAsync();

        Assert.Equal(UserQuestionOutcome.Cancelled, (await first.WaitAsync(ApprovalFixtures.StepTimeout)).Outcome);
        Assert.Equal(UserQuestionOutcome.Cancelled, (await second.WaitAsync(ApprovalFixtures.StepTimeout)).Outcome);
        Assert.Empty(queue.Items);
    }

    [Fact]
    public async Task Many_parallel_requests_all_complete_one_at_a_time()
    {
        var (queue, handler) = Harness();
        const int count = 40;
        var asks = Enumerable.Range(0, count)
            .Select(_ => Task.Run(() => handler.AskAsync(Req(), CancellationToken.None).AsTask()))
            .ToArray();

        var answered = 0;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (answered < count && DateTimeOffset.UtcNow < deadline)
        {
            var card = queue.Items.OfType<UserQuestionCardViewModel>().FirstOrDefault();
            if (card is null)
            {
                await Task.Delay(5);
                continue;
            }

            Assert.True(queue.Items.Count <= 1);
            card.Questions[0].Options[0].IsSelected = true;
            card.SubmitCommand.Execute(null);
            answered++;
            await WaitForAsync(() => Task.FromResult(!queue.Items.Contains(card)));
        }

        var responses = await Task.WhenAll(asks).WaitAsync(ApprovalFixtures.StepTimeout);
        Assert.Equal(count, answered);
        Assert.All(responses, r => Assert.Equal(UserQuestionOutcome.Answered, r.Outcome));
        Assert.Empty(queue.Items);
    }
}