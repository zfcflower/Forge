using System.Text;

namespace Forge.Workspace;

/// <summary>Where a screenshot run stopped; <see cref="Done"/> means the images are in the pull request description.</summary>
public enum ScreenshotRunStage
{
    /// <summary>Nothing went wrong.</summary>
    Done,

    /// <summary><c>.forge/screenshots.json</c> is unusable, or there is no command to run at all.</summary>
    Configuration,

    /// <summary>The files to publish were refused.</summary>
    Files,

    /// <summary>The paths to clean up afterwards were refused.</summary>
    CleanupPaths,

    /// <summary>The base branch could not be read or fetched.</summary>
    Base,

    /// <summary>The command failed, timed out or wrote no image; nothing was pushed.</summary>
    Capture,

    /// <summary>The images are stored, but pushing them or updating the description failed.</summary>
    Publish,
}

/// <summary>
/// What to do: capture new images (with <see cref="Command"/> or the project's own command) or publish files that already
/// exist, and which temporary paths to remove once the description holds them. These are the arguments of the
/// <c>pr_screenshots</c> tool; the Git panel's button fills in only what the project declares.
/// </summary>
public sealed record ScreenshotRunRequest
{
    /// <summary>One-line shell command that writes PNG files into <see cref="ScreenshotCapture.OutputDirectoryVariable"/>; null uses the project's.</summary>
    public string? Command { get; init; }

    /// <summary>Existing PNG files to publish instead of capturing; empty means "capture".</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Temporary paths inside the worktree to remove once the description was updated.</summary>
    public IReadOnlyList<string> CleanupPaths { get; init; } = [];
}

/// <summary>
/// Outcome of <see cref="ScreenshotRunner.RunAsync"/>. <see cref="Message"/> is the report in plain sentences (what was
/// published, which screens, what was cleaned up) and, on failure, what stopped the run: the tool appends its "next step"
/// hints, the Git panel shows it as the result of the operation along with the hint of <see cref="Failure"/>.
/// </summary>
public sealed record ScreenshotRunResult(
    bool Success,
    ScreenshotRunStage Stage,
    string Message,
    IReadOnlyList<ScreenshotPair> Pairs,
    IReadOnlyList<string> Notes,
    GitRemoteFailureKind Failure = GitRemoteFailureKind.None,
    PrFailureReason Reason = PrFailureReason.None,
    string? PullRequestUrl = null,
    ScreenshotCaptureResult? Capture = null,
    ScreenshotPublishResult? Publish = null,
    ScreenshotCleanupReport? Cleanup = null);

/// <summary>
/// One full screenshot step, shared by the <c>pr_screenshots</c> tool and the Git panel's "Screenshots" button so the two
/// cannot drift: read the project's declaration (the caller has already resolved the pull request), capture before and
/// after (<see cref="ScreenshotCapture"/>), publish (<see cref="ScreenshotPublisher"/>) and finally remove the temporary
/// paths. Nothing is captured or published when a check before the capture refuses the run, and the capture directory is
/// deleted whatever happens.
/// </summary>
public sealed class ScreenshotRunner(GitCli git, IPrPlatform platform, GitToolSettings settings, ScreenshotCaptureOptions? options = null)
{
    private readonly GitCli git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly IPrPlatform platform = platform ?? throw new ArgumentNullException(nameof(platform));
    private readonly GitToolSettings settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly ScreenshotCaptureOptions options = options ?? new ScreenshotCaptureOptions();

    /// <summary>
    /// Runs the step in <paramref name="worktree"/> against <paramref name="baseBranch"/> (a branch name; it is compared as
    /// <c>origin/&lt;base&gt;</c> and fetched first). Cancellation throws after everything the run created is gone.
    /// </summary>
    public async Task<ScreenshotRunResult> RunAsync(
        string worktree,
        string? baseBranch,
        ScreenshotRunRequest request,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktree);
        ArgumentNullException.ThrowIfNull(request);
        worktree = WorkspacePaths.Normalize(worktree);

        var capturing = request.Files.Count == 0;
        ScreenshotConfig? config = null;
        if (capturing)
        {
            var loaded = ScreenshotConfigLoader.Load(worktree);
            if (loaded.State == ScreenshotConfigState.Invalid)
            {
                return Failed(ScreenshotRunStage.Configuration, loaded.Error!);
            }

            config = loaded.Config;
            if (config is null && string.IsNullOrWhiteSpace(request.Command))
            {
                return Failed(ScreenshotRunStage.Configuration, ScreenshotCapture.CannotCaptureMessage);
            }
        }

        var cleanup = new ScreenshotCleanup(git, settings);
        var plan = await cleanup.PlanAsync(worktree, request.CleanupPaths, ct).ConfigureAwait(false);
        if (!plan.Success)
        {
            return Failed(ScreenshotRunStage.CleanupPaths, plan.Error!);
        }

        var notes = new List<string>();
        IReadOnlyList<ScreenshotPair> pairs;
        ScreenshotCaptureResult? captureResult = null;
        string? baseLabel = null;
        string? captureDirectory = null;
        try
        {
            if (capturing)
            {
                var baseName = PrLifecycleFailure.StripRemotePrefix(baseBranch?.Trim() ?? string.Empty);
                if (baseName.Length == 0 || baseName[0] == '-')
                {
                    return Failed(
                        ScreenshotRunStage.Base,
                        "No base branch is known: pass the branch to compare with (it is read as origin/<base>).",
                        GitRemoteFailureKind.Other);
                }

                var fetch = await git.FetchAsync(worktree, "origin", NetworkOptions(), ct).ConfigureAwait(false);
                if (!fetch.Success)
                {
                    return Failed(
                        ScreenshotRunStage.Base,
                        $"Could not fetch origin before capturing the \"before\" images: {fetch.Message}",
                        fetch.Failure);
                }

                baseLabel = "origin/" + baseName;
                var capture = new ScreenshotCapture(git, options with { OverlayPaths = plan.RelativePaths });
                var captured = await capture.CaptureAsync(worktree, baseLabel, request.Command, ct).ConfigureAwait(false);
                notes.AddRange(captured.Notes);
                if (!captured.Success)
                {
                    return new ScreenshotRunResult(
                        false,
                        ScreenshotRunStage.Capture,
                        captured.Message,
                        [],
                        notes,
                        GitRemoteFailureKind.Other,
                        PrFailureReason.None,
                        null,
                        captured);
                }

                captureDirectory = captured.CaptureDirectory;
                pairs = captured.Pairs;
                captureResult = captured;
            }
            else
            {
                var built = ScreenshotFiles.Build(worktree, request.Files);
                if (!built.Success)
                {
                    return Failed(ScreenshotRunStage.Files, built.Error!);
                }

                pairs = built.Pairs;
            }

            var published = await new ScreenshotPublisher(git, platform, settings)
                .PublishAsync(worktree, pairs, baseLabel, ct).ConfigureAwait(false);
            if (!published.Success)
            {
                return new ScreenshotRunResult(
                    false,
                    ScreenshotRunStage.Publish,
                    published.Message,
                    pairs,
                    notes,
                    published.Failure,
                    published.Reason,
                    published.PullRequestUrl,
                    null,
                    published);
            }

            var report = new StringBuilder(512);
            report.Append(published.Message);
            AppendScreens(report, pairs);
            if (published.PullRequestUrl is { Length: > 0 } url)
            {
                report.Append("\nPull request: ").Append(url);
            }

            AppendNotes(report, notes);

            ScreenshotCapture.DeleteCaptureDirectory(captureDirectory);
            captureDirectory = null;

            var cleaned = plan.Targets.Count > 0
                ? await cleanup.RunAsync(worktree, plan, ct).ConfigureAwait(false)
                : null;
            AppendCleanup(report, cleaned);

            return new ScreenshotRunResult(
                true,
                ScreenshotRunStage.Done,
                report.ToString(),
                pairs,
                notes,
                GitRemoteFailureKind.None,
                PrFailureReason.None,
                published.PullRequestUrl,
                captureResult,
                published,
                cleaned);
        }
        finally
        {
            ScreenshotCapture.DeleteCaptureDirectory(captureDirectory);
        }
    }

    /// <summary>The message of a capture that failed, with what the two runs reported, for a log or a tool result.</summary>
    public static string DescribeCaptureFailure(ScreenshotCaptureResult captured)
    {
        ArgumentNullException.ThrowIfNull(captured);
        var text = new StringBuilder(512);
        text.Append("Screenshot capture failed: ").Append(captured.Message);
        AppendNotes(text, captured.Notes);
        return text.ToString();
    }

    private static void AppendScreens(StringBuilder report, IReadOnlyList<ScreenshotPair> pairs)
    {
        report.Append("\nScreens:");
        foreach (var pair in pairs)
        {
            report.Append("\n  ").Append(pair.Name).Append(pair.HasBefore && pair.HasAfter ? " (before and after)" : pair.HasAfter ? " (after only)" : " (before only)");
        }
    }

    private static void AppendNotes(StringBuilder report, IReadOnlyList<string> notes)
    {
        if (notes.Count == 0)
        {
            return;
        }

        report.Append("\nNotes:");
        foreach (var note in notes)
        {
            report.Append("\n  ").Append(note);
        }
    }

    private static void AppendCleanup(StringBuilder report, ScreenshotCleanupReport? cleaned)
    {
        if (cleaned is null)
        {
            return;
        }

        report.Append("\nCleanup: ");
        if (cleaned.Deleted.Count == 0 && cleaned.AlreadyGone.Count == 0)
        {
            report.Append("nothing to remove.");
        }
        else
        {
            if (cleaned.Deleted.Count > 0)
            {
                report.Append("removed ").Append(string.Join(", ", cleaned.Deleted)).Append('.');
            }

            if (cleaned.AlreadyGone.Count > 0)
            {
                report.Append(" Already gone: ").Append(string.Join(", ", cleaned.AlreadyGone)).Append('.');
            }
        }

        if (cleaned.CommitSha is not null)
        {
            report.Append(" Committed the removal of tracked files as ").Append(PrLifecycleFailure.Short(cleaned.CommitSha)).Append('.');
        }

        if (cleaned.PushMessage is not null)
        {
            report.Append(' ').Append(cleaned.PushMessage);
        }

        foreach (var problem in cleaned.Problems)
        {
            report.Append("\nWARNING: ").Append(problem);
        }
    }

    private GitNetworkOptions NetworkOptions()
    {
        var timeout = settings.NetworkTimeout();
        return timeout > TimeSpan.Zero ? new GitNetworkOptions { Timeout = timeout } : new GitNetworkOptions();
    }

    private static ScreenshotRunResult Failed(
        ScreenshotRunStage stage,
        string message,
        GitRemoteFailureKind failure = GitRemoteFailureKind.None) =>
        new(false, stage, message, [], [], failure);
}
