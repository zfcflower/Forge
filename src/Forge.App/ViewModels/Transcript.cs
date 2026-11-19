using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Forge.Abstractions;
using Forge.App.Changes.Markdown;
using Forge.App.Localization;
using Forge.Core.Providers;
using Forge.Core.Storage;

namespace Forge.App.ViewModels;

internal abstract class TranscriptItemViewModel : NotifyingObject
{
    /// <summary>
    /// Re-raises the item's text properties in the current UI language. The owning session calls this after a language
    /// switch; the texts themselves are derived in the getters, never cached, so this only notifies.
    /// </summary>
    public virtual void RefreshText()
    {
    }
}

/// <summary>
/// A user message, rendered as an IM-style bubble aligned right: the typed text, image thumbnails, and a chip (icon, name,
/// size) for each other attachment - PDF, audio, video, or a text file inlined into the message.
/// </summary>
internal sealed class UserMessageViewModel : TranscriptItemViewModel, ICopyableText
{
    public UserMessageViewModel(
        string text,
        DateTimeOffset at,
        IReadOnlyList<ContentBlock>? attachments = null,
        int? round = null,
        TimeSpan? gapSincePrevious = null)
    {
        Text = text;
        Time = TextFormat.Clock(at);
        Round = round;
        GapSincePrevious = gapSincePrevious;
        if (attachments is not { Count: > 0 })
        {
            Images = [];
            Files = [];
            return;
        }

        List<TranscriptImageViewModel>? images = null;
        List<TranscriptFileViewModel>? files = null;
        foreach (var block in attachments)
        {
            if (block is ImageBlock image)
            {
                (images ??= []).Add(new TranscriptImageViewModel(image));
            }
            else if (FileChipViewModel.Describe(block) is { } chip)
            {
                (files ??= []).Add(new TranscriptFileViewModel(block, chip.Name, chip.Kind, chip.MediaType, chip.Bytes));
            }
        }

        Images = images is null ? [] : images;
        Files = files is null ? [] : files;
    }

    public string Text { get; }

    public bool HasText => Text.Length > 0;

    /// <summary>The images the message carried, as thumbnails; empty for a text-only message.</summary>
    public IReadOnlyList<TranscriptImageViewModel> Images { get; }

    public bool HasImages => Images.Count > 0;

    /// <summary>The other attachments the message carried, as chips.</summary>
    public IReadOnlyList<TranscriptFileViewModel> Files { get; }

    public bool HasFiles => Files.Count > 0;

    public string Time { get; }

    /// <summary>
    /// The round this message starts, peeked from the transcript's counter when the message arrived. The card the next
    /// <c>BeginTurn</c> opens carries the same number, so the subtitle names the round the reply below belongs to.
    /// Null for a transcript that never numbers rounds (sub-agent transcripts have no user turns).
    /// </summary>
    public int? Round { get; }

    /// <summary>How long after the previous user message this one arrived; null for the first round of the transcript.</summary>
    public TimeSpan? GapSincePrevious { get; }

    /// <summary>Whether the message carries a round marker: only transcripts that number rounds do.</summary>
    public bool HasRound => Round.HasValue;

    /// <summary>
    /// The round marker on the meta line under the message: <c>第 8 轮</c>. Rendered in the accent ink; null on a
    /// transcript that never numbers rounds (a sub-agent tab), where the meta line is just the clock.
    /// </summary>
    public string? RoundText => Round is { } round ? SessionStrings.UserTurnRound(round) : null;

    /// <summary>
    /// The rest of the meta line: <c> · 12 分钟后 · 12:30:45</c> under the round marker, or the bare clock when there is
    /// none. The gap segment is omitted for the first round; the clock is the round's local start time, derived on every
    /// read so a language switch re-renders it.
    /// </summary>
    public string MetaText => (Round.HasValue ? " · " : string.Empty)
        + (GapSincePrevious is { } gap ? SessionStrings.TurnGap(TextFormat.Elapsed(gap)) + " · " : string.Empty)
        + Time;

    string ICopyableText.CopyText => Text;

    public override void RefreshText()
    {
        OnPropertyChanged(nameof(RoundText));
        OnPropertyChanged(nameof(MetaText));
        foreach (var file in Files)
        {
            file.RefreshText();
        }
    }
}

/// <summary>A one-line system note inside a transcript (compaction, denied call, stopped turn).</summary>
internal sealed class NoticeViewModel(LocText text, bool isError = false, TimelineNodeKind kind = TimelineNodeKind.Notice)
    : TranscriptItemViewModel
{
    /// <summary>The note in the current language, resolved when the note is rendered.</summary>
    public string Text => text.Resolve();

    public bool IsError { get; } = isError;

    /// <summary>Which timeline node the note wears; a denied approval is <see cref="TimelineNodeKind.Approval"/>.</summary>
    public TimelineNodeKind Kind { get; } = kind;

    public override void RefreshText() => OnPropertyChanged(nameof(Text));
}

/// <summary>Base of the blocks inside a turn card: reply text, tool rows and notes, in arrival order.</summary>
internal abstract class TurnBlockViewModel : ObservableObject
{
    /// <summary>The card this block belongs to; set when the card adds it. The flat transcript row needs it to find its turn.</summary>
    public TurnCardViewModel? Turn { get; internal set; }

    /// <summary>Re-raises the block's text properties in the current UI language; see <see cref="TranscriptItemViewModel.RefreshText"/>.</summary>
    public virtual void RefreshText()
    {
    }
}

/// <summary>
/// One run of reply text. The text streams into a <see cref="StreamingMarkdown" /> document that commits block boundaries as
/// they close, so a flush hands the view only the new blocks and the unfinished tail instead of re-laying out the whole reply.
/// </summary>
internal sealed class TurnTextViewModel : TurnBlockViewModel, ICopyableText
{
    private readonly StreamingMarkdown document = new();
    private LocText placeholder;
    private bool isStreaming = true;

    /// <summary>Raised on the UI thread after <see cref="Append"/> or <see cref="Complete"/> changed the document.</summary>
    public event EventHandler? ContentChanged;

    /// <summary>The incremental document the chat view renders.</summary>
    public StreamingMarkdown Document => document;

    public bool IsStreaming
    {
        get => isStreaming;
        private set => Set(ref isStreaming, value);
    }

    /// <summary>Whether to show the "the model returned no text" placeholder instead of content.</summary>
    public bool HasPlaceholder => !placeholder.IsEmpty;

    /// <summary>The placeholder in the current language; empty unless the reply arrived empty.</summary>
    public string PlaceholderText => placeholder.Resolve();

    /// <summary>The reply's whole text, exactly as it arrived; the placeholder of a reply that never arrived is not text.</summary>
    public string CopyText => document.Source;

    public void Append(string chunk)
    {
        if (chunk.Length == 0)
        {
            return;
        }

        document.Append(chunk);
        RaiseContentChanged();
    }

    /// <summary>Closes the reply; idempotent, so a text block cut off by a tool row can be sealed and finished alike.</summary>
    public void Complete()
    {
        if (!isStreaming)
        {
            return;
        }

        IsStreaming = false;
        document.Complete();
        if (document.IsEmpty)
        {
            placeholder = SessionStrings.NoText;
            OnPropertyChanged(nameof(PlaceholderText));
            OnPropertyChanged(nameof(HasPlaceholder));
        }

        RaiseContentChanged();
    }

    public override void RefreshText() => OnPropertyChanged(nameof(PlaceholderText));

    private void RaiseContentChanged() => ContentChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// One stretch of the model's reasoning, in its turn card's own place in the block order. It streams like reply text but
/// carries no Markdown: collapsed (the default) the row shows the first line, opened it shows the whole text, so a long chain
/// of thought never pushes the answer out of sight. Reasoning lives only in the event stream - it is not stored - so a
/// replayed session has no thinking rows.
/// </summary>
internal sealed class TurnThinkingViewModel : TurnBlockViewModel
{
    /// <summary>Characters of the first line the collapsed row shows before it stops with an ellipsis.</summary>
    internal const int PreviewCharacters = 160;

    private readonly StringBuilder text = new();
    private string? materialized;
    private string? preview;
    private bool isStreaming = true;
    private bool isExpanded;

    /// <summary>True while the model is still reasoning: the timeline node wears the running state until it stops.</summary>
    public bool IsStreaming
    {
        get => isStreaming;
        private set => Set(ref isStreaming, value);
    }

    /// <summary>Collapsed by default: the answer, not the reasoning, is what the user reads first.</summary>
    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }

    public RelayCommand ToggleCommand => new(() => IsExpanded = !IsExpanded);

    /// <summary>Whether anything at all has arrived; a reasoning row with no text is not shown as a preview.</summary>
    public bool HasText => text.Length > 0;

    /// <summary>The reasoning as it has arrived so far, exactly as the provider sent it.</summary>
    public string Text => materialized ??= text.ToString();

    /// <summary>
    /// The first non-blank line, capped at <see cref="PreviewCharacters">; what the collapsed row shows. Only that line is
    /// read out of the buffer, so streaming a long paragraph stays O(to the cap) instead of copying the whole text per frame.
    /// </summary>
    public string Preview => preview ??= BuildPreview();

    public void Append(string chunk)
    {
        if (chunk.Length == 0)
        {
            return;
        }

        text.Append(chunk);
        materialized = null;
        preview = null;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(HasText));
    }

    /// <summary>Closes the reasoning stretch; idempotent, like the reply text it precedes.</summary>
    public void Complete()
    {
        if (isStreaming)
        {
            IsStreaming = false;
        }
    }

    private string BuildPreview()
    {
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        var end = start;
        while (end < text.Length && end - start < PreviewCharacters && text[end] != '\n')
        {
            end++;
        }

        var line = text.ToString(start, end - start).TrimEnd();
        return end < text.Length && text[end] != '\n' ? line + "…" : line;
    }
}

/// <summary>A note inside a turn card, such as a redirected or stopped sub-agent.</summary>
internal sealed class TurnNoticeViewModel(LocText text, bool isError, TimelineNodeKind kind = TimelineNodeKind.Notice)
    : TurnBlockViewModel
{
    /// <summary>The note in the current language, resolved when the note is rendered.</summary>
    public string Text => text.Resolve();

    public bool IsError { get; } = isError;

    /// <summary>Which timeline node the note wears; a denied approval is <see cref="TimelineNodeKind.Approval"/>.</summary>
    public TimelineNodeKind Kind { get; } = kind;

    public override void RefreshText() => OnPropertyChanged(nameof(Text));
}

/// <summary>
/// How a turn ended, for the round-end divider: a completed turn shows its tokens and rate, any other outcome shows
/// its status with the tokens as unknown. Mirrors <see cref="Forge.Core.Storage.TurnMetricStatus"/> for the values the
/// transcript itself can tell apart; the metrics table is the authority, the card only seeds from it on replay.
/// </summary>
internal enum TurnEndStatus
{
    Completed,
    Stopped,
    Failed,
    Limit,
}

/// <summary>
/// One agent turn: a light card whose header carries "第 N 轮 · 时间 · 耗时 · 输入/输出 token" and whose body is the tree of
/// reply text and tool rows. A token side the provider never reported is shown as "—".
/// </summary>
internal sealed class TurnCardViewModel : TranscriptItemViewModel
{
    private readonly DateTimeOffset startedAt;
    private readonly Dictionary<string, ToolRowViewModel> toolRows = new(StringComparer.Ordinal);
    private readonly ObservableCollection<TurnBlockViewModel> blocks = [];
    private TurnTextViewModel? openText;
    private TurnThinkingViewModel? openThinking;
    private TurnFootnoteViewModel? footnoteRow;
    private TurnDividerViewModel? dividerRow;
    private LocText footnote;
    private TimeSpan? lastElapsed;
    private bool isExpanded = true;
    private bool isRunning = true;
    private int inputTokens;
    private int outputTokens;
    private int cacheReadTokens;
    private int cacheWriteTokens;
    private int cacheWrite1hTokens;
    private StopReason stop = StopReason.EndTurn;
    private bool sawUsage;

    /// <summary>
    /// How long the model spent streaming this turn's answers: the sum of the agent's stream-time differences since the
    /// turn began (see <see cref="NoteStream"/>), or the row's <c>stream_ms</c> for a replayed turn. It is the divider's
    /// output-rate denominator; the wall clock (<see cref="Elapsed"/>) stays the "耗时" segment. Zero means the turn
    /// streamed nothing — a fact, never an unknown.
    /// </summary>
    internal TimeSpan StreamElapsed { get; private set; }

    /// <summary>Every tool call this card saw start, including a late result whose call row never opened.</summary>
    private int toolCallsTotal;

    /// <summary>A replayed tool count from <c>turn_metrics</c>, used only while the replay itself saw no tool call.</summary>
    private int? seededToolCalls;

    /// <summary>The provider and model the metrics table recorded for this turn; live turns never know them.</summary>
    private string? providerName;
    private string? modelName;

    /// <summary>The failure text the metrics table recorded; the tooltip shows it when the turn left no footnote.</summary>
    private string? seededError;

    public TurnCardViewModel(int round, DateTimeOffset startedAt)
    {
        Round = round;
        this.startedAt = startedAt;
        Blocks = new ReadOnlyObservableCollection<TurnBlockViewModel>(blocks);
    }

    public int Round { get; }

    /// <summary>When the turn began (live) or when its user message was stored (replay).</summary>
    internal DateTimeOffset StartedAt => startedAt;

    public ReadOnlyObservableCollection<TurnBlockViewModel> Blocks { get; }

    /// <summary>
    /// The card title, derived from the turn's state on every read: a language switch (or a tick, or new usage) only has to
    /// re-raise it, and the elapsed time re-formats in the current language.
    /// </summary>
    public string Header =>
        SessionStrings.TurnHeader(
            Round,
            startedAt,
            lastElapsed,
            isRunning,
            sawUsage ? inputTokens : null,
            sawUsage ? outputTokens : null);

    public string? Footnote => footnote.IsEmpty ? null : footnote.Resolve();

    public bool HasFootnote => !footnote.IsEmpty;

    /// <summary>Main-session turns are always expanded; a sub-agent collapses every turn but the newest.</summary>
    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (Set(ref isExpanded, value))
            {
                Owner?.ExpandedChanged(this);
            }
        }
    }

    /// <summary>The transcript whose flat row list shows this card; it keeps the card's block rows in step.</summary>
    internal TranscriptViewModel? Owner { get; set; }

    /// <summary>The row after the card's blocks that shows the footnote; created on first use.</summary>
    internal TurnFootnoteViewModel FootnoteRow => footnoteRow ??= new TurnFootnoteViewModel(this);

    /// <summary>The user message that opened this turn, set by the transcript when the turn begins.</summary>
    internal UserMessageViewModel? UserMessage { get; set; }

    /// <summary>
    /// How this turn ended. Live turns derive it from the footnote and the stop reason when they finish; replayed turns
    /// start as completed (a replayed finish carries no outcome) and are corrected from <c>turn_metrics</c>.
    /// </summary>
    internal TurnEndStatus EndStatus { get; private set; } = TurnEndStatus.Completed;

    /// <summary>The row after the footnote that closes the turn with its metrics; created on first use.</summary>
    internal TurnDividerViewModel DividerRow => dividerRow ??= new TurnDividerViewModel(this);

    /// <summary>The divider only exists once the turn is over; a running turn shows no metrics.</summary>
    internal bool HasDivider => !IsRunning;

    /// <summary>
    /// The card's own tool-call count (every start, never de-duplicated — the same口径 the metrics table records).
    /// A replay that recorded no call but has a metrics row falls back to that row's count.
    /// </summary>
    internal int ToolCount => toolCallsTotal > 0 ? toolCallsTotal : seededToolCalls ?? 0;

    /// <summary>The provider and model that ran the turn: the composer's choice stamped when the turn began, or what the
    /// metrics table recorded for a replayed one. Null when neither source knew.</summary>
    internal string? ProviderName => providerName;

    internal string? ModelName => modelName;

    /// <summary>Stamps the live turn's model on the card, at the turn's start, so the divider line and tooltip can show it.</summary>
    internal void NoteModel(string? provider, string? model)
    {
        if (string.IsNullOrEmpty(provider) && string.IsNullOrEmpty(model))
        {
            return;
        }

        providerName = provider;
        modelName = model;
        dividerRow?.RefreshText();
    }

    /// <summary>Adds what one <c>AgentTurnCompleted</c> streamed since the previous one (the event carries the instance's
    /// cumulative time) and lets the divider re-read its line: only this moves, so only this needs a refresh. No
    /// allocation, and a non-positive difference is ignored because a counter that went backwards streamed nothing.</summary>
    internal void NoteStream(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        StreamElapsed += elapsed;
        dividerRow?.RefreshText();
    }

    /// <summary>The model line the divider tooltip shows: the model with its provider, or unknown.</summary>
    internal string DividerModelText =>
        modelName is { Length: > 0 } model
            ? providerName is { Length: > 0 } provider ? $"{model} ({provider})" : model
            : TextFormat.None;

    /// <summary>The user-visible text of the footnote, for the divider's status line; null when the turn went through.</summary>
    internal string? ErrorSummary => footnote.IsEmpty ? seededError : footnote.Resolve();

    /// <summary>
    /// This turn's token snapshot, all five columns — or null when it must read as unknown: a turn that did not complete
    /// never shows a token (a failure's partial usage is not its usage), and neither does one the provider said nothing about.
    /// </summary>
    internal TokenUsage? ReportedUsage =>
        EndStatus == TurnEndStatus.Completed && sawUsage
            ? new TokenUsage(inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, cacheWrite1hTokens)
            : null;

    public RelayCommand ToggleCommand => new(() => IsExpanded = !IsExpanded);

    public bool IsRunning
    {
        get => isRunning;
        private set => Set(ref isRunning, value);
    }

    public bool HasUsage => sawUsage;

    public TokenUsage Usage => new(inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, cacheWrite1hTokens);

    /// <summary>
    /// Seeds what a stored conversation cannot reconstruct (wall clock, stream time, outcome, identity) from the turn's
    /// metrics row. Only fills what the replay did not already measure: a measured elapsed time and a counted tool call stay.
    /// The outcome always follows the table — a replayed finish carries no outcome of its own.
    /// </summary>
    internal void SeedReplayMetrics(TurnMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        var duration = metric.EndedAt - metric.StartedAt;
        if (lastElapsed is null && duration >= TimeSpan.Zero)
        {
            lastElapsed = duration;
            OnPropertyChanged(nameof(Elapsed));
        }

        if (toolCallsTotal == 0 && metric.ToolCalls is { } tools && tools >= 0)
        {
            seededToolCalls = tools;
        }

        // The stream time is a seed, not a difference: a replayed card measured none of its own, so the row's total is
        // assigned rather than added (a live card is never seeded — its time arrives through NoteStream). Assigning keeps
        // the seed idempotent if the history is loaded twice, like the other two seeds above. <c>stream_ms</c> is never
        // null, so this happens unconditionally: a replayed turn that streamed nothing reads zero, which the rate turns
        // into "—" instead of falling back to the wall clock.
        StreamElapsed = metric.StreamElapsed;

        EndStatus = metric.Status switch
        {
            TurnMetricStatus.Completed => TurnEndStatus.Completed,
            TurnMetricStatus.Stopped => TurnEndStatus.Stopped,
            TurnMetricStatus.Limit => TurnEndStatus.Limit,
            _ => TurnEndStatus.Failed,
        };
        providerName ??= metric.Provider;
        modelName ??= metric.Model;
        seededError ??= metric.Error;
        dividerRow?.RefreshText();
    }

    /// <summary>The divider tooltip, resolved when shown: the cost comes from the transcript's resolver, if it has one.</summary>
    internal string BuildDividerTooltip()
    {
        var usage = ReportedUsage;
        var cost = usage is { } reported ? Owner?.ResolveCost?.Invoke(reported, providerName, modelName) : null;
        return TurnDividerText.Tooltip(EndStatus, ErrorSummary, lastElapsed, StreamElapsed, ToolCount, usage, DividerModelText, cost);
    }

    private void AddBlock(TurnBlockViewModel block)
    {
        block.Turn = this;
        blocks.Add(block);
        Owner?.BlockAdded(this, block);
    }

    public void AppendText(string chunk)
    {
        SealThinking();
        if (openText is null)
        {
            openText = new TurnTextViewModel();
            AddBlock(openText);
        }

        openText.Append(chunk);
    }

    /// <summary>
    /// Merges one chunk of the model's reasoning into the open thinking row, opening one (after sealing the reply text) when
    /// this is a new stretch. Reasoning and reply alternate in arrival order, so both share <see cref="Blocks"/>
    /// rather than a list of their own.
    /// </summary>
    public void AppendThinking(string chunk)
    {
        if (openThinking is null)
        {
            SealText();
            openThinking = new TurnThinkingViewModel();
            AddBlock(openThinking);
        }

        openThinking.Append(chunk);
    }

    /// <summary>Adds a running tool call's row and hands it back: the session attaches the live state (wave W8).</summary>
    public ToolRowViewModel AddTool(ToolCall call)
    {
        SealText();
        SealThinking();
        var row = new ToolRowViewModel(call.Name, call.ArgumentsJson);
        toolRows[call.Id] = row;
        toolCallsTotal++;
        AddBlock(row);
        return row;
    }

    public void CompleteTool(ToolCall call, ToolResult result)
    {
        if (toolRows.Remove(call.Id, out var row))
        {
            row.Complete(result);
            return;
        }

        // A result whose call the card never opened (a replayed tool message without its call block): it still counts.
        toolCallsTotal++;
        var late = new ToolRowViewModel(call.Name, call.ArgumentsJson);
        late.Complete(result);
        AddBlock(late);
    }

    public void AddNotice(LocText text, bool isError = false, TimelineNodeKind kind = TimelineNodeKind.Notice)
    {
        SealText();
        SealThinking();
        AddBlock(new TurnNoticeViewModel(text, isError, kind));
    }

    /// <summary>
    /// Closes the open reply text before a tool row or a note takes over: a sealed block no longer streams, so it shows its
    /// copy button and its rendering stops being provisional.
    /// </summary>
    private void SealText()
    {
        openText?.Complete();
        openText = null;
    }

    /// <summary>Closes the open reasoning stretch once the answer starts, or once another block takes over.</summary>
    private void SealThinking()
    {
        openThinking?.Complete();
        openThinking = null;
    }

    public void AddUsage(TokenUsage usage, StopReason stopReason)
    {
        inputTokens += usage.InputTokens;
        outputTokens += usage.OutputTokens;
        cacheReadTokens += usage.CacheReadTokens;
        cacheWriteTokens += usage.CacheWriteTokens;
        cacheWrite1hTokens += usage.CacheWrite1hTokens;
        stop = stopReason;
        sawUsage = true;
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(HasUsage));

        // The usage can land after Finish (the Background flush may trail the turn's Normal-priority continuation), and
        // the divider must not keep showing "—" until some later refresh.
        dividerRow?.RefreshText();
    }

    /// <summary>Closes the turn; called on success, failure and cancellation alike.</summary>
    public void Finish(TimeSpan? elapsed, LocText? error)
    {
        openText?.Complete();
        openText = null;
        SealThinking();
        lastElapsed = elapsed;
        IsRunning = false;
        OnPropertyChanged(nameof(Header));
        footnote = error ?? default;
        EndStatus = footnote.IsEmpty
            ? stop == StopReason.MaxTokens ? TurnEndStatus.Limit : TurnEndStatus.Completed
            : footnote.Equals(SessionStrings.Stopped) ? TurnEndStatus.Stopped : TurnEndStatus.Failed;
        OnPropertyChanged(nameof(Footnote));
        OnPropertyChanged(nameof(HasFootnote));
        Owner?.FootnoteChanged(this);
        Owner?.DividerChanged(this);
        foreach (var row in toolRows.Values)
        {
            if (row.Status == "…")
            {
                row.Complete(SessionStrings.NoToolResult, isError: true);
            }
        }

        toolRows.Clear();
    }

    /// <summary>A running card re-reads its header once per tick; a finished one has nothing to update.</summary>
    public void Tick()
    {
        if (isRunning)
        {
            OnPropertyChanged(nameof(Header));
        }
    }

    /// <summary>Elapsed time the card shows, or null when it was replayed from the store and never measured.</summary>
    public TimeSpan? Elapsed => isRunning ? null : lastElapsed;

    public override void RefreshText()
    {
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(Footnote));
        OnPropertyChanged(nameof(HasFootnote));
        footnoteRow?.RefreshText();
        dividerRow?.RefreshText();
        foreach (var block in blocks)
        {
            block.RefreshText();
        }
    }
}

/// <summary>The closing row of a turn that failed or was stopped: its footnote, as a row of its own in the flat transcript.</summary>
internal sealed class TurnFootnoteViewModel(TurnCardViewModel card) : TranscriptItemViewModel
{
    public TurnCardViewModel Card { get; } = card;

    public string? Text => Card.Footnote;

    public override void RefreshText() => OnPropertyChanged(nameof(Text));
}

/// <summary>
/// The metrics line that closes every finished turn, on its own row after the footnote: a horizontal rule with the turn's
/// numbers in the middle. It survives a collapsed turn (it is not one of the card's blocks but the turn's closing row, like
/// the footnote), and it only ever exists for a finished turn — a running turn shows no metrics.
/// </summary>
internal sealed class TurnDividerViewModel(TurnCardViewModel card) : TranscriptItemViewModel
{
    public TurnCardViewModel Card { get; } = card;

    /// <summary>The line in the middle of the rule, derived on every read so a language switch re-renders it.</summary>
    public string Line => TurnDividerText.Line(
        Card.EndStatus,
        Card.Elapsed,
        Card.StreamElapsed,
        Card.ReportedUsage?.InputTokens,
        Card.ReportedUsage?.OutputTokens,
        Card.ToolCount,
        Card.ModelName);

    /// <summary>True for a turn that did not complete: the gutter node wears the failing state.</summary>
    public bool HasFailed => Card.EndStatus != TurnEndStatus.Completed;

    /// <summary>Everything the line leaves out: the caches, the hit rate, the cost, the model and the outcome.</summary>
    public string Tooltip => Card.BuildDividerTooltip();

    public override void RefreshText()
    {
        OnPropertyChanged(nameof(Line));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(Tooltip));
    }
}

/// <summary>
/// The round-end divider's texts as pure functions of the turn's numbers, so the rules (a rate only when both sides are
/// known, unknown tokens as "—" never zero, a failed turn's status line) are assertable without a view.
/// <para>The output rate divides by the model's streaming time, never by the turn's wall clock: the wall clock also counts
/// queueing, approvals, tool runs and git snapshots, which would make a fast answer look slow whenever a tool ran slow.
/// The wall clock still times the turn itself (the "耗时" segment).</para>
/// </summary>
internal static class TurnDividerText
{
    /// <summary>
    /// Output tokens over the turn's streaming seconds. Null unless both sides are known: an unknown output count has no
    /// rate, and a zero denominator would divide by zero — it reads "—" like any other unknown, and is deliberately not
    /// replaced by the wall clock (a missing stream time is an adapter defect, not something to paper over at display time).
    /// </summary>
    public static double? OutputRate(int? outputTokens, TimeSpan streamElapsed) =>
        outputTokens is { } output && streamElapsed > TimeSpan.Zero
            ? output / streamElapsed.TotalSeconds
            : null;

    /// <summary><c>54 tok/s</c>: whole numbers stay whole, small rates keep one decimal.</summary>
    public static string FormatRate(double rate) =>
        (rate >= 100
            ? rate.ToString("0", CultureInfo.InvariantCulture)
            : rate.ToString("0.#", CultureInfo.InvariantCulture)) + " tok/s";

    /// <summary>A cost that stays readable at both ends, the strip's own tiers: <c>$0.0041</c>, <c>$1.23</c>.</summary>
    public static string FormatCost(double value) => value switch
    {
        >= 10 => "$" + value.ToString("0.0", CultureInfo.InvariantCulture),
        >= 1 => "$" + value.ToString("0.00", CultureInfo.InvariantCulture),
        >= 0.01 => "$" + value.ToString("0.000", CultureInfo.InvariantCulture),
        _ => "$" + value.ToString("0.0000", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// The line in the middle of the rule. A completed turn shows all four segments (unknown sides read "—"); any other
    /// outcome shows its status with the tokens as unknown — a failure's partial usage is not its usage. The rate reads
    /// from <paramref name="streamElapsed"/> while <paramref name="elapsed"/> only times the turn.
    /// </summary>
    public static string Line(
        TurnEndStatus status,
        TimeSpan? elapsed,
        TimeSpan streamElapsed,
        int? inputTokens,
        int? outputTokens,
        int toolCalls,
        string? model)
    {
        var elapsedText = elapsed is { } wall ? TextFormat.Elapsed(wall) : TextFormat.None;
        var core = status != TurnEndStatus.Completed
            ? SessionStrings.TurnDividerFailed(
                SessionStrings.TurnStatusText(status),
                elapsedText,
                toolCalls)
            : SessionStrings.TurnDivider(
                elapsedText,
                TextFormat.Number(inputTokens),
                TextFormat.Number(outputTokens),
                toolCalls,
                OutputRate(outputTokens, streamElapsed) is { } known ? FormatRate(known) : TextFormat.None);

        // The model rides at the end of the line — an id, not localized text — and is omitted entirely when nobody knew it.
        return model is { Length: > 0 } name ? core + " · " + name : core;
    }

    /// <summary>
    /// The hover text: status and error, elapsed time and rate, all five token columns, the hit rate, the cost and the
    /// model. Every unknown reads "—"; the rate note (the denominator is the model's streaming time) rides along when a
    /// rate shows.
    /// </summary>
    public static string Tooltip(
        TurnEndStatus status,
        string? error,
        TimeSpan? elapsed,
        TimeSpan streamElapsed,
        int toolCalls,
        TokenUsage? usage,
        string modelText,
        string? costText)
    {
        var statusText = SessionStrings.TurnStatusText(status);
        var statusLine = SessionStrings.DividerTipStatus(
            string.IsNullOrEmpty(error) ? statusText : statusText + " · " + error);
        var rate = usage is { } known && status == TurnEndStatus.Completed
            ? OutputRate(known.OutputTokens, streamElapsed)
            : null;
        var timeLine = SessionStrings.DividerTipTime(
            elapsed is { } wall ? TextFormat.Elapsed(wall) : TextFormat.None,
            toolCalls,
            rate is { } perSecond ? FormatRate(perSecond) : TextFormat.None);
        var tokensLine = SessionStrings.DividerTipTokens(
            TextFormat.Number(usage?.InputTokens),
            TextFormat.Number(usage?.OutputTokens),
            TextFormat.Number(usage?.CacheReadTokens),
            TextFormat.Number(usage?.CacheWriteTokens),
            TextFormat.Number(usage?.CacheWrite1hTokens));
        var metaLine = SessionStrings.DividerTipMeta(
            usage is { } measured
                ? (SessionUsageMath.CacheHitRatio(measured) * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%"
                : TextFormat.None,
            string.IsNullOrEmpty(costText) ? TextFormat.None : costText,
            string.IsNullOrEmpty(modelText) ? TextFormat.None : modelText);
        return rate is null
            ? string.Join("\n", statusLine, timeLine, tokensLine, metaLine)
            : string.Join("\n", statusLine, timeLine, tokensLine, metaLine, SessionStrings.DividerRateNote);
    }
}

/// <summary>
/// The ordered transcript of one agent (the main session or one sub-agent). Streaming text is merged on the worker thread
/// into a pending buffer; the session's frame coalescer drains it on the UI thread.
/// </summary>
internal sealed class TranscriptViewModel : ObservableObject
{
    private readonly List<TranscriptItemViewModel> items = [];

    /// <summary>The list the view shows: one row per user message, notice, turn header, block and footnote, in order.</summary>
    private readonly ObservableCollection<object> rows = [];
    private readonly Lock pendingGate = new();
    private readonly StringBuilder pending = new();
    private readonly StringBuilder pendingThinking = new();
    private TurnCardViewModel? current;
    private int round;

    /// <summary>When the latest user message arrived, for the next message's gap segment; reset with the transcript.</summary>
    private DateTimeOffset? lastUserAt;

    /// <summary>The latest user message not yet claimed by a turn; the next <c>BeginTurn</c> anchors its blocks after it.</summary>
    private UserMessageViewModel? pendingUserMessage;

    public TranscriptViewModel(bool collapseOlderTurns)
    {
        CollapseOlderTurns = collapseOlderTurns;
        Rows = new ReadOnlyObservableCollection<object>(rows);
    }

    /// <summary>
    /// What the transcript list binds to. It is flat on purpose: a virtualizing list can only estimate the height of what it
    /// has not realized, and an estimate over whole turns (a turn is anywhere from one line to several screens) is far
    /// off, which makes the scroll bar jump when dragged. Rows of one block each keep the estimate close and the realized
    /// part small, however long the conversation is.
    /// </summary>
    public ReadOnlyObservableCollection<object> Rows { get; }

    public bool CollapseOlderTurns { get; }

    public bool IsEmpty => items.Count == 0;

    public TurnCardViewModel? Current => current;

    /// <summary>
    /// Formats a turn's cost for the divider tooltip, or null when it cannot be priced. The transcript owns no pricing:
    /// the session plugs its model-metadata lookup in when it opens the session, and a transcript without one shows "—".
    /// </summary>
    public Func<TokenUsage, string?, string?, string?>? ResolveCost { get; set; }

    public void AddUserMessage(string text, DateTimeOffset at, IReadOnlyList<ContentBlock>? attachments = null)
    {
        // Only main-session transcripts number rounds: a sub-agent transcript has no user-turn concept and its user
        // messages (a replayed stored task) stay exactly as before. The round number is peeked, not issued: the card
        // the next BeginTurn opens issues the same number, so the subtitle names the round the reply below belongs to.
        // Every user message is immediately followed by its turn's BeginTurn (live and replay alike); a stray BeginTurn
        // without a message simply claims nothing.
        UserMessageViewModel message;
        if (CollapseOlderTurns)
        {
            message = new UserMessageViewModel(text, at, attachments);
        }
        else
        {
            TimeSpan? gap = null;
            if (lastUserAt is { } previous)
            {
                if (at > previous)
                {
                    gap = at - previous;
                    lastUserAt = at;
                }

                // A rolled-back clock leaves both the gap and the watermark alone: advancing to the earlier time
                // would inflate the next message's interval.
            }
            else
            {
                lastUserAt = at;
            }

            message = new UserMessageViewModel(text, at, attachments, round + 1, gap);
            pendingUserMessage = message;
        }

        Add(message);
    }

    public void AddNotice(LocText text, bool isError = false, TimelineNodeKind kind = TimelineNodeKind.Notice) =>
        Add(new NoticeViewModel(text, isError, kind));

    /// <summary>
    /// Starts the next turn card and collapses the previous one when this transcript only expands the newest. A stored
    /// conversation is replayed with <paramref name="closePrevious"/>, because a replayed card has no live turn that
    /// would close it and the next user message always starts a new one.
    /// </summary>
    public TurnCardViewModel BeginTurn(DateTimeOffset at, bool closePrevious = false)
    {
        if (closePrevious)
        {
            current?.Finish(null, null);
        }

        if (current is { IsRunning: true })
        {
            return current;
        }

        round++;
        var card = new TurnCardViewModel(round, at);
        if (CollapseOlderTurns)
        {
            foreach (var item in items)
            {
                if (item is TurnCardViewModel previous)
                {
                    previous.IsExpanded = false;
                }
            }
        }

        current = card;
        card.Owner = this;
        if (pendingUserMessage is { } pending)
        {
            pendingUserMessage = null;
            card.UserMessage = pending;
        }

        Add(card);
        return card;
    }

    /// <summary>A block joined a card: it takes its place after the card's last block row, unless the card is collapsed.</summary>
    internal void BlockAdded(TurnCardViewModel card, TurnBlockViewModel block)
    {
        if (!card.IsExpanded)
        {
            return;
        }

        var at = EndOfBlocks(card);
        if (at >= 0)
        {
            rows.Insert(at, block);
        }
    }

    /// <summary>A card was expanded or collapsed: its block rows appear after its header or go away.</summary>
    internal void ExpandedChanged(TurnCardViewModel card)
    {
        var header = RowIndex(card);
        if (header < 0)
        {
            return;
        }

        if (card.IsExpanded)
        {
            var at = header + 1;
            foreach (var block in card.Blocks)
            {
                rows.Insert(at++, block);
            }
        }
        else
        {
            while (header + 1 < rows.Count && rows[header + 1] is TurnBlockViewModel block && ReferenceEquals(block.Turn, card))
            {
                rows.RemoveAt(header + 1);
            }
        }
    }

    /// <summary>A card gained or lost its footnote: the footnote row follows the card's block rows.</summary>
    internal void FootnoteChanged(TurnCardViewModel card)
    {
        var row = card.FootnoteRow;
        var at = RowIndex(row);
        if (card.HasFootnote)
        {
            if (at < 0 && EndOfBlocks(card) is var insertAt && insertAt >= 0)
            {
                rows.Insert(insertAt, row);
            }

            row.RefreshText();
        }
        else if (at >= 0)
        {
            rows.RemoveAt(at);
        }
    }

    /// <summary>
    /// A card finished: its divider row lands after the footnote (or after the blocks when there is none), just before
    /// the next user message. Re-entrant like the finish itself — a replayed turn finishes once per tool message.
    /// </summary>
    internal void DividerChanged(TurnCardViewModel card)
    {
        var row = card.DividerRow;
        var at = RowIndex(row);
        if (card.HasDivider)
        {
            if (at < 0)
            {
                var insert = card.HasFootnote && RowIndex(card.FootnoteRow) is var footnoteAt && footnoteAt >= 0
                    ? footnoteAt + 1
                    : EndOfBlocks(card);
                if (insert >= 0)
                {
                    rows.Insert(insert, row);
                }
            }

            row.RefreshText();
        }
        else if (at >= 0)
        {
            rows.RemoveAt(at);
        }
    }

    /// <summary>A stored row only meets a card whose start is this close to its own; see <see cref="SeedTurnMetrics"/>.</summary>
    private static readonly TimeSpan MetricsMatchWindow = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Seeds what the stored conversation cannot reconstruct (wall clock, stream time, tool count, outcome) from the metrics
    /// table: each stored row meets the card with the same round number whose start is within <see cref="MetricsMatchWindow"/>
    /// of the row's. A round the table never recorded or whose number does not line up with the replay's own count keeps its replayed
    /// values and reads "—" — that mismatch is a known limitation, not a lookup failure.
    /// </summary>
    public void SeedTurnMetrics(IReadOnlyList<TurnMetric> metrics)
    {
        if (metrics is null || metrics.Count == 0)
        {
            return;
        }

        var byTurn = new Dictionary<int, TurnMetric>(metrics.Count);
        foreach (var metric in metrics)
        {
            byTurn[metric.Turn] = metric;
        }

        foreach (var item in items)
        {
            // The stored number and the replay's own count can disagree (a turn whose user message was never persisted, a
            // session that numbers differently); a row whose start is nowhere near the card's belongs to another turn.
            if (item is TurnCardViewModel card && byTurn.TryGetValue(card.Round, out var metric)
                && (metric.StartedAt - card.StartedAt).Duration() <= MetricsMatchWindow)
            {
                card.SeedReplayMetrics(metric);
            }
        }
    }

    /// <summary>The index just past the card's header and block rows, or -1 when the card is not in the list.</summary>
    private int EndOfBlocks(TurnCardViewModel card)
    {
        var at = RowIndex(card);
        if (at >= 0)
        {
            at++;
            while (at < rows.Count && rows[at] is TurnBlockViewModel block && ReferenceEquals(block.Turn, card))
            {
                at++;
            }

            return at;
        }

        // A main-session turn has no header row: its blocks follow the user message directly. Find the end of the
        // blocks this card already has; with none yet, anchor after the user message that opened the turn.
        for (var index = rows.Count - 1; index >= 0; index--)
        {
            if (rows[index] is TurnBlockViewModel existing && ReferenceEquals(existing.Turn, card))
            {
                return index + 1;
            }
        }

        if (card.UserMessage is { } user)
        {
            var userAt = RowIndex(user);
            if (userAt >= 0)
            {
                return userAt + 1;
            }
        }

        return rows.Count;
    }

    /// <summary>Searches from the end: the rows being changed are almost always the newest.</summary>
    private int RowIndex(object row)
    {
        for (var index = rows.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(rows[index], row))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Merges a worker-thread delta into the pending buffer; the owner schedules the UI-thread flush.</summary>
    public void QueueDelta(string chunk)
    {
        lock (pendingGate)
        {
            pending.Append(chunk);
        }
    }

    /// <summary>Merges worker-thread reasoning into its own pending buffer, flushed by the same frame as the reply text.</summary>
    public void QueueThinkingDelta(string chunk)
    {
        lock (pendingGate)
        {
            pendingThinking.Append(chunk);
        }
    }

    /// <summary>
    /// Drains both pending buffers in arrival order: reasoning first, because a provider that reasons always sends the
    /// thinking of one request before the answer of that request, and both land in the same card in the order they came.
    /// </summary>
    public void FlushPending()
    {
        string thinking;
        string chunk;
        lock (pendingGate)
        {
            thinking = pendingThinking.ToString();
            pendingThinking.Clear();
            chunk = pending.ToString();
            pending.Clear();
        }

        if (thinking.Length > 0)
        {
            (current ?? BeginTurn(DateTimeOffset.Now)).AppendThinking(thinking);
        }

        if (chunk.Length > 0)
        {
            (current ?? BeginTurn(DateTimeOffset.Now)).AppendText(chunk);
        }
    }

    /// <summary>Called when the event stream ends, on success, failure or cancellation.</summary>
    public void EndTurn(TimeSpan? elapsed, LocText? error)
    {
        FlushPending();
        current?.Finish(elapsed, error);
    }

    public void Tick()
    {
        current?.Tick();
    }

    /// <summary>Re-raises every item's text after a language switch; the items derive their texts from their state.</summary>
    public void RefreshText()
    {
        foreach (var item in items)
        {
            item.RefreshText();
        }
    }

    public void Clear()
    {
        items.Clear();
        rows.Clear();
        current = null;
        round = 0;
        lastUserAt = null;
        pendingUserMessage = null;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Add(TranscriptItemViewModel item)
    {
        items.Add(item);
        if (item is TurnCardViewModel && !CollapseOlderTurns)
        {
            // A main-session turn renders no header row: the blocks follow the user message directly and the card
            // itself stays out of the flat list (a sub-agent transcript keeps its collapsible header). The card still
            // lives in items, so its blocks, footnote and divider anchor off it exactly as before.
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        rows.Add(item);
        OnPropertyChanged(nameof(IsEmpty));
    }
}
