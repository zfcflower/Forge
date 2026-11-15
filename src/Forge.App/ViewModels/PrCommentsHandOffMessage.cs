using System.Globalization;
using System.Text;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>
/// The message the review-comment panel hands to the agent when the user asks it to handle one thread: where the thread
/// sits, every comment with its author and time, and one closing instruction naming the tools that answer it. It is model
/// input, not UI text, so it is English and built here rather than in a string table (the same rule as the checks panel's
/// fix message and a Mermaid block's repair message).
/// <para>Bounds: a comment body keeps at most <see cref="MaxBodyCharacters"/> (a reviewer can paste a log), at most
/// <see cref="MaxComments"/> comments are written out (the rest are counted), and the whole message stays under
/// <see cref="MaxMessageCharacters"/>, so handing over a thread cannot blow up the turn's context.</para>
/// </summary>
internal static class PrCommentsHandOffMessage
{
    /// <summary>Characters kept of one comment body.</summary>
    internal const int MaxBodyCharacters = 4000;

    /// <summary>Comments written out in full; the oldest are the ones kept, because a thread reads from its start.</summary>
    internal const int MaxComments = 20;

    /// <summary>Characters of the whole message; the instruction and the heading are trimmed last.</summary>
    internal const int MaxMessageCharacters = 20000;

    /// <summary>Builds the hand-off message for <paramref name="thread"/> of <paramref name="number"/>.</summary>
    public static string Build(int number, string? url, PrReviewThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        var message = new StringBuilder(2048);
        message.Append("A reviewer left a review thread on pull request #")
            .Append(number.ToString(CultureInfo.InvariantCulture));
        if (url is { Length: > 0 } address)
        {
            message.Append(" (").Append(address).Append(')');
        }

        message.Append(" that the user wants you to handle.\n\n");
        message.Append("== Thread ==\n");
        message.Append("thread_id: ").Append(thread.Id).Append('\n');
        message.Append("location: ").Append(Location(thread)).Append('\n');
        message.Append(thread.IsOutdated
            ? "state: outdated (the diff moved past this line; the location above is where it was first placed)\n"
            : thread.IsResolved ? "state: resolved\n" : "state: unresolved\n");

        var written = 0;
        var budget = MaxMessageCharacters - message.Length - 1200;
        foreach (var comment in thread.Comments)
        {
            if (written == MaxComments)
            {
                break;
            }

            var header = "### " + (comment.Author is { Length: > 0 } author ? author : "unknown author")
                + (comment.CreatedAt is { } at
                    ? " (" + at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) + ")"
                    : string.Empty)
                + "\n";
            var body = comment.Body.ReplaceLineEndings("\n");
            if (body.Length > MaxBodyCharacters)
            {
                body = body[..MaxBodyCharacters] + "…(cut)";
            }

            var block = header + (body.Length > 0 ? body : "(empty)") + "\n";
            if (block.Length > budget)
            {
                message.Append("(").Append((thread.Comments.Count - written).ToString(CultureInfo.InvariantCulture))
                    .Append(" more comment(s) were left out: the message is bounded.)\n\n");
                written = MaxComments;
                break;
            }

            budget -= block.Length;
            message.Append(block).Append('\n');
            written++;
        }

        if (thread.Comments.Count > written)
        {
            // Two ways to land here: the comment cap (MaxComments) stopped the loop, or the character budget did not
            // (the budget path already wrote its own line and set written = MaxComments, so the count is equal then).
            message.Append("(").Append((thread.Comments.Count - written).ToString(CultureInfo.InvariantCulture))
                .Append(" more comment(s) were left out: the message is bounded.)\n\n");
        }

        message.Append("== What to do ==\n");
        message.Append(
            "Read the whole pull request diff and the current code first, then judge this thread: apply (you changed the " +
            "code), push_back (you disagree or it is out of scope) or clarify (you cannot judge yet). Answer with the " +
            "pr_comment_reply tool using this thread_id and your verdict, and once the code is pushed and the reply posted, " +
            "close the thread with pr_comment_resolve. Do not post a reply you have not verified against the code, and do " +
            "not resolve a thread you only clarified.\n");
        return message.ToString();
    }

    /// <summary>"path:line" of the thread, or the line it was first placed on when the diff moved past it.</summary>
    private static string Location(PrReviewThread thread)
    {
        if (thread.Path is not { Length: > 0 } path)
        {
            return "unknown (the platform reported no path)";
        }

        var line = thread.IsOutdated ? thread.OriginalLine ?? thread.Line : thread.Line ?? thread.OriginalLine;
        return line is { } value
            ? path + ":" + value.ToString(CultureInfo.InvariantCulture) + (thread.IsOutdated ? " (original line)" : string.Empty)
            : path;
    }
}