using System.Collections.ObjectModel;
using System.Globalization;
using Forge.App.Services;
using Forge.App.Git;
using Forge.App.Localization;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>
/// What handing the failing checks to the agent produced: the turn ran, the session was busy so the prompt waits in its
/// composer, or there was no session to hand it to at all.
/// </summary>
internal enum ChecksHandOff
{
    Ran,
    Queued,
    NoSession,
}

/// <summary>
/// The pull request checks panel (wave W5): every check of one pull request with its state, duration and link, the verdict
/// of the latest reading, the tail of a failed job's log, and the three things a user does from here — refresh, mark a
/// draft ready, or hand the failures to the agent.
/// <para>Reads run <see cref="PrChecksPoller.ObserveAsync"/> (the same two platform calls the <c>pr_checks</c> tool makes)
/// on a background task and report back through <c>post</c>, so every field of this object is only ever written on the UI
/// thread. It watches until the reading reaches a verdict: a pending check keeps it polling at the configured interval, a
/// settled one stops and waits for <see cref="RefreshCommand"/>. Nothing changes by itself after a verdict, and every poll
/// is two gh processes. <see cref="Dispose"/> cancels the poll; the window calls it when the panel closes.</para>
/// <para>Nothing here writes to the repository or the pull request except <see cref="MarkReadyCommand"/>, which runs
/// <c>gh pr ready</c> on the draft the user is looking at. A read failure is shown next to the last good reading instead of
/// blanking it: the checks did not change because the network did.</para>
/// </summary>
internal sealed class ChecksPanelViewModel : ObservableObject, IDisposable
{
    private readonly string workingDirectory;
    private readonly int pullRequestNumber;
    private readonly string pullRequestUrl;
    private readonly IPrPlatform platform;
    private readonly PrChecksPoller poller;
    private readonly Func<PrChecksPollerOptions> options;
    private readonly Func<int> failureLogTailLines;
    private readonly Action<Action> post;
    private readonly Action<string>? openLink;
    private readonly Func<string, Task<ChecksHandOff>>? handToAgent;
    private readonly Action? close;
    private readonly TimeProvider time;

    private CancellationTokenSource? watchSource;
    private Task watch = Task.CompletedTask;

    /// <summary>
    /// Which poll the fields belong to. Cancelling the previous watch does not stop a read that is already in flight from
    /// posting its result, so every post carries the generation it started under and a stale one is dropped: a refresh must
    /// not be overwritten by the poll it replaced, and its "reading" indicator must not be cleared by that poll's tail.
    /// </summary>
    private int watchGeneration;
    private PrChecksObservation? observation;
    private PrWaitOutcome? outcome;
    private bool hasRead;
    private bool isReading;
    private string problem = string.Empty;
    private string status = string.Empty;
    private bool disposed;

    /// <summary>The thread the panel was built on (the UI thread); a language notification from another one is not ours.</summary>
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    public ChecksPanelViewModel(
        string workingDirectory,
        int pullRequestNumber,
        string? pullRequestUrl,
        IPrPlatform platform,
        Func<PrChecksPollerOptions> options,
        Func<int> failureLogTailLines,
        Action<Action> post,
        Action<string>? openLink = null,
        Func<string, Task<ChecksHandOff>>? handToAgent = null,
        Action? close = null,
        TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequestNumber);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(failureLogTailLines);
        ArgumentNullException.ThrowIfNull(post);

        this.workingDirectory = workingDirectory;
        this.pullRequestNumber = pullRequestNumber;
        this.pullRequestUrl = pullRequestUrl ?? string.Empty;
        this.platform = platform;
        this.options = options;
        this.failureLogTailLines = failureLogTailLines;
        this.post = post;
        this.openLink = openLink;
        this.handToAgent = handToAgent;
        this.close = close;
        this.time = time ?? TimeProvider.System;
        poller = new PrChecksPoller(platform, this.time);

        RefreshCommand = new AsyncRelayCommand(_ => RestartAsync(), () => !disposed);
        MarkReadyCommand = new AsyncRelayCommand(_ => MarkReadyAsync(), () => !disposed && IsDraft);
        FixCommand = new AsyncRelayCommand(_ => FixAsync(), () => !disposed && CanFix);
        CloseCommand = new RelayCommand(Close);
        OpenPullRequestCommand = new RelayCommand(() => Open(this.pullRequestUrl), () => HasPullRequestUrl);

        // Every text on this panel is localized, several of them formatted from the reading. A language switch re-reads
        // them all through one notification instead of one per property; the subscription is dropped by Dispose.
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>"#12".</summary>
    public string NumberText => Loc.Instance.Format(ChecksStrings.Number, pullRequestNumber);

    public bool HasPullRequestUrl => pullRequestUrl.Length > 0;

    /// <summary>One row per check of the latest reading, in <see cref="PrCheckText.BucketRank"/> order.</summary>
    public ObservableCollection<ChecksRowViewModel> Checks { get; } = [];

    public bool HasChecks => Checks.Count > 0;

    /// <summary>True when a successful read found no checks at all (a fresh head whose workflows have not registered yet).</summary>
    public bool IsEmpty => hasRead && Checks.Count == 0 && !HasProblem;

    /// <summary>The verdict of the latest reading, or null while it is not determined yet.</summary>
    public PrWaitOutcome? Outcome => outcome;

    /// <summary>A read is in flight; the panel polls until the reading settles.</summary>
    public bool IsReading
    {
        get => isReading;
        private set => Set(ref isReading, value);
    }

    /// <summary>The merge state and review decision of the latest reading, as the platform texted them.</summary>
    public string DetailText
    {
        get
        {
            if (observation is not { } current)
            {
                return string.Empty;
            }

            var merge = current.MergeState;
            var mergeText = Loc.Instance.Format(
                ChecksStrings.DetailMergeState,
                merge.MergeStateStatusText.Length > 0 ? merge.MergeStateStatusText : TextFormat.None);
            var review = merge.ReviewDecisionText.Length > 0
                ? Loc.Instance.Format(ChecksStrings.DetailReview, merge.ReviewDecisionText)
                : string.Empty;
            return review.Length > 0
                ? mergeText + Loc.Instance[ChecksStrings.CountSeparator] + review
                : mergeText;
        }
    }

    /// <summary>The verdict line: what the latest reading means for merging, or why it is not decided yet.</summary>
    public string VerdictText => outcome switch
    {
        PrWaitOutcome.Ready => Loc.Instance[ChecksStrings.VerdictReady],
        PrWaitOutcome.ReadyWithHooks => Loc.Instance[ChecksStrings.VerdictReadyWithHooks],
        PrWaitOutcome.ChecksFailed => Loc.Instance[ChecksStrings.VerdictFailed],
        PrWaitOutcome.Behind => Loc.Instance[ChecksStrings.VerdictBehind],
        PrWaitOutcome.Dirty => Loc.Instance[ChecksStrings.VerdictDirty],
        PrWaitOutcome.Blocked => Loc.Instance[ChecksStrings.VerdictBlocked],
        PrWaitOutcome.Unstable => Loc.Instance[ChecksStrings.VerdictUnstable],
        PrWaitOutcome.Draft => Loc.Instance[ChecksStrings.VerdictDraft],
        PrWaitOutcome.NoCi => Loc.Instance[ChecksStrings.VerdictNoCi],
        PrWaitOutcome.NotOpen => Loc.Instance[ChecksStrings.VerdictNotOpen],
        PrWaitOutcome.Failed or PrWaitOutcome.TimedOut =>
            Loc.Instance.Format(ChecksStrings.ReadFailed, problem.Length > 0 ? problem : TextFormat.None),
        null => hasRead ? Loc.Instance[ChecksStrings.VerdictWaiting] : Loc.Instance[ChecksStrings.VerdictUnknown],
        _ => Loc.Instance[ChecksStrings.VerdictUnknown],
    };

    /// <summary>Everything passed and nothing else is in the way.</summary>
    public bool IsReady => outcome is PrWaitOutcome.Ready or PrWaitOutcome.ReadyWithHooks or PrWaitOutcome.NoCi;

    /// <summary>A check failed, or the platform could not be read: the red tint.</summary>
    public bool IsFailed => outcome is PrWaitOutcome.ChecksFailed or PrWaitOutcome.Failed;

    /// <summary>The checks are fine but the merge is not (behind, conflicting, blocked, draft, unstable).</summary>
    public bool IsWarning => outcome is PrWaitOutcome.Behind or PrWaitOutcome.Dirty or PrWaitOutcome.Blocked
        or PrWaitOutcome.Unstable or PrWaitOutcome.Draft;

    /// <summary>Nothing is decided yet.</summary>
    public bool IsWaiting => outcome is null;

    /// <summary>"3 通过 · 1 失败 · 2 运行中 · 1 跳过"; empty before the first successful read.</summary>
    public string SummaryText
    {
        get
        {
            if (observation is not { } current)
            {
                return string.Empty;
            }

            var failed = current.FailingChecks.Count;
            var pending = current.PendingChecks.Count;
            var skipped = current.SkippedCount;
            var passed = current.PassedCount - skipped;
            var separator = Loc.Instance[ChecksStrings.CountSeparator];
            var parts = new List<string>(4);
            if (passed > 0)
            {
                parts.Add(Loc.Instance.Format(ChecksStrings.CountPassed, TextFormat.Number(passed)));
            }

            if (failed > 0)
            {
                parts.Add(Loc.Instance.Format(ChecksStrings.CountFailed, TextFormat.Number(failed)));
            }

            if (pending > 0)
            {
                parts.Add(Loc.Instance.Format(ChecksStrings.CountPending, TextFormat.Number(pending)));
            }

            if (skipped > 0)
            {
                parts.Add(Loc.Instance.Format(ChecksStrings.CountSkipped, TextFormat.Number(skipped)));
            }

            return string.Join(separator, parts);
        }
    }

    public bool HasSummary => SummaryText.Length > 0;

    /// <summary>Why the latest read failed (auth, network, gh missing); empty when it succeeded.</summary>
    public string ProblemText => problem;

    public bool HasProblem => problem.Length > 0;

    /// <summary>The empty state's headline: a successful read found no checks.</summary>
    public string EmptyTitleText => Loc.Instance[ChecksStrings.EmptyTitle];

    public string EmptyBodyText => Loc.Instance[ChecksStrings.EmptyBody];

    /// <summary>"读取于 12:30:45" of the latest successful read.</summary>
    public string LastReadText => observation is { } reading
        ? Loc.Instance.Format(ChecksStrings.ReadAt, TextFormat.Clock(reading.ObservedAt))
        : string.Empty;

    public bool HasLastRead => observation is not null;

    /// <summary>What the last hand-off or mark-ready produced; empty before one ran.</summary>
    public string StatusText => status;

    public bool HasStatus => status.Length > 0;

    /// <summary>The pull request is a draft, so <c>gh pr ready</c> has something to do.</summary>
    public bool IsDraft => observation?.MergeState.IsDraft == true;

    /// <summary>There is at least one failed check and a session to hand it to.</summary>
    public bool CanFix => handToAgent is not null && observation is { FailingChecks.Count: > 0 };

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand MarkReadyCommand { get; }

    public AsyncRelayCommand FixCommand { get; }

    public RelayCommand CloseCommand { get; }

    /// <summary>Opens the pull request in the browser (the header's address line).</summary>
    public RelayCommand OpenPullRequestCommand { get; }

    /// <summary>
    /// The read in flight. Completed once the reading settled (or the panel closed); exposed so the window can drive it and
    /// so a test can await one read deterministically.
    /// </summary>
    public Task Watch => watch;

    /// <summary>Starts the first read. The window calls this once after opening the panel.</summary>
    public void Start() => RefreshCommand.Execute(null);

    /// <summary>Cancels the poll. The window calls it when the panel closes; idempotent.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        watchGeneration++;

        // Nothing is reading any more: the poll's own tail would be dropped by the generation guard, so the indicator is
        // cleared here instead.
        IsReading = false;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        RefreshCommand.NotifyCanExecuteChanged();
        MarkReadyCommand.NotifyCanExecuteChanged();
        FixCommand.NotifyCanExecuteChanged();

        // Cancel, do not dispose: a delay inside the poll still holds the token, and disposing the source would turn its
        // registration into an ObjectDisposedException instead of a cancellation.
        var source = watchSource;
        watchSource = null;
        source?.Cancel();
    }

    /// <summary>Cancels the read in flight and starts a new one; the returned task is the new watch.</summary>
    private Task RestartAsync()
    {
        var previous = watchSource;
        watchSource = null;
        previous?.Cancel();

        if (disposed)
        {
            return Task.CompletedTask;
        }

        var source = new CancellationTokenSource();
        watchSource = source;
        var generation = ++watchGeneration;
        IsReading = true;
        watch = WatchAsync(source.Token, generation);
        return watch;
    }

    private async Task WatchAsync(CancellationToken ct, int generation)
    {
        try
        {
            var settings = options();
            var started = time.GetTimestamp();
            while (!ct.IsCancellationRequested)
            {
                var read = await poller.ObserveAsync(workingDirectory, pullRequestNumber, ct).ConfigureAwait(false);

                // Decided off the UI thread: the post may run later, and whether the poll continues must not depend on it.
                var settled = read.Success && read.Value is { } reading && PrChecksPoller.Verdict(reading) is not null;
                post(() => ApplyIfCurrent(read, generation));
                if (settled)
                {
                    return;
                }

                // The same bound the pr_checks tool waits for: a check that never finishes must not keep a foreground panel
                // asking gh every interval for as long as the window is open. Refresh starts a fresh budget.
                if (time.GetElapsedTime(started) >= settings.MaxWait)
                {
                    post(() =>
                    {
                        if (generation == watchGeneration)
                        {
                            SetStatus(Loc.Instance.Format(
                                ChecksStrings.WaitTimeout,
                                TextFormat.Number((long)settings.MaxWait.TotalMinutes)));
                        }
                    });
                    return;
                }

                await Task.Delay(settings.Interval, time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            post(() =>
            {
                if (generation == watchGeneration)
                {
                    Report(exception.Message);
                }
            });
        }
        finally
        {
            post(() =>
            {
                if (generation == watchGeneration)
                {
                    IsReading = false;
                }
            });
        }
    }

    /// <summary>Applies a reading unless the poll it came from was replaced or the panel closed; runs on the UI thread.</summary>
    private void ApplyIfCurrent(PrResult<PrChecksObservation> read, int generation)
    {
        if (generation == watchGeneration)
        {
            Apply(read);
        }
    }

    /// <summary>Writes one reading into the panel; runs on the UI thread.</summary>
    private void Apply(PrResult<PrChecksObservation> read)
    {
        if (disposed)
        {
            return;
        }

        if (!read.Success || read.Value is not { } reading)
        {
            Report(read.Status.Message);
            return;
        }

        problem = string.Empty;
        observation = reading;
        outcome = PrChecksPoller.Verdict(reading);
        hasRead = true;
        ApplyChecks(reading.Checks);
        RaiseReading();
    }

    private void Report(string reason)
    {
        if (disposed)
        {
            return;
        }

        problem = reason.Length > 0 ? reason : TextFormat.None;
        RaiseReading();
    }

    /// <summary>
    /// Replaces the rows with the latest reading, keeping the instance of a check that is still there, so a log the user
    /// opened survives the next poll.
    /// </summary>
    private void ApplyChecks(PrChecksSnapshot snapshot)
    {
        var carried = new Dictionary<string, ChecksRowViewModel>(StringComparer.Ordinal);
        foreach (var row in Checks)
        {
            carried[row.Key] = row;
        }

        Checks.Clear();
        foreach (var check in Ordered(snapshot.Checks))
        {
            Checks.Add(carried.TryGetValue(KeyOf(check), out var existing) ? existing.Adopt(check) : Create(check));
        }
    }

    private static List<PrCheck> Ordered(IReadOnlyList<PrCheck> checks)
    {
        var ordered = new List<PrCheck>(checks);
        ordered.Sort(static (left, right) =>
        {
            var byBucket = PrCheckText.BucketRank(left.Bucket).CompareTo(PrCheckText.BucketRank(right.Bucket));
            return byBucket != 0 ? byBucket : string.CompareOrdinal(left.Name, right.Name);
        });
        return ordered;
    }

    /// <summary>A check's identity across polls: the platform reuses neither record nor link, but the name and job are stable.</summary>
    private static string KeyOf(PrCheck check) => ChecksRowViewModel.KeyOf(check);

    private ChecksRowViewModel Create(PrCheck check) => new(check, openLink, ReadLogAsync);

    /// <summary>
    /// The failed job log of one check, cut to the configured tail (the same trim the CI fix chain and the agent message
    /// use); an error text instead when it cannot be read or the check has no job behind it.
    /// </summary>
    private async Task<PrChecksJobLog> ReadLogAsync(ChecksRowViewModel row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        return await ReadLogAsync(row.Check, ct).ConfigureAwait(true);
    }

    private async Task<PrChecksJobLog> ReadLogAsync(PrCheck check, CancellationToken ct)
    {
        if (check.JobId is not { } job)
        {
            return new PrChecksJobLog(check, string.Empty, false, Loc.Instance[ChecksStrings.LogNone]);
        }

        var log = await platform.GetFailedJobLogAsync(workingDirectory, check.RunId, job, ct).ConfigureAwait(true);
        if (!log.Success || log.Value is not { } text)
        {
            return new PrChecksJobLog(check, string.Empty, false, log.Status.Message);
        }

        var tail = TextTail.Keep(text.Text, failureLogTailLines(), PrChecksFixMessage.MaxJobLogCharacters);
        return new PrChecksJobLog(check, tail, text.Truncated || tail.Length < text.Text.Length, null);
    }

    /// <summary>
    /// Builds the agent message (digest, every failed job's log tail, one instruction) and hands it to the session; nothing
    /// is sent when the panel closes first.
    /// </summary>
    private async Task FixAsync()
    {
        if (observation is not { } reading)
        {
            SetStatus(Loc.Instance[ChecksStrings.FixNothing]);
            return;
        }

        if (handToAgent is null)
        {
            SetStatus(Loc.Instance[ChecksStrings.FixNoSession]);
            return;
        }

        var logs = new List<PrChecksJobLog>(reading.FailingChecks.Count);
        foreach (var check in reading.FailingChecks)
        {
            logs.Add(await ReadLogAsync(check, CancellationToken.None).ConfigureAwait(true));
        }

        var message = PrChecksFixMessage.Build(pullRequestNumber, pullRequestUrl, reading, logs, failureLogTailLines());
        var handed = await handToAgent(message).ConfigureAwait(true);
        SetStatus(Loc.Instance[handed switch
        {
            ChecksHandOff.Ran => ChecksStrings.FixSent,
            ChecksHandOff.Queued => ChecksStrings.FixQueued,
            _ => ChecksStrings.FixNoSession,
        }]);
    }

    /// <summary>Marks a draft ready for review, then re-reads: the merge state and the checks both usually move on it.</summary>
    private async Task MarkReadyAsync()
    {
        var marked = await platform.MarkPullRequestReadyAsync(workingDirectory, pullRequestNumber, CancellationToken.None)
            .ConfigureAwait(true);
        SetStatus(marked.Success
            ? Loc.Instance[ChecksStrings.MarkReadyDone]
            : Loc.Instance.Format(ChecksStrings.MarkReadyFailed, marked.Status.Message));
        if (marked.Success)
        {
            await RestartAsync().ConfigureAwait(true);
        }
    }

    private void Open(string url)
    {
        if (url.Length > 0)
        {
            openLink?.Invoke(url);
        }
    }

    private void Close()
    {
        // The window owns the panel's lifetime: it clears its host slot and calls Dispose.
        close?.Invoke();
    }

    /// <summary>
    /// A language switch re-reads every text on the panel. The null property name means "everything changed", which is what
    /// the indexer-bound texts and the formatted verdict need; the rows are asked too, because they format bucket words.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (disposed || Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        OnPropertyChanged(null);
        foreach (var row in Checks)
        {
            row.Relocalize();
        }
    }

    private void SetStatus(string text)
    {
        status = text;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
    }

    private void RaiseReading()
    {
        OnPropertyChanged(nameof(HasChecks));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Outcome));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(VerdictText));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasSummary));
        OnPropertyChanged(nameof(ProblemText));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(LastReadText));
        OnPropertyChanged(nameof(HasLastRead));
        OnPropertyChanged(nameof(IsDraft));
        OnPropertyChanged(nameof(CanFix));
        MarkReadyCommand.NotifyCanExecuteChanged();
        FixCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>
/// One row of the checks panel: a single check of the latest reading and, for a failed check with a workflow job, the tail
/// of that job's log. The row is immutable except for the log and the two flags that show it; the panel hands the next
/// reading of the same check to <see cref="Adopt"/> instead of building a new row, so a log the user opened stays open.
/// </summary>
internal sealed class ChecksRowViewModel : NotifyingObject
{
    private readonly Action<string>? openLink;
    private readonly Func<ChecksRowViewModel, CancellationToken, Task<PrChecksJobLog>> loadLog;
    private PrCheck check;
    private string logTail = string.Empty;
    private string logProblem = string.Empty;
    private bool logCut;
    private bool isLogOpen;
    private bool isLogLoading;

    public ChecksRowViewModel(
        PrCheck check,
        Action<string>? openLink,
        Func<ChecksRowViewModel, CancellationToken, Task<PrChecksJobLog>> loadLog)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(loadLog);
        this.check = check;
        this.openLink = openLink;
        this.loadLog = loadLog;
        ToggleLogCommand = new AsyncRelayCommand(_ => ToggleLogAsync(), () => IsFailing);
        OpenLinkCommand = new RelayCommand(OpenLink, () => HasLink);
    }

    /// <summary>The platform's record of this check as of the latest reading.</summary>
    public PrCheck Check => check;

    /// <summary>Stable across polls (name plus job id), so the panel can carry a row over to the next reading.</summary>
    public string Key => KeyOf(check);

    public string Name => check.Name;

    /// <summary>The workflow that reported the check; empty for an external status context.</summary>
    public string WorkflowText => check.Workflow;

    public bool HasWorkflow => check.Workflow.Length > 0;

    /// <summary>The raw state ("IN_PROGRESS", "FAILURE"), kept so the row shows what the platform actually said.</summary>
    public string StateText => check.State;

    public string DescriptionText => check.Description.ReplaceLineEndings(" ");

    public bool HasDescription => DescriptionText.Length > 0;

    /// <summary>The raw state ("IN_PROGRESS", "FAILURE"), kept so the row shows what the platform actually said.</summary>
    public string BucketText => Loc.Instance[check.Bucket switch
    {
        PrCheckBucket.Pass => ChecksStrings.BucketPass,
        PrCheckBucket.Fail => ChecksStrings.BucketFail,
        PrCheckBucket.Pending => ChecksStrings.BucketPending,
        PrCheckBucket.Skipping => ChecksStrings.BucketSkipped,
        PrCheckBucket.Cancel => ChecksStrings.BucketCancelled,
        _ => ChecksStrings.BucketUnknown,
    }];

    public bool IsFailing => check.Bucket is PrCheckBucket.Fail or PrCheckBucket.Cancel;

    public bool IsPending => check.Bucket is PrCheckBucket.Pending or PrCheckBucket.Unknown;

    public bool IsPassed => check.Bucket is PrCheckBucket.Pass or PrCheckBucket.Skipping;

    /// <summary>How long the check took, when the platform reported both of its timestamps; empty otherwise.</summary>
    public string DurationText =>
        check is { StartedAt: { } started, CompletedAt: { } completed } && completed > started
            ? TextFormat.Elapsed(completed - started)
            : string.Empty;

    public bool HasDuration => DurationText.Length > 0;

    public string Link => check.Link ?? string.Empty;

    public bool HasLink => Link.Length > 0;

    /// <summary>The check has a workflow job behind it, so its log can be read; an external status context has none.</summary>
    public bool HasLog => check.JobId is not null;

    /// <summary>The row's log is expanded.</summary>
    public bool IsLogOpen => isLogOpen;

    /// <summary>The row's log is being read right now.</summary>
    public bool IsLogLoading => isLogLoading;

    /// <summary>The tail of the job log, once it was read.</summary>
    public string LogTail => logTail;

    public bool HasLogTail => logTail.Length > 0;

    /// <summary>Why the log could not be read, or that this check has none; empty while it was never asked.</summary>
    public string LogProblem => logProblem;

    public bool HasLogProblem => logProblem.Length > 0;

    /// <summary>The platform cut the log at its capture limit, or the configured tail did.</summary>
    public bool IsLogCut => logCut;

    public string ToggleLogLabel => Loc.Instance[isLogOpen ? ChecksStrings.ButtonHideLog : ChecksStrings.ButtonLog];

    /// <summary>Reads the log and expands it; expands or collapses an already-read one without a platform call.</summary>
    public AsyncRelayCommand ToggleLogCommand { get; }

    public RelayCommand OpenLinkCommand { get; }

    /// <summary>Stable identity of a check across polls: name plus job id, or name plus workflow for external contexts.</summary>
    public static string KeyOf(PrCheck check) =>
        check.JobId is { } job
            ? check.Name + "#" + job.ToString(CultureInfo.InvariantCulture)
            : check.Name + "#" + check.Workflow;

    /// <summary>Asks the row to re-read its localized texts after a language switch.</summary>
    internal void Relocalize() => OnPropertyChanged(null);

    /// <summary>Takes the next reading of this same check, keeping what the user already loaded.</summary>
    public ChecksRowViewModel Adopt(PrCheck next)
    {
        ArgumentNullException.ThrowIfNull(next);
        check = next;
        OnPropertyChanged(nameof(Check));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(BucketText));
        OnPropertyChanged(nameof(IsFailing));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsPassed));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(HasDuration));
        OnPropertyChanged(nameof(Link));
        OnPropertyChanged(nameof(HasLink));
        OnPropertyChanged(nameof(HasLog));
        OpenLinkCommand.NotifyCanExecuteChanged();
        ToggleLogCommand.NotifyCanExecuteChanged();
        return this;
    }

    private async Task ToggleLogAsync()
    {
        if (isLogOpen)
        {
            isLogOpen = false;
            RaiseLog();
            return;
        }

        if (HasLogTail || HasLogProblem)
        {
            isLogOpen = true;
            RaiseLog();
            return;
        }

        if (!HasLog)
        {
            logProblem = Loc.Instance[ChecksStrings.LogNone];
            isLogOpen = true;
            RaiseLog();
            return;
        }

        isLogLoading = true;
        RaiseLog();
        try
        {
            var result = await loadLog(this, CancellationToken.None).ConfigureAwait(true);
            logTail = result.Tail;
            logProblem = result.Error is { Length: > 0 } error
                ? Loc.Instance.Format(ChecksStrings.LogFailed, error)
                : string.Empty;
            logCut = result.Truncated;
            isLogOpen = true;
        }
        finally
        {
            isLogLoading = false;
            RaiseLog();
        }
    }

    private void OpenLink()
    {
        if (HasLink)
        {
            openLink?.Invoke(Link);
        }
    }

    private void RaiseLog()
    {
        OnPropertyChanged(nameof(IsLogOpen));
        OnPropertyChanged(nameof(IsLogLoading));
        OnPropertyChanged(nameof(LogTail));
        OnPropertyChanged(nameof(HasLogTail));
        OnPropertyChanged(nameof(LogProblem));
        OnPropertyChanged(nameof(HasLogProblem));
        OnPropertyChanged(nameof(IsLogCut));
        OnPropertyChanged(nameof(ToggleLogLabel));
    }
}
