using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Forge.Abstractions;
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
    private readonly List<FileNodeViewModel> children = [];
    private bool isExpanded;

    public FileNodeViewModel(
        FileEntry entry,
        FileIndex index,
        Action<string> open,
        Action<string> openInNewTab,
        int depth,
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
        ToggleCommand = new RelayCommand(Toggle);
        OpenCommand = new RelayCommand(() => this.open(RelativePath));
        OpenInNewTabCommand = new RelayCommand(() => this.openInNewTab(RelativePath));
    }

    public string Name { get; }

    public string RelativePath { get; }

    public bool IsDirectory { get; }

    public int Depth { get; }

    /// <summary>One 12 px step per level, each carrying a 1 px guide line.</summary>
    public IReadOnlyList<IndentMark> Guides => IndentMarks.For(Depth);

    /// <summary>The file or folder icon drawn before the name.</summary>
    public Geometry Icon => IsDirectory ? TreeIcons.Folder : TreeIcons.File;

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
            if (value)
            {
                LoadChildren();
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
            children.Add(new FileNodeViewModel(entry, index, open, openInNewTab, Depth + 1, expansionChanged));
        }
    }
}

/// <summary>
/// One row of <c>已改动</c>: the path, and at the far right the diff line counts followed by the status letter. A file
/// whose counts could not be read (no HEAD yet, an unreadable diff) shows the status letter alone, and so does a binary
/// file: a patch of a binary change has no line counts, so "+0 -0" would be a lie.
/// </summary>
internal sealed record ChangedFileViewModel(
    string Path,
    string Status,
    int? Insertions,
    int? Deletions,
    bool IsBinary,
    string Description)
{
    public string Meta => ChangeBadge.Format(Status, Insertions, Deletions, IsBinary);

    public string Display => Path + "  " + Meta;
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

        return "+" + added.ToString(CultureInfo.InvariantCulture) + " -" + removed.ToString(CultureInfo.InvariantCulture) +
               " " + statusLetter;
    }
}

/// <summary>
/// The right-top "文件" tool: a lazy tree of everything (<c>全部</c>) and the git status list with M/A/D/?? badges
/// (<c>已改动</c>). Both refresh on turn end and on a debounced file-system watcher; the tree is rebuilt off the dispatcher
/// and re-applied as one virtualized row list, keeping every expanded folder and the row order.
/// </summary>
internal sealed class FilesPanelViewModel : ObservableObject
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
    private Dictionary<string, LineCount>? countsCache;
    private string? countsFingerprint;
    private bool isLoading;
    private int selectedTab;
    private int refreshGeneration;

    public FilesPanelViewModel(GitCli git, Action<string> openFile, Action<string> openFileInNewTab, Action<string> openChanges)
    {
        this.git = git;
        this.openFile = openFile;
        this.openFileInNewTab = openFileInNewTab;
        this.openChanges = openChanges;
        ChangedCommand = new AsyncRelayCommand(ct => RefreshAsync(ct), () => true, OnFailed);
        AllTabCommand = new RelayCommand(() => SelectedTab = 0);
        ChangedTabCommand = new RelayCommand(() => SelectedTab = 1);
    }

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
        ? Strings.FileSummary(Changes.Count, index?.FileCount ?? 0)
        : Strings.NoSession;

    public string? Error { get; private set; }

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
            countsCache = null;
            countsFingerprint = null;
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

            Error = null;
            OnPropertyChanged(nameof(Summary));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or GitException or UnauthorizedAccessException)
        {
            Error = exception.Message;
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
            var node = new FileNodeViewModel(entry, built, openFile, openFileInNewTab, 0, OnExpansionChanged);
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
        if (countsCache is not null && string.Equals(fingerprint, countsFingerprint, StringComparison.Ordinal))
        {
            return countsCache;
        }

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

        countsFingerprint = fingerprint;
        countsCache = counts;
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

    private static string Describe(GitStatusEntry entry) =>
        entry.IsStaged && entry.IsUnstaged ? Strings.StagedAndUnstaged() : entry.IsStaged ? Strings.Staged() : Strings.WorkingTree();

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
            Error = Strings.WatchUnavailable(exception.Message);
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

    private void OnFailed(Exception exception) => Error = exception.Message;
}

// ---------------------------------------------------------------- file document

internal sealed record FileLineViewModel(int Number, string Text);

/// <summary>Read-only file view: one entry per line with its number, virtualized in the view.</summary>
internal sealed class FileDocumentViewModel : ObservableObject
{
    private const int MaxLines = 20000;

    private string path = string.Empty;
    private string tabTitle = Strings.FileTabTitle(Strings.FilesName);
    private bool isLoading;
    private string? error;

    private FileDocumentViewModel()
    {
        LineItems = new ReadOnlyObservableCollection<FileLineViewModel>(Lines);
        LoadCommand = new AsyncRelayCommand(ct => LoadAsync(path, ct), () => path.Length > 0, exception => Error = exception.Message);
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

    public string TabTitle
    {
        get => tabTitle;
        private set => Set(ref tabTitle, value);
    }

    public bool IsLoading
    {
        get => isLoading;
        private set => Set(ref isLoading, value);
    }

    public string? Error
    {
        get => error;
        private set
        {
            if (Set(ref error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(error);

    public static FileDocumentViewModel Create() => new();

    /// <summary>Replaces this fixed document's content with another file.</summary>
    public void Reload(FileDocumentViewModel other)
    {
        Path = other.Path;
        TabTitle = other.TabTitle;
        Lines.Clear();
        foreach (var line in other.Lines)
        {
            Lines.Add(line);
        }

        Error = other.Error;
    }

    public async Task LoadAsync(string filePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        Path = System.IO.Path.GetFullPath(filePath);
        TabTitle = Strings.FileTabTitle(System.IO.Path.GetFileName(Path));
        Lines.Clear();
        Error = null;
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
                            return new[] { Strings.FileTooLarge(info.Length) };
                        }

                        var read = await File.ReadAllLinesAsync(filePath, ct).ConfigureAwait(false);
                        return read.Length > MaxLines ? read[..MaxLines] : read;
                    },
                    ct)
                .ConfigureAwait(true);

            if (lines is null)
            {
                Error = Strings.FileMissing(filePath);
                return;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                Lines.Add(new FileLineViewModel(i + 1, lines[i].Replace("\t", "    ", StringComparison.Ordinal)));
            }

            if (lines.Length == MaxLines)
            {
                Error = Strings.LinesTruncated(MaxLines);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Error = exception.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }
}

// ---------------------------------------------------------------- changes document

internal enum ChangeScope
{
    ByTurn,
    Uncommitted,
}

internal sealed record DiffLineViewModel(string Prefix, string Text, string Kind, string OldNumber, string NewNumber)
{
    public bool IsAdd => Kind == "add";

    public bool IsRemove => Kind == "del";

    public bool IsHunk => Kind == "hunk";
}

/// <summary>One row of the changes document: a file header or one diff line, in the order they are stacked.</summary>
internal abstract class DiffRowViewModel(FileDiffViewModel file)
{
    public FileDiffViewModel File { get; } = file;
}

internal sealed class DiffFileRowViewModel(FileDiffViewModel file) : DiffRowViewModel(file)
{
    public string Header => File.Header;

    public string Badge => File.Badge;

    public bool IsHeader => true;
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
}

internal sealed class FileDiffViewModel
{
    public FileDiffViewModel(FileDiff diff, string prefix = "")
    {
        Path = Strip(diff, prefix);
        var added = diff.AddedLineCount;
        var removed = diff.RemovedLineCount;
        Header = Strings.FileDiffHeader(Path, added, removed);
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
            lines.Add(new DiffLineViewModel(string.Empty, hunk.Header, "hunk", string.Empty, string.Empty));
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
                    line.Text.Replace("\t", "    ", StringComparison.Ordinal),
                    kind,
                    line.OldLineNumber > 0 ? line.OldLineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
                    line.NewLineNumber > 0 ? line.NewLineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty));
            }
        }

        if (lines.Count == 0)
        {
            lines.Add(new DiffLineViewModel(" ", Strings.NoTextualChange, "ctx", string.Empty, string.Empty));
        }

        Lines = lines;
    }

    public string Path { get; }

    public string Header { get; }

    public string Badge { get; }

    public IReadOnlyList<DiffLineViewModel> Lines { get; }

    /// <summary>Diff paths are repository-relative; the panel shows them relative to the session directory.</summary>
    private static string Strip(FileDiff diff, string prefix)
    {
        var path = diff.Path.Replace(System.IO.Path.DirectorySeparatorChar, '/');
        return prefix.Length > 0 && path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }
}

internal sealed record TurnChoiceViewModel(int Turn, string Label, string TreeBefore, string? TreeAfter);

/// <summary>
/// The fixed "改动" document. Scope "按轮次" diffs the two trees the workspace layer recorded around one main-session turn
/// (<see cref="GitCli.GetTreeDiffAsync"/> over the hashes in <c>turn_checkpoints</c>); "未提交改动" diffs the working tree
/// against HEAD including untracked files. All files are stacked into one flat row list that the view virtualizes;
/// selecting one scrolls the view to it.
/// </summary>
internal sealed class ChangesViewModel : ObservableObject
{
    private readonly GitCli git;
    private readonly ITurnCheckpointStore checkpoints;
    private readonly List<FileDiffViewModel> files = [];
    private PanelContext context = PanelContext.Empty;
    private ChangeScope scope = ChangeScope.Uncommitted;
    private TurnChoiceViewModel? selectedTurn;
    private FileDiffViewModel? selectedFile;
    private bool isLoading;
    private string? message;
    private int generation;

    public ChangesViewModel(GitCli git, ITurnCheckpointStore checkpoints)
    {
        this.git = git;
        this.checkpoints = checkpoints;
        RefreshCommand = new AsyncRelayCommand(ct => LoadAsync(ct), () => true, exception => Message = exception.Message);
        ByTurnCommand = new RelayCommand(() => ScopeIndex = 0);
        UncommittedCommand = new RelayCommand(() => ScopeIndex = 1);
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

    public string? Message
    {
        get => message;
        private set
        {
            if (Set(ref message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(message);

    public string Summary => context.HasSession
        ? Strings.ChangesSummary(files.Count, IsByTurn)
        : Strings.NoSession;

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

            Message = files.Count == 0 ? Strings.NoChanges : null;
            SelectedFile = files.FirstOrDefault();
            OnPropertyChanged(nameof(Summary));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or GitException or UnauthorizedAccessException)
        {
            Message = exception.Message;
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
            Message = exception.Message;
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
                Strings.ChangeTurnLabel(checkpoint.Turn, checkpoint.StartedAt),
                checkpoint.TreeBefore,
                checkpoint.TreeAfter));
        }

        if (selectedTurn is null || Turns.All(turn => turn.Turn != selectedTurn.Turn))
        {
            selectedTurn = Turns.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedTurn));
        }
    }

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

internal sealed class TodoEntryViewModel : ObservableObject
{
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
    }

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
                owner.Update(Id, null, value ? TodoStatus.Done : TodoStatus.Pending);
            }
        }
    }

    public RelayCommand RemoveCommand => new(() => owner.Remove(Id));

    public RelayCommand MoveUpCommand => new(() => owner.Move(Id, Order - 1));

    public RelayCommand MoveDownCommand => new(() => owner.Move(Id, Order + 1));
}

/// <summary>The TODO panel of the active session; the agent's <c>todo</c> tool writes the same rows.</summary>
internal sealed class TodoListViewModel : ObservableObject
{
    private readonly ITodoStore store;
    private string sessionId = string.Empty;
    private string newTitle = string.Empty;
    private string summary = Strings.NoSession;
    private int generation;

    public TodoListViewModel(ITodoStore store)
    {
        this.store = store;
        AddCommand = new AsyncRelayCommand(ct => AddAsync(ct), () => sessionId.Length > 0 && !string.IsNullOrWhiteSpace(newTitle), exception => summary = exception.Message);
        RefreshCommand = new AsyncRelayCommand(ct => RefreshAsync(ct), () => sessionId.Length > 0, exception => summary = exception.Message);
        ClearDoneCommand = new AsyncRelayCommand(ct => ClearDoneAsync(ct), () => Items.Any(static item => item.IsDone), exception => summary = exception.Message);
        store.Changed += OnStoreChanged;
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

    public string Summary
    {
        get => summary;
        private set => Set(ref summary, value);
    }

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
            Summary = Strings.NoSession;
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

            Summary = Items.Count == 0
                ? Strings.TodoEmpty()
                : Strings.TodoSummary(Items.Count(static item => item.IsDone), Items.Count);
            OnPropertyChanged(nameof(IsEmpty));
            ClearDoneCommand.NotifyCanExecuteChanged();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            Summary = exception.Message;
        }
    }

    internal void Update(string id, string? title, TodoStatus? status)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await store.UpdateAsync(id, title, status, null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => Summary = exception.Message);
                }
            });
    }

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
                    await Dispatcher.UIThread.InvokeAsync(() => Summary = exception.Message);
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
                    await Dispatcher.UIThread.InvokeAsync(() => Summary = exception.Message);
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
}

// ---------------------------------------------------------------- review

internal sealed record ReviewEntryViewModel(string Path, string Line, string Body, string Status);

/// <summary>Review comments of the active worktree, read-only for now; click opens the file in the changes document.</summary>
internal sealed class ReviewPanelViewModel : ObservableObject
{
    private readonly ReviewStore store;
    private readonly Action<string> openChange;
    private PanelContext context = PanelContext.Empty;
    private string summary = Strings.NoSession;

    public ReviewPanelViewModel(ReviewStore store, Action<string> openChange)
    {
        this.store = store;
        this.openChange = openChange;
        RefreshCommand = new AsyncRelayCommand(ct => RefreshAsync(ct), () => true, exception => Summary = exception.Message);
    }

    public ObservableCollection<ReviewEntryViewModel> Items { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public string Summary
    {
        get => summary;
        private set => Set(ref summary, value);
    }

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
            Summary = Strings.NoSession;
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
                    Strings.ReviewLine(comment.NewLineStart, comment.NewLineEnd),
                    comment.Body,
                    Strings.ReviewStatus(comment.IsResolved, comment.Status == ReviewCommentStatus.Sent)));
            }

            Summary = Strings.ReviewSummary(Items.Count);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException)
        {
            Summary = exception.Message;
        }
    }
}

// ---------------------------------------------------------------- spec tree

/// <summary>One spec of the graph, shown as part of the flat, virtualized row list of the spec tree.</summary>
internal sealed class SpecNodeViewModel : ObservableObject, IFlatTreeNode<SpecNodeViewModel>
{
    private readonly Action<SpecNodeViewModel> expansionChanged;
    private readonly List<SpecNodeViewModel> children = [];
    private bool isExpanded;

    public SpecNodeViewModel(int depth, bool isExpanded, Action<SpecNodeViewModel> expansionChanged)
    {
        Depth = depth;
        this.isExpanded = isExpanded;
        this.expansionChanged = expansionChanged;
    }

    public required string Title { get; init; }

    public required string Status { get; init; }

    public required string FullPath { get; init; }

    public required string Id { get; init; }

    public int Depth { get; private set; }

    public Thickness Indent => new(Depth * 12, 0, 0, 0);

    public IReadOnlyList<SpecNodeViewModel> Children => children;

    public string Glyph => children.Count > 0 ? (isExpanded ? "▾" : "▸") : "·";

    public string Display => Status.Length > 0 ? Title + "  " + Status : Title;

    public RelayCommand? ToggleCommand => children.Count > 0 ? new RelayCommand(() => IsExpanded = !IsExpanded) : null;

    public RelayCommand OpenCommand => new(Open);

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (!Set(ref isExpanded, value))
            {
                return;
            }

            OnPropertyChanged(nameof(Glyph));
            expansionChanged(this);
        }
    }

    public Action<string>? OpenFile { get; init; }

    public void Add(SpecNodeViewModel child) => children.Add(child);

    public void SetDepth(int depth) => Depth = depth;

    private void Open() => OpenFile?.Invoke(FullPath);
}

/// <summary>
/// The spec tree of the active project root, following the declared parent hierarchy. The graph is read and the row list
/// is built on a pool thread; the panel keeps a virtualized row list and remembers which nodes the user collapsed.
/// </summary>
internal sealed class SpecTreeViewModel : ObservableObject
{
    private readonly Func<string, SpecGraph> graphFactory;
    private readonly Action<string> openFile;
    private readonly FlatTree<SpecNodeViewModel> tree = new();
    private readonly HashSet<string> collapsedIds = new(StringComparer.Ordinal);
    private PanelContext context = PanelContext.Empty;
    private string summary = Strings.NoProject;

    public SpecTreeViewModel(Func<string, SpecGraph> graphFactory, Action<string> openFile)
    {
        this.graphFactory = graphFactory;
        this.openFile = openFile;
        RefreshCommand = new AsyncRelayCommand(ct => RefreshAsync(ct), () => Context.ProjectRoot is not null, exception => Summary = exception.Message);
    }

    /// <summary>The visible rows of the tree, in display order; the view virtualizes this one list.</summary>
    public RangeObservableCollection<SpecNodeViewModel> Rows => tree.Rows;

    public AsyncRelayCommand RefreshCommand { get; }

    public PanelContext Context => context;

    public string Summary
    {
        get => summary;
        private set => Set(ref summary, value);
    }

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
            summary = Strings.NoProject;
            OnPropertyChanged(nameof(Summary));
            return;
        }

        var collapsed = new HashSet<string>(collapsedIds, StringComparer.Ordinal);
        try
        {
            var built = await Task.Run(() => BuildAsync(root, collapsed, ct), ct).ConfigureAwait(true);
            tree.Replace(built.Rows);
            Summary = Strings.SpecCount(built.Count);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            Summary = exception.Message;
        }
    }

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
