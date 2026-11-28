using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Forge.Abstractions;
using Forge.App.Changes.Markdown;
using Forge.App.Localization;
using Forge.App.Providers;
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
/// One round of a transcript: an independent container that owns every row of that round — the user message, the turn's
/// blocks, its footnote and the round-end row. Rounds are the list's items, so rows of different rounds are never siblings:
/// hovering, selecting, text selection and the timeline axis all stay inside one round.
/// <para>The axis is continuous inside a round because the rows of one round are the items of one <c>ItemsControl</c>: a row
/// only has to answer "am I the first/last of my round", which no other round's items can change.</para>
/// </summary>
internal sealed class TranscriptRoundViewModel : TranscriptItemViewModel
{
    private readonly ObservableCollection<object> rows = [];

    public TranscriptRoundViewModel() => Rows = new ReadOnlyObservableCollection<object>(rows);

    /// <summary>The rows of this round, in order; the round template binds a plain item host to it.</summary>
    public ReadOnlyObservableCollection<object> Rows { get; }

    /// <summary>The turn this round belongs to, or null for a round that holds only a note.</summary>
    public TurnCardViewModel? Card { get; internal set; }

    /// <summary>The mutable view the transcript edits; only the transcript model touches it.</summary>
    internal ObservableCollection<object> RowList => rows;

    public override void RefreshText()
    {
        foreach (var row in rows)
        {
            switch (row)
            {
                case TranscriptItemViewModel item:
                    item.RefreshText();
                    break;
                case TurnBlockViewModel block:
                    block.RefreshText();
                    break;
            }
        }
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
        At = at;
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

    /// <summary>When the message was written: the history flyout reads it for the row's relative age.</summary>
    public DateTimeOffset At { get; }

    /// <summary>
    /// The round this message starts, peeked from the transcript's counter when the message arrived. The card the next
    /// <c>BeginTurn</c> opens carries the same number, so the subtitle names the round the reply below belongs to.
    /// Null for a transcript that never numbers rounds (sub-agent transcripts have no user turns). A replay moves the whole
    /// page by one amount when its metrics rows agree on where the page began (see <see cref="Renumber"/> and
    /// <c>TranscriptViewModel.ShiftRoundNumbers</c>); it never renumbers a round on its own.
    /// </summary>
    public int? Round { get; private set; }

    /// <summary>Moves this message's round by the page's one shift; a page's rounds keep their order and spacing.</summary>
    internal void Renumber(int round)
    {
        if (Round == round)
        {
            return;
        }

        Round = round;
        OnPropertyChanged(nameof(RoundText));
        OnPropertyChanged(nameof(MetaText));
    }

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

/// <summary>
/// A run of consecutive tool calls inside one turn: the cards are laid out in a grid of at most four columns, and at most one
/// of them is open at a time — opening one shows its full body (arguments, result, live tail) under the grid, reusing the
/// row's own expanded content.
/// <para>A group exists so the transcript can show several calls side by side instead of one row each; the timeline still
/// gets one node for the run, on the group's row.</para>
/// </summary>
internal sealed class TurnToolGroupViewModel : TurnBlockViewModel
{
    private readonly ObservableCollection<ToolRowViewModel> tools = [];
    private readonly ObservableCollection<object> items = [];
    private ToolRowViewModel? open;

    public TurnToolGroupViewModel()
    {
        Tools = new ReadOnlyObservableCollection<ToolRowViewModel>(tools);
        Items = new ReadOnlyObservableCollection<object>(items);
    }

    public ReadOnlyObservableCollection<ToolRowViewModel> Tools { get; }

    /// <summary>
    /// What the grid draws, in order: the cards, and — right after the row that holds it — the open call's body. One flat
    /// sequence with one item host: the panel below gives the body the full width, so no second list is nested in the grid.
    /// </summary>
    public ReadOnlyObservableCollection<object> Items { get; }

    /// <summary>The one call whose body is open, or null; the grid always shows every card.</summary>
    public ToolRowViewModel? Open => open;

    public bool HasOpen => open is not null;

    /// <summary>Whether any call in the run is still running; the group's node on the axis pulses while one is. A call waiting
    /// for the user's approval counts as live: the run has not ended, only its spinner would be a lie.</summary>
    public bool IsRunning
    {
        get
        {
            foreach (var tool in tools)
            {
                if (tool.IsRunning || tool.IsAwaitingApproval)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Whether any call in the run failed; the group's node then wears the failure colour.</summary>
    public bool IsError
    {
        get
        {
            foreach (var tool in tools)
            {
                if (tool.IsError)
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal void Add(ToolRowViewModel tool)
    {
        tool.PropertyChanged += OnToolChanged;
        tools.Add(tool);
        Rebuild();
    }

    /// <summary>Rebuilds the flat item sequence after the run grows or a card opens or closes.</summary>
    private void Rebuild()
    {
        items.Clear();
        for (var i = 0; i < tools.Count; i += 4)
        {
            var end = Math.Min(i + 4, tools.Count);
            ToolRowViewModel? openInRow = null;
            for (var j = i; j < end; j++)
            {
                items.Add(tools[j]);
                if (tools[j].IsExpanded)
                {
                    openInRow = tools[j];
                }
            }

            if (openInRow is not null)
            {
                items.Add(new ToolOpenViewModel(openInRow));
            }
        }
    }

    private void OnToolStateChanged()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsError));
    }

    private void OnToolChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ToolRowViewModel.IsRunning) or nameof(ToolRowViewModel.IsAwaitingApproval) or nameof(ToolRowViewModel.IsError))
        {
            OnToolStateChanged();
            return;
        }

        if (e.PropertyName != nameof(ToolRowViewModel.IsExpanded))
        {
            return;
        }

        var tool = (ToolRowViewModel)sender!;
        if (tool.IsExpanded)
        {
            open = tool;
            foreach (var other in tools)
            {
                if (!ReferenceEquals(other, tool))
                {
                    other.IsExpanded = false;
                }
            }
        }
        else if (ReferenceEquals(open, tool))
        {
            open = null;
        }

        OnPropertyChanged(nameof(Open));
        OnPropertyChanged(nameof(HasOpen));
        Rebuild();
    }
}

/// <summary>The open call's body inside a tool card grid: it takes the whole width under the row it was clicked in.</summary>
internal sealed class ToolOpenViewModel(ToolRowViewModel tool)
{
    public ToolRowViewModel Tool { get; } = tool;
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

    /// <summary>
    /// The reasoning as it arrived, with runs of blank lines collapsed to one: providers separate paragraphs with one or two
    /// empty lines, and three or more read as a hole in the block rather than as spacing.
    /// </summary>
    public string Text => materialized ??= Collapse(text.ToString());

    private static string Collapse(string raw)
    {
        if (raw.IndexOf("\n\n", StringComparison.Ordinal) < 0)
        {
            return raw;
        }

        // One line break between paragraphs: the text block's own line height (1.6x the font size) is the spacing, so a
        // literal blank line would double it and read as a hole in the block.
        var builder = new StringBuilder(raw.Length);
        var newlines = 0;
        foreach (var character in raw)
        {
            if (character == '\n')
            {
                if (++newlines <= 1)
                {
                    builder.Append(character);
                }

                continue;
            }

            newlines = 0;
            builder.Append(character);
        }

        return builder.ToString();
    }

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
internal sealed class TurnNoticeViewModel(
    LocText text,
    bool isError,
    TimelineNodeKind kind = TimelineNodeKind.Notice,
    bool fromUser = false)
    : TurnBlockViewModel
{
    /// <summary>The note in the current language, resolved when the note is rendered.</summary>
    public string Text => text.Resolve();

    public bool IsError { get; } = isError;

    /// <summary>Which timeline node the note wears; a denied approval is <see cref="TimelineNodeKind.Approval"/>.</summary>
    public TimelineNodeKind Kind { get; } = kind;

    /// <summary>
    /// The note carries the user's own words (a delivered interjection): it reads at body size in the primary colour, like the
    /// message bubble it stands for, instead of the muted caption every other note uses.
    /// </summary>
    public bool FromUser { get; } = fromUser;

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
    private TurnToolGroupViewModel? openToolGroup;
    private TurnFootnoteViewModel? footnoteRow;
    private TurnDividerViewModel? dividerRow;
    private TurnWorkingViewModel? workingRow;
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

    /// <summary>The reasoning step the turn ran with: stamped at the turn's start, or seeded from its metrics row.</summary>
    private ThinkingEffort? effort;

    /// <summary>The failure text the metrics table recorded; the tooltip shows it when the turn left no footnote.</summary>
    private string? seededError;

    public TurnCardViewModel(int round, DateTimeOffset startedAt)
    {
        Round = round;
        this.startedAt = startedAt;
        Blocks = new ReadOnlyObservableCollection<TurnBlockViewModel>(blocks);
    }

    public int Round { get; internal set; }

    /// <summary>When the turn began (live) or when its user message was stored (replay).</summary>
    internal DateTimeOffset StartedAt => startedAt;

    public ReadOnlyObservableCollection<TurnBlockViewModel> Blocks { get; }

    public string? Footnote => footnote.IsEmpty ? null : footnote.Resolve();

    public bool HasFootnote => !footnote.IsEmpty;

    /// <summary>Main-session turns are always expanded; a sub-agent folds every turn but the newest, from its round-end row.</summary>
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

    /// <summary>The transcript whose round list shows this card; it keeps the card's block rows in step.</summary>
    internal TranscriptViewModel? Owner { get; set; }

    /// <summary>The round container that hosts this turn's rows, set by the transcript when the turn begins.</summary>
    internal TranscriptRoundViewModel? Host { get; set; }

    /// <summary>The row after the card's blocks that shows the footnote; created on first use.</summary>
    internal TurnFootnoteViewModel FootnoteRow => footnoteRow ??= new TurnFootnoteViewModel(this);

    /// <summary>The "what happens next" row of a running turn; created on first use.</summary>
    internal TurnWorkingViewModel WorkingRow => workingRow ??= new TurnWorkingViewModel(this);

    /// <summary>
    /// Whether the turn is running but has nothing in flight to show. The working row speaks then: it is the difference
    /// between "the agent is thinking" and "the case is closed", which an empty transcript would otherwise leave to the clock.
    /// </summary>
    public bool IsWorking => isRunning && !IsProducing;

    /// <summary>Something is streaming or a tool is running: the turn's own content already says what it is doing.</summary>
    private bool IsProducing =>
        openText is { IsStreaming: true }
        || openThinking is { IsStreaming: true }
        || blocks.OfType<TurnToolGroupViewModel>().Any(group => group.IsRunning);

    private void NotifyWorking() => Owner?.WorkingChanged(this);

    /// <summary>The user message that opened this turn, set by the transcript when the turn begins.</summary>
    internal UserMessageViewModel? UserMessage { get; set; }

    /// <summary>
    /// What the user sent to open this turn (text and attachments), kept so a failed turn can be replayed unchanged.
    /// Set by the session that starts the turn; a replayed or sub-agent turn has none and is never offered a retry.
    /// </summary>
    internal ComposerPrompt? Prompt { get; set; }

    /// <summary>
    /// Whether this turn may be replayed as-is: it failed and ran no tool call, so replaying it cannot repeat a side effect.
    /// A turn that already ran one is never offered — `bash`, `write` or a `git push` would happen twice — and a stopped or
    /// token-limited turn is no failure to retry. Reply text or reasoning already streamed does not block it: a stream that
    /// dies mid-answer was never persisted, which is exactly the shape worth re-sending. The live half (is the session
    /// writable and quiet, is a resume already waiting) belongs to the owning session and is asked through
    /// <see cref="TranscriptViewModel.CanRetry"/>.
    /// </summary>
    internal bool CanRetry =>
        Prompt is not null &&
        EndStatus == TurnEndStatus.Failed &&
        toolCallsTotal == 0 &&
        Owner?.CanRetry(this) == true;

    /// <summary>Replays this turn through the owning session's own turn entry point; see <see cref="TranscriptViewModel.RetryTurn"/>.</summary>
    internal Task RetryAsync() => Owner?.RetryAsync(this) ?? Task.CompletedTask;

    /// <summary>
    /// Whether this turn may be <em>continued</em> instead of replayed: it failed after it had already run a tool, so replaying
    /// the prompt would repeat side effects while leaving the work undone is the only alternative. It sends a continue
    /// instruction into the same session — history, tool results and all — through the ordinary turn path, which is what a user
    /// does by hand; <see cref="CanRetry"/> stays the cheaper replay for the shape where nothing happened yet, and the two are
    /// never offered together. The live half is the session's, asked through <see cref="TranscriptViewModel.CanResume"/>.
    /// </summary>
    internal bool CanResume =>
        Prompt is not null &&
        EndStatus == TurnEndStatus.Failed &&
        toolCallsTotal > 0 &&
        Owner?.CanResume(this) == true;

    /// <summary>Continues this turn through the owning session's own turn entry point; see <see cref="TranscriptViewModel.ResumeTurn"/>.</summary>
    internal Task ResumeAsync() => Owner?.ResumeAsync(this) ?? Task.CompletedTask;

    /// <summary>Lets the live half of <see cref="CanRetry"/> and <see cref="CanResume"/> change without a new footnote.</summary>
    internal void RefreshRetry() => footnoteRow?.RefreshRetry();

    /// <summary>
    /// How this turn ended. Live turns derive it from the footnote and the stop reason when they finish; replayed turns
    /// start as completed (a replayed finish carries no outcome) and are corrected from <c>turn_metrics</c>.
    /// </summary>
    internal TurnEndStatus EndStatus { get; private set; } = TurnEndStatus.Completed;

    /// <summary>The row after the footnote that closes the turn with its metrics; created on first use.</summary>
    internal TurnDividerViewModel DividerRow => dividerRow ??= new TurnDividerViewModel(this);

    /// <summary>Lets an already-built divider row re-read the fold state, without building one that does not exist yet.</summary>
    internal void NotifyExpanded() => dividerRow?.RefreshExpanded();

    /// <summary>The divider only exists once the turn is over; a running turn shows no metrics.</summary>
    internal bool HasDivider => !IsRunning;

    /// <summary>
    /// The card's own tool-call count (every start, never de-duplicated — the same口径 the metrics table records).
    /// A replay that recorded no call but has a metrics row falls back to that row's count.
    /// </summary>
    internal int ToolCount => toolCallsTotal > 0 ? toolCallsTotal : seededToolCalls ?? 0;

    /// <summary>The provider and model that ran the turn: the composer's choice stamped when the turn began, or what the
    /// metrics table recorded for a replayed one. Null when neither source knew.</summary>
    internal string? ModelName => modelName;

    /// <summary>The provider half of the same reference; the sub-agent's replayed instruments read both back from the
    /// turn's stored metrics row.</summary>
    internal string? ProviderName => providerName;

    /// <summary>The reasoning step the turn ran with; null for a turn nobody recorded one for.</summary>
    internal ThinkingEffort? Effort => effort;

    /// <summary>
    /// The turn's model in the standard form every divider uses — <c>provider/model:effort</c>, the provider and the step
    /// omitted when they are unknown — and null when nothing named the model at all.
    /// </summary>
    internal string? ModelLabel => modelName is { Length: > 0 }
        ? TurnDividerText.ModelLabel(providerName, modelName, effort)
        : null;

    /// <summary>Stamps the live turn's model on the card, at the turn's start, so the divider line and tooltip can show it.</summary>
    internal void NoteModel(string? provider, string? model, ThinkingEffort? level = null)
    {
        if (string.IsNullOrEmpty(provider) && string.IsNullOrEmpty(model))
        {
            return;
        }

        providerName = provider;
        modelName = model;
        effort = level;
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

    /// <summary>The model reference the tooltip's last line shows: the standard label, or "—" when nothing knew the model.</summary>
    internal string DividerModelText => ModelLabel ?? TextFormat.None;

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
    /// <para>The row never renumbers the card it is paired with: a page's round numbers are one sequence, and taking a row's
    /// number card by card is what made two neighbouring rounds read the same "第 N 轮" when the table skipped a turn or
    /// repeated one. What the rows correct is where the page's sequence starts, once for the whole page (see
    /// <see cref="TranscriptViewModel.ShiftRoundNumbers"/>).</para>
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
        effort ??= metric.Effort;
        seededError ??= metric.Error;
        dividerRow?.RefreshText();
    }

    /// <summary>
    /// The five lines the row hides behind its hover: the outcome with the wall clock, the tokens with the cost, the caches
    /// with the hit rate, and the model with its provider — everything the line itself leaves out, kept off the row so the
    /// round closes on one wavy rule instead of a second printed line that a narrow window would truncate.
    /// </summary>
    internal string BuildDividerTooltip()
    {
        var usage = ReportedUsage;
        var cost = usage is { } priced ? Owner?.ResolveCost?.Invoke(priced, providerName, modelName) : null;
        var statusText = SessionStrings.TurnStatusText(EndStatus);
        return string.Join(
            "\n",
            SessionStrings.DividerTipState(
                string.IsNullOrEmpty(ErrorSummary) ? statusText : statusText + " · " + ErrorSummary,
                Elapsed is { } wall ? TextFormat.Elapsed(wall) : TextFormat.None),
            SessionStrings.DividerTipUsage(
                TextFormat.Number(usage?.InputTokens),
                TextFormat.Number(usage?.OutputTokens),
                string.IsNullOrEmpty(cost) ? TextFormat.None : cost),
            SessionStrings.DividerTipCacheRead(
                TextFormat.Number(usage?.CacheReadTokens),
                usage is { } measured
                    ? (SessionUsageMath.CacheHitRatio(measured) * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%"
                    : TextFormat.None),
            SessionStrings.DividerTipCacheWrite(
                TextFormat.Number(usage?.CacheWriteTokens),
                TextFormat.Number(usage?.CacheWrite1hTokens)),
            SessionStrings.DividerTipModel(DividerModelText));
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
        SealToolGroup();
        if (openText is null)
        {
            openText = new TurnTextViewModel();
            AddBlock(openText);
        }

        openText.Append(chunk);
        NotifyWorking();
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
            SealToolGroup();
            openThinking = new TurnThinkingViewModel();
            AddBlock(openThinking);
        }

        openThinking.Append(chunk);
        NotifyWorking();
    }

    /// <summary>Adds a running tool call's row and hands it back: the session attaches the live state (wave W8).</summary>
    public ToolRowViewModel AddTool(ToolCall call)
    {
        SealText();
        SealThinking();
        var row = new ToolRowViewModel(call.Name, call.ArgumentsJson);
        toolRows[call.Id] = row;
        toolCallsTotal++;

        // Consecutive calls share one group (and one grid) until something else takes the line; a call that arrives after a
        // reply or a note starts a new group.
        if (openToolGroup is null)
        {
            openToolGroup = new TurnToolGroupViewModel();
            AddBlock(openToolGroup);
        }

        openToolGroup.Add(row);
        if (!IsRunning)
        {
            // The turn was already closed when this call arrived: a start event can trail the end of its own turn (the coalescer
            // runs at Background, the turn's continuation does not), and nothing else would ever close the row - so it is closed
            // the same way Finish closes the rows it finds.
            row.Complete(SessionStrings.NoToolResult, isError: true);
        }

        NotifyWorking();
        return row;
    }

    public void CompleteTool(ToolCall call, ToolResult result)
    {
        if (toolRows.Remove(call.Id, out var row))
        {
            row.Complete(result);
            NotifyWorking();
            return;
        }

        // A result whose call the card never opened (a replayed tool message without its call block): it still counts.
        toolCallsTotal++;
        var late = new ToolRowViewModel(call.Name, call.ArgumentsJson);
        late.Complete(result);
        AddBlock(late);
    }

    /// <summary>The row a call still owns on this card, or null: a call that was waiting on the user is handed the same row.</summary>
    internal ToolRowViewModel? RowOf(string callId) =>
        toolRows.TryGetValue(callId, out var row) ? row : null;

    /// <summary>
    /// Closes a call the user refused on its own card: a replayed transcript shows the denial as that call's ✗ with the reason,
    /// so the live one does too instead of adding a second line about the same call. A refusal whose call this card never
    /// opened (a replayed transcript, a withdrawn turn) still gets the notice.
    /// </summary>
    public void DenyTool(ToolCall call, LocText reason)
    {
        if (toolRows.Remove(call.Id, out var row))
        {
            row.Complete(reason, isError: true);
            NotifyWorking();
            return;
        }

        AddNotice(reason, isError: true, kind: TimelineNodeKind.Approval);
    }

    public void AddNotice(
        LocText text,
        bool isError = false,
        TimelineNodeKind kind = TimelineNodeKind.Notice,
        bool fromUser = false)
    {
        SealText();
        SealThinking();
        SealToolGroup();
        AddBlock(new TurnNoticeViewModel(text, isError, kind, fromUser));
    }

    /// <summary>
    /// Closes the open reply text before a tool row or a note takes over: a sealed block no longer streams, so it shows its
    /// copy button and its rendering stops being provisional.
    /// </summary>
    private void SealToolGroup() => openToolGroup = null;

    private void SealText()
    {
        openText?.Complete();
        openText = null;
        NotifyWorking();
    }

    /// <summary>Closes the open reasoning stretch once the answer starts, or once another block takes over.</summary>
    private void SealThinking()
    {
        openThinking?.Complete();
        openThinking = null;
        NotifyWorking();
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
        footnote = error ?? default;
        EndStatus = footnote.IsEmpty
            ? stop == StopReason.MaxTokens ? TurnEndStatus.Limit : TurnEndStatus.Completed
            : footnote.Equals(SessionStrings.Stopped) ? TurnEndStatus.Stopped : TurnEndStatus.Failed;
        OnPropertyChanged(nameof(Footnote));
        OnPropertyChanged(nameof(HasFootnote));
        Owner?.FootnoteChanged(this);
        Owner?.DividerChanged(this);
        RefreshRetry();
        NotifyWorking();
        foreach (var row in toolRows.Values)
        {
            // A call still waiting for the user has no result either: the turn it belonged to is over, so it is closed with
            // the same "no result" the running ones get rather than keeping a waiting mark on a finished turn.
            if (row.IsRunning || row.IsAwaitingApproval)
            {
                row.Complete(SessionStrings.NoToolResult, isError: true);
            }
        }

        toolRows.Clear();
    }

    /// <summary>Elapsed time the card shows, or null when it was replayed from the store and never measured.</summary>
    public TimeSpan? Elapsed => isRunning ? null : lastElapsed;

    public override void RefreshText()
    {
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
/// <summary>
/// The transient row of a running turn that has nothing else to show: what the agent is about to do. It lives while
/// <see cref="TurnCardViewModel.IsWorking"/> is true, and leaves the transcript the moment output, reasoning or a tool call
/// takes over, or the turn ends.
/// </summary>
internal sealed class TurnWorkingViewModel(TurnCardViewModel card) : TranscriptItemViewModel
{
    public TurnCardViewModel Card { get; } = card;

    public string Text => SessionStrings.TurnWorking;

    public override void RefreshText() => OnPropertyChanged(nameof(Text));
}

internal sealed class TurnFootnoteViewModel(TurnCardViewModel card) : TranscriptItemViewModel
{
    private AsyncRelayCommand? retryCommand;
    private AsyncRelayCommand? resumeCommand;

    public TurnCardViewModel Card { get; } = card;

    public string? Text => Card.Footnote;

    /// <summary>Whether the footnote shows the retry button beside its text.</summary>
    public bool CanRetry => Card.CanRetry;

    /// <summary>Whether it shows the continue button instead: the turn failed after running a tool, so a replay is off the table.</summary>
    public bool CanResume => Card.CanResume;

    /// <summary>
    /// Re-runs the failed turn. The command disables itself while it runs, and the session's runner reports its own
    /// failures (the turn is exactly the place a failure is already shown), so nothing escapes to the dispatcher.
    /// </summary>
    public AsyncRelayCommand RetryCommand =>
        retryCommand ??= new AsyncRelayCommand(_ => Card.RetryAsync(), () => Card.CanRetry);

    /// <summary>Continues the failed turn from where it stopped; same failure handling as <see cref="RetryCommand"/>.</summary>
    public AsyncRelayCommand ResumeCommand =>
        resumeCommand ??= new AsyncRelayCommand(_ => Card.ResumeAsync(), () => Card.CanResume);

    internal void RefreshRetry()
    {
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanResume));
        retryCommand?.NotifyCanExecuteChanged();
        resumeCommand?.NotifyCanExecuteChanged();
    }

    public override void RefreshText()
    {
        OnPropertyChanged(nameof(Text));
        RefreshRetry();
    }
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
        Card.ModelLabel);

    /// <summary>True for a turn that did not complete: the gutter node wears the failing state.</summary>
    public bool HasFailed => Card.EndStatus != TurnEndStatus.Completed;

    /// <summary>Everything the row leaves out, shown on hover: the outcome and the full token, cache, cost and model breakdown.</summary>
    public string Tooltip => Card.BuildDividerTooltip();

    /// <summary>
    /// The row is also the turn's fold handle, but only where a transcript folds older turns (a sub-agent's); a main-session
    /// turn is always whole and draws no chevron.
    /// </summary>
    public bool CanCollapse => Card.Owner?.CollapseOlderTurns == true;

    /// <summary>Whether the turn's blocks are in the list; the chevron turns with it.</summary>
    public bool IsExpanded => Card.IsExpanded;

    /// <summary>The chevron's screen-reader name, which is also the only place the action is spelled out.</summary>
    public string ToggleName => Card.IsExpanded ? SessionStrings.CollapseTurn : SessionStrings.ExpandTurn;

    /// <summary>Folds or unfolds the turn this rule closes; its blocks come and go above it.</summary>
    public RelayCommand ToggleCommand => new(() => Card.IsExpanded = !Card.IsExpanded);

    internal void RefreshExpanded()
    {
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(ToggleName));
    }

    public override void RefreshText()
    {
        OnPropertyChanged(nameof(Line));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(CanCollapse));
        RefreshExpanded();
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
    /// The turn's model in the standard form the divider line and its tooltip both print:
    /// <c>provider/model:effort</c>. The provider and the step are each omitted when that half is unknown, so a row from before
    /// the step was recorded reads as the plain reference it always did; an unknown model reads <c>—</c>, never a made-up
    /// combination.
    /// </summary>
    public static string ModelLabel(string? provider, string? model, ThinkingEffort? effort)
    {
        if (model is not { Length: > 0 } name)
        {
            return TextFormat.None;
        }

        var reference = provider is { Length: > 0 } source ? source + "/" + name : name;
        return effort is { } step ? reference + ":" + ThinkingEfforts.Name(step) : reference;
    }

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

}

/// <summary>
/// A transcript's rounds, as a list binds them, plus the transcript they belong to. The scroll behaviour reads the owner off
/// the collection it is already listening to, so the two can never be out of step: a list showing these rounds follows this
/// transcript's own signals without anyone handing it the transcript, and a tab that swaps its transcript swaps the signals
/// with the items.
/// </summary>
internal sealed class TranscriptRoundCollection(TranscriptViewModel owner, ObservableCollection<TranscriptRoundViewModel> rounds)
    : ReadOnlyObservableCollection<TranscriptRoundViewModel>(rounds)
{
    public TranscriptViewModel Owner { get; } = owner;
}

/// <summary>
/// The ordered transcript of one agent (the main session or one sub-agent). Streaming text is merged on the worker thread
/// into a pending buffer; the session's frame coalescer drains it on the UI thread.
/// </summary>
internal sealed class TranscriptViewModel : ObservableObject
{
    private readonly List<TranscriptItemViewModel> items = [];

    /// <summary>The list the view shows: one round per user message (or per turn, for a sub-agent), in order.</summary>
    private readonly ObservableCollection<TranscriptRoundViewModel> rounds = [];
    private readonly Lock pendingGate = new();
    private readonly StringBuilder pending = new();
    private readonly StringBuilder pendingThinking = new();
    private TurnCardViewModel? current;
    private int round;

    /// <summary>The number the last <see cref="SeedRoundCounter"/> set, before any round of the page opened.</summary>
    private int roundSeed;

    /// <summary>The round the last user message opened and the next <c>BeginTurn</c> claims; null once claimed.</summary>
    private TranscriptRoundViewModel? pendingRound;

    /// <summary>When the latest user message arrived, for the next message's gap segment; reset with the transcript.</summary>
    private DateTimeOffset? lastUserAt;

    /// <summary>The latest user message not yet claimed by a turn; the next <c>BeginTurn</c> anchors its blocks after it.</summary>
    private UserMessageViewModel? pendingUserMessage;

    /// <summary>
    /// Numbers the rounds that follow this call. A transcript read backwards from the end of a session starts part-way in: the
    /// page's first round is number <paramref name="playedRounds"/> + 1, so "第 N 轮" stays the session's own number instead of
    /// restarting at 1 on every restore. The seed is kept, because a page's metrics rows can move the whole sequence (see
    /// <see cref="ShiftRoundNumbers"/>).
    /// </summary>
    internal void SeedRoundCounter(long playedRounds)
    {
        round = (int)Math.Clamp(playedRounds, 0, int.MaxValue);
        roundSeed = round;
    }

    /// <summary>The number the page was seeded with: what a replay compares a metrics row's turn number against.</summary>
    internal int RoundSeed => roundSeed;

    /// <summary>
    /// Moves every round of the page by one amount, and the counter that continues after it with them: the replay's own
    /// numbering is one ordered sequence, so a page either starts where its metrics rows say all of its rounds began or
    /// keeps the count - never a number per card.
    /// </summary>
    internal void ShiftRoundNumbers(int shift)
    {
        if (shift == 0)
        {
            return;
        }

        round += shift;
        foreach (var host in rounds)
        {
            if (host.Card is not { } card)
            {
                continue;
            }

            card.Round += shift;
            card.UserMessage?.Renumber(card.Round);
        }
    }

    /// <summary>
    /// Puts an older page in front of what is already here. The page comes from a transcript of its own (numbered by
    /// <see cref="SeedRoundCounter"/>), so its rounds keep the numbers they were built with and nothing already read is renumbered.
    /// </summary>
    /// <summary>
    /// Raised after an older page landed in front of the transcript. A prepend is a collection of rounds whose first row is
    /// a user message - the same shape as the user sending one - so the scroll behaviour must be told which it was instead
    /// of guessing from the items.
    /// </summary>
    public event EventHandler? Prepended;

    internal void PrependRounds(TranscriptViewModel older)
    {
        ArgumentNullException.ThrowIfNull(older);
        for (var i = 0; i < older.rounds.Count; i++)
        {
            rounds.Insert(i, older.rounds[i]);
        }

        for (var i = 0; i < older.items.Count; i++)
        {
            items.Insert(i, older.items[i]);
        }

        OnPropertyChanged(nameof(IsEmpty));
        Prepended?.Invoke(this, EventArgs.Empty);
    }

    public TranscriptViewModel(bool collapseOlderTurns)
    {
        CollapseOlderTurns = collapseOlderTurns;
        Rounds = new TranscriptRoundCollection(this, rounds);
    }

    /// <summary>
    /// What the transcript list binds to: one container per round. Every row of a round lives inside that round, so rows of
    /// different rounds are never siblings and the axis of one round cannot be crossed by another. The panel prices a round
    /// it has not realized yet as the sum of its rows (see <c>TranscriptStackPanel</c>) instead of one average per round.
    /// <para>The collection names its transcript, so the list displaying it follows that transcript's own signals - an older
    /// page landing in front of the reader is not a message they sent (see <c>TranscriptFollower</c>).</para>
    /// </summary>
    public ReadOnlyObservableCollection<TranscriptRoundViewModel> Rounds { get; }

    /// <summary>
    /// Every row of every round in order, as a snapshot. The list binds to <see cref="Rounds"/>; this flattened view exists for
    /// callers that reason about individual rows (the tests and the bench), and it is deliberately not the live collection.
    /// </summary>
    internal List<object> FlatRows() => Rounds.SelectMany(round => round.Rows).ToList();

    public bool CollapseOlderTurns { get; }

    public bool IsEmpty => items.Count == 0;

    public TurnCardViewModel? Current => current;

    /// <summary>
    /// The retry of a failed turn, installed by whoever owns this transcript's turns: the gate answers from that session's
    /// live state (writable, quiet, no resume already waiting) and the runner replays the turn through that session's own
    /// entry point. A transcript therefore never names a session kind — a root session, a hand-created agent session driven
    /// another way, and a model-spawned sub-agent (which installs neither) are told apart by what is installed, not by a
    /// condition on the session.
    /// </summary>
    internal Func<TurnCardViewModel, bool>? CanRetryTurn { get; set; }

    /// <summary>Runs the retry; owns its own failures, so the transcript never observes a fault of it.</summary>
    internal Func<TurnCardViewModel, Task>? RetryTurn { get; set; }

    /// <summary>Whether the newest turn may be continued (a failed turn that already ran a tool); the session's live gate.</summary>
    internal Func<TurnCardViewModel, bool>? CanResumeTurn { get; set; }

    /// <summary>Runs the continue; owns its own failures like <see cref="RetryTurn"/>.</summary>
    internal Func<TurnCardViewModel, Task>? ResumeTurn { get; set; }

    /// <summary>Only the newest turn is retryable: once a later turn exists the user has already moved on past it.</summary>
    internal bool CanRetry(TurnCardViewModel card) =>
        ReferenceEquals(card, current) && CanRetryTurn?.Invoke(card) == true;

    internal Task RetryAsync(TurnCardViewModel card) =>
        RetryTurn is { } run && CanRetry(card) ? run(card) : Task.CompletedTask;

    /// <summary>The same newest-turn rule for continuing a turn that already had its side effects.</summary>
    internal bool CanResume(TurnCardViewModel card) =>
        ReferenceEquals(card, current) && CanResumeTurn?.Invoke(card) == true;

    internal Task ResumeAsync(TurnCardViewModel card) =>
        ResumeTurn is { } run && CanResume(card) ? run(card) : Task.CompletedTask;

    /// <summary>Re-reads the retry gate of the newest turn after the session's state moved (running, read-only, a resume run).</summary>
    internal void RefreshRetry() => current?.RefreshRetry();

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

        // A user message opens its own round; the turn that follows joins it, so the round is the unit the list hosts.
        items.Add(message);
        var host = new TranscriptRoundViewModel();
        host.RowList.Add(message);
        rounds.Add(host);

        // A user message opens the round its turn joins, in both transcript shapes: a sub-agent round then holds the task row
        // and the turn below it, so the axis between them is continuous instead of being cut between two rounds.
        pendingRound = host;

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// One session-to-session receipt as its own card (forge-core "reply receipt"): the initiator's transcript shows the three
    /// lines the receipt carries - header, change line, report - instead of a plain notice, so a reply that arrived while the
    /// session was idle reads as a message and not as a system note. It opens a round of its own; it never starts a turn.
    /// </summary>
    public void AddSessionReply(string text, DateTimeOffset at)
    {
        var reply = new SessionReplyViewModel(text, at);
        items.Add(reply);
        var host = new TranscriptRoundViewModel();
        host.RowList.Add(reply);
        rounds.Add(host);
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void AddNotice(LocText text, bool isError = false, TimelineNodeKind kind = TimelineNodeKind.Notice)
    {
        var notice = new NoticeViewModel(text, isError, kind);
        items.Add(notice);
        var host = new TranscriptRoundViewModel();
        host.RowList.Add(notice);
        rounds.Add(host);
        OnPropertyChanged(nameof(IsEmpty));
    }

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
        // A sub-agent's card can be begun anywhere - by a tool row, by the first streamed delta, by a usage flush - so
        // the model that runs the turn is stamped here, through the one hook every creation path shares. The root sets
        // none: its own turn flow names the composer's choice at the moment it began.
        StampTurnModel?.Invoke(card);
        var wasCurrent = current;
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

        // The turn that was current until now stopped being retryable the moment a newer one started; its button has to go
        // with it, and nothing else will touch that row again.
        wasCurrent?.RefreshRetry();
        var claimed = pendingRound is { };
        var host = claimed ? pendingRound! : new TranscriptRoundViewModel();
        pendingRound = null;
        host.Card = card;
        card.Host = host;
        if (!claimed)
        {
            rounds.Add(host);
        }

        if (pendingUserMessage is { } user)
        {
            pendingUserMessage = null;
            card.UserMessage = user;
        }

        items.Add(card);
        OnPropertyChanged(nameof(IsEmpty));

        // A fresh turn has nothing in flight yet: its working row says so from the first frame.
        WorkingChanged(card);
        return card;
    }

    /// <summary>A block joined a card: it takes its place after the card's last block row, unless the card is collapsed.</summary>
    internal void BlockAdded(TurnCardViewModel card, TurnBlockViewModel block)
    {
        if (!card.IsExpanded || card.Host?.RowList is not { } rows)
        {
            return;
        }

        var at = EndOfBlocks(card);
        if (at >= 0)
        {
            rows.Insert(at, block);
        }
    }

    /// <summary>
    /// A card was expanded or collapsed from its round-end row: its block rows appear after the round's user message or go
    /// away. No transcript keeps a header row of its own, so the card itself is never in the list — only its blocks are.
    /// </summary>
    internal void ExpandedChanged(TurnCardViewModel card)
    {
        if (card.Host?.RowList is not { } rows)
        {
            return;
        }

        // The round-end row's chevron reads the flag it just changed.
        card.NotifyExpanded();
        if (card.IsExpanded)
        {
            var at = EndOfBlocks(card);
            if (at < 0)
            {
                return;
            }

            foreach (var block in card.Blocks)
            {
                rows.Insert(at++, block);
            }
        }
        else
        {
            for (var index = rows.Count - 1; index >= 0; index--)
            {
                if (rows[index] is TurnBlockViewModel block && ReferenceEquals(block.Turn, card))
                {
                    rows.RemoveAt(index);
                }
            }
        }
    }

    /// <summary>
    /// A card started or stopped waiting: the "what happens next" row is the last row of the turn's blocks while nothing is in
    /// flight, and leaves as soon as something is (or the turn ends).
    /// </summary>
    internal void WorkingChanged(TurnCardViewModel card)
    {
        if (card.Host?.RowList is not { } rows)
        {
            return;
        }

        var row = card.WorkingRow;
        var at = IndexOf(rows, row);
        if (card.IsWorking)
        {
            if (at < 0 && EndOfBlocks(card) is var insert && insert >= 0)
            {
                rows.Insert(insert, row);
            }
        }
        else if (at >= 0)
        {
            rows.RemoveAt(at);
        }
    }

    /// <summary>A card gained or lost its footnote: the footnote row follows the card's block rows.</summary>
    internal void FootnoteChanged(TurnCardViewModel card)
    {
        if (card.Host?.RowList is not { } rows)
        {
            return;
        }

        var row = card.FootnoteRow;
        var at = IndexOf(rows, row);
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
    /// A card finished: its end row lands after the footnote (or after the blocks when there is none) and becomes the last row
    /// of the round, where the axis ends on the round's closing node. Re-entrant like the finish itself — a replayed turn
    /// finishes once per tool message.
    /// </summary>
    internal void DividerChanged(TurnCardViewModel card)
    {
        if (card.Host?.RowList is not { } rows)
        {
            return;
        }

        var row = card.DividerRow;
        var at = IndexOf(rows, row);
        if (card.HasDivider)
        {
            if (at < 0)
            {
                var insert = card.HasFootnote && IndexOf(rows, card.FootnoteRow) is var footnoteAt && footnoteAt >= 0
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

    /// <summary>The index just past the card's header and block rows inside its own round, or -1 when it has no round.</summary>
    private int EndOfBlocks(TurnCardViewModel card)
    {
        if (card.Host?.RowList is not { } rows)
        {
            return -1;
        }

        var at = IndexOf(rows, card);
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
            var userAt = IndexOf(rows, user);
            if (userAt >= 0)
            {
                return userAt + 1;
            }
        }

        // Nothing of the turn is in the list yet: its blocks belong above the rows that close it.
        if (card.HasFootnote && IndexOf(rows, card.FootnoteRow) is var footnoteAt && footnoteAt >= 0)
        {
            return footnoteAt;
        }

        if (card.HasDivider && IndexOf(rows, card.DividerRow) is var dividerAt && dividerAt >= 0)
        {
            return dividerAt;
        }

        return rows.Count;
    }

    /// <summary>Searches from the end: the rows being changed are almost always the newest.</summary>
    private static int IndexOf(System.Collections.IList rows, object row)
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

    /// <summary>
    /// Names the model on every turn card this transcript begins; a sub-agent sets it once and every turn's divider then
    /// says what ran it, exactly the way the root's dividers do. Null on the root's own transcript.
    /// </summary>
    internal Action<TurnCardViewModel>? StampTurnModel;

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

    /// <summary>
    /// The turn boundary a sub-agent's own event stream reports: closes the turn like <see cref="EndTurn"/> and then forgets
    /// the card, so the next content begins a fresh one instead of appending to the closed one. The root must <em>not</em>
    /// use this - its trailing Background flush still owes the just-closed card its text - but a child's boundary arrives as
    /// <c>AgentTurnCompleted</c>, and the coalescer has already drained that turn's text ahead of the event, so nothing of
    /// the closed turn can follow it.
    /// </summary>
    public void EndIsolatedTurn(TimeSpan? elapsed, LocText? error)
    {
        EndTurn(elapsed, error);
        current = null;
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
        rounds.Clear();
        current = null;
        round = 0;
        lastUserAt = null;
        pendingRound = null;
        pendingUserMessage = null;
        OnPropertyChanged(nameof(IsEmpty));
    }
}
