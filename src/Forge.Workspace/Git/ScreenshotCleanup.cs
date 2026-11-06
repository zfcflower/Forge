using System.Globalization;

namespace Forge.Workspace;

/// <summary>One path the agent asked to have removed, resolved against the worktree.</summary>
public sealed record ScreenshotCleanupTarget(string FullPath, string Relative);

/// <summary>
/// The checked list of paths to remove. <see cref="TrackedFiles"/> are the files git tracks at or below the targets, which is
/// what decides whether the removal needs a commit. <see cref="Error"/> is set when the list is refused, and then nothing was
/// removed or will be.
/// </summary>
public sealed record ScreenshotCleanupPlan(
    IReadOnlyList<ScreenshotCleanupTarget> Targets,
    IReadOnlyList<string> TrackedFiles,
    string? Error)
{
    public static ScreenshotCleanupPlan Empty { get; } = new([], [], null);

    public bool Success => Error is null;

    /// <summary>The paths relative to the worktree, for <see cref="ScreenshotCaptureOptions.OverlayPaths"/>.</summary>
    public IReadOnlyList<string> RelativePaths => Targets.Select(static target => target.Relative).ToArray();
}

/// <summary>What <see cref="ScreenshotCleanup.RunAsync"/> did. <see cref="Problems"/> is empty when everything worked.</summary>
public sealed record ScreenshotCleanupReport(
    IReadOnlyList<string> Deleted,
    IReadOnlyList<string> AlreadyGone,
    string? CommitSha,
    string? PushMessage,
    IReadOnlyList<string> Problems)
{
    public bool Success => Problems.Count == 0;
}

/// <summary>
/// Removes the temporary files a one-off capture left in the worktree (the script the agent wrote, a scratch output directory).
/// Deleting is dangerous, so the list is checked before anything is captured (<see cref="PlanAsync"/>): every path must be inside
/// the worktree, must not be the worktree itself or anything under <c>.git</c>, and must not pass through a symbolic link;
/// removing tracked files is capped. Only after the screenshots are in the description does <see cref="RunAsync"/> delete. When
/// tracked files were among them the removal is committed – exactly those files, other staged work stays out – and pushed with
/// the same lease as the pull request tools use.
/// </summary>
public sealed class ScreenshotCleanup(GitCli git, GitToolSettings settings)
{
    /// <summary>More tracked files than this in one cleanup is almost certainly a wrong path (a whole source directory).</summary>
    public const int MaxTrackedFiles = 25;

    public const int MaxPaths = 50;

    public const string CommitMessage = "chore: remove temporary screenshot capture files";

    private readonly GitCli git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly GitToolSettings settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public async Task<ScreenshotCleanupPlan> PlanAsync(string worktree, IReadOnlyList<string> paths, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktree);
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return ScreenshotCleanupPlan.Empty;
        }

        if (paths.Count > MaxPaths)
        {
            return Refused($"cleanup_paths has {paths.Count.ToString(CultureInfo.InvariantCulture)} entries; at most {MaxPaths.ToString(CultureInfo.InvariantCulture)} are accepted.");
        }

        worktree = WorkspacePaths.Normalize(worktree);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var targets = new List<ScreenshotCleanupTarget>(paths.Count);
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Contains('\0', StringComparison.Ordinal))
            {
                return Refused("cleanup_paths contains an empty path.");
            }

            var full = WorkspacePaths.Normalize(Path.Combine(worktree, raw.Trim()));
            if (!WorkspacePaths.IsUnder(full, worktree))
            {
                return Refused($"'{raw}' is outside the worktree ({worktree}); cleanup_paths only accepts paths inside it.");
            }

            if (WorkspacePaths.Equals(full, worktree))
            {
                return Refused($"'{raw}' is the worktree itself; list the files or directories to remove.");
            }

            var relative = Path.GetRelativePath(worktree, full).Replace('\\', '/');
            if (relative.Split('/')[0].Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                return Refused($"'{raw}' is inside .git, which is never removed.");
            }

            try
            {
                WorkspacePaths.EnsurePlainPathInside(worktree, full, "cleanup_paths");
            }
            catch (ArgumentException exception)
            {
                return Refused($"'{raw}' is refused: {exception.Message}");
            }

            if (seen.Add(full))
            {
                targets.Add(new ScreenshotCleanupTarget(full, relative));
            }
        }

        IReadOnlyList<string> tracked;
        try
        {
            tracked = await git.ListTrackedFilesAsync(worktree, targets.Select(static target => target.Relative).ToArray(), ct).ConfigureAwait(false);
        }
        catch (GitException exception)
        {
            return Refused("Could not tell which of the cleanup_paths are tracked: " + exception.Message);
        }

        if (tracked.Count > MaxTrackedFiles)
        {
            return Refused(
                $"cleanup_paths covers {tracked.Count.ToString(CultureInfo.InvariantCulture)} tracked files; more than {MaxTrackedFiles.ToString(CultureInfo.InvariantCulture)} " +
                "is refused as a probable mistake. List the temporary files individually.");
        }

        return new ScreenshotCleanupPlan(targets, tracked, null);
    }

    public async Task<ScreenshotCleanupReport> RunAsync(string worktree, ScreenshotCleanupPlan plan, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktree);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.Success)
        {
            return new ScreenshotCleanupReport([], [], null, null, [plan.Error!]);
        }

        var deleted = new List<string>();
        var gone = new List<string>();
        var problems = new List<string>();
        foreach (var target in plan.Targets)
        {
            if (File.Exists(target.FullPath))
            {
                ScreenshotFileSystem.DeleteFile(target.FullPath);
            }
            else if (Directory.Exists(target.FullPath))
            {
                ScreenshotFileSystem.DeleteDirectory(target.FullPath);
            }
            else
            {
                gone.Add(target.Relative);
                continue;
            }

            if (WorkspacePaths.Exists(target.FullPath))
            {
                problems.Add($"Could not delete '{target.Relative}'.");
            }
            else
            {
                deleted.Add(target.Relative);
            }
        }

        string? commitSha = null;
        string? pushMessage = null;
        if (plan.TrackedFiles.Count > 0)
        {
            (commitSha, pushMessage) = await CommitAndPushAsync(worktree, plan.TrackedFiles, problems, ct).ConfigureAwait(false);
        }

        return new ScreenshotCleanupReport(deleted, gone, commitSha, pushMessage, problems);
    }

    private async Task<(string? Commit, string? Push)> CommitAndPushAsync(
        string worktree,
        IReadOnlyList<string> tracked,
        List<string> problems,
        CancellationToken ct)
    {
        string? branch;
        GitCommit commit;
        try
        {
            branch = await git.GetCurrentBranchAsync(worktree, ct).ConfigureAwait(false);
            commit = await git.CommitPathsAsync(worktree, tracked, CommitMessage, ct).ConfigureAwait(false);
        }
        catch (GitException exception)
        {
            problems.Add(
                "The tracked files were deleted from disk but their removal could not be committed: " + exception.Message +
                " Commit the deletion yourself or restore the files.");
            return (null, null);
        }

        if (branch is null)
        {
            problems.Add($"The removal was committed ({PrLifecycleFailure.Short(commit.Id)}) but HEAD is detached, so nothing was pushed.");
            return (commit.Id, null);
        }

        var outcome = await new PrBranchPublisher(git, settings).PublishAsync(worktree, branch, PrPushMode.New, pin: null, ct).ConfigureAwait(false);
        if (!outcome.Success)
        {
            problems.Add(
                $"The removal was committed ({PrLifecycleFailure.Short(commit.Id)}) but not pushed: {outcome.Failure!.Content} " +
                "Push it with git_sync when ready.");
            return (commit.Id, null);
        }

        return (commit.Id, outcome.Message);
    }

    private static ScreenshotCleanupPlan Refused(string error) => new([], [], error);
}
