namespace Forge.Abstractions;

/// <summary>How a user-driven child session's turn ended; the reply receipt's status is always one of these, never absent.
/// A cancelled turn maps to <see cref="Stopped"/> at the call site; adding a member here means teaching
/// <see cref="SessionReplies.StatusText"/> about it (the switch throws instead of guessing).</summary>
public enum SessionReplyStatus
{
    Completed,
    Failed,
    Stopped,
}

/// <summary>
/// Session-to-session messaging between a root session and its user-driven child sessions: the one tool name, the reply
/// receipt's fixed three-line shape, and the caps that bound what a receipt costs in context. Kept in one place so the
/// producer (Core) and the transcript card (App) cannot drift; the receipt text is also what the model reads next turn.
/// </summary>
public static class SessionReplies
{
    /// <summary>The delivery tool. Pure async: it returns as soon as the message is queued, never a reply.</summary>
    public const string ToolName = "message_session";

    /// <summary>
    /// The tool's parameter schema, kept here so Core (the tool) and App (the `@` picker and the receipt card) cannot drift:
    /// <c>target</c> is the session's name as the tree and the `@` picker show it — names are unique among one root's
    /// children, so the tool resolves by name within the calling root's children and an ambiguous name is an error, never a
    /// guess. <c>message</c> is what the target reads as its next user message.
    /// </summary>
    public const string InputSchemaJson =
        """{"type":"object","properties":{"target":{"type":"string","description":"Session to deliver to, by the name the tree and the @ picker show (the root's name when a child posts back to its root). Names are unique among one root's children."},"message":{"type":"string","description":"What the target reads as its next user message. Delivery is async: this call returns once the message is queued, and the reply arrives later as a receipt message."}},"required":["target","message"],"additionalProperties":false}""";

    /// <summary>The report is cut at this many characters, or <see cref="ReportLineLimit"/> lines, whichever comes first.</summary>
    public const int ReportCharLimit = 4000;

    public const int ReportLineLimit = 250;

    /// <summary>Every receipt starts with this; the transcript recognises a receipt by it instead of guessing from the text.</summary>
    public const string Marker = "[会话回复";

    /// <summary>
    /// The wire text in front of a stored report's artifact id: a receipt that was cut, or whose report was stored at all, ends
    /// with this line and the id behind it. It is wire text the model reads next turn, never translated copy, and it is defined
    /// only here - the card that turns the line into "open the full report" reads this constant instead of repeating the words.
    /// </summary>
    public const string FullReportPointer = "\n完整报告: ";

    /// <summary>The status word used on the wire; a failure or stop always carries its reason, flattened to one line so the three-line receipt stays three lines. An unmapped status throws rather than reading as success.</summary>
    public static string StatusText(SessionReplyStatus status, string? reason = null) => status switch
    {
        SessionReplyStatus.Completed => "完成",
        SessionReplyStatus.Failed => $"失败({Reason(reason)})",
        SessionReplyStatus.Stopped => $"已停止({Reason(reason)})",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "A reply status added to the enum must be mapped here explicitly."),
    };

    /// <summary>
    /// Builds the receipt the initiator's transcript gets once the target's turn ended — completed, failed or stopped alike.
    /// Header, change line, then the report. Pass the <em>full</em> report: this method applies the caps, and a report that
    /// does not fit <b>requires</b> an <paramref name="artifactId"/> (throwing otherwise) so the part that was cut is never lost
    /// without a way to read it back. With an id the pointer is always appended, whether or not the cut lost anything.
    /// </summary>
    /// <exception cref="ArgumentException">The report exceeds the caps and <paramref name="artifactId"/> is null.</exception>
    public static string Build(
        string dispatchId,
        string fromSession,
        SessionReplyStatus status,
        string? reason,
        string taskLabel,
        int changedFiles,
        long addedLines,
        long removedLines,
        string report,
        string? artifactId = null)
    {
        var text = new System.Text.StringBuilder(128 + Math.Min(report.Length, ReportCharLimit));
        text.Append(Marker).Append(" #").Append(Label(dispatchId, 24))
            .Append(" · 来自 ").Append(Label(fromSession))
            .Append(" · 状态:").Append(StatusText(status, reason))
            .Append(" · 任务:").Append(Label(taskLabel, 40))
            .Append("]\n改动: ").Append(changedFiles).Append(" 个文件 (+").Append(addedLines).Append("/−").Append(removedLines).Append(")\n报告: ");
        var store = string.IsNullOrWhiteSpace(artifactId) ? null : Label(artifactId, 80);
        var fitted = AppendCapped(text, report);
        if (!fitted && store is null)
        {
            throw new ArgumentException("A report past the caps must be stored as an artifact first; pass its id.", nameof(artifactId));
        }

        if (!fitted || store is not null)
        {
            text.Append(FullReportPointer).Append(store);
        }

        return text.ToString();
    }

    /// <summary>
    /// Appends the report up to the caps and says whether <em>all</em> of it fit — the caller hangs the artifact pointer on that
    /// answer instead of comparing lengths, which a multi-line report (CRLF separators) would mis-report as truncated.
    /// </summary>
    private static bool AppendCapped(System.Text.StringBuilder text, string report)
    {
        var used = 0;
        var lines = 0;
        foreach (var line in report.AsSpan().EnumerateLines())
        {
            if (lines == ReportLineLimit)
            {
                return false;
            }

            var separator = lines > 0 ? 1 : 0;
            if (used + separator + line.Length <= ReportCharLimit)
            {
                if (separator == 1)
                {
                    text.Append('\n');
                    used++;
                }

                text.Append(line);
                used += line.Length;
                lines++;
                continue;
            }

            // What still fits is kept (a first line can be longer than the whole cap), the rest goes to the artifact.
            var room = ReportCharLimit - used - separator;
            if (room > 0)
            {
                if (separator == 1)
                {
                    text.Append('\n');
                }

                text.Append(line[..room]);
            }

            return false;
        }

        return true;
    }

    private static string Reason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? "原因未知" : Label(reason.Trim(), 120);

    /// <summary>One line, no runs of whitespace, at most <paramref name="limit"/> characters; longer input ends in “…”.</summary>
    private static string Label(string value, int limit = 60)
    {
        var text = new System.Text.StringBuilder(Math.Min(value.Length + 1, limit + 1));
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = text.Length > 0;
                continue;
            }

            if (text.Length >= limit)
            {
                return text.Append('…').ToString();
            }

            if (pendingSpace)
            {
                text.Append(' ');
                pendingSpace = false;
            }

            text.Append(c);
        }

        return text.ToString();
    }
}
