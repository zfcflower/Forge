using System.Collections.ObjectModel;
using Forge.App.Localization;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>
/// Which threads the review-comment panel lists. Every thread lands in exactly one of the three, so the chip counts add up
/// to the number of threads read: an outdated thread is listed under 过期 even when it is also resolved (that is the one
/// state the diff cannot show any more, and the user goes there to see where the comment used to be).
/// </summary>
internal enum CommentFilter
{
    /// <summary>Unresolved and still anchored in the diff: the threads somebody has to answer.</summary>
    Open,

    /// <summary>Resolved and still anchored: what the review already settled.</summary>
    Resolved,

    /// <summary>The diff moved past the line: the thread keeps its original line and can no longer be replied to in place.</summary>
    Outdated,
}

/// <summary>
/// The pull request review-thread panel (wave W16a, L2): every review thread of one pull request under three filters, one
/// thread's location and comments (Markdown), a reply box that posts through the platform, resolve, and the hand-off of a
/// thread to the open session's agent.
/// <para>Reads run <see cref="IPrPlatform.GetReviewThreadsAsync"/> on a background task and report back through
/// <c>post</c>, so every field here is written on the UI thread only. There is no poll: threads change when somebody
/// reviews, so <see cref="RefreshCommand"/> is the one way to read them again, exactly like the checks panel after its
/// verdict settled.</para>
/// <para>Writes are the user's own: <see cref="PrCommentThreadViewModel.ReplyCommand"/> posts a comment as them, and a
/// resolve closes the thread. Nothing here changes code, pushes, or merges. A failed write is shown on the row and the
/// typed text stays in the box, so a network failure costs nothing.</para>
/// <para>Resolving is idempotent: a thread the platform already reports as resolved is not resolved a second time — the
/// row says so instead. Reopening has no platform call behind it in this build (<see cref="IPrPlatform"/> only resolves
/// threads), so the action is offered disabled with that reason rather than hidden or silently dead.</para>
/// </summary>
internal sealed class PrCommentsViewModel : ObservableObject, IDisposable
{
    private readonly string workingDirectory;
    private readonly int pullRequestNumber;
    private readonly string pullRequestUrl;
    private readonly IPrPlatform platform;
    private readonly Action<Action> post;
    private readonly Action<string>? openLink;
    private readonly Func<string, Task<ChecksHandOff>>? handToAgent;
    private readonly Action? close;
    private readonly TimeProvider time;

    /// <summary>The panel's lifetime: a read or a write still in flight when the panel closes never lands.</summary>
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>Which read the fields belong to; a read replaced by a refresh may not overwrite the newer one.</summary>
    private int generation;

    /// <summary>Every thread of the latest reading, in the platform's order (oldest first); the filters select from it.</summary>
    private readonly List<PrCommentThreadViewModel> all = [];
    private CommentFilter filter = CommentFilter.Open;
    private bool isReading;
    private bool hasRead;
    private string problem = string.Empty;
    private string status = string.Empty;
    private DateTimeOffset? readAt;
    private bool disposed;

    /// <summary>The thread the panel was built on (the UI thread); a language notification from another one is not ours.</summary>
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    public PrCommentsViewModel(
        string workingDirectory,
        int pullRequestNumber,
        string? pullRequestUrl,
        IPrPlatform platform,
        Action<Action> post,
        Action<string>? openLink = null,
        Func<string, Task<ChecksHandOff>>? handToAgent = null,
        Action? close = null,
        TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequestNumber);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(post);

        this.workingDirectory = workingDirectory;
        this.pullRequestNumber = pullRequestNumber;
        this.pullRequestUrl = pullRequestUrl ?? string.Empty;
        this.platform = platform;
        this.post = post;
        this.openLink = openLink;
        this.handToAgent = handToAgent;
        this.close = close;
        this.time = time ?? TimeProvider.System;

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), () => !disposed);
        CloseCommand = new RelayCommand(Close);
        OpenPullRequestCommand = new RelayCommand(() => Open(this.pullRequestUrl), () => HasPullRequestUrl);
        ShowOpenCommand = new RelayCommand(() => SetFilter(CommentFilter.Open), () => filter != CommentFilter.Open);
        ShowResolvedCommand = new RelayCommand(() => SetFilter(CommentFilter.Resolved), () => filter != CommentFilter.Resolved);
        ShowOutdatedCommand = new RelayCommand(() => SetFilter(CommentFilter.Outdated), () => filter != CommentFilter.Outdated);

        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>"#12".</summary>
    public string NumberText => PrCommentsStrings.NumberText(pullRequestNumber);

    public bool HasPullRequestUrl => pullRequestUrl.Length > 0;

    /// <summary>The threads the selected filter lists, in the platform's order.</summary>
    public ObservableCollection<PrCommentThreadViewModel> Threads { get; } = [];

    public CommentFilter Filter => filter;

    public bool IsOpenFilter => filter == CommentFilter.Open;

    public bool IsResolvedFilter => filter == CommentFilter.Resolved;

    public bool IsOutdatedFilter => filter == CommentFilter.Outdated;

    /// <summary>The three filter chips, each carrying how many threads are behind it.</summary>
    public string OpenFilterText => PrCommentsStrings.FilterText(CommentFilter.Open, Count(CommentFilter.Open));

    public string ResolvedFilterText => PrCommentsStrings.FilterText(CommentFilter.Resolved, Count(CommentFilter.Resolved));

    public string OutdatedFilterText => PrCommentsStrings.FilterText(CommentFilter.Outdated, Count(CommentFilter.Outdated));

    public bool HasThreads => Threads.Count > 0;

    /// <summary>A successful read found nothing under the selected filter.</summary>
    public bool IsEmpty => hasRead && Threads.Count == 0 && !HasProblem;

    public string EmptyTitleText => PrCommentsStrings.EmptyTitle(filter);

    public string EmptyBodyText => PrCommentsStrings.EmptyBodyText;

    /// <summary>A read is in flight.</summary>
    public bool IsReading
    {
        get => isReading;
        private set => Set(ref isReading, value);
    }

    /// <summary>Why the last read failed (gh missing, not signed in, network); empty when it succeeded.</summary>
    public string ProblemText => problem;

    public bool HasProblem => problem.Length > 0;

    /// <summary>"读取于 12:30:45" of the last successful read.</summary>
    public string LastReadText => readAt is { } at
        ? PrCommentsStrings.ReadAtText(TextFormat.Clock(at))
        : string.Empty;

    public bool HasLastRead => readAt is not null;

    /// <summary>What the last panel-level action produced (a read failure is <see cref="ProblemText"/> instead).</summary>
    public string StatusText => status;

    public bool HasStatus => status.Length > 0;

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand CloseCommand { get; }

    /// <summary>Opens the pull request in the browser (the header's address line).</summary>
    public RelayCommand OpenPullRequestCommand { get; }

    public RelayCommand ShowOpenCommand { get; }

    public RelayCommand ShowResolvedCommand { get; }

    public RelayCommand ShowOutdatedCommand { get; }

    /// <summary>Starts the first read. The window calls this once after opening the panel.</summary>
    public void Start() => RefreshCommand.Execute(null);

    /// <summary>Cancels whatever is in flight. The window calls it when the panel closes; idempotent.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        generation++;
        IsReading = false;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        RefreshCommand.NotifyCanExecuteChanged();
        foreach (var row in all)
        {
            row.Dispose();
        }

        ShowOpenCommand.NotifyCanExecuteChanged();
        ShowResolvedCommand.NotifyCanExecuteChanged();
        ShowOutdatedCommand.NotifyCanExecuteChanged();
        OpenPullRequestCommand.NotifyCanExecuteChanged();

        // Cancel, never dispose: a platform call still holding the token would otherwise turn its registration into an
        // ObjectDisposedException instead of a cancellation.
        lifetime.Cancel();
    }

    private Task RefreshAsync()
    {
        var current = ++generation;
        IsReading = true;
        return ReadAsync(current);
    }

    private async Task ReadAsync(int current)
    {
        PrResult<IReadOnlyList<PrReviewThread>> read;
        try
        {
            read = await platform
                .GetReviewThreadsAsync(workingDirectory, pullRequestNumber, lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            post(() => ApplyFailure(exception.Message, current));
            return;
        }

        post(() => Apply(read, current));
    }

    /// <summary>Writes one reading unless a newer read (or the panel's close) replaced it; runs on the UI thread.</summary>
    private void Apply(PrResult<IReadOnlyList<PrReviewThread>> read, int current)
    {
        if (disposed || current != generation)
        {
            return;
        }

        IsReading = false;
        if (!read.Success || read.Value is not { } threads)
        {
            ApplyFailure(read.Status.Message, current);
            return;
        }

        problem = string.Empty;
        readAt = time.GetUtcNow();
        hasRead = true;
        Adopt(threads);
        Rebuild();
    }

    private void ApplyFailure(string reason, int current)
    {
        if (disposed || current != generation)
        {
            return;
        }

        IsReading = false;
        // A failed read keeps the rows it already had: the threads did not change because the network did.
        problem = reason.Length > 0 ? reason : TextFormat.None;
        RaiseReading();
    }

    /// <summary>
    /// Takes the latest reading: a thread that is still there keeps its row (and with it the typed reply and the status of
    /// the last action), a new one gets a row, and one the platform no longer returns is dropped.
    /// </summary>
    private void Adopt(IReadOnlyList<PrReviewThread> threads)
    {
        var carried = new Dictionary<string, PrCommentThreadViewModel>(all.Count, StringComparer.Ordinal);
        foreach (var row in all)
        {
            carried.TryAdd(row.Id, row);
        }

        all.Clear();
        foreach (var thread in threads)
        {
            all.Add(carried.Remove(thread.Id, out var existing) ? existing.Adopt(thread) : Create(thread));
        }

        foreach (var orphan in carried.Values)
        {
            orphan.Dispose();
        }
    }

    private PrCommentThreadViewModel Create(PrReviewThread thread) => new(
        thread,
        lifetime.Token,
        post,
        platform,
        workingDirectory,
        handToAgent is null ? null : HandOffAsync,
        RaiseReading);

    /// <summary>
    /// Hands one thread to the open session's agent: the panel owns the pull request's address and the session, so it
    /// builds the message; the row only carries the answer back (a turn ran, the prompt waits in the composer, there is no
    /// session).
    /// </summary>
    private async Task<ChecksHandOff> HandOffAsync(PrReviewThread thread)
    {
        if (handToAgent is null)
        {
            return ChecksHandOff.NoSession;
        }

        var message = PrCommentsHandOffMessage.Build(pullRequestNumber, pullRequestUrl, thread);
        return await handToAgent(message).ConfigureAwait(true);
    }

    private void SetFilter(CommentFilter next)
    {
        if (filter == next)
        {
            return;
        }

        filter = next;
        Rebuild();
    }

    /// <summary>Refills the visible list from the reading, keeping the row instances of the threads it shows.</summary>
    private void Rebuild()
    {
        Threads.Clear();
        foreach (var row in all)
        {
            if (row.Filter == filter)
            {
                Threads.Add(row);
            }
        }

        RaiseReading();
    }

    private int Count(CommentFilter bucket)
    {
        var count = 0;
        foreach (var row in all)
        {
            if (row.Filter == bucket)
            {
                count++;
            }
        }

        return count;
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

    private void RaiseReading()
    {
        OnPropertyChanged(nameof(Filter));
        OnPropertyChanged(nameof(IsOpenFilter));
        OnPropertyChanged(nameof(IsResolvedFilter));
        OnPropertyChanged(nameof(IsOutdatedFilter));
        OnPropertyChanged(nameof(OpenFilterText));
        OnPropertyChanged(nameof(ResolvedFilterText));
        OnPropertyChanged(nameof(OutdatedFilterText));
        OnPropertyChanged(nameof(HasThreads));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitleText));
        OnPropertyChanged(nameof(EmptyBodyText));
        OnPropertyChanged(nameof(ProblemText));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(LastReadText));
        OnPropertyChanged(nameof(HasLastRead));
        ShowOpenCommand.NotifyCanExecuteChanged();
        ShowResolvedCommand.NotifyCanExecuteChanged();
        ShowOutdatedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A language switch re-reads every formatted text on the panel and on its rows.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (disposed || Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        RaiseReading();
        foreach (var row in all)
        {
            row.Relocalize();
        }
    }
}

/// <summary>
/// One review thread of the panel: where it sits, its comments as Markdown, the reply box, the resolve action and the
/// hand-off to the agent. The row is built once per thread and adopted by the next reading of the same thread, so a typed
/// reply, an open status line and the last action's result all survive a refresh.
/// <para>Threading: the commands run on the UI thread and hand the platform call to the thread pool; the answer is posted
/// back through the panel's <c>post</c>, so every property here is UI-thread state.</para>
/// </summary>
internal sealed class PrCommentThreadViewModel : NotifyingObject, IDisposable
{
    private readonly CancellationToken panelLifetime;
    private readonly Action<Action> post;
    private readonly IPrPlatform platform;
    private readonly string workingDirectory;
    private readonly Func<PrReviewThread, Task<ChecksHandOff>>? handOff;
    private readonly Action? changed;
    private PrReviewThread thread;
    private string replyDraft = string.Empty;
    private string status = string.Empty;
    private bool statusIsError;
    private bool busy;
    private int actionGeneration;
    private bool disposed;

    public PrCommentThreadViewModel(
        PrReviewThread thread,
        CancellationToken panelLifetime,
        Action<Action> post,
        IPrPlatform platform,
        string workingDirectory,
        Func<PrReviewThread, Task<ChecksHandOff>>? handOff,
        Action? changed = null)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(post);

        this.thread = thread;
        this.panelLifetime = panelLifetime;
        this.post = post;
        this.platform = platform;
        this.workingDirectory = workingDirectory;
        this.handOff = handOff;
        this.changed = changed;

        ReplyCommand = new AsyncRelayCommand(_ => Pending = ReplyAsync(), () => CanReply, ReportFailure);
        ResolveCommand = new AsyncRelayCommand(_ => Pending = ResolveAsync(), () => !busy && !disposed, ReportFailure);
        HandOffCommand = new AsyncRelayCommand(_ => Pending = HandOffAsync(), () => CanHandOff, ReportFailure);
        Fill(thread);
    }

    /// <summary>The platform's id of this thread; the panel carries a row over to the next reading by it.</summary>
    public string Id => thread.Id;

    /// <summary>The latest reading of this thread.</summary>
    public PrReviewThread Thread => thread;

    /// <summary>Where the thread belongs among the three filters (see <see cref="CommentFilter"/>).</summary>
    public CommentFilter Filter => thread.IsOutdated
        ? CommentFilter.Outdated
        : thread.IsResolved ? CommentFilter.Resolved : CommentFilter.Open;

    public bool IsResolved => thread.IsResolved;

    public bool IsOutdated => thread.IsOutdated;

    /// <summary>The comments of the thread in the platform's order (oldest first).</summary>
    public ObservableCollection<PrCommentItemViewModel> Comments { get; } = [];

    public bool HasComments => Comments.Count > 0;

    public string CommentCountText => PrCommentsStrings.CommentCountText(Comments.Count);

    /// <summary>"src/main.cs:42", or the line the thread was first placed on once the diff moved past it.</summary>
    public string LocationText
    {
        get
        {
            var path = PathText;
            if (path.Length == 0)
            {
                return PrCommentsStrings.LocationUnknownText;
            }

            // An outdated thread has no line in the current diff; its original line is the only place it ever was.
            var line = thread.IsOutdated ? thread.OriginalLine ?? thread.Line : thread.Line ?? thread.OriginalLine;
            return line is { } value
                ? PrCommentsStrings.LocationText(path, value, thread.IsOutdated)
                : path;
        }
    }

    /// <summary>True when <see cref="LocationText"/> points at the line the thread was first placed on.</summary>
    public bool IsOriginalLocation => thread.IsOutdated && (thread.OriginalLine ?? thread.Line) is not null;

    public string PathText => thread.Path ?? string.Empty;

    /// <summary>"已解决 · 由 alice 解决", or "已解决"; empty while the thread is open.</summary>
    public string ResolutionText
    {
        get
        {
            if (!thread.IsResolved)
            {
                return string.Empty;
            }

            return thread.ResolvedBy is { Length: > 0 } who
                ? PrCommentsStrings.ResolvedText + " · " + PrCommentsStrings.ResolvedByText(who)
                : PrCommentsStrings.ResolvedText;
        }
    }

    public bool HasResolution => ResolutionText.Length > 0;

    public string OutdatedText => PrCommentsStrings.OutdatedText;

    /// <summary>The reply box's text; a failed post keeps it so the user can retry without typing it again.</summary>
    public string ReplyDraft
    {
        get => replyDraft;
        set
        {
            if (Set(ref replyDraft, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanReply));
                ReplyCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ReplyPlaceholderText => PrCommentsStrings.ReplyPlaceholderText;

    /// <summary>
    /// The thread still takes replies: an outdated one moved out of the diff, and the platform closes a resolved one, so the
    /// box is not offered there at all.
    /// </summary>
    public bool IsReplyable => !thread.IsOutdated && !thread.IsResolved;

    /// <summary>The reply box takes typing right now. It is not the send gate: an empty box must still be editable, or the
    /// first character could never be typed (the composer's send button has the same split).</summary>
    public bool IsReplyEnabled => IsReplyable && !busy && !disposed;

    /// <summary>A reply can be posted right now: the thread is open, the box holds text and no action is running.</summary>
    public bool CanReply => IsReplyEnabled && replyDraft.Trim().Length > 0;

    /// <summary>The row is talking to the platform right now; every action waits for it.</summary>
    public bool IsBusy => busy;

    /// <summary>There is a session to hand this thread to (the window wires one; a standalone view has none).</summary>
    public bool HasHandOff => handOff is not null;

    /// <summary>The hand-off can run right now: a host is wired and the row is not already talking to the platform.</summary>
    public bool CanHandOff => handOff is not null && !busy && !disposed;

    public string ResolveLabel => PrCommentsStrings.ButtonResolveText(thread.IsResolved);

    /// <summary>Reopening is unavailable: <see cref="IPrPlatform"/> resolves threads and has no inverse.</summary>
    public bool CanReopen => false;

    public string ReopenTooltip => thread.IsResolved ? PrCommentsStrings.ReopenUnavailableText : string.Empty;

    public string ReplyLabel => PrCommentsStrings.ButtonReplyText;

    public string HandOffLabel => PrCommentsStrings.ButtonHandOffText;

    /// <summary>What the row's last action produced (posted, resolved, queued, or why it failed).</summary>
    public string StatusText => status;

    public bool HasStatus => status.Length > 0;

    /// <summary>The status line reports a failed write, so it is drawn in the danger colour.</summary>
    public bool StatusIsError => statusIsError;

    public AsyncRelayCommand ReplyCommand { get; }

    public AsyncRelayCommand ResolveCommand { get; }

    public AsyncRelayCommand HandOffCommand { get; }

    /// <summary>
    /// The task of the last action this row started. The commands are <see cref="AsyncRelayCommand"/>s, whose execution is
    /// <c>async void</c>; awaiting this avoids racing the dispatcher.
    /// </summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>Takes the next reading of this same thread, keeping the typed reply and the status line.</summary>
    public PrCommentThreadViewModel Adopt(PrReviewThread next)
    {
        ArgumentNullException.ThrowIfNull(next);
        thread = next;
        Fill(next);
        OnPropertyChanged(nameof(Thread));
        OnPropertyChanged(nameof(Filter));
        OnPropertyChanged(nameof(IsResolved));
        OnPropertyChanged(nameof(IsOutdated));
        OnPropertyChanged(nameof(LocationText));
        OnPropertyChanged(nameof(IsOriginalLocation));
        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(ResolutionText));
        OnPropertyChanged(nameof(HasResolution));
        OnPropertyChanged(nameof(CommentCountText));
        OnPropertyChanged(nameof(IsReplyable));
        OnPropertyChanged(nameof(IsReplyEnabled));
        OnPropertyChanged(nameof(CanReply));
        OnPropertyChanged(nameof(ResolveLabel));
        OnPropertyChanged(nameof(ReopenTooltip));
        OnPropertyChanged(nameof(CanHandOff));
        ReplyCommand.NotifyCanExecuteChanged();
        ResolveCommand.NotifyCanExecuteChanged();
        HandOffCommand.NotifyCanExecuteChanged();
        return this;
    }

    public void Dispose()
    {
        disposed = true;
        actionGeneration++;
    }

    /// <summary>
    /// Posts the typed reply. The comment the platform created is appended to the row, so the answer is visible without a
    /// refresh; on failure the text stays in the box and the reason goes on the row.
    /// </summary>
    private async Task ReplyAsync()
    {
        var body = replyDraft.Trim();
        if (body.Length == 0)
        {
            SetStatus(PrCommentsStrings.ReplyEmptyText, isError: true);
            return;
        }

        var current = Begin();
        PrResult<PrReviewComment> result;
        try
        {
            result = await platform
                .ReplyToReviewThreadAsync(workingDirectory, thread.Id, body, panelLifetime)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            End(current);
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Finish(current, () => SetStatus(PrCommentsStrings.ReplyFailedText(exception.Message), isError: true));
            return;
        }

        Finish(current, () =>
        {
            if (!result.Success || result.Value is not { } comment)
            {
                // The typed text is deliberately kept: retrying after a network failure must not cost the answer.
                SetStatus(PrCommentsStrings.ReplyFailedText(result.Status.Message), isError: true);
                return;
            }

            Comments.Add(new PrCommentItemViewModel(comment));
            OnPropertyChanged(nameof(HasComments));
            OnPropertyChanged(nameof(CommentCountText));
            replyDraft = string.Empty;
            OnPropertyChanged(nameof(ReplyDraft));
            OnPropertyChanged(nameof(CanReply));
            SetStatus(PrCommentsStrings.ReplyDoneText, isError: false);
        });
    }

    /// <summary>
    /// Resolves the thread through the platform. Resolving twice is not an error and not a second write: the row reports
    /// that it was already resolved, which is what a stale row (resolved on the platform meanwhile) runs into.
    /// </summary>
    private async Task ResolveAsync()
    {
        if (thread.IsResolved)
        {
            SetStatus(PrCommentsStrings.ResolveAlreadyText, isError: false);
            return;
        }

        var current = Begin();
        PrResult<PrThreadResolution> result;
        try
        {
            result = await platform
                .ResolveReviewThreadAsync(workingDirectory, thread.Id, panelLifetime)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            End(current);
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Finish(current, () => SetStatus(PrCommentsStrings.ResolveFailedText(exception.Message), isError: true));
            return;
        }

        Finish(current, () =>
        {
            if (!result.Success)
            {
                SetStatus(PrCommentsStrings.ResolveFailedText(result.Status.Message), isError: true);
                return;
            }

            // The platform answers with the thread's state; a call that did not change it must not claim it did.
            if (result.Value is { IsResolved: true })
            {
                thread = thread with { IsResolved = true };
                OnPropertyChanged(nameof(Thread));
                OnPropertyChanged(nameof(IsResolved));
                OnPropertyChanged(nameof(Filter));
                OnPropertyChanged(nameof(ResolutionText));
                OnPropertyChanged(nameof(HasResolution));
                OnPropertyChanged(nameof(ResolveLabel));
                OnPropertyChanged(nameof(ReopenTooltip));
                OnPropertyChanged(nameof(IsReplyable));
                OnPropertyChanged(nameof(CanReply));
                ReplyCommand.NotifyCanExecuteChanged();

                // The row stays in the list (removing it would take the status line away with it), but the filter chips
                // count buckets, so they are re-derived now rather than after the next read.
                changed?.Invoke();
            }

            SetStatus(PrCommentsStrings.ResolveDoneText, isError: false);
        });
    }

    /// <summary>
    /// Builds the English hand-off message for this thread and gives it to the session through the panel's callback: an
    /// idle session runs it as one turn, a busy one gets it in its composer. The answer is reported on the row.
    /// </summary>
    private async Task HandOffAsync()
    {
        if (handOff is null)
        {
            SetStatus(PrCommentsStrings.HandOffNoneText, isError: false);
            return;
        }

        var current = Begin();
        ChecksHandOff handed;
        try
        {
            handed = await handOff(thread).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Finish(current, () => SetStatus(PrCommentsStrings.ActionFailedText(exception.Message), isError: true));
            return;
        }

        Finish(current, () => SetStatus(
            handed switch
            {
                ChecksHandOff.Ran => PrCommentsStrings.HandOffRanText,
                ChecksHandOff.Queued => PrCommentsStrings.HandOffQueuedText,
                _ => PrCommentsStrings.HandOffNoneText,
            },
            isError: false));
    }

    /// <summary>Asks the row to re-read its localized texts after a language switch.</summary>
    internal void Relocalize() => OnPropertyChanged(null);

    /// <summary>Marks the row busy and returns the generation this action runs under.</summary>
    private int Begin()
    {
        busy = true;
        var current = ++actionGeneration;
        RaiseBusy();
        return current;
    }

    /// <summary>Undoes <see cref="Begin"/> for an action that was cancelled.</summary>
    private void End(int current)
    {
        if (current == actionGeneration)
        {
            post(() => Release(current));
        }
    }

    /// <summary>Applies an action's answer on the UI thread unless the row moved on (a newer action or the panel's close).</summary>
    private void Finish(int current, Action apply) => post(() =>
    {
        if (current != actionGeneration)
        {
            return;
        }

        apply();
        Release(current);
    });

    private void Release(int current)
    {
        if (current != actionGeneration)
        {
            return;
        }

        busy = false;
        RaiseBusy();
    }

    private void RaiseBusy()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsReplyable));
        OnPropertyChanged(nameof(IsReplyEnabled));
        OnPropertyChanged(nameof(CanReply));
        OnPropertyChanged(nameof(CanHandOff));
        ReplyCommand.NotifyCanExecuteChanged();
        ResolveCommand.NotifyCanExecuteChanged();
        HandOffCommand.NotifyCanExecuteChanged();
    }

    private void SetStatus(string text, bool isError)
    {
        status = text;
        statusIsError = isError;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(StatusIsError));
    }

    /// <summary>A command body threw (not a cancellation, which the command swallows): the reason goes on the row.</summary>
    private void ReportFailure(Exception exception)
    {
        if (!disposed)
        {
            SetStatus(PrCommentsStrings.ActionFailedText(exception.Message), isError: true);
        }
    }

    /// <summary>Rebuilds the comment rows from one reading, keeping the instances of the comments that are still there.</summary>
    private void Fill(PrReviewThread next)
    {
        var carried = new Dictionary<string, PrCommentItemViewModel>(Comments.Count, StringComparer.Ordinal);
        foreach (var row in Comments)
        {
            carried.TryAdd(row.Id, row);
        }

        Comments.Clear();
        foreach (var comment in next.Comments)
        {
            Comments.Add(carried.Remove(comment.Id, out var existing) ? existing.Adopt(comment) : new PrCommentItemViewModel(comment));
        }

        OnPropertyChanged(nameof(HasComments));
        OnPropertyChanged(nameof(CommentCountText));
    }
}

/// <summary>
/// One comment of a review thread: who wrote it, when, and the body. The body is Markdown written by a reviewer (or by
/// the agent through <c>pr_comment_reply</c>), so the view renders it with the shared renderer; the view model only carries
/// the text.
/// </summary>
internal sealed class PrCommentItemViewModel : NotifyingObject
{
    private PrReviewComment comment;

    public PrCommentItemViewModel(PrReviewComment comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        this.comment = comment;
    }

    public string Id => comment.Id;

    public PrReviewComment Comment => comment;

    public string Author => comment.Author is { Length: > 0 } author ? author : PrCommentsStrings.AnonymousText;

    /// <summary>"11-15 10:20" of the comment, empty when the platform did not report one.</summary>
    public string TimeText => comment.CreatedAt is { } at ? TextFormat.ShortStamp(at) : string.Empty;

    public bool HasTime => comment.CreatedAt is not null;

    /// <summary>The body as the reviewer wrote it; the view parses and renders it.</summary>
    public string Body => comment.Body;

    public bool HasBody => comment.Body.Length > 0;

    /// <summary>"alice · 11-15 10:20" — the one line above the body.</summary>
    public string HeaderText => HasTime ? Author + " · " + TimeText : Author;

    public PrCommentItemViewModel Adopt(PrReviewComment next)
    {
        ArgumentNullException.ThrowIfNull(next);
        comment = next;
        OnPropertyChanged(null);
        return this;
    }
}
