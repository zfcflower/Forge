using System.Globalization;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Marks one review thread resolved, but only after the agent answered it with apply or push_back.</summary>
internal sealed class PrCommentResolveTool(PrCommentEnvironment environment) : GitToolBase(
    environment.Git,
    PrCommentTools.ResolveName,
    "Mark one review thread of this branch's pull request resolved. Code-enforced: the thread must carry your own reply " +
    "with verdict apply or push_back (post it with pr_comment_reply first); a clarify reply cannot resolve, and neither " +
    "can a thread that got any comment after your verdict reply – even one from the same account, because the reviewer " +
    "and you may share one gh login: read what was written and answer again. The thread is " +
    "re-read right before resolving; one that is already resolved is left alone. Read pr_workflow (phase " +
    "review-comments) first.",
    """
    {"type":"object","properties":{"thread_id":{"type":"string","description":"Thread id from pr_comments."}},"required":["thread_id"],"additionalProperties":false}
    """,
    ToolSideEffect.Write)
{
    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments, "thread_id");
        var threadId = GitToolJson.RequiredString(arguments, "thread_id").Trim();
        var directory = context.WorkingDirectory;

        // Observed, never assumed: the thread and the evidence are read now.
        var lookup = await environment.ReadThreadAsync(directory, threadId, ct).ConfigureAwait(false);
        if (lookup.Error is not null)
        {
            return lookup.Error;
        }

        var pull = lookup.PullRequest!;
        var thread = lookup.Thread!;
        if (thread.IsResolved)
        {
            return new ToolResult(PrCommentReplyTool.AlreadyResolved(thread) + " Nothing to do.");
        }

        var record = await environment.EvaluateAsync(directory, thread, ct).ConfigureAwait(false);
        if (record is null)
        {
            return new ToolResult(
                $"Thread {thread.Id} has no verdict reply from you, so it cannot be resolved. Judge the comment, then answer it " +
                "with pr_comment_reply (verdict apply or push_back); resolve only afterwards.",
                IsError: true);
        }

        if (record.Verdict == PrReplyVerdict.Clarify)
        {
            return new ToolResult(
                $"Your last reply on thread {thread.Id} is a clarify: a question is not an answer, so the thread cannot be " +
                "resolved. Read the reviewer's response with pr_comments, then reply again with apply or push_back.",
                IsError: true);
        }

        var newer = CommentsAfter(thread, record);
        if (newer > 0)
        {
            return new ToolResult(
                $"Thread {thread.Id} has {newer.ToString(CultureInfo.InvariantCulture)} comment(s) after your " +
                $"{PrReplyLedger.VerdictName(record.Verdict)} reply. Read them with pr_comments and answer again with " +
                "pr_comment_reply before resolving.",
                IsError: true);
        }

        var resolved = await environment.Platform.ResolveReviewThreadAsync(directory, thread.Id, ct).ConfigureAwait(false);
        if (!resolved.Success || resolved.Value is null)
        {
            return PrCommentEnvironment.Failure(resolved, "Could not resolve the thread.");
        }

        if (!resolved.Value.IsResolved)
        {
            return new ToolResult(
                $"The platform accepted the request but reports thread {thread.Id} as still unresolved. Read it again with pr_comments.",
                IsError: true);
        }

        return new ToolResult(
            $"Resolved thread {thread.Id} of pull request #{pull.Number.ToString(CultureInfo.InvariantCulture)} " +
            $"(your verdict: {PrCommentText.VerdictText(record)}).");
    }

    /// <summary>
    /// How many comments follow the verdict reply. Every one counts, whatever its author: the reviewer and the agent may
    /// act as the same gh login, so "same author" proves nothing about who wrote it or whether it was answered.
    /// </summary>
    private static int CommentsAfter(PrReviewThread thread, PrReplyRecord record)
    {
        for (var i = 0; i < thread.Comments.Count; i++)
        {
            if (string.Equals(thread.Comments[i].Id, record.CommentId, StringComparison.Ordinal))
            {
                return thread.Comments.Count - i - 1;
            }
        }

        return 0;
    }
}
