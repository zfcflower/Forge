namespace Forge.Workspace;

/// <summary>How a worktree path is derived from the repository.</summary>
public enum WorktreeLayout
{
    /// <summary><c>&lt;RootDirectory&gt;/&lt;repoName&gt;/&lt;slug&gt;</c>, with <c>RootDirectory</c> defaulting to <c>&lt;repo&gt;/../.forge-worktrees</c>.</summary>
    NestedByRepository,

    /// <summary>
    /// <c>&lt;repoParent&gt;/&lt;repoName&gt;.worktrees/&lt;slug&gt;</c>, so each repository's worktrees sit next to it under a name
    /// derived from the repository. An explicit <c>RootDirectory</c> is the directory those per-repository folders are
    /// created in.
    /// </summary>
    SiblingOfRepository,
}

public sealed record WorktreeOptions
{
    /// <summary>
    /// Base directory for worktrees; the worktree itself lands in <c>&lt;RootDirectory&gt;/&lt;repoName&gt;/&lt;slug&gt;</c> (see
    /// <see cref="Layout"/>) and defaults to <c>&lt;repo&gt;/../.forge-worktrees</c>. A relative value is resolved against
    /// the repository's parent directory, so worktrees live outside the repository and are never committed.
    /// </summary>
    public string? RootDirectory { get; init; }

    /// <summary>Whether worktrees nest under the repository name or sit in a sibling <c>&lt;repoName&gt;.worktrees</c> folder.</summary>
    public WorktreeLayout Layout { get; init; } = WorktreeLayout.NestedByRepository;

    public string BranchPrefix { get; init; } = "forge/";

    /// <summary>Commit, branch or tag the session branch starts from; defaults to <c>HEAD</c> of the repository.</summary>
    public string? StartPoint { get; init; }

    public int MaxSlugLength { get; init; } = 48;

    public int MaxSlugAttempts { get; init; } = 1000;
}

public sealed record WorktreeSession(
    string RepositoryRoot,
    string Path,
    string Branch,
    string Slug,
    string? BaseCommit)
{
    /// <summary>
    /// The branch the manager's own <c>git worktree add -b</c> created for this session, set only there. A session built
    /// any other way (or copied onto another branch name) has none, and its branch is never rolled back
    /// (<see cref="WorktreeManager.TryDeleteCreatedBranchAsync"/>).
    /// </summary>
    internal string? CreatedBranch { get; init; }
}

/// <summary>
/// A cone-mode sparse checkout: only the listed top-level directories (plus the files at the repository root) are
/// materialised in the worktree.
/// </summary>
public sealed record SparseCheckout(IReadOnlyList<string> Directories);

/// <summary>Removal was refused because the worktree still holds work.</summary>
public sealed class WorktreeRemovalRefusedException : Exception
{
    public WorktreeRemovalRefusedException(string worktreePath, IReadOnlyList<string> reasons)
        : base($"Refusing to remove worktree '{worktreePath}': {string.Join("; ", reasons)}")
    {
        WorktreePath = worktreePath;
        Reasons = reasons;
    }

    public string WorktreePath { get; }

    public IReadOnlyList<string> Reasons { get; }
}

/// <summary>
/// One worktree plus branch per session. Worktrees outlive the session: nothing is removed until a caller
/// asks, and removal refuses to discard uncommitted or unmerged work unless forced.
/// </summary>
public sealed class WorktreeManager
{
    private const string DefaultDirectoryName = ".forge-worktrees";

    private const int MaxAddAttempts = 20;

    private static readonly Dictionary<string, CreateGate> CreateGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly Lock CreateGatesLock = new();

    internal static int ActiveCreateGates
    {
        get
        {
            lock (CreateGatesLock)
            {
                return CreateGates.Count;
            }
        }
    }

    private sealed class CreateGate
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int Users;
    }

    private static CreateGate AcquireGate(string key)
    {
        lock (CreateGatesLock)
        {
            if (!CreateGates.TryGetValue(key, out var gate))
            {
                gate = new CreateGate();
                CreateGates[key] = gate;
            }

            gate.Users++;
            return gate;
        }
    }

    private static void ReleaseGate(string key, CreateGate gate)
    {
        lock (CreateGatesLock)
        {
            if (--gate.Users == 0)
            {
                CreateGates.Remove(key);
                gate.Semaphore.Dispose();
            }
        }
    }

    private readonly GitCli git;

    public WorktreeManager(GitCli git, WorktreeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        this.git = git;
        Options = options ?? new WorktreeOptions();
    }

    public WorktreeOptions Options { get; }

    /// <summary>
    /// Creates a worktree on a caller-chosen branch name. Unlike the title overload the name is never suffixed: an
    /// invalid name (<c>git check-ref-format --branch</c>) throws <see cref="ArgumentException"/>, and a branch that
    /// already exists throws <see cref="GitException"/> naming it, because the user typed that exact name. The worktree
    /// directory slug comes from the part after the last <c>/</c> and is suffixed only when that directory exists. A
    /// null or blank <paramref name="branchName"/> falls back to the title overload.
    /// </summary>
    public async Task<WorktreeSession> CreateAsync(
        string repositoryPath,
        string title,
        string? branchName,
        CancellationToken ct = default,
        SparseCheckout? sparse = null)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return await CreateAsync(repositoryPath, title, ct).ConfigureAwait(false);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var repositoryRoot = await ResolveRepositoryRootAsync(repositoryPath, ct).ConfigureAwait(false);
        if (!await git.IsValidBranchNameAsync(repositoryRoot, branchName, ct).ConfigureAwait(false))
        {
            throw new ArgumentException($"'{branchName}' is not a valid branch name; see git check-ref-format.", nameof(branchName));
        }

        var startPoint = StartPointName();
        var baseCommit = await ResolveBaseCommitAsync(repositoryRoot, startPoint, ct).ConfigureAwait(false);

        // The directory is named after the last path segment; the branch keeps whatever the caller typed, slashes included.
        var segment = branchName[(branchName.LastIndexOf('/') + 1)..];
        var baseSlug = Slugify(segment, Options.MaxSlugLength);
        return await CreateInRepositoryAsync(repositoryRoot, baseSlug, branchName, startPoint, baseCommit, sparse, ct).ConfigureAwait(false);
    }

    public async Task<WorktreeSession> CreateAsync(string repositoryPath, string title, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var repositoryRoot = await ResolveRepositoryRootAsync(repositoryPath, ct).ConfigureAwait(false);
        var startPoint = StartPointName();
        var baseCommit = await ResolveBaseCommitAsync(repositoryRoot, startPoint, ct).ConfigureAwait(false);
        var baseSlug = Slugify(title, Options.MaxSlugLength);
        return await CreateInRepositoryAsync(repositoryRoot, baseSlug, null, startPoint, baseCommit, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a worktree on a caller-chosen branch name that starts at <paramref name="startPoint"/> - a local branch,
    /// a remote-tracking ref such as <c>origin/main</c>, or any revision - instead of at HEAD. The start point is resolved
    /// to its commit and the worktree is added from that commit, so the new branch never picks up an upstream from the
    /// remote-tracking ref it started at; a start point that does not resolve throws <see cref="GitException"/>. Name
    /// handling matches the HEAD-starting overload: an invalid name throws <see cref="ArgumentException"/> and an existing
    /// branch throws <see cref="GitException"/> naming it.
    /// </summary>
    public async Task<WorktreeSession> CreateAsync(
        string repositoryPath,
        string title,
        string? branchName,
        string startPoint,
        CancellationToken ct = default,
        SparseCheckout? sparse = null)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return await CreateAsync(repositoryPath, title, ct).ConfigureAwait(false);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(startPoint);
        var repositoryRoot = await ResolveRepositoryRootAsync(repositoryPath, ct).ConfigureAwait(false);
        if (!await git.IsValidBranchNameAsync(repositoryRoot, branchName, ct).ConfigureAwait(false))
        {
            throw new ArgumentException($"'{branchName}' is not a valid branch name; see git check-ref-format.", nameof(branchName));
        }

        var baseCommit = await ResolveBaseCommitAsync(repositoryRoot, startPoint, ct).ConfigureAwait(false);
        var segment = branchName[(branchName.LastIndexOf('/') + 1)..];
        var baseSlug = Slugify(segment, Options.MaxSlugLength);
        return await CreateInRepositoryAsync(repositoryRoot, baseSlug, branchName, baseCommit, baseCommit, sparse, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The gate-protected part every overload shares: serialize the check-then-act per repository, allocate the directory
    /// slug, then let one <c>git worktree add -b</c> create the branch and the worktree together.
    /// </summary>
    /// <param name="branchName">
    /// The caller's exact branch name, or null to mint <see cref="WorktreeOptions.BranchPrefix"/> + slug. A non-null name is
    /// never suffixed or reused - only the directory slug may gain <c>-2</c>, <c>-3</c>... when that directory is taken.
    /// </param>
    private async Task<WorktreeSession> CreateInRepositoryAsync(
        string repositoryRoot,
        string baseSlug,
        string? branchName,
        string startPoint,
        string? baseCommit,
        SparseCheckout? sparse,
        CancellationToken ct)
    {
        if (sparse is not null && sparse.Directories.Count == 0)
        {
            throw new ArgumentException("A sparse checkout needs at least one directory.", nameof(sparse));
        }

        var attempts = Math.Max(1, Options.MaxSlugAttempts);
        var gateKey = WorkspacePaths.Normalize(repositoryRoot);
        var gate = AcquireGate(gateKey);
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            var existing = await GetBranchNamesAsync(repositoryRoot, ct).ConfigureAwait(false);
            var addAttempts = 0;
            GitException? lastFailure = null;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (branchName is not null && existing.Contains(branchName))
                {
                    // Re-checked every round: a concurrent process may have taken the name since the gate was entered.
                    throw BranchExists(branchName);
                }

                var slug = attempt == 1 ? baseSlug : $"{baseSlug}-{attempt}";
                var branch = branchName ?? Options.BranchPrefix + slug;
                var path = ResolveWorktreePath(repositoryRoot, slug);

                // The minted branch follows the slug, so both have to be free; a caller-chosen branch only frees up a directory.
                if (WorkspacePaths.Exists(path) || (branchName is null && existing.Contains(branch)))
                {
                    continue;
                }

                var parent = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                try
                {
                    var worktree = await git.AddWorktreeAsync(repositoryRoot, path, branch, startPoint, ct, noCheckout: sparse is not null).ConfigureAwait(false);
                    var session = new WorktreeSession(repositoryRoot, worktree.Path, branch, slug, baseCommit) { CreatedBranch = branch };
                    if (sparse is not null)
                    {
                        await ApplySparseOrCleanUpAsync(session, sparse, ct).ConfigureAwait(false);
                    }

                    return session;
                }
                catch (GitException exception) when (addAttempts < MaxAddAttempts && IsCollision(exception, path))
                {
                    addAttempts++;
                    lastFailure = exception;
                    existing = await GetBranchNamesAsync(repositoryRoot, ct).ConfigureAwait(false);
                }
            }

            var target = branchName ?? baseSlug;
            if (lastFailure is not null)
            {
                throw new GitException(
                    $"Could not create a worktree branch for '{target}' after {addAttempts} attempts: {lastFailure.Message}",
                    lastFailure.Arguments,
                    lastFailure.ExitCode,
                    lastFailure.StandardError);
            }

            throw branchName is null
                ? new GitException($"Could not find an unused branch name for '{baseSlug}'.", [], -1, string.Empty)
                : new GitException(
                    $"Could not find an unused worktree directory name for branch '{branchName}' after {attempts} attempts.",
                    [],
                    -1,
                    string.Empty);
        }
        finally
        {
            if (entered)
            {
                gate.Semaphore.Release();
            }

            ReleaseGate(gateKey, gate);
        }
    }

    private string StartPointName() => string.IsNullOrWhiteSpace(Options.StartPoint) ? "HEAD" : Options.StartPoint;

    private async Task<string> ResolveRepositoryRootAsync(string repositoryPath, CancellationToken ct) =>
        await git.GetRepositoryRootAsync(repositoryPath, ct).ConfigureAwait(false)
        ?? throw new GitException($"'{repositoryPath}' is not inside a git repository.", [], -1, string.Empty);

    /// <summary>
    /// The commit a start point names. The <c>^{commit}</c> suffix is what makes the answer a commit id: an annotated tag
    /// resolves to its tag object otherwise, which would be reported as the session's base commit while the worktree
    /// itself landed on the tagged commit.
    /// </summary>
    private async Task<string> ResolveBaseCommitAsync(string repositoryRoot, string startPoint, CancellationToken ct) =>
        await git.RevParseAsync(repositoryRoot, startPoint + "^{commit}", ct).ConfigureAwait(false)
        ?? throw new GitException($"Start point '{startPoint}' does not resolve to a commit.", [], -1, string.Empty);

    /// <summary>
    /// A branch the caller asked for by name is already there. The user typed that exact name, so it is reported and
    /// never suffixed, renamed or reused.
    /// </summary>
    private static GitException BranchExists(string branchName) =>
        new(
            $"Branch '{branchName}' already exists. Choose a different branch name; an existing branch is never reused.",
            [],
            -1,
            string.Empty);

    private async Task<HashSet<string>> GetBranchNamesAsync(string repositoryRoot, CancellationToken ct)
    {
        var branches = await git.GetBranchesAsync(repositoryRoot, ct: ct).ConfigureAwait(false);
        return new HashSet<string>(branches.Select(static branch => branch.Name), StringComparer.Ordinal);
    }

    private static bool IsCollision(GitException exception, string path) =>
        WorkspacePaths.Exists(path)
        || exception.StandardError.Contains("already exists", StringComparison.OrdinalIgnoreCase)
        || exception.StandardError.Contains("cannot lock ref", StringComparison.OrdinalIgnoreCase);

    public Task<IReadOnlyList<GitWorktree>> ListAsync(string repositoryPath, CancellationToken ct = default) =>
        git.ListWorktreesAsync(repositoryPath, ct);

    /// <summary>
    /// Removes the worktree, leaving its branch behind. Without <paramref name="force"/> the call refuses
    /// when the worktree has uncommitted changes, when its HEAD is detached and contained in no branch or tag,
    /// when commits are not merged into <paramref name="baseBranch"/> (default: the repository's default
    /// branch), or when no default base branch can be determined at all.
    /// </summary>
    public async Task RemoveAsync(
        string repositoryPath,
        string worktreePath,
        bool force = false,
        string? baseBranch = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        worktreePath = System.IO.Path.IsPathRooted(worktreePath)
            ? System.IO.Path.GetFullPath(worktreePath)
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(repositoryPath, worktreePath));

        if (!force)
        {
            var reasons = await CheckRemovableAsync(repositoryPath, worktreePath, baseBranch, ct).ConfigureAwait(false);
            if (reasons.Count > 0)
            {
                throw new WorktreeRemovalRefusedException(worktreePath, reasons);
            }
        }

        await git.RemoveWorktreeAsync(repositoryPath, worktreePath, force, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The reasons removing the worktree would discard work, empty when it is safe: uncommitted changes, a detached HEAD
    /// contained in no branch, remote branch or tag, commits not merged into <paramref name="baseBranch"/> (default: the
    /// repository's default branch), or no base branch to judge by. Read-only; <see cref="RemoveAsync"/> uses it as its guard.
    /// </summary>
    public async Task<IReadOnlyList<string>> CheckRemovableAsync(
        string repositoryPath,
        string worktreePath,
        string? baseBranch = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        worktreePath = System.IO.Path.IsPathRooted(worktreePath)
            ? System.IO.Path.GetFullPath(worktreePath)
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(repositoryPath, worktreePath));

        var reasons = new List<string>();
        var status = await git.GetStatusAsync(worktreePath, ct).ConfigureAwait(false);
        if (status.Count > 0)
        {
            reasons.Add($"{status.Count} uncommitted change{(status.Count == 1 ? string.Empty : "s")}");
        }

        var repositoryRoot = await git.GetRepositoryRootAsync(worktreePath, ct).ConfigureAwait(false) ?? repositoryPath;
        var head = await git.RevParseAsync(worktreePath, "HEAD", ct).ConfigureAwait(false);
        var detached = head is not null && await git.GetCurrentBranchAsync(worktreePath, ct).ConfigureAwait(false) is null;
        await AddCommitReasonsAsync(reasons, worktreePath, repositoryRoot, head, detached, baseBranch, ct).ConfigureAwait(false);
        return reasons;
    }

    /// <summary>
    /// The commit half of <see cref="CheckRemovableAsync"/> run from the repository instead of inside the worktree, for a
    /// worktree where git itself cannot run (a broken <c>.git</c> link) but whose HEAD is known from
    /// <c>git worktree list --porcelain</c>: a detached <paramref name="head"/> contained in no branch, remote branch or tag,
    /// commits not merged into <paramref name="baseBranch"/> (default: the repository's default branch), or no base branch
    /// to judge by, in the same words. Uncommitted changes cannot be read this way and are not reported. Read-only.
    /// </summary>
    public async Task<IReadOnlyList<string>> CheckCommitsAsync(
        string repositoryPath,
        string head,
        bool detached,
        string? baseBranch = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(head);
        var repositoryRoot = await git.GetRepositoryRootAsync(repositoryPath, ct).ConfigureAwait(false) ?? repositoryPath;
        var reasons = new List<string>();
        await AddCommitReasonsAsync(reasons, repositoryRoot, repositoryRoot, head, detached, baseBranch, ct).ConfigureAwait(false);
        return reasons;
    }

    /// <summary>The detached-HEAD and unmerged-commit reasons, asked in <paramref name="workingDirectory"/>.</summary>
    private async Task AddCommitReasonsAsync(
        List<string> reasons,
        string workingDirectory,
        string repositoryRoot,
        string? head,
        bool detached,
        string? baseBranch,
        CancellationToken ct)
    {
        var baseRef = baseBranch ?? await git.GetDefaultBranchAsync(repositoryRoot, ct).ConfigureAwait(false);
        if (head is not null
            && detached
            && !await git.IsCommitReferencedAsync(workingDirectory, head, ct).ConfigureAwait(false))
        {
            reasons.Add($"HEAD is detached at '{head}' and is not contained in any branch, remote branch or tag");
        }

        if (baseRef is null)
        {
            reasons.Add("no base branch could be determined, so unmerged work cannot be ruled out; pass baseBranch explicitly or force the removal");
        }
        else if (head is not null)
        {
            var ahead = await git.CountCommitsAheadAsync(workingDirectory, head, baseRef, ct).ConfigureAwait(false);
            if (ahead > 0)
            {
                reasons.Add($"{ahead} commit{(ahead == 1 ? string.Empty : "s")} not merged into '{baseRef}'");
            }
        }
    }

    /// <summary>
    /// Finishes a worktree that was added with <c>--no-checkout</c>: cone-mode sparse set, then the checkout. When a step
    /// fails the worktree is cleaned up the way <c>WorktreeService.TryRemoveCreatedAsync</c> does (unforced worktree
    /// removal, then <c>branch -d</c>; never forced, never a directory delete) and the original failure is rethrown. A
    /// no-checkout worktree reads as dirty to git, so the cleanup first tries to populate it with a plain checkout; if the
    /// leftover still cannot be removed without force it stays, and the message says where.
    /// </summary>
    private async Task ApplySparseOrCleanUpAsync(WorktreeSession session, SparseCheckout sparse, CancellationToken ct)
    {
        try
        {
            await git.SetSparseCheckoutAsync(session.Path, sparse.Directories, ct).ConfigureAwait(false);
            await git.CheckoutAsync(session.Path, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var left = await TryCleanUpUnfinishedAsync(session).ConfigureAwait(false);
            if (left && exception is GitException failure)
            {
                throw new GitException(
                    $"{failure.Message} The unfinished worktree '{session.Path}' (branch '{session.Branch}') could not be removed without force and was left in place.",
                    failure.Arguments,
                    failure.ExitCode,
                    failure.StandardError);
            }

            throw;
        }
    }

    /// <summary>True when the unfinished worktree is still there afterwards.</summary>
    private async Task<bool> TryCleanUpUnfinishedAsync(WorktreeSession session)
    {
        try
        {
            // Populates index and files so git no longer sees every tracked path as deleted; best effort.
            await git.CheckoutAsync(session.Path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine($"[worktree] could not populate the unfinished sparse worktree '{session.Path}': {exception.Message}");
        }

        try
        {
            await RemoveAsync(session.RepositoryRoot, session.Path, force: false, baseBranch: session.BaseCommit, ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine($"[worktree] could not remove the unfinished sparse worktree '{session.Path}': {exception.Message}");
            return true;
        }

        await TryDeleteCreatedBranchAsync(session, CancellationToken.None).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Rollback of the branch that this manager's own <c>git worktree add -b</c> created for <paramref name="session"/>,
    /// after the same creation failed (the only branch deletion Forge does, TASK-session-worktree-lifecycle 12.4). A
    /// session carries <see cref="WorktreeSession.CreatedBranch"/> only when it was built right after <c>add -b</c> succeeded,
    /// so its branch did not exist before that operation (an existing name makes <c>add -b</c> fail and no session is
    /// built); any other session, or one copied onto another branch name, is refused. The branch is deleted
    /// only while it still points exactly at <see cref="WorktreeSession.BaseCommit"/>, the commit it was created at: a
    /// branch that moved since (a commit, a reset) is kept, and so is one whose base is unknown. Deletion is always
    /// <c>branch -d</c>, never <c>-D</c>, so git refuses it as well when the branch holds unmerged work. Callers remove the
    /// worktree first (git refuses to delete a branch checked out somewhere). Failures are traced, never thrown: the
    /// caller is already reporting the original error. Returns whether the branch was deleted.
    /// </summary>
    public async Task<bool> TryDeleteCreatedBranchAsync(WorktreeSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!string.Equals(session.CreatedBranch, session.Branch, StringComparison.Ordinal))
        {
            System.Diagnostics.Trace.WriteLine($"[worktree] kept the branch '{session.Branch}': this manager did not create it.");
            return false;
        }

        try
        {
            if (session.BaseCommit is null)
            {
                System.Diagnostics.Trace.WriteLine($"[worktree] kept the branch '{session.Branch}': its start commit is unknown.");
                return false;
            }

            var tip = await git.RevParseAsync(session.RepositoryRoot, "refs/heads/" + session.Branch + "^{commit}", ct).ConfigureAwait(false);
            if (tip is null)
            {
                return false;
            }

            if (!string.Equals(tip, session.BaseCommit, StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Trace.WriteLine($"[worktree] kept the branch '{session.Branch}': it moved from {session.BaseCommit} to {tip} after it was created.");
                return false;
            }

            await git.DeleteBranchAsync(session.RepositoryRoot, session.Branch, force: false, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine($"[worktree] could not delete the branch '{session.Branch}': {exception.Message}");
            return false;
        }
    }

    public Task PruneAsync(string repositoryPath, CancellationToken ct = default) =>
        git.PruneWorktreesAsync(repositoryPath, ct);

    public string ResolveWorktreePath(string repositoryRoot, string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        var repository = WorkspacePaths.Normalize(repositoryRoot);
        var parent = System.IO.Path.GetDirectoryName(repository);
        var repositoryName = WorkspacePaths.FileName(repositoryRoot);
        var configured = string.IsNullOrWhiteSpace(Options.RootDirectory)
            ? null
            : System.IO.Path.IsPathRooted(Options.RootDirectory)
                ? Options.RootDirectory
                : System.IO.Path.Combine(parent ?? repository, Options.RootDirectory);

        return Options.Layout switch
        {
            WorktreeLayout.SiblingOfRepository => System.IO.Path.GetFullPath(System.IO.Path.Combine(
                configured ?? parent ?? repository,
                repositoryName + ".worktrees",
                slug)),
            _ => System.IO.Path.GetFullPath(System.IO.Path.Combine(
                configured ?? System.IO.Path.Combine(parent ?? repository, DefaultDirectoryName),
                repositoryName,
                slug)),
        };
    }

    /// <summary>Lowercases and reduces a title to characters that are safe in a branch name and a directory name.</summary>
    public static string Slugify(string title, int maxLength = 48)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        var builder = new System.Text.StringBuilder(maxLength);
        var lastWasSeparator = true;
        foreach (var raw in title ?? string.Empty)
        {
            var ch = char.ToLowerInvariant(raw);
            var isAllowed = char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_';
            if (isAllowed && builder.Length < maxLength)
            {
                builder.Append(ch);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && builder.Length < maxLength)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('-', '_', '.');
        if (slug.Length == 0)
        {
            return "session";
        }

        return slug;
    }
}
