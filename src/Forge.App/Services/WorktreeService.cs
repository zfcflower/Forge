using System.Diagnostics;
using Forge.App.Localization;
using Forge.Workspace;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Services;

/// <summary>
/// Thin glue over <see cref="WorktreeManager"/>: sessions get the sibling layout
/// (<c>&lt;repoParent&gt;/&lt;repoName&gt;.worktrees/&lt;slug&gt;</c>, branch <c>forge/&lt;slug&gt;</c>) and the general
/// workspace is created and initialised once. Neither worktree naming nor subprocess handling is implemented here.
/// </summary>
internal sealed class WorktreeService
{
    public const string GeneralFolderName = "General";

    private static readonly string[] GeneralFolders = ["scratch", "extensions", "files"];

    private readonly GitCli git;
    private readonly WorktreeManager worktrees;

    public WorktreeService(GitCli git)
    {
        ArgumentNullException.ThrowIfNull(git);
        this.git = git;
        worktrees = new WorktreeManager(git, new WorktreeOptions { Layout = WorktreeLayout.SiblingOfRepository });
    }

    public WorktreeManager Manager => worktrees;

    public Task<bool> IsRepositoryAsync(string path, CancellationToken ct) => git.IsRepositoryAsync(path, ct);

    /// <summary>Which branch a checkout directory has checked out; never throws for a non-repository or a detached HEAD.</summary>
    public Task<GitHead> GetHeadAsync(string path, CancellationToken ct) => git.GetHeadAsync(path, ct);

    /// <summary>Creates an unused worktree and its <c>forge/&lt;slug&gt;</c> branch; the branch starts at HEAD.</summary>
    public Task<WorktreeSession> CreateAsync(string repositoryRoot, string title, CancellationToken ct) =>
        worktrees.CreateAsync(repositoryRoot, title, ct);

    /// <summary>
    /// Creates a worktree on the caller's branch name; the branch starts at HEAD and the name is used as typed, so an
    /// invalid name throws <see cref="ArgumentException"/> and an existing branch throws <see cref="GitException"/>. A blank
    /// name falls back to the title overload.
    /// </summary>
    public Task<WorktreeSession> CreateAsync(string repositoryRoot, string title, string? branchName, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(branchName)
            ? worktrees.CreateAsync(repositoryRoot, title, ct)
            : worktrees.CreateAsync(repositoryRoot, title, branchName, ct);

    /// <summary>
    /// Creates a worktree on the caller's branch name with the branch starting at <paramref name="startPoint"/> (a local
    /// branch, a remote-tracking ref such as <c>origin/main</c>, or any revision) instead of at HEAD. The new branch never
    /// tracks the ref it started at; a start point that does not resolve throws <see cref="GitException"/>.
    /// </summary>
    public Task<WorktreeSession> CreateAsync(
        string repositoryRoot,
        string title,
        string? branchName,
        string startPoint,
        CancellationToken ct) =>
        worktrees.CreateAsync(repositoryRoot, title, branchName, startPoint, ct);

    /// <summary>
    /// As above, as a cone-mode sparse checkout of <paramref name="sparse"/>'s top-level directories when it is not null
    /// (7.9): <c>worktree add --no-checkout</c>, <c>sparse-checkout set --cone</c>, then the checkout; a failed step
    /// removes the unfinished worktree and the branch it created before the error is thrown.
    /// </summary>
    public Task<WorktreeSession> CreateAsync(
        string repositoryRoot,
        string title,
        string? branchName,
        string startPoint,
        SparseCheckout? sparse,
        CancellationToken ct) =>
        worktrees.CreateAsync(repositoryRoot, title, branchName, startPoint, ct, sparse);

    /// <summary>
    /// Best-effort cleanup of a worktree this app created a moment ago for a session whose row could not be written: the
    /// worktree is removed first, then the branch that <c>git worktree add -b</c> created for it
    /// (<see cref="WorktreeManager.TryDeleteCreatedBranchAsync"/>: only while the branch is still at its start commit).
    /// Neither step is forced - a worktree that holds anything (only a concurrent edit could have put it there) stays, and
    /// with it its branch, which is checked out there; a branch that moved, or that git does not consider merged
    /// (<c>branch -d</c>, never <c>-D</c>), stays too.
    /// Failures are traced and never thrown, because the caller is already reporting the original error; only an
    /// out-of-memory condition escapes.
    /// </summary>
    public async Task TryRemoveCreatedAsync(WorktreeSession worktree, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        try
        {
            await worktrees
                .RemoveAsync(worktree.RepositoryRoot, worktree.Path, force: false, baseBranch: worktree.BaseCommit, ct: ct)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Worktree.ZLogWarning($"could not remove the unused worktree '{worktree.Path}': {exception.Message}");
            return;
        }

        // Only the branch this creation made, and only while it is still at its start commit (12.4, rollback).
        await worktrees.TryDeleteCreatedBranchAsync(worktree, ct).ConfigureAwait(false);
    }

    /// <summary>Worktree path this app would use for <paramref name="slug"/>; shown in the project tree.</summary>
    public string ResolveWorktreePath(string repositoryRoot, string slug) =>
        worktrees.ResolveWorktreePath(repositoryRoot, slug);

    /// <summary>Worktrees git knows about for a repository, newest path first; the repository itself is skipped.</summary>
    public async Task<IReadOnlyList<GitWorktree>> ListAsync(string repositoryRoot, CancellationToken ct)
    {
        var root = await git.GetRepositoryRootAsync(repositoryRoot, ct).ConfigureAwait(false);
        if (root is null)
        {
            return [];
        }

        var all = await worktrees.ListAsync(root, ct).ConfigureAwait(false);
        var result = new List<GitWorktree>();
        foreach (var worktree in all)
        {
            if (!string.Equals(Path.GetFullPath(worktree.Path), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            {
                result.Add(worktree);
            }
        }

        return result;
    }

    /// <summary>
    /// Ensures the general workspace exists with a git repository and its three folders, so general sessions also get
    /// "changes" and review. Returns the workspace root.
    /// </summary>
    public async Task<string> EnsureGeneralWorkspaceAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var root = Path.GetFullPath(workspacePath);
        Directory.CreateDirectory(root);
        foreach (var folder in GeneralFolders)
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
        }

        if (await git.IsRepositoryAsync(root, ct).ConfigureAwait(false))
        {
            return root;
        }

        var init = await git.RunAsync(root, ["init", "-q"], ct).ConfigureAwait(false);
        if (!init.Success)
        {
            throw new GitException(
                ShellStrings.GitInitFailed(init.StandardError.Trim()).Resolve(),
                ["init"],
                init.ExitCode,
                init.StandardError);
        }

        var ignorePath = Path.Combine(root, ".gitignore");
        if (!File.Exists(ignorePath))
        {
            await File.WriteAllTextAsync(ignorePath, "bin/\nobj/\n", ct).ConfigureAwait(false);
        }

        if (await git.RevParseAsync(root, "HEAD", ct).ConfigureAwait(false) is null)
        {
            await git.RunAsync(root, ["add", "--all"], ct).ConfigureAwait(false);
            await git.RunAsync(
                    root,
                    ["-c", "user.name=Forge", "-c", "user.email=forge@localhost", "commit", "-q", "-m", "General workspace"],
                    ct)
                .ConfigureAwait(false);
        }

        return root;
    }

    /// <summary>Creates a fresh scratch folder for one general session: <c>scratch/&lt;yyyy-MM-dd&gt;-&lt;slug&gt;</c>.</summary>
    public static string CreateScratchFolder(string generalRoot, string title)
    {
        var scratchRoot = Path.Combine(generalRoot, "scratch");
        Directory.CreateDirectory(scratchRoot);
        var prefix = DateTimeOffset.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var slug = WorktreeManager.Slugify(title);
        for (var attempt = 1; attempt <= 200; attempt++)
        {
            var name = attempt == 1
                ? prefix + "-" + slug
                : prefix + "-" + slug + "-" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var path = Path.Combine(scratchRoot, name);
            if (Directory.Exists(path) || File.Exists(path))
            {
                continue;
            }

            Directory.CreateDirectory(path);
            return path;
        }

        var fallback = Path.Combine(scratchRoot, prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}
