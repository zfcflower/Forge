using System.Collections.ObjectModel;
using System.Diagnostics;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>
/// The pull request half of the Git tab: the branch's pull request, the "Wait for checks" watch with its live progress and
/// verdict, the CI marker of the status bar, and the read-only list of review threads.
/// <para>It exists only when the panel was given <see cref="GitPrServices"/>; without them every member is inert and no gh
/// process is ever started. Local git never depends on any of it: the pull request is read in a second phase after the
/// local snapshot was applied, on the thread pool, under its own cancellation source that every context change, a clear and
/// <see cref="Dispose"/> cancel (a cancelled read applies nothing). That phase is deliberately not part of
/// <see cref="IsBusy"/>: gh can take seconds and must not disable fetch, pull and push meanwhile. It is skipped for a
/// repository without remotes or without a checked-out branch, where no pull request can exist.</para>
/// <para>A watch belongs to the worktree that started it (<see cref="WorktreeState"/>): switching to another session does
/// not stop it, attaching the worktree again shows its progress or verdict, and it ends on <see cref="CancelWaitCommand"/>,
/// <see cref="ReleaseSession"/> (the session that started it closed) or <see cref="Dispose"/>. A worktree with a running
/// watch is never evicted from the bounded table.</para>
/// </summary>
internal sealed partial class GitPanelViewModel
{
    private readonly GitPrServices? pullRequests;

    /// <summary>The pull request read of the attached context; cancelled and cleared by every context change and by <see cref="Dispose"/>.</summary>
    private CancellationTokenSource? pullRequestSource;

    private PullRequestPhase pullRequestPhase = PullRequestPhase.None;
    private PullRequestInfo? pullRequest;
    private string pullRequestDetail = string.Empty;
    private ReviewPhase reviewPhase = ReviewPhase.None;
    private IReadOnlyList<PrReviewThread> reviewThreads = [];
    private string reviewDetail = string.Empty;

    private enum PullRequestPhase
    {
        /// <summary>Nothing to show: the feature is off, there is no repository, remote or branch, or no read has happened yet.</summary>
        None,
        Loading,

        /// <summary>gh is missing or not signed in; <see cref="pullRequestDetail"/> says why.</summary>
        NotReady,
        NoPullRequest,

        /// <summary>The read failed for another reason; <see cref="pullRequestDetail"/> is the platform's message.</summary>
        Failed,
        Loaded,
    }

    private enum ReviewPhase
    {
        None,
        Loading,
        Loaded,
        Failed,
    }

    /// <summary>Starts "Wait for checks" on the branch's pull request; disabled while a watch of this worktree runs.</summary>
    public RelayCommand WaitForChecksCommand { get; }

    /// <summary>Stops the watch of the attached worktree.</summary>
    public RelayCommand CancelWaitCommand { get; }

    /// <summary>The review threads of the pull request, unresolved ones first; replaced as a whole by every read.</summary>
    public ObservableCollection<GitReviewThreadItem> ReviewThreads { get; } = [];

    // ---------------------------------------------------------------- the pull request

    /// <summary>True when the panel shows anything about a pull request (a header, a notice or the gh hint).</summary>
    public bool ShowPullRequest => pullRequestPhase != PullRequestPhase.None;

    public bool HasPullRequest => pullRequestPhase == PullRequestPhase.Loaded && pullRequest is not null;

    /// <summary>"#12 Title" of the branch's pull request; empty when there is none.</summary>
    public string PullRequestHeaderText => pullRequest is { } info && HasPullRequest
        ? Loc.Instance.Format(GitStrings.PullRequestHeader, info.Number, info.Title)
        : string.Empty;

    /// <summary>"Open", "Draft", "Closed" or "Merged".</summary>
    public string PullRequestStateText => pullRequest is { } info && HasPullRequest
        ? info.State switch
        {
            PrState.Merged => Loc.Instance[GitStrings.PullRequestStateMerged],
            PrState.Closed => Loc.Instance[GitStrings.PullRequestStateClosed],
            _ => Loc.Instance[info.IsDraft ? GitStrings.PullRequestStateDraft : GitStrings.PullRequestStateOpen],
        }
        : string.Empty;

    /// <summary>"Reading the pull request…", "This branch has no pull request yet." or the read failure; empty otherwise.</summary>
    public string PullRequestNoticeText => pullRequestPhase switch
    {
        PullRequestPhase.Loading => Loc.Instance[GitStrings.PullRequestLoading],
        PullRequestPhase.NoPullRequest => Loc.Instance[GitStrings.PullRequestNone],
        PullRequestPhase.Failed => Loc.Instance.Format(GitStrings.PullRequestReadFailed, pullRequestDetail),
        _ => string.Empty,
    };

    public bool HasPullRequestNotice => PullRequestNoticeText.Length > 0;

    /// <summary>"Install and sign in to gh" while gh is not usable; local git is unaffected.</summary>
    public string GhNotReadyText =>
        pullRequestPhase == PullRequestPhase.NotReady ? Loc.Instance[GitStrings.PullRequestGhNotReady] : string.Empty;

    /// <summary>What the platform said about gh (not installed, not logged in, no connection).</summary>
    public string GhNotReadyDetail => pullRequestPhase == PullRequestPhase.NotReady ? pullRequestDetail : string.Empty;

    public bool HasGhNotReady => pullRequestPhase == PullRequestPhase.NotReady;

    public bool HasGhNotReadyDetail => GhNotReadyDetail.Length > 0;

    // ---------------------------------------------------------------- waiting for the checks

    /// <summary>True while the attached worktree has a running watch.</summary>
    public bool IsWatching => !disposed && current.Watch is { Running: true };

    /// <summary>The "Wait for checks" button: the branch has a pull request and no watch is running.</summary>
    public bool ShowWaitButton => HasPullRequest && !IsWatching;

    /// <summary>The "Cancel" button that replaces it while waiting.</summary>
    public bool ShowCancelWait => IsWatching;

    /// <summary>True while waiting, and afterwards as long as a reading is known.</summary>
    public bool HasWatchProgress => IsWatching || WatchObservation is not null;

    /// <summary>"3 passed · 1 failed · 2 pending"; "Waiting for checks…" before the first reading.</summary>
    public string WatchProgressText => WatchObservation is { } observation
        ? PrWatchText.Progress(observation)
        : IsWatching ? Loc.Instance[GitStrings.WatchWaiting] : string.Empty;

    /// <summary>"Merge state: CLEAN" with the platform's raw text; empty before the first reading.</summary>
    public string WatchMergeText => WatchObservation is { } observation ? PrWatchText.MergeState(observation) : string.Empty;

    /// <summary>How the last wait of this worktree ended, in a sentence; "Waiting was cancelled." after a cancel; empty while waiting.</summary>
    public string WatchVerdictText => current.Watch switch
    {
        { Result: { } result } => PrWatchText.Outcome(result),
        { Cancelled: true } => Loc.Instance[GitStrings.WatchCancelled],
        _ => string.Empty,
    };

    public bool HasWatchVerdict => WatchVerdictText.Length > 0;

    /// <summary>True when the verdict asks the user to do something (failed checks, conflicts, a missing review, a timeout).</summary>
    public bool WatchVerdictIsProblem => current.Watch is { Result: { } result } && PrWatchText.IsProblem(result.Outcome);

    /// <summary>"CI ✓", "CI ✗", "CI …" and so on for the status bar; empty when the attached worktree has no watch to report.</summary>
    public string ChecksText => current.Watch switch
    {
        { Result: { } result } => PrWatchText.Finished(result.Outcome),
        { Running: true } watch => PrWatchText.Running(watch.Observation),
        _ => string.Empty,
    };

    private PrChecksObservation? WatchObservation => current.Watch?.Observation;

    // ---------------------------------------------------------------- review threads

    /// <summary>The "Review" group: shown with the pull request.</summary>
    public bool ShowReview => HasPullRequest;

    /// <summary>"3 threads, 1 unresolved"; empty until the threads are read or when there are none.</summary>
    public string ReviewSummaryText => reviewPhase == ReviewPhase.Loaded && reviewThreads.Count > 0
        ? Loc.Instance.Format(GitStrings.ReviewSummary, reviewThreads.Count, CountUnresolved(reviewThreads))
        : string.Empty;

    /// <summary>"Reading the review threads…", "No review threads." or the read failure; empty while threads are listed.</summary>
    public string ReviewNoticeText => reviewPhase switch
    {
        ReviewPhase.Loading when ReviewThreads.Count == 0 => Loc.Instance[GitStrings.ReviewLoading],
        ReviewPhase.Loaded when reviewThreads.Count == 0 => Loc.Instance[GitStrings.ReviewEmpty],
        ReviewPhase.Failed => Loc.Instance.Format(GitStrings.ReviewReadFailed, reviewDetail),
        _ => string.Empty,
    };

    public bool HasReviewNotice => ReviewNoticeText.Length > 0;

    public bool HasReviewThreads => ReviewThreads.Count > 0;

    // ---------------------------------------------------------------- session lifecycle

    /// <summary>
    /// Stops the watches that <paramref name="sessionId"/> started: its tab was closed or the session was archived. A watch
    /// another session started on the same worktree is left alone.
    /// </summary>
    public void ReleaseSession(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (disposed)
        {
            return;
        }

        var any = false;
        foreach (var state in worktrees.Values)
        {
            if (state.Watch is { Running: true } watch && string.Equals(watch.SessionId, sessionId, StringComparison.Ordinal))
            {
                StopWatch(watch);
                any = true;
            }
        }

        if (any)
        {
            RaisePullRequestTexts();
            NotifyCommands();
        }
    }

    // ---------------------------------------------------------------- the watch

    private bool CanWaitForChecks() =>
        !disposed && pullRequests is not null && HasContext && snapshot.HasRepository && HasPullRequest && !IsWatching;

    private bool CanCancelWait() => IsWatching;

    private void StartWatch()
    {
        if (!CanWaitForChecks() || pullRequests is not { } services || pullRequest is not { } info)
        {
            return;
        }

        var state = current;
        var watch = new WatchState(context.SessionId, info.Number);
        var watcher = new PrWatcher(
            services.Poller,
            context.WorkingDirectory,
            info.Number,
            services.Options(),
            services.PostToUi,
            observation => OnWatchProgress(state, watch, observation),
            result => OnWatchCompleted(state, watch, result),
            services.Notifier);
        watch.Watcher = watcher;
        state.Watch = watch;
        RaisePullRequestTexts();
        NotifyCommands();
        watcher.Start();
    }

    private void CancelWait()
    {
        if (current.Watch is not { Running: true } watch)
        {
            return;
        }

        StopWatch(watch);
        RaisePullRequestTexts();
        NotifyCommands();
    }

    /// <summary>Marks the watch cancelled first, so a reading that is already on its way to the UI thread is dropped, then stops it.</summary>
    private static void StopWatch(WatchState watch)
    {
        watch.Cancelled = true;
        watch.Watcher?.Cancel();
    }

    /// <summary>A reading of a watch; ignored once the watch was replaced, cancelled or finished.</summary>
    private void OnWatchProgress(WorktreeState state, WatchState watch, PrChecksObservation observation)
    {
        if (disposed || !ReferenceEquals(state.Watch, watch) || !watch.Running)
        {
            return;
        }

        watch.Observation = observation;
        if (IsCurrent(state))
        {
            RaisePullRequestTexts();
        }
    }

    /// <summary>The end of a watch; ignored when the watch was cancelled or replaced meanwhile.</summary>
    private void OnWatchCompleted(WorktreeState state, WatchState watch, PrWaitResult result)
    {
        if (disposed || !ReferenceEquals(state.Watch, watch) || watch.Cancelled)
        {
            return;
        }

        watch.Result = result;
        watch.Observation = result.Observation ?? watch.Observation;
        if (IsCurrent(state))
        {
            RaisePullRequestTexts();
            NotifyCommands();
        }
    }

    // ---------------------------------------------------------------- reading the pull request

    /// <summary>
    /// Phase two of a read: starts the pull request lookup for the context whose local snapshot was just applied. A previous
    /// lookup is cancelled; what was on screen stays until the new answer arrives (a refresh does not flicker), and only a
    /// panel that shows nothing yet announces "reading".
    /// </summary>
    private void BeginPullRequestRead()
    {
        CancelPullRequestRead();
        if (pullRequests is not { } services || disposed || !HasContext || !CanHavePullRequest())
        {
            ResetPullRequest();
            RaisePullRequestTexts();
            NotifyCommands();
            return;
        }

        if (pullRequestPhase == PullRequestPhase.None)
        {
            pullRequestPhase = PullRequestPhase.Loading;
            RaisePullRequestTexts();
        }

        var source = new CancellationTokenSource();
        pullRequestSource = source;
        _ = LoadPullRequestAsync(services.Platform, context.WorkingDirectory, source);
    }

    /// <summary>A pull request needs a repository with a remote and a checked-out branch.</summary>
    private bool CanHavePullRequest() =>
        snapshot.Status is { Branch: { Length: > 0 } } && snapshot.Remotes.Count > 0;

    private async Task LoadPullRequestAsync(IPrPlatform platform, string workingDirectory, CancellationTokenSource source)
    {
        var token = source.Token;
        try
        {
            var read = await Task.Run(() => ReadPullRequestAsync(platform, workingDirectory, token), token).ConfigureAwait(true);
            if (!IsCurrentPullRequestRead(source))
            {
                return;
            }

            ApplyPullRequest(read);
            if (read.Info is not { } info)
            {
                return;
            }

            var threads = await Task.Run(() => platform.GetReviewThreadsAsync(workingDirectory, info.Number, token), token)
                .ConfigureAwait(true);
            if (IsCurrentPullRequestRead(source))
            {
                ApplyReviewThreads(threads);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A platform that throws instead of reporting (it must not) is shown like any other failed read.
            Trace.WriteLine("[git] the pull request read failed: " + exception.Message);
            if (IsCurrentPullRequestRead(source))
            {
                ApplyPullRequest(PullRequestRead.Failed(exception.Message));
            }
        }
        finally
        {
            if (ReferenceEquals(pullRequestSource, source))
            {
                pullRequestSource = null;
            }

            source.Dispose();
        }
    }

    /// <summary>gh availability, then the branch's pull request; runs on a pool thread.</summary>
    private static async Task<PullRequestRead> ReadPullRequestAsync(IPrPlatform platform, string workingDirectory, CancellationToken ct)
    {
        var availability = await platform.GetAvailabilityAsync(workingDirectory, ct).ConfigureAwait(false);
        if (!availability.IsReady)
        {
            return PullRequestRead.NotReady(availability.Detail);
        }

        var view = await platform.ViewPullRequestAsync(workingDirectory, null, ct).ConfigureAwait(false);
        if (view.Success && view.Value is { } info)
        {
            return PullRequestRead.Found(info);
        }

        if (view.PullRequestNotFound)
        {
            return PullRequestRead.NoPullRequest;
        }

        return view.Reason == PrFailureReason.PlatformNotInstalled
            ? PullRequestRead.NotReady(view.Status.Message)
            : PullRequestRead.Failed(view.Status.Message);
    }

    private void ApplyPullRequest(PullRequestRead read)
    {
        pullRequestPhase = read.Phase;
        pullRequest = read.Info;
        pullRequestDetail = read.Detail;
        if (read.Info is null)
        {
            ResetReview();
        }
        else if (reviewPhase == ReviewPhase.None)
        {
            reviewPhase = ReviewPhase.Loading;
        }

        RaisePullRequestTexts();
        NotifyCommands();
    }

    private void ApplyReviewThreads(PrResult<IReadOnlyList<PrReviewThread>> result)
    {
        if (result.Success && result.Value is { } threads)
        {
            reviewThreads = threads;
            reviewPhase = ReviewPhase.Loaded;
            reviewDetail = string.Empty;
            RebuildReviewItems();
        }
        else
        {
            reviewPhase = ReviewPhase.Failed;
            reviewDetail = result.Status.Message;
            reviewThreads = [];
            ReviewThreads.Clear();
        }

        RaisePullRequestTexts();
    }

    /// <summary>Lists the threads unresolved first (the order within each group is the platform's); also run after a language switch.</summary>
    private void RebuildReviewItems()
    {
        ReviewThreads.Clear();
        foreach (var thread in reviewThreads)
        {
            if (!thread.IsResolved)
            {
                ReviewThreads.Add(new GitReviewThreadItem(thread));
            }
        }

        foreach (var thread in reviewThreads)
        {
            if (thread.IsResolved)
            {
                ReviewThreads.Add(new GitReviewThreadItem(thread));
            }
        }
    }

    private bool IsCurrentPullRequestRead(CancellationTokenSource source) =>
        !disposed && ReferenceEquals(pullRequestSource, source);

    private void CancelPullRequestRead()
    {
        var running = pullRequestSource;
        if (running is null)
        {
            return;
        }

        // The read's owner disposes the source once its task has unwound; here it only stops counting as the current one.
        pullRequestSource = null;
        running.Cancel();
    }

    /// <summary>Forgets everything shown about a pull request (not the watches, which belong to their worktrees).</summary>
    private void ResetPullRequest()
    {
        pullRequestPhase = PullRequestPhase.None;
        pullRequest = null;
        pullRequestDetail = string.Empty;
        ResetReview();
    }

    private void ResetReview()
    {
        reviewPhase = ReviewPhase.None;
        reviewThreads = [];
        reviewDetail = string.Empty;
        ReviewThreads.Clear();
    }

    /// <summary>Stops every watch and the pull request read; called once by <see cref="Dispose"/>.</summary>
    private void DisposePullRequests()
    {
        CancelPullRequestRead();
        foreach (var state in worktrees.Values)
        {
            if (state.Watch is { Running: true } watch)
            {
                StopWatch(watch);
            }
        }
    }

    private void RaisePullRequestTexts()
    {
        OnPropertyChanged(nameof(ShowPullRequest));
        OnPropertyChanged(nameof(HasPullRequest));
        OnPropertyChanged(nameof(PullRequestHeaderText));
        OnPropertyChanged(nameof(PullRequestStateText));
        OnPropertyChanged(nameof(PullRequestNoticeText));
        OnPropertyChanged(nameof(HasPullRequestNotice));
        OnPropertyChanged(nameof(GhNotReadyText));
        OnPropertyChanged(nameof(GhNotReadyDetail));
        OnPropertyChanged(nameof(HasGhNotReady));
        OnPropertyChanged(nameof(HasGhNotReadyDetail));
        OnPropertyChanged(nameof(IsWatching));
        OnPropertyChanged(nameof(ShowWaitButton));
        OnPropertyChanged(nameof(ShowCancelWait));
        OnPropertyChanged(nameof(HasWatchProgress));
        OnPropertyChanged(nameof(WatchProgressText));
        OnPropertyChanged(nameof(WatchMergeText));
        OnPropertyChanged(nameof(HasWatchVerdict));
        OnPropertyChanged(nameof(WatchVerdictText));
        OnPropertyChanged(nameof(WatchVerdictIsProblem));
        OnPropertyChanged(nameof(ChecksText));
        OnPropertyChanged(nameof(ShowReview));
        OnPropertyChanged(nameof(ReviewSummaryText));
        OnPropertyChanged(nameof(ReviewNoticeText));
        OnPropertyChanged(nameof(HasReviewNotice));
        OnPropertyChanged(nameof(HasReviewThreads));
        RaiseScreenshotTexts();
    }

    private static int CountUnresolved(IReadOnlyList<PrReviewThread> threads)
    {
        var count = 0;
        foreach (var thread in threads)
        {
            if (!thread.IsResolved)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// What the panel remembers about the wait of one worktree: the watcher while it runs, the last reading, and how it ended.
    /// Lives on its <see cref="WorktreeState"/> and is read and written on the UI thread only.
    /// </summary>
    private sealed class WatchState(string sessionId, int number)
    {
        /// <summary>The session that started the wait; closing that session stops it.</summary>
        public string SessionId { get; } = sessionId;

        public int Number { get; } = number;

        public PrWatcher? Watcher { get; set; }

        public PrChecksObservation? Observation { get; set; }

        public PrWaitResult? Result { get; set; }

        public bool Cancelled { get; set; }

        public bool Running => Result is null && !Cancelled;
    }

    /// <summary>The answer of one pull request lookup, built on a pool thread and applied in one go.</summary>
    private sealed record PullRequestRead(PullRequestPhase Phase, PullRequestInfo? Info, string Detail)
    {
        public static PullRequestRead NoPullRequest { get; } = new(PullRequestPhase.NoPullRequest, null, string.Empty);

        public static PullRequestRead Found(PullRequestInfo info) => new(PullRequestPhase.Loaded, info, string.Empty);

        public static PullRequestRead NotReady(string detail) => new(PullRequestPhase.NotReady, null, detail);

        public static PullRequestRead Failed(string detail) => new(PullRequestPhase.Failed, null, detail);
    }
}
