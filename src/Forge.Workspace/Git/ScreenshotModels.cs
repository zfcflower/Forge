namespace Forge.Workspace;

/// <summary>
/// One screen that was captured, matched by file name. <see cref="Name"/> is the file name without extension, made safe for
/// a git tree entry and a URL (letters, digits, <c>.</c>, <c>_</c>, <c>-</c>). An image that exists on one side only is kept:
/// the other path is null and the rendered table says so. The paths are local files; the published names are
/// <see cref="BeforeFileName"/> and <see cref="AfterFileName"/>.
/// </summary>
public sealed record ScreenshotPair(string Name, string? BeforePath, string? AfterPath)
{
    public string BeforeFileName => Name + "-before.png";

    public string AfterFileName => Name + "-after.png";

    public bool HasBefore => BeforePath is not null;

    public bool HasAfter => AfterPath is not null;
}

/// <summary>How one run of the screenshot command went: which side, the exit code, whether the timeout killed it, and the end of its output.</summary>
public sealed record ScreenshotRunReport(string Side, int ExitCode, bool TimedOut, TimeSpan Duration, string OutputTail)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Outcome of <see cref="ScreenshotCapture.CaptureAsync(string, string, string?, CancellationToken)"/>. On success
/// <see cref="Pairs"/> is non-empty and <see cref="CaptureDirectory"/> holds the copied images; the caller owns that directory
/// and removes it with <see cref="ScreenshotCapture.DeleteCaptureDirectory"/> once the images are published. On failure there
/// are no images and no directory: everything the capture created is already gone.
/// </summary>
public sealed record ScreenshotCaptureResult(
    bool Success,
    string Message,
    IReadOnlyList<ScreenshotPair> Pairs,
    string? CaptureDirectory,
    ScreenshotRunReport? After,
    ScreenshotRunReport? Before,
    IReadOnlyList<string> Notes)
{
    internal static ScreenshotCaptureResult Failed(
        string message,
        ScreenshotRunReport? after = null,
        ScreenshotRunReport? before = null,
        IReadOnlyList<string>? notes = null) =>
        new(false, message, [], null, after, before, notes ?? []);
}

/// <summary>Knobs for <see cref="ScreenshotCapture"/>; the defaults are what the tool uses.</summary>
public sealed record ScreenshotCaptureOptions
{
    /// <summary>
    /// Overrides the time one run of the command may take. Without it the value from <c>.forge/screenshots.json</c> applies,
    /// else five minutes. The configuration file itself cannot go below ten seconds; this override exists so callers and
    /// tests can set a shorter limit deliberately.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Where the temporary worktree and the collected images are created; the system temporary directory when null.</summary>
    public string? TempRoot { get; init; }

    /// <summary>
    /// Files or directories (relative to the session worktree) that are copied into the temporary base worktree before the
    /// "before" run, but only where the base has no file of that name. A one-off capture script the agent wrote exists on the
    /// branch only; without this the "before" run could never start it. Nothing the base already has is replaced.
    /// </summary>
    public IReadOnlyList<string> OverlayPaths { get; init; } = [];
}

/// <summary>Where a step of the screenshot publication stopped.</summary>
public enum ScreenshotPublishStage
{
    /// <summary>Nothing went wrong.</summary>
    Done,

    /// <summary>The branch or the remote could not be determined.</summary>
    Remote,

    /// <summary>The images could not be stored as git objects.</summary>
    Objects,

    /// <summary>The assets branch could not be pushed.</summary>
    Push,

    /// <summary>The images are on the remote but the pull request description could not be updated.</summary>
    Body,
}

/// <summary>
/// Outcome of <see cref="ScreenshotPublisher.PublishAsync"/>. After a failure at <see cref="ScreenshotPublishStage.Body"/> the
/// images are already pushed: <see cref="AssetsCommit"/> and <see cref="Markdown"/> are then set so the section can be written
/// by hand with <c>pr_edit</c>.
/// </summary>
public sealed record ScreenshotPublishResult(
    bool Success,
    ScreenshotPublishStage Stage,
    string Message,
    GitRemoteFailureKind Failure = GitRemoteFailureKind.None,
    PrFailureReason Reason = PrFailureReason.None,
    string? AssetsRef = null,
    string? AssetsCommit = null,
    string? Markdown = null,
    string? PullRequestUrl = null);

/// <summary>A repository on a GitHub-style host, as read from a remote address.</summary>
public sealed record GitHubRepository(string Host, string Owner, string Name)
{
    /// <summary>
    /// The address that shows <paramref name="fileName"/> of <paramref name="commit"/> as an image in a pull request description:
    /// <c>https://host/owner/repo/blob/&lt;sha&gt;/&lt;file&gt;?raw=true</c>. Pinned to a commit, so it keeps working when the assets
    /// branch is rewritten.
    /// </summary>
    public string RawImageUrl(string commit, string fileName) =>
        $"https://{Host}/{Owner}/{Name}/blob/{commit}/{Uri.EscapeDataString(fileName)}?raw=true";

    /// <summary>
    /// Reads the host, owner and repository from a remote address: <c>https://[user@]host/owner/repo[.git]</c>,
    /// <c>ssh://[user@]host[:port]/owner/repo[.git]</c>, <c>git://host/owner/repo</c> or the scp-like
    /// <c>[user@]host:owner/repo[.git]</c>. Anything else – a local path, a <c>file://</c> address, a path with more or fewer than
    /// two segments (GitLab subgroups) – is null: the image links are GitHub's, nothing else is guessed.
    /// </summary>
    public static GitHubRepository? Parse(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var text = remoteUrl.Trim();
        string host;
        string path;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || uri.Host.Length == 0
                || uri.Scheme is not ("https" or "http" or "ssh" or "git"))
            {
                return null;
            }

            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else
        {
            if (text[0] is '/' or '.' or '\\' or '~')
            {
                return null;
            }

            var colon = text.IndexOf(':');
            if (colon <= 0 || colon == text.Length - 1)
            {
                return null;
            }

            var authority = text[..colon];
            var at = authority.LastIndexOf('@');
            host = at >= 0 ? authority[(at + 1)..] : authority;
            path = text[(colon + 1)..];

            // A single letter before the colon is a Windows drive, not a host.
            if (host.Length < 2 || host.Contains('/') || host.Contains('\\'))
            {
                return null;
            }
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
        {
            return null;
        }

        var repository = segments[1];
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repository = repository[..^4];
        }

        return IsName(segments[0]) && IsName(repository) && IsHost(host)
            ? new GitHubRepository(host, segments[0], repository)
            : null;
    }

    private static bool IsName(string value)
    {
        if (value.Length == 0 || value is "." or "..")
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHost(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-'))
            {
                return false;
            }
        }

        return value.Length > 0;
    }
}
