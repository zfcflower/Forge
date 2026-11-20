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
    /// on translated copy - and the report keeps its own line breaks. The one label this view does recognise is the pointer to
    /// the stored full report at the very end (<see cref="ReportPointer"/>), and only to turn it into the button that opens it.
    /// </summary>
    private const int ReportLine = 2;

    /// <summary>The line break a receipt uses: <see cref="SessionReplies.Build"/> writes one line per line.</summary>
    private const string Newline = "\n";

    /// <summary>
    /// What <see cref="SessionReplies.Build"/> puts in front of the artifact id of a report that did not fit the caps. It is the
    /// wire text of the receipt (what the model reads next turn), never a translated label: the card only recognises it to offer
    /// the stored copy, it never shows it as text of its own. The words live in <see cref="SessionReplies.FullReportPointer"/>
    /// alone, so the producer and this card cannot drift apart.
    /// </summary>
    private const string ReportPointer = SessionReplies.FullReportPointer;

    public SessionReplyViewModel(string text, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(text);
        CopyText = text;
        At = at;
        var body = text;
        var artifactId = string.Empty;
        var pointer = text.LastIndexOf(ReportPointer, StringComparison.Ordinal);
        if (pointer >= 0)
        {
            // The pointer is the receipt's last line: everything before it is the (capped) report, and the id after it is what
            // the button opens.
            var pointed = text[(pointer + ReportPointer.Length)..].Trim();
            if (pointed.Length > 0)
            {
                artifactId = pointed;
                body = text[..pointer];
            }
        }

        ArtifactId = artifactId;

        var lines = body.ReplaceLineEndings(Newline).Split(Newline[0]);
        Header = lines.Length > 0 ? lines[0] : body;
        Changes = lines.Length > 1 && !string.IsNullOrWhiteSpace(lines[1]) ? lines[1] : string.Empty;
        var report = new StringBuilder(body.Length);
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
        OpenReportCommand = new RelayCommand(OpenReport);
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

    /// <summary>
    /// The artifact the receipt points at for the part of its report the caps cut away; empty when the whole report fitted (the
    /// receipt then carries no pointer at all).
    /// </summary>
    public string ArtifactId { get; }

    /// <summary>Whether the card offers the stored full report: the receipt names an artifact and a shell can open it.</summary>
    public bool HasArtifact => ArtifactId.Length > 0 && ReceiptReportOpener.Handler is not null;

    /// <summary>Opens the artifact the receipt points at in the preview tab.</summary>
    public RelayCommand OpenReportCommand { get; }

    public string OpenReportLabel => AgentSessionStrings.ReplyOpenReport;

    public DateTimeOffset At { get; }

    public string Time => At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string CopyLabel => AgentSessionStrings.ReplyCopy;

    /// <summary>Whether <paramref name="text"/> is a receipt (the fixed marker is the only test).</summary>
    public static bool IsReply(string? text) =>
        text is not null && text.StartsWith(SessionReplies.Marker, StringComparison.Ordinal);

    /// <summary>Hands the artifact id to the shell's opener; a card in a host without one offers no entry at all.</summary>
    private void OpenReport()
    {
        if (ArtifactId.Length > 0)
        {
            ReceiptReportOpener.Handler?.Invoke(ArtifactId);
        }
    }

    public override void RefreshText()
    {
        OnPropertyChanged(nameof(CopyLabel));
        OnPropertyChanged(nameof(OpenReportLabel));
        OnPropertyChanged(nameof(HasArtifact));
    }
}
