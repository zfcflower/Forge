using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Forge.App.Localization;
using Forge.Workspace;

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

internal sealed partial class ChangesViewModel
{
    private BranchChoiceViewModel? selectedBranch;
    private CancellationTokenSource? loadSource;
    private string? uncommittedFingerprint;
    private string branchSearch = string.Empty;
    private string? defaultBranch;
    private AsyncRelayCommand? reloadBranchesCommand;

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

    /// <summary>Reloads the branch list (the refresh icon of the picker).</summary>
    public AsyncRelayCommand ReloadBranchesCommand => reloadBranchesCommand ??= new AsyncRelayCommand(
        ct => context.HasSession && Directory.Exists(context.WorkingDirectory)
            ? ReloadBranchesAsync(context.WorkingDirectory, ct)
            : Task.CompletedTask,
        () => true,
        exception => SetMessage(LocText.Literal(exception.Message)));

    public bool HasLocalBranches => LocalBranches.Count > 0;

    public bool HasRemoteBranches => RemoteBranches.Count > 0;

    public bool HasBranches => HasLocalBranches || HasRemoteBranches;

    public bool HasLocalAndRemoteBranches => HasLocalBranches && HasRemoteBranches;

    public bool HasNoBranches => !HasBranches;

    public bool HasNoTurns => Turns.Count == 0;

    public RelayCommand AllChangesCommand { get; }

    public ChangeScope SelectedScope => scope;

    public bool IsAllChanges => scope == ChangeScope.All;

    public string ScopeLabel => scope switch
    {
        ChangeScope.All => Loc.Instance[InspectorStrings.ChangesAll],
        ChangeScope.ByTurn => selectedTurn?.Title ?? Loc.Instance[InspectorStrings.ChangesByTurn],
        ChangeScope.Commit => selectedCommit?.Subject ?? Loc.Instance[ChangesPickerStrings.CommitsGroup],
        _ => Loc.Instance[InspectorStrings.ChangesUncommitted],
    };

    public string CompareBranchLabel => selectedBranch is null
        ? Loc.Instance[InspectorStrings.ChangesCompareBranchPlaceholder]
        : InspectorStrings.ChangesCompareBranchLabel(selectedBranch.Name);

    public BranchChoiceViewModel? SelectedBranch
    {
        get => selectedBranch;
        set => SetSelectedBranch(value, reloadDiff: true);
    }

    private void SetScope(ChangeScope next, bool forceRefresh = false)
    {
        var changed = scope != next;
        if (!changed && !forceRefresh)
        {
            return;
        }

        if (changed)
        {
            scope = next;
            OnPropertyChanged(nameof(SelectedScope));
            OnPropertyChanged(nameof(IsByTurn));
            OnPropertyChanged(nameof(IsUncommitted));
            OnPropertyChanged(nameof(IsAllChanges));
            OnPropertyChanged(nameof(IsCommitScope));
            OnPropertyChanged(nameof(ScopeLabel));
            OnPropertyChanged(nameof(Summary));
            RefreshPickerChecks();
        }

        _ = LoadAsync(CancellationToken.None, reloadChoices: false);
    }

    private bool SetSelectedTurn(TurnChoiceViewModel? value)
    {
        if (ReferenceEquals(selectedTurn, value))
        {
            return false;
        }

        selectedTurn?.SetSelected(false);
        selectedTurn = value;
        selectedTurn?.SetSelected(true);
        OnPropertyChanged(nameof(SelectedTurn));
        OnPropertyChanged(nameof(ScopeLabel));
        RefreshPickerChecks();
        return true;
    }

    private void SelectTurn(TurnChoiceViewModel turn)
    {
        var changed = SetSelectedTurn(turn);
        SetScope(ChangeScope.ByTurn, forceRefresh: changed);
    }

    private void SelectBranch(BranchChoiceViewModel branch) => SelectedBranch = branch;

    private bool CanGoPreviousFile()
    {
        var index = selectedFile is null ? -1 : files.IndexOf(selectedFile);
        return index > 0;
    }

    private bool CanGoNextFile()
    {
        var index = selectedFile is null ? -1 : files.IndexOf(selectedFile);
        return index >= 0 && index < files.Count - 1;
    }

    private void SelectAdjacentFile(int offset)
    {
        var index = selectedFile is null ? -1 : files.IndexOf(selectedFile);
        var next = index + offset;
        if (next < 0 || next >= files.Count)
        {
            return;
        }

        var file = files[next];
        SelectedFile = file;
        FocusRequested?.Invoke(file);
    }

    private void OpenSelectedFile()
    {
        if (selectedFile is not null)
        {
            openFile?.Invoke(selectedFile.Path);
        }
    }

    public void ReportCopyPathFailure(string message) => SetMessage(InspectorStrings.FileCopyFailed(message));

    private void SetSelectedBranch(BranchChoiceViewModel? value, bool reloadDiff)
    {
        if (ReferenceEquals(selectedBranch, value))
        {
            return;
        }

        selectedBranch?.SetSelected(false);
        selectedBranch = value;
        selectedBranch?.SetSelected(true);
        OnPropertyChanged(nameof(SelectedBranch));
        OnPropertyChanged(nameof(CompareBranchLabel));
        if (reloadDiff && IsAllChanges)
        {
            _ = LoadAsync(CancellationToken.None, reloadChoices: false);
        }
    }

    private async Task ReloadBranchesAsync(string workingDirectory, CancellationToken ct)
    {
        var previousRef = selectedBranch?.RefName;
        var snapshot = await Task.Run(
                async () =>
                {
                    if (!await git.IsRepositoryAsync(workingDirectory, ct).ConfigureAwait(false))
                    {
                        return new BranchReadSnapshot([], null);
                    }

                    var branches = await git.GetBranchesAsync(workingDirectory, includeRemote: true, ct: ct).ConfigureAwait(false);
                    var defaultBranch = await git.GetDefaultBranchAsync(workingDirectory, ct).ConfigureAwait(false);
                    return new BranchReadSnapshot(branches, defaultBranch);
                },
                ct)
            .ConfigureAwait(true);

        defaultBranch = snapshot.DefaultBranch;
        OnPropertyChanged(nameof(DefaultBranch));
        LocalBranches.Clear();
        RemoteBranches.Clear();
        RaiseBranchGroupProperties();
        var choices = new List<BranchChoiceViewModel>(snapshot.Branches.Count);
        foreach (var branch in snapshot.Branches)
        {
            // A base must be a different ref from the current branch. Remote HEAD is a symbolic alias, not a branch choice.
            if (branch.IsCurrent || branch.IsRemote && branch.Name.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
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
        var next = choices.FirstOrDefault(choice => string.Equals(choice.RefName, previousRef, StringComparison.Ordinal))
            ?? DefaultChoice(choices, snapshot.DefaultBranch);
        SetSelectedBranch(next, reloadDiff: false);
    }

    private static BranchChoiceViewModel? DefaultChoice(IReadOnlyList<BranchChoiceViewModel> choices, string? defaultBranch)
    {
        if (defaultBranch is { Length: > 0 })
        {
            var match = choices.FirstOrDefault(choice =>
                choice.IsRemote && string.Equals(choice.Name, defaultBranch, StringComparison.Ordinal));
            match ??= choices.FirstOrDefault(choice =>
                string.Equals(choice.Name, defaultBranch, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }
        }

        return choices.Count == 0 ? null : choices[0];
    }

    private void ClearBranchChoices()
    {
        LocalBranches.Clear();
        RemoteBranches.Clear();
        RaiseBranchGroupProperties();
        SetSelectedBranch(null, reloadDiff: false);
    }

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

    public async Task RefreshFromFileSystemChangeAsync(CancellationToken ct)
    {
        if (!IsUncommitted || !context.HasSession)
        {
            return;
        }

        var currentGeneration = Volatile.Read(ref generation);
        var fingerprint = await ReadUncommittedFingerprintAsync(context.WorkingDirectory, ct).ConfigureAwait(true);
        if (currentGeneration != Volatile.Read(ref generation)
            || string.Equals(fingerprint, Volatile.Read(ref uncommittedFingerprint), StringComparison.Ordinal))
        {
            return;
        }

        await LoadAsync(ct, reloadChoices: false, precomputedFingerprint: fingerprint).ConfigureAwait(true);
    }

    private async Task<string?> ReadUncommittedFingerprintAsync(string workingDirectory, CancellationToken ct) =>
        await Task.Run(
                async () =>
                {
                    if (!await git.IsRepositoryAsync(workingDirectory, ct).ConfigureAwait(false))
                    {
                        return null;
                    }

                    var root = await git.GetRepositoryRootAsync(workingDirectory, ct).ConfigureAwait(false) ?? workingDirectory;
                    var entries = await git.GetStatusAsync(workingDirectory, ct).ConfigureAwait(false);
                    var head = await git.RevParseAsync(root, "HEAD", ct).ConfigureAwait(false);
                    var builder = new StringBuilder(workingDirectory);
                    builder.Append('\n').Append(head ?? "<unborn>");
                    foreach (var entry in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        builder.Append('\n').Append(entry.Path)
                            .Append(':').Append((int)entry.Kind)
                            .Append(':').Append((int)entry.IndexStatus)
                            .Append(':').Append((int)entry.WorkTreeStatus)
                            .Append(':').Append(entry.OriginalPath);
                        try
                        {
                            var path = entry.Path.Replace('/', Path.DirectorySeparatorChar);
                            var info = new FileInfo(Path.Combine(root, path));
                            if (info.Exists)
                            {
                                builder.Append(':').Append(info.Length.ToString(CultureInfo.InvariantCulture))
                                    .Append(':').Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                            }
                        }
                        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
                        {
                            builder.Append(":?");
                        }
                    }

                    return builder.ToString();
                },
                ct)
            .ConfigureAwait(true);

    private sealed record BranchReadSnapshot(IReadOnlyList<GitBranch> Branches, string? DefaultBranch);
}
