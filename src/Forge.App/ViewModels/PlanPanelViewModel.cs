using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.Workspace;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>
/// The plan panel: one session's plan, read-only. It shows the plan summary the agent wrote (and, on demand, any version it
/// replaced), the steps with the summary and verification the agent recorded for each, the step to work on next, and the
/// ways out of the panel - copy the plan, export it, or open the pull request whose body it already is.
/// <para>Read-only is the contract, not a limitation: step statuses and step commits belong to the agent's <c>todo</c>
/// calls, so the only action a step offers hands it back to the agent through the session's composer. Nothing here writes to
/// the store; the panel reads it.</para>
/// <para>Threading: every public method runs on the UI thread. The four store reads run on the thread pool through
/// <see cref="BackgroundRead"/> and come back with <c>ConfigureAwait(true)</c>; a read that a newer one overtook is dropped,
/// and reads are coalesced - a store change arriving during a read schedules one follow-up read instead of a second read
/// running beside it. Rows are patched in place, so a refresh keeps the list's scroll position while the agent works.</para>
/// </summary>
internal sealed class PlanPanelViewModel : NotifyingObject, IDisposable, ICopyableText
{
    /// <summary>How many replaced versions the panel asks the store for (the store keeps no more than this per session).</summary>
    internal const int RevisionTake = 50;

    private const string LooseSectionKey = "group:loose";

    /// <summary>Longest suggested file name (without the extension) the export offers.</summary>
    private const int SuggestedNameLength = 60;

    private readonly ITodoStore store;
    private readonly IPlanPanelHost host;

    private PlanPanelSession? session;
    private PlanSummary? summary;
    private string planMarkdown = string.Empty;
    private string summaryText = string.Empty;
    private string summaryStamp = string.Empty;
    private int progressDone;
    private int progressTotal;
    private LocText statusText;
    private bool statusIsFailure;
    private PlanRevisionViewModel? viewingRevision;
    private PlanRowViewModel? nextStep;
    private int generation;
    private bool refreshing;
    private bool refreshQueued;
    private bool isLoading;
    private bool disposed;

    public PlanPanelViewModel(ITodoStore store, IPlanPanelHost host)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        CloseCommand = new RelayCommand(() => this.host.ClosePlan());
        RefreshCommand = new AsyncRelayCommand(
            RefreshAsync,
            () => !refreshing,
            exception => SetStatus(LocText.Literal(exception.Message), failure: true));
        OpenPullRequestCommand = new RelayCommand(
            () => this.host.OpenPlanPullRequest(),
            () => this.host.CanOpenPlanPullRequest);
        BackToSummaryCommand = new RelayCommand(() => PreviewRevision(null));
        this.store.Changed += OnStoreChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>The plan's rows, in the order the PR body renders them: a group's header row, its steps, then the queue.</summary>
    public ObservableCollection<PlanRowViewModel> Rows { get; } = [];

    /// <summary>The replaced versions of the plan summary, newest first.</summary>
    public ObservableCollection<PlanRevisionViewModel> Revisions { get; } = [];

    /// <summary>Closes the overlay.</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>Reads the plan again; disabled while a read is running.</summary>
    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>Opens the pull-request compose dialog, whose body already starts with this plan.</summary>
    public RelayCommand OpenPullRequestCommand { get; }

    /// <summary>Leaves the revision preview and shows the current summary again.</summary>
    public RelayCommand BackToSummaryCommand { get; }

    /// <summary>The session the panel shows, for its header; empty when the window has none.</summary>
    public string SessionTitle => session?.Title ?? string.Empty;

    /// <summary>True when there is a session to read (otherwise the panel says so instead of showing an empty plan).</summary>
    public bool HasSession => session is not null;

    /// <summary>
    /// The Markdown the summary area renders: the current plan summary, or the version being previewed. The view turns it
    /// into controls, so the panel itself never builds a visual tree and stays testable without a window.
    /// </summary>
    public string SummaryText => viewingRevision?.Text ?? summaryText;

    /// <summary>True when the summary area has text at all (otherwise the view shows why it is empty).</summary>
    public bool HasSummary => SummaryText.Length > 0;

    /// <summary>"Updated 06-01 12:00": when the current summary was written; empty while a revision is previewed.</summary>
    public string SummaryStamp => viewingRevision is null ? summaryStamp : string.Empty;

    /// <summary>The stamp with the wording the panel shows beside the summary's heading; empty when there is nothing to say.</summary>
    public string SummaryUpdatedText => SummaryStamp is { Length: > 0 } stamp ? PlanStrings.SummaryUpdated(stamp).Resolve() : string.Empty;

    public bool HasSummaryStamp => SummaryUpdatedText.Length > 0;

    /// <summary>True while an older version is on screen; the banner then says when it was replaced.</summary>
    public bool IsViewingRevision => viewingRevision is not null;

    /// <summary>The banner above the summary while a revision is previewed.</summary>
    public string RevisionBanner => viewingRevision is null ? string.Empty : PlanStrings.RevisionBanner(viewingRevision.TimeText).Resolve();

    /// <summary>"3/7 done" of the whole plan; empty when it has no steps.</summary>
    public string ProgressText => progressTotal == 0 ? string.Empty : PlanStrings.Progress(progressDone, progressTotal).Resolve();

    public bool HasProgress => progressTotal > 0;

    /// <summary>True when the plan has at least one step.</summary>
    public bool HasSteps => hasSteps;

    private bool hasSteps;

    /// <summary>The step the plan should work on next: the first in-progress one, else the first pending one.</summary>
    public PlanRowViewModel? NextStep
    {
        get => nextStep;
        private set => SetAndNotify(ref nextStep, value, nameof(HasNextStep));
    }

    public bool HasNextStep => nextStep is not null;

    /// <summary>"4 versions"; empty when nothing was replaced yet.</summary>
    public string RevisionsCountText => Revisions.Count == 0 ? string.Empty : PlanStrings.RevisionCount(Revisions.Count).Resolve();

    public bool HasRevisions => Revisions.Count > 0;

    /// <summary>The panel's one line of feedback: a failed read, a hand-off, an export; empty when there is nothing to say.</summary>
    public string StatusText
    {
        get
        {
            if (statusText.IsEmpty && isLoading && !HasSummary && Rows.Count == 0)
            {
                return Loc.Instance[PlanStrings.Loading];
            }

            return statusText.Resolve();
        }
    }

    public bool HasStatus => statusText.IsEmpty ? isLoading && !HasSummary && Rows.Count == 0 : true;

    /// <summary>True when the status line reports a failure (the view draws it in the danger colour).</summary>
    public bool StatusIsFailure => statusIsFailure;

    /// <summary>The rendered plan exactly as the pull request body carries it; what the copy button puts on the clipboard.</summary>
    public string CopyText => planMarkdown;

    /// <summary>The file name the export suggests: the session's title, reduced to characters a file name can hold.</summary>
    public string SuggestedExportName => SuggestFileName(session?.Title) + "-plan.md";

    /// <summary>Reads the session's plan for the first time; the shell calls this right after it creates the panel.</summary>
    public void Open()
    {
        session = host.PlanSession;
        OnPropertyChanged(nameof(SessionTitle));
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(SuggestedExportName));
        _ = RefreshAsync(CancellationToken.None);
    }

    /// <summary>
    /// Reads the summary, the items, the groups and the replaced versions, then patches the panel. A read that a newer one
    /// overtook is dropped; a failure lands in the status line instead of leaving the panel empty without a word.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (disposed)
        {
            return;
        }

        if (refreshing)
        {
            refreshQueued = true;
            return;
        }

        refreshing = true;
        RefreshCommand.NotifyCanExecuteChanged();
        try
        {
            do
            {
                refreshQueued = false;
                await LoadAsync(ct).ConfigureAwait(true);
            }
            while (refreshQueued && !disposed);
        }
        finally
        {
            refreshing = false;
            RefreshCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Shows one replaced version in the summary area, or the current summary again when null.</summary>
    internal void PreviewRevision(PlanRevisionViewModel? revision)
    {
        viewingRevision = revision;
        RaiseSummaryChanged();
        OnPropertyChanged(nameof(IsViewingRevision));
        OnPropertyChanged(nameof(RevisionBanner));
    }

    /// <summary>
    /// Hands one step back to the agent: the message names the step's id (so the agent's <c>todo</c> call finds it) and asks
    /// for the done mark with its summary and verification. The outcome is reported in the status line.
    /// </summary>
    internal void Handoff(PlanRowViewModel step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.IsDone)
        {
            return;
        }

        if (host.PlanSession is not { } current || current.IsReadOnly)
        {
            SetStatus(new LocText(PlanStrings.HandoffReadOnly), failure: true);
            return;
        }

        var outcome = host.HandoffToAgent(PlanStrings.HandoffPrompt(step.Title, step.Id ?? string.Empty, step.GroupTitle, step.Note));
        SetStatus(
            outcome switch
            {
                PlanHandoff.Sent => PlanStrings.HandoffSent(step.Title),
                PlanHandoff.Queued => PlanStrings.HandoffQueued(step.Title),
                PlanHandoff.BlockedByDraft => new LocText(PlanStrings.HandoffBlocked),
                _ => new LocText(PlanStrings.HandoffUnavailable),
            },
            failure: outcome is not (PlanHandoff.Sent or PlanHandoff.Queued));
    }

    /// <summary>Writes the rendered plan to <paramref name="path"/>; the outcome is reported in the status line.</summary>
    public async Task ExportAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            await File.WriteAllTextAsync(path, planMarkdown, ct).ConfigureAwait(true);
            SetStatus(PlanStrings.ExportDone(path));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLogs.Session.ZLogWarning($"could not export the plan to '{path}': {exception.Message}");
            SetStatus(PlanStrings.ExportFailed(exception.Message), failure: true);
        }
    }

    /// <summary>Drops the store and language subscriptions; a closed panel must be refreshed by neither.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        store.Changed -= OnStoreChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    // ---------------------------------------------------------------- reading

    private async Task LoadAsync(CancellationToken ct)
    {
        if (host.PlanSession is not { } current)
        {
            session = null;
            OnPropertyChanged(nameof(SessionTitle));
            OnPropertyChanged(nameof(HasSession));
            ApplyEmpty();
            return;
        }

        if (!current.Equals(session))
        {
            session = current;
            OnPropertyChanged(nameof(SessionTitle));
            OnPropertyChanged(nameof(HasSession));
            OnPropertyChanged(nameof(SuggestedExportName));
        }

        var token = Interlocked.Increment(ref generation);
        SetLoading(true);
        try
        {
            var id = current.Id;
            var load = await BackgroundRead.RunAsync(
                async readToken =>
                {
                    var planSummary = await store.GetPlanSummaryAsync(id, readToken).ConfigureAwait(false);
                    var items = await store.ListAsync(id, readToken).ConfigureAwait(false);
                    var groups = await store.ListGroupsAsync(id, readToken).ConfigureAwait(false);
                    var revisions = await store.ListPlanRevisionsAsync(id, RevisionTake, readToken).ConfigureAwait(false);
                    return new PlanLoad(planSummary, items, groups, revisions);
                },
                ct).ConfigureAwait(true);

            if (token != Volatile.Read(ref generation) || disposed)
            {
                return;
            }

            Apply(load);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Session.ZLogWarning($"could not read the plan of session '{current.Id}': {exception.Message}");
            SetStatus(PlanStrings.Failed(exception.Message), failure: true);
        }
        finally
        {
            if (token == Volatile.Read(ref generation))
            {
                SetLoading(false);
            }
        }
    }

    /// <summary>Puts the read plan on screen: the summary, the rows, the next step, the revisions and the rendered body.</summary>
    private void Apply(PlanLoad load)
    {
        var summaryChanged = !string.Equals(summary?.Text, load.Summary?.Text, StringComparison.Ordinal)
                             || summary?.UpdatedAt != load.Summary?.UpdatedAt;
        summary = load.Summary;
        summaryText = load.Summary?.Text ?? string.Empty;
        summaryStamp = load.Summary is { } written ? TextFormat.ShortStamp(written.UpdatedAt) : string.Empty;
        planMarkdown = PrBodyRenderer.Render(load.Summary, load.Items, load.Groups);

        SyncRows(load.Items, load.Groups);
        NextStep = Rows.FirstOrDefault(static row => row.IsNext);
        ApplyProgress(load.Items);

        // A previewed version the store no longer holds (the history was trimmed) cannot stay on screen.
        if (viewingRevision is not null &&
            !load.Revisions.Any(revision => revision.ReplacedAt == viewingRevision.ReplacedAt &&
                                            string.Equals(revision.Text, viewingRevision.Text, StringComparison.Ordinal)))
        {
            viewingRevision = null;
            OnPropertyChanged(nameof(IsViewingRevision));
            OnPropertyChanged(nameof(RevisionBanner));
        }

        SyncRevisions(load.Revisions);
        if (summaryChanged)
        {
            RaiseSummaryChanged();
        }

        OpenPullRequestCommand.NotifyCanExecuteChanged();
    }

    private void ApplyEmpty()
    {
        summary = null;
        summaryText = string.Empty;
        summaryStamp = string.Empty;
        planMarkdown = string.Empty;
        viewingRevision = null;
        SyncRows([], []);
        Revisions.Clear();
        NextStep = null;
        ApplyProgress([]);
        OnPropertyChanged(nameof(HasRevisions));
        OnPropertyChanged(nameof(RevisionsCountText));
        RaiseSummaryChanged();
        OnPropertyChanged(nameof(IsViewingRevision));
        OnPropertyChanged(nameof(RevisionBanner));
    }

    /// <summary>
    /// Brings the row list to the plan's order, patching the rows it already holds: a row whose key still exists keeps its
    /// element (and so the list's scroll position), a row that moved is moved, a new one is inserted, and the leftovers are
    /// dropped from the tail.
    /// </summary>
    private void SyncRows(IReadOnlyList<TodoItem> items, IReadOnlyList<TodoGroup> groups)
    {
        var fresh = BuildRows(items, groups);

        // The step to work on next: the first in-progress row, else the first pending one, in plan order.
        var nextKey = string.Empty;
        foreach (var row in fresh)
        {
            if (row is { IsStepRow: true, IsDone: false } candidate)
            {
                if (candidate.IsInProgress)
                {
                    nextKey = candidate.Key;
                    break;
                }

                if (nextKey.Length == 0 && candidate.IsPending)
                {
                    nextKey = candidate.Key;
                }
            }
        }

        var index = 0;
        while (index < fresh.Count)
        {
            var wanted = fresh[index];
            if (index >= Rows.Count || !string.Equals(Rows[index].Key, wanted.Key, StringComparison.Ordinal))
            {
                var existing = -1;
                for (var probe = index + 1; probe < Rows.Count; probe++)
                {
                    if (string.Equals(Rows[probe].Key, wanted.Key, StringComparison.Ordinal))
                    {
                        existing = probe;
                        break;
                    }
                }

                if (existing >= 0)
                {
                    Rows.Move(existing, index);
                }
                else
                {
                    Rows.Insert(index, wanted);
                }
            }

            // A row that was just inserted is patched as well: it is the one that carries the next-step mark when the plan's
            // next step is new.
            Patch(Rows[index], wanted, string.Equals(wanted.Key, nextKey, StringComparison.Ordinal));
            index++;
        }

        while (Rows.Count > fresh.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        var steps = fresh.Count(static row => row.IsStepRow);
        if (hasSteps != steps > 0)
        {
            hasSteps = steps > 0;
            OnPropertyChanged(nameof(HasSteps));
        }
    }

    /// <summary>Patches one live row from the freshly built one; a row that was just inserted is already its own value.</summary>
    private static void Patch(PlanRowViewModel live, PlanRowViewModel fresh, bool isNext)
    {
        if (live.IsGroupRow)
        {
            live.UpdateGroup(fresh.Title, fresh.Done, fresh.Total, fresh.Status);
        }
        else if (fresh.Source is { } item)
        {
            live.UpdateStep(item, isNext);
        }
    }

    /// <summary>
    /// Flattens the checklist into rows in the order <c>PrBodyRenderer</c> renders the plan: one header row per group that
    /// has steps (group order), each followed by its steps in their own order, then the steps that belong to no group under
    /// the loose section's heading. A step whose group is missing from the read falls back to the queue instead of vanishing.
    /// </summary>
    private List<PlanRowViewModel> BuildRows(IReadOnlyList<TodoItem> items, IReadOnlyList<TodoGroup> groups)
    {
        var rows = new List<PlanRowViewModel>(items.Count + groups.Count + 1);
        var grouped = new Dictionary<string, List<TodoItem>>(StringComparer.Ordinal);
        var loose = new List<TodoItem>();
        var orderedGroups = groups.OrderBy(static group => group.Order).ThenBy(static group => group.Id, StringComparer.Ordinal).ToList();
        foreach (var group in orderedGroups)
        {
            grouped[group.Id] = [];
        }

        foreach (var item in items.OrderBy(static item => item.Order).ThenBy(static item => item.Id, StringComparer.Ordinal))
        {
            if (item.GroupId is { Length: > 0 } groupId && grouped.TryGetValue(groupId, out var steps))
            {
                steps.Add(item);
            }
            else
            {
                loose.Add(item);
            }
        }

        foreach (var group in orderedGroups)
        {
            if (grouped[group.Id] is not { Count: > 0 } steps)
            {
                continue;
            }

            rows.Add(BuildGroupRow("group:" + group.Id, group.Title, isLooseSection: false, steps));
            foreach (var step in steps)
            {
                rows.Add(new PlanRowViewModel(this, step, group.Title));
            }
        }

        if (loose.Count > 0)
        {
            rows.Add(BuildGroupRow(LooseSectionKey, Loc.Instance[PlanStrings.OtherSteps], isLooseSection: true, loose));
            foreach (var step in loose)
            {
                rows.Add(new PlanRowViewModel(this, step, null));
            }
        }

        return rows;
    }

    private static PlanRowViewModel BuildGroupRow(string key, string title, bool isLooseSection, IReadOnlyList<TodoItem> steps)
    {
        var row = new PlanRowViewModel(key, title, isLooseSection);
        row.UpdateGroup(
            title,
            steps.Count(static step => step.Status == TodoStatus.Done),
            steps.Count,
            TodoStatusRule.Derive(steps, static step => step.Status));
        return row;
    }

    private void ApplyProgress(IReadOnlyList<TodoItem> items)
    {
        var done = items.Count(static item => item.Status == TodoStatus.Done);
        if (progressDone == done && progressTotal == items.Count)
        {
            return;
        }

        progressDone = done;
        progressTotal = items.Count;
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(HasProgress));
    }

    private void SyncRevisions(IReadOnlyList<PlanRevision> revisions)
    {
        var same = revisions.Count == Revisions.Count;
        if (same)
        {
            for (var index = 0; index < revisions.Count; index++)
            {
                if (revisions[index].ReplacedAt != Revisions[index].ReplacedAt ||
                    !string.Equals(revisions[index].Text, Revisions[index].Text, StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }
        }

        if (same)
        {
            return;
        }

        Revisions.Clear();
        foreach (var revision in revisions.OrderByDescending(static revision => revision.ReplacedAt))
        {
            Revisions.Add(new PlanRevisionViewModel(this, revision));
        }

        OnPropertyChanged(nameof(RevisionsCountText));
        OnPropertyChanged(nameof(HasRevisions));
    }

    // ---------------------------------------------------------------- reports and texts

    private void RaiseSummaryChanged()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasSummary));
        OnPropertyChanged(nameof(SummaryStamp));
        OnPropertyChanged(nameof(SummaryUpdatedText));
        OnPropertyChanged(nameof(HasSummaryStamp));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(StatusText));
    }

    private void SetLoading(bool value)
    {
        if (isLoading == value)
        {
            return;
        }

        isLoading = value;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
    }

    private void SetStatus(LocText text, bool failure = false)
    {
        statusText = text;
        statusIsFailure = failure;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(StatusIsFailure));
    }

    private void OnStoreChanged(string changedSessionId)
    {
        if (disposed || !string.Equals(changedSessionId, session?.Id, StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.UIThread.Post(() => _ = RefreshAsync(CancellationToken.None));
    }

    /// <summary>The derived texts are resolved when they are read, so a language switch re-reads them without a new load.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var row in Rows)
        {
            row.RefreshTexts();
        }

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(RevisionBanner));
        OnPropertyChanged(nameof(SummaryUpdatedText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(RevisionsCountText));
        OnPropertyChanged(nameof(SuggestedExportName));
    }

    /// <summary>The session title cut down to what a file name holds; empty titles suggest "plan".</summary>
    private static string SuggestFileName(string? title)
    {
        var builder = new StringBuilder(SuggestedNameLength);
        foreach (var character in title ?? string.Empty)
        {
            if (builder.Length == SuggestedNameLength)
            {
                break;
            }

            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        }

        var name = builder.ToString().Trim('-', '_');
        return name.Length == 0 ? "plan" : name;
    }

    /// <summary>One read of the plan: the four queries a refresh needs, together, so a mid-way failure has nothing to apply.</summary>
    private readonly record struct PlanLoad(
        PlanSummary? Summary,
        IReadOnlyList<TodoItem> Items,
        IReadOnlyList<TodoGroup> Groups,
        IReadOnlyList<PlanRevision> Revisions);
}
