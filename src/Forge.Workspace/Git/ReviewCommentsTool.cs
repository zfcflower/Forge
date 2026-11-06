using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>
/// Lists the local review comments of this session's worktree – the ones the user wrote in the review panel – so the agent
/// can work through them. Keyed like the review panel: by workspace path (a worktree belongs to exactly one session).
/// </summary>
internal sealed class ReviewCommentsTool(GitCli git, ReviewStore reviews) : GitToolBase(
    git,
    PrCommentTools.ReviewListName,
    "List the local review comments the user wrote on this session's changes: id, file:lines, whether the user already " +
    "sent it (a draft is not yet addressed to you: do not act on drafts unless asked), the base commit it was written " +
    "against, and the text. Unresolved comments only by default; all=true includes resolved ones. Judge every comment " +
    "first (apply or push_back), then change code, then close each one with review_comment_resolve. Local comments and " +
    "pull request threads share one flow: read pr_workflow (phase review-comments) first. Read-only.",
    """
    {"type":"object","properties":{"all":{"type":"boolean","description":"Also list resolved comments; default false (unresolved only)."}},"additionalProperties":false}
    """,
    ToolSideEffect.ReadOnly)
{
    private const int QueryLimit = 1000;

    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments, "all");
        var all = GitToolJson.Boolean(arguments, "all", false);

        var comments = await reviews.QueryAsync(
            new ReviewQuery { WorkspacePath = context.WorkingDirectory, Limit = QueryLimit },
            ct).ConfigureAwait(false);

        var unresolved = 0;
        foreach (var comment in comments)
        {
            if (!comment.IsResolved)
            {
                unresolved++;
            }
        }

        var shown = comments
            .Where(comment => all || !comment.IsResolved)
            .OrderBy(static comment => comment.FilePath, StringComparer.Ordinal)
            .ThenBy(static comment => comment.NewLineStart)
            .ThenBy(static comment => comment.Id)
            .ToArray();

        var text = new StringBuilder(512);
        text.Append("Local review comments: ")
            .Append(unresolved.ToString(CultureInfo.InvariantCulture)).Append(" unresolved, ")
            .Append((comments.Count - unresolved).ToString(CultureInfo.InvariantCulture)).Append(" resolved. ")
            .Append(all ? "Showing all comments.\n" : "Showing unresolved comments; pass all=true to include resolved ones.\n");
        if (comments.Count >= QueryLimit)
        {
            text.Append("Only the newest ").Append(QueryLimit.ToString(CultureInfo.InvariantCulture))
                .Append(" comments are read; older ones are not listed.\n");
        }

        if (shown.Length == 0)
        {
            text.Append(comments.Count == 0 ? "\nThere are no local review comments.\n" : "\nThere are no unresolved local review comments.\n");
            return new ToolResult(text.ToString());
        }

        foreach (var comment in shown)
        {
            text.Append("\nComment ").Append(comment.Id.ToString(CultureInfo.InvariantCulture)).Append(" [")
                .Append(comment.IsResolved ? "resolved" : "unresolved").Append(", ")
                .Append(comment.Status == ReviewCommentStatus.Sent ? "sent" : "draft").Append("] ")
                .Append(comment.FilePath).Append(':').Append(Lines(comment))
                .Append(" · ").Append(PrCommentText.Timestamp(comment.CreatedAt));
            if (!string.IsNullOrWhiteSpace(comment.BaseCommit))
            {
                text.Append(" · base ").Append(PrCommentText.Short(comment.BaseCommit));
            }

            text.Append('\n');
            foreach (var line in comment.Body.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'))
            {
                text.Append("     ").Append(line).Append('\n');
            }
        }

        text.Append("\nNext: judge each comment first (apply or push_back), then change code, then review_comment_resolve with your verdict. See pr_workflow, phase review-comments.\n");
        return new ToolResult(text.ToString());
    }

    private static string Lines(ReviewComment comment) =>
        comment.NewLineEnd == comment.NewLineStart
            ? comment.NewLineStart.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{comment.NewLineStart}-{comment.NewLineEnd}");
}
