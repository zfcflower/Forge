namespace Forge.Workspace;

public sealed record WorktreeOptions
{
    /// <summary>
    /// Base directory for worktrees; the worktree itself lands in <c>&lt;RootDirectory&gt;/&lt;repoName&gt;/&lt;slug&gt;</c>.
    /// Defaults to <c>&lt;repo&gt;/../.forge-worktrees</c>, and a relative value is resolved against the repository's
    /// parent directory, so worktrees live outside the repository and are never committed.
    /// </summary>
    public string? RootDirectory { get; init; }

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
    string? BaseCommit);

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

    private readonly GitCli git;

    public WorktreeManager(GitCli git, WorktreeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        this.git = git;
        Options = options ?? new WorktreeOptions();
    }

    public WorktreeOptions Options { get; }

    public async Task<WorktreeSession> CreateAsync(string repositoryPath, string title, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var repositoryRoot = await git.GetRepositoryRootAsync(repositoryPath, ct).ConfigureAwait(false)
            ?? throw new GitException($"'{repositoryPath}' is not inside a git repository.", [], -1, string.Empty);

        var startPoint = string.IsNullOrWhiteSpace(Options.StartPoint) ? "HEAD" : Options.StartPoint;
        var baseCommit = await git.RevParseAsync(repositoryRoot, startPoint, ct).ConfigureAwait(false)
            ?? throw new GitException($"Start point '{startPoint}' does not resolve to a commit.", [], -1, string.Empty);

        var branches = await git.GetBranchesAsync(repositoryRoot, ct: ct).ConfigureAwait(false);
        var existing = new HashSet<string>(branches.Select(static branch => branch.Name), StringComparer.Ordinal);

        var baseSlug = Slugify(title, Options.MaxSlugLength);
        var attempts = Math.Max(1, Options.MaxSlugAttempts);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var slug = attempt == 1 ? baseSlug : $"{baseSlug}-{attempt}";
            var branch = Options.BranchPrefix + slug;
            var path = ResolveWorktreePath(repositoryRoot, slug);
            if (existing.Contains(branch) || Directory.Exists(path) || File.Exists(path))
            {
                continue;
            }

            var parent = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var worktree = await git.AddWorktreeAsync(repositoryRoot, path, branch, startPoint, ct).ConfigureAwait(false);
            return new WorktreeSession(repositoryRoot, worktree.Path, branch, slug, baseCommit);
        }

        throw new GitException($"Could not find an unused branch name for '{baseSlug}'.", [], -1, string.Empty);
    }

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
            var reasons = new List<string>();
            var status = await git.GetStatusAsync(worktreePath, ct).ConfigureAwait(false);
            if (status.Count > 0)
            {
                reasons.Add($"{status.Count} uncommitted change{(status.Count == 1 ? string.Empty : "s")}");
            }

            var repositoryRoot = await git.GetRepositoryRootAsync(worktreePath, ct).ConfigureAwait(false) ?? repositoryPath;
            var baseRef = baseBranch ?? await git.GetDefaultBranchAsync(repositoryRoot, ct).ConfigureAwait(false);
            var head = await git.RevParseAsync(worktreePath, "HEAD", ct).ConfigureAwait(false);

            if (head is not null
                && await git.GetCurrentBranchAsync(worktreePath, ct).ConfigureAwait(false) is null
                && !await git.IsCommitReferencedAsync(worktreePath, head, ct).ConfigureAwait(false))
            {
                reasons.Add($"HEAD is detached at '{head}' and is not contained in any branch, remote branch or tag");
            }

            if (baseRef is null)
            {
                reasons.Add("no base branch could be determined, so unmerged work cannot be ruled out; pass baseBranch explicitly or force the removal");
            }
            else if (head is not null)
            {
                var ahead = await git.CountCommitsAheadAsync(worktreePath, head, baseRef, ct).ConfigureAwait(false);
                if (ahead > 0)
                {
                    reasons.Add($"{ahead} commit{(ahead == 1 ? string.Empty : "s")} not merged into '{baseRef}'");
                }
            }

            if (reasons.Count > 0)
            {
                throw new WorktreeRemovalRefusedException(worktreePath, reasons);
            }
        }

        await git.RemoveWorktreeAsync(repositoryPath, worktreePath, force, ct).ConfigureAwait(false);
    }

    public Task PruneAsync(string repositoryPath, CancellationToken ct = default) =>
        git.PruneWorktreesAsync(repositoryPath, ct);

    public string ResolveWorktreePath(string repositoryRoot, string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        var parent = System.IO.Path.GetDirectoryName(WorkspacePaths.Normalize(repositoryRoot));
        var root = Options.RootDirectory;
        root = string.IsNullOrWhiteSpace(root)
            ? System.IO.Path.Combine(parent ?? repositoryRoot, DefaultDirectoryName)
            : System.IO.Path.IsPathRooted(root)
                ? root
                : System.IO.Path.Combine(parent ?? repositoryRoot, root);

        var repositoryName = WorkspacePaths.FileName(repositoryRoot);
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(root, repositoryName, slug));
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
