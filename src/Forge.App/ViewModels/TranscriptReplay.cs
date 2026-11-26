using System.Text;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.Core.Storage;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>Rebuilds a transcript from the stored messages of one session; the read is off the UI thread, the mapping on it.</summary>
internal static class TranscriptReplay
{
    /// <summary>
    /// One replay's one read: the stored page plus the metrics rows the session recorded. The two travel together on
    /// purpose — a caller cannot build a replayed transcript without its metadata, which is the invariant that kept
    /// breaking before (every history-loading path had to remember a separate seeding call).
    /// </summary>
    public sealed record Load(SessionPage Page, IReadOnlyList<TurnMetric> Metrics);

    public static Task<Load> ReadAllAsync(
        ISessionStore store, ITurnMetricsStore? turnMetrics, string sessionId, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var messages = new List<StoredMessage>();
            await foreach (var stored in store.ReadAsync(sessionId, 1, null, ct).ConfigureAwait(false))
            {
                messages.Add(stored);
            }

            return new Load(
                new SessionPage(messages, 0, HasMore: false),
                await ReadMetricsAsync(turnMetrics, sessionId, ct).ConfigureAwait(false));
        }, ct);

    /// <summary>
    /// Reads one page backwards from the end of the session (<paramref name="beforeSeq"/> = long.MaxValue for the newest page,
    /// otherwise the oldest sequence already shown), off the UI thread, together with the session's metrics rows. The page
    /// comes back starting at the user message that opened the turn it landed in (see
    /// <see cref="AlignToOpeningTurnAsync"/>).
    /// <para>A tab that already holds the metrics rows and the turn count above the page boundary passes them in
    /// (<paramref name="metrics"/>, <paramref name="turnsAboveBeforeSeq"/> = the previous page's
    /// <see cref="SessionPage.TurnsBefore"/>): metrics rows are immutable once written, and the store numbers the page from
    /// that count, so paging back does not re-read the session's whole metrics table or walk the message prefix (measured:
    /// 10k messages, 13 ms + 21 ms of every page). Null keeps the read.</para>
    /// </summary>
    public static async Task<Load> ReadPageAsync(
        ISessionStore store,
        ITurnMetricsStore? turnMetrics,
        string sessionId,
        long beforeSeq,
        int maxMessages,
        CancellationToken ct,
        IReadOnlyList<TurnMetric>? metrics = null,
        long? turnsAboveBeforeSeq = null)
    {
        // The rows are read first: where a turn began is what the page's start is aligned to, and both reads belong off the
        // UI thread (storage is synchronous under its async surface).
        var rows = metrics ?? await ReadMetricsAsync(turnMetrics, sessionId, ct).ConfigureAwait(false);
        var page = await Task.Run(
                async () => await AlignToOpeningTurnAsync(
                        store,
                        sessionId,
                        await store.ReadTailAsync(sessionId, beforeSeq, maxMessages, turnsAboveBeforeSeq, ct)
                            .ConfigureAwait(false),
                        rows,
                        ct)
                    .ConfigureAwait(false),
                ct)
            .ConfigureAwait(false);
        return new Load(page, rows);
    }

    /// <summary>
    /// Moves a page's start to the user message that opened the turn it landed in. The store aligns a page to a stored user
    /// message, which is not the same thing: a stored user message can also be an interjection (steering the loop delivered
    /// inside a turn) or a receipt, so a page starting there would draw that text as the turn's question and leave the real
    /// question on the page above it. A turn begins where the previous metrics row ends, so a page that lands inside a row's
    /// span is read back to the span's first user message — the rule the replay itself uses to tell a turn's opening input
    /// from an interjection. One read, bounded by one turn, and only for the pages that land inside one.
    /// <para>The rows are the only thing that knows where a turn began: a session whose rows carry no position (recorded
    /// before the column existed) keeps the boundary the store gave it.</para>
    /// </summary>
    private static async Task<SessionPage> AlignToOpeningTurnAsync(
        ISessionStore store,
        string sessionId,
        SessionPage page,
        IReadOnlyList<TurnMetric> metrics,
        CancellationToken ct)
    {
        if (page.Messages.Count == 0 || page.Messages[0].Message.Role != MessageRole.User)
        {
            return page;
        }

        var first = page.Messages[0].Seq;
        var ends = metrics
            .Where(metric => metric.LastSeq is > 0)
            .OrderBy(metric => metric.LastSeq)
            .ToArray();
        var landedIn = Array.FindIndex(ends, metric => metric.LastSeq!.Value >= first);
        if (landedIn < 0)
        {
            return page;
        }

        var spanStart = landedIn == 0 ? 1 : ends[landedIn - 1].LastSeq!.Value + 1;
        if (spanStart >= first)
        {
            // The page already starts at (or before) the turn's opening input.
            return page;
        }

        var prefix = new List<StoredMessage>();
        await foreach (var stored in store.ReadAsync(sessionId, spanStart, first - 1, ct).ConfigureAwait(false))
        {
            prefix.Add(stored);
        }

        // The loop persists a turn's input before anything else of it, so a span begins with that input; a span that does not
        // is one this cannot place, and its page stays what the store read.
        if (prefix.Count == 0 || prefix[0].Message.Role != MessageRole.User)
        {
            return page;
        }

        var messages = new List<StoredMessage>(prefix.Count + page.Messages.Count);
        messages.AddRange(prefix);
        messages.AddRange(page.Messages);
        var insidePrefix = 0L;
        foreach (var stored in prefix)
        {
            if (stored.Message.Role == MessageRole.User)
            {
                insidePrefix++;
            }
        }

        return page with
        {
            Messages = messages,
            TurnsBefore = page.TurnsBefore - insidePrefix,
            // The row above the span ends at a stored message, so a span that does not start the session has more above it.
            HasMore = spanStart > 1,
        };
    }

    /// <summary>
    /// The session's metrics rows, best-effort like everything metrics: a source a view does not have, or a read that
    /// failed, answers empty — the rows only fill blanks, they never turn into a notice.
    /// </summary>
    private static async Task<IReadOnlyList<TurnMetric>> ReadMetricsAsync(
        ITurnMetricsStore? turnMetrics, string sessionId, CancellationToken ct)
    {
        if (turnMetrics is null)
        {
            return [];
        }

        try
        {
            return await turnMetrics.ListAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException
            or ObjectDisposedException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Session.ZLogWarning($"turn metrics read skipped: {exception.GetType().Name}: {exception.Message}");
            return [];
        }
    }

    /// <summary>
    /// Replays a page into a transcript of its own, numbered from the rounds that precede it: the caller either takes it as the
    /// first content of a tab or prepends it in front of what is already on screen.
    /// </summary>
    public static (TranscriptViewModel Page, TokenUsage Usage) ReplayPage(Load load, bool collapseOlderTurns = false)
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns);
        transcript.SeedRoundCounter(load.Page.TurnsBefore);
        var usage = Apply(transcript, load.Page.Messages, load.Metrics);
        return (transcript, usage);
    }

    /// <summary>
    /// Replays <paramref name="messages"/> into <paramref name="transcript"/> and returns the usage they carried. Each
    /// turn's divider fields (wall clock, stream time, tool count, outcome, model, reasoning step) come from
    /// <paramref name="metrics"/>, paired by the row's <c>last_seq</c>: the seq of the last message the turn persisted —
    /// the card that consumed that very message is the turn's card, an exact position match that receipts and
    /// interjections cannot shift the way a counted round number drifted. Turn boundaries follow the live transcript
    /// rather than the raw message shapes: the rows' positions cut the stream into spans, the first user message of a
    /// span is the turn's opening input (the loop persists it before anything else of the turn), a later one is
    /// mid-turn steering drawn as the notice the live turn showed, and a receipt — stored as a user message because
    /// that is what the next turn reads — never opens a card. A span whose row skips over a missing row (the recorder
    /// may drop one) holds more than one turn's content, so every user message in it opens a card and its row is
    /// claimed only when exactly one opener exists — with more, no card can be proven to be the one the row's turn
    /// ended on, and the row stays unclaimed rather than stamped on the wrong card. A row without a seq (recorded
    /// before the column existed) pairs with nothing and leaves the card's replayed values reading "—"; history beyond
    /// the last row keeps the one-card-per-user-message shape and its own numbering.
    /// </summary>
    public static TokenUsage Apply(
        TranscriptViewModel transcript, IReadOnlyList<StoredMessage> messages, IReadOnlyList<TurnMetric> metrics)
    {
        var ends = metrics
            .Where(metric => metric.LastSeq is > 0)
            .OrderBy(metric => metric.LastSeq)
            .ToArray();
        var bySeq = new Dictionary<long, TurnMetric>(ends.Length);
        foreach (var metric in ends)
        {
            bySeq[metric.LastSeq!.Value] = metric;
        }

        // The last message each card consumed, in seq order — a card's last consumed seq is the position its row names.
        // Nothing is built when no row carries a position: there is nothing to pair.
        Dictionary<TurnCardViewModel, long>? lastSeqOfCard = ends.Length > 0 ? new() : null;

        var inputTokens = 0;
        var outputTokens = 0;
        var cacheReadTokens = 0;
        var cacheWriteTokens = 0;
        var cacheWrite1hTokens = 0;

        // The row whose span the next messages belong to (the first row whose position reaches past them), and whether
        // that span's opening user message has been drawn yet — together they tell a turn's input from a steering
        // message that arrived inside it. A row whose turn number skips over a missing row cannot bound its span:
        // the span then holds more than one turn's content (a dropped row is allowed — the recorder may drop one),
        // so every user message in it opens a card and the row is only claimed when exactly one opener exists —
        // with more, no card can be proven to be the one the row's turn ended on, and an unprovable pairing is
        // skipped rather than stamped on the wrong card.
        var spanGapped = new bool[ends.Length];
        for (var i = 0; i < ends.Length; i++)
        {
            spanGapped[i] = ends[i].Turn != (i == 0 ? 1 : ends[i - 1].Turn + 1);
        }

        var spanOpeners = new int[ends.Length];
        var rowAt = 0;
        var spanOpened = false;
        foreach (var stored in messages)
        {
            while (rowAt < ends.Length && ends[rowAt].LastSeq < stored.Seq)
            {
                rowAt++;
                spanOpened = false;
            }

            var covered = rowAt < ends.Length ? ends[rowAt] : null;
            var strict = covered is not null && !spanGapped[rowAt];
            var outcome = Apply(transcript, stored, covered, strict, ref spanOpened);
            if (outcome == ReplayMessage.Opened && covered is not null)
            {
                spanOpeners[rowAt]++;
            }

            if (outcome != ReplayMessage.None && lastSeqOfCard is not null && transcript.Current is { } card)
            {
                lastSeqOfCard[card] = stored.Seq;
            }

            if (stored.Usage is { } usage && (usage.InputTokens > 0 || usage.OutputTokens > 0))
            {
                transcript.Current?.AddUsage(usage, StopReason.EndTurn);
                inputTokens += usage.InputTokens;
                outputTokens += usage.OutputTokens;

                // Cache tokens are part of the stored usage and drive both the cache-hit ratio and the cost estimate, so a
                // reopened session has to replay them too; dropping them would report a 0% hit rate on a real number. The
                // hour-lived cache write is its own column for the same reason: it is priced at twice the input rate and is
                // not part of InputTokens, so a sum that skipped it would under-report the session's spend.
                cacheReadTokens += usage.CacheReadTokens;
                cacheWriteTokens += usage.CacheWriteTokens;
                cacheWrite1hTokens += usage.CacheWrite1hTokens;
            }
        }

        transcript.EndTurn(null, null);

        // A span that skipped a missing row and holds more than one opening message cannot say which turn its row
        // ended: the row stays unclaimed (its divider reads "—") rather than stamping another turn's card — an
        // unprovable pairing is worse than none.
        var ambiguous = new HashSet<TurnMetric>();
        for (var i = 0; i < ends.Length; i++)
        {
            if (spanGapped[i] && spanOpeners[i] > 1)
            {
                ambiguous.Add(ends[i]);
            }
        }

        // The pairing itself: a card's own last position meets the row that names it. Rows whose seq no card consumed
        // (an older page's rows, one the replay never reached) simply stay unclaimed, so the same whole-session list
        // serves every page without slicing. A row seeds its card's readings and nothing else.
        //
        // The page's round numbers stay one sequence: the count the replay assigned is ordered by construction, while a
        // row's own turn number can skip (a row the recorder dropped) or repeat what a neighbour says, and adopting it
        // card by card is what made two rounds of one page both read "第 N 轮". What the rows can correct is where the
        // sequence starts - the count also counts stored user messages that opened no turn (receipts, interjections) -
        // so they move the whole page by one amount, and only when every paired card derives the same one.
        var paired = 0;
        int? turnsBeforePage = null;
        var turnsBeforeAgree = true;
        if (lastSeqOfCard is not null)
        {
            foreach (var (card, seq) in lastSeqOfCard)
            {
                if (!bySeq.TryGetValue(seq, out var metric) || ambiguous.Contains(metric))
                {
                    continue;
                }

                card.SeedReplayMetrics(metric);
                paired++;
                var derived = metric.Turn - (card.Round - transcript.RoundSeed);
                if (turnsBeforePage is not { } known)
                {
                    turnsBeforePage = derived;
                }
                else if (known != derived)
                {
                    turnsBeforeAgree = false;
                }
            }
        }

        if (paired > 0 && turnsBeforeAgree && turnsBeforePage is { } baseTurn)
        {
            transcript.ShiftRoundNumbers(baseTurn - transcript.RoundSeed);
        }

        return new TokenUsage(inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, cacheWrite1hTokens);
    }

    /// <summary>What one message did to the transcript: nothing, content on the current card, or a turn opened.</summary>
    private enum ReplayMessage
    {
        None,
        Joined,
        Opened,
    }

    /// <summary>
    /// Replays one message. Answers what it did — only <see cref="ReplayMessage.Joined"/> and
    /// <see cref="ReplayMessage.Opened"/> messages join the card that is current, everything the turn's own row can
    /// pair by; a receipt draws as the reply card it is and never claims a turn's position. A user message is the
    /// span's opening input only when the span's row can bound it (<paramref name="strict"/>) and nothing opened
    /// yet; a later one inside a strict span is mid-turn steering, delivered at a tool-batch boundary and shown as
    /// the notice the live turn showed — never a card of its own, so the turn's tail stays on the card its question
    /// opened. Without a strict span (a row whose turn number skipped a missing one, history beyond the last row)
    /// every user message opens a card, exactly as it always did.
    /// </summary>
    private static ReplayMessage Apply(
        TranscriptViewModel transcript, StoredMessage stored, TurnMetric? covered, bool strict, ref bool spanOpened)
    {
        var message = stored.Message;
        var at = stored.At;
        switch (message.Role)
        {
            case MessageRole.User:
                var text = OrchestrationDirective.Strip(TextOf(message, skipAttachments: true));
                var attachments = AttachmentsOf(message);
                if (SessionReplyViewModel.IsReply(text))
                {
                    // A receipt is stored as a user message (that is what the next turn reads) but never opened a live
                    // turn — not between turns, not inside one at a tool-batch boundary — so the replay draws it as the
                    // reply card it is and leaves the current turn's card current.
                    transcript.AddSessionReply(text, at);
                    return ReplayMessage.None;
                }

                if (text.Length == 0 && attachments.Count == 0)
                {
                    return ReplayMessage.None;
                }

                if (strict && spanOpened && transcript.Current is not null)
                {
                    transcript.Current.AddNotice(SessionStrings.ComposerInterjectedNotice(text, attachments), fromUser: true);
                    return ReplayMessage.Joined;
                }

                if (covered is not null)
                {
                    spanOpened = true;
                }

                transcript.AddUserMessage(text, at, attachments);
                transcript.BeginTurn(at, closePrevious: true);
                return ReplayMessage.Opened;
            case MessageRole.Assistant:
                var card = transcript.Current ?? transcript.BeginTurn(at);

                // The content is replayed in its stored order: a provider records the reasoning of a request before the answer
                // of that request, and a reopened session must show the thinking rows the live one showed.
                foreach (var block in message.Content)
                {
                    switch (block)
                    {
                        case TextBlock piece when piece.Text.Length > 0:
                            card.AppendText(piece.Text);
                            break;
                        case ThinkingBlock thinking when thinking.Text.Length > 0:
                            card.AppendThinking(thinking.Text);
                            break;
                        case ToolCallBlock call:
                            card.AddTool(new ToolCall(call.Id, call.Name, call.ArgumentsJson));
                            break;
                    }
                }

                return ReplayMessage.Joined;
            case MessageRole.Tool:
                var target = transcript.Current;
                if (target is null)
                {
                    return ReplayMessage.None;
                }

                foreach (var block in message.Content)
                {
                    if (block is ToolResultBlock result)
                    {
                        target.CompleteTool(
                            new ToolCall(result.CallId, string.Empty, string.Empty),
                            new ToolResult(result.Blocks, result.IsError));
                    }
                }

                target.Finish(null, null);
                return ReplayMessage.Joined;
            default:
                return ReplayMessage.None;
        }
    }

    /// <summary>The attachments of a user message: images, files, and text files inlined in the attachment shape.</summary>
    private static List<ContentBlock> AttachmentsOf(ChatMessage message)
    {
        List<ContentBlock>? attachments = null;
        foreach (var block in message.Content)
        {
            if (block is ImageBlock or FileBlock || TextAttachment.IsAttachment(block))
            {
                (attachments ??= []).Add(block);
            }
        }

        return attachments ?? [];
    }

    private static string TextOf(ChatMessage message, bool skipAttachments = false)
    {
        var builder = new StringBuilder();
        foreach (var block in message.Content)
        {
            if (block is TextBlock text && !(skipAttachments && TextAttachment.IsAttachment(block)))
            {
                builder.Append(text.Text);
            }
        }

        return builder.ToString();
    }
}
