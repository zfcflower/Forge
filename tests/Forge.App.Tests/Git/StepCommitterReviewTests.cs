using Forge.Abstractions;
using Forge.App.Automations;
using Forge.App.Git;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// The automatic review trigger of wave W17b: a step that was just committed asks its session's agent for a
/// <c>request_review</c> of that commit when the <c>reviewAfterStepCommit</c> switch is on and the review mode is the
/// sub-agent, and never when the mode is manual, the switch is off, or the commit did not happen. The hand-off itself is
/// the shared one (<see cref="SessionWorkspaceViewModel.HandMessageAsync"/>, pinned by <c>SessionHandMessageTests</c>: a turn
/// when idle, the composer's draft when busy); here the messenger is a fake, so what is pinned is the trigger, its message,
/// and that nothing about it can change the commit result.
/// </summary>
public sealed class StepCommitterReviewTests
{
    private static CancellationTokenSource Bound() => new(TimeSpan.FromSeconds(90));

    private const string Origin = GitStrings.HandOffReviewOrigin;

    // ------------------------------------------------------------ the 2x2 preference matrix

    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 0)]
    public async Task A_review_is_asked_for_exactly_when_the_switch_is_on_and_the_mode_is_the_sub_agent(
        bool afterStepCommit,
        bool subAgentMode,
        int expected)
    {
        var messenger = new RecordingMessenger();
        await using var harness = await StepCommitHarness.CreateAsync(
            reviewSettings: () => new StepReviewSettings(afterStepCommit, subAgentMode),
            messenger: messenger);
        var item = await harness.AddDoneAsync();
        harness.Repo.WriteFile("new.txt", "x\n");
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Committed, result.Outcome);
        Assert.Equal(expected, messenger.Calls.Count);
    }

    [Fact]
    public async Task The_manual_mode_never_triggers_a_review_even_with_the_switch_on()
    {
        var messenger = new RecordingMessenger();
        var preferences = new AppPreferences { ReviewAfterStepCommit = true, ReviewMode = "manual" };
        await using var harness = await StepCommitHarness.CreateAsync(
            reviewSettings: StepReviewSettings.Live(preferences),
            messenger: messenger);
        var item = await harness.AddDoneAsync();
        harness.Repo.WriteFile("new.txt", "x\n");
        using var cts = Bound();

        await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Empty(messenger.Calls);
    }

    // ------------------------------------------------------------ the message and where it goes

    [Fact]
    public async Task The_request_names_the_step_id_of_the_commit_that_was_just_made()
    {
        var messenger = new RecordingMessenger();
        await using var harness = await StepCommitHarness.CreateAsync(
            reviewSettings: () => new StepReviewSettings(true, true),
            messenger: messenger);
        var item = await harness.AddDoneAsync(title: "Add the review trigger", commitSubject: "feat: trigger the review");
        harness.Repo.WriteFile("new.txt", "x\n");
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        var call = Assert.Single(messenger.Calls);
        Assert.Equal(StepCommitHarness.SessionId, call.SessionId);
        Assert.Contains($"step_id \"{item.Id}\"", call.Message, StringComparison.Ordinal);
        Assert.Contains("request_review", call.Message, StringComparison.Ordinal);
        Assert.Contains("feat: trigger the review", call.Message, StringComparison.Ordinal);
        Assert.Equal(Origin, call.OriginKey);
        Assert.Equal(StepCommitOutcome.Committed, result.Outcome);
        Assert.Equal(result.CommitId, await harness.Repo.HeadAsync());
    }

    [Fact]
    public async Task The_message_is_english_model_input_and_says_a_request_changes_verdict_is_not_a_finished_step()
    {
        var message = StepCommitter.BuildReviewRequest(
            new TodoItem("step-1", "session", "Wire the trigger", TodoStatus.Done, null, 0, DateTimeOffset.UnixEpoch,
                CommitSubject: "feat: wire the trigger"));

        Assert.Contains("step_id \"step-1\"", message, StringComparison.Ordinal);
        Assert.Contains("(step: feat: wire the trigger)", message, StringComparison.Ordinal);
        Assert.Contains("do not report the step as done while a blocking finding stands", message, StringComparison.Ordinal);
        Assert.Contains("request_review again at the new HEAD", message, StringComparison.Ordinal);
        Assert.DoesNotContain("评审", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_with_neither_a_commit_subject_nor_a_title_still_names_its_id()
    {
        var message = StepCommitter.BuildReviewRequest(
            new TodoItem("step-2", "session", "   ", TodoStatus.Done, null, 0, DateTimeOffset.UnixEpoch));

        Assert.Contains("step_id \"step-2\"", message, StringComparison.Ordinal);
        Assert.DoesNotContain("(step: ", message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ nothing else changes the commit

    [Fact]
    public async Task A_step_that_committed_nothing_asks_for_no_review()
    {
        var messenger = new RecordingMessenger();
        await using var harness = await StepCommitHarness.CreateAsync(
            reviewSettings: () => new StepReviewSettings(true, true),
            messenger: messenger);
        var item = await harness.AddDoneAsync();
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.NoChanges, result.Outcome);
        Assert.Empty(messenger.Calls);
    }

    [Fact]
    public async Task A_failed_commit_asks_for_no_review()
    {
        var messenger = new RecordingMessenger();
        await using var harness = await StepCommitHarness.CreateAsync(
            reviewSettings: () => new StepReviewSettings(true, true),
            messenger: messenger);
        var item = await harness.AddDoneAsync();
        harness.Repo.WriteFile("new.txt", "x\n");
        await harness.Repo.GitAsync("checkout", "-q", "--detach");
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Failed, result.Outcome);
        Assert.Empty(messenger.Calls);
    }

    [Fact]
    public async Task A_hand_off_that_throws_is_announced_once_and_the_commit_still_stands()
    {
        var messenger = new RecordingMessenger { Failure = new InvalidOperationException("the session view is gone") };
        var notifications = new RecordingNotifications();
        await using var harness = await StepCommitHarness.CreateAsync(
            reviewSettings: () => new StepReviewSettings(true, true),
            messenger: messenger,
            notifications: notifications);
        var item = await harness.AddDoneAsync();
        harness.Repo.WriteFile("new.txt", "x\n");
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Committed, result.Outcome);
        var call = Assert.Single(notifications.Calls);
        Assert.Equal(NotificationLevel.Warning, call.Level);
        Assert.Equal(GitStrings.NotificationReviewHandOffFailed, call.TitleKey);
        Assert.Equal("step-review:" + item.Id, call.Key);

        // The commit is real and recorded: the review request is what failed, not the step.
        Assert.Equal(result.CommitId, await harness.Repo.HeadAsync());
        Assert.Equal(result.CommitId, (await harness.StoredAsync(item.Id)).CommitId);
    }

    [Fact]
    public async Task A_request_that_was_queued_in_the_composer_returns_the_commit_unchanged()
    {
        var messenger = new RecordingMessenger { Result = false };
        var notifications = new RecordingNotifications();
        await using var harness = await StepCommitHarness.CreateAsync(
            reviewSettings: () => new StepReviewSettings(true, true),
            messenger: messenger,
            notifications: notifications);
        var item = await harness.AddDoneAsync();
        harness.Repo.WriteFile("new.txt", "x\n");
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Committed, result.Outcome);
        Assert.Single(messenger.Calls);
        // Busy session, message queued: the designed path, not something to announce.
        Assert.Empty(notifications.Calls);
    }

    [Fact]
    public async Task A_committer_without_a_messenger_commits_exactly_as_before()
    {
        var preferences = new AppPreferences { ReviewAfterStepCommit = true };
        await using var harness = await StepCommitHarness.CreateAsync(reviewSettings: StepReviewSettings.Live(preferences));
        var item = await harness.AddDoneAsync();
        harness.Repo.WriteFile("new.txt", "x\n");
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Committed, result.Outcome);
        Assert.Equal(result.CommitId, await harness.Repo.HeadAsync());
    }

    [Fact]
    public async Task The_preferences_are_read_at_each_commit_so_switching_the_switch_on_applies_to_the_next_one()
    {
        var preferences = new AppPreferences { ReviewAfterStepCommit = false, ReviewMode = "subagent" };
        var messenger = new RecordingMessenger();
        await using var harness = await StepCommitHarness.CreateAsync(
            reviewSettings: StepReviewSettings.Live(preferences),
            messenger: messenger);
        var first = await harness.AddDoneAsync("first");
        var second = await harness.AddDoneAsync("second");
        using var cts = Bound();

        harness.Repo.WriteFile("a.txt", "a\n");
        var off = await harness.Committer.CommitStepAsync(first, cts.Token);

        // No restart, no new committer: the same instance sees the switch the user just flipped.
        preferences.ReviewAfterStepCommit = true;
        harness.Repo.WriteFile("b.txt", "b\n");
        var on = await harness.Committer.CommitStepAsync(second, cts.Token);

        Assert.Equal(StepCommitOutcome.Committed, off.Outcome);
        Assert.Equal(StepCommitOutcome.Committed, on.Outcome);
        var call = Assert.Single(messenger.Calls);
        Assert.Contains($"step_id \"{second.Id}\"", call.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ the settings pair

    [Theory]
    [InlineData("subagent", true)]
    [InlineData("manual", false)]
    [InlineData("bogus", true)]
    [InlineData("", true)]
    public void The_settings_pair_wants_a_review_only_for_the_sub_agent_mode(string reviewMode, bool wants)
    {
        var settings = new StepReviewSettings(AfterStepCommit: true, SubAgentMode: !string.Equals(reviewMode, StepReviewSettings.ManualModeToken, StringComparison.Ordinal));

        Assert.Equal(wants, settings.WantsReview);
        Assert.False(new StepReviewSettings(AfterStepCommit: false, SubAgentMode: true).WantsReview);
    }

    [Fact]
    public void The_live_reader_takes_the_two_preferences_and_an_unknown_mode_counts_as_the_default()
    {
        var preferences = new AppPreferences { ReviewAfterStepCommit = true, ReviewMode = "manual" };
        var live = StepReviewSettings.Live(preferences);

        Assert.False(live().WantsReview);

        preferences.ReviewMode = "not-a-mode";
        Assert.Equal(AppPreferences.DefaultReviewMode, preferences.ReviewMode);
        Assert.True(live().WantsReview);

        preferences.ReviewAfterStepCommit = false;
        Assert.False(live().WantsReview);
    }

    // ------------------------------------------------------------ the messenger the composition root wires

    [Fact]
    public async Task The_messenger_resolves_the_session_and_hands_the_message_over_on_the_ui_thread()
    {
        var host = new FakeHost();
        var ui = new InlineUiThread();
        var messenger = new StepReviewMessenger(
            sessionId => string.Equals(sessionId, "s1", StringComparison.Ordinal) ? host : null,
            ui);

        var ran = await messenger.HandAsync("s1", "the message", new LocText(Origin));

        Assert.True(ran);
        Assert.Equal(1, ui.Calls);
        var handed = Assert.Single(host.Handed);
        Assert.Equal("the message", handed.Message);
        Assert.Equal(Origin, handed.OriginKey);
    }

    [Fact]
    public async Task The_messenger_reports_false_for_a_session_that_is_not_open_or_can_no_longer_take_messages()
    {
        var ui = new InlineUiThread();
        var closed = new FakeHost { CanHandOff = false };
        var messenger = new StepReviewMessenger(
            sessionId => string.Equals(sessionId, "closed", StringComparison.Ordinal) ? closed : null,
            ui);

        Assert.False(await messenger.HandAsync("not-open", "the message", new LocText(Origin)));
        Assert.False(await messenger.HandAsync("closed", "the message", new LocText(Origin)));
        Assert.Empty(closed.Handed);
    }

    [Fact]
    public async Task The_messenger_reports_what_the_session_did_with_the_message()
    {
        var idle = new FakeHost { Result = true };
        var busy = new FakeHost { Result = false };
        var ui = new InlineUiThread();

        Assert.True(await new StepReviewMessenger(_ => idle, ui).HandAsync("s1", "m", new LocText(Origin)));
        Assert.False(await new StepReviewMessenger(_ => busy, ui).HandAsync("s1", "m", new LocText(Origin)));
    }

    // ------------------------------------------------------------ every committed step asks, none of it changes the commit

    [Fact]
    public async Task Each_committed_step_asks_for_its_own_review()
    {
        var messenger = new RecordingMessenger();
        await using var harness = await StepCommitHarness.CreateAsync(
            reviewSettings: () => new StepReviewSettings(true, true),
            messenger: messenger);
        var first = await harness.AddDoneAsync("first");
        var second = await harness.AddDoneAsync("second");
        var third = await harness.AddDoneAsync("third");
        using var cts = Bound();

        harness.Repo.WriteFile("a.txt", "a\n");
        await harness.Committer.CommitStepAsync(first, cts.Token);
        harness.Repo.WriteFile("b.txt", "b\n");
        await harness.Committer.CommitStepAsync(second, cts.Token);
        harness.Repo.WriteFile("c.txt", "c\n");
        await harness.Committer.CommitStepAsync(third, cts.Token);

        Assert.Equal(3, messenger.Calls.Count);
        Assert.Contains(messenger.Calls, static call => call.Message.Contains("step_id \"", StringComparison.Ordinal));
        Assert.Contains(messenger.Calls, call => call.Message.Contains($"step_id \"{first.Id}\"", StringComparison.Ordinal));
        Assert.Contains(messenger.Calls, call => call.Message.Contains($"step_id \"{second.Id}\"", StringComparison.Ordinal));
        Assert.Contains(messenger.Calls, call => call.Message.Contains($"step_id \"{third.Id}\"", StringComparison.Ordinal));
    }

    public static TheoryData<Exception> HandOffFailures() =>
        new()
        {
            new IOException("the session view is gone"),
            new TimeoutException("the dispatcher timed out"),
            new InvalidOperationException("the session was taken over"),
        };

    [Theory]
    [MemberData(nameof(HandOffFailures))]
    public async Task Any_expected_hand_off_failure_is_announced_and_the_commit_still_stands(Exception failure)
    {
        var messenger = new RecordingMessenger { Failure = failure };
        var notifications = new RecordingNotifications();
        await using var harness = await StepCommitHarness.CreateAsync(
            reviewSettings: () => new StepReviewSettings(true, true),
            messenger: messenger,
            notifications: notifications);
        var item = await harness.AddDoneAsync();
        harness.Repo.WriteFile("new.txt", "x\n");
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Committed, result.Outcome);
        Assert.Single(notifications.Calls);
        Assert.Equal(result.CommitId, await harness.Repo.HeadAsync());
        Assert.Equal(result.CommitId, (await harness.StoredAsync(item.Id)).CommitId);
    }

    [Fact]
    public async Task A_cancellation_from_the_hand_off_propagates_but_the_commit_is_already_recorded()
    {
        var messenger = new RecordingMessenger { Failure = new OperationCanceledException("the ui thread is shutting down") };
        await using var harness = await StepCommitHarness.CreateAsync(
            reviewSettings: () => new StepReviewSettings(true, true),
            messenger: messenger);
        var item = await harness.AddDoneAsync();
        harness.Repo.WriteFile("new.txt", "x\n");
        using var cts = Bound();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Committer.CommitStepAsync(item, cts.Token));

        // The commit was persisted before the hand-off, so the cancellation did not lose it.
        var stored = await harness.StoredAsync(item.Id);
        Assert.NotNull(stored.CommitId);
        Assert.Equal(stored.CommitId, await harness.Repo.HeadAsync());
    }

    [Fact]
    public async Task The_hand_off_runs_after_the_gate_is_released_so_a_turn_can_commit_again()
    {
        var messenger = new BlockingMessenger();
        await using var harness = await StepCommitHarness.CreateAsync(
            reviewSettings: () => new StepReviewSettings(true, true),
            messenger: messenger);
        var first = await harness.AddDoneAsync("first");
        var second = await harness.AddDoneAsync("second");
        harness.Repo.WriteFile("one.txt", "1\n");
        using var cts = Bound();

        var firstTask = Task.Run(() => harness.Committer.CommitStepAsync(first, cts.Token), cts.Token);
        await messenger.WaitForEnterAsync();

        // The first commit finished and released the gate; it now waits inside the hand-off. A second commit into the same
        // worktree must be able to take the gate and complete, instead of deadlocking on a gate the first call still held.
        harness.Repo.WriteFile("two.txt", "2\n");
        var secondResult = await harness.Committer.CommitStepAsync(second, cts.Token);
        Assert.Equal(StepCommitOutcome.Committed, secondResult.Outcome);

        messenger.Release();
        var firstResult = await firstTask;
        Assert.Equal(StepCommitOutcome.Committed, firstResult.Outcome);
        Assert.Equal(2, messenger.Calls.Count);
        Assert.Equal(3, await harness.Repo.CommitCountAsync()); // seed + first + second
    }

    [Fact]
    public async Task The_review_switch_is_read_after_the_commit_so_a_mid_commit_change_applies()
    {
        var messenger = new RecordingMessenger();
        var preferences = new AppPreferences { ReviewAfterStepCommit = false, ReviewMode = StepReviewSettings.SubAgentModeToken };
        await using var harness = await StepCommitHarness.CreateAsync(
            reviewSettings: StepReviewSettings.Live(preferences),
            messenger: messenger);
        var item = await harness.AddDoneAsync();
        harness.Repo.WriteFile("new.txt", "x\n");

        var started = Path.Combine(harness.Repo.Root, "hook-" + Guid.NewGuid().ToString("N") + ".started");
        var release = Path.Combine(harness.Repo.Root, "hook-" + Guid.NewGuid().ToString("N") + ".release");
        harness.Repo.InstallHook("pre-commit", $"""
            touch "{started.Replace('\\', '/')}"
            i=0
            while [ ! -f "{release.Replace('\\', '/')}" ]; do
              sleep 0.1
              i=$((i+1))
              if [ $i -gt 600 ]; then
                echo "the test never released the hook" >&2
                exit 1
              fi
            done
            exit 0
            """);

        using var cts = Bound();
        var task = Task.Run(() => harness.Committer.CommitStepAsync(item, cts.Token), cts.Token);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (!File.Exists(started))
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("the pre-commit hook never started");
                }

                await Task.Delay(25, cts.Token);
            }

            // The commit is inside the pre-commit hook; flip the switch before the hand-off reads it.
            preferences.ReviewAfterStepCommit = true;
        }
        finally
        {
            File.WriteAllText(release, "go");
        }

        var result = await task;

        Assert.Equal(StepCommitOutcome.Committed, result.Outcome);
        Assert.Single(messenger.Calls);
    }

    // ------------------------------------------------------------ fakes

    /// <summary>One hand-off the trigger made, as the session's own view would have received it.</summary>
    private sealed record HandOff(string SessionId, string Message, string? OriginKey);

    private sealed class RecordingMessenger : IStepReviewMessenger
    {
        private readonly Lock gate = new();
        private readonly List<HandOff> calls = [];

        public IReadOnlyList<HandOff> Calls
        {
            get
            {
                lock (gate)
                {
                    return [.. calls];
                }
            }
        }

        /// <summary>What the session did with the message: true for a turn, false for the composer's draft.</summary>
        public bool Result { get; init; } = true;

        /// <summary>When set, the hand-off throws it, standing in for a session view that failed.</summary>
        public Exception? Failure { get; init; }

        public Task<bool> HandAsync(string sessionId, string message, LocText origin)
        {
            if (Failure is { } failure)
            {
                return Task.FromException<bool>(failure);
            }

            lock (gate)
            {
                calls.Add(new HandOff(sessionId, message, origin.Key));
            }

            return Task.FromResult(Result);
        }
    }

    /// <summary>
    /// Blocks the first hand-off until <see cref="Release"/> so a test can observe what else may proceed while the
    /// commit's review request is being delivered; later hand-offs complete at once.
    /// </summary>
    private sealed class BlockingMessenger : IStepReviewMessenger
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock gate = new();
        private readonly List<HandOff> calls = [];
        private int enteredCount;

        public IReadOnlyList<HandOff> Calls
        {
            get
            {
                lock (gate)
                {
                    return [.. calls];
                }
            }
        }

        public Task<bool> HandAsync(string sessionId, string message, LocText origin)
        {
            bool first;
            lock (gate)
            {
                calls.Add(new HandOff(sessionId, message, origin.Key));
                first = Interlocked.Increment(ref enteredCount) == 1;
            }

            if (!first)
            {
                return Task.FromResult(true);
            }

            entered.TrySetResult();
            return AwaitReleaseAsync();
        }

        private async Task<bool> AwaitReleaseAsync()
        {
            await release.Task.WaitAsync(TimeSpan.FromSeconds(90));
            return true;
        }

        public Task WaitForEnterAsync() => entered.Task.WaitAsync(TimeSpan.FromSeconds(90));

        public void Release() => release.TrySetResult();
    }

    private sealed class FakeHost : IAgentMessageHost
    {
        public bool CanHandOff { get; init; } = true;

        /// <summary>What <see cref="HandMessageAsync"/> answers: true ran a turn, false queued the message.</summary>
        public bool Result { get; init; } = true;

        public List<(string Message, string? OriginKey)> Handed { get; } = [];

        public Task<bool> HandMessageAsync(string message, LocText origin)
        {
            Handed.Add((message, origin.Key));
            return Task.FromResult(Result);
        }
    }

    /// <summary>The UI-thread seam, run inline: the test thread stands in for the dispatcher and counts the marshals.</summary>
    private sealed class InlineUiThread : IUiThread
    {
        public int Calls { get; private set; }

        public Task<T> InvokeAsync<T>(Func<Task<T>> work)
        {
            Calls++;
            return work();
        }

        public void Post(Action action) => action();
    }

    private sealed class RecordingNotifications : INotifications
    {
        private readonly List<(NotificationLevel Level, string? TitleKey, string? Body, string? Key)> calls = [];

        public IReadOnlyList<(NotificationLevel Level, string? TitleKey, string? Body, string? Key)> Calls => calls;

        public Task NotifyAsync(NotificationLevel level, LocText title, LocText body, string? key = null, CancellationToken ct = default)
        {
            calls.Add((level, title.Key, body.Resolve(), key));
            return Task.CompletedTask;
        }
    }
}
