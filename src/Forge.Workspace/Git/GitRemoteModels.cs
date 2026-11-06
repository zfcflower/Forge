namespace Forge.Workspace;

/// <summary>How <see cref="GitCli.PullAsync"/> integrates the upstream. The default only fast-forwards.</summary>
public enum GitPullStrategy { FastForwardOnly, Rebase, Merge }

/// <summary>
/// Why a branch or remote operation failed, judged from git's exit code and stderr (git runs under <c>LC_ALL=C</c>,
/// so the messages are stable English). <see cref="None"/> means success.
/// </summary>
public enum GitRemoteFailureKind
{
    None,

    /// <summary>Credentials missing or rejected; git could not prompt because prompts are disabled.</summary>
    AuthenticationFailed,

    /// <summary>The remote moved and the update is not a fast-forward, or a lease no longer matches.</summary>
    NonFastForward,

    /// <summary>A rebase or merge stopped on conflicts; the repository is left in that operation.</summary>
    Conflict,

    /// <summary>The remote name or the remote repository does not exist.</summary>
    RemoteNotFound,

    /// <summary>The current branch has no upstream and none was given.</summary>
    NoUpstream,

    /// <summary>The working tree has changes that the operation would overwrite or that it refuses to run with.</summary>
    DirtyWorkTree,

    /// <summary>Host unreachable, DNS, TLS or connection failures.</summary>
    Network,

    TimedOut,

    Other,
}

/// <summary>The in-progress multi-step operation that left the working tree mid-way, if any.</summary>
public enum GitOperationKind { None, Rebase, Merge, CherryPick, Revert }

/// <summary>
/// Branch, upstream and divergence from one <c>git status --porcelain=v2 --branch</c> read. Ahead/behind are relative
/// to the last fetched state of the upstream (they are only as fresh as the last fetch), and null when there is no
/// upstream or it is gone. <see cref="Branch"/> is null when HEAD is detached and null in an unborn repository;
/// <see cref="HeadCommit"/> is null only in an unborn repository, and never the all-zero placeholder oid.
/// </summary>
public sealed record GitBranchStatus(
    string? Branch,
    string? HeadCommit,
    string? Upstream,
    int? Ahead,
    int? Behind,
    bool IsDetached,
    bool UpstreamGone);

/// <summary>A configured remote.</summary>
public sealed record GitRemote(string Name, string FetchUrl, string? PushUrl);

/// <summary>The current conflict situation: which operation is in progress and which paths are unmerged.</summary>
public sealed record GitConflictState(GitOperationKind Operation, IReadOnlyList<string> ConflictedPaths)
{
    public bool InProgress => Operation != GitOperationKind.None;
}

/// <summary>
/// Outcome of a branch or remote operation. Expected failures (auth, non-fast-forward, conflicts, missing remote) are
/// reported here, never thrown; only cancellation throws. <see cref="StandardError"/> is git's raw stderr, kept for
/// display.
/// </summary>
public sealed record GitRemoteResult(GitRemoteFailureKind Failure, string Message, string StandardError)
{
    public bool Success => Failure == GitRemoteFailureKind.None;

    public static GitRemoteResult Ok(string message) => new(GitRemoteFailureKind.None, message, string.Empty);
}

/// <summary>
/// A remote ref read freshly with <c>ls-remote</c>; <see cref="Commit"/> is null when the ref does not exist (that is a
/// successful read, not a failure). A remote that does not exist is a failed read with
/// <see cref="GitRemoteFailureKind.RemoteNotFound"/>.
/// </summary>
public sealed record GitRemoteRef(GitRemoteResult Result, string? Commit);

public sealed record GitPushOptions
{
    /// <summary>Remote to push to; null means the branch's configured push remote, else <c>origin</c>.</summary>
    public string? Remote { get; init; }

    /// <summary>Local branch to push; null means the current branch.</summary>
    public string? Branch { get; init; }

    /// <summary>Pass <c>-u</c> so the pushed branch becomes the upstream (first push).</summary>
    public bool SetUpstream { get; init; }

    /// <summary>
    /// Rewrites the remote branch only if it still points at <see cref="ExpectedRemoteCommit"/>
    /// (<c>--force-with-lease=&lt;ref&gt;:&lt;sha&gt;</c>). Without an expected commit the push is a plain push;
    /// a lease is never taken against a cached remote-tracking ref.
    /// </summary>
    public bool ForceWithLease { get; init; }

    /// <summary>
    /// The remote branch tip the caller observed just now via <see cref="GitCli.LsRemoteAsync"/>; empty string means the
    /// ref must not exist yet. A lease that no longer matches – including this case – is reported as
    /// <see cref="GitRemoteFailureKind.NonFastForward"/>: the remote moved and nothing was pushed.
    /// </summary>
    public string? ExpectedRemoteCommit { get; init; }
}

/// <summary>Per-call knobs shared by the network operations.</summary>
public sealed record GitNetworkOptions
{
    /// <summary>Overrides <see cref="GitCli.NetworkTimeout"/>.</summary>
    public TimeSpan? Timeout { get; init; }
}
