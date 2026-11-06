using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Outcome of the pushed-commit gate: the full sha and where it was found, or the tool error to return.</summary>
internal sealed record PrCommentCommitCheck(string? FullSha, string? Where, ToolResult? Error);

/// <summary>
/// The code-enforced rule behind an <c>apply</c> reply: the cited commit must be on the pull request's head – the
/// <c>headRefOid</c> the platform reports for that pull request right now – not merely somewhere in the local repository
/// and not merely on a branch that happens to share the name. Observed, never assumed: the head comes from the platform
/// read that precedes the reply, and the cited commit has to equal it or be an ancestor of it according to
/// <c>git merge-base --is-ancestor</c>. An object that is missing locally – a commit someone pushed, or a head that moved –
/// is fetched once before the answer is "not found".
/// </summary>
internal sealed class PrCommentCommitGate(PrCommentEnvironment environment)
{
    private const string ShaHint =
        "Cite the exact commit sha (git_log shows it), not a branch, tag or HEAD~1: those move after the reply is posted.";

    public async Task<PrCommentCommitCheck> VerifyPushedAsync(
        string directory,
        string commit,
        PullRequestInfo pullRequest,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);

        var git = environment.Git;
        if (!PrReplyLedger.IsHexSha(commit))
        {
            return Fail($"commit '{commit}' is not a commit sha (7 to 40 hex digits). {ShaHint}");
        }

        var headText = pullRequest.HeadRefOid?.Trim() ?? string.Empty;
        if (!PrReplyLedger.IsHexSha(headText))
        {
            return Fail(
                $"The platform did not report a head commit for pull request #{pullRequest.Number}, so no commit can be checked " +
                "against it. Read the pull request again with pr_view and retry.");
        }

        var branch = await git.GetBranchStatusAsync(directory, ct).ConfigureAwait(false);
        var remoteBranch = PrLifecycleFailure.StripRemotePrefix(pullRequest.HeadRefName.Trim());
        var remote = await git
            .ResolvePushRemoteAsync(directory, branch.Branch ?? remoteBranch, null, ct)
            .ConfigureAwait(false);
        var network = environment.NetworkOptions();

        var full = await ResolveCommitAsync(directory, commit, ct).ConfigureAwait(false);
        var tip = await ResolveCommitAsync(directory, headText, ct).ConfigureAwait(false);
        if (full is null || tip is null)
        {
            // The cited commit may have been pushed by somebody else, and the head may have moved past this checkout.
            var fetch = await FetchOnceAsync(directory, remote, network, ct).ConfigureAwait(false);
            if (fetch is not null)
            {
                return new PrCommentCommitCheck(null, null, fetch);
            }

            full ??= await ResolveCommitAsync(directory, commit, ct).ConfigureAwait(false);
            tip ??= await ResolveCommitAsync(directory, headText, ct).ConfigureAwait(false);
            if (full is null)
            {
                return Fail(
                    $"commit {commit} does not exist in this repository, not even after fetching {remote} (or the abbreviation is ambiguous). {ShaHint}");
            }

            if (tip is null)
            {
                return Fail(
                    $"The head of pull request #{pullRequest.Number} ({PrCommentText.Short(headText)}) is not available locally even " +
                    $"after fetching {remote}; cannot check that commit {commit} is on it.");
            }
        }

        var ancestor = await git.RunAsync(directory, ["merge-base", "--is-ancestor", full, tip], ct).ConfigureAwait(false);
        var where = $"the head of pull request #{pullRequest.Number} at {PrCommentText.Short(tip)}";
        if (ancestor.Success)
        {
            return new PrCommentCommitCheck(full, where, null);
        }

        if (!ancestor.TimedOut && ancestor.ExitCode == 1)
        {
            return Fail(
                $"commit {PrCommentText.Short(full)} is not on {where}: it has not been pushed, or the pull request branch was " +
                "rewritten after it. Push it with git_sync push (force_with_lease only after a rebase), then reply citing a " +
                "commit that is on the head of the pull request.");
        }

        var detail = ancestor.StandardError.Trim();
        return Fail(
            $"Could not check whether commit {PrCommentText.Short(full)} is on {where}: " +
            (detail.Length > 0 ? detail : $"git exited with code {ancestor.ExitCode}."));
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
