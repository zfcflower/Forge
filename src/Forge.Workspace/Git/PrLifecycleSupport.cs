using System.Globalization;
using System.Text;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>
/// Shared plumbing for the pull request lifecycle tools (<c>pr_view</c>, <c>pr_create</c>, <c>pr_sync</c>,
/// <c>pr_edit</c>): the git helper and the hosting platform, plus the "is gh usable" check that runs before anything
/// reaches the network.
/// </summary>
internal abstract class PrLifecycleToolBase : GitToolBase
{
    protected PrLifecycleToolBase(
        GitCli git,
        IPrPlatform platform,
        string name,
        string description,
        string schema,
        ToolSideEffect sideEffect)
        : base(git, name, description, schema, sideEffect)
    {
        Platform = platform;
    }

    protected IPrPlatform Platform { get; }

    /// <summary>Null when the platform CLI is installed and logged in; otherwise the tool error to return.</summary>
    protected async Task<ToolResult?> RequireReadyAsync(string workingDirectory, CancellationToken ct)
    {
        var availability = await Platform.GetAvailabilityAsync(workingDirectory, ct).ConfigureAwait(false);
        return availability.IsReady ? null : PrLifecycleFailure.NotReady(Platform.Name, availability);
    }

    /// <summary>
    /// The base branch name without a remote prefix: the caller's choice, else the repository's default branch.
    /// Returns an error result instead of a name when none can be resolved or the value could be read as a git option.
    /// </summary>
    protected async Task<(string? Name, ToolResult? Error)> ResolveBaseAsync(
        string workingDirectory,
        string? requested,
        CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(requested)
            ? await Git.GetDefaultBranchAsync(workingDirectory, ct).ConfigureAwait(false)
            : requested.Trim();
        if (name is null)
        {
            return (null, new ToolResult(
                "No default branch could be resolved (no origin/HEAD, main, master or trunk); pass base explicitly.",
                IsError: true));
        }

        name = PrLifecycleFailure.StripRemotePrefix(name);
        if (name.Length == 0 || name[0] == '-')
        {
            return (null, new ToolResult(
                $"Base '{name}' is not a branch name (empty, or a leading '-' would be read as a git option).",
                IsError: true));
        }

        return (name, null);
    }
}

/// <summary>Formats refusals and platform failures the way the git tools do: the failure name, what happened, the next step.</summary>
internal static class PrLifecycleFailure
{
    private const int ListedPaths = 10;

    public static string StripRemotePrefix(string name)
    {
        const string Remote = "origin/";
        if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            name = name["refs/heads/".Length..];
        }

        return name.StartsWith(Remote, StringComparison.Ordinal) ? name[Remote.Length..] : name;
    }

    public static string Short(string? sha) =>
        string.IsNullOrEmpty(sha) ? "(none)" : sha.Length <= 7 ? sha : sha[..7];

    /// <summary>A gate or precondition that was not met; nothing was changed.</summary>
    public static ToolResult Refuse(string tool, string gate, string reason, string next) =>
        new($"{tool} refused ({gate}): {reason}\nNext: {next}", IsError: true);

    public static ToolResult Error(GitRemoteFailureKind kind, string message, string? next = null)
    {
        var text = new StringBuilder(256);
        text.Append(GitToolFailure.FailureName(kind)).Append(": ").Append(message);
        if (!string.IsNullOrEmpty(next))
        {
            text.Append("\nNext: ").Append(next);
        }

        return new ToolResult(text.ToString(), IsError: true);
    }

    public static ToolResult FromPlatform<T>(PrResult<T> result, string? hint = null)
    {
        var name = result.Reason != PrFailureReason.None
            ? result.Reason.ToString()
            : GitToolFailure.FailureName(result.Status.Failure);
        var text = new StringBuilder(256);
        text.Append(name).Append(": ").Append(result.Status.Message);
        var next = hint ?? HintFor(result.Reason, result.Status.Failure);
        if (next.Length > 0)
        {
            text.Append("\nNext: ").Append(next);
        }

        return new ToolResult(text.ToString(), IsError: true);
    }

    public static ToolResult NotReady(string platformName, PrPlatformAvailability availability)
    {
        var detail = string.IsNullOrWhiteSpace(availability.Detail) ? "no further detail" : availability.Detail.Trim();
        if (availability.State == PrPlatformState.NotInstalled)
        {
            return Error(
                GitRemoteFailureKind.Other,
                $"The {platformName} command line tool is not available: {detail}",
                "Install the GitHub CLI (gh) and have the user run 'gh auth login'. The local git tools still work.");
        }

        return availability.Failure is GitRemoteFailureKind.Network or GitRemoteFailureKind.TimedOut
            ? Error(
                availability.Failure,
                $"The {platformName} login could not be verified: {detail}",
                GitToolFailure.DefaultHint(availability.Failure))
            : Error(
                GitRemoteFailureKind.AuthenticationFailed,
                $"{platformName} is not logged in: {detail}",
                "Ask the user to run 'gh auth login' (gh cannot prompt here); the local git tools still work.");
    }

    public static string HintFor(PrFailureReason reason, GitRemoteFailureKind kind) => reason switch
    {
        PrFailureReason.PullRequestNotFound =>
            "There is no pull request for this branch yet; create one with pr_create (read pr_workflow phase 'creating' first).",
        PrFailureReason.PullRequestAlreadyExists =>
            "Use pr_view to see it, pr_edit to change its title or body and pr_sync to bring its branch up to date.",
        PrFailureReason.PlatformNotInstalled =>
            "Install the GitHub CLI (gh) and have the user run 'gh auth login'. The local git tools still work.",
        _ => kind switch
        {
            GitRemoteFailureKind.AuthenticationFailed =>
                "Ask the user to run 'gh auth login' (gh cannot prompt here), then retry.",
            GitRemoteFailureKind.Network => "gh could not reach the host. Check the connection and any proxy, then retry.",
            GitRemoteFailureKind.TimedOut => "The gh call hit the network timeout. Retry, or raise the timeout in settings.",
            GitRemoteFailureKind.RemoteNotFound =>
                "gh could not find the repository. Check that the origin remote points at a repository you can access.",
            _ => GitToolFailure.DefaultHint(kind),
        },
    };

    /// <summary>Up to ten paths, one per line, with a count of the rest.</summary>
    public static string ListPaths(IReadOnlyList<GitStatusEntry> entries)
    {
        var text = new StringBuilder(256);
        for (var i = 0; i < entries.Count && i < ListedPaths; i++)
        {
            text.Append("\n  ").Append(entries[i].Path);
            if (entries[i].Kind == GitStatusKind.Untracked)
            {
                text.Append(" (untracked)");
            }
        }

        if (entries.Count > ListedPaths)
        {
            text.Append("\n  ... and ").Append((entries.Count - ListedPaths).ToString(CultureInfo.InvariantCulture)).Append(" more");
        }

        return text.ToString();
    }
}

/// <summary>How a branch may be published; see <see cref="PrBranchPublisher"/>.</summary>
internal enum PrPushMode
{
    /// <summary>First publication (or a fast-forward): lease against the observed tip, never rewrites existing remote commits.</summary>
    New,

    /// <summary>After a rebase: lease against the observed tip, and the remote may be rewritten once it holds nothing unseen.</summary>
    Rewrite,

    /// <summary>After a merge: a plain push that git itself refuses unless it is a fast-forward.</summary>
    FastForward,
}

/// <summary>The outcome of publishing a branch: either a tool error or a one-line message for the report.</summary>
internal sealed record PrPublishOutcome(ToolResult? Failure, string Message)
{
    public bool Success => Failure is null;
}

/// <summary>
/// The commit the caller audited and the tool it audited it for. The push publishes exactly <see cref="Head"/>: the local
/// branch is checked against it once more right before the push and, if it moved, nothing is published.
/// </summary>
internal sealed record PrPublishPin(string Tool, string Head);

/// <summary>
/// Pushes the current branch for the pull request tools. The remote tip is read fresh with <c>ls-remote</c> right
/// before the push and used as the lease. A force push (<see cref="PrPushMode.Rewrite"/>) additionally refuses when the
/// remote branch holds commits this checkout never had – neither <c>HEAD</c> nor any earlier tip of the local branch
/// (its reflog) contains them – because overwriting those would delete somebody else's work.
/// <para>
/// With a <see cref="PrPublishPin"/> the push is also pinned to the audited commit: the refspec names that commit instead
/// of the branch ref, so a commit that lands on HEAD between the last check and the push cannot slip out unreviewed.
/// </para>
/// </summary>
internal sealed class PrBranchPublisher(GitCli git, GitToolSettings settings)
{
    private const int SeenTipLimit = 50;
    private const int ListedUnseen = 5;

    public async Task<PrPublishOutcome> PublishAsync(
        string workingDirectory,
        string branch,
        PrPushMode mode,
        PrPublishPin? pin,
        CancellationToken ct)
    {
        var head = await git.RevParseAsync(workingDirectory, "HEAD", ct).ConfigureAwait(false);
        if (head is null)
        {
            return Fail(PrLifecycleFailure.Error(GitRemoteFailureKind.Other, "HEAD does not resolve to a commit; nothing to push."));
        }

        if (pin is not null && !string.Equals(head, pin.Head, StringComparison.Ordinal))
        {
            return Fail(MovedSinceAudit(pin, head));
        }

        var network = NetworkOptions();
        var remote = await git.ResolvePushRemoteAsync(workingDirectory, branch, null, ct).ConfigureAwait(false);
        var observed = await git.LsRemoteAsync(workingDirectory, remote, branch, network, ct).ConfigureAwait(false);
        if (!observed.Result.Success)
        {
            return Fail(GitToolFailure.ToToolResult(observed.Result));
        }

        var status = await git.GetBranchStatusAsync(workingDirectory, ct).ConfigureAwait(false);
        var wantedUpstream = remote + "/" + branch;
        var setUpstream = status.Upstream is null
            || status.UpstreamGone
            || !string.Equals(status.Upstream, wantedUpstream, StringComparison.Ordinal);

        var remoteTip = observed.Commit;
        if (remoteTip is not null && !string.Equals(remoteTip, head, StringComparison.Ordinal))
        {
            var guard = await CheckRemoteTipAsync(workingDirectory, branch, remote, remoteTip, mode, ct).ConfigureAwait(false);
            if (guard is not null)
            {
                return Fail(guard);
            }
        }
        else if (remoteTip is not null && !setUpstream)
        {
            return new PrPublishOutcome(null, $"{branch} is already on {remote} at {PrLifecycleFailure.Short(head)}; nothing to push.");
        }

        // The checks above took time (ls-remote, merge-base, reflog). Read HEAD once more, so a commit that landed in
        // between is refused instead of published, and push the audited commit itself rather than the branch ref.
        var headNow = await git.RevParseAsync(workingDirectory, "HEAD", ct).ConfigureAwait(false);
        if (pin is not null && !string.Equals(headNow, pin.Head, StringComparison.Ordinal))
        {
            return Fail(MovedSinceAudit(pin, headNow));
        }

        var lease = mode != PrPushMode.FastForward;
        var result = await git.PushAsync(
            workingDirectory,
            new GitPushOptions
            {
                Remote = remote,
                Branch = branch,
                SetUpstream = setUpstream,
                ForceWithLease = lease,
                ExpectedRemoteCommit = lease ? remoteTip ?? string.Empty : null,
                SourceCommit = pin?.Head,
            },
            network,
            ct).ConfigureAwait(false);

        return result.Success
            ? new PrPublishOutcome(null, result.Message)
            : Fail(GitToolFailure.ToToolResult(result));
    }

    /// <summary>The gate-to-push race: the audited commit is not HEAD any more, so nothing was pushed.</summary>
    private static ToolResult MovedSinceAudit(PrPublishPin pin, string? headNow) =>
        PrLifecycleFailure.Refuse(
            pin.Tool,
            "gate-to-push race",
            $"HEAD was {PrLifecycleFailure.Short(pin.Head)} when the checks ran, but is {PrLifecycleFailure.Short(headNow)} now; " +
            "the commit that would be published was never reviewed, so nothing was pushed.",
            $"Read git_diff again at {PrLifecycleFailure.Short(headNow)} (no paths, not stat_only), commit or undo what arrived, " +
            $"then call {pin.Tool} again.");

    /// <summary>Null when the push may go ahead; otherwise the refusal. The remote branch exists and differs from HEAD.</summary>
    private async Task<ToolResult?> CheckRemoteTipAsync(
        string workingDirectory,
        string branch,
        string remote,
        string remoteTip,
        PrPushMode mode,
        CancellationToken ct)
    {
        if (!IsCommitHash(remoteTip))
        {
            return PrLifecycleFailure.Error(GitRemoteFailureKind.Other, $"ls-remote returned '{remoteTip}', which is not a commit hash.");
        }

        var ancestry = await git.RunAsync(
            workingDirectory,
            ["merge-base", "--is-ancestor", remoteTip, "HEAD"],
            ct).ConfigureAwait(false);
        if (ancestry.Success)
        {
            return null;
        }

        if (ancestry.ExitCode != 1 || ancestry.TimedOut)
        {
            return PrLifecycleFailure.Error(
                GitRemoteFailureKind.NonFastForward,
                $"{remote}/{branch} is at {PrLifecycleFailure.Short(remoteTip)}, a commit this checkout does not have: the remote " +
                "branch moved after the last fetch.",
                "Run git_fetch, look at what arrived (git_log on the remote branch), integrate it, then retry.");
        }

        if (mode != PrPushMode.Rewrite)
        {
            return PrLifecycleFailure.Error(
                GitRemoteFailureKind.NonFastForward,
                $"{remote}/{branch} already has commits that HEAD does not contain (remote tip {PrLifecycleFailure.Short(remoteTip)}); " +
                "this call never overwrites an existing remote branch.",
                "Fetch and read those commits (git_fetch, git_log), integrate them with git_sync pull or merge, then retry. " +
                "Only if the remote branch is yours alone and you know why it differs, use pr_sync after a rebase.");
        }

        return await RefuseUnseenCommitsAsync(workingDirectory, branch, remote, remoteTip, ct).ConfigureAwait(false);
    }

    private async Task<ToolResult?> RefuseUnseenCommitsAsync(
        string workingDirectory,
        string branch,
        string remote,
        string remoteTip,
        CancellationToken ct)
    {
        var reflog = await git.RunAsync(
            workingDirectory,
            ["reflog", "show", "--format=%H", "-n", SeenTipLimit.ToString(CultureInfo.InvariantCulture), "refs/heads/" + branch],
            ct).ConfigureAwait(false);

        var arguments = new List<string>(SeenTipLimit + 8)
        {
            "log",
            "--format=%h %s",
            "--max-count=" + (ListedUnseen + 1).ToString(CultureInfo.InvariantCulture),
            remoteTip,
            "^HEAD",
        };
        if (reflog.Success)
        {
            // The remote tip itself may be an earlier tip of this branch (the commit pushed before the rebase); excluding
            // it leaves nothing unseen, which is exactly the answer.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in reflog.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IsCommitHash(line) && seen.Add(line))
                {
                    arguments.Add("^" + line);
                }
            }
        }

        arguments.Add("--");
        var unseen = await git.RunAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        if (!unseen.Success)
        {
            return PrLifecycleFailure.Error(
                GitRemoteFailureKind.NonFastForward,
                $"Could not verify what {remote}/{branch} (at {PrLifecycleFailure.Short(remoteTip)}) holds: {unseen.StandardError.Trim()}",
                "Run git_fetch so the remote tip is known locally, then retry.");
        }

        var subjects = unseen.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (subjects.Length == 0)
        {
            return null;
        }

        var text = new StringBuilder(256);
        text.Append(remote).Append('/').Append(branch)
            .Append(" has commits that neither HEAD nor any earlier tip of the local branch contains, so someone else pushed to it; ")
            .Append("a force push would delete them:");
        for (var i = 0; i < subjects.Length && i < ListedUnseen; i++)
        {
            text.Append("\n  ").Append(subjects[i]);
        }

        if (subjects.Length > ListedUnseen)
        {
            text.Append("\n  ...");
        }

        return PrLifecycleFailure.Error(
            GitRemoteFailureKind.NonFastForward,
            text.ToString(),
            "Read them (git_fetch, then git_log on the remote branch). If they belong in the branch, integrate them with " +
            "git_sync merge or rebase onto the remote branch; only if they are to be dropped, push deliberately with " +
            "git_sync push and force_with_lease.");
    }

    private GitNetworkOptions NetworkOptions()
    {
        var timeout = settings.NetworkTimeout();
        return timeout > TimeSpan.Zero ? new GitNetworkOptions { Timeout = timeout } : new GitNetworkOptions();
    }

    private static bool IsCommitHash(string text)
    {
        if (text.Length is not (40 or 64))
        {
            return false;
        }

        foreach (var character in text)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static PrPublishOutcome Fail(ToolResult failure) => new(failure, string.Empty);
}
