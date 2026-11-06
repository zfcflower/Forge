using System.Diagnostics;
using Forge.App.Localization;
using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>
/// One wait on a pull request's checks, for the Git panel: runs <see cref="PrChecksPoller.WaitAsync"/> on a pool thread
/// and hands every reading and the final <see cref="PrWaitResult"/> to the UI thread through the <c>post</c> seam (the
/// panel owns the view state; nothing here touches it). When the wait ends for any reason except cancellation it raises one
/// localized system notification naming the pull request and how the wait ended.
/// <para>The watcher belongs to one worktree and one pull request and is used once: <see cref="Start"/> begins it,
/// <see cref="Cancel"/> ends it (idempotent, safe from any thread), and after a cancellation nothing more is delivered - not
/// a reading that was already posted, not the result, not a notification - so a cancelled wait can never overwrite what the
/// user sees afterwards. <see cref="Completion"/> finishes when the pool work is over (including the notification) and never
/// faults.</para>
/// <para>Every platform failure is a <see cref="PrWaitResult"/> of the poller (<see cref="PrWaitOutcome.Failed"/>); an
/// unexpected exception out of the poller is turned into the same result instead of escaping a background task.</para>
/// </summary>
internal sealed class PrWatcher : IDisposable
{
    private readonly PrChecksPoller poller;
    private readonly PrChecksPollerOptions options;
    private readonly Action<Action> post;
    private readonly Action<PrChecksObservation> onProgress;
    private readonly Action<PrWaitResult> onCompleted;
    private readonly ISystemNotifier? notifier;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Lock gate = new();
    private Task completion = Task.CompletedTask;
    private bool started;
    private bool cancelled;
    private bool finished;

    /// <param name="poller">Shared with the <c>pr_checks</c> tool, so the panel and the agent reach the same verdict.</param>
    /// <param name="workingDirectory">The worktree the pull request belongs to; gh runs there.</param>
    /// <param name="number">The pull request.</param>
    /// <param name="options">Timing of this wait; read once by the caller when the wait starts.</param>
    /// <param name="post">
    /// Runs an action on the UI thread without waiting for it (<c>UiThreadDispatcher.Instance.Post</c> in the app, an
    /// inline or queued call in a test).
    /// </param>
    /// <param name="onProgress">Called on the UI thread for every successful reading.</param>
    /// <param name="onCompleted">Called on the UI thread once, with how the wait ended (never after <see cref="Cancel"/>).</param>
    /// <param name="notifier">Receives the end-of-wait notification; null means none.</param>
    public PrWatcher(
        PrChecksPoller poller,
        string workingDirectory,
        int number,
        PrChecksPollerOptions options,
        Action<Action> post,
        Action<PrChecksObservation> onProgress,
        Action<PrWaitResult> onCompleted,
        ISystemNotifier? notifier = null)
    {
        ArgumentNullException.ThrowIfNull(poller);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(onProgress);
        ArgumentNullException.ThrowIfNull(onCompleted);

        this.poller = poller;
        WorkingDirectory = workingDirectory;
        Number = number;
        this.options = options;
        this.post = post;
        this.onProgress = onProgress;
        this.onCompleted = onCompleted;
        this.notifier = notifier;
    }

    public string WorkingDirectory { get; }

    public int Number { get; }

    /// <summary>True from <see cref="Start"/> until the wait ended or was cancelled.</summary>
    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return started && !cancelled && !finished;
            }
        }
    }

    /// <summary>True once <see cref="Cancel"/> was called.</summary>
    public bool IsCancelled
    {
        get
        {
            lock (gate)
            {
                return cancelled;
            }
        }
    }

    /// <summary>Finishes when the pool work is over, notification included; never faults. Completed before <see cref="Start"/>.</summary>
    public Task Completion
    {
        get
        {
            lock (gate)
            {
                return completion;
            }
        }
    }

    /// <summary>Starts the wait on the thread pool. A second call, or a call after <see cref="Cancel"/>, does nothing.</summary>
    public void Start()
    {
        lock (gate)
        {
            if (started || cancelled)
            {
                return;
            }

            started = true;
            completion = Task.Run(RunAsync);
        }
    }

    /// <summary>Stops the wait; the poller's delay and any gh call in flight are cancelled. Safe to call at any time, any number of times.</summary>
    public void Cancel()
    {
        lock (gate)
        {
            if (cancelled)
            {
                return;
            }

            cancelled = true;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposed after the wait ended: nothing left to stop.
        }
    }

    /// <summary>
    /// Cancels the wait. The cancellation source is released when the pool work has unwound (at the end of the run), or here
    /// when the watcher was never started.
    /// </summary>
    public void Dispose()
    {
        bool neverStarted;
        lock (gate)
        {
            neverStarted = !started;
        }

        Cancel();
        if (neverStarted)
        {
            cancellation.Dispose();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await RunCoreAsync(cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task RunCoreAsync(CancellationToken token)
    {
        PrWaitResult result;
        try
        {
            result = await poller
                .WaitAsync(WorkingDirectory, Number, options, new Reporter(this), token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            MarkFinished();
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The poller reports platform trouble as a result; anything else (a bad option, a bug) is shown the same way
            // instead of faulting a task nobody awaits.
            Trace.WriteLine("[watch] the wait failed: " + exception.Message);
            result = new PrWaitResult(
                PrWaitOutcome.Failed,
                null,
                0,
                TimeSpan.Zero,
                Failure: new GitRemoteResult(GitRemoteFailureKind.Other, exception.Message, string.Empty));
        }

        MarkFinished();
        if (IsCancelled)
        {
            return;
        }

        Post(() => onCompleted(result));
        await NotifyAsync(result, token).ConfigureAwait(false);
    }

    private void MarkFinished()
    {
        lock (gate)
        {
            finished = true;
        }
    }

    private async Task NotifyAsync(PrWaitResult result, CancellationToken token)
    {
        if (notifier is null)
        {
            return;
        }

        try
        {
            await notifier
                .NotifyAsync(
                    Loc.Instance.Format(GitStrings.NotifyTitle, Number),
                    PrWatchText.Outcome(result),
                    token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.WriteLine("[watch] the notification failed: " + exception.Message);
        }
    }

    /// <summary>Delivers an action to the UI thread unless the wait was cancelled before it got there.</summary>
    private void Post(Action action)
    {
        post(() =>
        {
            if (!IsCancelled)
            {
                action();
            }
        });
    }

    /// <summary>The progress channel: not <see cref="Progress{T}"/>, which would capture whatever context the pool thread has.</summary>
    private sealed class Reporter(PrWatcher owner) : IProgress<PrChecksObservation>
    {
        public void Report(PrChecksObservation value) => owner.Post(() => owner.onProgress(value));
    }
}
