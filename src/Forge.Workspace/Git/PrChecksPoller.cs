namespace Forge.Workspace;

/// <summary>
/// How a wait on a pull request's checks ended. Everything except <see cref="TimedOut"/> and <see cref="Failed"/> is a
/// verdict reached from one observation of the checks and the merge state; the poller never syncs, rebases, re-runs or
/// merges anything – it only reports.
/// </summary>
public enum PrWaitOutcome
{
    /// <summary>Merge state CLEAN and every check passed (skipped and neutral count as passed).</summary>
    Ready,

    /// <summary>Merge state HAS_HOOKS and every check passed; pre-receive hooks still run when merging (see <see cref="PrWaitResult.Note"/>).</summary>
    ReadyWithHooks,

    /// <summary>At least one check failed or was cancelled. Reported as soon as it is seen, even while other checks still run.</summary>
    ChecksFailed,

    /// <summary>Checks settled and the base branch has moved ahead (BEHIND).</summary>
    Behind,

    /// <summary>Checks settled and the branch conflicts with the base (DIRTY or mergeable = CONFLICTING).</summary>
    Dirty,

    /// <summary>Checks settled and a requirement other than the checks is unmet (BLOCKED), e.g. a required review.</summary>
    Blocked,

    /// <summary>Checks settled, mergeable, but a non-required status is not passing (UNSTABLE).</summary>
    Unstable,

    /// <summary>Checks settled and the pull request is a draft.</summary>
    Draft,

    /// <summary>No checks were reported on several consecutive polls and nothing else is wrong: the pull request has no CI.</summary>
    NoCi,

    /// <summary>The pull request was merged or closed while waiting, so there is nothing left to wait for.</summary>
    NotOpen,

    /// <summary>The maximum wait elapsed with checks still running or the merge state still UNKNOWN.</summary>
    TimedOut,

    /// <summary>The platform could not be read (auth, network, gh missing) on several consecutive polls.</summary>
    Failed,
}

/// <summary>Timing and confirmation counts of one wait. The defaults are the product defaults; the settings page overrides them.</summary>
public sealed record PrChecksPollerOptions
{
    /// <summary>Time between two polls. Must be positive.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Longest total wait, measured from the first poll. Must be positive.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How many consecutive successful polls must report no checks at all before the wait concludes there is no CI
    /// (a fresh head may simply not have registered its workflows yet). At least 1.
    /// </summary>
    public int NoChecksConfirmations { get; init; } = 3;
}

/// <summary>
/// One reading of a pull request: its checks and its merge state, read together. The two platform calls are not atomic,
/// so a check that finished between them can make the pair momentarily inconsistent; the next poll corrects it.
/// </summary>
public sealed record PrChecksObservation(PrChecksSnapshot Checks, PrMergeState MergeState, DateTimeOffset ObservedAt)
{
    /// <summary>Checks that failed or were cancelled. A cancelled check never counts as passed.</summary>
    public IReadOnlyList<PrCheck> FailingChecks => Select(IsFailing);

    /// <summary>Checks that have not settled: pending, queued, running, or in a state this build cannot classify.</summary>
    public IReadOnlyList<PrCheck> PendingChecks => Select(IsPending);

    /// <summary>Checks that passed or were skipped / neutral / stale (all of which count as passed).</summary>
    public int PassedCount => Checks.Count(PrCheckBucket.Pass) + Checks.Count(PrCheckBucket.Skipping);

    /// <summary>Checks the platform reported as skipped, neutral or stale (a subset of <see cref="PassedCount"/>).</summary>
    public int SkippedCount => Checks.Count(PrCheckBucket.Skipping);

    internal static bool IsFailing(PrCheck check) => check.Bucket is PrCheckBucket.Fail or PrCheckBucket.Cancel;

    internal static bool IsPending(PrCheck check) => check.Bucket is PrCheckBucket.Pending or PrCheckBucket.Unknown;

    private List<PrCheck> Select(Func<PrCheck, bool> predicate)
    {
        var selected = new List<PrCheck>();
        foreach (var check in Checks.Checks)
        {
            if (predicate(check))
            {
                selected.Add(check);
            }
        }

        return selected;
    }
}

/// <summary>
/// The end of a wait. <see cref="Observation"/> is the last successful reading (null only when no read ever succeeded);
/// <see cref="Failure"/> is the failure of the most recent poll when that poll failed – always set for
/// <see cref="PrWaitOutcome.Failed"/>, and possibly set for <see cref="PrWaitOutcome.TimedOut"/> when the last reads failed.
/// <see cref="Note"/> carries a caveat the caller must pass on (the HAS_HOOKS warning), otherwise null.
/// </summary>
public sealed record PrWaitResult(
    PrWaitOutcome Outcome,
    PrChecksObservation? Observation,
    int Polls,
    TimeSpan Elapsed,
    string? Note = null,
    GitRemoteResult? Failure = null,
    PrFailureReason FailureReason = PrFailureReason.None)
{
    /// <summary>The failing checks of the last observation, with the run and job ids <c>pr_ci_log</c> needs.</summary>
    public IReadOnlyList<PrCheck> FailedChecks => Observation?.FailingChecks ?? [];
}

/// <summary>
/// Polls the checks and merge state of a pull request until they settle. Shared by the <c>pr_checks</c> tool (wait mode)
/// and the App's Git panel watcher, so both reach the same verdict from the same observations. Stateless between calls
/// and safe to use from several threads at once; every delay goes through the injected <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// Outcome rules, evaluated on every successful poll in this order:
/// <list type="number">
/// <item>Merge state <c>Merged</c> or <c>Closed</c> → <see cref="PrWaitOutcome.NotOpen"/>.</item>
/// <item>Any check with bucket <c>Fail</c> or <c>Cancel</c> → <see cref="PrWaitOutcome.ChecksFailed"/> at once.</item>
/// <item>
/// No checks at all: keep waiting until <see cref="PrChecksPollerOptions.NoChecksConfirmations"/> consecutive successful
/// polls reported none (a failed read resets the count); after that the empty list counts as settled.
/// </item>
/// <item>
/// Otherwise any check with bucket <c>Pending</c> or <c>Unknown</c> → keep waiting (an unclassifiable check is never
/// treated as passed). A check passes when its bucket is <c>Pass</c> or <c>Skipping</c> (skipped, neutral, stale).
/// </item>
/// <item>
/// Checks settled (all passed, or none and confirmed): mergeable = CONFLICTING or merge state DIRTY → <c>Dirty</c>;
/// BEHIND → <c>Behind</c>; draft flag or merge state DRAFT → <c>Draft</c>; BLOCKED → <c>Blocked</c>; UNSTABLE →
/// <c>Unstable</c>; UNKNOWN → keep waiting; CLEAN → <c>Ready</c> (<c>NoCi</c> when there are no checks); HAS_HOOKS →
/// <c>ReadyWithHooks</c> with the hooks caveat in <see cref="PrWaitResult.Note"/> (<c>NoCi</c> plus the same note when
/// there are no checks).
/// </item>
/// </list>
/// A failed read is retried on the next tick; <see cref="MaxConsecutiveFailures"/> in a row (or the platform CLI not
/// being installed at all) end the wait with <see cref="PrWaitOutcome.Failed"/>. When <see cref="PrChecksPollerOptions.MaxWait"/>
/// has elapsed the wait ends with <see cref="PrWaitOutcome.TimedOut"/> after one last poll at the deadline. Cancellation
/// throws <see cref="OperationCanceledException"/>. The review decision never changes the outcome (a missing required
/// review already shows up as BLOCKED); it is part of the observation for the caller to report.
/// </remarks>
public sealed class PrChecksPoller
{
    /// <summary>Consecutive failed reads after which a wait gives up.</summary>
    public const int MaxConsecutiveFailures = 3;

    /// <summary>The caveat attached to a HAS_HOOKS verdict.</summary>
    public const string HooksCaveat =
        "The merge state is HAS_HOOKS: the pull request is mergeable with passing checks, but pre-receive hooks run when " +
        "it is merged and can still reject the merge. Tell the user; do not assume the merge will succeed.";

    private readonly IPrPlatform platform;
    private readonly TimeProvider time;

    public PrChecksPoller(IPrPlatform platform, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(time);
        this.platform = platform;
        this.time = time;
    }

    /// <summary>
    /// One reading: the checks and the merge state, requested concurrently. Expected failures (auth, network, missing
    /// pull request) come back as a failed result – the first failing call wins; only cancellation throws.
    /// </summary>
    public async Task<PrResult<PrChecksObservation>> ObserveAsync(
        string workingDirectory,
        int number,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);

        var checksTask = platform.GetChecksAsync(workingDirectory, number, ct);
        var mergeTask = platform.GetMergeStateAsync(workingDirectory, number, ct);
        await Task.WhenAll(checksTask, mergeTask).ConfigureAwait(false);

        var checks = await checksTask.ConfigureAwait(false);
        var merge = await mergeTask.ConfigureAwait(false);

        if (!checks.Success || checks.Value is null)
        {
            return Forward(checks, "Could not read the checks.");
        }

        if (!merge.Success || merge.Value is null)
        {
            return Forward(merge, "Could not read the merge state.");
        }

        return PrResult<PrChecksObservation>.Ok(new PrChecksObservation(checks.Value, merge.Value, time.GetUtcNow()));
    }

    /// <summary>
    /// Waits until the pull request's checks reach a verdict (see the type remarks), reporting every successful reading to
    /// <paramref name="progress"/>. The first poll happens immediately.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The number or an option is out of range.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public async Task<PrWaitResult> WaitAsync(
        string workingDirectory,
        int number,
        PrChecksPollerOptions options,
        IProgress<PrChecksObservation>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxWait, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.NoChecksConfirmations, 1);

        var started = time.GetTimestamp();
        PrChecksObservation? last = null;
        GitRemoteResult? lastFailure = null;
        var lastFailureReason = PrFailureReason.None;
        var consecutiveFailures = 0;
        var noChecksStreak = 0;

        for (var polls = 1; ; polls++)
        {
            ct.ThrowIfCancellationRequested();

            var read = await ObserveAsync(workingDirectory, number, ct).ConfigureAwait(false);
            if (read.Success && read.Value is { } observation)
            {
                consecutiveFailures = 0;
                lastFailure = null;
                lastFailureReason = PrFailureReason.None;
                last = observation;
                noChecksStreak = observation.Checks.NoneReported ? noChecksStreak + 1 : 0;
                progress?.Report(observation);

                if (Evaluate(observation, noChecksStreak, options.NoChecksConfirmations) is { } outcome)
                {
                    return new PrWaitResult(
                        outcome,
                        observation,
                        polls,
                        time.GetElapsedTime(started),
                        NoteFor(outcome, observation));
                }
            }
            else
            {
                // A failed read says nothing about the checks, so it also breaks a run of "no checks" confirmations:
                // the claim "this repository has no CI" needs consecutive successful reads.
                consecutiveFailures++;
                noChecksStreak = 0;
                lastFailure = read.Status;
                lastFailureReason = read.Reason;

                if (consecutiveFailures >= MaxConsecutiveFailures || read.Reason == PrFailureReason.PlatformNotInstalled)
                {
                    return new PrWaitResult(
                        PrWaitOutcome.Failed,
                        last,
                        polls,
                        time.GetElapsedTime(started),
                        null,
                        lastFailure,
                        lastFailureReason);
                }
            }

            var remaining = options.MaxWait - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return new PrWaitResult(
                    PrWaitOutcome.TimedOut,
                    last,
                    polls,
                    time.GetElapsedTime(started),
                    null,
                    lastFailure,
                    lastFailureReason);
            }

            await Task.Delay(remaining < options.Interval ? remaining : options.Interval, time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The verdict for one observation, or null when the answer is not determined yet and the wait must go on. A snapshot
    /// caller passes <paramref name="noChecksStreak"/> 0 so an empty list is never concluded to mean "no CI".
    /// </summary>
    internal static PrWaitOutcome? Evaluate(PrChecksObservation observation, int noChecksStreak, int noChecksConfirmations)
    {
        var merge = observation.MergeState;
        if (merge.State is PrState.Merged or PrState.Closed)
        {
            return PrWaitOutcome.NotOpen;
        }

        var failing = 0;
        var pending = 0;
        foreach (var check in observation.Checks.Checks)
        {
            if (PrChecksObservation.IsFailing(check))
            {
                failing++;
            }
            else if (PrChecksObservation.IsPending(check))
            {
                pending++;
            }
        }

        if (failing > 0)
        {
            return PrWaitOutcome.ChecksFailed;
        }

        var noChecks = observation.Checks.NoneReported;
        if (noChecks ? noChecksStreak < noChecksConfirmations : pending > 0)
        {
            return null;
        }

        var status = merge.MergeStateStatus;
        if (status == PrMergeStateStatus.Dirty || merge.Mergeable == PrMergeable.Conflicting)
        {
            return PrWaitOutcome.Dirty;
        }

        if (status == PrMergeStateStatus.Behind)
        {
            return PrWaitOutcome.Behind;
        }

        if (status == PrMergeStateStatus.Draft || merge.IsDraft)
        {
            return PrWaitOutcome.Draft;
        }

        return status switch
        {
            PrMergeStateStatus.Blocked => PrWaitOutcome.Blocked,
            PrMergeStateStatus.Unstable => PrWaitOutcome.Unstable,
            PrMergeStateStatus.Clean => noChecks ? PrWaitOutcome.NoCi : PrWaitOutcome.Ready,
            PrMergeStateStatus.HasHooks => noChecks ? PrWaitOutcome.NoCi : PrWaitOutcome.ReadyWithHooks,
            _ => null,
        };
    }

    private static string? NoteFor(PrWaitOutcome outcome, PrChecksObservation observation) =>
        outcome == PrWaitOutcome.ReadyWithHooks
        || (outcome == PrWaitOutcome.NoCi && observation.MergeState.MergeStateStatus == PrMergeStateStatus.HasHooks)
            ? HooksCaveat
            : null;

    private static PrResult<PrChecksObservation> Forward<T>(PrResult<T> failed, string fallbackMessage)
    {
        var status = failed.Status;
        return status.Success
            ? PrResult<PrChecksObservation>.Fail(GitRemoteFailureKind.Other, fallbackMessage + " The platform returned no value.")
            : PrResult<PrChecksObservation>.Fail(status.Failure, status.Message, status.StandardError, failed.Reason);
    }
}
