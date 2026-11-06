using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>
/// Brings the session branch up to date with its pull request's base and publishes it. It follows the branch's own
/// history: a merge commit in <c>origin/&lt;base&gt;..HEAD</c> means merge, otherwise rebase. Conflicts stop it and are
/// handed to the agent.
/// </summary>
internal sealed class PrSyncTool(GitCli git, IPrPlatform platform, GitToolSettings settings) : PrLifecycleToolBase(
    git,
    platform,
    PrTools.SyncName,
    "Bring the current branch up to date with the base of its pull request and push it. Read pr_workflow with phase " +
    "'syncing' first. The branch must already have a pull request: this tool never publishes a branch nobody asked to " +
    "review (use pr_create for that). It fetches origin, then merges origin/<base> when the branch already has merge " +
    "commits in origin/<base>..HEAD, otherwise rebases onto it (base: the open pull request's base). A " +
    "rebase is pushed with --force-with-lease using a lease read fresh from the remote, a merge with a plain push; the " +
    "push names the audited HEAD itself, so a commit that lands while the checks run is refused (gate-to-push race) and " +
    "the call has to be repeated; the push is refused if the remote branch holds commits this checkout never had. On " +
    "conflicts it stops and lists the conflicted files: read both sides of each with git log -p before editing (never " +
    "take one side wholesale), stage the resolutions, run git_sync continue, then call pr_sync again to push. HEAD " +
    "changes, so re-run the tests and read git_diff again afterwards.",
    """
    {"type":"object","properties":{},"additionalProperties":false}
    """,
    ToolSideEffect.Write)
{
    private const string Tool = PrTools.SyncName;

    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments);
        var directory = context.WorkingDirectory;

        // A rebase or merge stopped on conflicts leaves HEAD detached; report that before "detached".
        var conflict = await Git.GetConflictStateAsync(directory, ct).ConfigureAwait(false);
        if (conflict.InProgress)
        {
            return PrLifecycleFailure.Refuse(
                Tool,
                "an operation is in progress",
                $"a {GitCli.OperationName(conflict.Operation)} is already in progress" +
                (conflict.ConflictedPaths.Count > 0 ? $" with {conflict.ConflictedPaths.Count.ToString(CultureInfo.InvariantCulture)} unresolved file(s)." : "."),
                "Resolve the conflicts and stage the files, then run git_sync continue (or git_sync abort), and call pr_sync again.");
        }

        var status = await Git.GetBranchStatusAsync(directory, ct).ConfigureAwait(false);
        if (status.Branch is null)
        {
            return new ToolResult("HEAD is detached; check out the pull request's branch before syncing.", IsError: true);
        }

        var branch = status.Branch;

        var tracked = await Git.GetStatusAsync(directory, includeUntracked: false, ct).ConfigureAwait(false);
        if (tracked.Count > 0)
        {
            return PrLifecycleFailure.Refuse(
                Tool,
                "uncommitted changes",
                $"{tracked.Count.ToString(CultureInfo.InvariantCulture)} tracked path(s) have uncommitted changes:" +
                PrLifecycleFailure.ListPaths(tracked),
                "Commit them (finishing a todo step commits for you) or stash them, then call pr_sync again.");
        }

        var (baseName, baseSource, baseError) = await ResolveSyncBaseAsync(directory, branch, ct).ConfigureAwait(false);
        if (baseError is not null)
        {
            return baseError;
        }

        if (string.Equals(baseName, branch, StringComparison.Ordinal))
        {
            return new ToolResult(
                $"The branch {branch} is the base branch itself; pr_sync works on the branch of a pull request.",
                IsError: true);
        }

        var baseRef = "origin/" + baseName;
        var fetched = await Git.FetchAsync(directory, "origin", NetworkOptions(settings), ct).ConfigureAwait(false);
        if (!fetched.Success)
        {
            return GitToolFailure.ToToolResult(fetched);
        }

        var baseSha = await Git.RevParseAsync(directory, baseRef, ct).ConfigureAwait(false);
        var before = await Git.RevParseAsync(directory, "HEAD", ct).ConfigureAwait(false);
        if (baseSha is null || before is null)
        {
            return new ToolResult(
                baseSha is null
                    ? $"{baseRef} does not exist after the fetch; check the base branch name."
                    : "HEAD does not resolve to a commit; there is nothing to sync.",
                IsError: true);
        }

        var report = new StringBuilder(384);
        report.Append("Base: ").Append(baseRef).Append(" (").Append(baseSource).Append(")\n");

        var mode = PrPushMode.Rewrite;
        var mergeBase = await Git.GetMergeBaseAsync(directory, baseRef, before, ct).ConfigureAwait(false);
        if (string.Equals(mergeBase, baseSha, StringComparison.Ordinal))
        {
            report.Append(baseRef).Append(" is already contained in HEAD; nothing to integrate.\n");
        }
        else
        {
            var merges = await Git.RunAsync(directory, ["rev-list", "--merges", "--count", baseRef + "..HEAD"], ct).ConfigureAwait(false);
            if (!merges.Success || !int.TryParse(merges.StandardOutput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mergeCount))
            {
                return PrLifecycleFailure.Error(
                    GitRemoteFailureKind.Other,
                    "Could not tell whether the branch uses merge commits: " + merges.StandardError.Trim());
            }

            var behind = await Git.CountCommitsAheadAsync(directory, baseRef, before, ct).ConfigureAwait(false);
            var useMerge = mergeCount > 0;
            var integration = useMerge
                ? await Git.MergeAsync(directory, baseRef, ct).ConfigureAwait(false)
                : await Git.RebaseAsync(directory, baseRef, ct).ConfigureAwait(false);
            if (!integration.Success)
            {
                return integration.Failure == GitRemoteFailureKind.Conflict
                    ? await ConflictReportAsync(directory, branch, baseRef, useMerge, integration, ct).ConfigureAwait(false)
                    : GitToolFailure.ToToolResult(integration);
            }

            mode = useMerge ? PrPushMode.FastForward : PrPushMode.Rewrite;
            report.Append(integration.Message).Append(" The branch was ")
                .Append(behind.ToString(CultureInfo.InvariantCulture)).Append(" commit(s) behind; ")
                .Append(useMerge ? "it already had merge commits, so the base was merged in." : "it had no merge commits, so it was rebased.")
                .Append('\n');
        }

        var toPublish = await Git.RevParseAsync(directory, "HEAD", ct).ConfigureAwait(false);
        if (toPublish is null)
        {
            return new ToolResult("HEAD does not resolve to a commit; nothing to sync.", IsError: true);
        }

        // The pin publishes exactly this commit: HEAD is read again right before the push and the refspec names it, so a
        // commit that lands in between cannot be pushed unreviewed.
        var published = await new PrBranchPublisher(Git, settings)
            .PublishAsync(directory, branch, mode, new PrPublishPin(Tool, toPublish), ct)
            .ConfigureAwait(false);
        if (published.Failure is not null)
        {
            return new ToolResult(
                published.Failure.Content + "\nThe local branch is already updated as reported below; only the push is missing.\n" +
                report.ToString().TrimEnd(),
                IsError: true);
        }

        var after = await Git.RevParseAsync(directory, "HEAD", ct).ConfigureAwait(false);
        report.Append("Push: ").Append(published.Message).Append('\n');
        report.Append("HEAD: ").Append(PrLifecycleFailure.Short(before));
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            report.Append(" -> ").Append(PrLifecycleFailure.Short(after)).Append(" (changed: re-run the tests and read git_diff again)");
        }

        report.Append('\n').Append(BranchLine(await Git.GetBranchStatusAsync(directory, ct).ConfigureAwait(false)));
        return new ToolResult(report.ToString());
    }

    /// <summary>The base of the open pull request when there is one, otherwise the repository's default branch.</summary>
    private async Task<(string? Name, string Source, ToolResult? Error)> ResolveSyncBaseAsync(
        string directory,
        string branch,
        CancellationToken ct)
    {
        var note = string.Empty;
        var availability = await Platform.GetAvailabilityAsync(directory, ct).ConfigureAwait(false);
        if (availability.IsReady)
        {
            var view = await Platform.ViewPullRequestAsync(directory, branch, ct).ConfigureAwait(false);
            if (view.Success && view.Value is { State: PrState.Open } open)
            {
                var name = PrLifecycleFailure.StripRemotePrefix(open.BaseRefName.Trim());
                if (name.Length > 0 && name[0] != '-')
                {
                    return (name, "base of PR #" + open.Number.ToString(CultureInfo.InvariantCulture), null);
                }
            }
            else if (view.PullRequestNotFound)
            {
                // Syncing a branch that has no pull request would push commits nobody asked to review. pr_create owns
                // that branch's first publication and its gates.
                return (null, string.Empty, PrLifecycleFailure.Refuse(
                    Tool,
                    "no pull request for this branch",
                    $"branch {branch} has no pull request, so there is nothing to sync.",
                    "Open it first with pr_create (read pr_workflow phase 'creating'), or check with git_status that you are " +
                    "on the branch of the pull request. pr_sync only brings an existing pull request's branch up to date."));
            }
            else if (!view.Success)
            {
                note = "; the pull request could not be read: " + view.Status.Message;
            }
        }
        else
        {
            note = "; " + Platform.Name + " is not available: " + availability.Detail.Trim();
        }

        var (resolved, error) = await ResolveBaseAsync(directory, null, ct).ConfigureAwait(false);
        return (resolved, "default branch, no open pull request for " + branch + note, error);
    }

    private async Task<ToolResult> ConflictReportAsync(
        string directory,
        string branch,
        string baseRef,
        bool merge,
        GitRemoteResult failure,
        CancellationToken ct)
    {
        var state = await Git.GetConflictStateAsync(directory, ct).ConfigureAwait(false);
        var text = new StringBuilder(384);
        text.Append(merge ? "Merging " : "Rebasing ").Append(branch).Append(merge ? " with " : " onto ").Append(baseRef)
            .Append(" stopped on conflicts in ").Append(state.ConflictedPaths.Count.ToString(CultureInfo.InvariantCulture)).Append(" file(s):");
        foreach (var path in state.ConflictedPaths)
        {
            text.Append("\n  ").Append(path);
        }

        if (state.ConflictedPaths.Count == 0)
        {
            text.Append("\n  (git did not list the files; run git_status)");
        }

        if (!string.IsNullOrWhiteSpace(failure.Message))
        {
            text.Append("\n").Append(failure.Message.Trim());
        }

        return PrLifecycleFailure.Error(
            GitRemoteFailureKind.Conflict,
            text.ToString(),
            "Read both sides of each file before editing it: what the base changed with `git log -p " + branch + ".." + baseRef +
            " -- <file>`, what this branch changed with `git log -p " + baseRef + ".." + branch + " -- <file>`. Combine what both " +
            "intended; never take one side wholesale. Then stage the files, run git_sync continue (a rebase can stop again at the " +
            "next commit; repeat), and call pr_sync again to push. git_sync abort backs out.");
    }
}
