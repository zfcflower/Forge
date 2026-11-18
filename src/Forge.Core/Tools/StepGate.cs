using System.Collections.Concurrent;
using Forge.Abstractions;

namespace Forge.Core.Tools;

/// <summary>Whether the step's worktree holds changes the gate has to judge.</summary>
public enum StepChangeState
{
    /// <summary>
    /// Nothing of this step's own to judge: a general <c>scratch/</c> session, a sub-agent session (it shares its parent's
    /// worktree, so the parent's own steps own those changes), or a folder that is not a git repository. These sessions
    /// never commit a step, so the gate has nothing to require from them.
    /// </summary>
    NotApplicable,

    /// <summary><c>git status</c> is empty, untracked files included, so the step left no uncommitted change behind.</summary>
    Clean,

    /// <summary>The worktree has uncommitted changes: the step's code is written but not committed.</summary>
    Dirty,

    /// <summary>The probe could not answer (git failed, or the session's path could not be resolved); the gate fails closed.</summary>
    Unknown,
}

/// <summary>
/// The probe's answer; <see cref="Detail"/> carries the reason when the state is <see cref="StepChangeState.Unknown"/> (or
/// anything the caller should tell the model). <see cref="Tree"/> is the content fingerprint of the whole worktree
/// (tracked and untracked, ignore rules honoured) for <see cref="StepChangeState.Clean"/> and
/// <see cref="StepChangeState.Dirty"/> answers; it is null for the others.
/// </summary>
public sealed record StepChangeProbe(StepChangeState State, string? Detail = null, string? Tree = null);

/// <summary>
/// The only git capability the step gate holds. Core has no git: the app implements this over its own git layer and hands
/// it to <c>AgentHostOptions.StepChanges</c>. A host without an implementation has no gate.
/// </summary>
public interface IStepChangeProbe
{
    /// <summary>
    /// Reads whether <paramref name="sessionId"/>'s step worktree holds changes the gate has to judge. Expected failures
    /// come back as <see cref="StepChangeState.Unknown"/> with a model-readable detail; only cancellation throws.
    /// </summary>
    /// <param name="sessionId">The session whose worktree the step belongs to.</param>
    /// <param name="workingDirectory">The calling session's own directory, for a probe that has no session-side worktree to look at.</param>
    /// <param name="ct">Cancels the probe; a cancellation throws instead of answering.</param>
    Task<StepChangeProbe> ProbeAsync(string sessionId, string workingDirectory, CancellationToken ct);
}

/// <summary>
/// One step's last review verdict, kept per host (never persisted, never shown in the plan panel). <see cref="Tree"/> is the
/// content fingerprint of the worktree the reviewer was shown; null when it could not be taken, which no later check accepts.
/// </summary>
public sealed record StepReviewRecord(ReviewVerdict Verdict, string? CommitId, DateTimeOffset At, string? Tree = null);

/// <summary>One persisted ledger entry: a step's last verdict and its count of non-approving rounds.</summary>
public sealed record StepReviewEntry(string StepId, StepReviewRecord Record, int Rounds);

/// <summary>
/// Where a <see cref="StepReviewLedger"/> keeps its entries across host restarts. Implementations may throw; the ledger
/// treats a failed load as "no entries" (the safe side: the step is reviewed again) and a failed write as lost.
/// </summary>
public interface IStepReviewStore
{
    /// <summary>Every entry of one session.</summary>
    Task<IReadOnlyList<StepReviewEntry>> LoadAsync(string sessionId, CancellationToken ct);

    /// <summary>Inserts or replaces one step's entry.</summary>
    Task SaveAsync(string sessionId, StepReviewEntry entry, CancellationToken ct);

    /// <summary>Removes one step's entry; a missing one is not an error.</summary>
    Task DeleteAsync(string sessionId, string stepId, CancellationToken ct);
}

/// <summary>
/// The review verdicts of one host, keyed by session and step. <c>request_review</c> records one when a step review
/// finishes, the gate reads it when that step is marked done, and a step that leaves done forgets its entry, so its fix has
/// to be reviewed again. Memory is the authority while the host runs. With a <see cref="IStepReviewStore"/> every change is
/// also written behind, in order, and <see cref="EnsureLoadedAsync"/> brings a session's stored entries in before they are
/// read, so a restarted host keeps its verdicts; without one a restart knows none and every step is reviewed once more,
/// which is the safe side. Thread-safe.
/// </summary>
public sealed class StepReviewLedger
{
    private readonly ConcurrentDictionary<(string SessionId, string StepId), StepReviewRecord> records = new();
    private readonly ConcurrentDictionary<(string SessionId, string StepId), int> rounds = new();
    private readonly IStepReviewStore? store;
    private readonly Dictionary<string, Task> loads = new(StringComparer.Ordinal);
    private readonly HashSet<(string SessionId, string StepId)> touched = [];
    private readonly object chainLock = new();
    private Task chain = Task.CompletedTask;

    /// <param name="store">Persists the entries; null keeps them in memory only.</param>
    public StepReviewLedger(IStepReviewStore? store = null)
    {
        this.store = store;
    }

    /// <summary>Completes when every write and load queued so far has finished (tests, shutdown).</summary>
    public Task FlushAsync()
    {
        lock (chainLock)
        {
            return chain;
        }
    }

    /// <summary>
    /// Brings the stored entries of <paramref name="sessionId"/> into memory once. A step recorded or forgotten in this host
    /// is never overwritten by its stored row (the host's own change is newer), and the load runs behind every write already
    /// queued, so it cannot read a row that is about to be deleted. A failing store leaves the session without stored entries
    /// and is retried by the next call. Waiting can be cancelled; the load itself still completes.
    /// </summary>
    public async Task EnsureLoadedAsync(string sessionId, CancellationToken ct)
    {
        if (store is null || !IsUsable(sessionId))
        {
            return;
        }

        Task load;
        lock (chainLock)
        {
            if (!loads.TryGetValue(sessionId, out var existing))
            {
                var created = EnqueueLocked(() => LoadAsync(sessionId));
                loads[sessionId] = created;
                existing = created;
            }

            load = existing;
        }

        await load.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task LoadAsync(string sessionId)
    {
        IReadOnlyList<StepReviewEntry> entries;
        try
        {
            entries = await store!.LoadAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The store is unreachable: treat the session as having no stored entries now, and try again on the next call.
            lock (chainLock)
            {
                loads.Remove(sessionId);
            }

            return;
        }

        lock (chainLock)
        {
            foreach (var entry in entries)
            {
                var key = (sessionId, entry.StepId);
                if (touched.Contains(key))
                {
                    continue;
                }

                records[key] = entry.Record;
                if (entry.Rounds > 0)
                {
                    rounds[key] = entry.Rounds;
                }
            }
        }
    }

    private Task EnqueueLocked(Func<Task> work)
    {
        chain = chain.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        return chain;
    }

    private void PersistLocked(string sessionId, string stepId, StepReviewEntry? entry)
    {
        touched.Add((sessionId, stepId));
        if (store is null)
        {
            return;
        }

        EnqueueLocked(async () =>
        {
            try
            {
                if (entry is null)
                {
                    await store.DeleteAsync(sessionId, stepId, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await store.SaveAsync(sessionId, entry, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // A lost write only means the step is reviewed again after a restart.
            }
        });
    }

    /// <summary>
    /// Stores the verdict of a finished step review. A blank session or step id is ignored (such a call cannot be matched to
    /// a step later), and so is a blank commit id, which stays <c>null</c>.
    /// </summary>
    public void Record(string sessionId, string stepId, ReviewVerdict verdict, string? commitId, string? tree = null)
    {
        if (!IsUsable(sessionId) || !IsUsable(stepId))
        {
            return;
        }

        var record = new StepReviewRecord(
            verdict,
            string.IsNullOrWhiteSpace(commitId) ? null : commitId,
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(tree) ? null : tree);

        // The memory change and the queued write happen under one lock, so the database sees the same order as memory.
        lock (chainLock)
        {
            records[(sessionId, stepId)] = record;
            int count;
            if (verdict == ReviewVerdict.Approve)
            {
                rounds.TryRemove((sessionId, stepId), out _);
                count = 0;
            }
            else
            {
                count = rounds.AddOrUpdate((sessionId, stepId), 1, static (_, existing) => existing + 1);
            }

            PersistLocked(sessionId, stepId, new StepReviewEntry(stepId, record, count));
        }
    }

    /// <summary>
    /// How many finished non-approving reviews that step has had since it was last approved or forgotten; the auto-fix loop
    /// compares it with <see cref="ReviewFixPolicy.MaxRounds"/>. Call after <see cref="Record"/>.
    /// </summary>
    public int Rounds(string sessionId, string stepId) =>
        IsUsable(sessionId) && IsUsable(stepId) && rounds.TryGetValue((sessionId, stepId), out var count) ? count : 0;

    /// <summary>Drops the verdict of one step, so it needs another review before it can be done again.</summary>
    public void Forget(string sessionId, string stepId)
    {
        if (!IsUsable(sessionId) || !IsUsable(stepId))
        {
            return;
        }

        lock (chainLock)
        {
            records.TryRemove((sessionId, stepId), out _);
            rounds.TryRemove((sessionId, stepId), out _);
            PersistLocked(sessionId, stepId, null);
        }
    }

    /// <summary>
    /// The last recorded verdict of that step, or null when it has none: never reviewed in this host, forgotten after the
    /// step left done, or a restarted host.
    /// </summary>
    public StepReviewRecord? Find(string sessionId, string stepId)
    {
        if (!IsUsable(sessionId) || !IsUsable(stepId))
        {
            return null;
        }

        return records.TryGetValue((sessionId, stepId), out var record) ? record : null;
    }

    private static bool IsUsable(string? value) => !string.IsNullOrWhiteSpace(value);
}

/// <summary>
/// The gate's verdict for one attempt to mark a step done. <see cref="Reason"/> says why the step was refused and
/// <see cref="NextStep"/> what the model has to do instead; both are model-facing English text, never localized.
/// </summary>
public sealed record StepGateDecision(bool Allowed, string? Reason = null, string? NextStep = null)
{
    /// <summary>The step may be marked done.</summary>
    public static StepGateDecision Allow { get; } = new(true);
}

/// <summary>
/// The mechanical gate on marking a checklist step done: a step that changed code needs an approving review verdict and a
/// real verification, while a step that changed nothing is let through. Expected refusals are
/// <see cref="StepGateDecision"/> values, never exceptions; only cancellation propagates.
/// <para>Rules, in order. The gate is off when <c>enabled</c> is false or when there is no probe (a host without a git
/// evidence source has no gate). A probe answer of <see cref="StepChangeState.NotApplicable"/> lets the step through, an
/// <see cref="StepChangeState.Unknown"/> refuses (fail closed), and a <see cref="StepChangeState.Clean"/> worktree lets a
/// step through only when the step has no commit of its own - it then changed no code at all. Everything else (a dirty
/// worktree, or a step that already committed) needs an <see cref="ReviewVerdict.Approve"/> entry in the
/// <see cref="StepReviewLedger"/> and a verification that says more than "not verified".</para>
/// <para>Content binding. A verdict is recorded together with the worktree fingerprint the reviewer was shown
/// (<see cref="StepChangeProbe.Tree"/>), and the gate accepts it only while the worktree still has that fingerprint: a change
/// after the review, committed or not, makes the verdict stale and the step needs another review. A step that went in
/// progress also carries the fingerprint it started from (<see cref="TodoItem.BaselineTree"/>, taken by
/// <see cref="CaptureBaselineAsync"/>): a worktree equal to it changed nothing, whatever git status says, and a clean
/// worktree that differs from it holds code committed outside the step's own commit, which is then gated like any other.</para>
/// <para>Remaining limits. A step that was never put in progress has no baseline and is judged by git status and its own commit
/// only. The ledger is stored (<see cref="IStepReviewStore"/>), so a restarted host keeps its verdicts. A file the build or the tests write into the
/// worktree after the review (outside the ignore rules) changes the fingerprint and makes the verdict stale.</para>
/// </summary>
public sealed class StepGate
{
    /// <summary>The one verification value that carries no evidence: it is the form's placeholder, not a result.</summary>
    private const string NotVerified = "not verified";

    private const int ShortCommitLength = 7;

    private readonly IStepChangeProbe? probe;
    private readonly Func<bool>? enabled;

    /// <param name="ledger">Where the step review verdicts live; shared with the host's <c>request_review</c> tool.</param>
    /// <param name="probe">The git evidence source; null means this host has no gate.</param>
    /// <param name="enabled">Reads the "step completion requires a review" preference (<c>stepReviewRequired</c>) on every check; null means the gate is on whenever <paramref name="probe"/> is set.</param>
    public StepGate(StepReviewLedger ledger, IStepChangeProbe? probe = null, Func<bool>? enabled = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        Ledger = ledger;
        this.probe = probe;
        this.enabled = enabled;
    }

    /// <summary>The verdicts this gate reads; the host hands the same instance to <c>request_review</c>.</summary>
    public StepReviewLedger Ledger { get; }

    /// <summary>
    /// The worktree fingerprint to store as the baseline of a step that is going in progress, or null when the gate is off,
    /// has no probe, or the probe cannot tell (not applicable, unknown, an exception): a step without a baseline is judged by
    /// the older rules. Only cancellation throws.
    /// </summary>
    public async Task<string?> CaptureBaselineAsync(string sessionId, string workingDirectory, CancellationToken ct)
    {
        if (probe is null || (enabled is not null && !enabled()))
        {
            return null;
        }

        try
        {
            var answer = await probe.ProbeAsync(sessionId, workingDirectory, ct).ConfigureAwait(false);
            return answer.State is StepChangeState.Clean or StepChangeState.Dirty ? answer.Tree : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Judges one attempt to mark <paramref name="step"/> done. <paramref name="verification"/> is the value the step will
    /// hold after this call - the argument of the update, or the stored one when the call does not set it; null or blank
    /// counts as no evidence. <paramref name="workingDirectory"/> is the calling session's directory, used by the probe for
    /// the cases where the session has no worktree of its own.
    /// </summary>
    public async Task<StepGateDecision> CheckAsync(
        TodoItem step,
        string? verification,
        string workingDirectory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (enabled is not null && !enabled())
        {
            return StepGateDecision.Allow;
        }

        if (probe is null)
        {
            return StepGateDecision.Allow;
        }

        StepChangeProbe change;
        try
        {
            change = await probe.ProbeAsync(step.SessionId, workingDirectory, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A probe that throws is a probe that could not tell: the same answer as an explicit Unknown, and the message
            // is the only thing the model can act on.
            change = new StepChangeProbe(StepChangeState.Unknown, exception.Message);
        }

        switch (change.State)
        {
            case StepChangeState.NotApplicable:
                return StepGateDecision.Allow;
            case StepChangeState.Unknown:
                return Unproven(change.Detail);
            case StepChangeState.Clean when string.IsNullOrWhiteSpace(step.CommitId) && step.BaselineTree is null:
                // No uncommitted work is attributed to this step and it owns no commit of its own, so there is nothing here to
                // review or verify. That is the intended path for a check, an analysis or a plan edit - and, by the same rule,
                // for a step without a baseline whose code was committed outside the step machinery; see the class-level limits.
                return StepGateDecision.Allow;
        }

        if (change.State == StepChangeState.Clean && step.BaselineTree is { } baseline && change.Tree is { } current && string.Equals(baseline, current, StringComparison.Ordinal))
        {
            // Clean and exactly what it was when the step started: nothing of this step is left to judge. A Dirty worktree never
            // takes this shortcut: edits made before the step started are in the baseline, and the step's commit would take them in.
            return StepGateDecision.Allow;
        }

        var changeText = DescribeChange(change.State, step);
        await Ledger.EnsureLoadedAsync(step.SessionId, ct).ConfigureAwait(false);
        var record = Ledger.Find(step.SessionId, step.Id);
        if (record is null)
        {
            return new StepGateDecision(
                false,
                $"Step #{step.Id} cannot be marked done yet: {changeText}, and the step has no review verdict.",
                "Call request_review with {\"step_id\":\"" + step.Id + "\"}, act on its findings, and mark the step done only once its verdict line reads verdict: approve.");
        }

        if (record.Verdict != ReviewVerdict.Approve)
        {
            return new StepGateDecision(
                false,
                $"Step #{step.Id} cannot be marked done yet: {changeText}, and its last review is not an approval ({DescribeVerdict(record.Verdict)}).",
                "Fix what that review reported, commit the fix, then call request_review with the same step_id at the new HEAD; only an approve verdict lets the step be done.");
        }

        if (record.Tree is null || change.Tree is null || !string.Equals(record.Tree, change.Tree, StringComparison.Ordinal))
        {
            return new StepGateDecision(
                false,
                $"Step #{step.Id} cannot be marked done yet: {changeText}, and the worktree is no longer what its approving review saw"
                + (record.Tree is null ? " (that review recorded no content fingerprint)." : " (the content changed after the review)."),
                "Call request_review with the same step_id again so the review covers the current content, act on its findings, and mark the step done only once its verdict line reads verdict: approve.");
        }

        if (!IsVerified(verification))
        {
            return new StepGateDecision(
                false,
                $"Step #{step.Id} cannot be marked done yet: {changeText}, and its verification is missing or still says \"{NotVerified}\".",
                "Run the project's own checks (build, tests, lint), then pass what you ran and what came back as check → result in the verification argument of this update, and mark the step done again.");
        }

        return StepGateDecision.Allow;
    }

    /// <summary>The refusal of a probe that could not tell whether the step changed code: fail closed, and say what broke.</summary>
    private static StepGateDecision Unproven(string? detail) =>
        new(
            false,
            "Step refused: the gate could not tell whether this step changed code"
            + (string.IsNullOrWhiteSpace(detail) ? "." : ": " + detail.Trim()),
            "Repair the git problem named above (for example a broken repository, an unreadable worktree or a git that does not run), then call update with status done again.");

    /// <summary>Which evidence made the step gateable: uncommitted work, or the step's own commit.</summary>
    private static string DescribeChange(StepChangeState state, TodoItem step)
    {
        if (state == StepChangeState.Dirty)
        {
            return "the step's worktree has uncommitted changes";
        }

        if (string.IsNullOrWhiteSpace(step.CommitId))
        {
            return "the worktree content differs from where the step started, although nothing is uncommitted (code committed outside the step's own commit)";
        }

        var commit = step.CommitId ?? string.Empty;
        return "the step has its own commit " + commit[..Math.Min(ShortCommitLength, commit.Length)];
    }

    /// <summary>The actual verdict of the last review, named the way the reviewer wrote it.</summary>
    private static string DescribeVerdict(ReviewVerdict verdict) => verdict switch
    {
        ReviewVerdict.Approve => "verdict: approve",
        ReviewVerdict.RequestChanges => "verdict: request changes",
        _ => "no verdict line at all",
    };

    /// <summary>
    /// Whether <paramref name="verification"/> carries evidence. The form's placeholder counts as none, with or without a
    /// closing period and in any casing.
    /// </summary>
    private static bool IsVerified(string? verification)
    {
        if (string.IsNullOrWhiteSpace(verification))
        {
            return false;
        }

        var value = verification.Trim().TrimEnd('.').TrimEnd();
        return !string.Equals(value, NotVerified, StringComparison.OrdinalIgnoreCase);
    }
}