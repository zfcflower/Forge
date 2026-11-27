using System.Globalization;
using System.Text;
using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// The managed context: the model's accumulated history is one rendered entry per finished turn, plus - once history has
/// to shrink - one folded block covering the oldest turns. Nothing in here calls a model and nothing summarizes: every
/// line is either a stored field of the turn's ledger row or a fixed label, so the same rows always render the same bytes
/// (forge-core "托管上下文").
/// <para>The current turn is never rendered here: its raw exchange (user, assistant, every tool call and result) is what
/// the loop appends to the request while it runs, and only the finished turn becomes an entry.</para>
/// <para>An entry is <b>two</b> messages, not one: the task as a <c>user</c> message and the report as an <c>assistant</c>
/// one. The split is what keeps a provider's cache alive across turns: a gateway that compares whole messages (and welds
/// adjacent same-role ones together) can only reuse a prefix that ends on a message boundary, so one-message entries made
/// the entire history a single indivisible block that any new turn invalidated (measured: 0% read against opencode's
/// gateway, 84% once split - forge-core "托管上下文与确定性折叠").</para>
/// </summary>
internal static class ManagedContext
{
    /// <summary>The newest turns are never folded: a task's immediate past stays readable without a lookup.</summary>
    public const int MinRecentEntries = 3;

    /// <summary>
    /// The conclusion as the request shows it: the model's reply can be long, and the request carries the newest turns
    /// verbatim - so it is cut here, with the full text one <c>read_context</c> away. The cut is deterministic (characters, no
    /// tokenizer), which is what keeps the prompt-cache bytes stable.
    /// </summary>
    private const int ConclusionChars = 1_200;

    /// <summary>
    /// One finished turn as a tool reads it back: the task, the reply, then the receipt line. <paramref name="full"/> keeps the
    /// whole reply (see <see cref="ReadContextTool"/>). The request renders the same three parts as three messages plus the
    /// call that owns the receipt, so what a tool answers and what the model reads come from one set of renderers.
    /// </summary>
    public static string RenderEntry(TurnLedgerEntry entry, bool full = false)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return string.Concat(RenderTask(entry), "\n\n", RenderReply(entry, full), "\n\n", RenderReceipt(entry));
    }

    /// <summary>
    /// What was asked, as the request's <c>user</c> message. Verbatim and never cut: a truncated task reads as if the user had
    /// said less than they did. Empty text renders the template's own empty value, so no request carries an empty message.
    /// </summary>
    public static string RenderTask(TurnLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.TaskText.Length == 0 ? TurnReportTemplate.EmptyValue : entry.TaskText;
    }

    /// <summary>
    /// That turn's answer, as the request's <c>assistant</c> message: the reply itself and nothing else. No label opens it and
    /// no receipt follows it - whatever the harness adds to a reply is a shape the model copies back into its own answer
    /// (measured twice: a bold conclusion label, then a <c>&lt;evidence …&gt;</c> line, both echoed with invented numbers).
    /// </summary>
    public static string RenderReply(TurnLedgerEntry entry, bool full = false)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // A row recorded under an older template carries a conclusion label or heading inside the answer; unwrapping it
        // here keeps the entry from showing that heading and the text's at once. `full` keeps the whole reply for read_context.
        var reply = TurnReportTemplate.UnwrapConclusion(entry.ConclusionText);
        return reply.Length == 0 ? TurnReportTemplate.EmptyValue : full ? reply : Cut(reply, ConclusionChars);
    }

    /// <summary>
    /// The receipt of one turn, as the <c>tool</c> result that closes its exchange: everything the harness knows that the reply
    /// does not say. It rides a tool call for one reason - <c>tool</c> is the only role a model cannot write, so a receipt
    /// living there cannot be imitated into its next answer, while one living in the assistant message will be (that is what
    /// the previous two shapes did). The keys name what the tools take: <c>turn</c> is <c>read_context</c>'s
    /// <c>from_turn</c>, <c>seq</c> is <c>read_history</c>'s <c>from_seq</c>/<c>to_seq</c>.
    /// </summary>
    public static string RenderReceipt(TurnLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var text = new StringBuilder(160);
        text.Append("{\"turn\":").Append(entry.TurnSequence.ToString(CultureInfo.InvariantCulture))
            .Append(",\"seq\":\"").Append(entry.FromSeq.ToString(CultureInfo.InvariantCulture))
            .Append('-').Append(entry.ToSeq.ToString(CultureInfo.InvariantCulture)).Append('"');
        AppendList(text, "reads", entry.ReadFiles);
        AppendList(text, "writes", entry.ChangedFiles);
        AppendList(text, "specs", entry.Specs);
        return text.Append('}').ToString();
    }

    /// <summary>The arguments of one turn's receipt call: the turn it belongs to, so the exchange reads as one ask per turn.</summary>
    private static string ReceiptArguments(TurnLedgerEntry entry) =>
        string.Concat("{\"turn\":", entry.TurnSequence.ToString(CultureInfo.InvariantCulture), "}");

    /// <summary>The call id of one turn's receipt: stable per turn, so the same rows always render the same bytes.</summary>
    public static string ReceiptCallId(long turn) =>
        string.Concat(ReceiptCallPrefix, turn.ToString(CultureInfo.InvariantCulture));

    /// <summary>The name of the tool call that carries a receipt. Deliberately not a declared tool: nothing executes it, and a
    /// model that tries cannot succeed.</summary>
    public const string ReceiptToolName = "evidence_receipt";

    private const string ReceiptCallPrefix = "ev-";

    private static void AppendList(StringBuilder text, string key, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        text.Append(",\"").Append(key).Append("\":[");
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                text.Append(',');
            }

            text.Append('"').Append(values[index].Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
        }

        text.Append(']');
    }

    /// <summary>
    /// The folded block that stands in for a run of old turns: which turns it covers and the two lookup lines the model can
    /// copy verbatim. Deterministic from the rows, never a summary - and it aggregates nothing, because an entry no longer
    /// carries a field a summary could be rebuilt from.
    /// </summary>
    public static string RenderFold(IReadOnlyList<TurnLedgerEntry> folded)
    {
        ArgumentNullException.ThrowIfNull(folded);
        if (folded.Count == 0)
        {
            return string.Empty;
        }

        var first = folded[0];
        var last = folded[^1];
        var header = string.Concat(
            "[compacted: turns ",
            first.TurnSequence.ToString(CultureInfo.InvariantCulture),
            "–",
            last.TurnSequence.ToString(CultureInfo.InvariantCulture),
            " · seq ",
            first.FromSeq.ToString(CultureInfo.InvariantCulture),
            "–",
            last.ToSeq.ToString(CultureInfo.InvariantCulture),
            " · ",
            folded.Count.ToString(CultureInfo.InvariantCulture),
            " turns]\n");
        var tail = string.Concat(
            "retrieve: read_context(from_turn=",
            first.TurnSequence.ToString(CultureInfo.InvariantCulture),
            ", to_turn=",
            last.TurnSequence.ToString(CultureInfo.InvariantCulture),
            ")\noriginals: read_history(from_seq=",
            first.FromSeq.ToString(CultureInfo.InvariantCulture),
            ", to_seq=",
            last.ToSeq.ToString(CultureInfo.InvariantCulture),
            ")");

        return string.Concat(header, tail);
    }

    /// <summary>
    /// <summary>
    /// The highest `to_seq` of the settled turns the view renders - the boundary after which a stored message (a receipt
    /// delivered while the session was idle, an interjection) belongs to no entry yet and has to be appended verbatim.
    /// Zero when no settled row exists.
    /// </summary>
    public static long LastSeq(IReadOnlyList<TurnLedgerEntry> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        long last = 0;
        foreach (var row in rows)
        {
            if (row.Status != TurnLedgerStatus.Pending && row.ToSeq > last)
            {
                last = row.ToSeq;
            }
        }

        return last;
    }

    /// <summary>
    /// The one-line legend every managed request carries ahead of its history: what a field means and which tool reads it back.
    /// Written once per request rather than per entry, and fixed bytes, so it costs the prompt cache nothing.
    /// <para>It leads the <em>request</em> rather than the view on purpose. Inside the view it could only appear once the
    /// session had a settled turn to render, so a session's first turn went out without it and the second turn's first
    /// request suddenly led with it: writers merge adjacent same-role messages, so that turned the history's first block from
    /// the question into legend-plus-question, and every request of that turn re-read the whole 21k system+tools for a prompt
    /// whose history had not changed (measured). Leading every request with it keeps that block identical from the first turn
    /// on.</para>
    /// </summary>
    public const string Legend =
        "[managed context] each finished turn is three messages: the user message that asked, the assistant message that "
        + "answered, and the harness's own tool result carrying that turn's receipt. The receipt is data, not something to "
        + "write or repeat: turn is the number read_context(from_turn, to_turn) takes, seq its message range for "
        + "read_history(from_seq, to_seq), reads the files that turn opened, writes the files it changed, and specs the specs "
        + "it touched. A [compacted: ...] block stands in for older turns and names read_context(from_turn, to_turn) for "
        + "them.";

    /// <summary>
    /// The managed history as the request carries it: the legend, the folded block (when anything is folded), then one entry per
    /// unfolded turn, oldest first. Each entry is a <c>user</c> message (its task) followed by an <c>assistant</c> message (its
    /// report), so a new turn appends to a message-level prefix a provider can still reuse instead of rewriting one indivisible
    /// history block.
    /// </summary>
    public static IReadOnlyList<ChatMessage> Build(IReadOnlyList<TurnLedgerEntry> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return [];
        }

        // A pending row is the turn in flight: it has no answer to render, and its own exchange is what the request
        // already carries after this view. Rendering it would put the turn into its own history - as an empty entry, on
        // the stable-boundary breakpoint, with a `seq: n-0` pin - so it is skipped here by construction.
        List<TurnLedgerEntry>? folded = null;
        var messages = new List<ChatMessage>((rows.Count * 2) + 2);
        foreach (var row in rows)
        {
            if (row.Folded && row.Status != TurnLedgerStatus.Pending)
            {
                (folded ??= []).Add(row);
            }
        }

        if (folded is { Count: > 0 })
        {
            messages.Add(ChatMessage.User(RenderFold(folded)));
        }

        foreach (var row in rows)
        {
            if (!row.Folded && row.Status != TurnLedgerStatus.Pending)
            {
                messages.Add(ChatMessage.User(RenderTask(row)));
                messages.Add(ChatMessage.Assistant(RenderReply(row)));
                messages.Add(new ChatMessage(
                    MessageRole.Assistant,
                    [new ToolCallBlock(ReceiptCallId(row.TurnSequence), ReceiptToolName, ReceiptArguments(row))]));
                messages.Add(new ChatMessage(
                    MessageRole.Tool,
                    [new ToolResultBlock(ReceiptCallId(row.TurnSequence), RenderReceipt(row))]));
            }
        }

        return messages;
    }

    /// <summary>One bounded line: newlines folded to spaces, the tail replaced by an ellipsis when the cap is hit.</summary>
    private static string Cut(string value, int limit)
    {
        var text = value.ReplaceLineEndings(" ").Trim();
        return text.Length <= limit ? text : string.Concat(text.AsSpan(0, limit - 1), "…");
    }
}
