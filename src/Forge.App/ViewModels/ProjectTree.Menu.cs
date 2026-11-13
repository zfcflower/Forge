using System.Collections.Concurrent;
using System.Diagnostics;
using Forge.Abstractions;
using Forge.App.Lifecycle;
using Forge.App.Localization;
using Forge.Workspace;
using ICommand = System.Windows.Input.ICommand;

namespace Forge.App.ViewModels;

/// <summary>
/// What one refresh read about a linked workspace's checkout (lifecycle 5.3): its HEAD, a merge or rebase in progress,
/// and whether it is a sparse checkout. Read off the dispatcher at refresh time, never when a menu opens (I3).
/// </summary>
internal sealed record WorkspaceGitState(GitHeadKind Head, bool MergeOrRebase, bool IsSparse);

/// <summary>The tree-menu half of a row: the generated entries (5.4 rule 1), the "+" first group and the extra commands.</summary>
internal sealed partial class TreeNodeViewModel
{
    private bool isSparse;
    private bool isPlainProject;

    /// <summary>
    /// The row as its menu sees it, built from the last plan and the in-memory flags of the moment; null for a row without
    /// a menu. Calling it never starts a git process or a database read (I3).
    /// </summary>
    public Func<TreeRowState>? MenuState { get; set; }

    /// <summary>"Link an existing workspace…" on a repository project row (7.10).</summary>
    public ICommand? LinkWorkspaceCommand { get; private set; }

    /// <summary>"New session (replace X)…" on a workspace row with an active session (6.2).</summary>
    public ICommand? ReplaceSessionCommand { get; private set; }

    /// <summary>"Rename branch…" on a linked workspace row (7.8).</summary>
    public ICommand? RenameBranchCommand { get; private set; }

    /// <summary>"Continue this session…" on an archived session row (6.2).</summary>
    public ICommand? ContinueCommand { get; private set; }

    /// <summary>The checkout is sparse (7.9): the row shows a read-only mark.</summary>
    public bool IsSparse
    {
        get => isSparse;
        set => Set(ref isSparse, value);
    }

    public string SparseLabel => TreeMenuStrings.SparseMark;

    /// <summary>A project row whose root is not a git repository: no workspaces, so no trailing "+" either (5.2).</summary>
    public bool IsPlainProject
    {
        get => isPlainProject;
        set => SetAndNotify(ref isPlainProject, value, nameof(HasNewSession));
    }

    /// <summary>Replaces the commands only the generated menu reaches; null leaves the entry without a command (disabled).</summary>
    public void SetMenuCommands(ICommand? linkWorkspace, ICommand? replaceSession, ICommand? renameBranch, ICommand? continueSession)
    {
        LinkWorkspaceCommand = linkWorkspace;
        ReplaceSessionCommand = replaceSession;
        RenameBranchCommand = renameBranch;
        ContinueCommand = continueSession;
    }

    /// <summary>The generated menu of this row in the current language; empty for a row without a menu.</summary>
    public IReadOnlyList<TreeMenuItemViewModel> MenuItems()
    {
        if (MenuState?.Invoke() is not { } state)
        {
            return [];
        }

        return Render(TreeMenu.Build(state), state);
    }

    /// <summary>The first group of a root-checkout or workspace row: what its trailing "+" offers (5.3).</summary>
    public IReadOnlyList<TreeMenuItemViewModel> PlusItems()
    {
        if (MenuState?.Invoke() is not { } state)
        {
            return [];
        }

        return Render(TreeMenu.FirstGroup(state), state);
    }

    /// <summary>
    /// The trailing "+": on a root-checkout or workspace row it runs the first group's one enabled entry and returns true,
    /// or returns false so the view shows the first group as a small menu (several entries, or none enabled to explain
    /// why). Every other row runs its new-session command (a project row: "new workspace…").
    /// </summary>
    public bool TryRunPlus()
    {
        var state = MenuState?.Invoke();
        if (state?.Kind is not (TreeRowKind.RootCheckout or TreeRowKind.Workspace))
        {
            if (NewSessionCommand is { } create && create.CanExecute(null))
            {
                create.Execute(null);
            }

            return true;
        }

        var items = Render(TreeMenu.FirstGroup(state), state);
        TreeMenuItemViewModel? only = null;
        foreach (var item in items)
        {
            if (!item.IsEnabled)
            {
                continue;
            }

            if (only is not null)
            {
                return false;
            }

            only = item;
        }

        return only is not null && only.Run();
    }

    /// <summary>The command an entry runs on this row; null when the row does not have it.</summary>
    public ICommand? CommandFor(TreeMenuKey key) => key switch
    {
        TreeMenuKey.NewWorkspace or TreeMenuKey.NewSession => NewSessionCommand,
        TreeMenuKey.LinkWorkspace => LinkWorkspaceCommand,
        TreeMenuKey.RenameProject or TreeMenuKey.RenameSession => RenameCommand,
        TreeMenuKey.Reveal => RevealCommand,
        TreeMenuKey.CloseProject or TreeMenuKey.RemoveWorkspace or TreeMenuKey.RemoveSession => RemoveCommand,
        TreeMenuKey.ReplaceSession => ReplaceSessionCommand,
        TreeMenuKey.Fork or TreeMenuKey.ForkInherit => ForkCommand,
        TreeMenuKey.MergeBack => MergeBackCommand,
        TreeMenuKey.Sync => SyncCommand,
        TreeMenuKey.AbortMerge => AbortMergeCommand,
        TreeMenuKey.RenameBranch => RenameBranchCommand,
        TreeMenuKey.RestoreWorkspace or TreeMenuKey.Restore => RestoreCommand,
        TreeMenuKey.PurgeAll or TreeMenuKey.Purge => PurgeCommand,
        TreeMenuKey.Archive => ArchiveCommand,
        TreeMenuKey.View => OpenCommand,
        TreeMenuKey.Continue => ContinueCommand,
        _ => null,
    };

    private TreeMenuItemViewModel[] Render(IReadOnlyList<TreeMenuEntry> entries, TreeRowState state)
    {
        var items = new TreeMenuItemViewModel[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            items[i] = new TreeMenuItemViewModel(entry, TreeMenu.Header(entry.Key, state), CommandFor(entry.Key));
        }

        return items;
    }
}

/// <summary>The tree-menu half of the tree: the row snapshots, the commands only the menu reaches, the refresh-time git reads.</summary>
internal sealed partial class ProjectTreeViewModel
{
    /// <summary>How long one refresh's git read of a workspace stays good; later refreshes within it reuse the answer.</summary>
    private static readonly TimeSpan GitStateLifetime = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, (WorkspaceGitState? State, long Stamp)> gitStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string? Branch, long Stamp)> defaultBranches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The worktree list of each repository root (a null list is a read that failed: state unknown) and the linked
    /// directories that read was asked to judge. Keyed like the file system: case-insensitive on Windows only.
    /// </summary>
    private readonly ConcurrentDictionary<string, (IReadOnlyList<GitWorktree>? List, long Stamp, IReadOnlySet<string> Judged)> worktreeLists =
        new(PathComparer);

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Ids of the registered projects whose root the last refresh found not to be a git repository.</summary>
    private HashSet<string> plainProjects = new(StringComparer.Ordinal);

    /// <summary>"Link an existing workspace…" (7.10) of a repository project row; set by the shell.</summary>
    public Func<ProjectInfo, CancellationToken, Task>? LinkWorkspace { get; set; }

    /// <summary>"Rename branch…" (7.8) of a linked workspace row; set by the shell.</summary>
    public Func<WorkspaceTarget, CancellationToken, Task>? RenameBranch { get; set; }

    /// <summary>"New session (replace X)…" (6.2): the holder and the session that would replace it; set by the shell.</summary>
    public Func<SessionInfo, SessionCreate, CancellationToken, Task>? ReplaceSession { get; set; }

    /// <summary>"Continue this session…" (6.2) of an archived session row; set by the shell.</summary>
    public Func<string, CancellationToken, Task>? ContinueSession { get; set; }

    /// <summary>Why a session is not idle (its open tab's busy sources); in memory only, empty when idle or not open.</summary>
    public Func<string, IReadOnlyList<string>>? SessionBusyReasons { get; set; }

    /// <summary>A handover of the session is running; in memory only.</summary>
    public Func<string, bool>? IsSessionHandingOver { get; set; }

    /// <summary>A lifecycle operation holds the directory; in memory only.</summary>
    public Func<string, bool>? IsWorkspaceOperating { get; set; }

    /// <summary>
    /// Reads a linked workspace's checkout state, called off the dispatcher during a refresh (never from a menu); null
    /// leaves the state unknown, and the entries that depend on it stay for the flows to check after the click.
    /// </summary>
    public Func<string, CancellationToken, Task<WorkspaceGitState>>? ReadWorkspaceState { get; set; }

    /// <summary>The repository's default branch (<c>main</c> or <c>origin/main</c>), read like <see cref="ReadWorkspaceState"/>.</summary>
    public Func<string, CancellationToken, Task<string?>>? ReadDefaultBranch { get; set; }

    /// <summary>
    /// The repository's <c>git worktree list --porcelain</c>, read like <see cref="ReadWorkspaceState"/>: which linked
    /// workspace directories git still registers, and which it marks prunable. A failed read leaves those rows unverified.
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<GitWorktree>>>? ReadWorktrees { get; set; }

    /// <summary>Drops the cached git reads, so the next refresh asks git again (after a flow changed a branch or a checkout).</summary>
    public void InvalidateGitState()
    {
        gitStates.Clear();
        defaultBranches.Clear();
        worktreeLists.Clear();
    }

    /// <summary>
    /// The refresh-time git reads of the menus: the checkout state of every linked workspace directory that exists and
    /// the default branch of every repository project, bounded like the HEAD reads and reused for
    /// <see cref="GitStateLifetime"/>. A failed read is traced and planned as unknown.
    /// </summary>
    private async Task<MenuGitReads> ReadMenuGitStateAsync(
        IReadOnlyList<ProjectInfo> all,
        IReadOnlyList<SessionInfo> sessions,
        CancellationToken ct)
    {
        var readState = ReadWorkspaceState;
        var readDefault = ReadDefaultBranch;
        var readWorktrees = ReadWorktrees;
        if (scope == TreeScope.General || (readState is null && readDefault is null && readWorktrees is null))
        {
            return MenuGitReads.Empty;
        }

        var repositories = new Dictionary<string, ProjectInfo>(StringComparer.Ordinal);
        foreach (var project in all)
        {
            if (project.Kind == ProjectKind.Repository && project.RootPath.Length > 0)
            {
                repositories[project.Id] = project;
            }
        }

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Every linked workspace directory of a repository (removed ones too: their rows are planned the same way), the
        // directories a cached worktree list has to name before it is reused.
        var linked = new Dictionary<string, HashSet<string>>(PathComparer);
        foreach (var session in sessions)
        {
            if (session.ParentSessionId is null
                && session.WorktreePath is { Length: > 0 } path
                && session.ProjectId is { } projectId
                && repositories.TryGetValue(projectId, out var owner)
                && !RemovalFlow.SameDirectory(path, owner.RootPath))
            {
                if (session.Status != SessionStatus.Deleted)
                {
                    directories.Add(path);
                }

                if (!linked.TryGetValue(owner.RootPath, out var paths))
                {
                    linked[owner.RootPath] = paths = new HashSet<string>(PathComparer);
                }

                paths.Add(path);
            }
        }

        // Workspaces and projects that left the tree take their cached reads with them.
        foreach (var key in gitStates.Keys)
        {
            if (!directories.Contains(key))
            {
                gitStates.TryRemove(key, out _);
            }
        }

        foreach (var key in defaultBranches.Keys)
        {
            if (!repositories.Values.Any(project => string.Equals(project.RootPath, key, StringComparison.OrdinalIgnoreCase)))
            {
                defaultBranches.TryRemove(key, out _);
            }
        }

        foreach (var key in worktreeLists.Keys)
        {
            if (!linked.ContainsKey(key))
            {
                worktreeLists.TryRemove(key, out _);
            }
        }

        var now = Stopwatch.GetTimestamp();
        var states = new ConcurrentDictionary<string, WorkspaceGitState>(StringComparer.OrdinalIgnoreCase);
        var defaults = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lists = new ConcurrentDictionary<string, IReadOnlyList<GitWorktree>?>(PathComparer);
        var work = new List<Func<CancellationToken, Task>>();
        if (readState is not null)
        {
            foreach (var directory in directories)
            {
                if (gitStates.TryGetValue(directory, out var cached) && Fresh(cached.Stamp, now))
                {
                    if (cached.State is { } known)
                    {
                        states[directory] = known;
                    }

                    continue;
                }

                work.Add(async token =>
                {
                    WorkspaceGitState? state = null;
                    try
                    {
                        if (Directory.Exists(directory))
                        {
                            state = await readState(directory, token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                    {
                        Trace.WriteLine($"[tree] could not read the checkout state of '{directory}': {exception.Message}");
                    }

                    gitStates[directory] = (state, Stopwatch.GetTimestamp());
                    if (state is not null)
                    {
                        states[directory] = state;
                    }
                });
            }
        }

        if (readDefault is not null)
        {
            foreach (var project in repositories.Values)
            {
                var root = project.RootPath;
                if (defaultBranches.TryGetValue(root, out var cached) && Fresh(cached.Stamp, now))
                {
                    if (cached.Branch is { } known)
                    {
                        defaults[root] = known;
                    }

                    continue;
                }

                work.Add(async token =>
                {
                    string? branch = null;
                    try
                    {
                        if (Directory.Exists(root))
                        {
                            branch = await readDefault(root, token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                    {
                        Trace.WriteLine($"[tree] could not read the default branch of '{root}': {exception.Message}");
                    }

                    defaultBranches[root] = (branch, Stopwatch.GetTimestamp());
                    if (branch is not null)
                    {
                        defaults[root] = branch;
                    }
                });
            }
        }

        if (readWorktrees is not null)
        {
            foreach (var (root, paths) in linked)
            {
                work.Add(async token => lists[root] = await ReadWorktreeListAsync(readWorktrees, root, paths, now, token)
                    .ConfigureAwait(false));
            }
        }

        if (work.Count > 0)
        {
            await BackgroundRead.RunAsync(
                    token => Parallel.ForEachAsync(
                        work,
                        new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentHeadReads, CancellationToken = token },
                        async (read, inner) => await read(inner).ConfigureAwait(false)),
                    ct)
                .ConfigureAwait(true);
        }

        return new MenuGitReads(states, defaults, lists);
    }

    /// <summary>
    /// One repository's worktree list, off the dispatcher. The cached list is reused while it is fresh, unless a linked
    /// directory appeared that the cached read was not asked about and that it does not name (a workspace made since):
    /// that one sends git back to the list, so a new directory is never reported unregistered from an old answer, while
    /// a directory already judged unregistered waits for the lifetime like every other read. A root that is gone, or a
    /// read that failed, is null: unknown.
    /// </summary>
    private async Task<IReadOnlyList<GitWorktree>?> ReadWorktreeListAsync(
        Func<string, CancellationToken, Task<IReadOnlyList<GitWorktree>>> read,
        string root,
        IReadOnlySet<string> paths,
        long now,
        CancellationToken ct)
    {
        if (worktreeLists.TryGetValue(root, out var cached)
            && Fresh(cached.Stamp, now)
            && (cached.List is null
                || paths.All(path => cached.Judged.Contains(path) || WorktreeList.Find(cached.List, path) is not null || !Directory.Exists(path))))
        {
            return cached.List;
        }

        IReadOnlyList<GitWorktree>? list = null;
        try
        {
            if (Directory.Exists(root))
            {
                list = await read(root, ct).ConfigureAwait(false);
                if (list.Count == 0)
                {
                    // git always lists the main worktree first; an empty answer is not one to judge the workspaces by.
                    Trace.WriteLine($"[tree] the worktree list of '{root}' came back empty; its workspaces stay unverified");
                    list = null;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            Trace.WriteLine($"[tree] could not list the worktrees of '{root}': {exception.Message}");
        }

        if (list is not null)
        {
            // Matched here, off the dispatcher: link-resolved spellings (macOS /var vs /private/var) join the list under
            // Forge's spelling, so the plan compares strings only.
            list = WorktreeList.WithSpellings(list, paths);
        }

        worktreeLists[root] = (list, Stopwatch.GetTimestamp(), paths);
        return list;
    }

    /// <summary>The entry with <paramref name="directory"/>'s spelling (the background read added link-resolved ones).</summary>
    private static GitWorktree? FindWorktree(IReadOnlyList<GitWorktree> list, string directory)
    {
        foreach (var worktree in list)
        {
            if (RemovalFlow.SameDirectory(worktree.Path, directory))
            {
                return worktree;
            }
        }

        return null;
    }

    /// <summary>
    /// An existing linked workspace directory against its repository's worktree list: none when git registers it,
    /// prunable when git says so, unregistered when the list does not name it, unverified when the list could not be read.
    /// No list read at all (no reader wired) leaves the row as it was: none.
    /// </summary>
    internal static (TreeRowMark Mark, string? PrunableReason) WorktreeMark(MenuGitReads git, string root, string directory)
    {
        if (!git.Worktrees.TryGetValue(root, out var list))
        {
            return (TreeRowMark.None, null);
        }

        if (list is null)
        {
            return (TreeRowMark.Unverified, null);
        }

        return FindWorktree(list, directory) switch
        {
            null => (TreeRowMark.Unregistered, null),
            { IsPrunable: true } prunable => (TreeRowMark.Prunable, prunable.PrunableReason),
            _ => (TreeRowMark.None, null),
        };
    }

    /// <summary>
    /// Registered projects whose HEAD read said "not a repository": plain folders in the menus (5.2). A read that failed
    /// or timed out says nothing, so the project keeps its repository entries and the flows check after the click.
    /// </summary>
    private static HashSet<string> PlainProjectsOf(IReadOnlyList<ProjectInfo> all, IReadOnlyDictionary<string, GitHead> heads)
    {
        var plain = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in all)
        {
            if (project.Kind == ProjectKind.Repository
                && heads.TryGetValue(project.RootPath, out var head)
                && head.Kind == GitHeadKind.NotRepository)
            {
                plain.Add(project.Id);
            }
        }

        return plain;
    }

    /// <summary>A registered project whose root is a git repository, as the last refresh found it.</summary>
    private bool IsRepository(ProjectInfo? project) =>
        project is { Kind: ProjectKind.Repository } && !plainProjects.Contains(project.Id);

    private static bool Fresh(long stamp, long now) => Stopwatch.GetElapsedTime(stamp, now) < GitStateLifetime;

    /// <summary>True when <paramref name="branch"/> is the repository's default branch (<c>main</c> or <c>origin/main</c>).</summary>
    internal static bool IsDefaultBranchName(string? branch, string? defaultBranch)
    {
        if (string.IsNullOrEmpty(branch) || string.IsNullOrEmpty(defaultBranch))
        {
            return false;
        }

        if (string.Equals(branch, defaultBranch, StringComparison.Ordinal))
        {
            return true;
        }

        var slash = defaultBranch.IndexOf('/');
        return slash > 0 && string.Equals(branch, defaultBranch[(slash + 1)..], StringComparison.Ordinal);
    }

    /// <summary>The commands only the generated menu reaches (6.2, 7.8, 7.10); the rest are the row's existing commands.</summary>
    private void ApplyMenuCommands(TreeNodeViewModel node, TreeNodePlan plan)
    {
        ICommand? link = null;
        ICommand? replace = null;
        ICommand? renameBranch = null;
        ICommand? continueSession = null;
        switch (plan.Kind)
        {
            case TreeNodeKind.Project when IsRepository(plan.Project):
                var project = plan.Project!;
                link = new AsyncRelayCommand(
                    ct => LinkWorkspace?.Invoke(project, ct) ?? Task.CompletedTask,
                    () => LinkWorkspace is not null,
                    OnFailed);
                break;
            case TreeNodeKind.Worktree when plan.RowMark is TreeRowMark.None or TreeRowMark.Unverified && plan.Project is { } owner:
                var directory = plan.WorktreePath;
                if (plan.ActiveSession is { } holder)
                {
                    var branch = plan.Branch;
                    replace = new AsyncRelayCommand(
                        ct => ReplaceSession?.Invoke(
                                  holder,
                                  new SessionCreate(directory, ProjectStrings.WorktreeSessionTitle())
                                  {
                                      ProjectId = owner.Id,
                                      WorktreePath = directory,
                                      Branch = branch,
                                  },
                                  ct)
                              ?? Task.CompletedTask,
                        () => ReplaceSession is not null && !Blocked(directory) && IsSessionBusy?.Invoke(holder.Id) != true,
                        OnFailed);
                }

                if (IsLinkedWorkspace(plan, owner) && !string.IsNullOrEmpty(plan.Branch))
                {
                    var target = new WorkspaceTarget(owner, directory, plan.Branch, plan.ForkOfBranch);
                    var activeId = plan.ActiveSession?.Id;
                    renameBranch = new AsyncRelayCommand(
                        ct => RenameBranch?.Invoke(target, ct) ?? Task.CompletedTask,
                        () => RenameBranch is not null
                              && !Blocked(directory)
                              && (activeId is null || IsSessionBusy?.Invoke(activeId) != true),
                        OnFailed);
                }

                break;
            case TreeNodeKind.Session when plan.Session is { Status: SessionStatus.Archived, ParentSessionId: null } session:
                var cannotContinue = plan.DirectoryMissing || StaleText(plan.WorkspaceMark, plan.PrunableReason) is not null;
                continueSession = new AsyncRelayCommand(
                    ct => ContinueSession?.Invoke(session.Id, ct) ?? Task.CompletedTask,
                    () => ContinueSession is not null && !cannotContinue,
                    OnFailed);
                break;
        }

        node.SetMenuCommands(link, replace, renameBranch, continueSession);
        node.IsPlainProject = plan.Kind == TreeNodeKind.Project && !IsRepository(plan.Project);
        node.IsSparse = plan.GitState?.IsSparse == true;
        node.MenuState = MenuStateFactory(plan);
    }

    /// <summary>A Forge workspace of a repository project other than its root checkout.</summary>
    private bool IsLinkedWorkspace(TreeNodePlan plan, ProjectInfo project) =>
        !plan.IsMainCheckout
        && IsRepository(project)
        && !RemovalFlow.SameDirectory(plan.WorktreePath, project.RootPath);

    /// <summary>
    /// The snapshot factory of a row's menu: the plan's facts are fixed here, the in-memory flags (busy, handing over,
    /// operation, conflict) are read when the menu opens. Never a git or database call (I3).
    /// </summary>
    private Func<TreeRowState>? MenuStateFactory(TreeNodePlan plan)
    {
        switch (plan.Kind)
        {
            case TreeNodeKind.Project when plan.Project is { } project:
                var projectRow = new TreeRowState(IsRepository(project) ? TreeRowKind.RepositoryProject : TreeRowKind.PlainProject);
                return () => projectRow;
            case TreeNodeKind.Worktree when plan.RowMark == TreeRowMark.Recycled:
                var recycledDirectory = plan.WorktreePath;
                return () => new TreeRowState(TreeRowKind.RecycledWorkspace) { Operating = Operating(recycledDirectory) };
            case TreeNodeKind.Worktree when plan.Project is { } project:
                var directory = plan.WorktreePath;
                var linked = IsLinkedWorkspace(plan, project);
                var holder = plan.ActiveSession;
                var head = linked ? plan.GitState?.Head : plan.HeadKind;
                var facts = new TreeRowState(linked ? TreeRowKind.Workspace : TreeRowKind.RootCheckout)
                {
                    InRepository = IsRepository(project),
                    ForkOfBranch = linked ? plan.ForkOfBranch : null,
                    SessionTitle = holder?.Title,
                    Head = head,
                    HasBranch = !string.IsNullOrEmpty(plan.Branch),
                    IsDefaultBranch = plan.IsDefaultBranch,
                    DirectoryMissing = plan.RowMark == TreeRowMark.Missing,
                    StaleReason = StaleText(plan.RowMark, plan.PrunableReason),
                    MergeOrRebase = plan.GitState?.MergeOrRebase == true,
                };
                return () => facts with
                {
                    Busy = holder is null ? [] : BusyOf(holder.Id),
                    HandingOver = holder is not null && HandingOver(holder.Id),
                    Operating = Operating(directory),
                    Conflicted = Conflicted(directory),
                };
            case TreeNodeKind.Session when plan.Session is { ParentSessionId: null } session:
                var sessionDirectory = DirectoryOf(session);
                var kind = session.Status switch
                {
                    SessionStatus.Active => TreeRowKind.ActiveSession,
                    SessionStatus.Archived => TreeRowKind.HistorySession,
                    SessionStatus.Deleted => TreeRowKind.RemovedSession,
                    _ => TreeRowKind.None,
                };
                if (kind == TreeRowKind.None)
                {
                    return null;
                }

                var sessionFacts = new TreeRowState(kind)
                {
                    InRepository = IsRepository(plan.Project),
                    SessionTitle = session.Title,
                    DirectoryMissing = plan.DirectoryMissing,
                    StaleReason = StaleText(plan.WorkspaceMark, plan.PrunableReason),
                };
                var live = kind == TreeRowKind.ActiveSession;
                return () => sessionFacts with
                {
                    Busy = live ? BusyOf(session.Id) : [],
                    HandingOver = live && HandingOver(session.Id),
                    Operating = Operating(sessionDirectory),
                    Conflicted = Conflicted(sessionDirectory),
                };
            default:
                return null;
        }
    }

    /// <summary>The menu's reason for a workspace git no longer treats as a worktree; null for any other mark.</summary>
    private static string? StaleText(TreeRowMark mark, string? prunableReason) => mark switch
    {
        TreeRowMark.Prunable => LifecycleStrings.WorktreePrunable(prunableReason),
        TreeRowMark.Unregistered => LifecycleStrings.WorktreeUnregistered,
        _ => null,
    };

    private IReadOnlyList<string> BusyOf(string sessionId) => SessionBusyReasons?.Invoke(sessionId) ?? [];

    private bool HandingOver(string sessionId) => IsSessionHandingOver?.Invoke(sessionId) == true;

    private bool Operating(string directory) => IsWorkspaceOperating?.Invoke(directory) == true;

    private bool Conflicted(string directory) => IsWorkspaceConflicted?.Invoke(directory) == true;
}

/// <summary>
/// The git reads of one refresh: checkout states by directory, default branches by repository root, and worktree lists by
/// repository root (a null list is a read that failed; a root that is absent was not read).
/// </summary>
internal sealed record MenuGitReads(
    IReadOnlyDictionary<string, WorkspaceGitState> States,
    IReadOnlyDictionary<string, string> DefaultBranches,
    IReadOnlyDictionary<string, IReadOnlyList<GitWorktree>?> Worktrees)
{
    public static readonly MenuGitReads Empty = new(
        new Dictionary<string, WorkspaceGitState>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, IReadOnlyList<GitWorktree>?>(StringComparer.OrdinalIgnoreCase));
}
