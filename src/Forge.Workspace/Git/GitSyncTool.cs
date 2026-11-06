using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Pulls, pushes, rebases and merges, and finishes or abandons the operation a conflict left behind.</summary>
internal sealed class GitSyncTool(GitCli git, GitToolSettings settings) : GitToolBase(
    git,
    GitTools.SyncName,
    "Synchronise this branch with the remote. pull uses the configured strategy (fast-forward only by default) and the " +
    "strategy is owned by the user's settings, not by this call. push always re-reads the remote tip with ls-remote " +
    "immediately before pushing; with force_with_lease that fresh sha is the lease, and there is no plain force. " +
    "set_upstream defaults to true when the branch has no upstream. rebase and merge never fetch: they use onto, which " +
    "defaults to the branch's upstream and otherwise to origin/<default branch> as it was last fetched, so fetch first " +
    "when the remote may have moved. On conflicts, read both sides' history with git log -p before resolving, never " +
    "mechanically take one side; stage the resolutions, then run continue (or abort).",
    """
    {"type":"object","properties":{"operation":{"type":"string","enum":["pull","push","rebase","merge","continue","abort"],"description":"What to do."},"onto":{"type":"string","description":"Commit or ref to rebase or merge onto; default the branch's upstream, else origin/<default branch> as last fetched."},"remote":{"type":"string","description":"Remote to push to; default the branch's push remote, else origin."},"set_upstream":{"type":"boolean","description":"Push with -u; default true when the branch has no upstream."},"force_with_lease":{"type":"boolean","description":"Push with --force-with-lease=<ref>:<sha observed just now>; default false."}},"required":["operation"],"additionalProperties":false}
    """,
    ToolSideEffect.Write)
{
    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments, "operation", "onto", "remote", "set_upstream", "force_with_lease");

        var operation = GitToolJson.RequiredString(arguments, "operation");
        var directory = context.WorkingDirectory;
        switch (operation)
        {
            case "pull":
                return await PullAsync(directory, ct).ConfigureAwait(false);

            case "push":
                return await PushAsync(directory, arguments, ct).ConfigureAwait(false);

            case "rebase":
            case "merge":
                return await IntegrateAsync(directory, operation, arguments, ct).ConfigureAwait(false);

            case "continue":
                return Report(await Git.ContinueOperationAsync(directory, ct).ConfigureAwait(false));

            case "abort":
                return Report(await Git.AbortOperationAsync(directory, ct).ConfigureAwait(false));

            default:
                return new ToolResult(
                    $"Unknown operation '{operation}'; use pull, push, rebase, merge, continue or abort.",
                    IsError: true);
        }
    }

    private async Task<ToolResult> PullAsync(string directory, CancellationToken ct)
    {
        var result = await Git.PullAsync(directory, settings.PullStrategy(), NetworkOptions(settings), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return GitToolFailure.ToToolResult(result);
        }

        var status = await Git.GetBranchStatusAsync(directory, ct).ConfigureAwait(false);
        return new ToolResult(result.Message + "\n" + BranchLine(status));
    }

    private async Task<ToolResult> PushAsync(string directory, JsonElement arguments, CancellationToken ct)
    {
        var status = await Git.GetBranchStatusAsync(directory, ct).ConfigureAwait(false);
        if (status.Branch is null)
        {
            return new ToolResult("HEAD is detached; check out a branch before pushing.", IsError: true);
        }

        var remote = await Git.ResolvePushRemoteAsync(
            directory,
            status.Branch,
            GitToolJson.String(arguments, "remote"),
            ct).ConfigureAwait(false);

        // Observed, never assumed: the lease is the tip read here, not a remote-tracking ref.
        var observed = await Git.LsRemoteAsync(directory, remote, status.Branch, NetworkOptions(settings), ct).ConfigureAwait(false);
        if (!observed.Result.Success)
        {
            return GitToolFailure.ToToolResult(observed.Result);
        }

        var lease = GitToolJson.Boolean(arguments, "force_with_lease", false);
        var result = await Git.PushAsync(
            directory,
            new GitPushOptions
            {
                Remote = remote,
                Branch = status.Branch,
                SetUpstream = GitToolJson.Boolean(arguments, "set_upstream", status.Upstream is null || status.UpstreamGone),
                ForceWithLease = lease,
                ExpectedRemoteCommit = lease ? observed.Commit ?? string.Empty : null,
            },
            NetworkOptions(settings),
            ct).ConfigureAwait(false);

        if (!result.Success)
        {
            return GitToolFailure.ToToolResult(result);
        }

        var after = await Git.GetBranchStatusAsync(directory, ct).ConfigureAwait(false);
        return new ToolResult(result.Message + "\n" + BranchLine(after));
    }

    private async Task<ToolResult> IntegrateAsync(
        string directory,
        string operation,
        JsonElement arguments,
        CancellationToken ct)
    {
        var onto = GitToolJson.String(arguments, "onto");
        if (string.IsNullOrWhiteSpace(onto))
        {
            onto = await ResolveTargetAsync(directory, ct).ConfigureAwait(false);
            if (onto is null)
            {
                return new ToolResult(
                    "This branch has no upstream and no default branch could be resolved (no origin/HEAD, main, master or " +
                    "trunk); fetch first, or pass onto explicitly.",
                    IsError: true);
            }
        }

        var result = operation == "rebase"
            ? await Git.RebaseAsync(directory, onto, ct).ConfigureAwait(false)
            : await Git.MergeAsync(directory, onto, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return GitToolFailure.ToToolResult(result);
        }

        var status = await Git.GetBranchStatusAsync(directory, ct).ConfigureAwait(false);
        return new ToolResult(result.Message + "\n" + BranchLine(status));
    }

    /// <summary>
    /// The target a rebase or merge uses when the caller named none: the branch's upstream, else <c>origin/&lt;default
    /// branch&gt;</c> as it was last fetched. No fetch happens here, so an integration never uses a base the caller did not
    /// just look at.
    /// </summary>
    private async Task<string?> ResolveTargetAsync(string directory, CancellationToken ct)
    {
        var status = await Git.GetBranchStatusAsync(directory, ct).ConfigureAwait(false);
        if (status.Upstream is { Length: > 0 } upstream && !status.UpstreamGone)
        {
            return upstream;
        }

        var defaultBranch = await Git.GetDefaultBranchAsync(directory, ct).ConfigureAwait(false);
        if (defaultBranch is null || defaultBranch.StartsWith("origin/", StringComparison.Ordinal))
        {
            return defaultBranch;
        }

        var prefixed = "origin/" + defaultBranch;
        return await Git.RevParseAsync(directory, prefixed, ct).ConfigureAwait(false) is not null
            ? prefixed
            : defaultBranch;
    }
}
