using System.Collections.Concurrent;
using System.Diagnostics;
using Forge.App.Git;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

// Shared doubles of the pull request, watch and notification suites in this folder: PrWatcherTests, PrWatchTextTests,
// PrReviewThreadItemTests, SystemNotifierTests, GitPanelPullRequestTests and GitPanelPullRequestWatchTests. The file holds
// no test of its own; its name only follows the ownership pattern of the batch. Nothing here sleeps: every wait is a
// counter or a gate that the fake platform trips, bounded by PrKit.Budget.

/// <summary>Bounds and builders shared by the pull request suites.</summary>
internal static class PrKit
{
    /// <summary>Upper bound of every wait on a fake; reaching it fails the test instead of hanging.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    public static PullRequestInfo Pull(
        int number = 7,
        string title = "Add feature",
        PrState state = PrState.Open,
        bool draft = false) =>
        new(
            number,
            "https://github.com/acme/forge/pull/" + number,
            state,
            title,
            "body",
            "feature",
            "main",
            "abc1234",
            draft,
            PrMergeStateStatus.Clean,
            PrReviewDecision.None);

    public static PrResult<PullRequestInfo> FoundPull(int number = 7, string title = "Add feature") =>
        PrResult<PullRequestInfo>.Ok(Pull(number, title));

    public static PrResult<PullRequestInfo> NoPull() =>
        PrResult<PullRequestInfo>.Fail(
            GitRemoteFailureKind.Other,
            "no pull requests found",
            string.Empty,
            PrFailureReason.PullRequestNotFound);

    public static PrPlatformAvailability Ready { get; } = new(PrPlatformState.Ready, "ok", "github.com");

    public static PrReviewComment Comment(string body, string? author = "reviewer", string id = "c") =>
        new(id, null, author, body, null, null);

    public static PrReviewThread Thread(
        string id,
        bool resolved = false,
        bool outdated = false,
        string? path = "src/a.cs",
        int? line = 10,
        int? originalLine = null,
        params PrReviewComment[] comments) =>
        new(id, resolved, outdated, path, line, null, originalLine, "RIGHT", resolved ? "someone" : null, comments);

    public static PrResult<IReadOnlyList<PrReviewThread>> Threads(params PrReviewThread[] threads) =>
        PrResult<IReadOnlyList<PrReviewThread>>.Ok(threads);

    public static PrCheck Check(string name, PrCheckBucket bucket) =>
        new(name, "CI", string.Empty, bucket, string.Empty, null, null, null, null, null);

    public static PrMergeState Merge(
        PrMergeStateStatus status = PrMergeStateStatus.Clean,
        PrState state = PrState.Open,
        bool draft = false,
        PrMergeable mergeable = PrMergeable.Mergeable) =>
        new(
            state,
            draft,
            status,
            PrReviewDecision.None,
            mergeable,
            status switch
            {
                PrMergeStateStatus.Clean => "CLEAN",
                PrMergeStateStatus.HasHooks => "HAS_HOOKS",
                PrMergeStateStatus.Behind => "BEHIND",
                PrMergeStateStatus.Dirty => "DIRTY",
                PrMergeStateStatus.Blocked => "BLOCKED",
                PrMergeStateStatus.Unstable => "UNSTABLE",
                PrMergeStateStatus.Draft => "DRAFT",
                _ => "UNKNOWN",
            },
            string.Empty,
            mergeable == PrMergeable.Conflicting ? "CONFLICTING" : "MERGEABLE");

    public static PrChecksObservation Observation(
        PrMergeStateStatus status = PrMergeStateStatus.Clean,
        params PrCheck[] checks) =>
        new(new PrChecksSnapshot(checks), Merge(status), new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));

    public static PrWaitResult Result(
        PrWaitOutcome outcome,
        PrChecksObservation? observation = null,
        TimeSpan? elapsed = null,
        GitRemoteResult? failure = null,
        PrFailureReason reason = PrFailureReason.None) =>
        new(outcome, observation, 1, elapsed ?? TimeSpan.FromMinutes(1), null, failure, reason);

    /// <summary>One poll answered with a settled, passing pull request.</summary>
    public static PrAppStep Passing(PrMergeStateStatus status = PrMergeStateStatus.Clean) =>
        PrAppStep.Of(Merge(status), Check("build", PrCheckBucket.Pass), Check("lint", PrCheckBucket.Pass));

    public static PrAppStep Pending() =>
        PrAppStep.Of(Merge(), Check("build", PrCheckBucket.Pending), Check("lint", PrCheckBucket.Pass));
}

/// <summary>A counter that can be awaited: <see cref="WhenAtLeastAsync"/> completes once <see cref="Hit"/> was called that often.</summary>
internal sealed class PrAppCounter
{
    private readonly object gate = new();
    private readonly List<(int Target, TaskCompletionSource Source)> waiters = [];
    private int count;

    public int Count
    {
        get
        {
            lock (gate)
            {
                return count;
            }
        }
    }

    /// <summary>Counts one event and returns the new total (so concurrent callers get distinct numbers).</summary>
    public int Hit()
    {
        List<TaskCompletionSource> ready = [];
        int total;
        lock (gate)
        {
            total = ++count;
            for (var index = waiters.Count - 1; index >= 0; index--)
            {
                if (count >= waiters[index].Target)
                {
                    ready.Add(waiters[index].Source);
                    waiters.RemoveAt(index);
                }
            }
        }

        foreach (var source in ready)
        {
            source.TrySetResult();
        }

        return total;
    }

    /// <summary>Completes when the counter reached <paramref name="target"/>; fails the test (not hangs) after the budget.</summary>
    public async Task WhenAtLeastAsync(int target, string what)
    {
        Task task;
        lock (gate)
        {
            if (count >= target)
            {
                return;
            }

            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((target, source));
            task = source.Task;
        }

        try
        {
            await task.WaitAsync(PrKit.Budget);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"timed out waiting for {what} (wanted {target}, saw {Count})");
        }
    }
}

/// <summary>
/// A latch a fake platform call waits on. <see cref="Entered"/> counts the calls that reached it, <see cref="Cancelled"/>
/// those that left it by cancellation; <see cref="Release"/> lets every waiter through (also later ones).
/// </summary>
internal sealed class PrAppGate
{
    private readonly TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PrAppCounter Entered { get; } = new();

    public PrAppCounter Cancelled { get; } = new();

    public bool IsOpen => open.Task.IsCompleted;

    public void Release() => open.TrySetResult();

    /// <summary>A cooperative wait: cancellation ends it with <see cref="OperationCanceledException"/> and is counted.</summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        Entered.Hit();
        try
        {
            await open.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Cancelled.Hit();
            throw;
        }
    }

    /// <summary>A platform that ignores its token (a slow call that cannot be interrupted).</summary>
    public async Task WaitIgnoringCancellationAsync()
    {
        Entered.Hit();
        await open.Task.ConfigureAwait(false);
    }
}

/// <summary>One call a fake platform received: which one of its kind, where, for which pull request, and its token.</summary>
internal sealed record PrAppCall(int Number, string Directory, int PullRequest, CancellationToken Ct);

/// <summary>What one poll reads: the checks and the merge state, both successful or both failed.</summary>
internal sealed record PrAppStep(PrResult<PrChecksSnapshot> Checks, PrResult<PrMergeState> Merge)
{
    public static PrAppStep Of(PrMergeState merge, params PrCheck[] checks) =>
        new(PrResult<PrChecksSnapshot>.Ok(new PrChecksSnapshot(checks)), PrResult<PrMergeState>.Ok(merge));

    public static PrAppStep Failed(
        GitRemoteFailureKind kind = GitRemoteFailureKind.Network,
        string message = "gh failed",
        PrFailureReason reason = PrFailureReason.None) =>
        new(
            PrResult<PrChecksSnapshot>.Fail(kind, message, "stderr", reason),
            PrResult<PrMergeState>.Fail(kind, message, "stderr", reason));
}

/// <summary>
/// The n-th poll gets the n-th step (the last repeats). From poll <see cref="BlockFromPoll"/> (zero-based) on, the checks
/// read waits on <see cref="Gate"/> and, once released, answers with <see cref="AfterGate"/> (the last step by default) - the
/// way a test keeps a watch running without sleeping.
/// </summary>
internal sealed class PrAppScript
{
    private readonly PrAppStep[] steps;

    public PrAppScript(params PrAppStep[] steps)
    {
        ArgumentOutOfRangeException.ThrowIfZero(steps.Length);
        this.steps = steps;
    }

    public PrAppGate? Gate { get; init; }

    public int BlockFromPoll { get; init; } = int.MaxValue;

    public PrAppStep? AfterGate { get; init; }

    public async Task<PrResult<PrChecksSnapshot>> ChecksAsync(PrAppCall call)
    {
        var index = call.Number - 1;
        if (Gate is not null && index >= BlockFromPoll)
        {
            await Gate.WaitAsync(call.Ct).ConfigureAwait(false);
            return (AfterGate ?? steps[^1]).Checks;
        }

        return steps[Math.Min(index, steps.Length - 1)].Checks;
    }

    public Task<PrResult<PrMergeState>> MergeAsync(PrAppCall call)
    {
        var index = call.Number - 1;
        var step = Gate is not null && index >= BlockFromPoll
            ? AfterGate ?? steps[^1]
            : steps[Math.Min(index, steps.Length - 1)];
        return Task.FromResult(step.Merge);
    }

    /// <summary>A watch that delivers one pending reading and then stays running until <paramref name="gate"/> opens.</summary>
    public static PrAppScript PendingThenBlock(PrAppGate gate, PrAppStep? afterGate = null) =>
        new(PrKit.Pending()) { Gate = gate, BlockFromPoll = 1, AfterGate = afterGate ?? PrKit.Passing() };
}

/// <summary>
/// An <see cref="IPrPlatform"/> whose calls are delegates (a test sets the ones it cares about) and which counts every call.
/// The defaults describe a ready gh and a branch with pull request #7 without review threads. Calls that a pull request
/// panel must never make (create, edit, reply, resolve, log, diff) throw, so a test fails loudly if the panel reaches for them.
/// </summary>
internal sealed class PrAppPlatform : IPrPlatform
{
    private readonly ConcurrentQueue<string> directories = new();

    public string Name => "Fake";

    public PrAppCounter Availability { get; } = new();

    public PrAppCounter View { get; } = new();

    public PrAppCounter Threads { get; } = new();

    public PrAppCounter Checks { get; } = new();

    public PrAppCounter Merge { get; } = new();

    public Func<PrAppCall, Task<PrPlatformAvailability>> OnAvailability { get; set; } =
        static _ => Task.FromResult(PrKit.Ready);

    public Func<PrAppCall, Task<PrResult<PullRequestInfo>>> OnView { get; set; } =
        static _ => Task.FromResult(PrKit.FoundPull());

    public Func<PrAppCall, Task<PrResult<IReadOnlyList<PrReviewThread>>>> OnThreads { get; set; } =
        static _ => Task.FromResult(PrKit.Threads());

    public Func<PrAppCall, Task<PrResult<PrChecksSnapshot>>> OnChecks { get; set; } =
        static call => new PrAppScript(PrKit.Passing()).ChecksAsync(call);

    public Func<PrAppCall, Task<PrResult<PrMergeState>>> OnMerge { get; set; } =
        static call => new PrAppScript(PrKit.Passing()).MergeAsync(call);

    /// <summary>Every call made so far in the order it arrived, as <c>kind:directory</c>.</summary>
    public IReadOnlyCollection<string> Calls => [.. directories];

    public int TotalCalls => Availability.Count + View.Count + Threads.Count + Checks.Count + Merge.Count;

    /// <summary>Routes checks and merge reads to a script per working directory.</summary>
    public void UseScripts(Func<string, PrAppScript> scriptFor)
    {
        var scripts = new ConcurrentDictionary<string, PrAppScript>(StringComparer.OrdinalIgnoreCase);
        OnChecks = call => scripts.GetOrAdd(call.Directory, scriptFor).ChecksAsync(call);
        OnMerge = call => scripts.GetOrAdd(call.Directory, scriptFor).MergeAsync(call);
    }

    public Task<PrPlatformAvailability> GetAvailabilityAsync(string workingDirectory, CancellationToken ct = default) =>
        Run("availability", Availability, workingDirectory, 0, ct, OnAvailability);

    public Task<PrResult<PullRequestInfo>> ViewPullRequestAsync(
        string workingDirectory,
        string? selector = null,
        CancellationToken ct = default) =>
        Run("view", View, workingDirectory, 0, ct, OnView);

    public Task<PrResult<PrChecksSnapshot>> GetChecksAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        Run("checks", Checks, workingDirectory, number, ct, OnChecks);

    public Task<PrResult<PrMergeState>> GetMergeStateAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        Run("merge", Merge, workingDirectory, number, ct, OnMerge);

    public Task<PrResult<IReadOnlyList<PrReviewThread>>> GetReviewThreadsAsync(
        string workingDirectory,
        int number,
        CancellationToken ct = default) =>
        Run("threads", Threads, workingDirectory, number, ct, OnThreads);

    public Task<PrResult<PrReference>> CreatePullRequestAsync(
        string workingDirectory,
        string title,
        string body,
        string baseBranch,
        string headBranch,
        bool draft = false,
        CancellationToken ct = default) => throw new NotSupportedException("create");

    public Task<PrResult<PrReference>> EditPullRequestAsync(
        string workingDirectory,
        int number,
        string? title = null,
        string? body = null,
        CancellationToken ct = default) => throw new NotSupportedException("edit");

    public Task<PrResult<PrText>> GetFailedJobLogAsync(string workingDirectory, long? runId, long jobId, CancellationToken ct = default) =>
        throw new NotSupportedException("log");

    public Task<PrResult<PrText>> GetPullRequestDiffAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        throw new NotSupportedException("diff");

    public Task<PrResult<PrReviewComment>> ReplyToReviewThreadAsync(
        string workingDirectory,
        string threadId,
        string body,
        CancellationToken ct = default) => throw new NotSupportedException("reply");

    public Task<PrResult<PrThreadResolution>> ResolveReviewThreadAsync(
        string workingDirectory,
        string threadId,
        CancellationToken ct = default) => throw new NotSupportedException("resolve");

    private Task<T> Run<T>(
        string kind,
        PrAppCounter counter,
        string directory,
        int number,
        CancellationToken ct,
        Func<PrAppCall, Task<T>> body)
    {
        directories.Enqueue(kind + ":" + directory);
        var callNumber = counter.Hit();
        return body(new PrAppCall(callNumber, directory, number, ct));
    }
}

/// <summary>
/// A clock whose delays elapse instantly: creating a timer advances the virtual clock by its due time and fires it. Every
/// delay the code asked for is recorded.
/// </summary>
internal sealed class PrAppManualTime : TimeProvider
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly object gate = new();
    private readonly List<TimeSpan> delays = [];
    private long ticks;

    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (gate)
            {
                return [.. delays];
            }
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return Origin.AddTicks(ticks);
        }
    }

    public override long GetTimestamp()
    {
        lock (gate)
        {
            return ticks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (gate)
            {
                delays.Add(dueTime);
                ticks += dueTime.Ticks;
            }

            callback(state);
        }

        return new NoopTimer();
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// A clock that only moves when the test says so: a timer fires from <see cref="Advance"/> once the virtual time reached
/// its due time. Used for the notifier's 5 second limit, which must be provable without waiting 5 seconds.
/// </summary>
internal sealed class PrAppControlledTime : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ControlledTimer> timers = [];
    private readonly List<TimeSpan> requested = [];
    private TimeSpan now;

    /// <summary>The due times of every timer that was created or changed to a finite delay.</summary>
    public IReadOnlyList<TimeSpan> RequestedDueTimes
    {
        get
        {
            lock (gate)
            {
                return [.. requested];
            }
        }
    }

    public int ActiveTimers
    {
        get
        {
            lock (gate)
            {
                return timers.Count(static timer => !timer.Disposed && !timer.Fired && timer.Due is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero) + now;
        }
    }

    public override long GetTimestamp()
    {
        lock (gate)
        {
            return now.Ticks;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ControlledTimer(this, callback, state);
        lock (gate)
        {
            timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward and fires, on the calling thread, every timer that became due.</summary>
    public void Advance(TimeSpan by)
    {
        List<ControlledTimer> due = [];
        lock (gate)
        {
            now += by;
            foreach (var timer in timers)
            {
                if (!timer.Disposed && !timer.Fired && timer.Due is { } at && at <= now)
                {
                    timer.Fired = true;
                    due.Add(timer);
                }
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private void Record(TimeSpan dueTime)
    {
        lock (gate)
        {
            requested.Add(dueTime);
        }
    }

    private TimeSpan Now()
    {
        lock (gate)
        {
            return now;
        }
    }

    private sealed class ControlledTimer(PrAppControlledTime owner, TimerCallback callback, object? state) : ITimer
    {
        public TimeSpan? Due { get; private set; }

        public bool Disposed { get; private set; }

        public bool Fired { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                Due = null;
                return true;
            }

            owner.Record(dueTime);
            Fired = false;
            Due = owner.Now() + dueTime;
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A notifier that records its calls; its behaviour can be replaced to throw or to wait.</summary>
internal sealed class PrAppNotifier : ISystemNotifier
{
    private readonly ConcurrentQueue<(string Title, string Body)> calls = new();

    public PrAppCounter Count { get; } = new();

    public IReadOnlyList<(string Title, string Body)> Calls => [.. calls];

    public Func<string, string, CancellationToken, Task>? Behavior { get; set; }

    /// <summary>The token of the most recent call.</summary>
    public CancellationToken LastToken { get; private set; }

    public Task NotifyAsync(string title, string body, CancellationToken ct)
    {
        calls.Enqueue((title, body));
        LastToken = ct;
        Count.Hit();
        return Behavior?.Invoke(title, body, ct) ?? Task.CompletedTask;
    }
}

/// <summary>Collects what a watcher posts to the UI thread; nothing runs until the test drains it.</summary>
internal sealed class PrAppPostQueue
{
    private readonly ConcurrentQueue<Action> items = new();

    public int Pending => items.Count;

    public void Post(Action action) => items.Enqueue(action);

    /// <summary>Runs everything queued so far (and what the items queue meanwhile) on the calling thread; returns how many ran.</summary>
    public int Drain()
    {
        var ran = 0;
        while (items.TryDequeue(out var action))
        {
            action();
            ran++;
        }

        return ran;
    }
}

/// <summary>Captures <see cref="Trace"/> output for the duration of a test and removes itself.</summary>
internal sealed class PrAppTraceCapture : TraceListener
{
    private readonly ConcurrentQueue<string> lines = new();

    public PrAppTraceCapture()
    {
        Trace.Listeners.Add(this);
    }

    public IReadOnlyList<string> Lines => [.. lines];

    public override void Write(string? message)
    {
        if (!string.IsNullOrEmpty(message))
        {
            lines.Enqueue(message);
        }
    }

    public override void WriteLine(string? message) => Write(message);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Trace.Listeners.Remove(this);
        }

        base.Dispose(disposing);
    }
}
