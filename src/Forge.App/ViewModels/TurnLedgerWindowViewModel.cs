using System.Globalization;
using System.Text;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>
/// One page of a session's ledger, as the timeline window reads it: the turns below a cursor (<c>long.MaxValue</c> for the
/// newest page), how many the page may carry, and a token. The window holds only this, so the store stays off the UI thread
/// and an old session opens without reading its whole history.
/// </summary>
internal delegate Task<TurnLedgerPage> TurnLedgerPageReader(long beforeTurnSequence, int maxTurns, CancellationToken ct);

/// <summary>
/// One turn of the ledger timeline: the line at its node (「第 N 轮 — 用户输入」) and the Markdown its card shows — the
/// conclusion under its label, then the harness's evidence as bullets. The same fields the model's own history carries, drawn
/// the way the transcript draws a reply: <see cref="TurnReportTemplate.UnwrapConclusion"/> takes the echoed label off, so the
/// card shows the words once.
/// </summary>
internal sealed class TurnLedgerRowViewModel
{
    private readonly TurnLedgerEntry entry;

    internal TurnLedgerRowViewModel(TurnLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        this.entry = entry;
        var round = Loc.Instance.Format(SessionStrings.UserTurnRoundKey, entry.TurnSequence.ToString(CultureInfo.InvariantCulture));
        TaskText = entry.TaskText;
        var task = FirstLine(entry.TaskText);
        RoundLabel = round;
        Header = task.Length == 0 ? round : string.Concat(round, " — ", task);
        Markdown = Build(entry);
    }

    /// <summary>The turn this row records, in the ledger's own numbering.</summary>
    internal long TurnSequence => entry.TurnSequence;

    /// <summary>「第 N 轮」: that number as the row's header starts with.</summary>
    public string RoundLabel { get; }

    /// <summary>What was asked, bare and whole: the tooltip of the header, which shows only its first line.</summary>
    public string TaskText { get; }

    /// <summary>「第 N 轮 — 用户输入的内容」, the one line at the row's node.</summary>
    public string Header { get; }

    /// <summary>The card's content: 「结论」 then 「证据」, as Markdown.</summary>
    public string Markdown { get; }

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var end = trimmed.IndexOfAny(['\r', '\n']);
        return (end < 0 ? trimmed : trimmed[..end]).TrimEnd();
    }

    /// <summary>
    /// The card's Markdown: the turn's conclusion, then one bullet per evidence column the harness recorded. A column with
    /// nothing in it keeps no bullet — an empty line would read as "nothing was read" rather than "the harness recorded no
    /// file", which is the same information the model's own history carries. The message range is always there: it is the
    /// pointer back to the original exchange.
    /// </summary>
    private static string Build(TurnLedgerEntry entry)
    {
        var text = new StringBuilder(entry.ConclusionText.Length + 128);
        Label(text, SessionStrings.SessionLedgerConclusion);
        var conclusion = TurnReportTemplate.UnwrapConclusion(entry.ConclusionText);
        text.Append(conclusion.Length == 0 ? SessionStrings.SessionLedgerNoConclusion : conclusion).Append("\n\n");
        Label(text, SessionStrings.SessionLedgerEvidence);
        Bullet(text, SessionStrings.SessionLedgerRead, entry.ReadFiles);
        Bullet(text, SessionStrings.SessionLedgerWrote, entry.ChangedFiles);
        Bullet(text, SessionStrings.SessionLedgerSpecs, entry.Specs);
        text.Append("- ")
            .Append(SessionStrings.SessionLedgerMessages)
            .Append(": seq ")
            .Append(entry.FromSeq.ToString(CultureInfo.InvariantCulture))
            .Append('-')
            .Append(entry.ToSeq.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        return text.ToString();
    }

    private static void Label(StringBuilder text, string label) =>
        text.Append("**").Append(label).Append("**：\n\n");

    private static void Bullet(StringBuilder text, string label, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        text.Append("- ").Append(label).Append(": ");
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                text.Append('、');
            }

            AppendValue(text, values[index]);
        }

        text.Append('\n');
    }

    /// <summary>A value as inline code, so a path's underscores and stars stay the file's own characters.</summary>
    private static void AppendValue(StringBuilder text, string value) =>
        text.Append(value.Contains('`', StringComparison.Ordinal) ? value : string.Concat("`", value, "`"));
}

/// <summary>
/// The session timeline window's content: the session's turns as one list, oldest first, read a page at a time. The newest
/// page comes first; the reader pulls older ones by scrolling to the top, and each page lands in front of what is already
/// there (the list's own panel keeps the reader's place, which is why prepending is safe). The footer's total comes with
/// every page, since a paged window cannot count the session itself.
/// </summary>
internal sealed class TurnLedgerWindowViewModel : NotifyingObject
{
    /// <summary>How many turns one page carries: enough for a couple of screens, so the prefetch lands before it is needed.</summary>
    internal const int PageTurns = 60;

    private readonly TurnLedgerPageReader read;
    private readonly RangeObservableCollection<TurnLedgerRowViewModel> rows = [];

    /// <summary>The oldest turn on screen: the cursor the next page reads below. <c>long.MaxValue</c> before the first page.</summary>
    private long oldestTurn = long.MaxValue;
    private long total;
    private bool hasOlder = true;
    private bool loading;
    private bool loaded;
    private bool failed;

    internal TurnLedgerWindowViewModel(TurnLedgerPageReader read)
    {
        ArgumentNullException.ThrowIfNull(read);
        this.read = read;
    }

    /// <summary>The timeline, oldest turn first: the top of the list is the oldest turn already read.</summary>
    public RangeObservableCollection<TurnLedgerRowViewModel> Rows => rows;

    public string Title => SessionStrings.SessionLedger.Resolve();

    /// <summary>The footer: how many turns the session holds. Hidden until the first page says so.</summary>
    public string FooterText => SessionStrings.SessionLedgerFooter(total).Resolve();

    public bool ShowsFooter => total > 0;

    /// <summary>The empty state, and only once a read has answered: an empty list while the first page is still coming is
    /// silence, not "nothing was recorded".</summary>
    public bool IsEmpty => loaded && rows.Count == 0;

    /// <summary>The line above the timeline while a page is being read.</summary>
    public bool IsLoading => loading;

    /// <summary>Whether turns older than the ones on screen exist.</summary>
    public bool HasOlder => hasOlder;

    /// <summary>
    /// The last read failed: the window says so and offers the retry. It matters most on the first page, where nothing has been
    /// read yet and there is no scroll to retry with - a silent blank dialog would be the only outcome otherwise.
    /// </summary>
    public bool LoadFailed => failed;

    /// <summary>
    /// Reads the page below the oldest turn on screen and puts it in front. A read in flight owns the next scroll event, and a
    /// failed read leaves the cursor where it was so the next scroll retries it: the window is an observation, and a page that
    /// cannot be read costs that page, not the dialog.
    /// </summary>
    public async Task LoadOlderAsync(CancellationToken ct)
    {
        if (loading || !hasOlder)
        {
            return;
        }

        loading = true;
        // The loading line and the failure line share one row: a retry hides the old verdict while it runs.
        failed = false;
        Raise();
        try
        {
            var page = await Task.Run(() => read(oldestTurn, PageTurns, ct), ct).ConfigureAwait(true);
            if (page.Rows.Count > 0)
            {
                rows.InsertRange(0, [.. page.Rows.Select(static entry => new TurnLedgerRowViewModel(entry))]);
                oldestTurn = page.Rows[0].TurnSequence;
            }

            hasOlder = page.HasOlder;
            total = page.Total;
            loaded = true;
            failed = false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failed = true;
            AppLogs.Session.ZLogWarning($"turn ledger page read failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            loading = false;
            Raise();
        }
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasOlder));
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(ShowsFooter));
        OnPropertyChanged(nameof(LoadFailed));
    }
}
