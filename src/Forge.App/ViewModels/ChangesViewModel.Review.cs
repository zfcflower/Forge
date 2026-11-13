using System.Diagnostics;
using Forge.App.Changes;
using Forge.App.Localization;
using Forge.App.Review;
using Forge.Workspace;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>
/// The review half of the changes document: view mode/layout, per-file "viewed" and per-block "kept" marks (persisted per
/// session in <see cref="ReviewStore"/>, content-addressed so a changed diff drops them), block/file revert and navigation.
/// </summary>
internal sealed partial class ChangesViewModel
{
    private ReviewServices? reviewServices;
    private Func<string, string, string, string, Task<bool>>? confirm;
    private string repositoryRoot = string.Empty;
    private string? diffBaseCommit;
    private DiffSource? diffSource;
    private DiffViewMode viewMode = DiffViewMode.Inline;
    private DiffLayout layout = DiffLayout.Stacked;
    private bool wordWrap;
    private bool reviewCommandsReady;
    private int marksGeneration;
    private IReadOnlyList<IDiffFileItem> visibleFiles = [];
    private DiffReviewContext? reviewContext;
    private bool isReverting;
    private bool filesRevertable;
    private Task persistTail = Task.CompletedTask;
    private int marksLoadsInFlight;
    private bool marksReloadPending;

    public RelayCommand InlineModeCommand { get; private set; } = null!;

    public RelayCommand SplitModeCommand { get; private set; } = null!;

    public RelayCommand StackedLayoutCommand { get; private set; } = null!;

    public RelayCommand SingleLayoutCommand { get; private set; } = null!;

    public RelayCommand ToggleWrapCommand { get; private set; } = null!;

    public RelayCommand NextUnviewedCommand { get; private set; } = null!;

    public RelayCommand MarkAllViewedCommand { get; private set; } = null!;

    /// <summary>Asks the view to put a path on the clipboard (the view model has no clipboard).</summary>
    public event Action<string>? CopyPathRequested;

    public DiffViewMode ViewMode => viewMode;

    public bool IsInline => viewMode == DiffViewMode.Inline;

    public bool IsSplit => viewMode == DiffViewMode.Split;

    public DiffLayout Layout => layout;

    public bool IsStacked => layout == DiffLayout.Stacked;

    public bool IsSingle => layout == DiffLayout.Single;

    public bool WordWrap => wordWrap;

    /// <summary>The files the diff editor shows: every file when stacked, only the selected one in single-file layout.</summary>
    public IReadOnlyList<IDiffFileItem> VisibleFiles => visibleFiles;

    /// <summary>What the diff editor needs to create review comments; null without a session or review services.</summary>
    public DiffReviewContext? ReviewContext => reviewContext;

    public bool HasFiles => files.Count > 0;

    public string FileCountText => LocText.Plural(InspectorStrings.ChangesSummaryFilesBase, files.Count, TextFormat.Number(files.Count)).Resolve();

    public string TotalAddedText => "+" + TextFormat.Number(files.Sum(static file => ((IDiffFileItem)file).Added));

    public string TotalRemovedText => "−" + TextFormat.Number(files.Sum(static file => ((IDiffFileItem)file).Removed));

    public int ViewedCount => files.Count(static file => file.IsViewed);

    public string ViewedText => ChangesReviewStrings.ViewedSummary(ViewedCount, files.Count);

    public string ReviewedText => ChangesReviewStrings.ReviewedSummary(ViewedCount, files.Count);

    /// <summary>0–100 for the footer progress bar.</summary>
    public double ReviewedPercent => files.Count == 0 ? 0 : 100.0 * ViewedCount / files.Count;

    /// <summary>Wires the review store and the confirmation dialog; called once by the composition root.</summary>
    public void ConfigureReview(ReviewServices? services, Func<string, string, string, string, Task<bool>>? confirmAsync)
    {
        reviewServices = services;
        confirm = confirmAsync;
        UpdateReviewContext();
        if (files.Count > 0)
        {
            _ = LoadMarksAsync();
        }
    }

    private void EnsureReviewCommands()
    {
        if (reviewCommandsReady)
        {
            return;
        }

        reviewCommandsReady = true;
        InlineModeCommand = new RelayCommand(() => SetViewMode(DiffViewMode.Inline));
        SplitModeCommand = new RelayCommand(() => SetViewMode(DiffViewMode.Split));
        StackedLayoutCommand = new RelayCommand(() => SetLayout(DiffLayout.Stacked));
        SingleLayoutCommand = new RelayCommand(() => SetLayout(DiffLayout.Single));
        ToggleWrapCommand = new RelayCommand(() =>
        {
            wordWrap = !wordWrap;
            OnPropertyChanged(nameof(WordWrap));
        });
        NextUnviewedCommand = new RelayCommand(SelectNextUnviewed, () => files.Any(static file => !file.IsViewed));
        MarkAllViewedCommand = new RelayCommand(MarkAllViewed, () => files.Any(static file => !file.IsViewed));
    }

    public void SetViewMode(DiffViewMode value)
    {
        if (viewMode == value)
        {
            return;
        }

        viewMode = value;
        OnPropertyChanged(nameof(ViewMode));
        OnPropertyChanged(nameof(IsInline));
        OnPropertyChanged(nameof(IsSplit));
    }

    public void SetLayout(DiffLayout value)
    {
        if (layout == value)
        {
            return;
        }

        layout = value;
        OnPropertyChanged(nameof(Layout));
        OnPropertyChanged(nameof(IsStacked));
        OnPropertyChanged(nameof(IsSingle));
        UpdateVisibleFiles();
        if (selectedFile is not null)
        {
            FocusRequested?.Invoke(selectedFile);
        }
    }

    /// <summary>The diff editor scrolled another file to the top: follow it without scrolling back.</summary>
    public void SyncSelectionFromScroll(IDiffFileItem file)
    {
        if (layout == DiffLayout.Stacked && file is FileDiffViewModel model && !ReferenceEquals(model, selectedFile))
        {
            SelectedFile = model;
        }
    }

    private void OnSelectedFileChanged()
    {
        if (layout == DiffLayout.Single)
        {
            UpdateVisibleFiles();
        }
    }

    /// <summary>
    /// True when the loaded diff's new side is the working tree (uncommitted changes) or HEAD under it (branch compare), so
    /// a block can be restored by editing the working file. Turn and commit diffs are history: reverting them would edit
    /// (or delete) the current file on a coincidental match, so they never offer revert.
    /// </summary>
    internal bool FilesRevertable => filesRevertable;

    /// <summary>Called after every replacement of <see cref="Files"/> (UI thread).</summary>
    private void OnFilesReplaced(string root, bool revertable = false)
    {
        EnsureReviewCommands();
        repositoryRoot = root;
        filesRevertable = revertable && root.Length > 0;
        foreach (var file in files)
        {
            file.AttachOwner(this);
        }

        UpdateReviewContext();
        UpdateVisibleFiles();
        RaiseReviewTotals();
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(FileCountText));
        OnPropertyChanged(nameof(TotalAddedText));
        OnPropertyChanged(nameof(TotalRemovedText));
        if (files.Count > 0)
        {
            _ = LoadMarksAsync();
        }
    }

    private void UpdateVisibleFiles()
    {
        visibleFiles = layout == DiffLayout.Stacked
            ? files.ToArray()
            : selectedFile is null ? [] : [selectedFile];
        OnPropertyChanged(nameof(VisibleFiles));
    }

    private void UpdateReviewContext()
    {
        var next = reviewServices is not null && context.HasSession && context.WorkingDirectory.Length > 0
            ? new DiffReviewContext(reviewServices, new ReviewOwner(context.WorkingDirectory, context.SessionId), Volatile.Read(ref diffBaseCommit))
            : null;
        if (next != reviewContext)
        {
            reviewContext = next;
            OnPropertyChanged(nameof(ReviewContext));
        }
    }

    private void RaiseReviewTotals()
    {
        OnPropertyChanged(nameof(ViewedCount));
        OnPropertyChanged(nameof(ViewedText));
        OnPropertyChanged(nameof(ReviewedText));
        OnPropertyChanged(nameof(ReviewedPercent));
        NextUnviewedCommand.NotifyCanExecuteChanged();
        MarkAllViewedCommand.NotifyCanExecuteChanged();
    }

    private void RefreshReviewTexts()
    {
        OnPropertyChanged(nameof(FileCountText));
        RaiseReviewTotals();
        foreach (var file in files)
        {
            file.RefreshReviewText();
        }
    }

    // ---------------------------------------------------------------- marks

    private async Task LoadMarksAsync()
    {
        if (reviewServices is not { } services || !context.HasSession)
        {
            return;
        }

        var current = Interlocked.Increment(ref marksGeneration);
        var workspace = context.WorkingDirectory;
        var session = context.SessionId;
        var snapshot = files.ToArray();
        // The read queues behind every pending write, so it sees all toggles made before it started.
        var pendingWrites = persistTail;
        IReadOnlyList<ReviewMark> marks;
        marksLoadsInFlight++;
        try
        {
            marks = await Task.Run(async () =>
                {
                    await pendingWrites.ConfigureAwait(false);
                    return await services.Store.GetMarksAsync(workspace, session).ConfigureAwait(false);
                })
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Changes.ZLogWarning($"loading review marks failed: {exception.Message}");
            return;
        }
        finally
        {
            marksLoadsInFlight--;
        }

        if (current != Volatile.Read(ref marksGeneration))
        {
            // A toggle landed while this read was in flight and would be overwritten: read again behind it.
            if (marksReloadPending && marksLoadsInFlight == 0)
            {
                marksReloadPending = false;
                _ = LoadMarksAsync();
            }

            return;
        }

        var viewed = new HashSet<(string, string)>();
        var kept = new HashSet<(string, string)>();
        foreach (var mark in marks)
        {
            (mark.Kind == ReviewMarkKind.Viewed ? viewed : kept).Add((mark.FilePath, mark.ItemHash));
        }

        foreach (var file in snapshot)
        {
            file.SetViewed(viewed.Contains((file.Path, file.Fingerprint)));
            foreach (var block in file.Blocks)
            {
                block.SetKept(kept.Contains((file.Path, block.Block.Hash)));
            }
        }

        RaiseReviewTotals();
    }

    private void Persist(Func<ReviewStore, string, string, Task> write)
    {
        if (reviewServices is not { } services || !context.HasSession)
        {
            return;
        }

        var workspace = context.WorkingDirectory;
        var session = context.SessionId;
        if (marksLoadsInFlight > 0)
        {
            // An in-flight mark load would reapply the stale stored state over this toggle; drop it and read again.
            Interlocked.Increment(ref marksGeneration);
            marksReloadPending = true;
        }

        // Writes run one after another in toggle order, so "viewed, then unviewed" can never land reversed in the store.
        var previous = persistTail;
        persistTail = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try
            {
                await write(services.Store, workspace, session).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                AppLogs.Changes.ZLogWarning($"saving a review mark failed: {exception.Message}");
            }
        });
    }

    /// <summary>Completes when every mark write queued so far has reached the store (tests, shutdown).</summary>
    internal Task FlushMarksAsync() => persistTail;

    /// <summary>True when the loaded set can be re-read per file with whole-file context (its refs are known).</summary>
    internal bool CanLoadContext => diffSource is not null;

    /// <summary>
    /// Reads <paramref name="file"/> again with whole-file context under the refs of the loaded set and hands it the result,
    /// so its "N unmodified lines" bars can open. False when the set was replaced meanwhile, the read failed or overflowed,
    /// or the file's changes are no longer the ones shown (a reload follows from the watcher then).
    /// </summary>
    internal async Task<bool> LoadFullContextAsync(FileDiffViewModel file)
    {
        if (diffSource is not { } source)
        {
            return false;
        }

        IReadOnlyList<FileDiff> diffs;
        try
        {
            diffs = await Task.Run(() => ReadWholeAsync(source, [file.Diff], CancellationToken.None)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is GitException or IOException or InvalidOperationException)
        {
            // GitOutputTruncatedException is a GitException: a file too large to read whole keeps its folded stretches.
            AppLogs.Changes.ZLogWarning($"loading whole-file context failed: {exception.Message}");
            return false;
        }

        if (!ReferenceEquals(source, diffSource) || !files.Contains(file))
        {
            return false;
        }

        var full = diffs.FirstOrDefault(diff => string.Equals(diff.Path, file.Diff.Path, StringComparison.Ordinal));
        return full is not null && file.ApplyFullContext(full);
    }

    internal void OnViewedToggled(FileDiffViewModel file)
    {
        var viewed = file.IsViewed;
        var path = file.Path;
        var hash = file.Fingerprint;
        Persist((store, workspace, session) => viewed
            ? store.SetMarkAsync(workspace, session, ReviewMarkKind.Viewed, path, hash)
            : store.ClearMarkAsync(workspace, session, ReviewMarkKind.Viewed, path, hash));
        RaiseReviewTotals();
    }

    internal void ToggleKept(FileDiffViewModel file, ChangeBlockItemViewModel block)
    {
        var kept = !block.IsKept;
        block.SetKept(kept);
        var path = file.Path;
        var hash = block.Block.Hash;
        Persist((store, workspace, session) => kept
            ? store.SetMarkAsync(workspace, session, ReviewMarkKind.Kept, path, hash)
            : store.ClearMarkAsync(workspace, session, ReviewMarkKind.Kept, path, hash));
    }

    private void MarkAllViewed()
    {
        var items = new List<(string FilePath, string ItemHash)>();
        foreach (var file in files)
        {
            if (file.SetViewed(true))
            {
                items.Add((file.Path, file.Fingerprint));
            }
        }

        if (items.Count > 0)
        {
            Persist((store, workspace, session) => store.SetMarksAsync(workspace, session, ReviewMarkKind.Viewed, items));
        }

        RaiseReviewTotals();
    }

    /// <summary>"下一个未评审的" (J): the first unviewed file after the selected one, wrapping around.</summary>
    private void SelectNextUnviewed()
    {
        if (files.Count == 0)
        {
            return;
        }

        var start = selectedFile is null ? -1 : files.IndexOf(selectedFile);
        for (var step = 1; step <= files.Count; step++)
        {
            var candidate = files[(start + step + files.Count) % files.Count];
            if (!candidate.IsViewed)
            {
                SelectedFile = candidate;
                candidate.IsCollapsed = false;
                FocusRequested?.Invoke(candidate);
                return;
            }
        }
    }

    // ---------------------------------------------------------------- file actions

    internal void OpenInEditor(FileDiffViewModel file) => openFile?.Invoke(file.Path);

    internal void RequestCopyPath(FileDiffViewModel file) => CopyPathRequested?.Invoke(file.Path);

    internal void RevertBlock(FileDiffViewModel file, ChangeBlockItemViewModel block)
    {
        if (file.CanRevert)
        {
            _ = RunRevertAsync(root => ChangeBlockReverter.RevertBlockAsync(root, file.Diff, block.Block));
        }
    }

    internal async void RevertFile(FileDiffViewModel file)
    {
        if (!file.CanRevert)
        {
            return;
        }

        if (confirm is not null)
        {
            bool confirmed;
            try
            {
                confirmed = await confirm(
                        Loc.Instance[ChangesReviewStrings.RevertFileTitle],
                        ChangesReviewStrings.RevertFileMessage(file.Path),
                        Loc.Instance[ChangesReviewStrings.RevertFileConfirm],
                        ShellStrings.Cancel)
                    .ConfigureAwait(true);
            }
            catch (InvalidOperationException exception)
            {
                AppLogs.Changes.ZLogWarning($"revert confirmation failed: {exception.Message}");
                return;
            }

            if (!confirmed)
            {
                return;
            }
        }

        await RunRevertAsync(root => ChangeBlockReverter.RevertFileAsync(root, file.Diff)).ConfigureAwait(true);
    }

    private async Task RunRevertAsync(Func<string, Task<RevertResult>> revert)
    {
        if (isReverting || repositoryRoot.Length == 0)
        {
            return;
        }

        isReverting = true;
        var root = repositoryRoot;
        try
        {
            var result = await Task.Run(() => revert(root)).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                SetMessage(new LocText(ChangesReviewStrings.RevertFailedKey(result.Outcome)));
                return;
            }

            await LoadAsync(CancellationToken.None, reloadChoices: false).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetMessage(LocText.Literal(exception.Message));
        }
        finally
        {
            isReverting = false;
        }
    }
}

/// <summary>
/// The refs a loaded change set was read under, so one file can be read again with more context: <see cref="From"/> null is
/// the empty side of an unborn repository, <see cref="To"/> null is the working tree.
/// </summary>
internal sealed record DiffSource(string WorkingDirectory, string? From, string? To);
