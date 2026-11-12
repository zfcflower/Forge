using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>
/// The agent's pull request lifecycle tools. Every call works in <see cref="ToolContext.WorkingDirectory"/> (the
/// session's worktree) and reads the platform fresh. Read-only: <c>pr_view</c> and <c>pr_workflow</c>. Write:
/// <c>pr_create</c>, <c>pr_sync</c> and <c>pr_edit</c>. Split by side effect, one tool per fixed side effect, like
/// <see cref="GitTools"/>. Failures are tool errors that name the <see cref="GitRemoteFailureKind"/> or
/// <see cref="PrFailureReason"/> and the next step. Each tool's description names <c>pr_workflow</c> and the phase to
/// read first.
/// </summary>
public sealed class PrTools
{
    public const string ViewName = "pr_view";
    public const string CreateName = "pr_create";
    public const string SyncName = "pr_sync";
    public const string EditName = "pr_edit";
    public const string WorkflowName = "pr_workflow";

    /// <param name="workflowText">
    /// The user's override of one <c>pr_workflow</c> phase text (phase name, the call's working directory), or null to
    /// serve the compiled-in <see cref="PrWorkflowText"/>. Asked on every call, so an edit applies to the next read.
    /// </param>
    public PrTools(
        GitCli git,
        IPrPlatform platform,
        GitToolSettings settings,
        GitReviewLedger ledger,
        ITodoStore todos,
        Func<string, string, string?>? workflowText = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(todos);

        View = new PrViewTool(git, platform);
        Create = new PrCreateTool(git, platform, settings, ledger, todos);
        Sync = new PrSyncTool(git, platform, settings);
        Edit = new PrEditTool(git, platform);
        Workflow = new PrWorkflowTool(git, workflowText);
        All = [View, Create, Sync, Edit, Workflow];
    }

    /// <summary>
    /// <c>{"pr"?: number|string}</c> (ReadOnly) → number, state, URL, title, head and base branch, draft flag, merge state,
    /// review decision and the description of the given pull request (number, URL or branch), or of the current branch's.
    /// "No pull request" and "gh not ready" each get a clear message.
    /// </summary>
    public ITool View { get; }

    /// <summary>
    /// <c>{"title": string, "body"?: string, "base"?: string, "draft"?: bool}</c> (Write). Four gates in code, in order:
    /// clean tree (untracked included); fresh fetch then <c>origin/&lt;base&gt;</c> is an ancestor of HEAD; the full
    /// <c>git_diff</c> was read at exactly this HEAD (<see cref="GitReviewLedger"/> – code enforces the re-read only,
    /// the cleanup is the agent's judgement); the body's <c>## Testing</c> section is non-empty and not a platitude
    /// (<see cref="PrTestingEvidence"/>). Then HEAD and the tree are checked again, an existing open pull request for the
    /// branch is reported instead of creating another, the branch is pushed with <c>-u</c> and a lease from a fresh
    /// <c>ls-remote</c> – the refspec names the reviewed HEAD, so a commit that lands between the gates and the push is
    /// refused as a gate-to-push race – and the pull request is created (draft only when asked). <c>body</c> defaults to
    /// <see cref="PrBodyRenderer.Render"/> of the session's plan summary and steps.
    /// </summary>
    public ITool Create { get; }

    /// <summary>
    /// <c>{}</c> (Write). Refuses a branch that has no pull request (pr_create owns the first publication); fetches,
    /// merges <c>origin/&lt;base&gt;</c> when <c>origin/&lt;base&gt;..HEAD</c> holds a merge
    /// commit, otherwise rebases onto it; conflicts stop the call with the file list and the advice to read both sides
    /// with <c>git log -p</c> and then <c>git_sync</c> continue; success pushes (<c>--force-with-lease</c> with a fresh
    /// lease after a rebase, a plain push after a merge, the refspec naming the audited HEAD so a commit that lands in
    /// between is refused, and the push refused when the remote branch holds commits this checkout never had).
    /// </summary>
    public ITool Sync { get; }

    /// <summary>
    /// <c>{"title"?: string, "body"?: string, "section"?: {"heading": string, "content": string}}</c> (Write), via
    /// <see cref="PrBodyEditor"/>: read, compute, re-read right before writing, write, read back and verify.
    /// </summary>
    public ITool Edit { get; }

    /// <summary>
    /// <c>{"phase": "creating"|"syncing"|"body"|"checks"|"review-comments"|"screenshots"}</c> (ReadOnly) → the written
    /// guidance from <see cref="PrWorkflowText"/>.
    /// </summary>
    public ITool Workflow { get; }

    /// <summary>All five, for registration.</summary>
    public IReadOnlyList<ITool> All { get; }
}
