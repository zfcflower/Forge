using Forge.Abstractions;
using Forge.App.Localization;

namespace Forge.App.ViewModels;

/// <summary>
/// One line of the plan panel: a group's header row or one step. Both shapes live in this one type because the list renders
/// them with a single template - a group row carries the title, the count of its done steps and the status they add up to, a
/// step row adds the note, the recorded change, the verification and the commit (or why that commit failed). A group row's
/// step-only members stay at their defaults, which is what the template's visibility bindings read.
/// <para>Rows are keyed and patched in place: a refresh updates the row it already holds instead of rebuilding the list, so
/// the panel keeps its scroll position while the agent works, and a realized row is never replaced for a field it did not
/// change.</para>
/// </summary>
internal sealed class PlanRowViewModel : NotifyingObject
{
    /// <summary>Length of the commit id shown on a step row (the same seven characters the PR body and the TODO row use).</summary>
    internal const int ShortCommitLength = 7;

    private readonly PlanPanelViewModel? owner;
    private string title;
    private TodoStatus status;
    private int done;
    private int total;
    private string? note;
    private string? summary;
    private string? verification;
    private string? commitId;
    private string? commitError;
    private bool isNext;

    /// <summary>A group's header row: its title, and whether it is the heading of the steps that belong to no group.</summary>
    public PlanRowViewModel(string key, string title, bool isLooseSection)
    {
        Key = key;
        IsGroupRow = true;
        IsLooseSection = isLooseSection;
        this.title = title;
    }

    /// <summary>A step row, built from one stored item; <paramref name="groupTitle"/> is null for an item of the session queue.</summary>
    public PlanRowViewModel(PlanPanelViewModel owner, TodoItem item, string? groupTitle)
    {
        this.owner = owner;
        Key = "step:" + item.Id;
        Id = item.Id;
        GroupTitle = groupTitle;
        HandoffCommand = new RelayCommand(() => owner.Handoff(this), () => !IsDone);
        Source = item;
        title = item.Title;
        status = item.Status;
        note = Normalize(item.Note);
        summary = Normalize(item.Summary);
        verification = Normalize(item.Verification);
        commitId = Normalize(item.CommitId);
        commitError = Normalize(item.CommitError);
    }

    /// <summary>The stable identity of the row across refreshes: a step id or a group id, prefixed by its kind.</summary>
    public string Key { get; }

    /// <summary>True for a group's header row; the template then draws the group band instead of a step.</summary>
    public bool IsGroupRow { get; }

    /// <summary>True for a step row.</summary>
    public bool IsStepRow => !IsGroupRow;

    /// <summary>True for the heading of the steps that belong to no group (the session's queue).</summary>
    public bool IsLooseSection { get; }

    public string Title
    {
        get => title;
        private set => Set(ref title, value);
    }

    public TodoStatus Status => status;

    public bool IsDone => status == TodoStatus.Done;

    public bool IsInProgress => status == TodoStatus.InProgress;

    public bool IsPaused => status == TodoStatus.Paused;

    public bool IsPending => status == TodoStatus.Pending;

    /// <summary>The accessible name of the row's status icon ("not started", "in progress", "paused", "done").</summary>
    public string StatusName => Loc.Instance[TodoRowViewModel.StatusKey(status)];

    // ---------------------------------------------------------------- the group shape

    /// <summary>How many of the group's steps are done, and how many it has; both zero for a step row.</summary>
    public int Done => done;

    public int Total => total;

    /// <summary>"2/4": the count a group row shows at the right end of its title.</summary>
    public string ProgressText => total == 0 ? string.Empty : InspectorStrings.TodoProgress(done, total);

    public bool HasProgress => total > 0;

    // ---------------------------------------------------------------- the step shape

    /// <summary>The stored item this row was built from; the panel reads it back when it patches the row in place.</summary>
    internal TodoItem? Source { get; private set; }

    /// <summary>The checklist item's id: what the agent's <c>todo</c> call needs to find the step.</summary>
    public string? Id { get; }

    /// <summary>The group's title, for the message the panel hands to the agent; null for a loose item.</summary>
    public string? GroupTitle { get; }

    /// <summary>True when the step belongs to a named group (the loose section has no title of its own to show).</summary>
    public bool HasGroupTitle => GroupTitle is { Length: > 0 };

    /// <summary>The step to work on next: the first in-progress one, else the first pending one, in plan order.</summary>
    public bool IsNext
    {
        get => isNext;
        private set => Set(ref isNext, value);
    }

    /// <summary>True while the row offers "have the agent finish this step": every step that is not done yet.</summary>
    public bool CanHandoff => IsStepRow && !IsDone;

    /// <summary>The short note the agent attached to the step, or null.</summary>
    public string? Note
    {
        get => note;
        private set => SetAndNotify(ref note, value, nameof(HasNote));
    }

    public bool HasNote => note is { Length: > 0 };

    /// <summary>What the step changed, as the agent recorded it: the line the PR body shows for the step.</summary>
    public string? Summary
    {
        get => summary;
        private set => SetAndNotify(ref summary, value, nameof(HasSummary));
    }

    public bool HasSummary => summary is { Length: > 0 };

    /// <summary>"Changes: …" - the step's recorded change, with its label.</summary>
    public string SummaryText => summary is { Length: > 0 } text ? PlanStrings.StepSummary(text).Resolve() : string.Empty;

    /// <summary>How the step was checked, as the agent recorded it, or null.</summary>
    public string? Verification
    {
        get => verification;
        private set => SetAndNotify(ref verification, value, nameof(HasVerification));
    }

    /// <summary>"Verification: …" - the line that says how the step was proven.</summary>
    public string VerificationText => verification is { Length: > 0 } text ? PlanStrings.Verification(text).Resolve() : string.Empty;

    public bool HasVerification => verification is { Length: > 0 };

    /// <summary>The full commit id of the step, or null when the step produced no commit.</summary>
    public string? CommitId
    {
        get => commitId;
        private set => SetAndNotify(ref commitId, value, nameof(HasCommitId));
    }

    /// <summary>The first <see cref="ShortCommitLength"/> characters of <see cref="CommitId"/>; empty when there is none.</summary>
    public string CommitShort => commitId is null ? string.Empty : commitId[..Math.Min(ShortCommitLength, commitId.Length)];

    public bool HasCommitId => commitId is not null;

    /// <summary>Why the step commit failed (git's message, English), or null.</summary>
    public string? CommitError
    {
        get => commitError;
        private set => SetAndNotify(ref commitError, value, nameof(HasCommitError));
    }

    public bool HasCommitError => commitError is not null;

    /// <summary>Hands this step to the agent; the panel writes the report of that attempt into its status line.</summary>
    public RelayCommand? HandoffCommand { get; }

    /// <summary>Patches a group row from a fresh read, raising a notification only for what actually changed.</summary>
    internal void UpdateGroup(string newTitle, int newDone, int newTotal, TodoStatus newStatus)
    {
        Title = newTitle;
        if (done != newDone || total != newTotal)
        {
            done = newDone;
            total = newTotal;
            OnPropertyChanged(nameof(Done));
            OnPropertyChanged(nameof(Total));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(HasProgress));
        }

        SetStatus(newStatus);
    }

    /// <summary>Patches a step row from a fresh read of its item, raising a notification only for what actually changed.</summary>
    internal void UpdateStep(TodoItem item, bool next)
    {
        Source = item;
        Title = item.Title;
        Note = Normalize(item.Note);
        var newSummary = Normalize(item.Summary);
        if (!string.Equals(summary, newSummary, StringComparison.Ordinal))
        {
            Summary = newSummary;
            OnPropertyChanged(nameof(SummaryText));
        }

        var newVerification = Normalize(item.Verification);
        if (!string.Equals(verification, newVerification, StringComparison.Ordinal))
        {
            Verification = newVerification;
            OnPropertyChanged(nameof(VerificationText));
        }

        CommitId = Normalize(item.CommitId);
        CommitError = Normalize(item.CommitError);
        IsNext = next;
        SetStatus(item.Status);
    }

    /// <summary>Re-reads the texts that follow the interface language.</summary>
    internal void RefreshTexts()
    {
        OnPropertyChanged(nameof(StatusName));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(VerificationText));
    }

    private void SetStatus(TodoStatus value)
    {
        if (status == value)
        {
            return;
        }

        status = value;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsInProgress));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(StatusName));
        OnPropertyChanged(nameof(CanHandoff));
        HandoffCommand?.NotifyCanExecuteChanged();
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// One replaced version of the plan summary: when it was replaced, its first line as a preview, and its full text - which
/// the panel can show in place of the current summary and copy to the clipboard. Immutable: a revision never changes once
/// the store recorded it, so a refresh only adds and drops rows.
/// </summary>
internal sealed class PlanRevisionViewModel : ObservableObject, ICopyableText
{
    private const int PreviewLength = 160;

    public PlanRevisionViewModel(PlanPanelViewModel owner, PlanRevision revision)
    {
        ReplacedAt = revision.ReplacedAt;
        Text = revision.Text;
        TimeText = TextFormat.ShortStamp(revision.ReplacedAt);
        PreviewText = FirstLine(revision.Text);
        ViewCommand = new RelayCommand(() => owner.PreviewRevision(this));
    }

    public DateTimeOffset ReplacedAt { get; }

    /// <summary>The full text the plan summary held before it was replaced.</summary>
    public string Text { get; }

    /// <summary>The replacement time in the short local form the rest of the app uses ("06-01 12:00").</summary>
    public string TimeText { get; }

    /// <summary>The first non-empty line, trimmed to <see cref="PreviewLength"/> characters, as the row's one-liner.</summary>
    public string PreviewText { get; }

    /// <summary>Shows this version in the summary area.</summary>
    public RelayCommand ViewCommand { get; }

    /// <summary>The revision's whole text, for the row's copy button.</summary>
    public string CopyText => Text;

    /// <summary>
    /// The row's one-liner: the first line with anything on it, cut to <see cref="PreviewLength"/> characters. Computed once
    /// in the constructor - a revision never changes, and the row would otherwise re-split its whole text on every read.
    /// </summary>
    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            return trimmed.Length <= PreviewLength ? trimmed : trimmed[..PreviewLength] + "…";
        }

        return string.Empty;
    }
}
