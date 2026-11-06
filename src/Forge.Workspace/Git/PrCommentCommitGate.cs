using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Outcome of the pushed-commit gate: the full sha and where it was found, or the tool error to return.</summary>
internal sealed record PrCommentCommitCheck(string? FullSha, string? Where, ToolResult? Error);

/// <summary>
/// The code-enforced rule behind an <c>apply</c> reply: the cited commit must be on the remote branch of the pull request,
/// not merely in the local repository. Observed, never assumed – the remote tip is read from the network right now with
/// <c>ls-remote</c> (never from a remote-tracking ref), and the commit has to be an ancestor of (or equal to) that tip
/// according to <c>git merge-base --is-ancestor</c>. An object that is missing locally – a commit someone else pushed, or
/// a tip that moved – is fetched once before the answer is "not found".
/// </summary>
internal sealed class PrCommentCommitGate(PrCommentEnvironment environment)
{
    private const string ShaHint =
        "Cite the exact commit sha (git_log shows it), not a branch, tag or HEAD~1: those move after the reply is posted.";

    public async Task<PrCommentCommitCheck> VerifyPushedAsync(
        string directory,
        string commit,
        string? pullRequestHead,
        CancellationToken ct)
    {
        var git = environment.Git;
        if (!PrReplyLedger.IsHexSha(commit))
        {
            return Fail($"commit '{commit}' is not a commit sha (7 to 40 hex digits). {ShaHint}");
        }

        var status = await git.GetBranchStatusAsync(directory, ct).ConfigureAwait(false);
        var remoteBranch = await ReadUpstreamBranchAsync(directory, status.Branch, ct).ConfigureAwait(false)
            ?? pullRequestHead
            ?? status.Branch;
        if (string.IsNullOrWhiteSpace(remoteBranch))
        {
            return Fail(
                "Cannot tell which remote branch holds this pull request: HEAD is detached and the branch has no upstream. " +
                "Check out the branch and push it with git_sync push, then reply again.");
        }

        var remote = await git.ResolvePushRemoteAsync(directory, status.Branch ?? remoteBranch, null, ct).ConfigureAwait(false);
        var network = environment.NetworkOptions();

        // Fresh read: the lease logic of git_sync push relies on the same rule, and so does this gate.
        var observed = await git.LsRemoteAsync(directory, remote, remoteBranch, network, ct).ConfigureAwait(false);
        if (!observed.Result.Success)
        {
            return new PrCommentCommitCheck(null, null, GitToolFailure.ToToolResult(observed.Result));
        }

        if (observed.Commit is null)
        {
            return Fail(
                $"{remoteBranch} does not exist on {remote}, so commit {commit} is not pushed. Push the branch with git_sync push first.");
        }

        var fetched = false;
        var full = await ResolveCommitAsync(directory, commit, ct).ConfigureAwait(false);
        if (full is null)
        {
            var fetch = await FetchOnceAsync(directory, remote, network, ct).ConfigureAwait(false);
            fetched = true;
            if (fetch is not null)
            {
                return new PrCommentCommitCheck(null, null, fetch);
            }

            full = await ResolveCommitAsync(directory, commit, ct).ConfigureAwait(false);
            if (full is null)
            {
                return Fail(
                    $"commit {commit} does not exist in this repository, not even after fetching {remote} (or the abbreviation is ambiguous). {ShaHint}");
            }
        }

        var tip = await ResolveCommitAsync(directory, observed.Commit, ct).ConfigureAwait(false);
        if (tip is null)
        {
            if (!fetched)
            {
                var fetch = await FetchOnceAsync(directory, remote, network, ct).ConfigureAwait(false);
                if (fetch is not null)
                {
                    return new PrCommentCommitCheck(null, null, fetch);
                }
            }

            tip = await ResolveCommitAsync(directory, observed.Commit, ct).ConfigureAwait(false);
            if (tip is null)
            {
                return Fail(
                    $"The tip of {remote}/{remoteBranch} ({PrCommentText.Short(observed.Commit)}) is not available locally even after fetching; cannot check that commit {commit} is on it.");
            }
        }

        var ancestor = await git.RunAsync(directory, ["merge-base", "--is-ancestor", full, tip], ct).ConfigureAwait(false);
        var where = $"{remote}/{remoteBranch} at {PrCommentText.Short(tip)}";
        if (ancestor.Success)
        {
            return new PrCommentCommitCheck(full, where, null);
        }

        if (!ancestor.TimedOut && ancestor.ExitCode == 1)
        {
            return Fail(
                $"commit {PrCommentText.Short(full)} is not on {where}: it has not been pushed, or the branch was rewritten after it. " +
                "Push it with git_sync push (force_with_lease only after a rebase), then reply citing a commit that is on the remote.");
        }

        var detail = ancestor.StandardError.Trim();
        return Fail(
            $"Could not check whether commit {PrCommentText.Short(full)} is on {where}: " +
            (detail.Length > 0 ? detail : $"git exited with code {ancestor.ExitCode}."));
    }

    /// <summary>The remote branch name the local branch tracks (<c>branch.&lt;name&gt;.merge</c>), or null when it has no upstream.</summary>
    private async Task<string?> ReadUpstreamBranchAsync(string directory, string? localBranch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(localBranch))
        {
            return null;
        }

        var result = await environment.Git.RunAsync(
            directory,
            ["config", "--get", "branch." + localBranch + ".merge"],
            ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return null;
        }

        const string HeadsPrefix = "refs/heads/";
        var reference = result.StandardOutput.Trim();
        return reference.StartsWith(HeadsPrefix, StringComparison.Ordinal) && reference.Length > HeadsPrefix.Length
            ? reference[HeadsPrefix.Length..]
            : null;
    }

    private async Task<string?> ResolveCommitAsync(string directory, string revision, CancellationToken ct) =>
        await environment.Git.RevParseAsync(directory, revision + "^{commit}", ct).ConfigureAwait(false);

    /// <summary>Fetches the remote; a failure is the tool error to return, success is null.</summary>
    private async Task<ToolResult?> FetchOnceAsync(string directory, string remote, GitNetworkOptions network, CancellationToken ct)
    {
        var result = await environment.Git.FetchAsync(directory, remote, network, ct).ConfigureAwait(false);
        return result.Success ? null : GitToolFailure.ToToolResult(result);
    }

    private static PrCommentCommitCheck Fail(string message) =>
        new(null, null, new ToolResult(message, IsError: true));
}
