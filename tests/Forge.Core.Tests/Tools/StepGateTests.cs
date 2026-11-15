using Forge.Abstractions;
using Forge.Core.Tools;
using Xunit;

namespace Forge.Core.Tests.Tools;

/// <summary>
/// The step completion gate (<see cref="StepGate"/>): the rule order that decides whether a checklist step may be marked
/// done, from the switch and the probe's answer to the review verdict and the verification. Every refusal is a decision,
/// never an exception; only cancellation propagates.
/// </summary>
public sealed class StepGateTests
{
    private const string Session = "sess-a";

    private const string WorkingDirectory = "work/tree";

    private static TodoItem Step(string id = "s1", string? commitId = null) =>
        new(id, Session, "Wire the gate", TodoStatus.InProgress, null, 1, DateTimeOffset.UnixEpoch, CommitId: commitId);

    /// <summary>The probe as the gate sees it: one scripted answer, or a throw, and every call recorded.</summary>
    private sealed class ScriptedProbe : IStepChangeProbe
    {
        private int calls;

        public Func<string, StepChangeProbe>? Answer { get; set; }

        public Exception? Throw { get; set; }

        public int Calls => Volatile.Read(ref calls);

        public Task<StepChangeProbe> ProbeAsync(string sessionId, string workingDirectory, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            if (Throw is { } failure)
            {
                return Task.FromException<StepChangeProbe>(failure);
            }

            return Task.FromResult(Answer!(sessionId));
        }
    }

    private static Task<StepGateDecision> CheckAsync(
        TodoItem step,
        string? verification,
        IStepChangeProbe? probe = null,
        Func<bool>? enabled = null,
        StepReviewLedger? ledger = null) =>
        new StepGate(ledger ?? new StepReviewLedger(), probe, enabled)
            .CheckAsync(step, verification, WorkingDirectory, CancellationToken.None);

    // ---------------------------------------------------------------- the gate is off

    [Fact]
    public async Task A_disabled_gate_lets_every_step_through_without_reading_the_probe()
    {
        var probe = new ScriptedProbe { Answer = _ => new StepChangeProbe(StepChangeState.Dirty) };

        // Dirty, no verdict, no verification: everything the gate refuses is let through because the preference is off.
        var decision = await CheckAsync(Step(), verification: null, probe, enabled: () => false);

        Assert.True(decision.Allowed);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task A_gate_without_a_probe_lets_every_step_through()
    {
        var decision = await CheckAsync(Step(commitId: "abcdef1234567890"), verification: null, probe: null);

        Assert.True(decision.Allowed);
    }

    // ---------------------------------------------------------------- the probe's answers

    [Theory]
    [InlineData(null)]   // a step that committed nothing and has no evidence at all
    [InlineData("abcdef1234567890")]   // a step with a commit of its own: still not this gate's business
    public async Task A_not_applicable_session_is_never_gated(string? commitId)
    {
        var decision = await CheckAsync(Step(commitId: commitId), verification: null, probe: new ScriptedProbe
        {
            Answer = _ => new StepChangeProbe(StepChangeState.NotApplicable, "sub-agent session"),
        });

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task An_unknown_answer_fails_closed_and_carries_the_detail()
    {
        var decision = await CheckAsync(Step(), verification: null, probe: new ScriptedProbe
        {
            Answer = _ => new StepChangeProbe(StepChangeState.Unknown, "git could not run"),
        });

        Assert.False(decision.Allowed);
        Assert.Contains("could not tell", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("git could not run", decision.Reason, StringComparison.Ordinal);
        Assert.NotNull(decision.NextStep);
    }

    [Fact]
    public async Task A_probe_that_throws_is_answered_as_unknown_with_its_message()
    {
        var decision = await CheckAsync(Step(), verification: null, probe: new ScriptedProbe
        {
            Throw = new InvalidOperationException("the worktree is unreadable"),
        });

        Assert.False(decision.Allowed);
        Assert.Contains("the worktree is unreadable", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_probe_propagates_instead_of_answering()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CheckAsync(Step(), verification: null, probe: new ScriptedProbe
            {
                Throw = new OperationCanceledException(),
            }));
    }

    // ---------------------------------------------------------------- a clean worktree

    [Theory]
    [InlineData(null, true)]    // nothing was changed by this step: nothing to review, nothing to verify
    [InlineData("abcdef1234567890", false)]    // the step already committed: that commit needs a review
    public async Task A_clean_worktree_passes_only_when_the_step_committed_nothing(string? commitId, bool expectedAllowed)
    {
        var decision = await CheckAsync(Step(commitId: commitId), verification: null, probe: new ScriptedProbe
        {
            Answer = _ => new StepChangeProbe(StepChangeState.Clean),
        });

        Assert.Equal(expectedAllowed, decision.Allowed);
        if (!expectedAllowed)
        {
            Assert.Contains("its own commit", decision.Reason, StringComparison.Ordinal);
            Assert.Contains("abcdef1", decision.Reason, StringComparison.Ordinal);
            Assert.Contains("request_review", decision.NextStep, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- a dirty worktree needs a review

    [Fact]
    public async Task A_dirty_step_without_a_verdict_is_refused_and_sent_to_request_review()
    {
        var decision = await CheckAsync(Step(id: "step-9"), verification: null, probe: new ScriptedProbe
        {
            Answer = _ => new StepChangeProbe(StepChangeState.Dirty),
        });

        Assert.False(decision.Allowed);
        Assert.Contains("uncommitted changes", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("step-9", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("request_review", decision.NextStep, StringComparison.Ordinal);
        Assert.Contains("step-9", decision.NextStep, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReviewVerdict.RequestChanges)]
    [InlineData(ReviewVerdict.Unstructured)]
    public async Task A_step_whose_last_review_is_not_an_approval_is_refused_with_that_verdict_named(ReviewVerdict verdict)
    {
        var ledger = new StepReviewLedger();
        ledger.Record(Session, "s1", verdict, "abcdef1234567890");
        var decision = await CheckAsync(
            Step(),
            verification: "dotnet test: 10 passed",
            probe: new ScriptedProbe { Answer = _ => new StepChangeProbe(StepChangeState.Dirty) },
            ledger: ledger);

        Assert.False(decision.Allowed);
        Assert.Contains(verdict == ReviewVerdict.RequestChanges ? "request changes" : "no verdict line at all", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not verified")]
    [InlineData("NOT VERIFIED")]
    [InlineData("Not Verified")]
    [InlineData("not verified.")]
    public async Task An_approved_step_without_real_evidence_is_refused(string? verification)
    {
        var ledger = new StepReviewLedger();
        ledger.Record(Session, "s1", ReviewVerdict.Approve, "abcdef1234567890");
        var decision = await CheckAsync(
            Step(),
            verification,
            probe: new ScriptedProbe { Answer = _ => new StepChangeProbe(StepChangeState.Dirty) },
            ledger: ledger);

        Assert.False(decision.Allowed);
        Assert.Contains("verification", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_approved_step_with_real_evidence_is_let_through()
    {
        var ledger = new StepReviewLedger();
        ledger.Record(Session, "s1", ReviewVerdict.Approve, "abcdef1234567890");
        var decision = await CheckAsync(
            Step(),
            verification: "dotnet test Forge.Core.Tests: 12 passed, 0 failed",
            probe: new ScriptedProbe { Answer = _ => new StepChangeProbe(StepChangeState.Dirty) },
            ledger: ledger);

        Assert.True(decision.Allowed);
    }
}

/// <summary>
/// The verdicts one host remembers per (session, step): what Record keeps, what Find answers, what Forget drops, and the
/// keying that keeps two sessions' steps apart.
/// </summary>
public sealed class StepReviewLedgerTests
{
    [Fact]
    public void Record_Find_and_Forget_round_trip()
    {
        var ledger = new StepReviewLedger();
        ledger.Record("sess-a", "s1", ReviewVerdict.Approve, "abcdef1234567890");

        var found = ledger.Find("sess-a", "s1");
        Assert.NotNull(found);
        Assert.Equal(ReviewVerdict.Approve, found.Verdict);
        Assert.Equal("abcdef1234567890", found.CommitId);

        ledger.Forget("sess-a", "s1");
        Assert.Null(ledger.Find("sess-a", "s1"));
    }

    [Theory]
    [InlineData("")]        // a blank session id cannot be matched later
    [InlineData("   ")]
    public void A_record_with_an_unusable_session_id_is_ignored(string sessionId)
    {
        var ledger = new StepReviewLedger();

        ledger.Record(sessionId, "s1", ReviewVerdict.Approve, "abcdef1234567890");

        Assert.Null(ledger.Find(sessionId, "s1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_record_with_an_unusable_step_id_is_ignored(string stepId)
    {
        var ledger = new StepReviewLedger();

        ledger.Record("sess-a", stepId, ReviewVerdict.Approve, "abcdef1234567890");

        Assert.Null(ledger.Find("sess-a", stepId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_commit_id_is_stored_as_null(string? commitId)
    {
        var ledger = new StepReviewLedger();

        ledger.Record("sess-a", "s1", ReviewVerdict.Approve, commitId);

        Assert.Null(ledger.Find("sess-a", "s1")!.CommitId);
    }

    [Fact]
    public void A_forget_with_unusable_ids_drops_nothing()
    {
        var ledger = new StepReviewLedger();
        ledger.Record("sess-a", "s1", ReviewVerdict.Approve, null);

        ledger.Forget("", "s1");
        ledger.Forget("sess-a", " ");

        Assert.NotNull(ledger.Find("sess-a", "s1"));
    }

    [Fact]
    public void Entries_are_keyed_by_session_and_step_and_do_not_interfere()
    {
        var ledger = new StepReviewLedger();
        ledger.Record("sess-a", "s1", ReviewVerdict.Approve, "aaaaaaa1234567890");
        ledger.Record("sess-a", "s2", ReviewVerdict.RequestChanges, null);
        ledger.Record("sess-b", "s1", ReviewVerdict.Unstructured, null);

        Assert.Equal(ReviewVerdict.Approve, ledger.Find("sess-a", "s1")!.Verdict);
        Assert.Equal(ReviewVerdict.RequestChanges, ledger.Find("sess-a", "s2")!.Verdict);
        Assert.Equal(ReviewVerdict.Unstructured, ledger.Find("sess-b", "s1")!.Verdict);
        Assert.Null(ledger.Find("sess-c", "s1"));
        Assert.Null(ledger.Find("sess-a", "s3"));
    }
}
