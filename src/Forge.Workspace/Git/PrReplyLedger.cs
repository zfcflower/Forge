using System.Collections.Concurrent;

namespace Forge.Workspace;

/// <summary>What the agent concluded about one review comment. Decided before any code is touched.</summary>
public enum PrReplyVerdict
{
    /// <summary>The comment is right; the change was made and is pushed (a commit is cited).</summary>
    Apply,

    /// <summary>The comment is wrong or out of scope; the reply says why.</summary>
    PushBack,

    /// <summary>The comment cannot be judged yet; the reply asks the reviewer. Never resolves the thread.</summary>
    Clarify,
}

/// <summary>
/// The last verdict the agent replied with on one review thread. <see cref="Commit"/> is the cited commit (full sha when
/// this process verified it, the short sha from the marker when it was recovered from the platform) and is set exactly for
/// <see cref="PrReplyVerdict.Apply"/>. <see cref="CommentId"/> is the platform id of the reply that carries the verdict.
/// </summary>
public sealed record PrReplyRecord(PrReplyVerdict Verdict, string? Commit, string? CommentId);

/// <summary>
/// Remembers, per pull request review thread, the last verdict the agent replied with and the commit it cited. The gate of
/// <c>pr_comment_resolve</c> reads it: only a thread whose last verdict reply is <c>apply</c> or <c>push_back</c> may be
/// resolved, and <c>clarify</c> never resolves.
/// <para>
/// Thread-safe and in memory, so a restart would forget everything. That is why the platform is the evidence and the
/// memory only a helper: every reply the tool posts starts with a visible marker line (see <see cref="FormatReply"/>), and
/// <see cref="Evaluate"/> rebuilds the verdict from the comments of a freshly read thread. A comment counts when it is a
/// reply (never the first comment of a thread), its first line parses as the marker, and it was authored by the current
/// user – or is the very comment this process created. A deleted reply therefore proves nothing, and a marker typed by
/// somebody else does not unlock the thread.
/// </para>
/// </summary>
public sealed class PrReplyLedger
{
    private const string MarkerPrefix = "**Verdict: ";
    private const string MarkerSuffix = "**";
    private const int MinShaLength = 7;
    private const int MaxShaLength = 40;

    private readonly ConcurrentDictionary<string, PrReplyRecord> byThread = new(StringComparer.Ordinal);
    private volatile string? currentUser;

    /// <summary>The platform login learned from the reply this process posted last, or null before the first reply.</summary>
    public string? CurrentUser => currentUser;

    /// <summary>Notes the platform login of the person the tool acts as, as reported by the platform for a created comment.</summary>
    public void NoteCurrentUser(string? login)
    {
        if (!string.IsNullOrWhiteSpace(login))
        {
            currentUser = login;
        }
    }

    /// <summary>Remembers the verdict reply just posted on <paramref name="threadId"/>; a later reply replaces it.</summary>
    public void Record(string threadId, PrReplyVerdict verdict, string? commit, string? commentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        byThread[threadId] = new PrReplyRecord(verdict, commit, commentId);
    }

    /// <summary>What this process remembers for the thread, or null. Not evidence by itself: see <see cref="Evaluate"/>.</summary>
    public PrReplyRecord? Get(string threadId) =>
        !string.IsNullOrEmpty(threadId) && byThread.TryGetValue(threadId, out var record) ? record : null;

    /// <summary>
    /// The agent's last verdict reply on <paramref name="thread"/> as the freshly read thread shows it, or null when the
    /// thread carries none. <paramref name="user"/> is the current platform login (null when unknown); without it only the
    /// comment this process created itself is recognised. The returned record's <see cref="PrReplyRecord.CommentId"/> is
    /// the id of the comment that carries the verdict, so the caller can tell what was written after it.
    /// </summary>
    public PrReplyRecord? Evaluate(PrReviewThread thread, string? user)
    {
        ArgumentNullException.ThrowIfNull(thread);

        var own = Get(thread.Id)?.CommentId;
        PrReplyRecord? result = null;

        // Index 0 is the reviewer's opening comment; the tool only ever writes replies.
        for (var index = 1; index < thread.Comments.Count; index++)
        {
            var comment = thread.Comments[index];
            if (!TryParseMarker(comment.Body, out var parsed))
            {
                continue;
            }

            var ours = (own is not null && string.Equals(comment.Id, own, StringComparison.Ordinal))
                || (user is not null
                    && comment.Author is not null
                    && string.Equals(comment.Author, user, StringComparison.OrdinalIgnoreCase));
            if (ours)
            {
                result = parsed with { CommentId = comment.Id };
            }
        }

        return result;
    }

    /// <summary>True when some reply of the thread starts with the marker, whoever wrote it; decides whether the user must be looked up at all.</summary>
    public static bool HasMarkerReply(PrReviewThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        for (var index = 1; index < thread.Comments.Count; index++)
        {
            if (TryParseMarker(thread.Comments[index].Body, out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The visible marker line, first line of every reply the tool posts: <c>**Verdict: apply** (abc1234)</c>,
    /// <c>**Verdict: push_back**</c> or <c>**Verdict: clarify**</c>. Only <c>apply</c> carries the commit (a short sha of
    /// 7 to 40 hex digits).
    /// </summary>
    public static string FormatMarker(PrReplyVerdict verdict, string? commit)
    {
        var name = VerdictName(verdict);
        if (verdict != PrReplyVerdict.Apply)
        {
            return MarkerPrefix + name + MarkerSuffix;
        }

        if (commit is null || !IsHexSha(commit))
        {
            throw new ArgumentException("An apply marker needs a commit sha of 7 to 40 hex digits.", nameof(commit));
        }

        return MarkerPrefix + name + MarkerSuffix + " (" + commit + ")";
    }

    /// <summary>The marker line, a blank line, then the agent's text.</summary>
    public static string FormatReply(PrReplyVerdict verdict, string? commit, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        return FormatMarker(verdict, commit) + "\n\n" + body.Trim();
    }

    /// <summary>
    /// Reads the marker from the first non-blank line of a comment. Strict: the verdict must be one of the three names, an
    /// apply marker must cite a hex sha and the other two must not.
    /// </summary>
    public static bool TryParseMarker(string? body, out PrReplyRecord record)
    {
        record = default!;
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        var text = body.AsSpan().TrimStart();
        var lineEnd = text.IndexOf('\n');
        var line = (lineEnd < 0 ? text : text[..lineEnd]).Trim();
        if (!line.StartsWith(MarkerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        line = line[MarkerPrefix.Length..];
        var close = line.IndexOf(MarkerSuffix, StringComparison.Ordinal);
        if (close <= 0 || !TryParseVerdict(line[..close], out var verdict))
        {
            return false;
        }

        var rest = line[(close + MarkerSuffix.Length)..].Trim();
        string? commit = null;
        if (rest.Length > 0)
        {
            if (rest.Length < 3 || rest[0] != '(' || rest[^1] != ')' || !IsHexSha(rest[1..^1]))
            {
                return false;
            }

            commit = rest[1..^1].ToString();
        }

        if ((verdict == PrReplyVerdict.Apply) != (commit is not null))
        {
            return false;
        }

        record = new PrReplyRecord(verdict, commit, null);
        return true;
    }

    /// <summary>The wire name of a verdict: <c>apply</c>, <c>push_back</c> or <c>clarify</c>.</summary>
    public static string VerdictName(PrReplyVerdict verdict) => verdict switch
    {
        PrReplyVerdict.Apply => "apply",
        PrReplyVerdict.PushBack => "push_back",
        _ => "clarify",
    };

    /// <summary>Parses a wire name exactly (lower case); anything else is not a verdict.</summary>
    public static bool TryParseVerdict(ReadOnlySpan<char> text, out PrReplyVerdict verdict)
    {
        if (text.SequenceEqual("apply"))
        {
            verdict = PrReplyVerdict.Apply;
            return true;
        }

        if (text.SequenceEqual("push_back"))
        {
            verdict = PrReplyVerdict.PushBack;
            return true;
        }

        if (text.SequenceEqual("clarify"))
        {
            verdict = PrReplyVerdict.Clarify;
            return true;
        }

        verdict = PrReplyVerdict.Clarify;
        return false;
    }

    /// <summary>True for 7 to 40 hexadecimal digits: a sha a person or tool can cite.</summary>
    public static bool IsHexSha(ReadOnlySpan<char> text)
    {
        if (text.Length < MinShaLength || text.Length > MaxShaLength)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
