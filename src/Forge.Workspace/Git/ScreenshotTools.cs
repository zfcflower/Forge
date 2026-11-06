using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>
/// The agent's screenshot tool: <c>pr_screenshots</c>. One tool, side effect <see cref="ToolSideEffect.Execute"/> – it runs a
/// command from the repository, pushes a branch and edits the pull request – so the approval policy asks the user. It is
/// registered next to <see cref="PrTools"/> and works in <see cref="ToolContext.WorkingDirectory"/>, the session's worktree.
/// The work itself is <see cref="ScreenshotRunner"/>, which the Git panel's screenshot button uses too; this class is the
/// agent-facing surface: argument validation, the pull request preconditions and a "next step" for every failure.
/// </summary>
public sealed class ScreenshotTools
{
    public const string ScreenshotsName = "pr_screenshots";

    /// <param name="captureOptions">Timeout, temporary directory and the like; the defaults are right for the app.</param>
    public ScreenshotTools(GitCli git, IPrPlatform platform, GitToolSettings settings, ScreenshotCaptureOptions? captureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(settings);

        Screenshots = new ScreenshotsTool(git, platform, settings, captureOptions ?? new ScreenshotCaptureOptions());
        All = [Screenshots];
    }

    /// <summary>
    /// <c>{"command"?: string, "files"?: string[], "cleanup_paths"?: string[], "base"?: string}</c> (Execute). Captures before and
    /// after images (<see cref="ScreenshotCapture"/>), publishes them (<see cref="ScreenshotPublisher"/>) and removes the
    /// temporary files afterwards (<see cref="ScreenshotCleanup"/>). With neither <c>.forge/screenshots.json</c>, <c>command</c> nor
    /// <c>files</c> it says that it cannot capture automatically and does nothing.
    /// </summary>
    public ITool Screenshots { get; }

    /// <summary>The one tool, for registration.</summary>
    public IReadOnlyList<ITool> All { get; }
}

internal sealed class ScreenshotsTool : PrLifecycleToolBase
{
    private readonly GitToolSettings settings;
    private readonly ScreenshotCaptureOptions captureOptions;

    public ScreenshotsTool(GitCli git, IPrPlatform platform, GitToolSettings settings, ScreenshotCaptureOptions captureOptions)
        : base(
            git,
            platform,
            ScreenshotTools.ScreenshotsName,
            Description,
            Schema,
            ToolSideEffect.Execute)
    {
        this.settings = settings;
        this.captureOptions = captureOptions;
    }

    private const string Description =
        "Take before/after screenshots of a user-visible change and put them in the '## Screenshots' section of the pull request " +
        "description. It runs the project's screenshot command (from .forge/screenshots.json, or the command you pass) once in this " +
        "worktree for the 'after' images and once in a temporary worktree of origin/<base> for the 'before' images, pairs the PNG files " +
        "by name, pushes them to the branch assets/<branch> (never into the pull request's files) and updates the description through " +
        "the same verified edit as pr_edit. It needs an open pull request (pr_create first) and, because it runs commands from the " +
        "repository and pushes, the user's approval. The command must write its PNG files into the directory named by the " +
        "FORGE_SCREENSHOT_DIR environment variable (with .forge/screenshots.json that is the configured output directory) and must also " +
        "run on the base branch: files that exist only on this branch are missing there. List a one-off capture script in " +
        "cleanup_paths: it is copied into the base worktree for the 'before' run and deleted at the end (a tracked one is removed with " +
        "a commit that is pushed). Instead of capturing, files publishes PNG files that already exist ('<name>-before.png' and " +
        "'<name>-after.png' pair up, any other name is an 'after' image). With no .forge/screenshots.json, no command and no files " +
        "it reports that it cannot capture automatically; do not invent images. Read pr_workflow with phase 'screenshots' first.";

    private const string Schema =
        """
        {"type":"object","properties":{"command":{"type":"string","description":"One-line shell command that writes PNG files into the directory in the FORGE_SCREENSHOT_DIR environment variable. Replaces the command of .forge/screenshots.json. Runs with this worktree as its directory, so it can also use relative paths. Not with files."},"files":{"type":"array","items":{"type":"string"},"description":"Publish these existing PNG files instead of capturing (absolute, or relative to the worktree). <name>-before.png and <name>-after.png pair up; any other <name>.png is shown as an after image."},"cleanup_paths":{"type":"array","items":{"type":"string"},"description":"Temporary files or directories inside the worktree to delete once the description is updated (for example the one-off capture script). They are also copied into the base worktree for the before run. Tracked files are removed with a commit that is pushed."},"base":{"type":"string","description":"Base branch to compare with (default: the pull request's base). Compared as origin/<base>."}},"additionalProperties":false}
        """;

    protected override async ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments, "command", "files", "cleanup_paths", "base");
        var command = GitToolJson.String(arguments, "command");
        var files = GitToolJson.Strings(arguments, "files");
        var cleanupPaths = GitToolJson.Strings(arguments, "cleanup_paths");
        var requestedBase = GitToolJson.String(arguments, "base");

        if (command is not null && command.Trim().Length == 0)
        {
            return new ToolResult("command must not be blank; leave it out to use .forge/screenshots.json.", IsError: true);
        }

        if (command is not null && command.Contains('\n', StringComparison.Ordinal))
        {
            return new ToolResult("command must be a single line; put several steps in a script and run that.", IsError: true);
        }

        if (files.Count > 0 && (command is not null || requestedBase is not null))
        {
            return new ToolResult("files publishes existing images and runs nothing: do not combine it with command or base.", IsError: true);
        }

        var worktree = context.WorkingDirectory;
        var capturing = files.Count == 0;
        if (capturing && ConfigurationRefusal(worktree, command) is { } refused)
        {
            return refused;
        }

        if (await RequireReadyAsync(worktree, ct).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        var view = await Platform.ViewPullRequestAsync(worktree, null, ct).ConfigureAwait(false);
        if (!view.Success || view.Value is not { } pullRequest)
        {
            return PrLifecycleFailure.FromPlatform(view);
        }

        if (pullRequest.State != PrState.Open)
        {
            return PrLifecycleFailure.Refuse(
                ScreenshotTools.ScreenshotsName,
                "pull request state",
                $"Pull request #{pullRequest.Number.ToString(CultureInfo.InvariantCulture)} is {pullRequest.State}; screenshots go into an open one.",
                "Nothing was changed.");
        }

        string? baseName = null;
        if (capturing)
        {
            var (resolved, baseError) = await ResolveBaseAsync(worktree, requestedBase ?? pullRequest.BaseRefName, ct)
                .ConfigureAwait(false);
            if (baseError is not null)
            {
                return baseError;
            }

            baseName = resolved;
        }

        var run = await new ScreenshotRunner(Git, Platform, settings, captureOptions)
            .RunAsync(
                worktree,
                baseName,
                new ScreenshotRunRequest { Command = command, Files = files, CleanupPaths = cleanupPaths },
                ct)
            .ConfigureAwait(false);
        return run.Success ? new ToolResult(run.Message) : FailureOf(run);
    }

    /// <summary>
    /// The declaration has to be usable before anything runs, and without a declaration and a command there is nothing to
    /// capture at all. Checked here so the tool answers before it starts a gh process.
    /// </summary>
    private static ToolResult? ConfigurationRefusal(string worktree, string? command)
    {
        var loaded = ScreenshotConfigLoader.Load(worktree);
        if (loaded.State == ScreenshotConfigState.Invalid)
        {
            return PrLifecycleFailure.Refuse(
                ScreenshotTools.ScreenshotsName,
                "configuration",
                loaded.Error!,
                "Fix .forge/screenshots.json, or pass command to capture without it.");
        }

        if (loaded.State == ScreenshotConfigState.Missing && command is null)
        {
            return PrLifecycleFailure.Refuse(
                ScreenshotTools.ScreenshotsName,
                "nothing to run",
                ScreenshotCapture.CannotCaptureMessage,
                "Tell the user the screenshots cannot be taken automatically and why. Do not guess how to start the application and " +
                "never fabricate an image. If the user has screenshots, pass them as files.");
        }

        return null;
    }

    /// <summary>Turns a stopped run into the tool error the agent sees: what happened, and what to do next.</summary>
    private static ToolResult FailureOf(ScreenshotRunResult run) => run.Stage switch
    {
        ScreenshotRunStage.Configuration => PrLifecycleFailure.Refuse(
            ScreenshotTools.ScreenshotsName,
            "configuration",
            run.Message,
            "Fix .forge/screenshots.json, or pass command. With neither, tell the user the screenshots cannot be taken " +
            "automatically; never fabricate an image."),
        ScreenshotRunStage.Files => PrLifecycleFailure.Refuse(
            ScreenshotTools.ScreenshotsName,
            "files",
            run.Message,
            "Nothing was pushed. Give existing PNG files named <name>-before.png and <name>-after.png."),
        ScreenshotRunStage.CleanupPaths => PrLifecycleFailure.Refuse(
            ScreenshotTools.ScreenshotsName,
            "cleanup_paths",
            run.Message,
            "Nothing was captured or removed. Fix cleanup_paths and call again."),
        ScreenshotRunStage.Base => GitToolFailure.ToToolResult(
            new GitRemoteResult(run.Failure, run.Message, string.Empty),
            "Check the origin remote and the base branch with git_status; the before images are taken from origin/<base>."),
        ScreenshotRunStage.Capture => new ToolResult(
            (run.Capture is { } captured ? ScreenshotRunner.DescribeCaptureFailure(captured) : run.Message) +
            "\nNext: nothing was pushed and the description is unchanged. Fix the command (it must write PNG files into the " +
            "directory in FORGE_SCREENSHOT_DIR and also run on the base branch) and call again, or tell the user the screenshots " +
            "cannot be taken automatically. Never fabricate an image.",
            IsError: true),
        _ => PublishFailure(run),
    };

    private static ToolResult PublishFailure(ScreenshotRunResult run)
    {
        var published = run.Publish;
        if (published is null)
        {
            return GitToolFailure.ToToolResult(new GitRemoteResult(run.Failure, run.Message, string.Empty));
        }

        if (published.Stage != ScreenshotPublishStage.Body)
        {
            var hint = published.Stage switch
            {
                ScreenshotPublishStage.Remote => "Check the origin remote with git_status; the image links are built from its GitHub address.",
                ScreenshotPublishStage.Objects => "The images could not be stored; check disk space and that the files are readable PNGs.",
                _ => null,
            };
            return GitToolFailure.ToToolResult(new GitRemoteResult(published.Failure, published.Message, string.Empty), hint);
        }

        var text = new StringBuilder(512);
        text.Append(published.Reason != PrFailureReason.None ? published.Reason.ToString() : GitToolFailure.FailureName(published.Failure))
            .Append(": ").Append(published.Message);
        text.Append("\nNext: the images are on the remote already. Retry pr_screenshots, or write the section yourself with pr_edit (section Screenshots) using:\n")
            .Append(published.Markdown);
        return new ToolResult(text.ToString(), IsError: true);
    }
}
