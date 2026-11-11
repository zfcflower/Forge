using System.Diagnostics;
using Forge.Abstractions;
using Forge.App.Lifecycle;
using Forge.App.Localization;
using Forge.Core.Storage;

namespace Forge.App.ViewModels;

/// <summary>
/// Remove session / remove workspace / restore / purge / close project (TASK-session-worktree-lifecycle 7.2-7.7): the
/// dialogs and the in-memory guards (busy, handing over, operation, conflict) live here, the decisions and the disk and
/// store writes in <see cref="RemovalFlow"/>. Every failure ends in a notice; nothing is retried or undone silently.
/// </summary>
internal sealed partial class MainWindowViewModel
{
    /// <summary>Hands a tree the removal callbacks (7.2-7.7); called from <see cref="WireLifecycle"/>.</summary>
    private void WireRemoval(ProjectTreeViewModel tree)
    {
        tree.RemoveSession = RemoveSessionAsync;
        tree.RemoveWorkspace = async (target, ct) => await RemoveWorkspaceAsync(target, target.ForkOfBranch, report: true, ct).ConfigureAwait(true);
        tree.RestoreSession = RestoreSessionAsync;
        tree.RestoreWorkspace = RestoreWorkspaceAsync;
        tree.PurgeSession = PurgeSessionAsync;
        tree.PurgeAll = PurgeAllAsync;
        tree.CloseProject = CloseProjectAsync;
    }

    /// <summary>6.6 step 6 through 7.3: the fork workspace's removal with the source branch as the guard's base.</summary>
    private Task<string?> RemoveForkWorkspaceAsync(ForkRelation relation, string baseBranch, CancellationToken ct) =>
        RemoveWorkspaceAsync(
            new WorkspaceTarget(relation.Project, relation.ForkDirectory, relation.ForkBranch, relation.SourceBranch),
            baseBranch,
            report: false,
            ct);

    /// <summary>
    /// "Remove session…" (7.2): re-read, idle check, the plan off the UI thread (directory fate, guard reasons, sub-agent
    /// count, unmerged forks), one dialog (its button says "remove anyway" when there are reasons), the unsaved-buffer
    /// guard, a second idle check, then the removal with the workspace held (no turn, fork or merge starts under it).
    /// </summary>
    internal async Task RemoveSessionAsync(SessionInfo snapshot, ProjectInfo project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(project);
        var title = RemovalStrings.RemoveSessionTitle;
        if (dialogs is null)
        {
            Status.SetWorkspaceProblem(LocText.Literal(RemovalStrings.Unavailable));
            return;
        }

        var store = composition.Sessions;
        var current = await BackgroundRead.RunAsync(token => store.GetSessionAsync(snapshot.Id, token), ct).ConfigureAwait(true);
        if (current is null || current.Status == SessionStatus.Deleted)
        {
            await dialogs.NotifyAsync(title, RemovalStrings.Changed).ConfigureAwait(true);
            await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
            return;
        }

        if (await ShowRemovalBusyAsync(title, [current]).ConfigureAwait(true))
        {
            return;
        }

        var flow = composition.Removal;
        SessionRemovalPlan plan;
        try
        {
            plan = await Task.Run(() => flow.PlanSessionRemovalAsync(current, project, CancellationToken.None), CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await dialogs.NotifyAsync(title, RemovalStrings.CheckFailed(exception.Message)).ConfigureAwait(true);
            return;
        }

        var message = new List<string> { RemovalStrings.SessionQuestion(current.Title) };
        message.Add(plan.Fate switch
        {
            DirectoryFate.Trash => RemovalStrings.SessionTrash(plan.Directory),
            DirectoryFate.Shared => RemovalStrings.SessionKeptShared,
            DirectoryFate.Missing => RemovalStrings.SessionMissing,
            _ => RemovalStrings.SessionKeptRoot,
        });
        AppendGuardLines(message, plan.Reasons, plan.ChildCount, plan.UnmergedForks);
        var confirm = plan.Reasons.Count > 0 ? RemovalStrings.RemoveAnyway : RemovalStrings.Remove;
        if (await dialogs.ChooseAsync(title, string.Join("\n\n", message), [confirm, ShellStrings.Cancel]).ConfigureAwait(true) != 0)
        {
            return;
        }

        if (!await ConfirmArchiveAsync(current.Id).ConfigureAwait(true)
            || await ShowRemovalBusyAsync(title, [current]).ConfigureAwait(true))
        {
            return;
        }

        RemovalOutcome outcome;
        if (!TryHoldForRemoval(plan.Directory, out var held))
        {
            await dialogs.NotifyAsync(title, RemovalStrings.Busy(HandoverStrings.BusyWorkspaceOperating)).ConfigureAwait(true);
            return;
        }

        try
        {
            if (plan.Fate == DirectoryFate.Trash)
            {
                ReleaseBeforeTrash(plan.Directory);
            }

            outcome = await Task.Run(() => flow.ExecuteSessionRemovalAsync(plan, CancellationToken.None), CancellationToken.None)
                .ConfigureAwait(true);
        }
        finally
        {
            ReleaseAfterRemoval(plan.Directory, held);
        }

        ReleaseRemovedSessions(outcome.RemovedSessionIds);
        if (outcome.Error is { } error)
        {
            await dialogs.NotifyAsync(RemovalStrings.RemoveFailedTitle, error).ConfigureAwait(true);
        }

        await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// "Remove workspace…" (7.3). With an active session X the only way on is "archive X and remove": X is archived by the
    /// archive guard first, and a later failure keeps the archive and says the directory was not removed. A missing
    /// directory still needs one confirmation and only prunes and removes the records. With <paramref name="report"/>
    /// false (the merge-back caller) a failure is returned instead of shown; a cancel returns null.
    /// </summary>
    private async Task<string?> RemoveWorkspaceAsync(WorkspaceTarget target, string? baseBranch, bool report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var title = RemovalStrings.RemoveWorkspaceTitle;
        if (dialogs is null)
        {
            Status.SetWorkspaceProblem(LocText.Literal(RemovalStrings.Unavailable));
            return report ? null : RemovalStrings.Unavailable;
        }

        async Task<string?> FailAsync(string error)
        {
            if (report)
            {
                await dialogs.NotifyAsync(RemovalStrings.RemoveFailedTitle, error).ConfigureAwait(true);
                return null;
            }

            return error;
        }

        var flow = composition.Removal;
        WorkspaceRemovalPlan plan;
        try
        {
            plan = await Task.Run(
                    () => flow.PlanWorkspaceRemovalAsync(target.Project, target.Directory, target.Branch, baseBranch, CancellationToken.None),
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return await FailAsync(RemovalStrings.CheckFailed(exception.Message)).ConfigureAwait(true);
        }

        if (WorkspaceBusy(target.Directory, plan.Sessions) is { } busyBefore)
        {
            return await FailAsync(RemovalStrings.Busy(busyBefore)).ConfigureAwait(true);
        }

        var message = new List<string>
        {
            plan.Missing ? RemovalStrings.WorkspaceMissing : RemovalStrings.WorkspaceTrash(plan.Directory),
        };
        if (plan.Active is { } activeShown)
        {
            message.Add(RemovalStrings.WorkspaceActive(activeShown.Title));
        }

        AppendGuardLines(message, plan.Reasons, 0, plan.UnmergedForks);
        var anyway = plan.Reasons.Count > 0;
        var confirm = plan.Active is { } active
            ? anyway ? RemovalStrings.ArchiveAndRemoveAnyway(active.Title) : RemovalStrings.ArchiveAndRemove(active.Title)
            : anyway ? RemovalStrings.RemoveAnyway : RemovalStrings.Remove;
        if (await dialogs.ChooseAsync(title, string.Join("\n\n", message), [confirm, ShellStrings.Cancel]).ConfigureAwait(true) != 0)
        {
            return null;
        }

        // The unsaved-buffer guard of the archive covers the whole directory, so one call (with any open session of the
        // workspace) protects every editor buffer under it.
        var guardId = plan.Active?.Id ?? plan.Sessions.FirstOrDefault(s => sessions.ContainsKey(s.Id))?.Id;
        if (guardId is not null && !await ConfirmArchiveAsync(guardId).ConfigureAwait(true))
        {
            return null;
        }

        if (WorkspaceBusy(target.Directory, plan.Sessions) is { } busyAfter)
        {
            return await FailAsync(RemovalStrings.Busy(busyAfter)).ConfigureAwait(true);
        }

        if (!TryHoldForRemoval(target.Directory, out var held))
        {
            return await FailAsync(RemovalStrings.Busy(HandoverStrings.BusyWorkspaceOperating)).ConfigureAwait(true);
        }

        RemovalOutcome outcome;
        SessionInfo? archived = null;
        try
        {
            if (plan.Active is { } toArchive)
            {
                var catalog = composition.Catalog;
                try
                {
                    await Task.Run(() => catalog.SetStatusAsync(toArchive.Id, SessionStatus.Archived, CancellationToken.None), CancellationToken.None)
                        .ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    return await FailAsync(RemovalStrings.ArchiveFailed(exception.Message)).ConfigureAwait(true);
                }

                archived = toArchive;
                OnSessionArchived(toArchive.Id);
            }

            if (!plan.Missing)
            {
                ReleaseBeforeTrash(target.Directory);
            }

            outcome = await Task.Run(() => flow.ExecuteWorkspaceRemovalAsync(plan, CancellationToken.None), CancellationToken.None)
                .ConfigureAwait(true);
        }
        finally
        {
            ReleaseAfterRemoval(target.Directory, held);
        }

        ReleaseRemovedSessions(outcome.RemovedSessionIds);
        string? failure = null;
        if (outcome.Error is { } error)
        {
            // The archive is not rolled back (7.3 step 1): say it happened and why the directory stayed.
            failure = await FailAsync(archived is null ? error : RemovalStrings.ArchivedNotRemoved(archived.Title, error)).ConfigureAwait(true);
        }

        if (report)
        {
            await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
        }

        return failure;
    }

    /// <summary>
    /// "Restore…" (7.4): a missing directory is rebuilt first (a worktree from its branch, a scratch folder empty), then
    /// the session goes back through the handover path: <see cref="ISessionCatalog.ActivateAsync"/>, or the occupied
    /// dialog when the workspace has an active session. A rebuilt directory whose session stayed removed says so; the
    /// retry finds the directory and skips the rebuild.
    /// </summary>
    internal async Task RestoreSessionAsync(SessionInfo snapshot, ProjectInfo project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(project);
        var title = RemovalStrings.RestoreTitle;
        var store = composition.Sessions;
        var current = await BackgroundRead.RunAsync(token => store.GetSessionAsync(snapshot.Id, token), ct).ConfigureAwait(true);
        if (current is not { Status: SessionStatus.Deleted })
        {
            await NotifyOrReportAsync(title, RemovalStrings.Changed).ConfigureAwait(true);
            await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
            return;
        }

        var directory = RemovalFlow.DirectoryOf(current);
        var (rebuilt, rebuildError) = await RebuildHeldAsync(current, project, directory).ConfigureAwait(true);
        if (rebuildError is not null)
        {
            await NotifyOrReportAsync(RemovalStrings.RestoreFailedTitle, rebuildError).ConfigureAwait(true);
            await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
            return;
        }

        string? reason = null;
        var catalog = composition.Catalog;
        try
        {
            if (!await BackgroundRead.RunAsync(token => catalog.ActivateAsync(current.Id, token), CancellationToken.None).ConfigureAwait(true))
            {
                reason = RemovalStrings.Changed;
            }
        }
        catch (WorktreeOccupiedException occupied)
        {
            // Never a direct SetStatus(Active): the workspace's active session is handed over to this one (6.2).
            await ShowOccupiedAsync(occupied, HandoverIntent.Resume(current.Id), CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            reason = exception.Message;
        }

        var after = await BackgroundRead.RunAsync(token => store.GetSessionAsync(current.Id, token), CancellationToken.None).ConfigureAwait(true);
        if (after is { Status: SessionStatus.Active })
        {
            await OpenSessionByIdAsync(current.Id, CancellationToken.None).ConfigureAwait(true);
            if (rebuilt && project.Kind == ProjectKind.Repository)
            {
                await NotifyOrReportAsync(title, RemovalStrings.RebuiltWorktree(directory)).ConfigureAwait(true);
            }
        }
        else if (rebuilt)
        {
            await NotifyOrReportAsync(
                    RemovalStrings.RestoreFailedTitle,
                    reason is null ? RemovalStrings.NotActivatedCancelled : RemovalStrings.NotActivated(reason))
                .ConfigureAwait(true);
        }
        else if (reason is not null)
        {
            await NotifyOrReportAsync(RemovalStrings.RestoreFailedTitle, reason).ConfigureAwait(true);
        }

        await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
    }

    /// <summary>"Restore workspace…" (7.5): rebuilds the directory only; its sessions stay removed until restored one by one.</summary>
    internal async Task RestoreWorkspaceAsync(WorkspaceTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var removed = await RemovedRootsAtAsync(target, ct).ConfigureAwait(true);
        var probe = removed.FirstOrDefault();
        if (probe is null)
        {
            await NotifyOrReportAsync(RemovalStrings.RestoreFailedTitle, RemovalStrings.Changed).ConfigureAwait(true);
            await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
            return;
        }

        var (rebuilt, error) = await RebuildHeldAsync(probe, target.Project, target.Directory).ConfigureAwait(true);
        await NotifyOrReportAsync(
                error is null ? RemovalStrings.RestoreTitle : RemovalStrings.RestoreFailedTitle,
                error ?? (rebuilt ? RemovalStrings.WorkspaceRestored(target.Directory) : RemovalStrings.WorkspacePresent(target.Directory)))
            .ConfigureAwait(true);
        await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
    }

    /// <summary>"Delete permanently…" (7.5): "cannot be undone", then the purge; the inheritance guard's refusal names the heirs.</summary>
    internal async Task PurgeSessionAsync(SessionInfo snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var title = RemovalStrings.PurgeTitle;
        if (dialogs is null)
        {
            Status.SetWorkspaceProblem(LocText.Literal(RemovalStrings.Unavailable));
            return;
        }

        if (!await dialogs.ConfirmAsync(title, RemovalStrings.PurgeQuestion(snapshot.Title), RemovalStrings.PurgeConfirm, ShellStrings.Cancel)
                .ConfigureAwait(true))
        {
            return;
        }

        var flow = composition.Removal;
        var outcome = await Task.Run(() => flow.PurgeAsync(snapshot, CancellationToken.None), CancellationToken.None).ConfigureAwait(true);
        ReleaseRemovedSessions(outcome.PurgedIds);
        var notes = PurgeNotes(snapshot, outcome);
        if (notes.Count > 0)
        {
            await dialogs.NotifyAsync(title, string.Join("\n\n", notes)).ConfigureAwait(true);
        }

        await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// "Delete all sessions permanently…" (7.5): every removed root session of the workspace, in passes until no more can
    /// go (an heir purged later frees the one it inherited from); the ones the inheritance guard still keeps are listed.
    /// </summary>
    internal async Task PurgeAllAsync(WorkspaceTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var title = RemovalStrings.PurgeTitle;
        if (dialogs is null)
        {
            Status.SetWorkspaceProblem(LocText.Literal(RemovalStrings.Unavailable));
            return;
        }

        var pending = (await RemovedRootsAtAsync(target, ct).ConfigureAwait(true)).ToList();
        if (pending.Count == 0)
        {
            await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
            return;
        }

        if (!await dialogs.ConfirmAsync(title, RemovalStrings.PurgeAllQuestion(pending.Count), RemovalStrings.PurgeConfirm, ShellStrings.Cancel)
                .ConfigureAwait(true))
        {
            return;
        }

        var flow = composition.Removal;
        var notes = new List<string>();
        var last = new Dictionary<string, PurgeOutcome>(StringComparer.Ordinal);
        bool progressed;
        do
        {
            progressed = false;
            foreach (var session in pending.ToArray())
            {
                var outcome = await Task.Run(() => flow.PurgeAsync(session, CancellationToken.None), CancellationToken.None).ConfigureAwait(true);
                if (outcome.Purged)
                {
                    progressed = true;
                    pending.Remove(session);
                    last.Remove(session.Id);
                    ReleaseRemovedSessions(outcome.PurgedIds);
                    if (outcome.Warning is { } warning)
                    {
                        notes.Add(warning);
                    }
                }
                else
                {
                    last[session.Id] = outcome;
                    if (outcome.Error is not null)
                    {
                        // A store failure is not an inheritance refusal: it is reported once and not retried.
                        pending.Remove(session);
                    }
                }
            }
        }
        while (progressed && pending.Count > 0);

        foreach (var (id, outcome) in last)
        {
            if (outcome.Error is { } error)
            {
                notes.Add(error);
            }
            else if (outcome.InheritingTitles.Count > 0)
            {
                var session = pending.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
                notes.Add(RemovalStrings.PurgeRefused(session?.Title ?? id, outcome.InheritingTitles));
            }
        }

        if (notes.Count > 0)
        {
            await dialogs.NotifyAsync(title, string.Join("\n\n", notes.Distinct(StringComparer.Ordinal))).ConfigureAwait(true);
        }

        await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// "Close project…" (7.7), the soft close: refused while any session of the project is busy, handing over, or in an
    /// operation / conflicted workspace; otherwise its runtimes and tabs are released and the registration is closed.
    /// Session states stay as they are, so adding the same path again brings the tree back unchanged.
    /// </summary>
    internal async Task CloseProjectAsync(ProjectInfo project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var title = RemovalStrings.CloseTitle;
        if (dialogs is null)
        {
            Status.SetWorkspaceProblem(LocText.Literal(RemovalStrings.Unavailable));
            return;
        }

        if (!await dialogs.ConfirmAsync(title, RemovalStrings.CloseQuestion(project.Name), RemovalStrings.CloseConfirm, ShellStrings.Cancel)
                .ConfigureAwait(true))
        {
            return;
        }

        var catalog = composition.Catalog;
        IReadOnlyList<SessionInfo> members;
        try
        {
            members = await BackgroundRead.RunAsync(
                    token => catalog.QueryAsync(new SessionQuery { ProjectId = project.Id }, token),
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            await dialogs.NotifyAsync(title, RemovalStrings.CloseFailed(exception.Message)).ConfigureAwait(true);
            return;
        }

        if (SessionsBusy(members) is { } busy)
        {
            await dialogs.NotifyAsync(title, RemovalStrings.CloseBusy(busy)).ConfigureAwait(true);
            return;
        }

        foreach (var member in members)
        {
            if (sessions.ContainsKey(member.Id) && !await ConfirmArchiveAsync(member.Id).ConfigureAwait(true))
            {
                return;
            }
        }

        // The buffer guards awaited dialogs: check again on the UI thread, which owns every busy source.
        if (SessionsBusy(members) is { } busyAfter)
        {
            await dialogs.NotifyAsync(title, RemovalStrings.CloseBusy(busyAfter)).ConfigureAwait(true);
            return;
        }

        // The project's live tabs are held while the registry write runs (no turn starts under it); the tabs and runtimes
        // are released only once the project is closed, so a failed close leaves the window as it was.
        var held = new List<SessionWorkspaceViewModel>();
        foreach (var member in members)
        {
            if (sessions.TryGetValue(member.Id, out var view) && !view.IsReadOnlyView && handingOver.Add(member.Id))
            {
                view.IsHandingOver = true;
                held.Add(view);
            }
        }

        string? failure = null;
        var projects = composition.Projects;
        try
        {
            if (!await Task.Run(() => projects.CloseAsync(project.Id, CancellationToken.None), CancellationToken.None).ConfigureAwait(true))
            {
                failure = RemovalStrings.CloseFailed(RemovalStrings.Changed);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = RemovalStrings.CloseFailed(exception.Message);
        }
        finally
        {
            ReleaseWorkspaceSessions(held);
        }

        if (failure is not null)
        {
            await dialogs.NotifyAsync(title, failure).ConfigureAwait(true);
        }
        else
        {
            foreach (var member in members)
            {
                if (sessions.ContainsKey(member.Id) || openTabs.Contains(member.Id))
                {
                    OnSessionArchived(member.Id);
                }
                else
                {
                    composition.Runtimes.Release(member.Id);
                }
            }
        }

        await RefreshTreesAfterLifecycleAsync().ConfigureAwait(true);
    }

    private static void AppendGuardLines(List<string> message, IReadOnlyList<string> reasons, int childCount, IReadOnlyList<string> unmergedForks)
    {
        if (reasons.Count > 0)
        {
            // The guard's reasons are shown verbatim (7.2 step 2).
            message.Add(RemovalStrings.Reasons + "\n" + string.Join("\n", reasons.Select(static r => "• " + r)));
        }

        if (childCount > 0)
        {
            message.Add(RemovalStrings.Children(childCount));
        }

        if (unmergedForks.Count > 0)
        {
            message.Add(RemovalStrings.UnmergedForks(unmergedForks));
        }
    }

    /// <summary>Shows why one of <paramref name="candidates"/> cannot be removed now; true when it did.</summary>
    private async Task<bool> ShowRemovalBusyAsync(string title, IReadOnlyList<SessionInfo> candidates)
    {
        if (SessionsBusy(candidates) is not { } busy)
        {
            return false;
        }

        await dialogs!.NotifyAsync(title, RemovalStrings.Busy(busy)).ConfigureAwait(true);
        return true;
    }

    /// <summary>"title: reason, reason" for every busy session (section 2, idle, plus the workspace states); null when none is.</summary>
    private string? SessionsBusy(IEnumerable<SessionInfo> candidates)
    {
        var lines = new List<string>();
        foreach (var candidate in candidates)
        {
            sessions.TryGetValue(candidate.Id, out var view);
            var reasons = HandoverBusyReasons(candidate, view);
            if (reasons.Count > 0)
            {
                lines.Add(candidate.Title + ": " + string.Join(", ", reasons));
            }
        }

        return lines.Count == 0 ? null : string.Join("; ", lines);
    }

    private string? WorkspaceBusy(string directory, IReadOnlyList<SessionInfo> roots)
    {
        if (SessionsBusy(roots) is { } busy)
        {
            return busy;
        }

        var states = composition.WorkspaceStates;
        if (states.IsOperating(directory))
        {
            return HandoverStrings.BusyWorkspaceOperating;
        }

        return states.IsConflicted(directory) ? HandoverStrings.BusyMergeConflict : null;
    }

    /// <summary>
    /// The workspace in the "operation in progress" state with its live tabs held (no turn, fork, merge or sync starts on
    /// it) for the removal; false when another operation already runs there.
    /// </summary>
    private bool TryHoldForRemoval(string directory, out List<SessionWorkspaceViewModel> held)
    {
        held = [];
        if (!composition.WorkspaceStates.TryBeginOperation(directory))
        {
            return false;
        }

        held = HoldWorkspaceSessions(directory);
        NotifyTreeCommandStates();
        return true;
    }

    private void ReleaseAfterRemoval(string directory, List<SessionWorkspaceViewModel> held)
    {
        ReleaseWorkspaceSessions(held);
        composition.WorkspaceStates.EndOperation(directory);
        NotifyTreeCommandStates();
    }

    /// <summary>
    /// Just before a directory goes to the trash, every session view of that workspace leaves the window like an archived
    /// one (its terminals ended, tab closed, runtime released), so no shell or handle of Forge's own keeps the directory
    /// locked and fails the move. The idle checks already passed and the buffer guard ran, so no queued message or unsaved
    /// edit is lost; if the trash still fails, the records stay and the session opens again from the tree.
    /// </summary>
    private void ReleaseBeforeTrash(string directory)
    {
        var key = OccupancyKey.Compute(null, directory);
        var ids = sessions.Values
            .Where(view => OccupancyKey.Compute(null, view.Info.WorktreePath ?? view.Info.WorkspacePath) == key)
            .Select(static view => view.SessionId)
            .ToArray();
        foreach (var id in ids)
        {
            OnSessionArchived(id);
        }
    }

    /// <summary>Removed or purged sessions (sub-agent sessions included) leave the window like archived ones: tab closed, view and runtime released.</summary>
    private void ReleaseRemovedSessions(IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
        {
            OnSessionArchived(id);
        }
    }

    /// <summary>The rebuild of 7.4 step 1 with the directory in the "operation in progress" state while git runs.</summary>
    private async Task<(bool Rebuilt, string? Error)> RebuildHeldAsync(SessionInfo session, ProjectInfo project, string directory)
    {
        var states = composition.WorkspaceStates;
        if (!states.TryBeginOperation(directory))
        {
            return (false, RemovalStrings.Busy(HandoverStrings.BusyWorkspaceOperating));
        }

        NotifyTreeCommandStates();
        try
        {
            var flow = composition.Removal;
            return await Task.Run(() => flow.RebuildDirectoryAsync(session, project, CancellationToken.None), CancellationToken.None)
                .ConfigureAwait(true);
        }
        finally
        {
            states.EndOperation(directory);
            NotifyTreeCommandStates();
        }
    }

    /// <summary>The removed (Deleted) root sessions whose directory is <paramref name="target"/>'s, read after the click.</summary>
    private async Task<IReadOnlyList<SessionInfo>> RemovedRootsAtAsync(WorkspaceTarget target, CancellationToken ct)
    {
        var catalog = composition.Catalog;
        var key = OccupancyKey.Compute(target.Directory, target.Directory);
        try
        {
            var history = await BackgroundRead.RunAsync(
                    token => catalog.ListHistoryAsync(new SessionQuery { ProjectId = target.Project.Id }, token),
                    ct)
                .ConfigureAwait(true);
            return history
                .Where(s => s.ParentSessionId is null
                            && s.Status == SessionStatus.Deleted
                            && string.Equals(OccupancyKey.Compute(s.WorktreePath, s.WorkspacePath), key, StringComparison.Ordinal))
                .ToArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Trace.WriteLine("[lifecycle] could not list the removed sessions: " + exception.Message);
            await NotifyOrReportAsync(RemovalStrings.PurgeTitle, exception.Message).ConfigureAwait(true);
            return [];
        }
    }

    private static List<string> PurgeNotes(SessionInfo session, PurgeOutcome outcome)
    {
        var notes = new List<string>();
        if (outcome.InheritingTitles.Count > 0)
        {
            notes.Add(RemovalStrings.PurgeRefused(session.Title, outcome.InheritingTitles));
        }

        if (outcome.Error is { } error)
        {
            notes.Add(error);
        }

        if (outcome.Warning is { } warning)
        {
            notes.Add(warning);
        }

        return notes;
    }
}
