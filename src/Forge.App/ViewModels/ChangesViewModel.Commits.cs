using System.Collections.ObjectModel;
using Forge.App.Localization;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>One commit of the current branch since the comparison base, as a row of the scope picker.</summary>
internal sealed class CommitChoiceViewModel : ObservableObject
{
    private bool isChecked;

    public CommitChoiceViewModel(GitCommitInfo commit, Action<CommitChoiceViewModel> select)
    {
        Sha = commit.Sha;
        ShortSha = commit.ShortSha;
        Subject = commit.Subject.Trim();
        AuthorDate = commit.AuthorDate;
        ParentSha = commit.ParentShas.Count > 0 ? commit.ParentShas[0] : null;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public string Sha { get; }

    public string ShortSha { get; }

    /// <summary>The first line of the commit message, trimmed; also the scope label while the commit is the scope.</summary>
    public string Subject { get; }

    public DateTimeOffset AuthorDate { get; }

    /// <summary>The first parent; null for a root commit (diffed against the empty tree).</summary>
    public string? ParentSha { get; }

    public string AutomationId => "changes-commit-" + ShortSha;

    /// <summary>"abc1234 · 3 天前".</summary>
    public string Subtitle => ChangesPickerStrings.CommitSubtitle(ShortSha, AuthorDate, DateTimeOffset.Now);

    /// <summary>True while this commit is the scope in use (the check mark of its picker row).</summary>
    public bool IsChecked
    {
        get => isChecked;
        internal set => Set(ref isChecked, value);
    }

    public RelayCommand SelectCommand { get; }
}

internal sealed partial class ChangesViewModel
{
    private CommitChoiceViewModel? selectedCommit;
    private CancellationTokenSource? pickerSource;
    private int? uncommittedCount;
    private RelayCommand? latestTurnCommand;

    /// <summary>The commits of the current branch since the comparison base, newest first.</summary>
    public ObservableCollection<CommitChoiceViewModel> Commits { get; } = [];

    public bool HasCommits => Commits.Count > 0;

    public bool HasNoCommits => Commits.Count == 0;

    public bool IsCommitScope => scope == ChangeScope.Commit;

    public CommitChoiceViewModel? SelectedCommit => selectedCommit;

    /// <summary>How many files have uncommitted changes; null until the picker has read it.</summary>
    public int? UncommittedCount => uncommittedCount;

    /// <summary>False only when the working tree is known to be clean: the picker then shows a disabled "没有未提交的改动" row.</summary>
    public bool HasUncommitted => uncommittedCount is null or > 0;

    public bool HasNoUncommitted => !HasUncommitted;

    /// <summary>The newest turn, or null when none finished.</summary>
    public TurnChoiceViewModel? LatestTurn => Turns.Count > 0 ? Turns[0] : null;

    public bool IsLatestTurnChecked => scope == ChangeScope.ByTurn && LatestTurn is { } latest && ReferenceEquals(latest, selectedTurn);

    /// <summary>"上一轮 · 3 个文件" (the shortcut row).</summary>
    public string LatestTurnShortcutText => ChangesPickerStrings.LatestTurnShortcut(LatestTurn?.FileCount);

    /// <summary>Selects the newest turn.</summary>
    public RelayCommand LatestTurnCommand => latestTurnCommand ??= new RelayCommand(
        () =>
        {
            if (LatestTurn is { } latest)
            {
                SelectTurn(latest);
            }
        },
        () => LatestTurn is not null);

    /// <summary>A session switch drops the previous repository's commits; a stale sha must not be diffed in the new one.</summary>
    private void ClearCommitChoices()
    {
        Commits.Clear();
        Volatile.Write(ref selectedCommit, null);
        OnPropertyChanged(nameof(SelectedCommit));
        OnPropertyChanged(nameof(HasCommits));
        OnPropertyChanged(nameof(HasNoCommits));
        OnPropertyChanged(nameof(ScopeLabel));
    }

    private void SelectCommit(CommitChoiceViewModel commit)
    {
        var changed = !ReferenceEquals(selectedCommit, commit);
        Volatile.Write(ref selectedCommit, commit);
        OnPropertyChanged(nameof(SelectedCommit));
        OnPropertyChanged(nameof(ScopeLabel));
        SetScope(ChangeScope.Commit, forceRefresh: changed);
        RefreshPickerChecks();
    }

    /// <summary>
    /// Brings the check marks of the picker in line with the scope: only the row of the scope in use shows one, although a
    /// turn or a commit stays remembered while another scope is shown.
    /// </summary>
    private void RefreshPickerChecks()
    {
        foreach (var turn in Turns)
        {
            turn.IsChecked = scope == ChangeScope.ByTurn && ReferenceEquals(turn, selectedTurn);
        }

        foreach (var commit in Commits)
        {
            commit.IsChecked = scope == ChangeScope.Commit && selectedCommit is { } chosen
                && string.Equals(chosen.Sha, commit.Sha, StringComparison.Ordinal);
        }

        OnPropertyChanged(nameof(IsLatestTurnChecked));
    }

    private void RefreshTurnPresentation()
    {
        for (var index = 0; index < Turns.Count; index++)
        {
            Turns[index].IsLatest = index == 0;
        }

        latestTurnCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(LatestTurn));
        OnPropertyChanged(nameof(LatestTurnShortcutText));
        OnPropertyChanged(nameof(HasNoTurns));
        OnPropertyChanged(nameof(ScopeLabel));
    }

    /// <summary>
    /// Fills in what the scope picker shows beyond the cheap lists: the number of uncommitted files, the file count of each
    /// turn (cached per turn: its trees never change) and the commits of the branch. Called when the flyout opens; the reads
    /// run on the pool and a newer call cancels the older one.
    /// </summary>
    public async Task RefreshScopePickerAsync(CancellationToken ct = default)
    {
        RefreshTurnPresentation();
        RefreshPickerChecks();
        if (!context.HasSession || !Directory.Exists(context.WorkingDirectory))
        {
            return;
        }

        var workingDirectory = context.WorkingDirectory;
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var previous = Interlocked.Exchange(ref pickerSource, source);
        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await Task.WhenAll(
                    RefreshUncommittedCountAsync(workingDirectory, source.Token),
                    RefreshTurnCountsAsync(workingDirectory, source.Token),
                    RefreshCommitsAsync(workingDirectory, source.Token))
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref pickerSource, null, source);
            source.Dispose();
        }
    }

    private async Task RefreshUncommittedCountAsync(string workingDirectory, CancellationToken ct)
    {
        int? count;
        try
        {
            count = await Task.Run(
                    async () => await git.IsRepositoryAsync(workingDirectory, ct).ConfigureAwait(false)
                        ? (await git.GetStatusAsync(workingDirectory, ct).ConfigureAwait(false)).Count
                        : 0,
                    ct)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or GitException)
        {
            return;
        }

        if (uncommittedCount != count)
        {
            uncommittedCount = count;
            OnPropertyChanged(nameof(UncommittedCount));
            OnPropertyChanged(nameof(HasUncommitted));
            OnPropertyChanged(nameof(HasNoUncommitted));
        }
    }

    private async Task RefreshTurnCountsAsync(string workingDirectory, CancellationToken ct)
    {
        foreach (var turn in Turns.ToArray())
        {
            if (turn.FileCount is not null || turn.TreeAfter is not { } after)
            {
                continue;
            }

            var before = turn.TreeBefore;
            try
            {
                var changes = await Task.Run(
                        () => git.GetTreeChangesAsync(workingDirectory, before, after, ct),
                        ct)
                    .ConfigureAwait(true);
                turn.FileCount = changes.Count;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or GitException)
            {
                // A turn whose trees can no longer be read just keeps showing its age only.
            }
        }

        OnPropertyChanged(nameof(LatestTurnShortcutText));
    }

    private async Task RefreshCommitsAsync(string workingDirectory, CancellationToken ct)
    {
        var baseRef = selectedBranch?.RefName;
        IReadOnlyList<GitCommitInfo> found;
        if (baseRef is null)
        {
            found = [];
        }
        else
        {
            try
            {
                found = await Task.Run(() => git.GetCommitsAsync(workingDirectory, baseRef, "HEAD", ct: ct), ct).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or GitException)
            {
                // Unreadable history shows the empty state, not an error.
                found = [];
            }
        }

        Commits.Clear();
        foreach (var info in found)
        {
            Commits.Add(new CommitChoiceViewModel(info, SelectCommit));
        }

        OnPropertyChanged(nameof(HasCommits));
        OnPropertyChanged(nameof(HasNoCommits));
        RefreshPickerChecks();
    }

    /// <summary>
    /// The diff of the selected commit (<see cref="ChangeScope.Commit"/>): <c>commit^..commit</c> against its first parent,
    /// or the empty tree for a root commit, with whole files as context.
    /// </summary>
    private async Task<IReadOnlyList<FileDiff>> ReadCommitDiffAsync(string workingDirectory, int contextLines, CancellationToken ct)
    {
        var commit = Volatile.Read(ref selectedCommit);
        return commit is null
            ? []
            : await git.GetTreeDiffAsync(workingDirectory, commit.ParentSha, commit.Sha, contextLines, ct).ConfigureAwait(false);
    }
}
