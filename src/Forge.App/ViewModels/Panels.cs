using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.Editor.Highlighting;
using Forge.Editor.TextMate;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>The working context the right-hand panels follow: the active session's directory and its project root.</summary>
internal sealed record PanelContext(string SessionId, string WorkingDirectory, string? ProjectRoot)
{
    public static PanelContext Empty { get; } = new(string.Empty, string.Empty, null);

    public bool HasSession => SessionId.Length > 0;
}

// ---------------------------------------------------------------- files

/// <summary>
/// One node of the lazy file tree. Children come from the immutable <see cref="FileIndex"/>, and the tree is shown as one
/// flat, virtualized list of the rows that are currently visible (see <see cref="FlatTree{T}"/>).
/// </summary>
internal sealed class FileNodeViewModel : ObservableObject, IFlatTreeNode<FileNodeViewModel>
{
    private readonly FileIndex index;
    private readonly Action<string> open;
    private readonly Action<string> openInNewTab;
    private readonly Action<FileNodeViewModel> expansionChanged;
    private readonly FileNodeViewModel? parent;
    private readonly List<FileNodeViewModel> children = [];
    private int guideMask;
    private bool isExpanded;

    public FileNodeViewModel(
        FileEntry entry,
        FileIndex index,
        Action<string> open,
        Action<string> openInNewTab,
        int depth,
        FileNodeViewModel? parent,
        Action<FileNodeViewModel> expansionChanged)
    {
        Name = entry.Name;
        RelativePath = entry.RelativePath;
        IsDirectory = entry.IsDirectory;
        Depth = depth;
        this.index = index;
        this.open = open;
        this.openInNewTab = openInNewTab;
        this.expansionChanged = expansionChanged;
        this.parent = parent;
        guideMask = GuideMaskOf(parent);
        ToggleCommand = new RelayCommand(Toggle);
        OpenCommand = new RelayCommand(() => this.open(RelativePath));
        OpenInNewTabCommand = new RelayCommand(() => this.openInNewTab(RelativePath));
    }

    public string Name { get; }

    public string RelativePath { get; }

    public bool IsDirectory { get; }

    public int Depth { get; }

    /// <summary>The guide lines of this row: one step per expanded ancestor folder, aligned to that ancestor's chevron.</summary>
    public IReadOnlyList<IndentMark> Guides => IndentMarks.ForMask(guideMask);

    /// <summary>The file or folder icon drawn before the name; an expanded folder shows the open shape.</summary>
    public Geometry Icon => IsDirectory
        ? (isExpanded ? TreeIcons.FolderOpen : TreeIcons.Folder)
        : TreeIcons.File;

    public IReadOnlyList<FileNodeViewModel> Children => children;

    public RelayCommand ToggleCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand OpenInNewTabCommand { get; }

    /// <summary>Chevron points right when collapsed, down when expanded.</summary>
    public bool ChevronRotated => isExpanded;

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (!Set(ref isExpanded, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ChevronRotated));
            OnPropertyChanged(nameof(Icon));
            if (value)
            {
                LoadChildren();
            }

            foreach (var child in children)
            {
                child.RefreshGuideMask();
            }

            expansionChanged(this);
        }
    }

    private void Toggle()
    {
        if (IsDirectory)
        {
            IsExpanded = !IsExpanded;
            return;
        }

        open(RelativePath);
    }

    private void LoadChildren()
    {
        if (!IsDirectory || children.Count > 0)
        {
            return;
        }

        foreach (var entry in index.List(RelativePath))
        {
            children.Add(new FileNodeViewModel(entry, index, open, openInNewTab, Depth + 1, this, expansionChanged));
        }
    }

    /// <summary>The levels whose expanded ancestor folder draws a guide line above this row.</summary>
    private static int GuideMaskOf(FileNodeViewModel? parent) =>
        parent is null
            ? 0
            : parent.guideMask | (parent.isExpanded && parent.Depth < 31 ? 1 << parent.Depth : 0);

    /// <summary>Recomputes this node's guide mask after an ancestor expanded or collapsed, then its subtree.</summary>
    private void RefreshGuideMask()
    {
        var next = GuideMaskOf(parent);
        if (next == guideMask)
        {
            return;
        }

        guideMask = next;
        OnPropertyChanged(nameof(Guides));
        foreach (var child in children)
        {
            child.RefreshGuideMask();
        }
    }
}

/// <summary>
/// One row of <c>已改动</c>: the path, and at the far right the diff line counts followed by the status letter. A file
/// whose counts could not be read (no HEAD yet, an unreadable diff) shows the status letter alone, and so does a binary
/// file: a patch of a binary change has no line counts, so "+0 -0" would be a lie.
/// <para>The status description is kept as a <see cref="LocText"/> and resolved when the tooltip is read, so the row shows
/// the current language whenever the panel re-raises it. A row never subscribes to <see cref="Loc.LanguageChanged"/>: one
/// strong reference per row would keep the whole panel alive.</para>
/// </summary>
internal sealed class ChangedFileViewModel : ObservableObject
{
    private readonly LocText description;

    public ChangedFileViewModel(
        string path,
        string status,
        int? insertions,
        int? deletions,
        bool isBinary,
        LocText description)
    {
        Path = path;
        Status = status;
        Insertions = insertions;
        Deletions = deletions;
        IsBinary = isBinary;
        this.description = description;
    }

    public string Path { get; }

    public string Status { get; }

    public int? Insertions { get; }

    public int? Deletions { get; }

    public bool IsBinary { get; }

    /// <summary>Staged, staged and unstaged, or the working tree: the tooltip of the row.</summary>
    public string Description => description.Resolve();

    public string Meta => ChangeBadge.Format(Status, Insertions, Deletions, IsBinary);

    public string Display => Path + "  " + Meta;

    /// <summary>Re-reads the description after a language switch; the owning panel calls this, the row never listens itself.</summary>
    internal void RefreshText() => OnPropertyChanged(nameof(Description));
}

/// <summary>The line-count half of a <c>已改动</c> row: counts are shown only when the diff reported them.</summary>
internal static class ChangeBadge
{
    /// <summary>"+N -N M", or the status letter alone when the counts are unknown or the file is binary.</summary>
    public static string Format(string statusLetter, int? insertions, int? deletions, bool isBinary)
    {
        if (isBinary || insertions is not { } added || deletions is not { } removed)
        {
            return statusLetter;
        }

        return "+" + TextFormat.Id(added) + " -" + TextFormat.Id(removed) + " " + statusLetter;
    }
}

/// <summary>
/// The right-top "文件" tool: a lazy tree of everything (<c>全部</c>) and the git status list with M/A/D/?? badges
/// (<c>已改动</c>). Both refresh on turn end and on a debounced file-system watcher; the tree is rebuilt off the dispatcher
/// and re-applied as one virtualized row list, keeping every expanded folder and the row order. The panel's own texts are
/// resolved from <see cref="Loc"/> on every read and re-raised when the language changes.
/// </summary>
internal sealed class FilesPanelViewModel : ObservableObject, IDisposable
{
    private readonly GitCli git;
    private readonly Action<string> openFile;
    private readonly Action<string> openFileInNewTab;
    private readonly Action<string> openChanges;
    private readonly ChangesViewModel? changeReview;
    private readonly ChangedFileTree changedTree;
    private readonly FlatTree<FileNodeViewModel> tree = new();
    private readonly HashSet<string> expandedPaths = new(StringComparer.OrdinalIgnoreCase);
    private FileIndex? index;
    private FileSystemWatcher? watcher;
    private DispatcherTimer? watcherTimer;
    private CountsSnapshot? countsSnapshot;
    private readonly Func<string, CancellationToken, Task<Dictionary<string, LineCount>>>? lineCountsFactory;
    private readonly Git.GitSyncViewModel? gitSync;
    private LocText error;
    private bool isLoading;
    private int selectedTab;
    private int refreshGeneration;

    public FilesPanelViewModel(GitCli git, Action<string> openFile, Action<string> openFileInNewTab, Action<string> openChanges)
        : this(git, openFile, openFileInNewTab, openChanges, lineCountsFactory: null, changeReview: null)
    {
    }

    internal FilesPanelViewModel(
        GitCli git,
        Action<string> openFile,
        Action<string> openFileInNewTab,
        Action<string> openChanges,
        Func<string, CancellationToken, Task<Dictionary<string, LineCount>>>? lineCountsFactory,
        ChangesViewModel? changeReview = null,
        Git.GitSyncViewModel? gitSync = null)
    {
        this.git = git;
        this.gitSync = gitSync;
        this.openFile = openFile;
        this.openFileInNewTab = openFileInNewTab;
        this.openChanges = openChanges;
        this.lineCountsFactory = lineCountsFactory;
        this.changeReview = changeReview;
        changedTree = new ChangedFileTree(OpenChanged);
        changedTree.FoldingChanged += OnChangedFoldingChanged;
        ToggleChangedFoldingCommand = new RelayCommand(() => changedTree.SetAllExpanded(changedTree.IsAllCollapsed));
        if (changeReview is not null)
        {
            changeReview.Files.CollectionChanged += OnChangeReviewFilesChanged;
            changeReview.PropertyChanged += OnChangeReviewPropertyChanged;
            changedTree.Replace(changeReview.Files);
            changedTree.SetSelected(changeReview.SelectedFile);
        }
        ChangedCommand = new AsyncRelayCommand(ct => RefreshAsync(ct), () => true, OnFailed);
        AllTabCommand = new RelayCommand(() => SelectedTab = 0);
        ChangedTabCommand = new RelayCommand(() => SelectedTab = 1);
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Test seam: builds the panel already attached to a context, without starting the watcher or a background refresh.</summary>
    internal FilesPanelViewModel(
        GitCli git,
        Action<string> openFile,
        Action<string> openFileInNewTab,
        Action<string> openChanges,
        Func<string, CancellationToken, Task<Dictionary<string, LineCount>>>? lineCountsFactory,
        PanelContext context)
        : this(git, openFile, openFileInNewTab, openChanges, lineCountsFactory, changeReview: null)
    {
        Context = context;
    }

    /// <summary>The last atomically published fingerprint/result pair; exposed for the cache race tests.</summary>
    internal CountsSnapshot? CountsSnapshotForTests => Volatile.Read(ref countsSnapshot);

    /// <summary>The visible rows of the "全部" tree, in display order; the view virtualizes this one list.</summary>
    public RangeObservableCollection<FileNodeViewModel> Rows => tree.Rows;

    /// <summary>The legacy status rows are used only by standalone panel tests; the app binds to the shared diff view model.</summary>
    public ObservableCollection<ChangedFileViewModel> Changes { get; } = [];

    public ChangesViewModel? ChangeReview => changeReview;

    /// <summary>
    /// The git row at the bottom of the panel: branch, ahead/behind and the three sync verbs for the active session's
    /// worktree. It sits here because the 已改动 tree compares against a local ref, and only a fetch makes that ref current.
    /// </summary>
    public Git.GitSyncViewModel? Git => gitSync;

    /// <summary>The 已改动 tab's rows: the shared change set as a folder tree (virtualized flat list).</summary>
    public RangeObservableCollection<ChangedTreeNodeViewModel> ChangedRows => changedTree.Rows;

    internal ChangedFileTree ChangedTree => changedTree;

    /// <summary>Folds or unfolds every folder of the changed tree.</summary>
    public RelayCommand ToggleChangedFoldingCommand { get; }

    /// <summary>The refresh button belongs to the 全部 tab (and the legacy list); the changed tree has the fold toggle instead.</summary>
    public bool ShowRefreshButton => !ShowSharedChanges;

    public bool ShowFoldingButton => ShowSharedChanges;

    public bool IsChangedFolded => changedTree.IsAllCollapsed;

    public bool IsChangedUnfolded => !IsChangedFolded;

    /// <summary>Tooltip/name of the fold toggle: what pressing it does now.</summary>
    public string FoldingToggleText => Loc.Instance[IsChangedFolded ? ChangesPickerStrings.ExpandAll : ChangesPickerStrings.CollapseAll];

    public bool HasSharedChanges => changeReview is not null;

    public AsyncRelayCommand ChangedCommand { get; }

    public RelayCommand AllTabCommand { get; }

    public RelayCommand ChangedTabCommand { get; }

    public PanelContext Context { get; private set; } = PanelContext.Empty;

    public int SelectedTab
    {
        get => selectedTab;
        set
        {
            if (Set(ref selectedTab, value))
            {
                OnPropertyChanged(nameof(IsAllTab));
                OnPropertyChanged(nameof(IsChangedTab));
                OnPropertyChanged(nameof(ShowSharedChanges));
                OnPropertyChanged(nameof(ShowFallbackChanges));
                OnPropertyChanged(nameof(ShowRefreshButton));
                OnPropertyChanged(nameof(ShowFoldingButton));
            }
        }
    }

    public bool IsAllTab => selectedTab == 0;

    public bool IsChangedTab => selectedTab == 1;

    public bool ShowSharedChanges => IsChangedTab && changeReview is not null;

    public bool ShowFallbackChanges => IsChangedTab && changeReview is null;

    public bool IsLoading
    {
        get => isLoading;
        private set => Set(ref isLoading, value);
    }

    /// <summary>
    /// The "全部" tab's badge: every file the index holds. The tree is lazy, so this is the index's count rather than the
    /// rows on screen — collapsing a folder must not shrink "all files".
    /// </summary>
    public int AllCount => index?.FileCount ?? 0;

    /// <summary>The "已改动" tab's badge: one per row of <see cref="Changes"/>.</summary>
    public int ChangedCount => changeReview?.Files.Count ?? Changes.Count;

    /// <summary>
    /// The tabs' accessible names. Assistive technology replaces a button's content with this name, so the badge's number has
    /// to appear here or the count would be invisible to it.
    /// </summary>
    public string AllTabName => InspectorStrings.FilesAllCountName(AllCount);

    public string ChangedTabName => InspectorStrings.FilesChangedCountName(ChangedCount);

    /// <summary>Re-raises everything the toolbar reads off the two counts: the badges and the accessible names.</summary>
    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(AllCount));
        OnPropertyChanged(nameof(ChangedCount));
        OnPropertyChanged(nameof(AllTabName));
        OnPropertyChanged(nameof(ChangedTabName));
    }

    private void OnChangeReviewFilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RaiseCounts();
        if (changeReview is not null)
        {
            changedTree.Replace(changeReview.Files);
        }
    }

    private void OnChangeReviewPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(ChangesViewModel.SelectedFile), StringComparison.Ordinal))
        {
            changedTree.SetSelected(changeReview?.SelectedFile);
        }
    }

    private void OnChangedFoldingChanged()
    {
        OnPropertyChanged(nameof(IsChangedFolded));
        OnPropertyChanged(nameof(IsChangedUnfolded));
        OnPropertyChanged(nameof(FoldingToggleText));
    }

    /// <summary>Why the last refresh failed, in the language of the moment it is read; null when it did not fail.</summary>
    public string? Error => error.IsEmpty ? null : error.Resolve();

    public void Attach(PanelContext context)
    {
        var changed = !string.Equals(Context.SessionId, context.SessionId, StringComparison.Ordinal);
        Context = context;
        OnPropertyChanged(nameof(Context));
        RaiseCounts();
        if (changed)
        {
            expandedPaths.Clear();
            tree.Clear();
            Changes.Clear();
            index = null;
            Volatile.Write(ref countsSnapshot, null);
        }

        RestartWatcher();
        _ = RefreshAsync(CancellationToken.None, refreshChangeReview: false, fromWatcher: false);
    }

    public Task RefreshAsync(CancellationToken ct) => RefreshAsync(ct, refreshChangeReview: true, fromWatcher: false);

    private async Task RefreshAsync(CancellationToken ct, bool refreshChangeReview, bool fromWatcher)
    {
        var context = Context;
        if (!context.HasSession || !Directory.Exists(context.WorkingDirectory))
        {
            tree.Clear();
            if (changeReview is null)
            {
                Changes.Clear();
            }

            index = null;
            if (changeReview is not null && refreshChangeReview)
            {
                await changeReview.LoadAsync(ct).ConfigureAwait(true);
            }

            RaiseCounts();
            return;
        }

        var generation = Interlocked.Increment(ref refreshGeneration);
        IsLoading = true;
        try
        {
            var workingDirectory = context.WorkingDirectory;
            var expanded = new HashSet<string>(expandedPaths, StringComparer.OrdinalIgnoreCase);
            var built = await Task.Run(() => BuildTreeAsync(workingDirectory, expanded, ct), ct).ConfigureAwait(true);
            var status = changeReview is null
                ? await Task.Run(() => ReadStatusAsync(workingDirectory, ct), ct).ConfigureAwait(true)
                : null;
            if (generation != Volatile.Read(ref refreshGeneration))
            {
                return;
            }

            index = built.Index;
            tree.Replace(built.Rows);

            if (status is not null)
            {
                Changes.Clear();
                foreach (var change in status)
                {
                    Changes.Add(change);
                }
            }
            else if (refreshChangeReview)
            {
                await changeReview!.LoadAsync(ct).ConfigureAwait(true);
            }
            else if (fromWatcher)
            {
                await changeReview!.RefreshFromFileSystemChangeAsync(ct).ConfigureAwait(true);
            }

            error = default;
            RaiseCounts();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or GitException or UnauthorizedAccessException)
        {
            error = LocText.Literal(exception.Message);
        }
        finally
        {
            if (generation == Volatile.Read(ref refreshGeneration))
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Index, node view models and visible rows are all built here, on a pool thread.</summary>
    private async Task<(FileIndex Index, List<FileNodeViewModel> Rows)> BuildTreeAsync(
        string workingDirectory,
        HashSet<string> expanded,
        CancellationToken ct)
    {
        var built = await FileIndex.BuildAsync(workingDirectory, git, ct).ConfigureAwait(false);
        var roots = new List<FileNodeViewModel>();
        foreach (var entry in built.List(string.Empty))
        {
            var node = new FileNodeViewModel(entry, built, openFile, openFileInNewTab, 0, null, OnExpansionChanged);
            Expand(node, expanded);
            roots.Add(node);
        }

        return (built, FlatTree<FileNodeViewModel>.Flatten(roots));
    }

    private static void Expand(FileNodeViewModel node, HashSet<string> expanded)
    {
        if (!node.IsDirectory || !expanded.Contains(node.RelativePath))
        {
            return;
        }

        node.IsExpanded = true;
        foreach (var child in node.Children)
        {
            Expand(child, expanded);
        }
    }

    private void OnExpansionChanged(FileNodeViewModel node)
    {
        if (node.IsExpanded)
        {
            expandedPaths.Add(node.RelativePath);
        }
        else
        {
            expandedPaths.Remove(node.RelativePath);
        }

        tree.ExpansionChanged(node);
    }

    /// <summary>Opens the changes document scoped to one changed file.</summary>
    public void OpenChanged(ChangedFileViewModel change) => openChanges(change.Path);

    public void OpenChanged(FileDiffViewModel change) => openChanges(change.Path);

    private async Task<IReadOnlyList<ChangedFileViewModel>> ReadStatusAsync(string workingDirectory, CancellationToken ct)
    {
        if (!await git.IsRepositoryAsync(workingDirectory, ct).ConfigureAwait(false))
        {
            return [];
        }

        var entries = await git.GetStatusAsync(workingDirectory, ct).ConfigureAwait(false);
        var root = await git.GetRepositoryRootAsync(workingDirectory, ct).ConfigureAwait(false);
        var prefix = root is null ? string.Empty : Path.GetRelativePath(root, workingDirectory).Replace(Path.DirectorySeparatorChar, '/');
        if (string.Equals(prefix, ".", StringComparison.Ordinal))
        {
            prefix = string.Empty;
        }
        else if (prefix.Length > 0)
        {
            prefix += "/";
        }

        var counts = await ReadLineCountsAsync(workingDirectory, root ?? workingDirectory, entries, ct).ConfigureAwait(false);
        var result = new List<ChangedFileViewModel>(entries.Count);
        foreach (var entry in entries)
        {
            var path = entry.Path;
            if (prefix.Length > 0)
            {
                if (!path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                path = path[prefix.Length..];
            }

            var known = counts.TryGetValue(path.Replace(Path.DirectorySeparatorChar, '/'), out var lines);
            result.Add(new ChangedFileViewModel(
                path,
                Status(entry),
                known ? lines.Added : null,
                known ? lines.Removed : null,
                known && lines.IsBinary,
                Describe(entry)));
        }

        result.Sort(static (left, right) => string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// Insertions and deletions per path with the same semantics as <c>git diff --numstat HEAD</c>, which is also what the
    /// changes document shows for "未提交改动": the working tree against HEAD, untracked files included, paths kept
    /// relative to the session directory so a session in a subdirectory of the repository (the general workspace's
    /// <c>scratch/…</c>) still finds its rows. A patch of a binary change carries no counts and stays unknown.
    /// The whole diff is read at most once per file-system fingerprint: the watcher ticks every 700 ms and rebuilding a
    /// full patch each time is what makes the panel expensive.
    /// </summary>
    private async Task<Dictionary<string, LineCount>> ReadLineCountsAsync(
        string workingDirectory,
        string repositoryRoot,
        IReadOnlyList<GitStatusEntry> entries,
        CancellationToken ct)
    {
        var fingerprint = CountsFingerprint(workingDirectory, repositoryRoot, entries);
        var snapshot = Volatile.Read(ref countsSnapshot);
        if (snapshot is not null && string.Equals(fingerprint, snapshot.Fingerprint, StringComparison.Ordinal))
        {
            return snapshot.Counts;
        }

        var counts = lineCountsFactory is not null
            ? await lineCountsFactory(fingerprint, ct).ConfigureAwait(false)
            : await ComputeLineCountsAsync(workingDirectory, repositoryRoot, entries, ct).ConfigureAwait(false);

        Volatile.Write(ref countsSnapshot, new CountsSnapshot(fingerprint, counts));
        return counts;
    }

    private async Task<Dictionary<string, LineCount>> ComputeLineCountsAsync(
        string workingDirectory,
        string repositoryRoot,
        IReadOnlyList<GitStatusEntry> entries,
        CancellationToken ct)
    {
        var counts = new Dictionary<string, LineCount>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var head = await git.RevParseAsync(repositoryRoot, "HEAD", ct).ConfigureAwait(false);
            // Run from the repository root so tracked and untracked paths are both repository-relative; the row keys
            // below are session-relative, which is what RelativeTo turns them into.
            var diffs = await git.GetDiffAsync(
                    repositoryRoot,
                    new GitDiffOptions
                    {
                        From = head is null ? null : "HEAD",
                        IncludeUntracked = true,
                        Paths = ["."],
                    },
                    ct)
                .ConfigureAwait(false);
            var root = await git.GetRepositoryRootAsync(repositoryRoot, ct).ConfigureAwait(false) ?? repositoryRoot;
            foreach (var diff in diffs)
            {
                var path = RelativeTo(root, workingDirectory, diff.Path);
                if (path is null)
                {
                    continue;
                }

                counts[path] = counts.TryGetValue(path, out var existing)
                    ? new LineCount(existing.Added + diff.AddedLineCount, existing.Removed + diff.RemovedLineCount, existing.IsBinary || diff.IsBinary)
                    : new LineCount(diff.AddedLineCount, diff.RemovedLineCount, diff.IsBinary);
            }
        }
        catch (Exception exception) when (exception is GitException or IOException or InvalidOperationException)
        {
            counts.Clear();
        }

        return counts;
    }

    /// <summary>Cheap change detector: the status entries plus the size and write time of every file they name.</summary>
    private static string CountsFingerprint(
        string workingDirectory,
        string repositoryRoot,
        IReadOnlyList<GitStatusEntry> entries)
    {
        if (entries.Count == 0)
        {
            return workingDirectory;
        }

        var builder = new System.Text.StringBuilder(workingDirectory);
        foreach (var entry in entries)
        {
            builder.Append('\n').Append(entry.Path).Append(':').Append((int)entry.Kind).Append(entry.IsStaged ? "s" : "w");
            try
            {
                var info = new FileInfo(Path.Combine(repositoryRoot, entry.Path));
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
    }

    /// <summary>The diff path (repository-relative) as the row shows it (session-relative), or null when outside.</summary>
    private static string? RelativeTo(string repositoryRoot, string workingDirectory, string path)
    {
        var normalized = path.Replace(Path.DirectorySeparatorChar, '/');
        var relative = Path.GetRelativePath(repositoryRoot, workingDirectory).Replace(Path.DirectorySeparatorChar, '/');
        if (string.Equals(relative, ".", StringComparison.Ordinal) || relative.Length == 0)
        {
            return normalized;
        }

        var prefix = relative + "/";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? normalized[prefix.Length..] : null;
    }

    internal readonly record struct LineCount(int Added, int Removed, bool IsBinary);

    /// <summary>An immutable fingerprint/result pair so the two halves of the line-count cache can only be read and written together.</summary>
    internal sealed record CountsSnapshot(string Fingerprint, Dictionary<string, LineCount> Counts);

    private static string Status(GitStatusEntry entry) => entry.Kind switch
    {
        GitStatusKind.Untracked => "A",
        GitStatusKind.Renamed => "R",
        GitStatusKind.Copied => "C",
        GitStatusKind.Unmerged => "U",
        _ => entry.IsStaged ? entry.IndexStatus switch
        {
            GitEntryStatus.Added => "A",
            GitEntryStatus.Deleted => "D",
            GitEntryStatus.Renamed => "R",
            _ => "M",
        } : entry.WorkTreeStatus switch
        {
            GitEntryStatus.Added => "A",
            GitEntryStatus.Deleted => "D",
            _ => "M",
        },
    };

    private static LocText Describe(GitStatusEntry entry) =>
        InspectorStrings.ChangeDescription(entry.IsStaged, entry.IsUnstaged);

    private void RestartWatcher()
    {
        watcher?.Dispose();
        watcher = null;
        watcherTimer?.Stop();
        if (!Context.HasSession || !Directory.Exists(Context.WorkingDirectory))
        {
            return;
        }

        try
        {
            if (watcherTimer is null)
            {
                watcherTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
                watcherTimer.Tick += OnWatcherTick;
            }

            watcher = new FileSystemWatcher(Context.WorkingDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            watcher.Changed += OnFileSystemChanged;
            watcher.Created += OnFileSystemChanged;
            watcher.Deleted += OnFileSystemChanged;
            watcher.Renamed += OnFileSystemChanged;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            error = InspectorStrings.WatchUnavailable(exception.Message);
        }
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e) => Dispatcher.UIThread.Post(RestartDebounce);

    private void RestartDebounce()
    {
        watcherTimer?.Stop();
        watcherTimer?.Start();
    }

    private void OnWatcherTick(object? sender, EventArgs e)
    {
        watcherTimer?.Stop();
        if (IsLoading)
        {
            watcherTimer?.Start();
            return;
        }

        _ = RefreshAsync(CancellationToken.None, refreshChangeReview: false, fromWatcher: true);
    }

    private void OnFailed(Exception exception) => error = LocText.Literal(exception.Message);

    /// <summary>
    /// Re-reads the panel's own texts and hands the refresh down to every changed-file row: a row never subscribes to
    /// <see cref="Loc.LanguageChanged"/> itself, one strong reference per row would keep the whole panel alive.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RaiseCounts();
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(FoldingToggleText));
        foreach (var change in Changes)
        {
            change.RefreshText();
        }
    }

    /// <summary>
    /// Drops the language subscription (a panel that is gone must not be refreshed by the next language switch) and the
    /// file watcher with its debounce timer: a stopped <see cref="DispatcherTimer"/> stays in its dispatcher's list until
    /// the dispatcher next runs, so its Tick handler is unhooked to end the reference to this panel.
    /// </summary>
    public void Dispose()
    {
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        if (changeReview is not null)
        {
            changeReview.Files.CollectionChanged -= OnChangeReviewFilesChanged;
            changeReview.PropertyChanged -= OnChangeReviewPropertyChanged;
        }

        changedTree.FoldingChanged -= OnChangedFoldingChanged;
        watcher?.Dispose();
        watcher = null;
        if (watcherTimer is not null)
        {
            watcherTimer.Stop();
            watcherTimer.Tick -= OnWatcherTick;
            watcherTimer = null;
        }
    }
}

// ---------------------------------------------------------------- file document

/// <summary>
/// File view model: the native editor renders and highlights the text. The file's content and path are data
/// and are never translated; only the tab title's leading mark and the placeholder before a file is loaded come from the
/// string table, and both are read when the property is read.
/// </summary>
internal sealed partial class FileDocumentViewModel : ObservableObject
{
    private readonly GrammarRegistry? grammars;
    private string path = string.Empty;
    private string? fileName;
    private LocText error;
    private bool isLoading;

    private FileDocumentViewModel(GrammarRegistry? grammars)
    {
        this.grammars = grammars;
        LoadCommand = new AsyncRelayCommand(
            ct => LoadAsync(path, ct),
            () => path.Length > 0,
            exception => SetError(LocText.Literal(exception.Message)));
    }

    public AsyncRelayCommand LoadCommand { get; }

    public string Path
    {
        get => path;
        private set
        {
            if (Set(ref path, value))
            {
                OnPropertyChanged(nameof(PathDisplay));
            }
        }
    }

    public string PathDisplay => path;

    /// <summary>The file name, or the generic word until a file is loaded; the tab's leading mark is its icon, not a glyph.</summary>
    public string TabTitle => InspectorStrings.FileTabTitle((fileName ?? Loc.Instance[InspectorStrings.FilesName]) + (IsDirty ? " *" : string.Empty));

    public bool IsLoading
    {
        get => isLoading;
        private set => Set(ref isLoading, value);
    }

    /// <summary>The read problem (missing file, too large, truncated, an OS error), in the language of the moment it is read.</summary>
    public string? Error => error.IsEmpty ? null : error.Resolve();

    public bool HasError => !error.IsEmpty;

    private void SetError(LocText value)
    {
        error = value;
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
    }

}

// ---------------------------------------------------------------- changes document

internal enum ChangeScope
{
    ByTurn = 0,
    Uncommitted = 1,
    All = 2,

    /// <summary>One commit of the current branch since the comparison base (<c>commit^..commit</c>).</summary>
    Commit = 3,
}

/// <summary>
/// One file of the changes document. The review half (blocks, viewed/kept marks, commands, <see cref="IDiffFileItem"/>)
/// lives in <c>FileDiffViewModel.Review.cs</c>.
/// </summary>
internal sealed partial class FileDiffViewModel
{
    private readonly int added;
    private readonly int removed;
    private readonly bool isBinary;

    /// <summary>Added line count (for the changed-file tree).</summary>
    public int Added => added;

    /// <summary>Removed line count (for the changed-file tree).</summary>
    public int Removed => removed;

    public bool IsBinary => isBinary;

    public FileDiffViewModel(FileDiff diff, string prefix = "", bool fullContext = false)
    {
        Path = Strip(diff, prefix);
        added = diff.AddedLineCount;
        removed = diff.RemovedLineCount;
        isBinary = diff.IsBinary;
        Badge = diff.Status switch
        {
            FileDiffStatus.Added => "A",
            FileDiffStatus.Deleted => "D",
            FileDiffStatus.Renamed => "R",
            FileDiffStatus.Copied => "C",
            _ => "M",
        };
        Diff = diff;
        InitializeReview(diff, fullContext);
    }

    public string Path { get; }

    public string Header => InspectorStrings.FileDiffHeader(Path, added, removed);

    public string Badge { get; }

    /// <summary>The added/removed counts and status shown in the file panel; binary patches do not invent zero counts.</summary>
    public string Meta => ChangeBadge.Format(Badge, added, removed, isBinary);

    public string Description => Header;

    /// <summary>Diff paths are repository-relative; the panel shows them relative to the session directory.</summary>
    private static string Strip(FileDiff diff, string prefix)
    {
        var path = diff.Path.Replace(System.IO.Path.DirectorySeparatorChar, '/');
        return prefix.Length > 0 && path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }
}

internal sealed class TurnChoiceViewModel : ObservableObject
{
    private readonly LocText label;
    private bool isSelected;

    private int? fileCount;
    private bool isLatest;
    private bool isChecked;

    public TurnChoiceViewModel(
        int turn,
        LocText label,
        string treeBefore,
        string? treeAfter,
        Action<TurnChoiceViewModel> select,
        DateTimeOffset startedAt = default)
    {
        Turn = turn;
        StartedAt = startedAt;
        this.label = label;
        TreeBefore = treeBefore;
        TreeAfter = treeAfter;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public int Turn { get; }

    public string AutomationId => "changes-turn-" + TextFormat.Id(Turn);

    /// <summary>The turn number and its start time, in the language of the moment the entry is read.</summary>
    public string Label => label.Resolve();

    public bool IsSelected
    {
        get => isSelected;
        private set => Set(ref isSelected, value);
    }

    public string TreeBefore { get; }

    public string? TreeAfter { get; }

    public DateTimeOffset StartedAt { get; }

    public RelayCommand SelectCommand { get; }

    /// <summary>How many files the turn changed; null until the picker has counted them in the background.</summary>
    public int? FileCount
    {
        get => fileCount;
        internal set
        {
            if (Set(ref fileCount, value))
            {
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    /// <summary>The newest turn: shown as "上一轮".</summary>
    public bool IsLatest
    {
        get => isLatest;
        internal set
        {
            if (Set(ref isLatest, value))
            {
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    /// <summary>True when this turn is the scope in use (the ✓ of the picker row); <see cref="IsSelected"/> only remembers the choice.</summary>
    public bool IsChecked
    {
        get => isChecked;
        internal set => Set(ref isChecked, value);
    }

    /// <summary>"上一轮" for the newest turn, otherwise "第 14:05 轮".</summary>
    public string Title => IsLatest ? Loc.Instance[ChangesPickerStrings.LatestTurn] : ChangesPickerStrings.TurnTitle(StartedAt);

    /// <summary>"3 个文件 · 3 天前".</summary>
    public string Subtitle => ChangesPickerStrings.TurnSubtitle(fileCount, StartedAt, DateTimeOffset.Now);

    internal void SetSelected(bool value) => IsSelected = value;

    /// <summary>Re-reads the label after a language switch; the panel calls this for every entry.</summary>
    internal void RefreshText()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
    }
}

/// <summary>
/// The fixed "改动" document. Scope "按轮次" diffs the two trees the workspace layer recorded around one main-session turn
/// (<see cref="GitCli.GetTreeDiffAsync"/> over the hashes in <c>turn_checkpoints</c>); "未提交改动" diffs the working tree
/// against HEAD including untracked files. All files are stacked into one flat row list that the view virtualizes;
/// selecting one scrolls the view to it.
/// </summary>
internal sealed partial class ChangesViewModel : ObservableObject, IDisposable
{
    private readonly GitCli git;
    private readonly ITurnCheckpointStore checkpoints;
    private readonly Action<string>? openFile;
    private readonly RangeObservableCollection<FileDiffViewModel> files = [];
    private PanelContext context = PanelContext.Empty;
    private ChangeScope scope = ChangeScope.Uncommitted;
    private TurnChoiceViewModel? selectedTurn;
    private FileDiffViewModel? selectedFile;
    private bool isLoading;
    private LocText message;
    private int generation;

    public ChangesViewModel(GitCli git, ITurnCheckpointStore checkpoints, Action<string>? openFile = null)
    {
        this.git = git;
        this.checkpoints = checkpoints;
        this.openFile = openFile;
        // One picker instance hosts both the compare-base flyout and (through the dialog service) the new-branch dialog;
        // picking another base reloads the diff only where the scope compares against a branch.
        picker = new BranchPickerViewModel(
            git,
            OnBranchPicked,
            exception => SetMessage(LocText.Literal(exception.Message)));
        picker.PropertyChanged += OnPickerPropertyChanged;
        RefreshCommand = new AsyncRelayCommand(
            ct => LoadAsync(ct),
            () => true,
            exception => SetMessage(LocText.Literal(exception.Message)));
        AllChangesCommand = new RelayCommand(() => SetScope(ChangeScope.All));
        UncommittedCommand = new RelayCommand(() => SetScope(ChangeScope.Uncommitted));
        PreviousFileCommand = new RelayCommand(() => SelectAdjacentFile(-1), CanGoPreviousFile);
        NextFileCommand = new RelayCommand(() => SelectAdjacentFile(1), CanGoNextFile);
        OpenInEditorCommand = new RelayCommand(OpenSelectedFile, () => openFile is not null && selectedFile is not null);
        EnsureReviewCommands();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<TurnChoiceViewModel> Turns { get; } = [];

    public RangeObservableCollection<FileDiffViewModel> Files => files;

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand UncommittedCommand { get; }

    public RelayCommand PreviousFileCommand { get; }

    public RelayCommand NextFileCommand { get; }

    public RelayCommand OpenInEditorCommand { get; }

    public bool HasSelectedFile => selectedFile is not null;

    public PanelContext Context => context;

    public bool IsByTurn => scope == ChangeScope.ByTurn;

    public bool IsUncommitted => scope == ChangeScope.Uncommitted;

    public TurnChoiceViewModel? SelectedTurn
    {
        get => selectedTurn;
        set
        {
            if (value is null)
            {
                return;
            }

            if (SetSelectedTurn(value))
            {
                _ = LoadAsync(CancellationToken.None, reloadChoices: false);
            }
        }
    }

    public FileDiffViewModel? SelectedFile
    {
        get => selectedFile;
        set
        {
            if (Set(ref selectedFile, value))
            {
                OnPropertyChanged(nameof(HasSelectedFile));
                PreviousFileCommand.NotifyCanExecuteChanged();
                NextFileCommand.NotifyCanExecuteChanged();
                OpenInEditorCommand.NotifyCanExecuteChanged();
                OnSelectedFileChanged();
            }
        }
    }

    public bool IsLoading
    {
        get => isLoading;
        private set => Set(ref isLoading, value);
    }

    /// <summary>"没有改动。" or why the last load failed, in the language of the moment it is read.</summary>
    public string Message => message.Resolve();

    public bool HasMessage => !message.IsEmpty;

    public string Summary => context.HasSession
        ? InspectorStrings.ChangesSummary(files.Count, IsByTurn, IsAllChanges)
        : Loc.Instance[InspectorStrings.ChangesNoSession];

    public event Action<FileDiffViewModel>? FocusRequested;

    public void Attach(PanelContext panelContext)
    {
        var changed = !string.Equals(context.SessionId, panelContext.SessionId, StringComparison.Ordinal);
        context = panelContext;
        OnPropertyChanged(nameof(Context));
        if (changed)
        {
            files.ReplaceAll(Array.Empty<FileDiffViewModel>());
            OnFilesReplaced(string.Empty);
            Turns.Clear();
            SetSelectedTurn(null);
            OnPropertyChanged(nameof(HasNoTurns));
            ClearBranchChoices();
            ClearCommitChoices();
            Volatile.Write(ref uncommittedFingerprint, null);
        }

        _ = LoadAsync(CancellationToken.None);
    }

    /// <summary>Opens (and scrolls to) one file of the current change set.</summary>
    public async Task ShowFileAsync(string path, CancellationToken ct)
    {
        var match = files.FirstOrDefault(
            file => string.Equals(file.Path.Replace('\\', '/'), path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            await LoadAsync(ct).ConfigureAwait(true);
            match = files.FirstOrDefault(
                file => string.Equals(file.Path.Replace('\\', '/'), path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        }

        if (match is not null)
        {
            SelectedFile = match;
            FocusRequested?.Invoke(match);
        }
    }

    public Task LoadAsync(CancellationToken ct) => LoadAsync(ct, reloadChoices: true);

    private async Task LoadAsync(CancellationToken ct, bool reloadChoices, string? precomputedFingerprint = null)
    {
        var current = Interlocked.Increment(ref generation);
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var previous = Interlocked.Exchange(ref loadSource, source);
        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            var panelContext = context;
            if (!panelContext.HasSession || !Directory.Exists(panelContext.WorkingDirectory))
            {
                files.ReplaceAll(Array.Empty<FileDiffViewModel>());
                OnFilesReplaced(string.Empty);
                if (reloadChoices)
                {
                    Turns.Clear();
                    SetSelectedTurn(null);
                    OnPropertyChanged(nameof(HasNoTurns));
                    ClearBranchChoices();
                    Volatile.Write(ref uncommittedFingerprint, null);
                }

                SelectedFile = null;
                SetMessage(panelContext.HasSession
                    ? new LocText(InspectorStrings.ChangesNoChanges)
                    : new LocText(InspectorStrings.ChangesNoSession));
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(ScopeLabel));
                return;
            }

            IsLoading = true;
            var workingDirectory = panelContext.WorkingDirectory;
            picker.SetWorkingDirectory(workingDirectory);
            if (reloadChoices)
            {
                await ReloadTurnsAsync(source.Token).ConfigureAwait(true);

                // A load that a newer one has already replaced must not touch the shared picker: its read would bump the
                // picker's generation and then be cancelled, so the newer load's answer would be discarded and the picker
                // would stay empty (attaching a context fires a load of its own, so two loads overlapping is normal).
                if (current != Volatile.Read(ref generation))
                {
                    return;
                }

                await picker.LoadAsync(workingDirectory, skipCurrentBranch: true, source.Token).ConfigureAwait(true);
            }

            var scopeSnapshot = scope;
            var turn = selectedTurn;
            var branch = picker.SelectedBranch;
            var fingerprint = scopeSnapshot == ChangeScope.Uncommitted
                ? precomputedFingerprint ?? await ReadUncommittedFingerprintAsync(workingDirectory, source.Token).ConfigureAwait(true)
                : null;
            var built = await Task.Run(
                    () => BuildRowsAsync(workingDirectory, scopeSnapshot, turn, branch, source.Token),
                    source.Token)
                .ConfigureAwait(true);
            if (current != Volatile.Read(ref generation) || source.IsCancellationRequested)
            {
                return;
            }

            var previousSelection = selectedFile?.Path;
            Volatile.Write(ref diffBaseCommit, built.BaseCommit);
            diffSource = built.Source;
            files.ReplaceAll(built.Files);
            OnFilesReplaced(built.RepositoryRoot, scopeSnapshot is ChangeScope.Uncommitted or ChangeScope.All);
            if (scopeSnapshot == ChangeScope.Uncommitted)
            {
                Volatile.Write(ref uncommittedFingerprint, fingerprint);
            }

            var nextMessage = built.Message.IsEmpty
                ? files.Count == 0 ? new LocText(InspectorStrings.ChangesNoChanges) : default
                : built.Message;
            SetMessage(nextMessage);
            SelectedFile = files.FirstOrDefault(file => string.Equals(file.Path, previousSelection, StringComparison.Ordinal))
                ?? files.FirstOrDefault();
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(ScopeLabel));
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or GitException or UnauthorizedAccessException)
        {
            if (current == Volatile.Read(ref generation))
            {
                SetMessage(LocText.Literal(exception.Message));
            }
        }
        finally
        {
            if (current == Volatile.Read(ref generation))
            {
                IsLoading = false;
            }

            Interlocked.CompareExchange(ref loadSource, null, source);
            source.Dispose();
        }
    }

    private async Task ReloadTurnsAsync(CancellationToken ct)
    {
        if (!context.HasSession)
        {
            return;
        }

        var sessionId = context.SessionId;
        var stored = await BackgroundRead.RunAsync(token => checkpoints.ListAsync(sessionId, token), ct).ConfigureAwait(true);
        var wanted = stored.Where(static checkpoint => checkpoint.TreeAfter is not null).Reverse().ToArray();
        for (var index = Turns.Count - 1; index >= 0; index--)
        {
            if (wanted.All(checkpoint => checkpoint.Turn != Turns[index].Turn))
            {
                Turns.RemoveAt(index);
            }
        }

        for (var index = 0; index < wanted.Length; index++)
        {
            var checkpoint = wanted[index];
            if (Turns.Any(turn => turn.Turn == checkpoint.Turn))
            {
                continue;
            }

            Turns.Insert(index, new TurnChoiceViewModel(
                checkpoint.Turn,
                InspectorStrings.ChangeTurnLabel(checkpoint.Turn, checkpoint.StartedAt),
                checkpoint.TreeBefore,
                checkpoint.TreeAfter,
                SelectTurn,
                checkpoint.StartedAt));
        }

        OnPropertyChanged(nameof(HasNoTurns));
        if (selectedTurn is null || Turns.All(turn => turn.Turn != selectedTurn.Turn))
        {
            SetSelectedTurn(Turns.FirstOrDefault());
        }
    }

    private void SetMessage(LocText value)
    {
        message = value;
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(HasMessage));
    }

    /// <summary>
    /// Re-reads the document's own texts and hands the refresh down to the turn entries and diff rows: a row never
    /// subscribes to <see cref="Loc.LanguageChanged"/> itself, one strong reference per row would keep the document alive.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ScopeLabel));
        OnPropertyChanged(nameof(CompareBranchLabel));
        OnPropertyChanged(nameof(Message));
        foreach (var turn in Turns)
        {
            turn.RefreshText();
        }

        RefreshReviewTexts();
    }

    /// <summary>Drops the language subscription and cancels any in-flight diff; a gone document must not be refreshed later.</summary>
    public void Dispose()
    {
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        picker.PropertyChanged -= OnPickerPropertyChanged;
        Interlocked.Increment(ref generation);
        try
        {
            Interlocked.Exchange(ref loadSource, null)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed record BuiltChanges(
        List<FileDiffViewModel> Files, string RepositoryRoot, LocText Message, string? BaseCommit, DiffSource? Source);

    /// <summary>
    /// Hunk context of the changes view: only the changes and three lines around them are read. The unmodified stretches
    /// between hunks are "N unmodified lines" bars, loaded per file on demand (<see cref="LoadFullContextAsync"/>).
    /// </summary>
    private const int SummaryContextLines = 3;

    private sealed record DiffRead(IReadOnlyList<FileDiff> Diffs, string Prefix, LocText Message, string? BaseCommit, DiffSource? Source);

    /// <summary>The diff is read and mapped to view models on a pool thread; the caller applies it once.</summary>
    private async Task<BuiltChanges> BuildRowsAsync(
        string workingDirectory,
        ChangeScope selectedScope,
        TurnChoiceViewModel? turn,
        BranchChoiceViewModel? branch,
        CancellationToken ct)
    {
        // Only the changes are read up front; unmodified stretches stay unloaded until the user opens one.
        var read = await ReadDiffAsync(workingDirectory, selectedScope, turn, branch, SummaryContextLines, ct).ConfigureAwait(false);

        // The Markdown preview renders the whole new document, so modified Markdown files are read whole right away (one
        // git call for all of them). A whole read that overflows git's output cap leaves them as hunks (Source diff only).
        var whole = await ReadMarkdownWholeAsync(workingDirectory, read, ct).ConfigureAwait(false);

        var root = await git.GetRepositoryRootAsync(workingDirectory, ct).ConfigureAwait(false) ?? workingDirectory;
        var builtFiles = new List<FileDiffViewModel>(read.Diffs.Count);
        foreach (var diff in read.Diffs)
        {
            ct.ThrowIfCancellationRequested();
            builtFiles.Add(whole is not null && whole.TryGetValue(diff.Path, out var full)
                ? new FileDiffViewModel(full, read.Prefix, fullContext: true)
                : new FileDiffViewModel(diff, read.Prefix));
        }

        return new BuiltChanges(builtFiles, root, read.Message, read.BaseCommit, read.Source);
    }

    private async Task<Dictionary<string, FileDiff>?> ReadMarkdownWholeAsync(string workingDirectory, DiffRead read, CancellationToken ct)
    {
        if (read.Source is not { } source)
        {
            return null;
        }

        List<FileDiff>? wanted = null;
        foreach (var diff in read.Diffs)
        {
            if (diff.Status is FileDiffStatus.Modified or FileDiffStatus.Renamed or FileDiffStatus.Copied
                && !diff.IsBinary && diff.Hunks.Count > 0 && FileDiffViewModel.IsMarkdownPath(diff.Path))
            {
                (wanted ??= []).Add(diff);
            }
        }

        if (wanted is null)
        {
            return null;
        }

        try
        {
            var diffs = await ReadWholeAsync(source, wanted, ct).ConfigureAwait(false);
            var byPath = new Dictionary<string, FileDiff>(diffs.Count, StringComparer.Ordinal);
            foreach (var diff in diffs)
            {
                byPath[diff.Path] = diff;
            }

            return byPath;
        }
        catch (GitOutputTruncatedException)
        {
            return null;
        }
    }

    /// <summary>The whole-file diffs of <paramref name="files"/> under the same refs as the loaded set.</summary>
    private Task<IReadOnlyList<FileDiff>> ReadWholeAsync(DiffSource source, IReadOnlyList<FileDiff> files, CancellationToken ct)
    {
        var paths = new List<string>(files.Count * 2);
        foreach (var file in files)
        {
            // ":(top)" anchors at the repository root (diff paths are root-relative); a rename needs both sides for git to pair them.
            if (file.NewPath is { Length: > 0 } newPath)
            {
                paths.Add(":(top)" + newPath);
            }

            if (file.OldPath is { Length: > 0 } oldPath && !string.Equals(oldPath, file.NewPath, StringComparison.Ordinal))
            {
                paths.Add(":(top)" + oldPath);
            }
        }

        return git.GetDiffAsync(
            source.WorkingDirectory,
            new GitDiffOptions { From = source.From, To = source.To, Paths = paths, ContextLines = GitCli.FullContextLines },
            ct);
    }

    private async Task<DiffRead> ReadDiffAsync(
        string workingDirectory,
        ChangeScope selectedScope,
        TurnChoiceViewModel? turn,
        BranchChoiceViewModel? branch,
        int contextLines,
        CancellationToken ct)
    {
        if (!await git.IsRepositoryAsync(workingDirectory, ct).ConfigureAwait(false))
        {
            return new DiffRead([], string.Empty, default, null, null);
        }

        var root = await git.GetRepositoryRootAsync(workingDirectory, ct).ConfigureAwait(false);
        var relative = root is null ? "." : Path.GetRelativePath(root, workingDirectory).Replace(Path.DirectorySeparatorChar, '/');
        var prefix = string.Equals(relative, ".", StringComparison.Ordinal) ? string.Empty : relative + "/";

        if (selectedScope == ChangeScope.ByTurn)
        {
            if (turn is not { TreeAfter: { } after })
            {
                return new DiffRead([], prefix, default, null, null);
            }

            return new DiffRead(
                await git.GetTreeDiffAsync(workingDirectory, turn.TreeBefore, after, contextLines, ct).ConfigureAwait(false),
                prefix,
                default,
                null,
                new DiffSource(workingDirectory, turn.TreeBefore, after));
        }

        if (selectedScope == ChangeScope.Commit)
        {
            var commit = Volatile.Read(ref selectedCommit);
            return new DiffRead(
                await ReadCommitDiffAsync(workingDirectory, contextLines, ct).ConfigureAwait(false),
                prefix,
                default,
                null,
                commit is { ParentSha: { } parent } ? new DiffSource(workingDirectory, parent, commit.Sha) : null);
        }

        if (selectedScope == ChangeScope.All)
        {
            if (branch is null)
            {
                return new DiffRead([], prefix, new LocText(InspectorStrings.ChangesNoBranches), null, null);
            }

            if (await git.GetCurrentBranchAsync(workingDirectory, ct).ConfigureAwait(false) is null)
            {
                return new DiffRead([], prefix, new LocText(InspectorStrings.ChangesNoCurrentBranch), null, null);
            }

            var mergeBase = await git.GetMergeBaseAsync(workingDirectory, branch.RefName, "HEAD", ct).ConfigureAwait(false);
            if (mergeBase is null)
            {
                return new DiffRead([], prefix, new LocText(InspectorStrings.ChangesNoCommonHistory), null, null);
            }

            var branchDiffs = await git.GetDiffAsync(
                    workingDirectory,
                    new GitDiffOptions { From = mergeBase, To = "HEAD", Paths = ["."], ContextLines = contextLines },
                    ct)
                .ConfigureAwait(false);
            return new DiffRead(branchDiffs, prefix, default, mergeBase, new DiffSource(workingDirectory, mergeBase, "HEAD"));
        }

        var head = await git.RevParseAsync(workingDirectory, "HEAD", ct).ConfigureAwait(false);
        var diffs = await git.GetDiffAsync(
                workingDirectory,
                new GitDiffOptions
                {
                    From = head is null ? null : "HEAD",
                    IncludeUntracked = true,
                    Paths = ["."],
                    ContextLines = contextLines,
                },
                ct)
            .ConfigureAwait(false);
        return new DiffRead(diffs, prefix, default, head, new DiffSource(workingDirectory, head is null ? null : "HEAD", null));
    }
}

// ---------------------------------------------------------------- todo

/// <summary>
/// One row of the TODO panel: a loose item of the session queue, or a step of a group. The row shows the outcome of its
/// step commit below the title (the short sha once committed, or the failure reason with a "Retry commit" button), marks an
/// item the user queued with an outline user icon, and offers a delete button that only appears while the row is hovered or
/// keyboard focused. Nothing here writes the status: the agent's <c>todo</c> tool is the only writer, so the panel cannot
/// tick a row done and a tick never commits a step (the agent's own done does, inside Core).
/// </summary>
internal sealed class TodoRowViewModel : ObservableObject
{
    /// <summary>Length of the commit id shown on the row.</summary>
    internal const int ShortCommitLength = 7;

    private readonly TodoListViewModel owner;

    /// <param name="owner">The panel the row acts on (delete, retry).</param>
    /// <param name="item">The stored item the row mirrors; a refresh rebuilds the row from the store.</param>
    /// <param name="readOnly">True for a row of the panel's done section, which is a record and not an editor.</param>
    public TodoRowViewModel(TodoListViewModel owner, TodoItem item, bool readOnly = false)
    {
        this.owner = owner;
        Id = item.Id;
        GroupId = item.GroupId;
        Order = item.Order;
        Title = item.Title;
        Status = item.Status;
        Origin = item.Origin;
        IsReadOnly = readOnly;
        CommitId = string.IsNullOrEmpty(item.CommitId) ? null : item.CommitId;
        CommitError = string.IsNullOrWhiteSpace(item.CommitError) ? null : item.CommitError;
        RetryCommitCommand = new AsyncRelayCommand(
            ct => owner.RetryCommitAsync(Id, ct),
            () => HasCommitError,
            owner.ReportFailure);
        RemoveCommand = new RelayCommand(() => owner.Remove(Id));
    }

    public string Id { get; }

    /// <summary>The group this row is a step of, or null for a loose item of the session queue (both are a layer of their own).</summary>
    public string? GroupId { get; }

    /// <summary>Dense, zero-based position inside the row's own layer (the loose queue, or one group).</summary>
    public int Order { get; }

    public string Title { get; }

    public TodoStatus Status { get; }

    public TodoOrigin Origin { get; }

    public bool IsDone => Status == TodoStatus.Done;

    public bool IsPaused => Status == TodoStatus.Paused;

    public bool IsInProgress => Status == TodoStatus.InProgress;

    public bool IsPending => Status == TodoStatus.Pending;

    /// <summary>Only a not-started row may be dragged, and a drag only ever reorders it inside its own layer.</summary>
    public bool CanDrag => IsPending;

    /// <summary>True for an item the user queued in the panel, which shows the outline user mark at its right end.</summary>
    public bool IsUserAdded => Origin == TodoOrigin.User;

    /// <summary>True for a row of the done section: it has no delete button, the section is a record of what happened.</summary>
    public bool IsReadOnly { get; }

    /// <summary>The accessible name of the row's status icon ("not started", "in progress", "paused", "done").</summary>
    public string StatusName => Loc.Instance[StatusKey(Status)];

    /// <summary>The full commit id of the step, or null when the step has no commit.</summary>
    public string? CommitId { get; }

    /// <summary>The first <see cref="ShortCommitLength"/> characters of <see cref="CommitId"/>; empty when there is none.</summary>
    public string CommitShort => CommitId is null ? string.Empty : CommitId[..Math.Min(ShortCommitLength, CommitId.Length)];

    public bool HasCommitId => CommitId is not null;

    /// <summary>Why the step commit failed (git's message, English), or null.</summary>
    public string? CommitError { get; }

    public bool HasCommitError => CommitError is not null;

    /// <summary>The row's second line: the short sha of the step's commit, or the failure that has to be dealt with.</summary>
    public bool HasDetailLine => HasCommitId || HasCommitError;

    /// <summary>Commits the step again (the failure was a hook, a rebase in progress, a detached HEAD, ...).</summary>
    public AsyncRelayCommand RetryCommitCommand { get; }

    /// <summary>Deletes the row (a loose item or a step); the done section never shows it.</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>The table key of one status, shared by a row and the group it belongs to.</summary>
    internal static string StatusKey(TodoStatus status) => status switch
    {
        TodoStatus.InProgress => InspectorStrings.TodoStatusInProgress,
        TodoStatus.Paused => InspectorStrings.TodoStatusPaused,
        TodoStatus.Done => InspectorStrings.TodoStatusDone,
        _ => InspectorStrings.TodoStatusPending,
    };

    /// <summary>Re-reads the texts that follow the interface language (the panel calls this on a language switch).</summary>
    internal void RefreshTexts() => OnPropertyChanged(nameof(StatusName));
}

/// <summary>
/// One group of the session's plan: a title, its steps and the progress the steps add up to. The status is derived from the
/// steps and never stored - all done, one in progress, one paused, else pending (an empty group is pending). A group carries
/// no actions of its own: no delete button and no chevron while it is unfinished, because an unfinished plan stays open. A
/// finished group folds to a single row in the done section and opens again from there.
/// </summary>
internal sealed class TodoGroupViewModel : ObservableObject
{
    private bool isExpanded;

    /// <param name="owner">The panel its step rows act on.</param>
    /// <param name="group">The stored group the row mirrors.</param>
    /// <param name="steps">The group's steps, already in their own layer order.</param>
    /// <param name="readOnly">True when the group is finished and sits in the done section.</param>
    public TodoGroupViewModel(TodoListViewModel owner, TodoGroup group, IReadOnlyList<TodoItem> steps, bool readOnly)
    {
        Id = group.Id;
        Order = group.Order;
        Title = group.Title;
        foreach (var step in steps)
        {
            Entries.Add(new TodoRowViewModel(owner, step, readOnly));
        }

        Status = DeriveStatus(Entries);
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public string Id { get; }

    /// <summary>Dense, zero-based position across the session's groups.</summary>
    public int Order { get; }

    public string Title { get; }

    /// <summary>The group's steps, in their own layer order.</summary>
    public ObservableCollection<TodoRowViewModel> Entries { get; } = [];

    /// <summary>What the steps add up to when the group was built (a panel row never changes status on its own).</summary>
    public TodoStatus Status { get; }

    public bool IsDone => Status == TodoStatus.Done;

    public bool IsPaused => Status == TodoStatus.Paused;

    public bool IsInProgress => Status == TodoStatus.InProgress;

    public bool IsPending => Status == TodoStatus.Pending;

    public int TotalCount => Entries.Count;

    public int DoneCount => Entries.Count(static entry => entry.IsDone);

    /// <summary>"1/3": how many of the group's steps are done, out of all of them.</summary>
    public string Progress => InspectorStrings.TodoProgress(DoneCount, TotalCount);

    /// <summary>"4 个步骤" / "4 steps": the count a folded group shows in the done section.</summary>
    public string StepsText => InspectorStrings.TodoGroupSteps(TotalCount).Resolve();

    /// <summary>The accessible name of the group's status icon (its derived status).</summary>
    public string StatusName => Loc.Instance[TodoRowViewModel.StatusKey(Status)];

    /// <summary>"展开" / "收起": what the done section's fold row does next.</summary>
    public string ToggleName => Loc.Instance[IsExpanded ? InspectorStrings.TodoCollapse : InspectorStrings.TodoExpand];

    /// <summary>Open in the done section (a finished group is folded by default); an unfinished group is always open.</summary>
    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (Set(ref isExpanded, value))
            {
                OnPropertyChanged(nameof(ToggleName));
            }
        }
    }

    public RelayCommand ToggleCommand { get; }

    /// <summary>All done, else one in progress, else one paused, else pending; an empty group is pending.</summary>
    internal static TodoStatus DeriveStatus(IReadOnlyList<TodoRowViewModel> steps)
    {
        if (steps.Count == 0)
        {
            return TodoStatus.Pending;
        }

        if (steps.All(static step => step.IsDone))
        {
            return TodoStatus.Done;
        }

        if (steps.Any(static step => step.IsInProgress))
        {
            return TodoStatus.InProgress;
        }

        return steps.Any(static step => step.IsPaused) ? TodoStatus.Paused : TodoStatus.Pending;
    }

    /// <summary>Re-reads the texts that follow the interface language (the panel calls this on a language switch).</summary>
    internal void RefreshTexts()
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(StepsText));
        OnPropertyChanged(nameof(StatusName));
        OnPropertyChanged(nameof(ToggleName));
        foreach (var entry in Entries)
        {
            entry.RefreshTexts();
        }
    }
}

/// <summary>
/// The TODO panel of the active session: the pinned queue row on top, then the unfinished groups with their steps, the loose
/// queue, and last the done section (finished groups folded, then finished loose items). The agent's <c>todo</c> tool is the
/// only writer of the status; the panel queues an item, deletes a row, reorders a row inside its own layer and retries a
/// failed step commit. The status line is kept as a <see cref="LocText"/> and resolved when it is read, so a language switch
/// only has to re-raise it.
/// </summary>
internal sealed class TodoListViewModel : ObservableObject, IDisposable
{
    private readonly ITodoStore store;
    private readonly IStepCommitter? committer;
    private string sessionId = string.Empty;
    private string newTitle = string.Empty;
    private LocText statusText = new(InspectorStrings.TodoNoSession);
    private int generation;

    /// <param name="store">The checklist the panel reads and edits.</param>
    /// <param name="committer">
    /// Commits a step again when its row's "Retry commit" button runs; null (a unit test) means the panel only reads and
    /// edits the checklist. The agent's own updates commit inside Core, never through here.
    /// </param>
    public TodoListViewModel(ITodoStore store, IStepCommitter? committer = null)
    {
        this.store = store;
        this.committer = committer;
        AddCommand = new AsyncRelayCommand(
            ct => AddAsync(ct),
            () => sessionId.Length > 0 && !string.IsNullOrWhiteSpace(newTitle),
            exception => SetStatus(LocText.Literal(exception.Message)));
        store.Changed += OnStoreChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>The unfinished groups, in group order; each one carries its steps.</summary>
    public ObservableCollection<TodoGroupViewModel> Groups { get; } = [];

    /// <summary>The unfinished loose items of the session queue, in queue order.</summary>
    public ObservableCollection<TodoRowViewModel> Loose { get; } = [];

    /// <summary>The finished groups, in group order; each is folded to one row and opens on demand.</summary>
    public ObservableCollection<TodoGroupViewModel> DoneGroups { get; } = [];

    /// <summary>The finished loose items, in queue order.</summary>
    public ObservableCollection<TodoRowViewModel> DoneLoose { get; } = [];

    public AsyncRelayCommand AddCommand { get; }

    public string SessionId => sessionId;

    public string NewTitle
    {
        get => newTitle;
        set
        {
            if (Set(ref newTitle, value))
            {
                AddCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Why there is nothing to show (no session, an empty checklist, a store failure); empty while the panel has rows.</summary>
    public string StatusText => statusText.Resolve();

    /// <summary>Only "nothing to show" and failures get a line of their own; a panel with rows shows no summary at all.</summary>
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>True when the done section has anything in it.</summary>
    public bool HasDone => DoneGroups.Count > 0 || DoneLoose.Count > 0;

    /// <summary>True when the session has no group, no loose item and nothing done.</summary>
    public bool IsEmpty => Groups.Count == 0 && Loose.Count == 0 && !HasDone;

    public void Attach(string? newSessionId)
    {
        var next = newSessionId ?? string.Empty;
        if (string.Equals(sessionId, next, StringComparison.Ordinal))
        {
            return;
        }

        sessionId = next;
        ClearLists();
        AddCommand.NotifyCanExecuteChanged();
        _ = RefreshAsync(CancellationToken.None);
    }

    /// <summary>Reads the session's items and groups (one read each) and rebuilds the panel's four lists from them.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (sessionId.Length == 0)
        {
            SetStatus(new LocText(InspectorStrings.TodoNoSession));
            ClearLists();
            return;
        }

        var current = Interlocked.Increment(ref generation);
        try
        {
            var id = sessionId;
            var items = await BackgroundRead.RunAsync(token => store.ListAsync(id, token), ct).ConfigureAwait(true);
            var groups = await BackgroundRead.RunAsync(token => store.ListGroupsAsync(id, token), ct).ConfigureAwait(true);
            if (current != Volatile.Read(ref generation))
            {
                return;
            }

            Rebuild(items, groups);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            SetStatus(LocText.Literal(exception.Message));
        }
    }

    /// <summary>
    /// The "Retry commit" button: reads the item again (the agent may have changed its summary or committed meanwhile),
    /// and commits it if it is still done. Runs on the thread pool; the awaiting command stays disabled until it ends.
    /// </summary>
    internal Task RetryCommitAsync(string id, CancellationToken ct)
    {
        var owningSession = sessionId;
        if (committer is null || owningSession.Length == 0)
        {
            return Task.CompletedTask;
        }

        return BackgroundRead.RunAsync(
            async token =>
            {
                var items = await store.ListAsync(owningSession, token).ConfigureAwait(false);
                var item = items.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
                if (item is { Status: TodoStatus.Done })
                {
                    await committer.CommitStepAsync(item, token).ConfigureAwait(false);
                }
            },
            ct);
    }

    /// <summary>Shows an unexpected failure of a row command in the panel's status line.</summary>
    internal void ReportFailure(Exception exception) => SetStatus(LocText.Literal(exception.Message));

    /// <summary>Deletes a row (a loose item or a step) on the thread pool; a failure lands in the status line.</summary>
    internal void Remove(string id) =>
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await store.RemoveAsync(id, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => SetStatus(LocText.Literal(exception.Message)));
                }
            });

    /// <summary>
    /// Reorders a row inside its own layer (a drag inside the loose queue or inside one group); the store clamps the order to
    /// the layer. Runs on the thread pool; a failure lands in the status line.
    /// </summary>
    internal Task MoveAsync(string id, int order, CancellationToken ct) =>
        BackgroundRead.RunAsync(
            async token =>
            {
                try
                {
                    await store.MoveAsync(id, order, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => SetStatus(LocText.Literal(exception.Message)));
                }
            },
            ct);

    /// <summary>Queues a loose item for the agent; the agent promotes it into a group when it takes the work on.</summary>
    private async Task AddAsync(CancellationToken ct)
    {
        var title = newTitle.Trim();
        if (title.Length == 0 || sessionId.Length == 0)
        {
            return;
        }

        NewTitle = string.Empty;
        var id = sessionId;
        await BackgroundRead
            .RunAsync(token => store.AddAsync(new TodoAddRequest(id, title, Origin: TodoOrigin.User), token), ct)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Splits what the store returned into the panel's four lists. A step belongs to the group named by its
    /// <see cref="TodoItem.GroupId"/>; a loose item (no group) stands in the session queue. Finished groups (folded, in group
    /// order) come last, before the finished loose items.
    /// </summary>
    private void Rebuild(IReadOnlyList<TodoItem> items, IReadOnlyList<TodoGroup> groups)
    {
        ClearLists();

        foreach (var group in groups.OrderBy(static group => group.Order).ThenBy(static group => group.Id, StringComparer.Ordinal))
        {
            var steps = items
                .Where(item => string.Equals(item.GroupId, group.Id, StringComparison.Ordinal))
                .OrderBy(static item => item.Order)
                .ThenBy(static item => item.Id, StringComparer.Ordinal)
                .ToList();
            var finished = steps.Count > 0 && steps.All(static step => step.Status == TodoStatus.Done);
            var model = new TodoGroupViewModel(this, group, steps, readOnly: finished);
            (finished ? DoneGroups : Groups).Add(model);
        }

        foreach (var item in items
                     .Where(static item => item.GroupId is null)
                     .OrderBy(static item => item.Order)
                     .ThenBy(static item => item.Id, StringComparer.Ordinal))
        {
            var done = item.Status == TodoStatus.Done;
            (done ? DoneLoose : Loose).Add(new TodoRowViewModel(this, item, readOnly: done));
        }

        SetStatus(IsEmpty ? new LocText(InspectorStrings.TodoEmpty) : default);
    }

    private void ClearLists()
    {
        Groups.Clear();
        Loose.Clear();
        DoneGroups.Clear();
        DoneLoose.Clear();
        OnPropertyChanged(nameof(HasDone));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OnStoreChanged(string changedSessionId)
    {
        if (string.Equals(changedSessionId, sessionId, StringComparison.Ordinal))
        {
            Dispatcher.UIThread.Post(() => _ = RefreshAsync(CancellationToken.None));
        }
    }

    private void SetStatus(LocText value)
    {
        statusText = value;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
    }

    /// <summary>The status line and the rows' derived texts are resolved when they are read; a language switch re-reads them.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
        foreach (var group in Groups.Concat(DoneGroups))
        {
            group.RefreshTexts();
        }

        foreach (var row in Loose.Concat(DoneLoose))
        {
            row.RefreshTexts();
        }
    }

    /// <summary>Drops the language subscription; a panel that is gone must not be refreshed by the next language switch.</summary>
    public void Dispose() => Loc.Instance.LanguageChanged -= OnLanguageChanged;
}

// ---------------------------------------------------------------- review

/// <summary>
/// One review comment row. The line label and the status are formatted when they are read, so the row follows the language
/// without subscribing to <see cref="Loc.LanguageChanged"/> itself; the body is the reviewer's own text and is never
/// translated.
/// </summary>
internal sealed class ReviewEntryViewModel : ObservableObject
{
    private readonly int lineStart;
    private readonly int lineEnd;
    private readonly bool resolved;
    private readonly bool sent;

    public ReviewEntryViewModel(string path, int lineStart, int lineEnd, string body, bool resolved, bool sent)
    {
        Path = path;
        this.lineStart = lineStart;
        this.lineEnd = lineEnd;
        Body = body;
        this.resolved = resolved;
        this.sent = sent;
    }

    public string Path { get; }

    public string Line => InspectorStrings.ReviewLine(lineStart, lineEnd);

    public string Body { get; }

    public string Status => InspectorStrings.ReviewStatus(resolved, sent);

    /// <summary>Re-reads the two localized texts after a language switch; the panel calls this for every row.</summary>
    internal void RefreshText()
    {
        OnPropertyChanged(nameof(Line));
        OnPropertyChanged(nameof(Status));
    }
}

/// <summary>
/// Review comments of the active worktree, read-only for now; click opens the file in the changes document. The summary is
/// kept as a <see cref="LocText"/>; the rows are refreshed through the panel, never through the static event.
/// </summary>
internal sealed class ReviewPanelViewModel : ObservableObject, IDisposable
{
    private readonly ReviewStore store;
    private readonly Action<string> openChange;
    private PanelContext context = PanelContext.Empty;
    private LocText summary = new(InspectorStrings.ReviewNoSession);

    public ReviewPanelViewModel(ReviewStore store, Action<string> openChange)
    {
        this.store = store;
        this.openChange = openChange;
        RefreshCommand = new AsyncRelayCommand(
            ct => RefreshAsync(ct),
            () => true,
            exception => SetSummary(LocText.Literal(exception.Message)));
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<ReviewEntryViewModel> Items { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>"3 条意见" / "3 comments", or why there is nothing to show, in the language of the moment it is read.</summary>
    public string Summary => summary.Resolve();

    public void Attach(PanelContext panelContext)
    {
        var changed = !string.Equals(context.SessionId, panelContext.SessionId, StringComparison.Ordinal);
        context = panelContext;
        if (changed)
        {
            _ = RefreshAsync(CancellationToken.None);
        }
    }

    public void Open(ReviewEntryViewModel entry) => openChange(entry.Path);

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!context.HasSession)
        {
            Items.Clear();
            SetSummary(new LocText(InspectorStrings.ReviewNoSession));
            return;
        }

        try
        {
            var workingDirectory = context.WorkingDirectory;
            var comments = await BackgroundRead.RunAsync(
                    token => store.QueryAsync(new ReviewQuery { WorkspacePath = workingDirectory }, token),
                    ct)
                .ConfigureAwait(true);
            Items.Clear();
            foreach (var comment in comments)
            {
                Items.Add(new ReviewEntryViewModel(
                    comment.FilePath,
                    comment.NewLineStart,
                    comment.NewLineEnd,
                    comment.Body,
                    comment.IsResolved,
                    comment.Status == ReviewCommentStatus.Sent));
            }

            SetSummary(InspectorStrings.ReviewSummary(Items.Count));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException)
        {
            SetSummary(LocText.Literal(exception.Message));
        }
    }

    private void SetSummary(LocText value)
    {
        summary = value;
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>
    /// Re-reads the summary and hands the refresh down to every comment row: a row never subscribes to
    /// <see cref="Loc.LanguageChanged"/> itself, one strong reference per row would keep the panel alive.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Summary));
        foreach (var item in Items)
        {
            item.RefreshText();
        }
    }

    /// <summary>Drops the language subscription; a panel that is gone must not be refreshed by the next language switch.</summary>
    public void Dispose() => Loc.Instance.LanguageChanged -= OnLanguageChanged;
}

// ---------------------------------------------------------------- spec tree

/// <summary>One spec of the graph, shown as part of the flat, virtualized row list of the spec tree.</summary>
internal sealed class SpecNodeViewModel : ObservableObject, IFlatTreeNode<SpecNodeViewModel>
{
    private readonly Action<SpecNodeViewModel> expansionChanged;
    private readonly List<SpecNodeViewModel> children = [];
    private SpecNodeViewModel? parent;
    private int guideMask;
    private bool isExpanded;

    public SpecNodeViewModel(int depth, bool isExpanded, Action<SpecNodeViewModel> expansionChanged)
    {
        Depth = depth;
        this.isExpanded = isExpanded;
        this.expansionChanged = expansionChanged;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        OpenCommand = new RelayCommand(Open);
    }

    public required string Title { get; init; }

    public required string Status { get; init; }

    public required string FullPath { get; init; }

    public required string Id { get; init; }

    public int Depth { get; private set; }

    /// <summary>The guide lines of this row: one step per expanded ancestor, aligned to that ancestor's chevron.</summary>
    public IReadOnlyList<IndentMark> Guides => IndentMarks.ForMask(guideMask);

    /// <summary>Documents use the file shape; a node with children uses the closed/open folder shape.</summary>
    public Geometry Icon => children.Count > 0
        ? (isExpanded ? TreeIcons.FolderOpen : TreeIcons.Folder)
        : TreeIcons.File;

    public IReadOnlyList<SpecNodeViewModel> Children => children;

    public bool HasToggle => children.Count > 0;

    public bool HasStatus => Status.Length > 0;

    public RelayCommand ToggleCommand { get; }

    public RelayCommand OpenCommand { get; }

    /// <summary>Chevron points right when collapsed, down when expanded.</summary>
    public bool ChevronRotated => isExpanded;

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (!Set(ref isExpanded, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ChevronRotated));
            OnPropertyChanged(nameof(Icon));
            foreach (var child in children)
            {
                child.RefreshGuideMask();
            }

            expansionChanged(this);
        }
    }

    public Action<string>? OpenFile { get; init; }

    public void Add(SpecNodeViewModel child)
    {
        children.Add(child);
        child.parent = this;
    }

    /// <summary>Sets the row's depth during the top-down build; the guide mask is derived from the parent, which is
    /// already assigned because the build walks parents first.</summary>
    public void SetDepth(int depth)
    {
        Depth = depth;
        guideMask = GuideMaskOf(parent);
        OnPropertyChanged(nameof(Guides));
    }

    private static int GuideMaskOf(SpecNodeViewModel? parent) =>
        parent is null
            ? 0
            : parent.guideMask | (parent.isExpanded && parent.Depth < 31 ? 1 << parent.Depth : 0);

    /// <summary>Recomputes this node's guide mask after an ancestor expanded or collapsed, then its subtree.</summary>
    private void RefreshGuideMask()
    {
        var next = GuideMaskOf(parent);
        if (next == guideMask)
        {
            return;
        }

        guideMask = next;
        OnPropertyChanged(nameof(Guides));
        foreach (var child in children)
        {
            child.RefreshGuideMask();
        }
    }

    private void Open() => OpenFile?.Invoke(FullPath);
}

/// <summary>
/// The spec tree of the active project root, following the declared parent hierarchy. The graph is read and the row list
/// is built on a pool thread; the panel keeps a virtualized row list and remembers which nodes the user collapsed. The
/// rows show the graph's own titles and status words, so only the summary is language-dependent and it is kept as a
/// <see cref="LocText"/>.
/// </summary>
internal sealed class SpecTreeViewModel : ObservableObject, IDisposable
{
    private readonly Func<string, SpecGraph> graphFactory;
    private readonly Action<string> openFile;
    private readonly FlatTree<SpecNodeViewModel> tree = new();
    private readonly HashSet<string> collapsedIds = new(StringComparer.Ordinal);
    private PanelContext context = PanelContext.Empty;
    private LocText summary = new(InspectorStrings.SpecNoProject);

    public SpecTreeViewModel(Func<string, SpecGraph> graphFactory, Action<string> openFile)
    {
        this.graphFactory = graphFactory;
        this.openFile = openFile;
        RefreshCommand = new AsyncRelayCommand(
            ct => RefreshAsync(ct),
            () => Context.ProjectRoot is not null,
            exception => SetSummary(LocText.Literal(exception.Message)));
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>The visible rows of the tree, in display order; the view virtualizes this one list.</summary>
    public RangeObservableCollection<SpecNodeViewModel> Rows => tree.Rows;

    public AsyncRelayCommand RefreshCommand { get; }

    public PanelContext Context => context;

    /// <summary>"12 份规格" / "12 specs", or why there is no tree, in the language of the moment it is read.</summary>
    public string Summary => summary.Resolve();

    public void Attach(PanelContext panelContext)
    {
        var changed = !string.Equals(context.ProjectRoot, panelContext.ProjectRoot, StringComparison.OrdinalIgnoreCase);
        context = panelContext;
        OnPropertyChanged(nameof(Context));
        if (changed)
        {
            collapsedIds.Clear();
            tree.Clear();
            _ = RefreshAsync(CancellationToken.None);
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (context.ProjectRoot is not { } root || !Directory.Exists(root))
        {
            tree.Clear();
            SetSummary(new LocText(InspectorStrings.SpecNoProject));
            return;
        }

        var collapsed = new HashSet<string>(collapsedIds, StringComparer.Ordinal);
        try
        {
            var built = await Task.Run(() => BuildAsync(root, collapsed, ct), ct).ConfigureAwait(true);
            tree.Replace(built.Rows);
            SetSummary(InspectorStrings.SpecCount(built.Count));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            SetSummary(LocText.Literal(exception.Message));
        }
    }

    private void SetSummary(LocText value)
    {
        summary = value;
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>The rows carry the graph's own titles; only the summary is resolved from the string table.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(Summary));

    /// <summary>Drops the language subscription; a panel that is gone must not be refreshed by the next language switch.</summary>
    public void Dispose() => Loc.Instance.LanguageChanged -= OnLanguageChanged;

    /// <summary>Reads the graph, maps it to node view models and flattens the visible rows, all on one pool thread.</summary>
    private async Task<(List<SpecNodeViewModel> Rows, int Count)> BuildAsync(
        string root,
        HashSet<string> collapsed,
        CancellationToken ct)
    {
        var graph = graphFactory(root);
        var nodes = await graph.ListAsync(ct).ConfigureAwait(false);
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            ct.ThrowIfCancellationRequested();
            var view = await graph.GetAsync(node.Id, ct).ConfigureAwait(false);
            parents[node.Id] = view?.Frontmatter.Parent;
        }

        var byId = new Dictionary<string, SpecNodeViewModel>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            byId[node.Id] = new SpecNodeViewModel(0, !collapsed.Contains(node.Id), OnExpansionChanged)
            {
                Id = node.Id,
                Title = node.Title ?? node.Id,
                Status = node.Status ?? string.Empty,
                FullPath = Path.Combine(root, node.Path.Replace('/', Path.DirectorySeparatorChar)),
                OpenFile = openFile,
            };
        }

        var roots = new List<SpecNodeViewModel>();
        foreach (var node in nodes)
        {
            var parent = parents[node.Id];
            if (parent is not null && byId.TryGetValue(parent, out var parentNode))
            {
                parentNode.Add(byId[node.Id]);
            }
            else
            {
                roots.Add(byId[node.Id]);
            }
        }

        foreach (var node in roots)
        {
            AssignDepth(node, 0);
        }

        return (FlatTree<SpecNodeViewModel>.Flatten(roots), nodes.Count);
    }

    private static void AssignDepth(SpecNodeViewModel node, int depth)
    {
        node.SetDepth(depth);
        foreach (var child in node.Children)
        {
            AssignDepth(child, depth + 1);
        }
    }

    private void OnExpansionChanged(SpecNodeViewModel node)
    {
        if (node.IsExpanded)
        {
            collapsedIds.Remove(node.Id);
        }
        else
        {
            collapsedIds.Add(node.Id);
        }

        tree.ExpansionChanged(node);
    }
}
