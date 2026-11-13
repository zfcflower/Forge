using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>
/// The written guidance behind the pull request tools, one text per phase of the work. The gates in the tools are
/// enforced by code; these texts are what the agent reads to know how to do the parts code cannot judge. They are C#
/// raw-string constants (no embedded resources) and are served by the read-only <c>pr_workflow</c> tool.
/// </summary>
public static class PrWorkflowText
{
    public const string CreatingPhase = "creating";
    public const string SyncingPhase = "syncing";
    public const string BodyPhase = "body";
    public const string ChecksPhase = "checks";
    public const string ReviewCommentsPhase = "review-comments";
    public const string ScreenshotsPhase = "screenshots";

    /// <summary>Every phase name, in the order a pull request goes through them.</summary>
    public static IReadOnlyList<string> Phases { get; } =
        [CreatingPhase, SyncingPhase, BodyPhase, ChecksPhase, ReviewCommentsPhase, ScreenshotsPhase];

    public const string Creating = """
        # Creating a pull request (tool: pr_create)

        ## Order of work
        1. Do the work as todo steps; each finished step is committed for you. Nothing may stay uncommitted, and untracked files count.
        2. Run the project's own build and tests on the final code. Note the exact commands and what they showed.
        3. Read the whole branch diff once: git_diff with no paths and no stat_only. Clean up what it shows: debug output, commented-out code, scratch and temporary files, unrelated edits, secrets. Commit the cleanup. A new commit moves HEAD, so read git_diff again afterwards.
        4. Write the plan summary (Summary / Changes / Testing). The Testing section must say what you ran and what it showed (a command and its outcome, or a manual check and its result). Never claim a run you did not do.
        5. Call pr_create with title, and optionally body, base and draft. body defaults to the plan summary plus a generated "## Plan" section (one entry per step with its commit). base defaults to the repository's default branch. Pass draft only when the user asked for a draft.

        ## What pr_create checks, in this order
        1. The working tree is clean, untracked files included.
        2. After a fresh fetch, origin/<base> is an ancestor of HEAD. If not, run pr_sync (phase "syncing"), re-run the tests, and read git_diff again.
        3. The full git_diff was read at this exact HEAD. Code only enforces that the diff was re-read; whether the cleanup is actually good is your judgement, and nothing checks it.
        4. The body has a non-empty "## Testing" section that is not a platitude ("tests pass", "tested", "n/a" and the like are refused). Code cannot tell whether you really ran anything; be honest.

        After the gates it reads the state once more (HEAD unchanged, still clean), refuses if a pull request for the branch already exists (and returns its URL), pushes with -u and a lease taken from a fresh ls-remote, then creates the pull request. The push names the reviewed HEAD itself: a commit that lands after the gates is refused as a gate-to-push race instead of being published, and you read git_diff again. It never merges.

        ## After it is created
        Read phase "checks" to watch CI and understand the merge state. For a change users can see, read phase "screenshots". Use phase "body" to adjust the description.
        """;

    public const string Syncing = """
        # Bringing a pull request branch up to date (tool: pr_sync)

        ## When
        The branch already has a pull request (pr_create made one) and pr_view or pr_checks reports BEHIND or DIRTY and you decided to update the branch, or pr_create refused gate 2.

        ## What pr_sync does
        1. Refuses when the branch has no pull request: creating one is pr_create's job, with its gates. It then fetches origin and resolves the base: the open pull request's base branch.
        2. If origin/<base> is already in HEAD there is nothing to integrate.
        3. Otherwise it follows the branch's own history: any merge commit in origin/<base>..HEAD means merge, no merge commit means rebase.
        4. Pushes: after a rebase with --force-with-lease and a lease read fresh with ls-remote, after a merge with a plain push. The push names the HEAD it audited, so a commit that lands while the checks run is refused as a gate-to-push race; call pr_sync again when that happens. It refuses to overwrite remote commits that this checkout never had (someone else pushed); do not force past that refusal, read those commits first.

        ## Conflicts
        pr_sync stops and lists the conflicted files. Before editing any of them, read both sides:
        - what the base changed: git log -p <branch>..origin/<base> -- <file>
        - what this branch changed: git log -p origin/<base>..<branch> -- <file>
        Work out what each side intended and combine them. Never take one side wholesale just to make the conflict go away. Then stage the files, run git_sync continue (a rebase may stop again on the next commit; repeat), and call pr_sync again to push. git_sync abort backs out entirely.

        ## Afterwards
        HEAD changed, so everything that depended on it is stale: re-run the tests, read git_diff again (pr_create's review gate is bound to HEAD), and check whether the description still matches (phase "body"). Then wait for checks again (phase "checks").
        """;

    public const string Body = """
        # Editing the title and body (tool: pr_edit)

        - Prefer a section edit: {"section": {"heading": "Testing", "content": "..."}} replaces only that "## " section and leaves every other section as it is. Replace the whole body only when you mean to rewrite all of it.
        - pr_edit reads the current title and body, computes your change, reads again right before writing, writes, and reads back to check it took effect. If someone changed the body in between, a section edit is applied onto the newest body; a full replacement is refused and both versions are shown, so decide how to combine them and call again. A read-back that differs from what was written is reported as an error: do not assume it worked.
        - Keep the description true. After re-running tests or changing the approach, update "## Testing" and "## Changes". The "## Plan" section lists the steps with their commits; update it when steps change.
        - Say what changed and why it matters, not a diff of the diff. Do not put secrets, tokens or local paths in a body. GitHub accepts at most 65536 characters.
        - Screenshots live in the "## Screenshots" section; phase "screenshots" explains how they get there.
        """;

    public const string Checks = """
        # Watching a pull request (tools: pr_checks, pr_ci_log, pr_view)

        ## Snapshot or wait
        A snapshot reads the checks and the merge state once and reports what it saw. With wait it polls until a final state, a bounded time at most, and is cancelled with the turn. Use a snapshot to look; use wait right after a push when you have nothing else to do. Report exactly what you observed.

        ## Rules for reading the result
        - An empty list of checks means "not determined yet", not "there is no CI": a fresh push may not have registered its checks. Only repeated empty reads justify "this repository has no CI".
        - Pending, queued and UNKNOWN are not results; keep waiting.
        - The pull request is only good when the merge state is CLEAN or HAS_HOOKS and every check passed.

        ## Merge states and what to do
        - CLEAN: mergeable and nothing blocks it.
        - HAS_HOOKS: mergeable with passing checks, but pre-receive hooks still run at merge time; tell the user.
        - BEHIND: the base moved on. Decide first whether to update: pr_sync (phase "syncing") rebases or merges and pushes. Do not sync by reflex, and do not run git commands to fix it without deciding.
        - DIRTY: the branch conflicts with the base. Same: decide, then resolve with both sides read (phase "syncing").
        - BLOCKED: a required check, review or rule is missing. Read the checks and the review decision; do not try to get around the rule.
        - UNSTABLE: mergeable, but non-required checks are failing. Read their logs; fix what your change caused.
        - DRAFT: the pull request is a draft. Tell the user; do not mark it ready on your own.
        - UNKNOWN: the platform has not computed it yet. Read again shortly; never treat it as good.

        ## A failing check
        Take the job id from the snapshot and read the failed steps with pr_ci_log; the failure is at the end of the log. Reproduce it locally, fix the cause, push, and watch again. A rerun without reading the log is not a diagnosis.

        Never merge a pull request yourself. When everything is green, tell the user and stop.
        """;

    public const string ReviewComments = """
        # Handling review comments (tools: pr_comments for pull request threads, review_comments for local comments)

        ## Judge before changing
        A review comment is a claim, not an order. For each one, read the code it points at and its context (the pull request diff helps), then give it a verdict before touching anything:
        - apply: the comment is right. Change the code, commit, push, then reply with verdict apply and cite the pushed commit sha. The reply is refused unless that commit is the head of the pull request or is contained in it (the platform reports the head with the pull request; a commit that only exists locally is refused).
        - push_back: the comment is wrong, out of scope, or conflicts with a requirement. Reply with verdict push_back and the concrete reason (the code, the requirement, the test that shows it).
        - clarify: you cannot judge without more information. Reply with verdict clarify and a precise question. Change nothing; the thread stays open.

        ## Order
        List all threads first, group them by file, judge all of them, then make the changes in as few sensible commits as possible and push once. After that reply to each thread, then resolve.

        ## Resolving
        Resolve a thread only after you replied to it with apply or push_back. A clarify thread cannot be resolved; the reviewer answers first. Any comment that arrives after your verdict reply blocks resolving, even one written by the same account (the reviewer and you may share one gh login): read it and answer again. Outdated threads still deserve a verdict.

        ## Local comments
        Comments the user left in the local review pane (review_comments) get the same judgement. Read them with list, decide, make the change, then resolve them with resolve. Only comments that were actually sent can be resolved; a draft has nobody to answer and is refused. There is no pull request reply for these.

        After pushing, watch the checks again (phase "checks"); changes made for a review can break CI.
        """;

    public const string Screenshots = """
        # Screenshots for user-visible changes (tool: pr_screenshots)

        1. The project declares how to take them in .forge/screenshots.json on the session branch: {"command": "...", "output": "dir", "timeoutSeconds": 300}. The command writes PNG files into the output directory (a headless or end-to-end test, for example). It runs once in the session worktree for the "after" images and once in a temporary worktree of origin/<base> for the "before" images; images are paired by file name, and an image that exists on one side only is kept and marked as such.
        2. If the project declares nothing, write a one-off capture script and pass its command to pr_screenshots as an argument. The script is temporary: do not commit it unless the user wants it, and it is removed once the description has been updated.
        3. If neither exists, report "cannot screenshot" and say why. Do not guess how to start the application and never fabricate an image.

        pr_screenshots runs commands from the repository, so it needs the user's approval. The images are pushed to an assets branch (they never become part of the pull request's files) and the "## Screenshots" section of the description is updated through the same read, edit, verify steps as pr_edit.
        """;

    /// <summary>The text for <paramref name="phase"/> (case-insensitive), or false for an unknown phase.</summary>
    public static bool TryGet(string? phase, out string text)
    {
        text = (phase?.Trim().ToLowerInvariant()) switch
        {
            CreatingPhase => Creating,
            SyncingPhase => Syncing,
            BodyPhase => PrWorkflowText.Body,
            ChecksPhase => Checks,
            ReviewCommentsPhase => ReviewComments,
            ScreenshotsPhase => Screenshots,
            _ => string.Empty,
        };

        return text.Length > 0;
    }
}

/// <summary>Returns the written guidance for one phase of the pull request workflow.</summary>
internal sealed class PrWorkflowTool(GitCli git, Func<string, string, string?>? workflowText = null) : GitToolBase(
    git,
    PrTools.WorkflowName,
    "Read the written guidance for one phase of working with pull requests. Read the phase before using the matching " +
    "tool: 'creating' before pr_create, 'syncing' before pr_sync, 'body' before pr_edit, 'checks' before pr_view or " +
    "waiting for CI, 'review-comments' before answering review comments (pull request threads and local comments), " +
    "'screenshots' before capturing screenshots. The tools enforce the gates in code; this text is how to do the parts " +
    "code cannot judge.",
    """
    {"type":"object","properties":{"phase":{"type":"string","enum":["creating","syncing","body","checks","review-comments","screenshots"],"description":"Which phase to read."}},"required":["phase"],"additionalProperties":false}
    """,
    ToolSideEffect.ReadOnly)
{
    protected override ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments, "phase");
        var phase = GitToolJson.RequiredString(arguments, "phase");
        return ValueTask.FromResult(PrWorkflowText.TryGet(phase, out var text)
            ? new ToolResult(Override(phase, context.WorkingDirectory) ?? text)
            : new ToolResult(
                $"Unknown phase '{phase}'; use one of: {string.Join(", ", PrWorkflowText.Phases)}.",
                IsError: true));
    }

    private string? Override(string phase, string workingDirectory)
    {
        if (workflowText is null)
        {
            return null;
        }

        var text = workflowText(phase.Trim().ToLowerInvariant(), workingDirectory);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
