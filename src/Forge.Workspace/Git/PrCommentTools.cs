using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>
/// The agent's review comment tools: the pull request side and the local side share one flow – judge every comment first
/// (apply, push_back or clarify), then change code, then answer and close – described by <c>pr_workflow</c>, phase
/// <c>review-comments</c>. Split by side effect, so the approval policy sees a fixed side effect per tool name:
/// read-only <c>pr_comments</c> and <c>review_comments</c>; write <c>pr_comment_reply</c>, <c>pr_comment_resolve</c> and
/// <c>review_comment_resolve</c>. Every call works in <see cref="ToolContext.WorkingDirectory"/>, the session's worktree,
/// and on the pull request of its current branch. Results are compact text; failures are tool errors.
/// <para>
/// Code-enforced: an <c>apply</c> reply must cite a commit that is on the head of the pull request (the <c>headRefOid</c>
/// the platform reports, matched with <c>merge-base --is-ancestor</c>, fetching first when an object is missing locally);
/// a reply whose post timed out is looked up on the thread before it is written again; a thread is resolved only when it
/// carries the agent's own reply with verdict apply or push_back and no comment at all follows it; clarify never
/// resolves; the thread is re-read from the platform right before replying and again before resolving, and one that is
/// already resolved is left alone. The reply starts with a visible marker line (<see cref="PrReplyLedger.FormatMarker"/>),
/// so the evidence survives a restart: the ledger is rebuilt from the platform, not trusted from memory.
/// </para>
/// </summary>
public sealed class PrCommentTools
{
    public const string ListName = "pr_comments";
    public const string ReplyName = "pr_comment_reply";
    public const string ResolveName = "pr_comment_resolve";
    public const string ReviewListName = "review_comments";
    public const string ReviewResolveName = "review_comment_resolve";

    /// <param name="git">The git wrapper used for the pushed-commit gate.</param>
    /// <param name="platform">The pull request platform.</param>
    /// <param name="settings">Live settings; only the network timeout is read.</param>
    /// <param name="ledger">Shared verdict memory.</param>
    /// <param name="reviews">The local review store; null leaves the two local tools out of <see cref="All"/>.</param>
    /// <param name="currentUser">
    /// Finds the platform login the tools act as, which is how the markers of earlier runs are recognised after a restart
    /// (see <see cref="PrCommentUser.FromGh"/>). Without it only replies this process posted count as the agent's own.
    /// </param>
    public PrCommentTools(
        GitCli git,
        IPrPlatform platform,
        GitToolSettings settings,
        PrReplyLedger ledger,
        ReviewStore? reviews,
        PrUserResolver? currentUser = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ledger);

        var environment = new PrCommentEnvironment(git, platform, settings, ledger, currentUser);
        Comments = new PrCommentsTool(environment);
        Reply = new PrCommentReplyTool(environment);
        Resolve = new PrCommentResolveTool(environment);

        if (reviews is null)
        {
            All = [Comments, Reply, Resolve];
            return;
        }

        ReviewComments = new ReviewCommentsTool(git, reviews);
        ReviewResolve = new ReviewCommentResolveTool(git, reviews);
        All = [Comments, Reply, Resolve, ReviewComments, ReviewResolve];
    }

    /// <summary>
    /// <c>pr_comments</c> (read-only): <c>{"all"?: bool, "include_diff"?: bool}</c> → every review thread of the branch's
    /// pull request (paginated to the end) with id, resolved/outdated, path:line, all comments oldest first and the verdict
    /// already replied. Unresolved only by default. <c>include_diff</c> appends the full pull request diff.
    /// </summary>
    public ITool Comments { get; }

    /// <summary>
    /// <c>pr_comment_reply</c> (write): <c>{"thread_id", "verdict": "apply"|"push_back"|"clarify", "body", "commit"?}</c>.
    /// Posts <c>**Verdict: …**</c> plus the body as a reply. <c>apply</c> requires <c>commit</c> that is pushed; the other
    /// verdicts refuse a commit. A thread that is already resolved gets no reply.
    /// </summary>
    public ITool Reply { get; }

    /// <summary>
    /// <c>pr_comment_resolve</c> (write): <c>{"thread_id"}</c>. Only for a thread whose latest agent reply is apply or
    /// push_back and has no later comment from others; clarify never resolves.
    /// </summary>
    public ITool Resolve { get; }

    /// <summary>
    /// <c>review_comments</c> (read-only): <c>{"all"?: bool}</c> → local review comments of the session's worktree, keyed by
    /// workspace path like the review panel. Null when no review store was given.
    /// </summary>
    public ITool? ReviewComments { get; }

    /// <summary>
    /// <c>review_comment_resolve</c> (write): <c>{"id", "verdict": "apply"|"push_back", "note"?}</c> → sets the resolved
    /// flag; <c>push_back</c> requires a note. Null when no review store was given.
    /// </summary>
    public ITool? ReviewResolve { get; }

    /// <summary>Every tool that exists, for registration: three PR tools, plus the two local ones when a store was given.</summary>
    public IReadOnlyList<ITool> All { get; }
}
