using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Forge.App.Localization;

namespace Forge.App.ViewModels;

/// <summary>
/// The compare-base side of the changes panel. The branch list itself lives in <see cref="BranchPickerViewModel"/>,
/// because the new-branch dialog shows the very same picker; every member here forwards to <see cref="Picker"/> so the
/// panel's own bindings and its tests keep reading the list, the search and the selection from this view model.
/// </summary>
internal sealed partial class ChangesViewModel
{
    private readonly BranchPickerViewModel picker;
    private CancellationTokenSource? loadSource;
    private string? uncommittedFingerprint;

    /// <summary>The shared branch picker the compare-base flyout and the new-branch dialog host.</summary>
    public BranchPickerViewModel Picker => picker;

    public ObservableCollection<BranchChoiceViewModel> LocalBranches => picker.LocalBranches;

    public ObservableCollection<BranchChoiceViewModel> RemoteBranches => picker.RemoteBranches;

    /// <summary>The local branches that match <see cref="BranchSearch"/>, in picker order.</summary>
    public ObservableCollection<BranchChoiceViewModel> VisibleLocalBranches => picker.VisibleLocalBranches;

    /// <summary>The remote-tracking branches that match <see cref="BranchSearch"/>, one group per remote.</summary>
    public ObservableCollection<BranchGroupViewModel> VisibleRemoteGroups => picker.VisibleRemoteGroups;

    public bool HasVisibleLocalBranches => picker.HasVisibleLocalBranches;

    public bool HasVisibleRemoteBranches => picker.HasVisibleRemoteBranches;

    /// <summary>Branches exist but the search filtered all of them out.</summary>
    public bool HasNoBranchMatch => picker.HasNoBranchMatch;

    /// <summary>The default branch of the repository (as resolved on the last branch load); null when none is known.</summary>
    public string? DefaultBranch => picker.DefaultBranch;

    /// <summary>The live, case-insensitive "contains" filter of the picker on the branch names.</summary>
    public string BranchSearch
    {
        get => picker.BranchSearch;
        set => picker.BranchSearch = value;
    }

    public bool HasLocalBranches => picker.HasLocalBranches;

    public bool HasRemoteBranches => picker.HasRemoteBranches;

    public bool HasBranches => picker.HasBranches;

    public bool HasLocalAndRemoteBranches => picker.HasLocalAndRemoteBranches;

    public bool HasNoBranches => picker.HasNoBranches;

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

    public string CompareBranchLabel => picker.CompareBranchLabel;

    public BranchChoiceViewModel? SelectedBranch
    {
        get => picker.SelectedBranch;
        set => picker.SelectedBranch = value;
    }

    /// <summary>Reloads the branch list (the refresh icon of the picker).</summary>
    public AsyncRelayCommand ReloadBranchesCommand => picker.ReloadBranchesCommand;

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

    /// <summary>Drops the branch lists and the selection of the picker (the diff is reloaded by the caller).</summary>
    private void ClearBranchChoices() => picker.Clear();

    /// <summary>
    /// A pick reloads the diff when the scope actually compares against a branch; the notification itself is raised by the
    /// picker and forwarded through <see cref="OnPickerPropertyChanged"/>.
    /// </summary>
    private void OnBranchPicked()
    {
        if (IsAllChanges)
        {
            _ = LoadAsync(CancellationToken.None, reloadChoices: false);
        }
    }

    /// <summary>
    /// The picker notifies its own members (selection, search, list flags); this view model forwards them so its bindings
    /// and the existing tests see the same events they saw when the list lived here.
    /// </summary>
    private void OnPickerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        OnPropertyChanged(e.PropertyName);

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
}
