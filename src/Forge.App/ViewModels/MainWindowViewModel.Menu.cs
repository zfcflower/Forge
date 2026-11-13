using System.Diagnostics;
using Forge.Abstractions;
using Forge.App.Lifecycle;
using Forge.App.Localization;
using Forge.Core.Storage;
using Forge.Workspace;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>
/// The tree menus' flows that live in the shell (TASK-session-worktree-lifecycle 5, 7.8, 7.10): "rename branch…",
/// "link existing workspace…", "new session (replace X)…" and "continue this session…". The menus themselves only read
/// in-memory state (5.4 rule 4); every git and store read happens here, after the click.
/// </summary>
internal sealed partial class MainWindowViewModel
{
    /// <summary>Hands a tree the menu callbacks and the live states its menus read; called from <see cref="WireLifecycle"/>.</summary>
    private void WireMenu(ProjectTreeViewModel tree)
    {
        var states = composition.WorkspaceStates;
        var git = composition.Git;
        tree.ReplaceSession = (holder, create, ct) => ReplaceSessionAsync(git, holder, create, ct);
        tree.ContinueSession = ContinueSessionAsync;
        tree.RenameBranch = RenameBranchAsync;
        tree.LinkWorkspace = LinkWorkspaceAsync;
        tree.SessionBusyReasons = id => sessions.TryGetValue(id, out var view) ? view.BusyReasons() : [];
        tree.IsSessionHandingOver = handingOver.Contains;
        tree.IsWorkspaceOperating = states.IsOperating;
        tree.ReadWorkspaceState = (directory, ct) => ReadWorkspaceStateAsync(git, directory, ct);
        tree.ReadDefaultBranch = (root, ct) => git.GetDefaultBranchAsync(root, ct);
        // The raw porcelain list (it throws when git fails): WorktreeService.ListAsync answers "none" for a root git cannot
        // resolve, which would read as "every workspace unregistered" instead of "unknown".
        tree.ReadWorktrees = (root, ct) => git.ListWorktreesAsync(root, ct);
    }

    /// <summary>
    /// "New session (replace X)…" (6.2): the branch the new session records is read from the workspace now, after the
    /// click; the tree's plan only knows the branch the holder was created on, which an outside checkout may have changed.
    /// </summary>
    private async Task ReplaceSessionAsync(GitCli git, SessionInfo holder, SessionCreate create, CancellationToken ct)
    {
        var directory = create.WorktreePath ?? create.WorkspacePath;
        GitHead head;
        try
        {
            head = await BackgroundRead.RunAsync(token => git.GetHeadAsync(directory, token), ct).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            // The handover reads the workspace again and reports what it cannot do; the stored branch stands meanwhile.
            AppLogs.Menu.ZLogWarning($"could not read the HEAD of '{directory}': {exception.Message}");
            head = new GitHead(GitHeadKind.Branch, create.Branch);
        }

        var branch = head.Kind == GitHeadKind.Branch ? head.Branch : null;
        await TakeOverAsync(holder, HandoverIntent.NewSession(create with { Branch = branch }), ct).ConfigureAwait(true);
    }

    /// <summary>HEAD, a running merge / rebase and the sparse flag of one workspace; read at tree refresh, never on menu open.</summary>
    private static async Task<WorkspaceGitState> ReadWorkspaceStateAsync(GitCli git, string directory, CancellationToken ct)
    {
        var head = await git.GetHeadAsync(directory, ct).ConfigureAwait(false);
        if (head.Kind == GitHeadKind.NotRepository)
        {
            return new WorkspaceGitState(head.Kind, false, false);
        }

        var conflict = await git.GetConflictStateAsync(directory, ct).ConfigureAwait(false);
        var sparse = await git.GetSparseConfigAsync(directory, ct).ConfigureAwait(false);
        return new WorkspaceGitState(head.Kind, IsMergeOrRebase(conflict.Operation), sparse is not null);
    }

    private static bool IsMergeOrRebase(GitOperationKind operation) =>
        operation is GitOperationKind.Merge or GitOperationKind.Rebase;

    // ---------------------------------------------------------------- 7.8 rename branch

    /// <summary>
    /// "Rename branch…" (7.8): idle check over the workspace's open sessions and states, the upstream warning, the name
    /// (validated like a new branch, an existing name refused), then - with the workspace held - a re-read of HEAD and of a
    /// running merge / rebase, <c>git branch -m</c> and the catalog transaction. A store failure renames the branch back;
    /// every failure ends in a notice.
    /// </summary>
    internal async Task RenameBranchAsync(WorkspaceTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var title = TreeMenuStrings.RenameBranchTitle;
        if (dialogs is null || target.Branch is not { Length: > 0 } oldName)
        {
            Status.SetWorkspaceProblem(LocText.Literal(RemovalStrings.Unavailable));
            return;
        }

        var directory = target.Directory;
        if (RenameBusy(directory) is { } busy)
        {
            await dialogs.NotifyAsync(title, TreeMenuStrings.RenameBranchBusy([busy])).ConfigureAwait(true);
            return;
        }

        var git = composition.Git;
        string? remote;
        try
        {
            // The menu hides the entry on the default branch from the refresh's read; that read may have failed or be
            // stale, so the flow asks git again before anything is offered.
            var defaultBranch = await BackgroundRead.RunAsync(token => git.GetDefaultBranchAsync(directory, token), ct)
                .ConfigureAwait(true);
            if (ProjectTreeViewModel.IsDefaultBranchName(oldName, defaultBranch))
            {
                await dialogs.NotifyAsync(title, TreeMenuStrings.RenameBranchDefault(oldName)).ConfigureAwait(true);
                return;
            }

            remote = await BackgroundRead.RunAsync(token => git.GetUpstreamRemoteAsync(directory, oldName, token), ct)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            await dialogs.NotifyAsync(title, TreeMenuStrings.RenameBranchFailed(exception.Message)).ConfigureAwait(true);
            return;
        }

        if (remote is not null
            && await dialogs.ChooseAsync(
                    title,
                    TreeMenuStrings.RenameBranchUpstream(oldName, remote),
                    [TreeMenuStrings.RenameBranchContinue, ShellStrings.Cancel])
                .ConfigureAwait(true) != 0)
        {
            return;
        }

        var typed = await dialogs.PromptTextAsync(title, oldName).ConfigureAwait(true);
        var newName = typed?.Trim();
        if (string.IsNullOrEmpty(newName) || string.Equals(newName, oldName, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (!await BackgroundRead.RunAsync(token => git.IsValidBranchNameAsync(directory, newName, token), ct).ConfigureAwait(true))
            {
                await dialogs.NotifyAsync(title, TreeMenuStrings.RenameBranchInvalid(newName)).ConfigureAwait(true);
                return;
            }

            var branches = await BackgroundRead.RunAsync(token => git.GetBranchesAsync(directory, false, token), ct).ConfigureAwait(true);
            if (branches.Any(branch => string.Equals(branch.Name, newName, StringComparison.Ordinal)))
            {
                await dialogs.NotifyAsync(title, TreeMenuStrings.RenameBranchTaken(newName)).ConfigureAwait(true);
                return;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            await dialogs.NotifyAsync(title, TreeMenuStrings.RenameBranchFailed(exception.Message)).ConfigureAwait(true);
            return;
        }

        // The dialogs awaited the user: check again, then hold the workspace so no turn, fork, merge or removal starts
        // while the branch and the records change.
        if (RenameBusy(directory) is { } late)
        {
            await dialogs.NotifyAsync(title, TreeMenuStrings.RenameBranchBusy([late])).ConfigureAwait(true);
            return;
        }

        if (!TryHoldForRemoval(directory, out var held))
        {
            await dialogs.NotifyAsync(title, TreeMenuStrings.RenameBranchBusy([HandoverStrings.BusyWorkspaceOperating])).ConfigureAwait(true);
            return;
        }

        string? notice;
        try
        {
            notice = await RenameHeldAsync(target, oldName, newName).ConfigureAwait(true);
        }
        finally
        {
            ReleaseAfterRemoval(directory, held);
        }

        if (notice is not null)
        {
            await dialogs.NotifyAsync(title, notice).ConfigureAwait(true);
        }

        await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
    }

    /// <summary>The git rename and the catalog transaction with the workspace held; null on success, else the notice.</summary>
    private async Task<string?> RenameHeldAsync(WorkspaceTarget target, string oldName, string newName)
    {
        var git = composition.Git;
        var catalog = composition.Catalog;
        var directory = target.Directory;
        try
        {
            var head = await Task.Run(() => git.GetHeadAsync(directory, CancellationToken.None)).ConfigureAwait(true);
            if (head.Kind != GitHeadKind.Branch || !string.Equals(head.Branch, oldName, StringComparison.Ordinal))
            {
                return TreeMenuStrings.RenameBranchMoved(oldName);
            }

            var conflict = await Task.Run(() => git.GetConflictStateAsync(directory, CancellationToken.None)).ConfigureAwait(true);
            if (IsMergeOrRebase(conflict.Operation))
            {
                return TreeMenuStrings.RenameBranchBusy([TreeMenuStrings.MergeOrRebase]);
            }

            await Task.Run(() => git.RenameBranchAsync(directory, oldName, newName, CancellationToken.None)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return TreeMenuStrings.RenameBranchFailed(exception.Message);
        }

        try
        {
            // No row changed is not a failure: the workspace's sessions may record no branch (linked on a detached HEAD).
            await Task.Run(() => catalog.RenameBranchAsync(target.Project.Id, directory, oldName, newName, CancellationToken.None))
                .ConfigureAwait(true);
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Menu.ZLogWarning($"branch rename records failed for '{directory}': {exception.Message}");
            try
            {
                await Task.Run(() => git.RenameBranchAsync(directory, newName, oldName, CancellationToken.None)).ConfigureAwait(true);
                return TreeMenuStrings.RenameBranchRolledBack(oldName, exception.Message);
            }
            catch (Exception rollback) when (rollback is not OutOfMemoryException)
            {
                return TreeMenuStrings.RenameBranchRollbackFailed(oldName, newName, exception.Message, rollback.Message);
            }
        }
    }

    /// <summary>Why the branch of <paramref name="directory"/> cannot be renamed now (an open session busy or handing over, an operation, a conflict); null when it can.</summary>
    private string? RenameBusy(string directory)
    {
        var key = OccupancyKey.Compute(null, directory);
        var open = sessions.Values
            .Where(view => !view.IsReadOnlyView
                && OccupancyKey.Compute(null, view.Info.WorktreePath ?? view.Info.WorkspacePath) == key)
            .Select(static view => view.Info)
            .ToArray();
        return WorkspaceBusy(directory, open);
    }

    // ---------------------------------------------------------------- 7.10 link existing workspace

    /// <summary>
    /// "Link existing workspace…" (7.10): lists git's worktrees of the project in the background, leaves out the root
    /// checkout and every directory any Forge session (archived and removed ones included) points at, greys out locked
    /// and prunable ones with the reason, and creates a session on the single chosen one through the conditional insert
    /// (an occupied workspace ends in the occupied dialog). A detached HEAD is allowed after a warning.
    /// </summary>
    internal async Task LinkWorkspaceAsync(ProjectInfo project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var title = TreeMenuStrings.LinkTitle;
        if (dialogs is null)
        {
            Status.SetWorkspaceProblem(LocText.Literal(RemovalStrings.Unavailable));
            return;
        }

        IReadOnlyList<GitWorktree> candidates;
        try
        {
            candidates = await BackgroundRead.RunAsync(token => LinkCandidatesAsync(project, token), ct).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            await dialogs.NotifyAsync(title, TreeMenuStrings.LinkFailed(exception.Message)).ConfigureAwait(true);
            return;
        }

        if (candidates.Count == 0)
        {
            await dialogs.NotifyAsync(title, TreeMenuStrings.LinkNone).ConfigureAwait(true);
            return;
        }

        var entries = candidates.Select(LinkEntry).ToArray();
        var index = await dialogs.PickAsync(title, TreeMenuStrings.LinkMessage, entries).ConfigureAwait(true);
        if (index < 0 || index >= candidates.Count || entries[index].DisabledReason is not null)
        {
            return;
        }

        var chosen = candidates[index];
        var path = Path.GetFullPath(chosen.Path);
        if (chosen.IsDetached
            && !await dialogs.ConfirmAsync(title, TreeMenuStrings.LinkDetachedWarning(path), TreeMenuStrings.LinkWorkspace, ShellStrings.Cancel)
                .ConfigureAwait(true))
        {
            return;
        }

        var create = new SessionCreate(path, ProjectStrings.WorktreeSessionTitle())
        {
            ProjectId = project.Id,
            WorktreePath = path,
            Branch = chosen.IsDetached ? null : chosen.Branch,
        };
        var catalog = composition.Catalog;
        SessionInfo created;
        try
        {
            created = await BackgroundRead.RunAsync(token => catalog.CreateAsync(create, token), ct).ConfigureAwait(true);
        }
        catch (WorktreeOccupiedException occupied)
        {
            await ShowOccupiedAsync(occupied, HandoverIntent.NewSession(create), ct).ConfigureAwait(true);
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            await dialogs.NotifyAsync(title, TreeMenuStrings.LinkFailed(exception.Message)).ConfigureAwait(true);
            return;
        }

        try
        {
            await OpenSessionAsync(created, ct).ConfigureAwait(true);
        }
        finally
        {
            // The session exists whether or not its tab opened: the tree shows it either way.
            await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
        }
    }

    /// <summary>The worktrees of <paramref name="project"/> no Forge session points at, the root checkout and bare entries left out.</summary>
    private async Task<IReadOnlyList<GitWorktree>> LinkCandidatesAsync(ProjectInfo project, CancellationToken ct)
    {
        var worktrees = await composition.Worktrees.ListAsync(project.RootPath, ct).ConfigureAwait(false);
        var catalog = composition.Catalog;
        var query = new SessionQuery { ProjectId = project.Id };
        var active = await catalog.QueryAsync(query, ct).ConfigureAwait(false);
        var history = await catalog.ListHistoryAsync(query, ct).ConfigureAwait(false);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in active.Concat(history))
        {
            used.Add(OccupancyKey.Compute(null, session.WorktreePath ?? session.WorkspacePath));
        }

        return worktrees
            .Where(worktree => !worktree.IsBare
                && !RemovalFlow.SameDirectory(worktree.Path, project.RootPath)
                && !used.Contains(OccupancyKey.Compute(null, worktree.Path)))
            .ToArray();
    }

    private static PickEntry LinkEntry(GitWorktree worktree)
    {
        var label = Path.GetFullPath(worktree.Path) + "  ·  "
            + (worktree.IsDetached || worktree.Branch is null ? TreeMenuStrings.LinkDetachedLabel : worktree.Branch);
        var reason = worktree.IsLocked
            ? TreeMenuStrings.LinkLocked(worktree.LockReason)
            : worktree.IsPrunable ? TreeMenuStrings.LinkPrunable(worktree.PrunableReason) : null;
        return new PickEntry(label, reason);
    }
}

/// <summary>One choice of <see cref="IDialogService.PickAsync"/>; a non-null <see cref="DisabledReason"/> greys it out with that reason.</summary>
internal sealed record PickEntry(string Label, string? DisabledReason = null);
