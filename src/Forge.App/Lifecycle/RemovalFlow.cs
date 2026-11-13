using System.Diagnostics;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.Core.Storage;
using Forge.IO;
using Forge.Workspace;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Lifecycle;

/// <summary>
/// What a removal does with the session's directory (section 2, "目录归属", one decision for every flow).
/// </summary>
internal enum DirectoryFate
{
    /// <summary>Not Forge's to recycle (the repository root checkout, a non-repository project root, the general root): records only.</summary>
    Kept,

    /// <summary>Forge's to recycle, but another non-Deleted session still uses it: records only.</summary>
    Shared,

    /// <summary>Already gone: <c>git worktree prune</c>, then the records.</summary>
    Missing,

    /// <summary>The last session of a Forge-owned directory: the directory goes to the trash, then the records.</summary>
    Trash,
}

/// <summary>The git reads and writes the removal, restore and purge flows (7.2-7.5) need; real or substituted in tests.</summary>
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

    private static bool SamePathSafe(string left, string right)
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
/// "Remove session…" read off the UI thread (7.2): where the directory goes, the guard reasons (only when it goes to the
/// trash), how many sub-agent sessions go with it, and the forks of its branch that were never merged back (6.6).
/// </summary>
internal sealed record SessionRemovalPlan(
    SessionInfo Session,
    ProjectInfo Project,
    string Directory,
    DirectoryFate Fate,
    IReadOnlyList<GuardReason> Guard,
    int ChildCount,
    IReadOnlyList<string> UnmergedForks)
{
    /// <summary>The guard reasons as the dialog lists them.</summary>
    public IReadOnlyList<string> Reasons => [.. Guard.Select(static reason => reason.Text)];
}

/// <summary>
/// "Remove workspace…" read off the UI thread (7.3): the workspace's non-Deleted root sessions (archived included), its
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

/// <summary>A removal's result: <see cref="Error"/> is the user-visible reason when it did not (fully) go through.</summary>
internal sealed record RemovalOutcome(bool Succeeded, string? Error, IReadOnlyList<string> RemovedSessionIds)
{
    public static RemovalOutcome Done(IReadOnlyList<string> removed) => new(true, null, removed);

    public static RemovalOutcome Failed(string error) => new(false, error, []);
}

/// <summary>A purge's result: purged ids, the sessions that kept it (inheritance guard), and a cleanup warning.</summary>
internal sealed record PurgeOutcome(
    IReadOnlyList<string> PurgedIds,
    IReadOnlyList<string> InheritingTitles,
    string? Warning,
    string? Error)
{
    public bool Purged => PurgedIds.Count > 0;
}

/// <summary>
/// Removal, restore and purge (TASK-session-worktree-lifecycle 7.2-7.5) without any dialog: the shell asks, this decides
/// and acts. Disk first, then the record (I2): a trash failure changes nothing; a record failure after the directory went
/// is left for the startup self-heal and never undone by moving the directory back. The trash is the only way a directory
/// is removed (no <c>git worktree remove</c>, no forced or direct delete), and never for a repository or project root.
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

    public async Task<SessionRemovalPlan> PlanSessionRemovalAsync(SessionInfo session, ProjectInfo project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(project);
        var all = await catalog.QueryAsync(new SessionQuery { ProjectId = project.Id }, ct).ConfigureAwait(false);
        var directory = DirectoryOf(session);
        var fate = Fate(session, project, all);
        IReadOnlyList<GuardReason> reasons = fate == DirectoryFate.Trash && IsRepositoryWorktree(session, project)
            ? await GuardAsync(project.RootPath, directory, session.ForkOfBranch, ct).ConfigureAwait(false)
            : [];
        var forks = project.Kind == ProjectKind.Repository && session.Branch is { Length: > 0 } branch
            ? await UnmergedForksAsync(project, branch, directory, all, ct).ConfigureAwait(false)
            : [];
        return new SessionRemovalPlan(session, project, directory, fate, reasons, Descendants(session.Id, all).Count, forks);
    }

    /// <summary>
    /// 7.2 steps 3-4 after the dialog. The session is re-read and the directory decision recomputed: a session that is gone
    /// or already removed, or a directory that would now go to the trash although the dialog did not say so, removes nothing.
    /// </summary>
    public async Task<RemovalOutcome> ExecuteSessionRemovalAsync(SessionRemovalPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var current = await store.GetSessionAsync(plan.Session.Id, ct).ConfigureAwait(false);
        if (current is null || current.Status == SessionStatus.Deleted)
        {
            return RemovalOutcome.Failed(RemovalStrings.Changed);
        }

        var all = await catalog.QueryAsync(new SessionQuery { ProjectId = plan.Project.Id }, ct).ConfigureAwait(false);
        var fate = Fate(current, plan.Project, all);
        if (fate == DirectoryFate.Trash && plan.Fate != DirectoryFate.Trash)
        {
            return RemovalOutcome.Failed(RemovalStrings.Changed);
        }

        if (fate == DirectoryFate.Trash && IsRepositoryWorktree(current, plan.Project)
            && await NewGuardReasonsAsync(plan.Project.RootPath, plan.Directory, current.ForkOfBranch, plan.Guard, ct).ConfigureAwait(false) is { } grown)
        {
            return RemovalOutcome.Failed(grown);
        }

        if (await RecycleAsync(plan.Project, current, plan.Directory, fate, ct).ConfigureAwait(false) is { } failure)
        {
            return RemovalOutcome.Failed(failure);
        }

        return await SoftDeleteAsync([current.Id], all, fate == DirectoryFate.Trash, ct).ConfigureAwait(false);
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

        var all = await catalog.QueryAsync(new SessionQuery { ProjectId = project.Id }, ct).ConfigureAwait(false);
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
    /// prune, then soft-delete every non-Deleted root session of the path (their sub-agent sessions with them). An Active
    /// session found now (one opened while the dialog was up) removes nothing.
    /// </summary>
    public async Task<RemovalOutcome> ExecuteWorkspaceRemovalAsync(WorkspaceRemovalPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Red line: a repository root or project root never goes to the trash, whoever built the plan.
        if (!IsRemovableWorkspace(plan.Project, plan.Directory))
        {
            return RemovalOutcome.Failed(RemovalStrings.RootRefused(plan.Directory));
        }

        var all = await catalog.QueryAsync(new SessionQuery { ProjectId = plan.Project.Id }, ct).ConfigureAwait(false);
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

        var fate = missing ? DirectoryFate.Missing : DirectoryFate.Trash;
        if (!missing
            && await NewGuardReasonsAsync(plan.Project.RootPath, plan.Directory, plan.BaseBranch, plan.Guard, ct).ConfigureAwait(false) is { } grown)
        {
            return RemovalOutcome.Failed(grown);
        }

        if (await RecycleWorkspaceAsync(plan.Project.RootPath, plan.Directory, fate, ct).ConfigureAwait(false) is { } failure)
        {
            return RemovalOutcome.Failed(failure);
        }

        if (roots.Count == 0)
        {
            return RemovalOutcome.Done([]);
        }

        return await SoftDeleteAsync(roots.Select(static s => s.Id).ToArray(), all, fate == DirectoryFate.Trash, ct).ConfigureAwait(false);
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
    /// 7.5 "delete permanently" of one Deleted session (and its sub-agent sessions): the store's single-transaction purge,
    /// then the files kept outside <c>forge.db</c> (artifacts, review marks and comments). The inheritance guard keeps the
    /// session and names who inherits from it; a cleanup failure after the purge only warns.
    /// </summary>
    public async Task<PurgeOutcome> PurgeAsync(SessionInfo session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        IReadOnlyList<string> purged;
        try
        {
            purged = await catalog.PurgeAsync(session.Id, ct).ConfigureAwait(false);
        }
        catch (SessionPurgeRefusedException refused)
        {
            var titles = new List<string>(refused.InheritingSessionIds.Count);
            foreach (var id in refused.InheritingSessionIds)
            {
                var inheriting = await store.GetSessionAsync(id, CancellationToken.None).ConfigureAwait(false);
                titles.Add(inheriting?.Title ?? id);
            }

            return new PurgeOutcome([], titles, null, null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var reason = exception is OperationCanceledException ? RemovalStrings.Cancelled : exception.Message;
            return new PurgeOutcome([], [], null, RemovalStrings.PurgeFailed(session.Title, reason));
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

        return new PurgeOutcome(
            purged,
            [],
            failures.Count == 0 ? null : RemovalStrings.PurgePartial(string.Join("; ", failures.Distinct(StringComparer.Ordinal))),
            null);
    }

    private static DirectoryFate Fate(SessionInfo session, ProjectInfo project, IReadOnlyList<SessionInfo> all)
    {
        if (!IsForgeOwned(session, project))
        {
            return DirectoryFate.Kept;
        }

        var directory = DirectoryOf(session);
        if (RootsAt(directory, all).Any(other => !string.Equals(other.Id, session.Id, StringComparison.Ordinal)))
        {
            return DirectoryFate.Shared;
        }

        return System.IO.Directory.Exists(directory) ? DirectoryFate.Trash : DirectoryFate.Missing;
    }

    /// <summary>"Remove workspace" applies to a repository worktree only: never the project's root checkout.</summary>
    public static bool IsRemovableWorkspace(ProjectInfo project, string directory) =>
        project.Kind == ProjectKind.Repository
        && !string.IsNullOrWhiteSpace(directory)
        && !SameDirectory(directory, project.RootPath);

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

    private static bool IsRepositoryWorktree(SessionInfo session, ProjectInfo project) =>
        project.Kind == ProjectKind.Repository && !string.IsNullOrEmpty(session.WorktreePath);

    /// <summary>Recycle step of a session removal: trash or prune by fate; a trash failure is the returned reason.</summary>
    private async Task<string?> RecycleAsync(ProjectInfo project, SessionInfo session, string directory, DirectoryFate fate, CancellationToken ct)
    {
        switch (fate)
        {
            case DirectoryFate.Trash:
                if (await TrashAsync(directory, ct).ConfigureAwait(false) is { } failure)
                {
                    return failure;
                }

                if (IsRepositoryWorktree(session, project))
                {
                    await TryPruneAsync(project.RootPath).ConfigureAwait(false);
                }

                return null;
            case DirectoryFate.Missing when IsRepositoryWorktree(session, project):
                await TryPruneAsync(project.RootPath).ConfigureAwait(false);
                return null;
            default:
                return null;
        }
    }

    private async Task<string?> RecycleWorkspaceAsync(string repositoryRoot, string directory, DirectoryFate fate, CancellationToken ct)
    {
        if (fate == DirectoryFate.Trash && await TrashAsync(directory, ct).ConfigureAwait(false) is { } failure)
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

    /// <summary>Soft-deletes the roots (the store cascades); the outcome names the roots and their sub-agent sessions, so the shell can close all their tabs.</summary>
    private async Task<RemovalOutcome> SoftDeleteAsync(IReadOnlyList<string> ids, IReadOnlyList<SessionInfo> all, bool directoryWent, CancellationToken ct)
    {
        try
        {
            await catalog.SoftDeleteAsync(ids, ct).ConfigureAwait(false);
            var removed = new List<string>(ids);
            foreach (var id in ids)
            {
                removed.AddRange(Descendants(id, all).Select(static s => s.Id));
            }

            return RemovalOutcome.Done(removed);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The directory is in the trash already (or was gone); the startup self-heal writes the record later. The
            // directory is never moved back (7.2 step 4).
            var reason = exception is OperationCanceledException ? RemovalStrings.Cancelled : exception.Message;
            return RemovalOutcome.Failed(directoryWent ? RemovalStrings.RecordFailed(reason) : RemovalStrings.RecordOnlyFailed(reason));
        }
    }

    /// <summary>The non-Deleted root sessions (archived included) whose occupancy key is <paramref name="directory"/>'s.</summary>
    private static List<SessionInfo> RootsAt(string directory, IReadOnlyList<SessionInfo> all)
    {
        var key = Key(directory);
        return all
            .Where(s => s.ParentSessionId is null
                        && s.Status != SessionStatus.Deleted
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
