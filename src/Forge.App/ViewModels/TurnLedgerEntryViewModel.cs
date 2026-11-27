using System.Globalization;
using Forge.Abstractions;
using Forge.App.Localization;

namespace Forge.App.ViewModels;

/// <summary>
/// The App-side rendering of one ledger row (forge-app "上下文时间线观察窗"): the entry's own fields assembled into the
/// lines the two observation windows show. Core does not render; this is the same field set the model's later context
/// projection reads, so the window shows exactly what was recorded. The section labels are the entry's own
/// (<c>**Conclusion**:</c>, <c>**Evidence**:</c>) - contract text, not localized prose, taken from
/// <see cref="TurnReportTemplate"/> so the window cannot drift from the entry - and the evidence's seq range is plain
/// text: the model's way back to the original is <c>read_history</c>, not a jump. A reply is free-form, so there is no
/// verdict to show: the row says which turn produced it.
/// </summary>
internal sealed class TurnLedgerEntryViewModel(TurnLedgerEntry entry)
{
    /// <summary>轮头: the turn this row records, one line.</summary>
    public string Header => string.Concat("Turn ", entry.TurnSequence.ToString(CultureInfo.InvariantCulture));

    /// <summary>The heading over the task: this window's own label, not a line the model reads - the model's history carries the
    /// user's words alone.</summary>
    public string TaskHeading => "Task";

    /// <summary>What was asked, bare: the heading above it is this view's own label.</summary>
    public string TaskText => entry.TaskText;

    /// <summary>
    /// The turn's answer, with a conclusion label or a heading of its own removed: a model may echo the label it reads in its
    /// history, and the view draws the label itself - leaving both in place is how one turn showed two of them.
    /// </summary>
    public string ConclusionText => TurnReportTemplate.UnwrapConclusion(entry.ConclusionText);

    /// <summary>This window's own labels: plain words, since it draws a header rather than markdown. Derived from the entry's
    /// own labels, so the two can never drift apart.</summary>
    public string ConclusionLabel => TurnReportTemplate.Plain(TurnReportTemplate.ConclusionLabel);

    public string EvidenceLabel => TurnReportTemplate.Plain(TurnReportTemplate.EvidenceLabel);

    /// <summary>The harness-written evidence, in the same order the entry's receipt line uses: the turn, the files it read,
    /// the files it changed, the specs, and the message range.</summary>
    public string EvidenceText
    {
        get
        {
            var builder = new System.Text.StringBuilder(128);
            builder.Append("turn: ")
                .Append(entry.TurnSequence.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
            AppendList(builder, "read", entry.ReadFiles);
            AppendList(builder, "wrote", entry.ChangedFiles);
            AppendList(builder, "spec", entry.Specs);
            builder.Append("seq: ")
                .Append(entry.FromSeq.ToString(CultureInfo.InvariantCulture))
                .Append('-')
                .Append(entry.ToSeq.ToString(CultureInfo.InvariantCulture));
            return builder.ToString();
        }
    }

    /// <summary>True while the turn in flight is the one this row records.</summary>
    public bool IsPending => entry.Status == TurnLedgerStatus.Pending;

    private static void AppendList(System.Text.StringBuilder builder, string label, IReadOnlyList<string> values)
    {
        builder.Append(label).Append(": ");
        if (values.Count == 0)
        {
            builder.Append(TurnReportTemplate.EmptyValue).Append('\n');
            return;
        }

        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(values[index]);
        }

        builder.Append('\n');
    }
}

/// <summary>
/// The session-level timeline dialog's content: every ledger row of one session, oldest turn first, with the per-row
/// badge and the footer's counts. The rows arrive already ordered (the store lists them that way) and the dialog is
/// rebuilt on every open, so there is no live state to keep.
/// </summary>
internal sealed class TurnLedgerWindowViewModel(IReadOnlyList<TurnLedgerEntry> rows)
{
    public IReadOnlyList<TurnLedgerEntryViewModel> Rows { get; } = [.. rows.Select(static row => new TurnLedgerEntryViewModel(row))];

    public string Title => SessionStrings.SessionLedger.Resolve();

    /// <summary>The footer: how many turns this session's ledger holds.</summary>
    public string FooterText => rows.Count == 0
        ? SessionStrings.SessionLedgerEmpty.Resolve()
        : SessionStrings.SessionLedgerFooter(rows.Count).Resolve();
}
