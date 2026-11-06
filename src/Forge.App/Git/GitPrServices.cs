using Forge.App.Approval;
using Forge.App.Localization;
using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>
/// What the Git panel needs besides local git to show the branch's pull request: the platform to read it from, the poller
/// "Wait for checks" runs, the wait timing, and the end-of-wait notification. Without it (the panel's constructor argument
/// is null) the panel is a purely local git panel and never starts a gh process.
/// </summary>
/// <param name="Platform">Reads the pull request, its availability and its review threads (a <see cref="ConfiguredPrPlatform"/> in the app).</param>
/// <param name="Poller">Shared with the <c>pr_checks</c> tool; its <see cref="TimeProvider"/> is the wait's clock.</param>
/// <param name="Options">The timing of one wait, read when the wait starts (live preferences in the app).</param>
/// <param name="Notifier">Raises the end-of-wait notification; null means none.</param>
/// <param name="Post">Runs an action on the UI thread without waiting; null means the app's UI-thread dispatcher.</param>
internal sealed record GitPrServices(
    IPrPlatform Platform,
    PrChecksPoller Poller,
    Func<PrChecksPollerOptions> Options,
    ISystemNotifier? Notifier = null,
    Action<Action>? Post = null)
{
    /// <summary>The dispatcher seam with its default filled in.</summary>
    public Action<Action> PostToUi => Post ?? UiThreadDispatcher.Instance.Post;
}

/// <summary>
/// One review thread of the pull request, as the Git panel lists it (read-only): where it is anchored, whether it is
/// resolved or outdated, the reviewer's first comment, how many replies followed, and the verdict of the latest reply that
/// carries the agent's marker line (see <see cref="PrReplyLedger.TryParseMarker"/>) with that reply's text. Built once from
/// the platform's thread with the language of that moment; the panel rebuilds the items when the language changes.
/// </summary>
internal sealed class GitReviewThreadItem
{
    /// <summary>Longest comment text shown; a longer one is cut and marked with an ellipsis.</summary>
    internal const int MaxCommentCharacters = 400;

    public GitReviewThreadItem(PrReviewThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        var loc = Loc.Instance;

        Id = thread.Id;
        IsResolved = thread.IsResolved;
        IsOutdated = thread.IsOutdated;
        LocationText = LocationOf(thread);
        StatusText = thread.IsOutdated
            ? loc[thread.IsResolved ? GitStrings.ReviewResolved : GitStrings.ReviewUnresolved] + " · " + loc[GitStrings.ReviewOutdated]
            : loc[thread.IsResolved ? GitStrings.ReviewResolved : GitStrings.ReviewUnresolved];

        if (thread.Comments.Count > 0)
        {
            var first = thread.Comments[0];
            var author = string.IsNullOrWhiteSpace(first.Author) ? loc[GitStrings.ReviewUnknownAuthor] : first.Author.Trim();
            FirstCommentText = author + ": " + Shorten(first.Body);
        }

        var replies = Math.Max(0, thread.Comments.Count - 1);
        RepliesText = replies > 0 ? loc.Format(GitStrings.ReviewReplies, replies) : string.Empty;

        // The latest marker reply wins: a thread can be answered "clarify" first and "apply" later.
        for (var index = thread.Comments.Count - 1; index >= 1; index--)
        {
            if (!PrReplyLedger.TryParseMarker(thread.Comments[index].Body, out var record))
            {
                continue;
            }

            VerdictText = record.Verdict switch
            {
                PrReplyVerdict.Apply => loc.Format(GitStrings.ReviewVerdictApply, record.Commit),
                PrReplyVerdict.PushBack => loc[GitStrings.ReviewVerdictPushBack],
                _ => loc[GitStrings.ReviewVerdictClarify],
            };
            VerdictReplyText = Shorten(WithoutMarkerLine(thread.Comments[index].Body));
            break;
        }
    }

    public string Id { get; }

    public bool IsResolved { get; }

    public bool IsOutdated { get; }

    /// <summary><c>path:line</c> (the original line when the thread is outdated), the bare path, or "(whole pull request)".</summary>
    public string LocationText { get; }

    /// <summary>"Unresolved", "Resolved", with " · Outdated" appended when the diff has moved on.</summary>
    public string StatusText { get; }

    /// <summary>"author: text" of the reviewer's opening comment; empty for a thread without comments.</summary>
    public string FirstCommentText { get; } = string.Empty;

    /// <summary>"Replies 2", or empty when nobody answered.</summary>
    public string RepliesText { get; }

    public bool HasReplies => RepliesText.Length > 0;

    /// <summary>"Verdict: applied (abc1234)" and the like, or empty when no reply carries the marker.</summary>
    public string VerdictText { get; } = string.Empty;

    public bool HasVerdict => VerdictText.Length > 0;

    /// <summary>The text of the marker reply without its marker line.</summary>
    public string VerdictReplyText { get; } = string.Empty;

    public bool HasVerdictReply => VerdictReplyText.Length > 0;

    private static string LocationOf(PrReviewThread thread)
    {
        if (string.IsNullOrWhiteSpace(thread.Path))
        {
            return Loc.Instance[GitStrings.ReviewNoFile];
        }

        return (thread.Line ?? thread.OriginalLine) is { } line ? thread.Path + ":" + line : thread.Path;
    }

    /// <summary>The comment without its first non-blank line (the marker), trimmed.</summary>
    private static string WithoutMarkerLine(string body)
    {
        var text = body.AsSpan().TrimStart();
        var lineEnd = text.IndexOf('\n');
        return lineEnd < 0 ? string.Empty : text[(lineEnd + 1)..].ToString().Trim();
    }

    private static string Shorten(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= MaxCommentCharacters ? trimmed : trimmed[..MaxCommentCharacters].TrimEnd() + "…";
    }
}
