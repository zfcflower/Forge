using System.Collections.ObjectModel;
using System.Text;
using Forge.Abstractions;
using Forge.App.Changes.Markdown;
using Forge.App.Localization;

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
    public UserMessageViewModel(string text, DateTimeOffset at, IReadOnlyList<ContentBlock>? attachments = null)
    {
        Text = text;
        Time = TextFormat.Clock(at);
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

    string ICopyableText.CopyText => Text;

    public override void RefreshText()
    {
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
    private LocText footnote;
    private TimeSpan? lastElapsed;
    private bool isExpanded = true;
    private bool isRunning = true;
    private int inputTokens;
    private int outputTokens;
    private StopReason stop = StopReason.EndTurn;
    private bool sawUsage;

    public TurnCardViewModel(int round, DateTimeOffset startedAt)
    {
        Round = round;
        this.startedAt = startedAt;
        Blocks = new ReadOnlyObservableCollection<TurnBlockViewModel>(blocks);
    }

    public int Round { get; }

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

    public RelayCommand ToggleCommand => new(() => IsExpanded = !IsExpanded);

    public bool IsRunning
    {
        get => isRunning;
        private set => Set(ref isRunning, value);
    }

    public bool HasUsage => sawUsage;

    public TokenUsage Usage => new(inputTokens, outputTokens);

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
        stop = stopReason;
        sawUsage = true;
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(HasUsage));
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
        OnPropertyChanged(nameof(Footnote));
        OnPropertyChanged(nameof(HasFootnote));
        Owner?.FootnoteChanged(this);
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

    public void AddUserMessage(string text, DateTimeOffset at, IReadOnlyList<ContentBlock>? attachments = null) =>
        Add(new UserMessageViewModel(text, at, attachments));

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

    /// <summary>The index just past the card's header and block rows, or -1 when the card is not in the list.</summary>
    private int EndOfBlocks(TurnCardViewModel card)
    {
        var at = RowIndex(card);
        if (at < 0)
        {
            return -1;
        }

        at++;
        while (at < rows.Count && rows[at] is TurnBlockViewModel block && ReferenceEquals(block.Turn, card))
        {
            at++;
        }

        return at;
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
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Add(TranscriptItemViewModel item)
    {
        items.Add(item);
        rows.Add(item);
        OnPropertyChanged(nameof(IsEmpty));
    }
}
