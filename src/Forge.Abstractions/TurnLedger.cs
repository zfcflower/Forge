using System.Text;

namespace Forge.Abstractions;

/// <summary>
/// How far a turn's ledger row has got. A turn always has exactly one row: <see cref="Pending"/> is what the loop writes
/// before the turn runs, <see cref="Settled"/> is what it becomes when the turn ends. The row records what a turn came from
/// and what it produced - it makes no claim about the reply's shape, because the harness asks the model for none.
/// </summary>
public enum TurnLedgerStatus
{
    Pending,
    Settled,
}

/// <summary>
/// The evidence pin a ledger row carries, all of it written by the harness rather than the model: the paths the turn read,
/// the paths it changed, the spec ids it read or changed, and the message range the turn covers. The two file lists are kept
/// apart on purpose - "what I looked at" and "what I changed" answer different questions, and only the second one is what a
/// later turn has to re-check. The range is the one immutable pointer: sequence numbers are assigned once and never rewritten,
/// which is why no line numbers appear here.
/// </summary>
public sealed record TurnEvidence(
    IReadOnlyList<string> ReadFiles,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> Specs,
    long FromSeq,
    long ToSeq)
{
    public static TurnEvidence Empty { get; } = new([], [], [], 0, 0);
}

/// <summary>One stored ledger row: the whole of what a single task turn is reduced to.</summary>
/// <param name="TaskText">The turn's opening user message, verbatim.</param>
/// <param name="AnswerText">The model's reply exactly as it was shown.</param>
/// <param name="ConclusionText">That reply as the entry renders it: the leading conclusion label, if the reply echoed one,
/// removed. Empty only when the reply was.</param>
public sealed record TurnLedgerEntry(
    string SessionId,
    long TurnSequence,
    TurnLedgerStatus Status,
    string TaskText,
    string AnswerText,
    string ConclusionText,
    IReadOnlyList<string> ReadFiles,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> Specs,
    long FromSeq,
    long ToSeq,
    long AnswerSeq,
    string? ProviderId,
    string? Model,
    string? AgentKind,
    int AgentDepth,
    bool Folded,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Opens a turn's row: everything the loop knows before the model has said anything.</summary>
/// <param name="TurnSequence">The number the caller already owns, or null to have the store allocate and reserve the
/// session's next one in the same insert.</param>
public sealed record TurnLedgerBegin(
    string SessionId,
    long? TurnSequence,
    string TaskText,
    string? ProviderId,
    string? Model,
    string? AgentKind,
    int AgentDepth,
    long FromSeq,
    DateTimeOffset StartedAt);

/// <summary>
/// Settles a turn's row: the reply, the conclusion the entry will render, the evidence the harness collected, and where the
/// reply landed in the session.
/// <paramref name="TurnSequence"/> is the number the row was opened under, or null when the open never landed and the store
/// should file the settled row under the session's next number itself.
/// </summary>
/// <param name="Opened">
/// What opened the turn, carried through so a settle that has to create the row itself (no begin landed under any number)
/// still files its task text and owner - the metadata only the begin ever knew.
/// </param>
public sealed record TurnLedgerCompletion(
    string SessionId,
    long? TurnSequence,
    TurnLedgerStatus Status,
    string AnswerText,
    string ConclusionText,
    TurnEvidence Evidence,
    long AnswerSeq,
    DateTimeOffset EndedAt,
    TurnLedgerBegin? Opened = null);

/// <summary>One hit of the ledger's full-text search: which turn, how well it matched, and the matching text.</summary>
public sealed record TurnLedgerHit(long TurnSequence, double Score, string Snippet);

/// <summary>
/// One page of one session's rows, read backwards from the newest: the rows themselves oldest first, whether older turns
/// remain above the page, and how many turns the session holds in all (the window's footer, which a paged read cannot
/// otherwise state).
/// </summary>
public sealed record TurnLedgerPage(IReadOnlyList<TurnLedgerEntry> Rows, bool HasOlder, long Total);

/// <summary>
/// The per-turn state facts: one row per task turn, appended at the turn's start and settled at its end. Reads serve the
/// session timeline window and the model's own context timeline (see the managed-context projection).
/// </summary>
public interface ITurnLedgerStore
{
    /// <summary>
    /// Opens the turn's row and answers the number it was filed under: the caller's own, or - when it has none - the
    /// session's next number, allocated and reserved by the same insert, so two writers can never be handed the same one.
    /// A row already present for that turn is kept as it is: one turn keeps one row.
    /// </summary>
    Task<long> BeginAsync(TurnLedgerBegin begin, CancellationToken ct);

    /// <summary>
    /// Settles the row this turn opened. A completion whose <see cref="TurnLedgerCompletion.TurnSequence"/> is null files a
    /// fresh settled row under the session's next number instead - a settle must never fail on a row the begin lost.
    /// </summary>
    Task CompleteAsync(TurnLedgerCompletion completion, CancellationToken ct);

    /// <summary>Every row of one session, oldest turn first.</summary>
    Task<IReadOnlyList<TurnLedgerEntry>> ListAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// One page of a session's rows, for a window that opens on the newest turns and reads older ones as the reader scrolls:
    /// the rows with a turn below <paramref name="beforeTurnSequence"/> (<c>long.MaxValue</c> for the newest page), at most
    /// <paramref name="maxTurns"/> of them, oldest first, with whether older turns remain and the session's total.
    /// </summary>
    Task<TurnLedgerPage> ListPageAsync(string sessionId, long beforeTurnSequence, int maxTurns, CancellationToken ct);

    /// <summary>One turn's row, or null when the session has no such turn.</summary>
    Task<TurnLedgerEntry?> GetAsync(string sessionId, long turnSequence, CancellationToken ct);

    /// <summary>Full-text search over the normalized fields of one session's rows, best match first.</summary>
    Task<IReadOnlyList<TurnLedgerHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct);

    /// <summary>
    /// Absorbs every row up to and including <paramref name="throughTurn"/> into the session's folded block. Monotonic: a
    /// call that would fold less than what is already folded does nothing, so the fold range can only grow and a resumed
    /// session renders the same shape the live one had.
    /// </summary>
    Task MarkFoldedAsync(string sessionId, long throughTurn, CancellationToken ct);
}

/// <summary>
/// The entry's own vocabulary: the two labels the harness writes around a finished turn, and the tolerant read-back of an
/// answer that still carries one of its own. Nothing here is asked of the model - a reply is free-form - so these strings are
/// the contract between the entry renderer (Core) and the two observation windows (App), in one place rather than restated
/// at each end.
/// </summary>
public static class TurnReportTemplate
{
    /// <summary>The bold label that opens the human-readable answer in what the model reads (and in what the harness appends
    /// to history): markdown emphasis rather than a heading, so it renders inline and cannot be mistaken for a section a
    /// reply has to write itself.</summary>
    public const string ConclusionLabel = "**Conclusion**:";

    /// <summary>
    /// The spellings earlier builds used and rows already stored still carry: the Chinese label forms and the <c>##</c> headings.
    /// They are accepted on the way in (a reply written from memory, a conclusion recorded before the labels changed) and
    /// stripped on the way out; nothing writes them any more, so no prompt text mixes two languages.
    /// </summary>
    public static readonly string[] LegacyConclusionLabels = ["**结论**:", "## 结论", "## Conclusion"];

    /// <summary>
    /// The answer with its own vestiges of the harness's shape removed: a conclusion label or heading it echoed, and any
    /// stand-alone receipt line it copied. The reply is free-form, but a model that reads finished turns in its own history
    /// reproduces what it reads - measured: an <c>&lt;evidence …&gt;</c> line copied back with an invented <c>seq=</c>, which
    /// would then be rendered again next to the real one. Only a line of exactly such a label, or a line that is nothing but a
    /// receipt marker, counts.
    /// </summary>
    public static string UnwrapConclusion(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var at = -1;
        var length = 0;
        foreach (var token in AllConclusionLabels)
        {
            var candidate = LastOwnLine(answer, token);
            if (candidate > at)
            {
                at = candidate;
                length = token.Length;
            }
        }

        var body = at < 0 ? answer : answer[(at + length)..];
        return StripReceipts(body).Trim();
    }

    /// <summary>The current label first, then every legacy spelling: one list, allocated once.</summary>
    private static readonly string[] AllConclusionLabels = [ConclusionLabel, .. LegacyConclusionLabels];

    /// <summary>
    /// Every receipt marker removed, wherever it sits: the model sometimes copies one into the middle of its own sentence
    /// ("Hi again! 👋  <evidence …>"), so this cuts the marker out and leaves the words around it alone.
    /// </summary>
    private static string StripReceipts(string text)
    {
        if (text.IndexOf("evidence", StringComparison.Ordinal) < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var comment = text.IndexOf("<!-- evidence", index, StringComparison.Ordinal);
            var tag = text.IndexOf("<evidence", index, StringComparison.Ordinal);
            var at = comment < 0 ? tag : tag < 0 ? comment : Math.Min(comment, tag);
            if (at < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            builder.Append(text, index, at - index);
            var isComment = comment >= 0 && comment == at;
            var close = isComment
                ? text.IndexOf("-->", at, StringComparison.Ordinal)
                : text.IndexOf('>', at);
            if (close < 0)
            {
                builder.Append(text, at, text.Length - at);
                break;
            }

            index = close + (isComment ? 3 : 1);
            // The marker usually ends a line: the spaces in front of it go with it.
            while (builder.Length > 0 && (builder[^1] == ' ' || builder[^1] == '	'))
            {
                builder.Length--;
            }
        }

        return builder.ToString();
    }

    /// <summary>The last index of <paramref name="token"/> standing alone on its own line, or -1.</summary>
    private static int LastOwnLine(string text, string token)
    {
        var at = text.LastIndexOf(token, StringComparison.Ordinal);
        while (at >= 0)
        {
            if (IsOwnLine(text, at, token.Length))
            {
                return at;
            }

            at = at == 0 ? -1 : text.LastIndexOf(token, at - 1, StringComparison.Ordinal);
        }

        return -1;
    }

    /// <summary>True when the text at <paramref name="at"/> starts its own line and ends it (ignoring trailing blanks).</summary>
    private static bool IsOwnLine(string text, int at, int length)
    {
        var lineStart = at == 0 || text[at - 1] == '\n';
        var end = at + length;
        while (end < text.Length && (text[end] == ' ' || text[end] == '\t'))
        {
            end++;
        }

        return lineStart && (end == text.Length || text[end] == '\r' || text[end] == '\n');
    }

    /// <summary>What an empty value reads as; the parser treats it the same as an omitted key.</summary>
    public const string EmptyValue = "—";
}
