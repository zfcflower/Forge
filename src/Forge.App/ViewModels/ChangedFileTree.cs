using Avalonia.Media;

namespace Forge.App.ViewModels;

/// <summary>
/// One row of the 已改动 tree: a (possibly compacted) folder or a changed file. A chain of folders that each hold exactly
/// one folder is one row named <c>a/b/c</c>; the row's <see cref="Path"/> is its deepest folder. Nodes are built eagerly
/// from the (small) change set, so expanding is only splicing rows in or out of the flat list.
/// </summary>
internal sealed class ChangedTreeNodeViewModel : ObservableObject, IFlatTreeNode<ChangedTreeNodeViewModel>
{
    private readonly Action<ChangedTreeNodeViewModel> expansionChanged;
    private readonly ChangedTreeNodeViewModel? parent;
    private readonly List<ChangedTreeNodeViewModel> children = [];
    private int guideMask;
    private bool isExpanded;
    private bool isSelectedFile;

    public ChangedTreeNodeViewModel(
        string name,
        string path,
        FileDiffViewModel? file,
        int depth,
        ChangedTreeNodeViewModel? parent,
        bool expanded,
        Action<ChangedTreeNodeViewModel> expansionChanged,
        Action<FileDiffViewModel> open)
    {
        Name = name;
        Path = path;
        File = file;
        Depth = depth;
        this.parent = parent;
        isExpanded = expanded;
        this.expansionChanged = expansionChanged;
        guideMask = parent is null ? 0 : parent.guideMask | (parent.isExpanded && parent.Depth < 31 ? 1 << parent.Depth : 0);
        ToggleCommand = new RelayCommand(() =>
        {
            if (file is null)
            {
                IsExpanded = !IsExpanded;
            }
            else
            {
                open(file);
            }
        });
    }

    /// <summary>The folder chain (<c>tests/Forge.Workspace.Tests/Git</c>) or the file name.</summary>
    public string Name { get; }

    /// <summary>The repository-relative path: the file, or the deepest folder of the chain.</summary>
    public string Path { get; }

    public FileDiffViewModel? File { get; }

    public bool IsDirectory => File is null;

    public int Depth { get; }

    public IReadOnlyList<ChangedTreeNodeViewModel> Children => children;

    public IReadOnlyList<IndentMark> Guides => IndentMarks.ForMask(guideMask);

    public RelayCommand ToggleCommand { get; }

    public bool ChevronRotated => isExpanded;

    public Geometry Icon => IsDirectory ? (isExpanded ? TreeIcons.FolderOpen : TreeIcons.Folder) : TreeIcons.File;

    public string Badge => File?.Badge ?? string.Empty;

    /// <summary>A file added by the change set: its name is drawn in the "added" green.</summary>
    public bool IsAdded => File is { Badge: "A" };

    /// <summary>"+3" / "−2" are shown for text files; a binary file shows only its status letter.</summary>
    public bool ShowCounts => File is { IsBinary: false };

    public bool ShowBadgeOnly => File is { IsBinary: true };

    public string AddedText => File is null ? string.Empty : "+" + File.Added.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string RemovedText => File is null ? string.Empty : "−" + File.Removed.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string? ToolTip => File?.Description;

    /// <summary>True for the file the changes document currently shows.</summary>
    public bool IsSelectedFile
    {
        get => isSelectedFile;
        internal set => Set(ref isSelectedFile, value);
    }

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (!IsDirectory || !Set(ref isExpanded, value))
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

    internal void AddChild(ChangedTreeNodeViewModel child) => children.Add(child);

    private void RefreshGuideMask()
    {
        var next = parent is null ? 0 : parent.guideMask | (parent.isExpanded && parent.Depth < 31 ? 1 << parent.Depth : 0);
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
/// The changed files as a tree shown through one flat, virtualized row list. Folders sort before files, both by name;
/// single-folder chains are compacted. The folded set survives a rebuild (a new diff of the same scope keeps what the
/// reader folded), and "collapse all / expand all" rebuilds the rows in one step.
/// </summary>
internal sealed class ChangedFileTree
{
    private readonly FlatTree<ChangedTreeNodeViewModel> tree = new();
    private readonly HashSet<string> collapsed = new(StringComparer.Ordinal);
    private readonly Action<FileDiffViewModel> open;
    private IReadOnlyList<FileDiffViewModel> files = [];
    private List<ChangedTreeNodeViewModel> roots = [];
    private FileDiffViewModel? selected;

    public ChangedFileTree(Action<FileDiffViewModel> open)
    {
        this.open = open;
    }

    public RangeObservableCollection<ChangedTreeNodeViewModel> Rows => tree.Rows;

    public IReadOnlyList<ChangedTreeNodeViewModel> Roots => roots;

    /// <summary>True when every folder is folded (and there is at least one folder): the toggle then offers "expand all".</summary>
    public bool IsAllCollapsed
    {
        get
        {
            var any = false;
            foreach (var folder in Folders())
            {
                if (folder.IsExpanded)
                {
                    return false;
                }

                any = true;
            }

            return any;
        }
    }

    public bool HasFolders => Folders().Any();

    /// <summary>Replaces the tree with the one of <paramref name="changed"/>, keeping the folded folders.</summary>
    public void Replace(IReadOnlyList<FileDiffViewModel> changed)
    {
        files = changed;
        Rebuild();
    }

    /// <summary>Folds or unfolds every folder.</summary>
    public void SetAllExpanded(bool expanded)
    {
        collapsed.Clear();
        if (!expanded)
        {
            foreach (var folder in Folders())
            {
                collapsed.Add(folder.Path);
            }
        }

        Rebuild();
    }

    /// <summary>Marks the row of <paramref name="file"/> as the shown one.</summary>
    public void SetSelected(FileDiffViewModel? file)
    {
        selected = file;
        foreach (var node in All())
        {
            if (!node.IsDirectory)
            {
                node.IsSelectedFile = ReferenceEquals(node.File, file);
            }
        }
    }

    /// <summary>Raised after a folder is folded or unfolded, so the owner can re-read <see cref="IsAllCollapsed"/>.</summary>
    public event Action? FoldingChanged;

    private void Rebuild()
    {
        roots = Build(files, collapsed, OnExpansionChanged, open);
        // Folders that vanished from the change set must not stay in the folded set forever.
        collapsed.IntersectWith(Folders().Select(static folder => folder.Path));
        tree.Replace(FlatTree<ChangedTreeNodeViewModel>.Flatten(roots));
        SetSelected(selected);
        FoldingChanged?.Invoke();
    }

    private void OnExpansionChanged(ChangedTreeNodeViewModel node)
    {
        if (node.IsExpanded)
        {
            collapsed.Remove(node.Path);
        }
        else
        {
            collapsed.Add(node.Path);
        }

        tree.ExpansionChanged(node);
        FoldingChanged?.Invoke();
    }

    private IEnumerable<ChangedTreeNodeViewModel> Folders() => All().Where(static node => node.IsDirectory);

    private IEnumerable<ChangedTreeNodeViewModel> All()
    {
        var stack = new Stack<ChangedTreeNodeViewModel>();
        for (var index = roots.Count - 1; index >= 0; index--)
        {
            stack.Push(roots[index]);
        }

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var index = node.Children.Count - 1; index >= 0; index--)
            {
                stack.Push(node.Children[index]);
            }
        }
    }

    /// <summary>
    /// Builds the node tree: split every path on <c>/</c>, then fold one-folder chains into one node and sort each level
    /// folders-first, then by name (ordinal, case-insensitive). Exposed for tests.
    /// </summary>
    internal static List<ChangedTreeNodeViewModel> Build(
        IReadOnlyList<FileDiffViewModel> changed,
        ISet<string> collapsedFolders,
        Action<ChangedTreeNodeViewModel> expansionChanged,
        Action<FileDiffViewModel> open)
    {
        var root = new Trie(string.Empty, string.Empty);
        foreach (var file in changed)
        {
            var parts = file.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var current = root;
            for (var index = 0; index < parts.Length - 1; index++)
            {
                current = current.Folder(parts[index]);
            }

            current.Files.Add(file);
        }

        var result = new List<ChangedTreeNodeViewModel>();
        Emit(root, null, 0);
        return result;

        void Emit(Trie container, ChangedTreeNodeViewModel? parent, int depth)
        {
            var folders = container.Folders.Values.OrderBy(static folder => folder.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var folder in folders)
            {
                // Fold a chain: a folder with no files of its own and exactly one folder inside is joined to that folder.
                var leaf = folder;
                var name = folder.Name;
                while (leaf.Files.Count == 0 && leaf.Folders.Count == 1)
                {
                    leaf = leaf.Folders.Values.First();
                    name += "/" + leaf.Name;
                }

                var node = new ChangedTreeNodeViewModel(
                    name, leaf.Path, null, depth, parent, !collapsedFolders.Contains(leaf.Path), expansionChanged, open);
                Add(node, parent);
                Emit(leaf, node, depth + 1);
            }

            foreach (var file in container.Files.OrderBy(static file => LastSegment(file.Path), StringComparer.OrdinalIgnoreCase))
            {
                Add(new ChangedTreeNodeViewModel(LastSegment(file.Path), file.Path, file, depth, parent, true, expansionChanged, open), parent);
            }
        }

        void Add(ChangedTreeNodeViewModel node, ChangedTreeNodeViewModel? parent)
        {
            if (parent is null)
            {
                result.Add(node);
            }
            else
            {
                parent.AddChild(node);
            }
        }
    }

    private static string LastSegment(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private sealed class Trie(string name, string path)
    {
        public string Name { get; } = name;

        public string Path { get; } = path;

        public Dictionary<string, Trie> Folders { get; } = new(StringComparer.Ordinal);

        public List<FileDiffViewModel> Files { get; } = [];

        public Trie Folder(string childName)
        {
            if (!Folders.TryGetValue(childName, out var child))
            {
                child = new Trie(childName, Path.Length == 0 ? childName : Path + "/" + childName);
                Folders.Add(childName, child);
            }

            return child;
        }
    }
}
