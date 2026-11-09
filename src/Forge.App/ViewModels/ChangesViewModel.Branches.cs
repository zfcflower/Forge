using System.Collections.ObjectModel;
using Forge.App.Localization;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>A local or remote-tracking branch option. The full ref remains attached so names with slashes are unambiguous.</summary>
internal sealed class BranchChoiceViewModel : ObservableObject
{
    private readonly Action<BranchChoiceViewModel> select;
    private bool isSelected;

    public BranchChoiceViewModel(GitBranch branch, Action<BranchChoiceViewModel> select)
    {
        Name = branch.Name;
        RefName = branch.RefName;
        IsRemote = branch.IsRemote;
        this.select = select;
        SelectCommand = new RelayCommand(() => this.select(this));
    }

    public string Name { get; }

    public string RefName { get; }

    public bool IsRemote { get; }

    public bool IsSelected
    {
        get => isSelected;
        private set => Set(ref isSelected, value);
    }

    public RelayCommand SelectCommand { get; }

    internal void SetSelected(bool value) => IsSelected = value;
}

internal sealed partial class ChangesViewModel
{
    private BranchChoiceViewModel? selectedBranch;
    private CancellationTokenSource? loadSource;

    public ObservableCollection<BranchChoiceViewModel> LocalBranches { get; } = [];

    public ObservableCollection<BranchChoiceViewModel> RemoteBranches { get; } = [];

    public RelayCommand AllChangesCommand { get; }

    public ChangeScope SelectedScope => scope;

    public bool IsAllChanges => scope == ChangeScope.All;

    public string ScopeLabel => scope switch
    {
        ChangeScope.All => Loc.Instance[InspectorStrings.ChangesAll],
        ChangeScope.ByTurn => selectedTurn?.Label ?? Loc.Instance[InspectorStrings.ChangesByTurn],
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
            OnPropertyChanged(nameof(ScopeIndex));
            OnPropertyChanged(nameof(ScopeLabel));
            OnPropertyChanged(nameof(Summary));
        }

        _ = LoadAsync(CancellationToken.None, reloadChoices: false);
    }

    private void SelectTurn(TurnChoiceViewModel turn)
    {
        var changed = !ReferenceEquals(selectedTurn, turn);
        if (changed)
        {
            selectedTurn = turn;
            OnPropertyChanged(nameof(SelectedTurn));
            OnPropertyChanged(nameof(ScopeLabel));
        }

        SetScope(ChangeScope.ByTurn, forceRefresh: changed);
    }

    private void SelectBranch(BranchChoiceViewModel branch) => SelectedBranch = branch;

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

        LocalBranches.Clear();
        RemoteBranches.Clear();
        var choices = new List<BranchChoiceViewModel>(snapshot.Branches.Count);
        foreach (var branch in snapshot.Branches)
        {
            // A base must be a different ref from the current branch. Remote HEAD is a symbolic alias, not a branch choice.
            if (branch.IsCurrent || branch.IsRemote && branch.Name.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var choice = new BranchChoiceViewModel(branch, SelectBranch);
            choices.Add(choice);
            (branch.IsRemote ? RemoteBranches : LocalBranches).Add(choice);
        }

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
        SetSelectedBranch(null, reloadDiff: false);
    }

    private sealed record BranchReadSnapshot(IReadOnlyList<GitBranch> Branches, string? DefaultBranch);
}
