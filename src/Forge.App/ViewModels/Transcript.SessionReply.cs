using System.Globalization;
using System.Text;
using Forge.Abstractions;
using Forge.App.Localization;

namespace Forge.App.ViewModels;

/// <summary>
/// One session-to-session receipt in the transcript (forge-core "reply receipt"): the target session's turn
/// ended - completed, failed or stopped - and it wrote the three-line message <see cref="SessionReplies.Build"/> produces
/// back to the session that asked. The card shows exactly those three lines, so the shape the model reads and the shape the
/// user sees come from the same text; <see cref="SessionReplies.Marker"/> is what recognises it, never a guess from the text.
/// </summary>
internal sealed class SessionReplyViewModel : TranscriptItemViewModel, ICopyableText
{
    /// <summary>
    /// The receipt's shape is fixed by <see cref="SessionReplies.Build"/>: header, change line, report. The lines are read by
    /// position rather than by their labels - the labels are the contract's fixed wire text, not something this view matches
    /// on translated copy - and the report keeps its own line breaks.
    /// </summary>
    private const int ReportLine = 2;

    /// <summary>The line break a receipt uses: <see cref="SessionReplies.Build"/> writes one line per line.</summary>
    private const string Newline = "\n";

    public SessionReplyViewModel(string text, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(text);
        CopyText = text;
        At = at;
        var lines = text.ReplaceLineEndings(Newline).Split(Newline[0]);
        Header = lines.Length > 0 ? lines[0] : text;
        Changes = lines.Length > 1 && !string.IsNullOrWhiteSpace(lines[1]) ? lines[1] : string.Empty;
        var report = new StringBuilder(text.Length);
        if (lines.Length > ReportLine)
        {
            // The line starts with the contract's report label; the report itself begins after its colon and one space.
            var first = lines[ReportLine];
            var label = first.IndexOf(": ", StringComparison.Ordinal);
            report.Append(label >= 0 ? first[(label + 2)..] : first);
        }

        for (var index = ReportLine + 1; index < lines.Length; index++)
        {
            report.Append(Newline[0]).Append(lines[index]);
        }

        Report = report.ToString().Trim();
    }

    /// <summary>The header line as the receipt wrote it: reply number, sender, status and the short task label.</summary>
    public string Header { get; }

    public bool HasHeader => Header.Length > 0;

    /// <summary>The change line: files touched and added/removed lines, read from that turn's checkpoint.</summary>
    public string Changes { get; }

    public bool HasChanges => Changes.Length > 0;

    /// <summary>The target's final reply, capped by <see cref="SessionReplies"/>; the rest is an artifact behind the receipt's "full report" pointer.</summary>
    public string Report { get; }

    public bool HasReport => Report.Length > 0;

    /// <summary>The whole receipt, for the copy button.</summary>
    public string CopyText { get; }

    public DateTimeOffset At { get; }

    public string Time => At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string CopyLabel => AgentSessionStrings.ReplyCopy;

    /// <summary>Whether <paramref name="text"/> is a receipt (the fixed marker is the only test).</summary>
    public static bool IsReply(string? text) =>
        text is not null && text.StartsWith(SessionReplies.Marker, StringComparison.Ordinal);

    public override void RefreshText() => OnPropertyChanged(nameof(CopyLabel));
}
