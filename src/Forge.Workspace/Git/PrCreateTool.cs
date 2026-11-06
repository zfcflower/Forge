using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>
/// Opens a pull request for the session branch after four gates that code enforces, then pushes with a lease and creates
/// it. Nothing is changed when a gate refuses; each refusal names the gate and the next step.
/// </summary>
internal sealed class PrCreateTool(
    GitCli git,
    IPrPlatform platform,
    GitToolSettings settings,
    GitReviewLedger ledger,
    ITodoStore todos) : PrLifecycleToolBase(
    git,
    platform,
    PrTools.CreateName,
    "Open a pull request for the current branch. Read pr_workflow with phase 'creating' first. Four gates are checked in " +
    "code, in order, and any failure refuses the call and says what to do: (1) the working tree is clean, untracked " +
    "files included; (2) after a fresh fetch, origin/<base> is an ancestor of HEAD, otherwise bring the branch up to " +
    "date first (pr_sync); (3) the full git_diff (no paths, not stat_only) was read at this exact HEAD - code can " +
    "only enforce that the diff was re-read, whether you actually cleaned up debug output, scratch files and unrelated " +
    "edits is your own judgement and nothing here checks it; (4) the body has a '## Testing' section that is not empty " +
    "and not a platitude such as 'tests pass', so say what you ran and what it showed (code cannot verify that it " +
    "happened - be honest). body defaults to the plan summary plus a generated '## Plan' section; base defaults to the " +
    "repository's default branch; draft only when true. After the gates the state is read once more, an existing pull " +
    "request for the branch is reported instead of creating another, the branch is pushed with a lease read fresh from " +
    "the remote, and the pull request is created. It is never merged.",
    """
    {"type":"object","properties":{"title":{"type":"string","description":"Pull request title (one line)."},"body":{"type":"string","description":"Description in Markdown with a non-trivial '## Testing' section; default the plan summary plus a generated '## Plan' section."},"base":{"type":"string","description":"Branch to merge into; default the repository's default branch."},"draft":{"type":"boolean","description":"Open as a draft; default false."}},"required":["title"],"additionalProperties":false}
    """,
    ToolSideEffect.Write)
{
    private const string Tool = PrTools.CreateName;

    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments, "title", "body", "base", "draft");

        var title = GitToolJson.RequiredString(arguments, "title").Trim();
        if (title.Length == 0 || title.Contains('\n', StringComparison.Ordinal))
        {
            return new ToolResult("title must be a single non-blank line.", IsError: true);
        }

        var suppliedBody = GitToolJson.String(arguments, "body");
        var draft = GitToolJson.Boolean(arguments, "draft", false);
        var directory = context.WorkingDirectory;

        if (await RequireReadyAsync(directory, ct).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        // A rebase or merge stopped on conflicts leaves HEAD detached; say what is really going on first.
        if (await CheckNoOperationAsync(directory, ct).ConfigureAwait(false) is { } inProgress)
        {
            return inProgress;
        }

        var status = await Git.GetBranchStatusAsync(directory, ct).ConfigureAwait(false);
        if (status.Branch is null)
        {
            return new ToolResult("HEAD is detached; check out a branch before opening a pull request.", IsError: true);
        }

        var branch = status.Branch;
        var (baseName, baseError) = await ResolveBaseAsync(directory, GitToolJson.String(arguments, "base"), ct).ConfigureAwait(false);
        if (baseError is not null)
        {
            return baseError;
        }

        if (string.Equals(baseName, branch, StringComparison.Ordinal))
        {
            return new ToolResult(
                $"The branch {branch} is the base branch itself; a pull request needs a work branch (create one with git_branch).",
                IsError: true);
        }

        var baseRef = "origin/" + baseName;

        // Gate 1: nothing uncommitted, untracked files included.
        var gate1 = await CheckCleanTreeAsync(directory, ct).ConfigureAwait(false);
        if (gate1 is not null)
        {
            return gate1;
        }

        // Gate 2: a fresh fetch, then the base must be contained in HEAD.
        var fetched = await Git.FetchAsync(directory, "origin", NetworkOptions(settings), ct).ConfigureAwait(false);
        if (!fetched.Success)
        {
            return GitToolFailure.ToToolResult(fetched);
        }

        var head = await Git.RevParseAsync(directory, "HEAD", ct).ConfigureAwait(false);
        if (head is null)
        {
            return new ToolResult("HEAD does not resolve to a commit; there is nothing to propose.", IsError: true);
        }

        var gate2 = await CheckUpToDateAsync(directory, baseRef, head, ct).ConfigureAwait(false);
        if (gate2 is not null)
        {
            return gate2;
        }

        // Gate 3 (and 5): the diff of exactly this HEAD was read.
        var reviewed = ledger.LastReviewed(context.SessionId, directory);
        if (!string.Equals(reviewed, head, StringComparison.Ordinal))
        {
            return PrLifecycleFailure.Refuse(
                Tool,
                "gate 3: self-review of the full diff",
                (reviewed is null
                    ? "the full diff of this branch has not been read in this session."
                    : $"the full diff was last read at {PrLifecycleFailure.Short(reviewed)}, but HEAD is now {PrLifecycleFailure.Short(head)}.") +
                " Code can only enforce that the diff was re-read at this exact HEAD; whether the cleanup of debug output, " +
                "scratch files and unrelated edits is thorough is your own judgement and is not checked.",
                "Call git_diff with no paths and without stat_only (default base), clean up what it shows and commit that, " +
                "call git_diff again after the last commit, then call pr_create again.");
        }

        // Gate 4: the description must say what was tested.
        var (body, bodySource) = await ResolveBodyAsync(context.SessionId, suppliedBody, ct).ConfigureAwait(false);
        if (PrTestingEvidence.Check(body) is { } testingProblem)
        {
            return PrLifecycleFailure.Refuse(
                Tool,
                "gate 4: testing evidence",
                $"{testingProblem} (body taken from {bodySource}).",
                "Run the project's tests, then write under '## Testing' what you ran and what it showed (the command and " +
                "its outcome, or the manual check and its result) - in the plan summary, or pass body. Do not claim runs " +
                "that did not happen.");
        }

        if (body.Length > PrBodyEditor.MaxBodyLength)
        {
            return new ToolResult(
                $"The body is {body.Length.ToString(CultureInfo.InvariantCulture)} characters; the platform accepts at most " +
                $"{PrBodyEditor.MaxBodyLength.ToString(CultureInfo.InvariantCulture)}. Shorten it.",
                IsError: true);
        }

        // The gates took time; look at the state once more before anything irreversible.
        var headNow = await Git.RevParseAsync(directory, "HEAD", ct).ConfigureAwait(false);
        if (!string.Equals(headNow, head, StringComparison.Ordinal))
        {
            return PrLifecycleFailure.Refuse(
                Tool,
                "state changed while checking",
                $"HEAD moved from {PrLifecycleFailure.Short(head)} to {PrLifecycleFailure.Short(headNow)} during the checks.",
                "Read git_diff again at the new HEAD and call pr_create again.");
        }

        var recheck = await CheckCleanTreeAsync(directory, ct).ConfigureAwait(false);
        if (recheck is not null)
        {
            return recheck;
        }

        // Never create a second pull request for the same branch.
        var existing = await Platform.ViewPullRequestAsync(directory, branch, ct).ConfigureAwait(false);
        if (existing.Success && existing.Value is { State: PrState.Open } open)
        {
            return PrLifecycleFailure.Refuse(
                Tool,
                "a pull request already exists",
                $"branch {branch} already has the open PR #{open.Number.ToString(CultureInfo.InvariantCulture)}: {open.Url}",
                "Use pr_view to see it, pr_sync to bring it up to date, or pr_edit to change its title or description.");
        }

        if (!existing.Success && !existing.PullRequestNotFound)
        {
            return PrLifecycleFailure.FromPlatform(existing);
        }

        // Publish: the lease is the remote tip read just now, never a cached remote-tracking ref.
        var publish = await new PrBranchPublisher(Git, settings).PublishAsync(directory, branch, PrPushMode.New, ct).ConfigureAwait(false);
        if (publish.Failure is not null)
        {
            return publish.Failure;
        }

        var created = await Platform.CreatePullRequestAsync(directory, title, body, baseName!, branch, draft, ct).ConfigureAwait(false);
        if (!created.Success || created.Value is null)
        {
            var pushed = $"The branch {branch} is already pushed ({publish.Message}); only the pull request is missing. ";
            return created.Reason == PrFailureReason.PullRequestAlreadyExists
                ? PrLifecycleFailure.FromPlatform(created)
                : PrLifecycleFailure.FromPlatform(
                    created,
                    pushed + PrLifecycleFailure.HintFor(created.Reason, created.Status.Failure) + " Then call pr_create again.");
        }

        var report = new StringBuilder(384);
        report.Append("Created pull request #").Append(created.Value.Number.ToString(CultureInfo.InvariantCulture))
            .Append(": ").Append(created.Value.Url).Append('\n');
        report.Append(branch).Append(" -> ").Append(baseName).Append(draft ? " (draft)" : " (ready for review)").Append('\n');
        report.Append("Push: ").Append(publish.Message).Append('\n');
        report.Append("Body: ").Append(bodySource).Append('\n');
        report.Append("Next: read pr_workflow phase 'checks' to watch CI and the merge state; phase 'screenshots' if the change is visible; ")
            .Append("pr_edit adjusts the description.");
        return new ToolResult(report.ToString());
    }

    private async Task<ToolResult?> CheckNoOperationAsync(string directory, CancellationToken ct)
    {
        var conflict = await Git.GetConflictStateAsync(directory, ct).ConfigureAwait(false);
        return conflict.InProgress
            ? PrLifecycleFailure.Refuse(
                Tool,
                "gate 1: clean working tree",
                $"a {GitCli.OperationName(conflict.Operation)} is in progress.",
                "Finish it with git_sync continue (after resolving and staging) or give it up with git_sync abort.")
            : null;
    }

    private async Task<ToolResult?> CheckCleanTreeAsync(string directory, CancellationToken ct)
    {
        if (await CheckNoOperationAsync(directory, ct).ConfigureAwait(false) is { } inProgress)
        {
            return inProgress;
        }

        var entries = await Git.GetStatusAsync(directory, includeUntracked: true, ct).ConfigureAwait(false);
        return entries.Count == 0
            ? null
            : PrLifecycleFailure.Refuse(
                Tool,
                "gate 1: clean working tree",
                $"{entries.Count.ToString(CultureInfo.InvariantCulture)} path(s) are not committed (untracked files count):" +
                PrLifecycleFailure.ListPaths(entries),
                "Commit what belongs to the change (finishing a todo step commits for you), remove or ignore what does not, " +
                "then call git_diff again if HEAD changed and retry.");
    }

    private async Task<ToolResult?> CheckUpToDateAsync(string directory, string baseRef, string head, CancellationToken ct)
    {
        var baseSha = await Git.RevParseAsync(directory, baseRef, ct).ConfigureAwait(false);
        if (baseSha is null)
        {
            return PrLifecycleFailure.Refuse(
                Tool,
                "gate 2: up to date with the base",
                $"{baseRef} does not exist after the fetch.",
                "Pass base explicitly with the name of an existing remote branch (see git_branch / git_status).");
        }

        var mergeBase = await Git.GetMergeBaseAsync(directory, baseRef, head, ct).ConfigureAwait(false);
        if (!string.Equals(mergeBase, baseSha, StringComparison.Ordinal))
        {
            var missing = await Git.CountCommitsAheadAsync(directory, baseRef, head, ct).ConfigureAwait(false);
            return PrLifecycleFailure.Refuse(
                Tool,
                "gate 2: up to date with the base",
                $"{baseRef} has {missing.ToString(CultureInfo.InvariantCulture)} commit(s) that HEAD does not contain " +
                "(checked right after a fresh fetch).",
                "Bring the branch up to date with pr_sync (or git_sync rebase onto " + baseRef + "), resolve any conflicts " +
                "after reading both sides, re-run the tests, read git_diff again because HEAD changes, then retry.");
        }

        var ahead = await Git.CountCommitsAheadAsync(directory, head, baseRef, ct).ConfigureAwait(false);
        return ahead > 0
            ? null
            : PrLifecycleFailure.Refuse(
                Tool,
                "gate 2: up to date with the base",
                $"the branch has no commits beyond {baseRef}; there is nothing to propose.",
                "Make and commit the change first (finishing a todo step commits for you), then retry.");
    }

    private async Task<(string Body, string Source)> ResolveBodyAsync(string sessionId, string? supplied, CancellationToken ct)
    {
        if (supplied is not null)
        {
            return (supplied, "the body argument");
        }

        var summary = await todos.GetPlanSummaryAsync(sessionId, ct).ConfigureAwait(false);
        var steps = await todos.ListAsync(sessionId, ct).ConfigureAwait(false);
        return (
            PrBodyRenderer.Render(summary, steps),
            $"the plan summary and {steps.Count.ToString(CultureInfo.InvariantCulture)} step(s)");
    }
}
