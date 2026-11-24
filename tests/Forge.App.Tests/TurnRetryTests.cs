using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests;
/// <summary>
/// Which failed turn gets the retry on its footnote: one that ran no tool may be replayed, nothing else may. A replay repeats
/// the user's own message, so the shapes that would repeat a side effect (a tool call), undo the user's decision (a stop) or
/// reach back past a later turn have to be refused — and the button is wired through the transcript, so a session kind that
/// installs no runner (a model-spawned sub-agent) gets no button.
/// </summary>
public sealed class TurnRetryTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 20, 9, 0, 0, TimeSpan.FromHours(8));

    private static TranscriptViewModel Transcript(bool retryable = true)
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: false);
        if (retryable)
        {
            transcript.CanRetryTurn = static _ => true;
            transcript.CanResumeTurn = static _ => true;
        }

        return transcript;
    }

    /// <summary>A turn that got as far as its first request and lost it: no tool ran, so a replay repeats nothing.</summary>
    private static TurnCardViewModel FailedTurn(
        TranscriptViewModel? transcript = null,
        Action<TurnCardViewModel>? beforeFinish = null,
        string prompt = "ship it")
    {
        var target = transcript ?? Transcript();
        target.AddUserMessage(prompt, At);
        var card = target.BeginTurn(At);
        card.Prompt = ComposerPrompt.FromText(prompt);
        beforeFinish?.Invoke(card);
        target.EndTurn(TimeSpan.FromSeconds(1), SessionStrings.TurnFailed("the provider refused the request"));
        return card;
    }

    [Fact]
    public void A_failed_turn_that_ran_no_tool_offers_the_retry()
    {
        var card = FailedTurn();

        Assert.True(card.CanRetry);
        Assert.True(card.FootnoteRow.CanRetry);
        Assert.True(card.FootnoteRow.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_retry_replays_the_exact_prompt_the_turn_opened_with()
    {
        var transcript = Transcript();
        var card = FailedTurn(
            transcript,
            beforeFinish: static c => c.AppendText("half an answer before the stream died"));
        var replayed = new List<ComposerPrompt>();
        transcript.RetryTurn = replayedCard =>
        {
            replayed.Add(replayedCard.Prompt!);
            return Task.CompletedTask;
        };

        await card.RetryAsync();

        var prompt = Assert.Single(replayed);
        Assert.Equal("ship it", prompt.Text);
    }

    [Fact]
    public void A_failed_turn_that_already_ran_a_tool_offers_no_retry()
    {
        // bash is the case that matters: replaying it would run the command a second time.
        var card = FailedTurn(beforeFinish: static c => c.AddTool(new ToolCall("t1", "bash", "{\"command\":\"git push\"}")));

        Assert.False(card.CanRetry);
        Assert.False(card.FootnoteRow.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_failed_turn_that_already_ran_a_tool_offers_the_continue_instead()
    {
        // The shape the Worker-2 turn had: a 429 after twenty tool calls. The replay is off the table, but leaving the user
        // with nothing is what made a retried turn look like it simply stopped, so the footnote offers the continue.
        var transcript = Transcript();
        var card = FailedTurn(
            transcript,
            beforeFinish: static c => c.AddTool(new ToolCall("t1", "bash", "{\"command\":\"dotnet build\"}")));
        var resumed = new List<TurnCardViewModel>();
        transcript.ResumeTurn = resumedCard =>
        {
            resumed.Add(resumedCard);
            return Task.CompletedTask;
        };

        Assert.False(card.CanRetry);
        Assert.True(card.CanResume);
        Assert.True(card.FootnoteRow.ResumeCommand.CanExecute(null));

        await card.ResumeAsync();

        Assert.Same(card, Assert.Single(resumed));
    }

    [Fact]
    public void A_failed_turn_that_ran_no_tool_offers_no_continue()
    {
        // Replaying is strictly better there (it re-sends the exact prompt), so the two affordances never share a footnote.
        var card = FailedTurn();

        Assert.True(card.CanRetry);
        Assert.False(card.CanResume);
        Assert.False(card.FootnoteRow.ResumeCommand.CanExecute(null));
    }

    [Fact]
    public void Only_the_newest_failed_turn_offers_the_continue()
    {
        var transcript = Transcript();
        var failed = FailedTurn(
            transcript,
            beforeFinish: static c => c.AddTool(new ToolCall("t1", "read", "{\"path\":\"a.txt\"}")));
        Assert.True(failed.FootnoteRow.CanResume);

        var raised = new List<string?>();
        failed.FootnoteRow.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        transcript.AddUserMessage("never mind, describe it instead", At.AddMinutes(1));
        var next = transcript.BeginTurn(At.AddMinutes(1));
        next.Prompt = ComposerPrompt.FromText("never mind, describe it instead");

        Assert.False(failed.CanResume);
        Assert.False(failed.FootnoteRow.CanResume);
        Assert.Contains(nameof(TurnFootnoteViewModel.CanResume), raised);
    }

    [Fact]
    public void A_stopped_turn_offers_no_retry()
    {
        var transcript = Transcript();
        transcript.AddUserMessage("ship it", At);
        transcript.BeginTurn(At).Prompt = ComposerPrompt.FromText("ship it");
        transcript.EndTurn(TimeSpan.FromSeconds(1), SessionStrings.Stopped);

        Assert.False(transcript.Current!.CanRetry);
    }

    [Fact]
    public void A_turn_that_completed_offers_no_retry()
    {
        var transcript = Transcript();
        transcript.AddUserMessage("ship it", At);
        transcript.BeginTurn(At).Prompt = ComposerPrompt.FromText("ship it");
        transcript.EndTurn(TimeSpan.FromSeconds(1), null);

        Assert.False(transcript.Current!.CanRetry);
    }

    [Fact]
    public void Only_the_newest_turn_offers_the_retry()
    {
        var transcript = Transcript();
        var failed = FailedTurn(transcript);
        Assert.True(failed.FootnoteRow.CanRetry);

        // The old footnote has to be told its button is gone: nothing else ever touches that row again, so a silent change
        // would leave a stale button on screen.
        var raised = new List<string?>();
        failed.FootnoteRow.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // The user typed again: the failed turn is history, and a retry of it would reopen a round the user already left.
        transcript.AddUserMessage("never mind, describe it instead", At.AddMinutes(1));
        var next = transcript.BeginTurn(At.AddMinutes(1));
        next.Prompt = ComposerPrompt.FromText("never mind, describe it instead");

        Assert.False(failed.CanRetry);
        Assert.False(failed.FootnoteRow.CanRetry);
        Assert.Contains(nameof(TurnFootnoteViewModel.CanRetry), raised);
        Assert.False(next.CanRetry);
    }

    [Fact]
    public void A_session_that_refuses_the_retry_now_hides_the_button()
    {
        // The gate is the owning session's: running, read-only, or an automation run already waiting for the quota.
        var transcript = Transcript();
        var card = FailedTurn(transcript);
        Assert.True(card.CanRetry);

        transcript.CanRetryTurn = static _ => false;
        transcript.RefreshRetry();

        Assert.False(card.CanRetry);
        Assert.False(card.FootnoteRow.CanRetry);
    }

    [Fact]
    public void A_transcript_without_a_runner_never_offers_it()
    {
        // A model-spawned sub-agent's transcript: the parent model decides, there is no composer, no retry.
        var transcript = new TranscriptViewModel(collapseOlderTurns: true);
        transcript.AddUserMessage("investigate", At);
        var card = transcript.BeginTurn(At);
        card.Prompt = ComposerPrompt.FromText("investigate");
        transcript.EndTurn(TimeSpan.FromSeconds(1), SessionStrings.TurnFailed("the provider refused the request"));

        Assert.False(card.CanRetry);
    }
}

/// <summary>
/// The footnote template itself: the compiled binding, the template selector and the button's visibility, which no
/// view-model assertion can cover.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TurnRetryViewTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 20, 9, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public Task The_footnote_hides_and_shows_the_button_with_the_session_s_gate() => SharedUi.RunAsync(() =>
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: false) { CanRetryTurn = static _ => true };
        transcript.AddUserMessage("ship it", At);
        var card = transcript.BeginTurn(At);
        card.Prompt = ComposerPrompt.FromText("ship it");
        transcript.EndTurn(TimeSpan.FromSeconds(1), SessionStrings.TurnFailed("the provider refused the request"));

        var window = new Window { Width = 640, Height = 240, Content = new ContentControl { Content = card.FootnoteRow } };
        window.Show();
        SharedUi.Settle(window);

        var button = window.GetVisualDescendants()
            .OfType<Button>()
            .Single(candidate => AutomationProperties.GetAutomationId(candidate) == "turn-retry");

        // A key that never got registered renders as "[turn.retry]", which no visibility assertion would catch.
        Assert.Equal(Loc.Instance[SessionStrings.TurnRetryKey], button.Content);
        Assert.True(button.IsVisible);

        // The owning session refused it after all (it became busy, or a resume run attached): the same row has to hide it.
        transcript.CanRetryTurn = static _ => false;
        transcript.RefreshRetry();
        SharedUi.Settle(window);
        Assert.False(button.IsVisible);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_footnote_offers_the_continue_when_the_turn_had_already_run_a_tool() => SharedUi.RunAsync(() =>
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: false)
        {
            CanRetryTurn = static _ => true,
            CanResumeTurn = static _ => true,
        };
        transcript.AddUserMessage("ship it", At);
        var card = transcript.BeginTurn(At);
        card.Prompt = ComposerPrompt.FromText("ship it");
        card.AddTool(new ToolCall("t1", "bash", "{\"command\":\"dotnet build\"}"));
        transcript.EndTurn(
            TimeSpan.FromSeconds(1),
            SessionStrings.TurnFailedRetried("the provider refused the request", 4, TimeSpan.FromSeconds(32)));

        var window = new Window { Width = 640, Height = 240, Content = new ContentControl { Content = card.FootnoteRow } };
        window.Show();
        SharedUi.Settle(window);

        var visible = window.GetVisualDescendants()
            .OfType<Button>()
            .Where(candidate => AutomationProperties.GetAutomationId(candidate) is "turn-retry" or "turn-resume")
            .Where(candidate => candidate.IsVisible)
            .ToList();

        // Exactly one, and it is the continue: a registered-key typo would render "[turn.resume]" here, and a wrong gate
        // would show the retry that replays the bash command.
        var button = Assert.Single(visible);
        Assert.Equal("turn-resume", AutomationProperties.GetAutomationId(button));
        Assert.Equal(Loc.Instance[SessionStrings.TurnResumeKey], button.Content);

        // The footnote carries the attempts: the user who only sees "429" would never learn it was tried five times.
        Assert.Equal(
            SessionStrings.TurnFailedRetried("the provider refused the request", 4, TimeSpan.FromSeconds(32)).Resolve(),
            card.Footnote);
        window.Close();
        return Task.CompletedTask;
    });
}
