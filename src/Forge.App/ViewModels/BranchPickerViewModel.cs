using System.Collections.ObjectModel;
using Forge.App.Localization;
using Forge.Workspace;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>A local or remote-tracking branch option. The full ref remains attached so names with slashes are unambiguous.</summary>
internal sealed class BranchChoiceViewModel : ObservableObject
{
    private readonly Action<BranchChoiceViewModel> select;
    private bool isSelected;
    private bool isDefault;

    public BranchChoiceViewModel(GitBranch branch, Action<BranchChoiceViewModel> select)
    {
        Name = branch.Name;
        RefName = branch.RefName;
        IsRemote = branch.IsRemote;
        var slash = IsRemote ? Name.IndexOf('/', StringComparison.Ordinal) : -1;
        RemoteName = slash > 0 ? Name[..slash] : string.Empty;
        ShortName = slash > 0 ? Name[(slash + 1)..] : Name;
        this.select = select;
        SelectCommand = new RelayCommand(() => this.select(this));
    }

    public string Name { get; }

    /// <summary>The name without its remote prefix (<c>main</c> for <c>origin/main</c>); a local branch keeps its name.</summary>
    public string ShortName { get; }

    /// <summary>The remote of a remote-tracking branch (<c>origin</c>); empty for a local branch.</summary>
    public string RemoteName { get; }

    public string RefName { get; }

    public string AutomationId => "changes-branch-" + RefName;

    public bool IsRemote { get; }

    public bool IsSelected
    {
        get => isSelected;
        private set => Set(ref isSelected, value);
    }

    /// <summary>True for the default branch of the repository, which the picker tags.</summary>
    public bool IsDefault
    {
        get => isDefault;
        internal set => Set(ref isDefault, value);
    }

    public RelayCommand SelectCommand { get; }

    internal void SetSelected(bool value) => IsSelected = value;
}

/// <summary>The branches of one remote (<c>origin</c>), as a sub-group of the picker.</summary>
internal sealed class BranchGroupViewModel(string name, IReadOnlyList<BranchChoiceViewModel> branches)
{
    public string Name { get; } = name;

    public IReadOnlyList<BranchChoiceViewModel> Branches { get; } = branches;
}

/// <summary>
/// One searchable, grouped branch list, shared by the changes panel's compare-base picker and the new-branch dialog's
/// start-point picker: both render the same menu card, so the lists, the live filter and the selection live here and the
/// owner only decides what a pick means. Reads run on the thread pool and are applied on the caller's thread; the branch
/// names themselves are never re-read per row or per render.
/// </summary>
internal sealed class BranchPickerViewModel : ObservableObject
{
    private readonly GitCli git;
    private readonly Action? selectionChanged;
    private readonly Action<Exception>? failed;
    private BranchChoiceViewModel? selectedBranch;
    private string branchSearch = string.Empty;
    private string? defaultBranch;
    private string workingDirectory = string.Empty;
    private bool skipCurrentBranch;
    private int loadGeneration;
    private AsyncRelayCommand? reloadCommand;

    /// <param name="git">The git reader.</param>
    /// <param name="selectionChanged">
    /// Called after the user picked another branch (not after a reload re-applies the list): the changes panel reloads its
    /// diff there, the new-branch dialog has nothing to do.
    /// </param>
    /// <param name="failed">Where a failed reload is reported; without it the failure is only traced.</param>
    public BranchPickerViewModel(GitCli git, Action? selectionChanged = null, Action<Exception>? failed = null)
    {
        this.git = git;
        this.selectionChanged = selectionChanged;
        this.failed = failed;
    }

    public ObservableCollection<BranchChoiceViewModel> LocalBranches { get; } = [];

    public ObservableCollection<BranchChoiceViewModel> RemoteBranches { get; } = [];

    /// <summary>The local branches that match <see cref="BranchSearch"/>, in picker order.</summary>
    public ObservableCollection<BranchChoiceViewModel> VisibleLocalBranches { get; } = [];

    /// <summary>The remote-tracking branches that match <see cref="BranchSearch"/>, one group per remote.</summary>
    public ObservableCollection<BranchGroupViewModel> VisibleRemoteGroups { get; } = [];

    public bool HasVisibleLocalBranches => VisibleLocalBranches.Count > 0;

    public bool HasVisibleRemoteBranches => VisibleRemoteGroups.Count > 0;

    /// <summary>Branches exist but the search filtered all of them out.</summary>
    public bool HasNoBranchMatch => HasBranches && VisibleLocalBranches.Count == 0 && VisibleRemoteGroups.Count == 0;

    public bool HasLocalBranches => LocalBranches.Count > 0;

    public bool HasRemoteBranches => RemoteBranches.Count > 0;

    public bool HasBranches => HasLocalBranches || HasRemoteBranches;

    public bool HasLocalAndRemoteBranches => HasLocalBranches && HasRemoteBranches;

    public bool HasNoBranches => !HasBranches;

    /// <summary>The default branch of the repository (as resolved on the last branch load); null when none is known.</summary>
    public string? DefaultBranch => defaultBranch;

    /// <summary>The live, case-insensitive "contains" filter of the picker on the branch names.</summary>
    public string BranchSearch
    {
        get => branchSearch;
        set
        {
            if (Set(ref branchSearch, value ?? string.Empty))
            {
                RebuildVisibleBranches();
            }
        }
    }

    public BranchChoiceViewModel? SelectedBranch
    {
        get => selectedBranch;
        set => SetSelectedBranch(value, notify: true);
    }

    /// <summary>The selected branch's own name (<c>origin/main</c>), empty while nothing is selected.</summary>
    public string SelectedBranchName => selectedBranch?.Name ?? string.Empty;

    public string CompareBranchLabel => selectedBranch is null
        ? Loc.Instance[InspectorStrings.ChangesCompareBranchPlaceholder]
        : InspectorStrings.ChangesCompareBranchLabel(selectedBranch.Name);

    /// <summary>
    /// Records the directory a later refresh should read, without reading anything. A load that skips the branch list (a
    /// scope switch, or one a newer load replaced) still has to leave the refresh button pointing at the right repository
    /// instead of the empty directory a cleared picker carries.
    /// </summary>
    public void SetWorkingDirectory(string workingDirectory) => this.workingDirectory = workingDirectory ?? string.Empty;

    /// <summary>Reloads the branch list (the refresh icon of the picker) from the last loaded working directory.</summary>
    public AsyncRelayCommand ReloadBranchesCommand => reloadCommand ??= new AsyncRelayCommand(
        ct => workingDirectory.Length > 0 && Directory.Exists(workingDirectory)
            ? LoadAsync(workingDirectory, skipCurrentBranch, ct)
            : Task.CompletedTask,
        () => true,
        ReportFailure);

    /// <summary>
    /// Reads the branches of <paramref name="workingDirectory"/> on the thread pool and applies them on the caller's
    /// thread. <paramref name="skipCurrentBranch"/> hides the branch the checkout has checked out (the compare base must be
    /// another ref); the new-branch dialog passes false, where the current branch is a legal start point. The selection
    /// survives by ref name, and a fresh list prefers <c>origin/&lt;default&gt;</c>, then <c>&lt;default&gt;</c>, then the
    /// first row. A load that a newer one replaced is dropped instead of applied.
    /// </summary>
    public async Task LoadAsync(string workingDirectory, bool skipCurrentBranch, CancellationToken ct)
    {
        // A load whose token is already cancelled must not claim the generation: it would invalidate the load that
        // replaced it and then never apply an answer of its own, leaving the picker empty.
        ct.ThrowIfCancellationRequested();
        this.workingDirectory = workingDirectory;
        this.skipCurrentBranch = skipCurrentBranch;
        var generation = Interlocked.Increment(ref loadGeneration);
        var previousRef = selectedBranch?.RefName;
        var snapshot = await Task.Run(
                async () =>
                {
                    if (workingDirectory.Length == 0
                        || !Directory.Exists(workingDirectory)
                        || !await git.IsRepositoryAsync(workingDirectory, ct).ConfigureAwait(false))
                    {
                        return new BranchReadSnapshot([], null);
                    }

                    var branches = await git.GetBranchesAsync(workingDirectory, includeRemote: true, ct: ct).ConfigureAwait(false);
                    var defaultBranch = await git.GetDefaultBranchAsync(workingDirectory, ct).ConfigureAwait(false);
                    return new BranchReadSnapshot(branches, defaultBranch);
                },
                ct)
            .ConfigureAwait(true);

        if (generation != Volatile.Read(ref loadGeneration))
        {
            // A newer load started while this one ran: its result is the one to show.
            return;
        }

        defaultBranch = snapshot.DefaultBranch;
        OnPropertyChanged(nameof(DefaultBranch));
        LocalBranches.Clear();
        RemoteBranches.Clear();
        var choices = new List<BranchChoiceViewModel>(snapshot.Branches.Count);
        foreach (var branch in snapshot.Branches)
        {
            // A base must be a different ref from the current branch. Remote HEAD is a symbolic alias, not a branch
            // choice: its short name is the bare remote ("origin"), so it is recognized by the full ref name.
            if ((branch.IsRemote && IsRemoteHead(branch)) || (skipCurrentBranch && branch.IsCurrent))
            {
                continue;
            }

            var choice = new BranchChoiceViewModel(branch, SelectBranch)
            {
                IsDefault = snapshot.DefaultBranch is { Length: > 0 } name && string.Equals(branch.Name, name, StringComparison.Ordinal),
            };
            choices.Add(choice);
            (branch.IsRemote ? RemoteBranches : LocalBranches).Add(choice);
        }

        RaiseBranchGroupProperties();
        SetSelectedBranch(RestoreSelection(choices, previousRef, snapshot.DefaultBranch), notify: false);
    }

    /// <summary>
    /// Drops the lists and the selection, e.g. when the changes panel switches session. A read that is still in flight is
    /// invalidated too, and the remembered directory is forgotten, so neither a late result nor the refresh button can
    /// refill the picker with the branches of the session it just left.
    /// </summary>
    public void Clear()
    {
        Interlocked.Increment(ref loadGeneration);
        workingDirectory = string.Empty;
        LocalBranches.Clear();
        RemoteBranches.Clear();
        RaiseBranchGroupProperties();
        SetSelectedBranch(null, notify: false);
    }

    private void SelectBranch(BranchChoiceViewModel branch) => SelectedBranch = branch;

    private void SetSelectedBranch(BranchChoiceViewModel? value, bool notify)
    {
        if (ReferenceEquals(selectedBranch, value))
        {
            return;
        }

        selectedBranch?.SetSelected(false);
        selectedBranch = value;
        selectedBranch?.SetSelected(true);
        OnPropertyChanged(nameof(SelectedBranch));
        OnPropertyChanged(nameof(SelectedBranchName));
        OnPropertyChanged(nameof(CompareBranchLabel));
        if (notify)
        {
            selectionChanged?.Invoke();
        }
    }

    private void ReportFailure(Exception exception)
    {
        if (failed is { } report)
        {
            report(exception);
            return;
        }

        AppLogs.Branches.ZLogWarning($"reload failed: {exception.Message}");
    }

    /// <summary>The selection a fresh list keeps: the same ref, else the default branch (remote first), else the first row.</summary>
    private static BranchChoiceViewModel? RestoreSelection(
        List<BranchChoiceViewModel> choices,
        string? previousRef,
        string? defaultBranch)
    {
        if (previousRef is { Length: > 0 })
        {
            for (var index = 0; index < choices.Count; index++)
            {
                if (string.Equals(choices[index].RefName, previousRef, StringComparison.Ordinal))
                {
                    return choices[index];
                }
            }
        }

        if (defaultBranch is { Length: > 0 })
        {
            for (var index = 0; index < choices.Count; index++)
            {
                if (choices[index].IsRemote && string.Equals(choices[index].Name, defaultBranch, StringComparison.Ordinal))
                {
                    return choices[index];
                }
            }

            for (var index = 0; index < choices.Count; index++)
            {
                if (string.Equals(choices[index].Name, defaultBranch, StringComparison.Ordinal))
                {
                    return choices[index];
                }
            }
        }

        return choices.Count == 0 ? null : choices[0];
    }

    private static bool IsRemoteHead(GitBranch branch) =>
        branch.RefName.EndsWith("/HEAD", StringComparison.Ordinal)
        || branch.Name.EndsWith("/HEAD", StringComparison.Ordinal)
        || !branch.Name.Contains('/', StringComparison.Ordinal);

    /// <summary>Re-derives the visible rows of the picker from the branch lists and the search text.</summary>
    private void RebuildVisibleBranches()
    {
        VisibleLocalBranches.Clear();
        VisibleRemoteGroups.Clear();
        foreach (var branch in LocalBranches)
        {
            if (MatchesSearch(branch))
            {
                VisibleLocalBranches.Add(branch);
            }
        }

        List<BranchChoiceViewModel>? group = null;
        string? groupName = null;
        foreach (var branch in RemoteBranches.OrderBy(static branch => branch.RemoteName, StringComparer.Ordinal))
        {
            if (!MatchesSearch(branch))
            {
                continue;
            }

            if (group is null || !string.Equals(groupName, branch.RemoteName, StringComparison.Ordinal))
            {
                if (group is not null)
                {
                    VisibleRemoteGroups.Add(new BranchGroupViewModel(groupName!, group));
                }

                group = [];
                groupName = branch.RemoteName;
            }

            group.Add(branch);
        }

        if (group is not null)
        {
            VisibleRemoteGroups.Add(new BranchGroupViewModel(groupName!, group));
        }

        OnPropertyChanged(nameof(HasVisibleLocalBranches));
        OnPropertyChanged(nameof(HasVisibleRemoteBranches));
        OnPropertyChanged(nameof(HasNoBranchMatch));
    }

    private bool MatchesSearch(BranchChoiceViewModel branch) =>
        branchSearch.Length == 0 || branch.Name.Contains(branchSearch.Trim(), StringComparison.OrdinalIgnoreCase);

    private void RaiseBranchGroupProperties()
    {
        RebuildVisibleBranches();
        OnPropertyChanged(nameof(HasLocalBranches));
        OnPropertyChanged(nameof(HasRemoteBranches));
        OnPropertyChanged(nameof(HasBranches));
        OnPropertyChanged(nameof(HasLocalAndRemoteBranches));
        OnPropertyChanged(nameof(HasNoBranches));
    }

    private sealed record BranchReadSnapshot(IReadOnlyList<GitBranch> Branches, string? DefaultBranch);
}
