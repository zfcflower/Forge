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

    public string AutomationId => "changes-branch-" + RefName;

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
    private string? uncommittedFingerprint;

    public ObservableCollection<BranchChoiceViewModel> LocalBranches { get; } = [];

    public ObservableCollection<BranchChoiceViewModel> RemoteBranches { get; } = [];

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
            OnPropertyChanged(nameof(ScopeLabel));
            OnPropertyChanged(nameof(Summary));
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

            var choice = new BranchChoiceViewModel(branch, SelectBranch);
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

    private void RaiseBranchGroupProperties()
    {
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
