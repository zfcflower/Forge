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
internal sealed class NoticeViewModel(LocText text, bool isError = false) : TranscriptItemViewModel
{
    /// <summary>The note in the current language, resolved when the note is rendered.</summary>
    public string Text => text.Resolve();

    public bool IsError { get; } = isError;

    public override void RefreshText() => OnPropertyChanged(nameof(Text));
}

/// <summary>Base of the blocks inside a turn card: reply text, tool rows and notes, in arrival order.</summary>
internal abstract class TurnBlockViewModel : ObservableObject
{
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
            OnPropertyChanged(nameof(HasPlaceholder));
        }

        RaiseContentChanged();
    }

    public override void RefreshText() => OnPropertyChanged(nameof(PlaceholderText));

    private void RaiseContentChanged() => ContentChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A note inside a turn card, such as a redirected or stopped sub-agent.</summary>
internal sealed class TurnNoticeViewModel(LocText text, bool isError) : TurnBlockViewModel
{
    /// <summary>The note in the current language, resolved when the note is rendered.</summary>
    public string Text => text.Resolve();

    public bool IsError { get; } = isError;

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
        set => SetAndNotify(ref isExpanded, value, nameof(ExpandGlyph));
    }

    public string ExpandGlyph => isExpanded ? "▾" : "▸";

    public RelayCommand ToggleCommand => new(() => IsExpanded = !IsExpanded);

    public bool IsRunning
    {
        get => isRunning;
        private set => Set(ref isRunning, value);
    }

    public bool HasUsage => sawUsage;

    public TokenUsage Usage => new(inputTokens, outputTokens);

    public void AppendText(string chunk)
    {
        if (openText is null)
        {
            openText = new TurnTextViewModel();
            blocks.Add(openText);
        }

        openText.Append(chunk);
    }

    /// <summary>Adds a running tool call's row and hands it back: the session attaches the live state (wave W8).</summary>
    public ToolRowViewModel AddTool(ToolCall call)
    {
        SealText();
        var row = new ToolRowViewModel(call.Name, call.ArgumentsJson);
        toolRows[call.Id] = row;
        blocks.Add(row);
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
        blocks.Add(late);
    }

    public void AddNotice(LocText text, bool isError = false)
    {
        SealText();
        blocks.Add(new TurnNoticeViewModel(text, isError));
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
        lastElapsed = elapsed;
        IsRunning = false;
        OnPropertyChanged(nameof(Header));
        footnote = error ?? default;
        OnPropertyChanged(nameof(Footnote));
        OnPropertyChanged(nameof(HasFootnote));
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
        foreach (var block in blocks)
        {
            block.RefreshText();
        }
    }
}

/// <summary>
/// The ordered transcript of one agent (the main session or one sub-agent). Streaming text is merged on the worker thread
/// into a pending buffer; the session's frame coalescer drains it on the UI thread.
/// </summary>
internal sealed class TranscriptViewModel : ObservableObject
{
    private readonly ObservableCollection<TranscriptItemViewModel> items = [];
    private readonly Lock pendingGate = new();
    private readonly StringBuilder pending = new();
    private TurnCardViewModel? current;
    private int round;

    public TranscriptViewModel(bool collapseOlderTurns)
    {
        CollapseOlderTurns = collapseOlderTurns;
        Items = new ReadOnlyObservableCollection<TranscriptItemViewModel>(items);
    }

    public ReadOnlyObservableCollection<TranscriptItemViewModel> Items { get; }

    public bool CollapseOlderTurns { get; }

    public bool IsEmpty => items.Count == 0;

    public TurnCardViewModel? Current => current;

    public void AddUserMessage(string text, DateTimeOffset at, IReadOnlyList<ContentBlock>? attachments = null) =>
        Add(new UserMessageViewModel(text, at, attachments));

    public void AddNotice(LocText text, bool isError = false) =>
        Add(new NoticeViewModel(text, isError));

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
        Add(card);
        return card;
    }

    /// <summary>Merges a worker-thread delta into the pending buffer; the owner schedules the UI-thread flush.</summary>
    public void QueueDelta(string chunk)
    {
        lock (pendingGate)
        {
            pending.Append(chunk);
        }
    }

    public void FlushPending()
    {
        string chunk;
        lock (pendingGate)
        {
            chunk = pending.ToString();
            pending.Clear();
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
        current = null;
        round = 0;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Add(TranscriptItemViewModel item)
    {
        items.Add(item);
        OnPropertyChanged(nameof(IsEmpty));
    }
}
