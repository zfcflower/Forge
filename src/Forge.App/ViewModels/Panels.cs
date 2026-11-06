using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Services;
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
    private readonly FlatTree<FileNodeViewModel> tree = new();
    private readonly HashSet<string> expandedPaths = new(StringComparer.OrdinalIgnoreCase);
    private FileIndex? index;
    private FileSystemWatcher? watcher;
    private DispatcherTimer? watcherTimer;
    private CountsSnapshot? countsSnapshot;
    private readonly Func<string, CancellationToken, Task<Dictionary<string, LineCount>>>? lineCountsFactory;
    private LocText error;
    private bool isLoading;
    private int selectedTab;
    private int refreshGeneration;

    public FilesPanelViewModel(GitCli git, Action<string> openFile, Action<string> openFileInNewTab, Action<string> openChanges)
        : this(git, openFile, openFileInNewTab, openChanges, lineCountsFactory: null)
    {
    }

    internal FilesPanelViewModel(
        GitCli git,
        Action<string> openFile,
        Action<string> openFileInNewTab,
        Action<string> openChanges,
        Func<string, CancellationToken, Task<Dictionary<string, LineCount>>>? lineCountsFactory)
    {
        this.git = git;
        this.openFile = openFile;
        this.openFileInNewTab = openFileInNewTab;
        this.openChanges = openChanges;
        this.lineCountsFactory = lineCountsFactory;
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
        : this(git, openFile, openFileInNewTab, openChanges, lineCountsFactory)
    {
        Context = context;
    }

    /// <summary>The last atomically published fingerprint/result pair; exposed for the cache race tests.</summary>
    internal CountsSnapshot? CountsSnapshotForTests => Volatile.Read(ref countsSnapshot);

    /// <summary>The visible rows of the "全部" tree, in display order; the view virtualizes this one list.</summary>
    public RangeObservableCollection<FileNodeViewModel> Rows => tree.Rows;

    public ObservableCollection<ChangedFileViewModel> Changes { get; } = [];

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
            }
        }
    }

    public bool IsAllTab => selectedTab == 0;

    public bool IsChangedTab => selectedTab == 1;

    public bool IsLoading
    {
        get => isLoading;
        private set => Set(ref isLoading, value);
    }

    public string Summary => Context.HasSession
        ? InspectorStrings.FileSummary(Changes.Count, index?.FileCount ?? 0)
        : Loc.Instance[InspectorStrings.FilesNoSession];

    /// <summary>Why the last refresh failed, in the language of the moment it is read; null when it did not fail.</summary>
    public string? Error => error.IsEmpty ? null : error.Resolve();

    public void Attach(PanelContext context)
    {
        var changed = !string.Equals(Context.SessionId, context.SessionId, StringComparison.Ordinal);
        Context = context;
        OnPropertyChanged(nameof(Context));
        OnPropertyChanged(nameof(Summary));
        if (changed)
        {
            expandedPaths.Clear();
            tree.Clear();
            Changes.Clear();
            index = null;
            Volatile.Write(ref countsSnapshot, null);
        }

        RestartWatcher();
        _ = RefreshAsync(CancellationToken.None);
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var context = Context;
        if (!context.HasSession || !Directory.Exists(context.WorkingDirectory))
        {
            tree.Clear();
            Changes.Clear();
            index = null;
            OnPropertyChanged(nameof(Summary));
            return;
        }

        var generation = Interlocked.Increment(ref refreshGeneration);
        IsLoading = true;
        try
        {
            var workingDirectory = context.WorkingDirectory;
            var expanded = new HashSet<string>(expandedPaths, StringComparer.OrdinalIgnoreCase);
            var built = await Task.Run(() => BuildTreeAsync(workingDirectory, expanded, ct), ct).ConfigureAwait(true);
            var status = await Task.Run(() => ReadStatusAsync(workingDirectory, ct), ct).ConfigureAwait(true);
            if (generation != Volatile.Read(ref refreshGeneration))
            {
                return;
            }

            index = built.Index;
            tree.Replace(built.Rows);

            Changes.Clear();
            foreach (var change in status)
            {
                Changes.Add(change);
            }

            error = default;
            OnPropertyChanged(nameof(Summary));
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

        _ = RefreshAsync(CancellationToken.None);
    }

    private void OnFailed(Exception exception) => error = LocText.Literal(exception.Message);

    /// <summary>
    /// Re-reads the panel's own texts and hands the refresh down to every changed-file row: a row never subscribes to
    /// <see cref="Loc.LanguageChanged"/> itself, one strong reference per row would keep the whole panel alive.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Error));
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

internal sealed record FileLineViewModel(int Number, string Text);

/// <summary>
/// Read-only file view: one entry per line with its number, virtualized in the view. The file's content and path are data
/// and are never translated; only the tab title's leading mark and the placeholder before a file is loaded come from the
/// string table, and both are read when the property is read.
/// </summary>
internal sealed class FileDocumentViewModel : ObservableObject
{
    private const int MaxLines = 20000;

    private string path = string.Empty;
    private string? fileName;
    private LocText error;
    private bool isLoading;

    private FileDocumentViewModel()
    {
        LineItems = new ReadOnlyObservableCollection<FileLineViewModel>(Lines);
        LoadCommand = new AsyncRelayCommand(
            ct => LoadAsync(path, ct),
            () => path.Length > 0,
            exception => SetError(LocText.Literal(exception.Message)));
    }

    public ObservableCollection<FileLineViewModel> Lines { get; } = [];

    public ReadOnlyObservableCollection<FileLineViewModel> LineItems { get; }

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

    /// <summary>The mark followed by the file name, or the generic word until a file is loaded.</summary>
    public string TabTitle => InspectorStrings.FileTabTitle(fileName ?? Loc.Instance[InspectorStrings.FilesName]);

    public bool IsLoading
    {
        get => isLoading;
        private set => Set(ref isLoading, value);
    }

    /// <summary>The read problem (missing file, too large, truncated, an OS error), in the language of the moment it is read.</summary>
    public string? Error => error.IsEmpty ? null : error.Resolve();

    public bool HasError => !error.IsEmpty;

    public static FileDocumentViewModel Create() => new();

    /// <summary>Replaces this fixed document's content with another file.</summary>
    public void Reload(FileDocumentViewModel other)
    {
        Path = other.Path;
        fileName = other.fileName;
        OnPropertyChanged(nameof(TabTitle));
        Lines.Clear();
        foreach (var line in other.Lines)
        {
            Lines.Add(line);
        }

        SetError(other.error);
    }

    public async Task LoadAsync(string filePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        Path = System.IO.Path.GetFullPath(filePath);
        fileName = System.IO.Path.GetFileName(Path);
        OnPropertyChanged(nameof(TabTitle));
        Lines.Clear();
        SetError(default);
        IsLoading = true;
        try
        {
            var lines = await Task.Run(
                    async () =>
                    {
                        if (!File.Exists(filePath))
                        {
                            return null;
                        }

                        var info = new FileInfo(filePath);
                        if (info.Length > 8 * 1024 * 1024)
                        {
                            // Shown as the document's only line, so it is resolved here, on the pool thread; Loc reads
                            // are thread-safe.
                            return new[] { InspectorStrings.FileTooLarge(info.Length).Resolve() };
                        }

                        var read = await File.ReadAllLinesAsync(filePath, ct).ConfigureAwait(false);
                        return read.Length > MaxLines ? read[..MaxLines] : read;
                    },
                    ct)
                .ConfigureAwait(true);

            if (lines is null)
            {
                SetError(InspectorStrings.FileMissing(filePath));
                return;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                Lines.Add(new FileLineViewModel(i + 1, lines[i].Replace("\t", "    ", StringComparison.Ordinal)));
            }

            if (lines.Length == MaxLines)
            {
                SetError(InspectorStrings.LinesTruncated(MaxLines));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetError(LocText.Literal(exception.Message));
        }
        finally
        {
            IsLoading = false;
        }
    }

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
    ByTurn,
    Uncommitted,
}

/// <summary>
/// One line of a file diff. A real line's text is content and stays a literal; the placeholder that stands in for a change
/// with no text is a <see cref="Loc"/> key, so it is resolved when it is shown.
/// </summary>
internal sealed class DiffLineViewModel
{
    private readonly LocText text;

    public DiffLineViewModel(string prefix, LocText text, string kind, string oldNumber, string newNumber)
    {
        Prefix = prefix;
        this.text = text;
        Kind = kind;
        OldNumber = oldNumber;
        NewNumber = newNumber;
    }

    public string Prefix { get; }

    public string Text => text.Resolve();

    public string Kind { get; }

    public string OldNumber { get; }

    public string NewNumber { get; }

    public bool IsAdd => Kind == "add";

    public bool IsRemove => Kind == "del";

    public bool IsHunk => Kind == "hunk";
}

/// <summary>One row of the changes document: a file header or one diff line, in the order they are stacked.</summary>
internal abstract class DiffRowViewModel(FileDiffViewModel file) : ObservableObject
{
    public FileDiffViewModel File { get; } = file;

    /// <summary>Re-reads the row's language-dependent text after a language switch; the panel calls this for every row.</summary>
    public virtual void RefreshText()
    {
    }
}

internal sealed class DiffFileRowViewModel(FileDiffViewModel file) : DiffRowViewModel(file)
{
    public string Header => File.Header;

    public string Badge => File.Badge;

    public bool IsHeader => true;

    public override void RefreshText() => OnPropertyChanged(nameof(Header));
}

internal sealed class DiffLineRowViewModel(FileDiffViewModel file, DiffLineViewModel line) : DiffRowViewModel(file)
{
    public DiffLineViewModel Line { get; } = line;

    public string Prefix => Line.Prefix;

    public string Text => Line.Text;

    public string OldNumber => Line.OldNumber;

    public string NewNumber => Line.NewNumber;

    public bool IsAdd => Line.IsAdd;

    public bool IsRemove => Line.IsRemove;

    public bool IsHunk => Line.IsHunk;

    public bool IsHeader => false;

    public override void RefreshText() => OnPropertyChanged(nameof(Text));
}

internal sealed class FileDiffViewModel
{
    private readonly int added;
    private readonly int removed;

    public FileDiffViewModel(FileDiff diff, string prefix = "")
    {
        Path = Strip(diff, prefix);
        added = diff.AddedLineCount;
        removed = diff.RemovedLineCount;
        Badge = diff.Status switch
        {
            FileDiffStatus.Added => "A",
            FileDiffStatus.Deleted => "D",
            FileDiffStatus.Renamed => "R",
            FileDiffStatus.Copied => "C",
            _ => "M",
        };
        var lines = new List<DiffLineViewModel>();
        foreach (var hunk in diff.Hunks)
        {
            lines.Add(new DiffLineViewModel(string.Empty, LocText.Literal(hunk.Header), "hunk", string.Empty, string.Empty));
            foreach (var line in hunk.Lines)
            {
                var kind = line.Kind switch
                {
                    DiffLineKind.Added => "add",
                    DiffLineKind.Removed => "del",
                    _ => "ctx",
                };
                var mark = line.Kind switch
                {
                    DiffLineKind.Added => "+",
                    DiffLineKind.Removed => "−",
                    _ => " ",
                };
                lines.Add(new DiffLineViewModel(
                    mark,
                    LocText.Literal(line.Text.Replace("\t", "    ", StringComparison.Ordinal)),
                    kind,
                    line.OldLineNumber > 0 ? TextFormat.Id(line.OldLineNumber) : string.Empty,
                    line.NewLineNumber > 0 ? TextFormat.Id(line.NewLineNumber) : string.Empty));
            }
        }

        if (lines.Count == 0)
        {
            lines.Add(new DiffLineViewModel(" ", new LocText(InspectorStrings.ChangesNoTextualChange), "ctx", string.Empty, string.Empty));
        }

        Lines = lines;
    }

    public string Path { get; }

    public string Header => InspectorStrings.FileDiffHeader(Path, added, removed);

    public string Badge { get; }

    public IReadOnlyList<DiffLineViewModel> Lines { get; }

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

    public TurnChoiceViewModel(int turn, LocText label, string treeBefore, string? treeAfter)
    {
        Turn = turn;
        this.label = label;
        TreeBefore = treeBefore;
        TreeAfter = treeAfter;
    }

    public int Turn { get; }

    /// <summary>The turn number and its start time, in the language of the moment the entry is read.</summary>
    public string Label => label.Resolve();

    public string TreeBefore { get; }

    public string? TreeAfter { get; }

    /// <summary>Re-reads the label after a language switch; the panel calls this for every entry.</summary>
    internal void RefreshText() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// The fixed "改动" document. Scope "按轮次" diffs the two trees the workspace layer recorded around one main-session turn
/// (<see cref="GitCli.GetTreeDiffAsync"/> over the hashes in <c>turn_checkpoints</c>); "未提交改动" diffs the working tree
/// against HEAD including untracked files. All files are stacked into one flat row list that the view virtualizes;
/// selecting one scrolls the view to it.
/// </summary>
internal sealed class ChangesViewModel : ObservableObject, IDisposable
{
    private readonly GitCli git;
    private readonly ITurnCheckpointStore checkpoints;
    private readonly List<FileDiffViewModel> files = [];
    private PanelContext context = PanelContext.Empty;
    private ChangeScope scope = ChangeScope.Uncommitted;
    private TurnChoiceViewModel? selectedTurn;
    private FileDiffViewModel? selectedFile;
    private bool isLoading;
    private LocText message;
    private int generation;

    public ChangesViewModel(GitCli git, ITurnCheckpointStore checkpoints)
    {
        this.git = git;
        this.checkpoints = checkpoints;
        RefreshCommand = new AsyncRelayCommand(
            ct => LoadAsync(ct),
            () => true,
            exception => SetMessage(LocText.Literal(exception.Message)));
        ByTurnCommand = new RelayCommand(() => ScopeIndex = 0);
        UncommittedCommand = new RelayCommand(() => ScopeIndex = 1);
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<TurnChoiceViewModel> Turns { get; } = [];

    /// <summary>Every header and diff line of the current scope, in display order; the view virtualizes this one list.</summary>
    public RangeObservableCollection<DiffRowViewModel> Rows { get; } = [];

    public IReadOnlyList<FileDiffViewModel> Files => files;

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand ByTurnCommand { get; }

    public RelayCommand UncommittedCommand { get; }

    public PanelContext Context => context;

    public int ScopeIndex
    {
        get => (int)scope;
        set
        {
            var next = value == 1 ? ChangeScope.Uncommitted : ChangeScope.ByTurn;
            if (scope == next)
            {
                return;
            }

            scope = next;
            OnPropertyChanged(nameof(IsByTurn));
            OnPropertyChanged(nameof(IsUncommitted));
            _ = LoadAsync(CancellationToken.None);
        }
    }

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

            if (Set(ref selectedTurn, value))
            {
                _ = LoadAsync(CancellationToken.None);
            }
        }
    }

    public FileDiffViewModel? SelectedFile
    {
        get => selectedFile;
        set => Set(ref selectedFile, value);
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
        ? InspectorStrings.ChangesSummary(files.Count, IsByTurn)
        : Loc.Instance[InspectorStrings.ChangesNoSession];

    public event Action<FileDiffViewModel>? FocusRequested;

    public void Attach(PanelContext panelContext)
    {
        var changed = !string.Equals(context.SessionId, panelContext.SessionId, StringComparison.Ordinal);
        context = panelContext;
        OnPropertyChanged(nameof(Context));
        if (changed)
        {
            files.Clear();
            Rows.ReplaceAll(Array.Empty<DiffRowViewModel>());
            Turns.Clear();
            selectedTurn = null;
        }

        _ = LoadAsync(CancellationToken.None);
    }

    /// <summary>Opens (and scrolls to) one file of the current change set.</summary>
    public async Task ShowFileAsync(string path, CancellationToken ct)
    {
        await LoadAsync(ct).ConfigureAwait(true);
        var match = files.FirstOrDefault(
            file => string.Equals(file.Path.Replace('\\', '/'), path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            SelectedFile = match;
            FocusRequested?.Invoke(match);
        }
    }

    /// <summary>Row index of a file header, for scrolling the virtualized list to that file.</summary>
    public int IndexOfFile(FileDiffViewModel file)
    {
        for (var index = 0; index < Rows.Count; index++)
        {
            if (ReferenceEquals(Rows[index].File, file))
            {
                return index;
            }
        }

        return -1;
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        if (!context.HasSession || !Directory.Exists(context.WorkingDirectory))
        {
            files.Clear();
            Rows.ReplaceAll(Array.Empty<DiffRowViewModel>());
            Turns.Clear();
            OnPropertyChanged(nameof(Summary));
            return;
        }

        var current = Interlocked.Increment(ref generation);
        IsLoading = true;
        try
        {
            var workingDirectory = context.WorkingDirectory;
            await ReloadTurnsAsync(ct).ConfigureAwait(true);

            var byTurn = IsByTurn;
            var turn = selectedTurn;
            var built = await Task.Run(() => BuildRowsAsync(workingDirectory, byTurn, turn, ct), ct).ConfigureAwait(true);
            if (current != Volatile.Read(ref generation))
            {
                return;
            }

            files.Clear();
            files.AddRange(built.Files);
            Rows.ReplaceAll(built.Rows);

            SetMessage(files.Count == 0 ? new LocText(InspectorStrings.ChangesNoChanges) : default);
            SelectedFile = files.FirstOrDefault();
            OnPropertyChanged(nameof(Summary));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or GitException or UnauthorizedAccessException)
        {
            SetMessage(LocText.Literal(exception.Message));
        }
        finally
        {
            if (current == Volatile.Read(ref generation))
            {
                IsLoading = false;
            }
        }
    }

    private async Task ReloadTurnsAsync(CancellationToken ct)
    {
        if (!IsByTurn || !context.HasSession)
        {
            return;
        }

        IReadOnlyList<TurnCheckpoint> stored;
        try
        {
            var sessionId = context.SessionId;
            stored = await BackgroundRead.RunAsync(token => checkpoints.ListAsync(sessionId, token), ct).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            SetMessage(LocText.Literal(exception.Message));
            return;
        }

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
                checkpoint.TreeAfter));
        }

        if (selectedTurn is null || Turns.All(turn => turn.Turn != selectedTurn.Turn))
        {
            selectedTurn = Turns.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedTurn));
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
        OnPropertyChanged(nameof(Message));
        foreach (var turn in Turns)
        {
            turn.RefreshText();
        }

        foreach (var row in Rows)
        {
            row.RefreshText();
        }
    }

    /// <summary>Drops the language subscription; a document that is gone must not be refreshed by the next language switch.</summary>
    public void Dispose() => Loc.Instance.LanguageChanged -= OnLanguageChanged;

    /// <summary>The diff is read, mapped to view models and flattened on a pool thread; the caller applies it once.</summary>
    private async Task<(List<FileDiffViewModel> Files, List<DiffRowViewModel> Rows)> BuildRowsAsync(
        string workingDirectory,
        bool byTurn,
        TurnChoiceViewModel? turn,
        CancellationToken ct)
    {
        var (diffs, prefix) = await ReadDiffAsync(workingDirectory, byTurn, turn, ct).ConfigureAwait(false);
        var builtFiles = new List<FileDiffViewModel>(diffs.Count);
        var rows = new List<DiffRowViewModel>();
        foreach (var diff in diffs)
        {
            var file = new FileDiffViewModel(diff, prefix);
            builtFiles.Add(file);
            rows.Add(new DiffFileRowViewModel(file));
            foreach (var line in file.Lines)
            {
                rows.Add(new DiffLineRowViewModel(file, line));
            }
        }

        return (builtFiles, rows);
    }

    private async Task<(IReadOnlyList<FileDiff> Diffs, string Prefix)> ReadDiffAsync(
        string workingDirectory,
        bool byTurn,
        TurnChoiceViewModel? turn,
        CancellationToken ct)
    {
        if (!await git.IsRepositoryAsync(workingDirectory, ct).ConfigureAwait(false))
        {
            return ([], string.Empty);
        }

        var root = await git.GetRepositoryRootAsync(workingDirectory, ct).ConfigureAwait(false);
        var relative = root is null ? "." : Path.GetRelativePath(root, workingDirectory).Replace(Path.DirectorySeparatorChar, '/');
        var prefix = string.Equals(relative, ".", StringComparison.Ordinal) ? string.Empty : relative + "/";

        if (byTurn)
        {
            if (turn is not { TreeAfter: { } after })
            {
                return ([], prefix);
            }

            return (await git.GetTreeDiffAsync(workingDirectory, turn.TreeBefore, after, ct).ConfigureAwait(false), prefix);
        }

        var head = await git.RevParseAsync(workingDirectory, "HEAD", ct).ConfigureAwait(false);
        var diffs = await git.GetDiffAsync(
                workingDirectory,
                new GitDiffOptions
                {
                    From = head is null ? null : "HEAD",
                    IncludeUntracked = true,
                    Paths = ["."],
                },
                ct)
            .ConfigureAwait(false);
        return (diffs, prefix);
    }
}

// ---------------------------------------------------------------- todo

/// <summary>
/// One TODO row. Besides the title and the done box it shows the outcome of the step commit: the short commit id once the
/// step was committed, or the failure reason with a "Retry commit" button. The commit fields come from the stored item and
/// are never edited here; only a user action in this row (ticking the box, retrying) asks the panel to commit.
/// </summary>
internal sealed class TodoEntryViewModel : ObservableObject
{
    /// <summary>Length of the commit id shown on the row.</summary>
    internal const int ShortCommitLength = 7;

    private readonly TodoListViewModel owner;
    private bool isDone;
    private string title;

    public TodoEntryViewModel(TodoListViewModel owner, TodoItem item)
    {
        this.owner = owner;
        Id = item.Id;
        title = item.Title;
        isDone = item.Status == TodoStatus.Done;
        Note = item.Note;
        Order = item.Order;
        CommitId = string.IsNullOrEmpty(item.CommitId) ? null : item.CommitId;
        CommitError = string.IsNullOrWhiteSpace(item.CommitError) ? null : item.CommitError;
        RetryCommitCommand = new AsyncRelayCommand(
            ct => owner.RetryCommitAsync(Id, ct),
            () => HasCommitError,
            owner.ReportFailure);
    }

    /// <summary>The full commit id of the step, or null when the step has no commit.</summary>
    public string? CommitId { get; }

    /// <summary>The first <see cref="ShortCommitLength"/> characters of <see cref="CommitId"/>; empty when there is none.</summary>
    public string CommitShort => CommitId is null ? string.Empty : CommitId[..Math.Min(ShortCommitLength, CommitId.Length)];

    public bool HasCommitId => CommitId is not null;

    /// <summary>Why the step commit failed (git's message, English), or null.</summary>
    public string? CommitError { get; }

    /// <summary>A failed commit only matters while the item is done: unticking it hides the marker and the retry button.</summary>
    public bool HasCommitError => isDone && CommitError is not null;

    /// <summary>Commits the step again (the failure was a hook, a rebase in progress, a detached HEAD, ...).</summary>
    public AsyncRelayCommand RetryCommitCommand { get; }

    public string Id { get; }

    public int Order { get; }

    public string Title
    {
        get => title;
        set
        {
            if (Set(ref title, value))
            {
                owner.Update(Id, value, null);
            }
        }
    }

    public string? Note { get; }

    public bool IsDone
    {
        get => isDone;
        set
        {
            if (Set(ref isDone, value))
            {
                OnPropertyChanged(nameof(HasCommitError));
                RetryCommitCommand.NotifyCanExecuteChanged();

                // Only the not-done to done edge made in this row commits a step. Rows rebuilt from the store (the agent's
                // update arrives that way) set the field directly and never come through here.
                owner.Update(Id, null, value ? TodoStatus.Done : TodoStatus.Pending, commitStep: value);
            }
        }
    }

    public RelayCommand RemoveCommand => new(() => owner.Remove(Id));

    public RelayCommand MoveUpCommand => new(() => owner.Move(Id, Order - 1));

    public RelayCommand MoveDownCommand => new(() => owner.Move(Id, Order + 1));
}

/// <summary>
/// The TODO panel of the active session; the agent's <c>todo</c> tool writes the same rows. The summary is kept as a
/// <see cref="LocText"/> and resolved when it is read, so a language switch only has to re-raise it.
/// </summary>
internal sealed class TodoListViewModel : ObservableObject, IDisposable
{
    private readonly ITodoStore store;
    private readonly IStepCommitter? committer;
    private string sessionId = string.Empty;
    private string newTitle = string.Empty;
    private LocText summary = new(InspectorStrings.TodoNoSession);
    private int generation;

    /// <param name="store">The checklist the panel edits and refreshes from.</param>
    /// <param name="committer">
    /// Commits a step when the user ticks an item done or retries a failed commit; null (a unit test) means the panel only
    /// edits the checklist. The agent's own updates commit inside Core, never through here.
    /// </param>
    public TodoListViewModel(ITodoStore store, IStepCommitter? committer = null)
    {
        this.store = store;
        this.committer = committer;
        AddCommand = new AsyncRelayCommand(
            ct => AddAsync(ct),
            () => sessionId.Length > 0 && !string.IsNullOrWhiteSpace(newTitle),
            exception => SetSummary(LocText.Literal(exception.Message)));
        RefreshCommand = new AsyncRelayCommand(
            ct => RefreshAsync(ct),
            () => sessionId.Length > 0,
            exception => SetSummary(LocText.Literal(exception.Message)));
        ClearDoneCommand = new AsyncRelayCommand(
            ct => ClearDoneAsync(ct),
            () => Items.Any(static item => item.IsDone),
            exception => SetSummary(LocText.Literal(exception.Message)));
        store.Changed += OnStoreChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<TodoEntryViewModel> Items { get; } = [];

    public AsyncRelayCommand AddCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand ClearDoneCommand { get; }

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

    /// <summary>"3/8 完成" for the attached session, or why there is nothing to show, in the language of the moment it is read.</summary>
    public string Summary => summary.Resolve();

    public bool IsEmpty => Items.Count == 0;

    public void Attach(string? newSessionId)
    {
        var next = newSessionId ?? string.Empty;
        if (string.Equals(sessionId, next, StringComparison.Ordinal))
        {
            return;
        }

        sessionId = next;
        Items.Clear();
        OnPropertyChanged(nameof(IsEmpty));
        AddCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        _ = RefreshAsync(CancellationToken.None);
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (sessionId.Length == 0)
        {
            SetSummary(new LocText(InspectorStrings.TodoNoSession));
            Items.Clear();
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        var current = Interlocked.Increment(ref generation);
        try
        {
            var id = sessionId;
            var items = await BackgroundRead.RunAsync(token => store.ListAsync(id, token), ct).ConfigureAwait(true);
            if (current != Volatile.Read(ref generation))
            {
                return;
            }

            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(new TodoEntryViewModel(this, item));
            }

            SetSummary(Items.Count == 0
                ? new LocText(InspectorStrings.TodoEmpty)
                : InspectorStrings.TodoSummary(Items.Count(static item => item.IsDone), Items.Count));
            OnPropertyChanged(nameof(IsEmpty));
            ClearDoneCommand.NotifyCanExecuteChanged();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            SetSummary(LocText.Literal(exception.Message));
        }
    }

    /// <summary>
    /// Writes a title or status change on the thread pool. With <paramref name="commitStep"/> (the user just ticked the item
    /// done) the step is committed after the status write, with the item exactly as the store returned it, still off the UI
    /// thread. The commit result reaches the row through the store's <c>Changed</c> refresh, like every other edit.
    /// </summary>
    internal void Update(string id, string? title, TodoStatus? status, bool commitStep = false)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var updated = await store.UpdateAsync(id, title, status, null, CancellationToken.None).ConfigureAwait(false);
                    if (commitStep && committer is not null && updated is { Status: TodoStatus.Done })
                    {
                        await committer.CommitStepAsync(updated, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => SetSummary(LocText.Literal(exception.Message)));
                }
                catch (Exception exception) when (commitStep && exception is not OperationCanceledException)
                {
                    // The committer reports its expected failures as results; anything thrown is a bug or a dead store.
                    await Dispatcher.UIThread.InvokeAsync(() => SetSummary(LocText.Literal(exception.Message)));
                }
            });
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

    /// <summary>Shows an unexpected failure of a row command in the panel summary line.</summary>
    internal void ReportFailure(Exception exception) => SetSummary(LocText.Literal(exception.Message));

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
                    await Dispatcher.UIThread.InvokeAsync(() => SetSummary(LocText.Literal(exception.Message)));
                }
            });

    internal void Move(string id, int order) =>
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await store.MoveAsync(id, order, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => SetSummary(LocText.Literal(exception.Message)));
                }
            });

    private async Task AddAsync(CancellationToken ct)
    {
        var title = newTitle.Trim();
        if (title.Length == 0 || sessionId.Length == 0)
        {
            return;
        }

        NewTitle = string.Empty;
        var id = sessionId;
        await BackgroundRead.RunAsync(token => store.AddAsync(id, title, null, null, token), ct).ConfigureAwait(true);
    }

    private async Task ClearDoneAsync(CancellationToken ct)
    {
        var done = Items.Where(static item => item.IsDone).Select(static item => item.Id).ToArray();
        await BackgroundRead.RunAsync(
                async token =>
                {
                    foreach (var id in done)
                    {
                        await store.RemoveAsync(id, token).ConfigureAwait(false);
                    }
                },
                ct)
            .ConfigureAwait(true);
    }

    private void OnStoreChanged(string changedSessionId)
    {
        if (string.Equals(changedSessionId, sessionId, StringComparison.Ordinal))
        {
            Dispatcher.UIThread.Post(() => _ = RefreshAsync(CancellationToken.None));
        }
    }

    private void SetSummary(LocText value)
    {
        summary = value;
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>The summary is a <see cref="LocText"/>; a language switch only has to re-read it.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(Summary));

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
