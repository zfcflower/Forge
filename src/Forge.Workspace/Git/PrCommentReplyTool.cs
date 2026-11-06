using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Posts the answer to one review thread, tagged with the verdict marker that the resolve gate later relies on.</summary>
internal sealed class PrCommentReplyTool(PrCommentEnvironment environment) : GitToolBase(
    environment.Git,
    PrCommentTools.ReplyName,
    "Reply to one review thread of this branch's pull request. Judge the comment first and say which verdict you reached: " +
    "apply (you changed the code: commit must be the sha of a commit that is already PUSHED to the pull request branch; " +
    "the tool checks the remote with ls-remote and merge-base --is-ancestor and refuses otherwise), push_back (you " +
    "disagree or it is out of scope: give the reasons in body) or clarify (you cannot judge yet: ask in body; a " +
    "clarify never allows resolving). The reply starts with a visible marker line such as '**Verdict: apply** (abc1234)' " +
    "which the tool writes itself; do not add one to body. The thread is re-read right before posting; a thread that " +
    "is already resolved is left alone. Read pr_workflow (phase review-comments) first. This posts a public comment.",
    """
    {"type":"object","properties":{"thread_id":{"type":"string","description":"Thread id from pr_comments."},"verdict":{"type":"string","enum":["apply","push_back","clarify"],"description":"What you concluded about the comment."},"body":{"type":"string","description":"The reply text, without the marker line."},"commit":{"type":"string","description":"Required for apply, forbidden otherwise: sha (7 to 40 hex digits) of the pushed commit that addresses the comment."}},"required":["thread_id","verdict","body"],"additionalProperties":false}
    """,
    ToolSideEffect.Write)
{
    private readonly PrCommentCommitGate gate = new(environment);

    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments, "thread_id", "verdict", "body", "commit");

        var threadId = GitToolJson.RequiredString(arguments, "thread_id").Trim();
        var verdictText = GitToolJson.RequiredString(arguments, "verdict");
        if (!PrReplyLedger.TryParseVerdict(verdictText, out var verdict))
        {
            return new ToolResult(
                $"Unknown verdict '{verdictText}'; use apply, push_back or clarify.",
                IsError: true);
        }

        var body = GitToolJson.RequiredString(arguments, "body");
        if (string.IsNullOrWhiteSpace(body))
        {
            return new ToolResult("'body' must say something: a reply of only whitespace is not an answer.", IsError: true);
        }

        var commit = GitToolJson.String(arguments, "commit")?.Trim();
        if (string.IsNullOrEmpty(commit))
        {
            commit = null;
        }

        if (verdict == PrReplyVerdict.Apply && commit is null)
        {
            return new ToolResult(
                "Verdict apply needs 'commit': the sha of the pushed commit that addresses the comment. Make the change, " +
                "commit it, push it with git_sync push, then reply citing it.",
                IsError: true);
        }

        if (verdict != PrReplyVerdict.Apply && commit is not null)
        {
            return new ToolResult(
                $"'commit' belongs to verdict apply only; a {PrReplyLedger.VerdictName(verdict)} reply cites no commit.",
                IsError: true);
        }

        var directory = context.WorkingDirectory;

        // Observed, never assumed: the thread is read now, not taken from an earlier listing.
        var lookup = await environment.ReadThreadAsync(directory, threadId, ct).ConfigureAwait(false);
        if (lookup.Error is not null)
        {
            return lookup.Error;
        }

        var pull = lookup.PullRequest!;
        var thread = lookup.Thread!;
        if (thread.IsResolved)
        {
            return new ToolResult(AlreadyResolved(thread) + " No reply was posted.");
        }

        string? fullCommit = null;
        string? where = null;
        if (verdict == PrReplyVerdict.Apply)
        {
            var check = await gate.VerifyPushedAsync(directory, commit!, pull.HeadRefName, ct).ConfigureAwait(false);
            if (check.Error is not null)
            {
                return check.Error;
            }

            fullCommit = check.FullSha;
            where = check.Where;
        }

        var shortCommit = fullCommit is null ? null : fullCommit[..Math.Min(7, fullCommit.Length)];
        var reply = PrReplyLedger.FormatReply(verdict, shortCommit, body);
        var posted = await environment.Platform.ReplyToReviewThreadAsync(directory, thread.Id, reply, ct).ConfigureAwait(false);
        if (!posted.Success || posted.Value is null)
        {
            return PrCommentEnvironment.Failure(posted, "Could not post the reply.");
        }

        environment.Ledger.NoteCurrentUser(posted.Value.Author);
        environment.Ledger.Record(thread.Id, verdict, fullCommit, posted.Value.Id);

        var text = new StringBuilder(256);
        text.Append("Posted a reply with verdict ").Append(PrReplyLedger.VerdictName(verdict)).Append(" to thread ").Append(thread.Id)
            .Append(" of pull request #").Append(pull.Number.ToString(CultureInfo.InvariantCulture));
        if (fullCommit is not null)
        {
            text.Append(", citing ").Append(PrCommentText.Short(fullCommit)).Append(" (on ").Append(where).Append(')');
        }

        text.Append('.');
        if (!string.IsNullOrEmpty(posted.Value.Url))
        {
            text.Append(' ').Append(posted.Value.Url);
        }

        text.Append('\n').Append(verdict == PrReplyVerdict.Clarify
            ? "The thread stays open: wait for the reviewer's answer, then reply again with apply or push_back."
            : "The thread is still open. Close it with pr_comment_resolve once every thread you handled is answered.");
        return new ToolResult(text.ToString());
    }

    internal static string AlreadyResolved(PrReviewThread thread) =>
        $"Thread {thread.Id} is already resolved" +
        (string.IsNullOrEmpty(thread.ResolvedBy) ? "." : $" (by {thread.ResolvedBy}).");
}
