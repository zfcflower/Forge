using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Prompts;
using Forge.App.ViewModels;
using Forge.Core.Providers;
using System.Runtime.CompilerServices;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The turn-level half of the 429 work, driven through a real session instead of the transcript primitives: a request that
/// loses every attempt has to leave one line per retry, close the turn as failed with the server's sentence, and — when the
/// endpoint is the thing that is busy — continue by itself once the wait is over. This is the wiring nothing else covers: the
/// event switch that turns <see cref="AgentRetryWait"/> into a row, and the finally that turns
/// <see cref="AgentFailed"/> into a footnote.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TurnOverloadWiringTests
{
    [Fact]
    public Task Every_retry_leaves_a_line_and_the_turn_ends_failed_with_its_attempts() => SharedUi.RunAsync(async () =>
    {
        var provider = new RefusingProvider(429, Overloaded);
        using var session = await OverloadSession.StartAsync(provider);

        await session.RunTurnAsync("ship it", expectRuns: 1);

        var card = Assert.IsType<TurnCardViewModel>(session.Tab.Transcript.Current);
        Assert.Equal(5, provider.Calls);
        Assert.Equal(TurnEndStatus.Failed, card.EndStatus);

        // One line per retry, numbered the way the event carries them: 2 of 5 up to 5 of 5, with the ladder's seconds.
        Assert.Equal(
            [
                RateLimitStrings.Retrying("scripted", 2, 5, TimeSpan.FromSeconds(1), RetryReason.Overloaded).Resolve(),
                RateLimitStrings.Retrying("scripted", 3, 5, TimeSpan.FromSeconds(3), RetryReason.Overloaded).Resolve(),
                RateLimitStrings.Retrying("scripted", 4, 5, TimeSpan.FromSeconds(8), RetryReason.Overloaded).Resolve(),
                RateLimitStrings.Retrying("scripted", 5, 5, TimeSpan.FromSeconds(20), RetryReason.Overloaded).Resolve(),
            ],
            session.Notes());

        // The footnote is the failure the user acts on, and it says what the attempts cost — a retried turn and an instant
        // refusal look identical without it.
        Assert.Equal(
            SessionStrings.TurnFailedRetried(provider.Message, 4, TimeSpan.FromSeconds(32)).Resolve(),
            card.Footnote);

        // An automatic round is already on its way, so the manual replay stays hidden instead of racing it.
        Assert.True(session.Tab.HasAutomationBanner);
        Assert.False(card.CanRetry);
    });

    [Fact]
    public Task A_429_that_outlives_the_ladder_continues_by_itself_and_replays_nothing() => SharedUi.RunAsync(async () =>
    {
        // The first turn spends the whole ladder; the automatic round then gets a real answer, and the request it sends is
        // the continue instruction — nothing that already ran is sent again.
        var provider = new RefusingProvider(429, Overloaded, answerAfterLadder: "resumed");
        using var session = await OverloadSession.StartAsync(provider);

        await session.RunTurnAsync("ship it", expectRuns: 2, autoResume: true);

        Assert.Equal(2, session.Turns);
        var resumed = Assert.IsType<TurnCardViewModel>(session.Tab.Transcript.Current);
        Assert.Equal(TurnEndStatus.Completed, resumed.EndStatus);
        Assert.Contains(AppPromptTexts.OverloadContinue, provider.LastUserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("ship it", provider.LastUserMessage, StringComparison.Ordinal);
    });

    [Fact]
    public Task A_failure_that_is_not_the_endpoint_being_busy_is_left_to_the_user() => SharedUi.RunAsync(async () =>
    {
        // 400 is not a wait: one attempt, no automatic round, no banner, and a footnote without an attempt count.
        var provider = new RefusingProvider(400, "invalid request");
        using var session = await OverloadSession.StartAsync(provider);

        await session.RunTurnAsync("ship it", expectRuns: 1);

        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, session.Turns);
        Assert.False(session.Tab.HasAutomationBanner);
        var card = Assert.IsType<TurnCardViewModel>(session.Tab.Transcript.Current);
        Assert.Equal(SessionStrings.TurnFailed(provider.Message).Resolve(), card.Footnote);
    });

    [Fact]
    public Task The_automatic_rounds_stop_at_their_cap() => SharedUi.RunAsync(async () =>
    {
        // An endpoint that stays busy forever must not keep a session talking to itself: three automatic rounds, then it
        // stops and leaves the footnote to the user.
        var provider = new RefusingProvider(429, Overloaded);
        using var session = await OverloadSession.StartAsync(provider);

        await session.RunTurnAsync("ship it", expectRuns: 4, autoResume: true);

        Assert.Equal(4, session.Turns);
        Assert.False(session.Tab.HasAutomationBanner);
    });

    [Fact]
    public Task A_message_the_user_sends_instead_takes_the_next_turn() => SharedUi.RunAsync(async () =>
    {
        // The pending round is minutes away; the user typing is the answer, so the next turn carries their words and not
        // the continue instruction.
        var provider = new RefusingProvider(429, Overloaded);
        using var session = await OverloadSession.StartAsync(provider);

        await session.RunTurnAsync("ship it", expectRuns: 1);
        await session.RunTurnAsync("carry on differently", expectRuns: 1);

        Assert.Equal(2, session.Turns);
        Assert.Equal("carry on differently", provider.LastUserMessage);
    });

    [Fact]
    public Task The_banner_s_cancel_stops_the_round_that_was_waiting() => SharedUi.RunAsync(async () =>
    {
        var provider = new RefusingProvider(429, Overloaded);
        using var session = await OverloadSession.StartAsync(provider);

        await session.RunTurnAsync("ship it", expectRuns: 1);
        Assert.True(session.Tab.HasAutomationBanner);

        session.Tab.CancelAutomationCommand.Execute(null);
        session.SettleFrames();

        Assert.False(session.Tab.HasAutomationBanner);
        Assert.Equal(1, session.Turns);
        Assert.Equal(5, provider.Calls);
    });

    private const string Overloaded = "The inference service is overloaded. Please try again later.";

    /// <summary>An endpoint that refuses every attempt, optionally answering once the ladder has been spent.</summary>
    private sealed class RefusingProvider(int statusCode, string body, string? answerAfterLadder = null) : IChatProvider
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public string LastUserMessage { get; private set; } = string.Empty;

        public string Id => "scripted";

        public string Message => $"scripted returned {statusCode}: {body}";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var call = Interlocked.Increment(ref calls);
            LastUserMessage = request.Messages.LastOrDefault(static message => message.Role == MessageRole.User)
                is { } last
                ? string.Concat(last.Content.OfType<Forge.Abstractions.TextBlock>().Select(static block => block.Text))
                : string.Empty;
            await Task.CompletedTask;

            // The ladder spends five attempts on the first turn's request; the automatic round after it gets an answer.
            if (answerAfterLadder is { } answer && call > 5)
            {
                yield return new TextDelta(answer);
                yield return new MessageCompleted(
                    new ChatMessage(MessageRole.Assistant, [new Forge.Abstractions.TextBlock(answer)]),
                    TokenUsage.Empty,
                    StopReason.EndTurn);
                yield break;
            }

            yield return Refuse();
        }

        /// <summary>Yielded from a call that only throws: the attempt still fails before producing anything.</summary>
        private ChatStreamEvent Refuse() =>
            throw new ProviderException(Message, retryable: statusCode is 429, statusCode);
    }
}
