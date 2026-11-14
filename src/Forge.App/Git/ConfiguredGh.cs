using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>
/// The GitHub CLI as the settings currently name it. <see cref="GhCli"/> is immutable (its executable is fixed at
/// construction), but the <c>gitGhPath</c> preference is loaded after the composition root has built its graph and can
/// change while the app runs; so every consumer goes through this object, which re-reads the path on each use and builds a
/// new <see cref="GhCli"/> and <see cref="GitHubPlatform"/> only when the path differs from the one it last built with.
/// An edit in the settings therefore applies to the next gh call without a restart. Thread-safe: the pair is swapped as one
/// immutable object.
/// </summary>
internal sealed class ConfiguredGh
{
    private readonly Func<string?> path;
    private readonly GitCli git;
    private readonly Func<TimeSpan> networkTimeout;
    private Binding? current;

    /// <param name="path">The configured executable; null, empty or whitespace means <c>gh</c> from PATH. Read on every use.</param>
    /// <param name="git">The git wrapper gh's repository-host lookup shares.</param>
    /// <param name="networkTimeout">Per-call timeout of gh, read on every call like the git preferences.</param>
    public ConfiguredGh(Func<string?> path, GitCli git, Func<TimeSpan> networkTimeout)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(networkTimeout);
        this.path = path;
        this.git = git;
        this.networkTimeout = networkTimeout;
    }

    /// <summary>The <see cref="GhCli"/> for the path configured right now.</summary>
    public GhCli Cli => Resolve().Cli;

    /// <summary>The pull request platform on top of <see cref="Cli"/>; what the tools and the panel talk to.</summary>
    public IPrPlatform Platform => Resolve().Platform;

    /// <summary>The login lookup of the pull request comment tools, on the gh that is configured when it runs.</summary>
    public PrUserResolver UserResolver =>
        (workingDirectory, ct) => PrCommentUser.FromGh(Resolve().Cli)(workingDirectory, ct);

    private Binding Resolve()
    {
        var wanted = path();
        var normalized = string.IsNullOrWhiteSpace(wanted) ? null : wanted.Trim();
        var binding = Volatile.Read(ref current);
        if (binding is not null && string.Equals(binding.Path, normalized, StringComparison.Ordinal))
        {
            return binding;
        }

        var cli = new GhCli(normalized, git) { NetworkTimeout = networkTimeout };
        binding = new Binding(normalized, cli, new GitHubPlatform(cli));
        Volatile.Write(ref current, binding);
        return binding;
    }

    private sealed record Binding(string? Path, GhCli Cli, GitHubPlatform Platform);
}

/// <summary>
/// <see cref="IPrPlatform"/> that forwards every call to the platform of a <see cref="ConfiguredGh"/>, so a tool or the
/// poller built once keeps working after the gh path setting changes. No behaviour of its own.
/// </summary>
internal sealed class ConfiguredPrPlatform(ConfiguredGh gh) : IPrPlatform
{
    private IPrPlatform Inner => gh.Platform;

    public string Name => Inner.Name;

    public Task<PrPlatformAvailability> GetAvailabilityAsync(string workingDirectory, CancellationToken ct = default) =>
        Inner.GetAvailabilityAsync(workingDirectory, ct);

    public Task<PrResult<PullRequestInfo>> ViewPullRequestAsync(
        string workingDirectory,
        string? selector = null,
        CancellationToken ct = default) =>
        Inner.ViewPullRequestAsync(workingDirectory, selector, ct);

    public Task<PrResult<PrReference>> CreatePullRequestAsync(
        string workingDirectory,
        string title,
        string body,
        string baseBranch,
        string headBranch,
        bool draft = false,
        CancellationToken ct = default) =>
        Inner.CreatePullRequestAsync(workingDirectory, title, body, baseBranch, headBranch, draft, ct);

    public Task<PrResult<PrReference>> EditPullRequestAsync(
        string workingDirectory,
        int number,
        string? title = null,
        string? body = null,
        CancellationToken ct = default) =>
        Inner.EditPullRequestAsync(workingDirectory, number, title, body, ct);

    public Task<PrResult<PrChecksSnapshot>> GetChecksAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        Inner.GetChecksAsync(workingDirectory, number, ct);

    public Task<PrResult<PrReference>> MergePullRequestAsync(
        string workingDirectory,
        int number,
        PrMergeMethod method,
        bool deleteBranch = false,
        CancellationToken ct = default) =>
        Inner.MergePullRequestAsync(workingDirectory, number, method, deleteBranch, ct);

    public Task<PrResult<PrReference>> MarkPullRequestReadyAsync(
        string workingDirectory,
        int number,
        CancellationToken ct = default) =>
        Inner.MarkPullRequestReadyAsync(workingDirectory, number, ct);

    public Task<PrResult<PrMergeState>> GetMergeStateAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        Inner.GetMergeStateAsync(workingDirectory, number, ct);

    public Task<PrResult<PrText>> GetFailedJobLogAsync(
        string workingDirectory,
        long? runId,
        long jobId,
        CancellationToken ct = default) =>
        Inner.GetFailedJobLogAsync(workingDirectory, runId, jobId, ct);

    public Task<PrResult<IReadOnlyList<PrReviewThread>>> GetReviewThreadsAsync(
        string workingDirectory,
        int number,
        CancellationToken ct = default) =>
        Inner.GetReviewThreadsAsync(workingDirectory, number, ct);

    public Task<PrResult<PrText>> GetPullRequestDiffAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        Inner.GetPullRequestDiffAsync(workingDirectory, number, ct);

    public Task<PrResult<PrReviewComment>> ReplyToReviewThreadAsync(
        string workingDirectory,
        string threadId,
        string body,
        CancellationToken ct = default) =>
        Inner.ReplyToReviewThreadAsync(workingDirectory, threadId, body, ct);

    public Task<PrResult<PrThreadResolution>> ResolveReviewThreadAsync(
        string workingDirectory,
        string threadId,
        CancellationToken ct = default) =>
        Inner.ResolveReviewThreadAsync(workingDirectory, threadId, ct);
}
