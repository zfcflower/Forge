using System.Globalization;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Marks one local review comment of this session resolved once the agent judged it (apply or push_back).</summary>
internal sealed class ReviewCommentResolveTool(GitCli git, ReviewStore reviews) : GitToolBase(
    git,
    PrCommentTools.ReviewResolveName,
    "Answer one local review comment. verdict is apply (you changed the code), push_back (you disagree: note is required " +
    "and must give the reasons) or clarify (you cannot judge it yet: note is required and is your question to the reviewer; " +
    "the comment stays open). apply and push_back also mark the comment resolved. Only comments of this session that " +
    "were actually sent to the reviewer can be answered: a comment written in another session of the same worktree is " +
    "answered like an unknown id, and a draft (never sent) has nobody to answer and is refused. " +
    "The verdict and note are stored with the comment and shown to the reviewer in the review panel; a later answer " +
    "replaces an earlier one. Read pr_workflow (phase review-comments) first.",
    """
    {"type":"object","properties":{"id":{"type":"integer","description":"Comment id from review_comments."},"verdict":{"type":"string","enum":["apply","push_back","clarify"],"description":"What you concluded about the comment."},"note":{"type":"string","description":"What you changed, why you disagree, or your question; required for push_back and clarify."}},"required":["id","verdict"],"additionalProperties":false}
    """,
    ToolSideEffect.Write)
{
    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments, "id", "verdict", "note");

        var id = ReadId(arguments);
        var verdictText = GitToolJson.RequiredString(arguments, "verdict");
        if (!PrReplyLedger.TryParseVerdict(verdictText, out var verdict))
        {
            return new ToolResult($"Unknown verdict '{verdictText}'; use apply, push_back or clarify.", IsError: true);
        }

        var note = GitToolJson.String(arguments, "note")?.Trim();
        if (verdict == PrReplyVerdict.PushBack && string.IsNullOrEmpty(note))
        {
            return new ToolResult(
                "push_back needs 'note' with your reasons: a comment that is resolved without a change must say why.",
                IsError: true);
        }

        if (verdict == PrReplyVerdict.Clarify && string.IsNullOrEmpty(note))
        {
            return new ToolResult(
                "clarify needs 'note' with your question: the reviewer reads it in the review panel, and without it there is nothing to answer.",
                IsError: true);
        }

        // Observed, never assumed: the comment is read now. One written in another session of the same worktree (the root
        // session and its agent sessions share one) is answered exactly like a missing id, so its existence does not leak.
        var comment = await reviews.GetAsync(id, ct).ConfigureAwait(false);
        var idText = id.ToString(CultureInfo.InvariantCulture);
        if (comment is null || !string.Equals(comment.SessionId, context.SessionId, StringComparison.Ordinal))
        {
            return new ToolResult($"There is no local review comment {idText}. Copy the id from review_comments.", IsError: true);
        }

        if (!WorkspacePaths.Equals(comment.WorkspacePath, context.WorkingDirectory))
        {
            return new ToolResult(
                $"Review comment {idText} belongs to another workspace and cannot be resolved from this session.",
                IsError: true);
        }

        if (comment.IsResolved && verdict != PrReplyVerdict.Clarify)
        {
            return new ToolResult($"Review comment {idText} is already resolved. Nothing to do.");
        }

        if (comment.IsResolved)
        {
            return new ToolResult(
                $"Review comment {idText} is already resolved; a question on a resolved comment is not shown as open. Ask the user in your answer instead.",
                IsError: true);
        }

        if (comment.Status != ReviewCommentStatus.Sent)
        {
            return new ToolResult(
                $"Review comment {idText} is still a draft: it was never sent to the reviewer, so there is no answer to give " +
                "and nothing to resolve.",
                IsError: true);
        }

        var resolve = verdict != PrReplyVerdict.Clarify;
        if (!await reviews.SetReplyAsync(id, PrReplyLedger.VerdictName(verdict), note, resolve, ct).ConfigureAwait(false))
        {
            return new ToolResult($"Review comment {idText} disappeared before it could be answered.", IsError: true);
        }

        var place = $"({comment.FilePath}:{comment.NewLineStart.ToString(CultureInfo.InvariantCulture)})";
        var result = resolve
            ? $"Resolved review comment {idText} {place}, verdict {PrReplyLedger.VerdictName(verdict)}."
            : $"Asked for clarification on review comment {idText} {place}; it stays open until the reviewer answers.";
        return new ToolResult(string.IsNullOrEmpty(note) ? result : result + "\nNote: " + note);
    }

    private static long ReadId(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("id", out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var id)
            || id <= 0)
        {
            throw new ArgumentException("'id' must be a positive integer (the id from review_comments).");
        }

        return id;
    }
}
