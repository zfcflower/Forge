using System.Globalization;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Marks one local review comment resolved once the agent judged it (apply or push_back).</summary>
internal sealed class ReviewCommentResolveTool(GitCli git, ReviewStore reviews) : GitToolBase(
    git,
    PrCommentTools.ReviewResolveName,
    "Mark one local review comment resolved. verdict is apply (you changed the code) or push_back (you disagree: note is " +
    "required and must give the reasons); a comment you cannot judge is not resolved, ask the user in your answer " +
    "instead. Only comments of this session's worktree that were actually sent to the reviewer can be resolved: a draft " +
    "(never sent) has nobody to answer and is refused. The resolved flag is all the review store " +
    "keeps: verdict and note are returned to you and show up in the conversation, they are not stored with the comment. " +
    "Read pr_workflow (phase review-comments) first.",
    """
    {"type":"object","properties":{"id":{"type":"integer","description":"Comment id from review_comments."},"verdict":{"type":"string","enum":["apply","push_back"],"description":"What you concluded about the comment."},"note":{"type":"string","description":"What you changed, or why you disagree; required for push_back."}},"required":["id","verdict"],"additionalProperties":false}
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
        if (!PrReplyLedger.TryParseVerdict(verdictText, out var verdict) || verdict == PrReplyVerdict.Clarify)
        {
            return new ToolResult(
                verdictText == "clarify"
                    ? "A local comment is resolved only after apply or push_back. If you cannot judge it, ask the user in your answer and leave it open."
                    : $"Unknown verdict '{verdictText}'; use apply or push_back.",
                IsError: true);
        }

        var note = GitToolJson.String(arguments, "note")?.Trim();
        if (verdict == PrReplyVerdict.PushBack && string.IsNullOrEmpty(note))
        {
            return new ToolResult(
                "push_back needs 'note' with your reasons: a comment that is resolved without a change must say why.",
                IsError: true);
        }

        // Observed, never assumed: the comment is read now.
        var comment = await reviews.GetAsync(id, ct).ConfigureAwait(false);
        var idText = id.ToString(CultureInfo.InvariantCulture);
        if (comment is null)
        {
            return new ToolResult($"There is no local review comment {idText}. Copy the id from review_comments.", IsError: true);
        }

        if (!WorkspacePaths.Equals(comment.WorkspacePath, context.WorkingDirectory))
        {
            return new ToolResult(
                $"Review comment {idText} belongs to another workspace and cannot be resolved from this session.",
                IsError: true);
        }

        if (comment.IsResolved)
        {
            return new ToolResult($"Review comment {idText} is already resolved. Nothing to do.");
        }

        if (comment.Status != ReviewCommentStatus.Sent)
        {
            return new ToolResult(
                $"Review comment {idText} is still a draft: it was never sent to the reviewer, so there is no answer to give " +
                "and nothing to resolve.",
                IsError: true);
        }

        if (!await reviews.SetResolvedAsync(id, true, ct).ConfigureAwait(false))
        {
            return new ToolResult($"Review comment {idText} disappeared before it could be resolved.", IsError: true);
        }

        var result = $"Resolved review comment {idText} ({comment.FilePath}:{comment.NewLineStart.ToString(CultureInfo.InvariantCulture)}), verdict {PrReplyLedger.VerdictName(verdict)}.";
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
