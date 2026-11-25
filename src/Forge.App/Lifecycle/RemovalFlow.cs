using System.Diagnostics;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.Core.Storage;
using Forge.IO;
using Forge.Workspace;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Lifecycle;

/// <summary>The git reads and writes the removal, restore and purge flows (7.2-7.5) need; real or substituted.</summary>
internal interface IRemovalGit
{
    /// <summary>The reasons removing the worktree would discard work (<see cref="WorktreeManager.CheckRemovableAsync"/>), verbatim.</summary>
    Task<IReadOnlyList<string>> CheckRemovableAsync(string repositoryRoot, string directory, string? baseBranch, CancellationToken ct);

    /// <summary>
    /// The commit half of the guard asked from the repository, for a worktree git cannot run inside
    /// (<see cref="WorktreeManager.CheckCommitsAsync"/>), verbatim.
    /// </summary>
    Task<IReadOnlyList<string>> CheckCommitsAsync(string repositoryRoot, string head, bool detached, string? baseBranch, CancellationToken ct);

    Task PruneAsync(string repositoryRoot, CancellationToken ct);

    Task<bool> IsAncestorAsync(string repositoryRoot, string ancestor, string descendant, CancellationToken ct);

    Task<bool> BranchExistsAsync(string repositoryRoot, string branch, CancellationToken ct);

    /// <summary><c>git worktree add &lt;directory&gt; &lt;branch&gt;</c> of an existing branch (no <c>-b</c>).</summary>
    Task AttachWorktreeAsync(string repositoryRoot, string directory, string branch, CancellationToken ct);

    /// <summary><c>git worktree list --porcelain</c> of the repository; throws when git fails.</summary>
    Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string repositoryRoot, CancellationToken ct);
}

/// <summary>The removal git over the real <see cref="GitCli"/> and <see cref="WorktreeManager"/>.</summary>
internal sealed class RemovalGit(GitCli git, WorktreeManager manager) : IRemovalGit
{
    public Task<IReadOnlyList<string>> CheckRemovableAsync(string repositoryRoot, string directory, string? baseBranch, CancellationToken ct) =>
        manager.CheckRemovableAsync(repositoryRoot, directory, baseBranch, ct);

    public Task<IReadOnlyList<string>> CheckCommitsAsync(string repositoryRoot, string head, bool detached, string? baseBranch, CancellationToken ct) =>
        manager.CheckCommitsAsync(repositoryRoot, head, detached, baseBranch, ct);

    public Task PruneAsync(string repositoryRoot, CancellationToken ct) => manager.PruneAsync(repositoryRoot, ct);

    public Task<bool> IsAncestorAsync(string repositoryRoot, string ancestor, string descendant, CancellationToken ct) =>
        git.IsAncestorAsync(repositoryRoot, ancestor, descendant, ct);

    public async Task<bool> BranchExistsAsync(string repositoryRoot, string branch, CancellationToken ct) =>
        await git.RevParseAsync(repositoryRoot, "refs/heads/" + branch, ct).ConfigureAwait(false) is not null;

    public async Task AttachWorktreeAsync(string repositoryRoot, string directory, string branch, CancellationToken ct) =>
        await git.AttachWorktreeAsync(repositoryRoot, directory, branch, ct).ConfigureAwait(false);

    public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string repositoryRoot, CancellationToken ct) =>
        git.ListWorktreesAsync(repositoryRoot, ct);
}

/// <summary>Where a worktree directory stands in its repository's <c>git worktree list --porcelain</c>.</summary>
internal enum WorktreeRegistration
{
    /// <summary>The list could not be read, or came back empty: unknown, judged neither healthy nor stale.</summary>
    Unknown,

    /// <summary>Listed and not prunable.</summary>
    Registered,

    /// <summary>Listed, but git marks it prunable (its <c>.git</c> link or gitdir is broken).</summary>
    Prunable,

    /// <summary>Not in the list: the folder exists, but git does not know it as a worktree.</summary>
    Unregistered,
}

/// <summary>Matching git's worktree list against Forge's spelling of a directory.</summary>
internal static class WorktreeList
{
    /// <summary>
    /// The entry naming <paramref name="directory"/>. Spellings are compared first; when none matches, the links of each
    /// spelling are resolved too (macOS <c>/var</c> is <c>/private/var</c>, and git prints the resolved path), which reads
    /// the file system: call it off the dispatcher.
    /// </summary>
    public static GitWorktree? Find(IReadOnlyList<GitWorktree> list, string directory)
    {
        ArgumentNullException.ThrowIfNull(list);
        foreach (var worktree in list)
        {
            if (RemovalFlow.SameDirectory(worktree.Path, directory))
            {
                return worktree;
            }
        }

        foreach (var worktree in list)
        {
            if (SamePathSafe(worktree.Path, directory))
            {
                return worktree;
            }
        }

        return null;
    }

    /// <summary>Where <paramref name="directory"/> stands in <paramref name="list"/>; a null or empty list is unknown.</summary>
    public static (WorktreeRegistration State, string? PrunableReason) Judge(IReadOnlyList<GitWorktree>? list, string directory)
    {
        if (list is not { Count: > 0 })
        {
            return (WorktreeRegistration.Unknown, null);
        }

        return Find(list, directory) switch
        {
            null => (WorktreeRegistration.Unregistered, null),
            { IsPrunable: true } prunable => (WorktreeRegistration.Prunable, prunable.PrunableReason),
            _ => (WorktreeRegistration.Registered, null),
        };
    }

    /// <summary>
    /// <paramref name="list"/> plus a copy, under Forge's spelling, of each entry that names one of
    /// <paramref name="directories"/> only after link resolution; afterwards a plain spelling comparison finds them all,
    /// so the dispatcher never touches the file system to match.
    /// </summary>
    public static IReadOnlyList<GitWorktree> WithSpellings(IReadOnlyList<GitWorktree> list, IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(directories);
        List<GitWorktree>? aliased = null;
        foreach (var directory in directories)
        {
            if (list.Any(worktree => RemovalFlow.SameDirectory(worktree.Path, directory)))
            {
                continue;
            }

            if (Find(list, directory) is { } same)
            {
                (aliased ??= [.. list]).Add(same with { Path = directory });
            }
        }

        return aliased ?? list;
    }

    internal static bool SamePathSafe(string left, string right)
    {
        try
        {
            return WorkspacePath.SamePath(left, right);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>What a removal guard reason says; the re-check before the trash compares reasons by it.</summary>
internal enum GuardReasonKind
{
    /// <summary>Work git found that the removal would discard (uncommitted changes, unmerged or unreferenced commits), verbatim.</summary>
    Change,

    /// <summary>Git marks the worktree prunable, so its changes cannot be checked.</summary>
    Prunable,

    /// <summary>Git does not list the folder, so its changes cannot be checked.</summary>
    Unregistered,

    /// <summary>The worktree list could not be read (or was empty): the folder could not be confirmed as a worktree.</summary>
    Unverified,

    /// <summary>Git could not run inside the worktree; its commits were checked from the repository, its changes were not.</summary>
    ChangesUnchecked,

    /// <summary>Git could not run inside the worktree, and neither changes nor commits could be checked.</summary>
    CheckFailed,
}

/// <summary>One removal guard reason: the text the dialog lists, and what it is about.</summary>
internal sealed record GuardReason(string Text, GuardReasonKind Kind)
{
    public static GuardReason Change(string text) => new(text, GuardReasonKind.Change);

    /// <summary>
    /// Whether this reason, shown in the dialog, already told the user about <paramref name="now"/>, found by the re-check.
    /// Found work matches by its text (more work added meanwhile is new). The other kinds embed git's wording, which can
    /// change between two runs without anything changing for the user, so they match by kind; "changes unchecked" is also
    /// covered by the broader "nothing could be checked".
    /// </summary>
    public bool Covers(GuardReason now)
    {
        ArgumentNullException.ThrowIfNull(now);
        return now.Kind switch
        {
            GuardReasonKind.Change => Kind == GuardReasonKind.Change && string.Equals(Text, now.Text, StringComparison.Ordinal),
            GuardReasonKind.ChangesUnchecked => Kind is GuardReasonKind.ChangesUnchecked or GuardReasonKind.CheckFailed,
            _ => Kind == now.Kind,
        };
    }
}

/// <summary>
/// "Remove workspace…" read off the UI thread (7.3): the workspace's root sessions (every state, history included), its
/// active session (archived first by "archive X and remove"), whether the directory is already gone, and the guard reasons.
/// </summary>
internal sealed record WorkspaceRemovalPlan(
    ProjectInfo Project,
    string Directory,
    string? Branch,
    string? BaseBranch,
    bool Missing,
    IReadOnlyList<GuardReason> Guard,
    IReadOnlyList<SessionInfo> Sessions,
    SessionInfo? Active,
    IReadOnlyList<string> UnmergedForks)
{
    /// <summary>The guard reasons as the dialog lists them.</summary>
    public IReadOnlyList<string> Reasons => [.. Guard.Select(static reason => reason.Text)];
}

/// <summary>
/// What a removal did: the removed ids (so the shell can close their tabs), a cleanup warning and a hard error. A removal
/// that reports <see cref="Error"/> may still have removed sessions.
/// </summary>
internal sealed record RemovalOutcome(
    IReadOnlyList<string> RemovedSessionIds,
    string? Warning,
    string? Error)
{
    /// <summary>
    /// The workspace directory is gone (it was already missing, or the trash took it): a later failure is about the
    /// sessions, not about a directory that stayed behind. A session removal never moves a directory and leaves this false.
    /// </summary>
    public bool DirectoryGone { get; init; }

    public static RemovalOutcome Failed(string error) => new([], null, error);
}

/// <summary>
/// Removal, restore and purge (TASK-session-worktree-lifecycle 7.2-7.5) without any dialog: the shell asks, this decides
/// and acts. A session removal never touches its directory or branch: it purges the session (and its descendants) and the
/// files kept outside the database, directly. A workspace removal recycles the directory (the trash is the only way a
/// directory is removed - no <c>git worktree remove</c>, no forced or direct delete - and never for a repository or
/// project root) and then removes every session of it through the same purge.
/// </summary>
internal sealed class RemovalFlow(
    ISessionCatalog catalog,
    ISessionStore store,
    IRemovalGit git,
    Func<string, CancellationToken, Task> moveToTrash,
    Func<string, CancellationToken, Task> cleanUpPurged)
{
    /// <summary>
    /// The one directory-ownership decision (section 2): a repository worktree other than the root checkout and a general
    /// scratch folder are Forge's to recycle; the root checkout, a non-repository project root and the general root never.
    /// </summary>
    public static bool IsForgeOwned(SessionInfo session, ProjectInfo project)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(project);
        var directory = DirectoryOf(session);
        if (SameDirectory(directory, project.RootPath))
        {
            return false;
        }

        return project.Kind == ProjectKind.General || !string.IsNullOrEmpty(session.WorktreePath);
    }

    public static string DirectoryOf(SessionInfo session) => session.WorktreePath ?? session.WorkspacePath;

    public static bool SameDirectory(string left, string right) =>
        string.Equals(Key(left), Key(right), StringComparison.Ordinal);

    /// <summary>
    /// How many sub-agent sessions removing <paramref name="session"/> takes with it, read off the UI thread (7.2) for the
    /// dialog's "N sub-agent sessions are removed with it" line.
    /// </summary>
    public async Task<int> CountRemovalDescendantsAsync(SessionInfo session, ProjectInfo project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(project);
        var all = await AllSessionsAsync(project.Id, ct).ConfigureAwait(false);
        return Descendants(session.Id, all).Count;
    }

    /// <summary>
    /// 7.2 after the dialog: a history session (Archived / Deleted) is purged with its descendants and every file kept
    /// outside the database. The directory and the branch are not touched, so nothing has to be guarded or recycled; a
    /// session that became Active again, or is gone, removes nothing.
    /// </summary>
    public async Task<RemovalOutcome> RemoveSessionAsync(SessionInfo session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var current = await store.GetSessionAsync(session.Id, ct).ConfigureAwait(false);
        if (current is null)
        {
            return RemovalOutcome.Failed(RemovalStrings.Changed);
        }

        if (current.Status == SessionStatus.Active)
        {
            return RemovalOutcome.Failed(RemovalStrings.ArchiveFirst);
        }

        var (removed, warning, error) = await PurgeAsync(current, ct).ConfigureAwait(false);
        return new RemovalOutcome(removed, warning, error);
    }

    /// <param name="baseBranch">
    /// The guard's base branch: the workspace's fork source (5.4 rule 6), or the branch a merge back just went into.
    /// </param>
    public async Task<WorkspaceRemovalPlan> PlanWorkspaceRemovalAsync(
        ProjectInfo project,
        string directory,
        string? branch,
        string? baseBranch,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!IsRemovableWorkspace(project, directory))
        {
            throw new InvalidOperationException(RemovalStrings.RootRefused(directory));
        }

        var all = await AllSessionsAsync(project.Id, ct).ConfigureAwait(false);
        var roots = RootsAt(directory, all);
        var missing = !System.IO.Directory.Exists(directory);
        IReadOnlyList<GuardReason> reasons = missing
            ? []
            : await GuardAsync(project.RootPath, directory, baseBranch, ct).ConfigureAwait(false);
        var forks = branch is { Length: > 0 }
            ? await UnmergedForksAsync(project, branch, directory, all, ct).ConfigureAwait(false)
            : [];
        return new WorkspaceRemovalPlan(
            project,
            directory,
            branch,
            baseBranch,
            missing,
            reasons,
            roots,
            roots.FirstOrDefault(static s => s.Status == SessionStatus.Active),
            forks);
    }

    /// <summary>
    /// 7.3 steps 2-4 after the dialog (and after "archive X" when it was chosen): recycle the directory unless it is gone,
    /// prune, then remove every root session of the path (their sub-agent sessions with them) through the one session
    /// cleanup. The removal order follows the inheritance guard: a session that still has an heir in the set is kept for a
    /// later round, and rounds repeat while they make progress, so an heir is purged before the session it inherits from.
    /// An Active session found now (one opened while the dialog was up) removes nothing.
    /// </summary>
    public async Task<RemovalOutcome> ExecuteWorkspaceRemovalAsync(WorkspaceRemovalPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Red line: a repository root or project root never goes to the trash, whoever built the plan.
        if (!IsRemovableWorkspace(plan.Project, plan.Directory))
        {
            return RemovalOutcome.Failed(RemovalStrings.RootRefused(plan.Directory));
        }

        var all = await AllSessionsAsync(plan.Project.Id, ct).ConfigureAwait(false);
        var roots = RootsAt(plan.Directory, all);
        if (roots.Any(static s => s.Status == SessionStatus.Active))
        {
            return RemovalOutcome.Failed(RemovalStrings.Changed);
        }

        var missing = !System.IO.Directory.Exists(plan.Directory);
        if (plan.Missing && !missing)
        {
            return RemovalOutcome.Failed(RemovalStrings.Changed);
        }

        if (!missing
            && await NewGuardReasonsAsync(plan.Project.RootPath, plan.Directory, plan.BaseBranch, plan.Guard, ct).ConfigureAwait(false) is { } grown)
        {
            return RemovalOutcome.Failed(grown);
        }

        if (await RecycleWorkspaceAsync(plan.Project.RootPath, plan.Directory, missing, ct).ConfigureAwait(false) is { } failure)
        {
            return RemovalOutcome.Failed(failure);
        }

        var outcome = await RemoveWorkspaceSessionsAsync(roots, ct).ConfigureAwait(false);
        return outcome with { DirectoryGone = true };
    }

    /// <summary>
    /// The session half of a workspace removal: every root session of the directory, newest first, through
    /// <see cref="PurgeAsync"/>. A store failure is reported once and not retried. Nothing here touches the directory.
    /// </summary>
    private async Task<RemovalOutcome> RemoveWorkspaceSessionsAsync(IReadOnlyList<SessionInfo> roots, CancellationToken ct)
    {
        if (roots.Count == 0)
        {
            return new RemovalOutcome([], null, null);
        }

        var removed = new List<string>();
        var warnings = new List<string>();
        var errors = new List<string>();
        foreach (var session in roots.OrderByDescending(static s => s.LastActiveAt))
        {
            var (purged, warning, failure) = await PurgeAsync(session, ct).ConfigureAwait(false);
            removed.AddRange(purged);
            if (warning is not null)
            {
                warnings.Add(warning);
            }

            if (failure is not null)
            {
                errors.Add(failure);
            }
        }

        return new RemovalOutcome(
            removed,
            warnings.Count == 0 ? null : string.Join("\n", warnings.Distinct(StringComparer.Ordinal)),
            errors.Count == 0 ? null : string.Join("\n", errors.Distinct(StringComparer.Ordinal)));
    }

    /// <summary>
    /// 7.4 step 1 / 7.5 "restore workspace": rebuilds a missing directory. A worktree comes back with
    /// <c>git worktree add &lt;path&gt; &lt;branch&gt;</c> (after a prune of the stale registration), a general scratch
    /// folder as an empty folder; a root checkout or project root is never rebuilt. Returns (rebuilt, error); an existing
    /// directory is (false, null).
    /// </summary>
    public async Task<(bool Rebuilt, string? Error)> RebuildDirectoryAsync(SessionInfo session, ProjectInfo? project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var directory = DirectoryOf(session);
        if (System.IO.Directory.Exists(directory))
        {
            return (false, null);
        }

        if (project is null || !IsForgeOwned(session, project))
        {
            return (false, RemovalStrings.RootMissing(directory));
        }

        if (project.Kind == ProjectKind.General)
        {
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                return (true, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return (false, RemovalStrings.RebuildFailed(directory, exception.Message));
            }
        }

        var branch = session.Branch;
        try
        {
            if (branch is not { Length: > 0 } || !await git.BranchExistsAsync(project.RootPath, branch, ct).ConfigureAwait(false))
            {
                return (false, RemovalStrings.BranchGone(branch ?? string.Empty));
            }

            await TryPruneAsync(project.RootPath).ConfigureAwait(false);
            await git.AttachWorktreeAsync(project.RootPath, directory, branch, ct).ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception exception) when (exception is GitException or IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return (false, RemovalStrings.RebuildFailed(directory, exception.Message));
        }
    }

    /// <summary>
    /// The one session cleanup (7.2, shared by session and workspace removal): the store's single-transaction purge of the
    /// session and its descendants, then the files kept outside <c>forge.db</c> (artifacts, review data, the stored session
    /// draft). The inheritance guard keeps the session and names who inherits from it; a cleanup failure after the purge
    /// only warns.
    /// </summary>
    private async Task<(IReadOnlyList<string> Removed, string? Warning, string? Error)> PurgeAsync(
        SessionInfo session,
        CancellationToken ct)
    {
        IReadOnlyList<string> purged;
        try
        {
            purged = await catalog.PurgeAsync(session.Id, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var reason = exception is OperationCanceledException ? RemovalStrings.Cancelled : exception.Message;
            return ([], null, RemovalStrings.RemoveFailed(session.Title, reason));
        }

        var failures = new List<string>();
        foreach (var id in purged)
        {
            try
            {
                await cleanUpPurged(id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                AppLogs.Lifecycle.ZLogWarning($"purge cleanup of '{id}' failed: {exception.Message}");

                // A purge with descendants cleans several ids: name the one that failed.
                failures.Add(purged.Count > 1 ? id + ": " + exception.Message : exception.Message);
            }
        }

        return (
            purged,
            failures.Count == 0 ? null : RemovalStrings.RemovePartial(string.Join("; ", failures.Distinct(StringComparer.Ordinal))),
            null);
    }

    /// <summary>
    /// "Remove workspace" applies to a repository worktree only: never the project's root checkout. The root is compared
    /// both by spelling (the occupancy key) and by link resolution, because git can print the root under another spelling
    /// (macOS <c>/var</c> vs <c>/private/var</c>, a Windows junction) and the red line must not depend on the spelling.
    /// </summary>
    public static bool IsRemovableWorkspace(ProjectInfo project, string directory) =>
        project.Kind == ProjectKind.Repository
        && !string.IsNullOrWhiteSpace(directory)
        && !SameDirectory(directory, project.RootPath)
        && !WorktreeList.SamePathSafe(directory, project.RootPath);

    /// <summary>
    /// The guard again just before the trash: reasons that appeared while the dialog was open (work added meanwhile) were
    /// never shown, so the removal stops and names them; the user removes again to see the full list. Null when nothing new.
    /// A reason counts as shown when a dialog reason covers it (<see cref="GuardReason.Covers"/>): a failed check that
    /// fails again in other words was shown; a failed check that now finds work, or a clean check that now fails, was not.
    /// Unlike the plan, the execution reports every failure here as its outcome (a cancellation included) instead of
    /// throwing, like the trash step after it: the shell shows one reason and nothing has moved.
    /// </summary>
    private async Task<string?> NewGuardReasonsAsync(string repositoryRoot, string directory, string? baseBranch, IReadOnlyList<GuardReason> shown, CancellationToken ct)
    {
        IReadOnlyList<GuardReason> now;
        try
        {
            now = await GuardAsync(repositoryRoot, directory, baseBranch, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return RemovalStrings.CheckFailed(exception is OperationCanceledException ? RemovalStrings.Cancelled : exception.Message);
        }

        var grown = now.Where(reason => !shown.Any(old => old.Covers(reason))).Select(static reason => reason.Text).ToArray();
        return grown.Length == 0 ? null : RemovalStrings.ReasonsGrew(grown);
    }

    /// <summary>
    /// The removal guard of an existing worktree directory. A directory git marks prunable or does not list cannot be
    /// asked about its changes (git there fails, or answers for an enclosing repository), so the guard names that instead:
    /// the dialog then asks "remove anyway" and the folder goes to the trash, where nothing is lost. A list that cannot
    /// be read (or comes back empty) is unknown, neither healthy nor stale: the usual check still runs, but its answer
    /// may be an enclosing repository's, so the guard also names the unverified state and the removal is never silent.
    /// A git check that fails (<see cref="IsCheckFailure"/>) is a reason too, never a dead end: "remove anyway" moves the
    /// folder to the trash, where nothing is lost. For a listed worktree git cannot run inside, the commits are still
    /// checked from the repository with the HEAD the list gives, so only the uncommitted changes stay unknown. Any other
    /// exception propagates and the plan fails.
    /// </summary>
    private async Task<IReadOnlyList<GuardReason>> GuardAsync(string repositoryRoot, string directory, string? baseBranch, CancellationToken ct)
    {
        IReadOnlyList<GitWorktree>? list = null;
        string? listFailure = null;
        try
        {
            list = await git.ListWorktreesAsync(repositoryRoot, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCheckFailure(exception, ct))
        {
            AppLogs.Lifecycle.ZLogWarning($"could not list the worktrees of '{repositoryRoot}': {exception.Message}");
            listFailure = exception.Message;
        }

        var (state, prunableReason) = WorktreeList.Judge(list, directory);
        switch (state)
        {
            case WorktreeRegistration.Prunable:
                return [new(RemovalStrings.GuardPrunable(prunableReason), GuardReasonKind.Prunable)];
            case WorktreeRegistration.Unregistered:
                return [new(RemovalStrings.GuardUnregistered, GuardReasonKind.Unregistered)];
            case WorktreeRegistration.Registered:
                return await CheckAsync(repositoryRoot, directory, baseBranch, WorktreeList.Find(list!, directory), ct).ConfigureAwait(false);
            default:
                var checkedReasons = await CheckAsync(repositoryRoot, directory, baseBranch, null, ct).ConfigureAwait(false);
                return
                [
                    new(RemovalStrings.GuardUnverified(listFailure ?? RemovalStrings.WorktreeListEmpty), GuardReasonKind.Unverified),
                    .. checkedReasons,
                ];
        }
    }

    /// <summary>
    /// The change and commit check inside the worktree. When git fails there and <paramref name="entry"/> (its line in the
    /// worktree list) gives a HEAD, the commits are checked from the repository instead; a failure is a reason either way.
    /// </summary>
    private async Task<IReadOnlyList<GuardReason>> CheckAsync(
        string repositoryRoot,
        string directory,
        string? baseBranch,
        GitWorktree? entry,
        CancellationToken ct)
    {
        string failure;
        try
        {
            var reasons = await git.CheckRemovableAsync(repositoryRoot, directory, baseBranch, ct).ConfigureAwait(false);
            return [.. reasons.Select(GuardReason.Change)];
        }
        catch (Exception exception) when (IsCheckFailure(exception, ct))
        {
            AppLogs.Lifecycle.ZLogWarning($"could not check '{directory}' for removal: {exception.Message}");
            failure = exception.Message;
        }

        if (entry is { HeadCommit: { Length: > 0 } head })
        {
            try
            {
                var commits = await git.CheckCommitsAsync(repositoryRoot, head, entry.IsDetached, baseBranch, ct).ConfigureAwait(false);
                return
                [
                    new(RemovalStrings.GuardChangesUnchecked(failure), GuardReasonKind.ChangesUnchecked),
                    .. commits.Select(GuardReason.Change),
                ];
            }
            catch (Exception exception) when (IsCheckFailure(exception, ct))
            {
                AppLogs.Lifecycle.ZLogWarning($"could not check the commits of '{directory}' from '{repositoryRoot}': {exception.Message}");
            }
        }

        return [new(RemovalStrings.GuardCheckFailed(failure), GuardReasonKind.CheckFailed)];
    }

    /// <summary>
    /// A git read that failed in a way the guard reports as a reason: git itself (<see cref="GitCli"/> reports a process
    /// that could not start or timed out as a <see cref="GitException"/> too), the file system, a timeout. A cancellation of
    /// <paramref name="ct"/> is not one (it propagates); any other cancellation is a timeout inside the git call. The shell
    /// plans with <see cref="CancellationToken.None"/>, so every cancellation it sees is such a timeout. Programming errors
    /// (<see cref="InvalidOperationException"/>, <see cref="ArgumentException"/>, ...) are not git failures: they propagate
    /// and the plan fails rather than turn into "remove anyway".
    /// </summary>
    private static bool IsCheckFailure(Exception exception, CancellationToken ct) =>
        exception is GitException
            or IOException
            or System.ComponentModel.Win32Exception
            or TimeoutException
            or UnauthorizedAccessException
        || exception is OperationCanceledException && !ct.IsCancellationRequested;

    private async Task<string?> RecycleWorkspaceAsync(string repositoryRoot, string directory, bool missing, CancellationToken ct)
    {
        if (!missing && await TrashAsync(directory, ct).ConfigureAwait(false) is { } failure)
        {
            return failure;
        }

        await TryPruneAsync(repositoryRoot).ConfigureAwait(false);
        return null;
    }

    private async Task<string?> TrashAsync(string directory, CancellationToken ct)
    {
        try
        {
            await moveToTrash(directory, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return RemovalStrings.TrashFailed(exception is OperationCanceledException ? RemovalStrings.Cancelled : exception.Message);
        }
    }

    /// <summary>A prune failure only leaves a stale registration the next refresh prunes again; traced, never thrown (7.2 step 3).</summary>
    private async Task TryPruneAsync(string repositoryRoot)
    {
        try
        {
            await git.PruneAsync(repositoryRoot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Lifecycle.ZLogWarning($"git worktree prune in '{repositoryRoot}' failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Every session of a project, removed ones included: a history row's descendants and a workspace's removed roots are
    /// only in the history read, and the purge cascades over all of them.
    /// </summary>
    private async Task<IReadOnlyList<SessionInfo>> AllSessionsAsync(string projectId, CancellationToken ct)
    {
        var query = new SessionQuery { ProjectId = projectId };
        var live = await catalog.QueryAsync(query, ct).ConfigureAwait(false);
        IReadOnlyList<SessionInfo> history;
        try
        {
            history = await catalog.ListHistoryAsync(query, ct).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            // A catalog without the lifecycle history (an alternate implementation) has no removed sessions to add.
            return live;
        }

        var removed = history.Where(static session => session.Status == SessionStatus.Deleted).ToArray();
        return removed.Length == 0 ? live : [.. live, .. removed];
    }

    /// <summary>The root sessions of <paramref name="directory"/>, every state (history included): a workspace removal removes them all.</summary>
    private static List<SessionInfo> RootsAt(string directory, IReadOnlyList<SessionInfo> all)
    {
        var key = Key(directory);
        return all
            .Where(s => s.ParentSessionId is null
                        && string.Equals(OccupancyKey.Compute(s.WorktreePath, s.WorkspacePath), key, StringComparison.Ordinal))
            .ToList();
    }

    private static List<SessionInfo> Descendants(string sessionId, IReadOnlyList<SessionInfo> all)
    {
        var byParent = all.Where(static s => s.ParentSessionId is not null).ToLookup(static s => s.ParentSessionId!, StringComparer.Ordinal);
        var found = new List<SessionInfo>();
        var pending = new Stack<string>();
        pending.Push(sessionId);
        while (pending.Count > 0)
        {
            foreach (var child in byParent[pending.Pop()])
            {
                found.Add(child);
                pending.Push(child.Id);
            }
        }

        return found;
    }

    /// <summary>
    /// The fork branches of <paramref name="branch"/> (6.6: a session with <c>fork_of_branch</c> = it, outside this
    /// directory) not yet merged into it. A branch whose check fails is listed too: unknown is not "merged".
    /// </summary>
    private async Task<IReadOnlyList<string>> UnmergedForksAsync(
        ProjectInfo project,
        string branch,
        string directory,
        IReadOnlyList<SessionInfo> all,
        CancellationToken ct)
    {
        var key = Key(directory);
        var forkBranches = all
            .Where(s => s.ParentSessionId is null
                        && string.Equals(s.ForkOfBranch, branch, StringComparison.Ordinal)
                        && s.Branch is { Length: > 0 }
                        && !string.Equals(OccupancyKey.Compute(s.WorktreePath, s.WorkspacePath), key, StringComparison.Ordinal))
            .Select(static s => s.Branch!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unmerged = new List<string>();
        foreach (var fork in forkBranches)
        {
            bool merged;
            try
            {
                merged = await git.IsAncestorAsync(project.RootPath, fork, branch, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsCheckFailure(exception, ct) || exception is ArgumentException or InvalidOperationException)
            {
                AppLogs.Lifecycle.ZLogWarning($"could not tell whether '{fork}' is merged into '{branch}': {exception.Message}");
                merged = false;
            }

            if (!merged)
            {
                unmerged.Add(fork);
            }
        }

        return unmerged;
    }

    private static string Key(string directory) => OccupancyKey.Compute(directory, directory);
}
