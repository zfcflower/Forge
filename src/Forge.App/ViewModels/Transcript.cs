using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using Forge.Abstractions;

namespace Forge.App.ViewModels;

internal abstract class TranscriptItemViewModel : ObservableObject;

/// <summary>A user message, rendered as an IM-style bubble aligned right.</summary>
internal sealed class UserMessageViewModel(string text, DateTimeOffset at) : TranscriptItemViewModel
{
    public string Text { get; } = text;

    public string Time { get; } = at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>A one-line system note inside a transcript (compaction, denied call, stopped turn).</summary>
internal sealed class NoticeViewModel(string text, bool isError = false) : TranscriptItemViewModel
{
    public string Text { get; } = text;

    public bool IsError { get; } = isError;
}

/// <summary>Base of the blocks inside a turn card: reply text, tool rows and notes, in arrival order.</summary>
internal abstract class TurnBlockViewModel : ObservableObject;

/// <summary>One rendered stretch of a reply; a delta updates only this segment's text.</summary>
internal sealed class TextSegmentViewModel(string value) : ObservableObject
{
    private string text = value;

    public string Text
    {
        get => text;
        set => Set(ref text, value);
    }
}

/// <summary>
/// One run of reply text, rendered as an <c>ItemsControl</c> of text blocks. Deltas are coalesced once per dispatcher pass
/// and land in the open segment only, so a flush never re-materializes the whole reply; a segment is closed at the next
/// paragraph break once it reaches <see cref="SegmentLimit"/> characters.
/// </summary>
internal sealed class TurnTextViewModel : TurnBlockViewModel
{
    private const int SegmentLimit = 2048;
    private const int HardLimit = SegmentLimit * 4;

    private readonly StringBuilder open = new();
    private readonly ObservableCollection<TextSegmentViewModel> segments = [];
    private bool isStreaming = true;

    public TurnTextViewModel() => Segments = new ReadOnlyObservableCollection<TextSegmentViewModel>(segments);

    public ReadOnlyObservableCollection<TextSegmentViewModel> Segments { get; }

    public bool IsStreaming
    {
        get => isStreaming;
        private set => Set(ref isStreaming, value);
    }

    public void Append(string chunk)
    {
        open.Append(chunk);
        var closed = false;
        while (open.Length > SegmentLimit)
        {
            var cut = LastBreak(open, '\n');
            if (cut <= 0)
            {
                if (open.Length <= HardLimit)
                {
                    break;
                }

                cut = LastBreak(open, ' ');
                if (cut <= 0)
                {
                    cut = open.Length;
                }
            }

            segments.Add(new TextSegmentViewModel(open.ToString(0, cut)));
            open.Remove(0, cut);
            closed = true;
        }

        if (closed || segments.Count == 0)
        {
            segments.Add(new TextSegmentViewModel(open.ToString()));
            return;
        }

        segments[^1].Text = open.ToString();
    }

    public void Complete()
    {
        IsStreaming = false;
        if (segments.Count == 0 && open.Length == 0)
        {
            segments.Add(new TextSegmentViewModel(Strings.NoText));
        }
    }

    /// <summary>Position just past the last <paramref name="marker" /> in the open segment, or -1 when there is none.</summary>
    private static int LastBreak(StringBuilder buffer, char marker)
    {
        for (var index = buffer.Length - 1; index >= 0; index--)
        {
            if (buffer[index] == marker)
            {
                return index + 1;
            }
        }

        return -1;
    }
}

/// <summary>A note inside a turn card, such as a redirected or stopped sub-agent.</summary>
internal sealed class TurnNoticeViewModel(string text, bool isError) : TurnBlockViewModel
{
    public string Text { get; } = text;

    public bool IsError { get; } = isError;
}

/// <summary>
/// One tool call: "▸ name · argument summary · ✓" collapsed, full arguments and result when expanded.
/// </summary>
internal sealed class ToolRowViewModel(string name, string argumentsJson) : TurnBlockViewModel
{
    private const int SummaryLimit = 160;

    private string status = "…";
    private string? result;
    private bool isError;
    private bool isExpanded;

    public string Name { get; } = name;

    public string Arguments { get; } = argumentsJson;

    public string ArgumentSummary { get; } = Compact(argumentsJson, SummaryLimit);

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    public string? Result
    {
        get => result;
        private set
        {
            if (Set(ref result, value))
            {
                OnPropertyChanged(nameof(HasResult));
            }
        }
    }

    public bool HasResult => !string.IsNullOrEmpty(result);

    public bool IsError
    {
        get => isError;
        private set => Set(ref isError, value);
    }

    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }

    public RelayCommand ToggleCommand => new(() => IsExpanded = !IsExpanded);

    public void Complete(ToolResult toolResult)
    {
        IsError = toolResult.IsError;
        Status = toolResult.IsError ? "✗" : "✓";
        Result = toolResult.Content;
    }

    internal static string Compact(string text, int limit)
    {
        var builder = new StringBuilder(Math.Min(text.Length, limit));
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (builder.Length >= limit)
            {
                builder.Append('…');
                break;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
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
    private string header;
    private string? footnote;
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
        header = Title();
    }

    public int Round { get; }

    public ReadOnlyObservableCollection<TurnBlockViewModel> Blocks { get; }

    public string Header
    {
        get => header;
        private set => Set(ref header, value);
    }

    public string? Footnote
    {
        get => footnote;
        private set
        {
            if (Set(ref footnote, value))
            {
                OnPropertyChanged(nameof(HasFootnote));
            }
        }
    }

    public bool HasFootnote => !string.IsNullOrEmpty(footnote);

    /// <summary>Main-session turns are always expanded; a sub-agent collapses every turn but the newest.</summary>
    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (Set(ref isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpandGlyph));
            }
        }
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

    public void AddTool(ToolCall call)
    {
        openText = null;
        var row = new ToolRowViewModel(call.Name, call.ArgumentsJson);
        toolRows[call.Id] = row;
        blocks.Add(row);
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

    public void AddNotice(string text, bool isError = false)
    {
        openText = null;
        blocks.Add(new TurnNoticeViewModel(text, isError));
    }

    public void AddUsage(TokenUsage usage, StopReason stopReason)
    {
        inputTokens += usage.InputTokens;
        outputTokens += usage.OutputTokens;
        stop = stopReason;
        sawUsage = true;
        Header = Title();
        OnPropertyChanged(nameof(HasUsage));
    }

    /// <summary>Closes the turn; called on success, failure and cancellation alike.</summary>
    public void Finish(TimeSpan? elapsed, string? error)
    {
        openText?.Complete();
        openText = null;
        lastElapsed = elapsed;
        IsRunning = false;
        Header = Title(elapsed);
        Footnote = error;
        foreach (var row in toolRows.Values)
        {
            if (row.Status == "…")
            {
                row.Complete(new ToolResult(Strings.NoToolResult, IsError: true));
            }
        }

        toolRows.Clear();
    }

    public void Tick(TimeSpan elapsed)
    {
        if (isRunning)
        {
            Header = Title(elapsed);
        }
    }

    /// <summary>Elapsed time the card shows, or null when it was replayed from the store and never measured.</summary>
    public TimeSpan? Elapsed => isRunning ? null : lastElapsed;

    private string Title(TimeSpan? elapsed = null) =>
        Strings.TurnHeader(
            Round,
            startedAt,
            elapsed,
            isRunning,
            sawUsage ? inputTokens : null,
            sawUsage ? outputTokens : null);
}

/// <summary>
/// The ordered transcript of one agent (the main session or one sub-agent). Streaming text is coalesced into at most one
/// UI update per dispatcher pass; every other event is posted to the UI thread by the owner.
/// </summary>
internal sealed class TranscriptViewModel : ObservableObject
{
    private readonly ObservableCollection<TranscriptItemViewModel> items = [];
    private readonly Lock pendingGate = new();
    private readonly StringBuilder pending = new();
    private int flushScheduled;
    private TurnCardViewModel? current;
    private int round;
    private DateTimeOffset currentStartedAt;

    public TranscriptViewModel(bool collapseOlderTurns)
    {
        CollapseOlderTurns = collapseOlderTurns;
        Items = new ReadOnlyObservableCollection<TranscriptItemViewModel>(items);
    }

    public ReadOnlyObservableCollection<TranscriptItemViewModel> Items { get; }

    public bool CollapseOlderTurns { get; }

    public bool IsEmpty => items.Count == 0;

    public TurnCardViewModel? Current => current;

    public void AddUserMessage(string text, DateTimeOffset at) =>
        Add(new UserMessageViewModel(text, at));

    public void AddNotice(string text, bool isError = false) =>
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
        currentStartedAt = at;
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

    public void QueueDelta(string chunk)
    {
        lock (pendingGate)
        {
            pending.Append(chunk);
        }

        if (Interlocked.Exchange(ref flushScheduled, 1) == 0)
        {
            Dispatcher.UIThread.Post(FlushPending, DispatcherPriority.Render);
        }
    }

    public void FlushPending()
    {
        Interlocked.Exchange(ref flushScheduled, 0);
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
    public void EndTurn(TimeSpan? elapsed, string? error)
    {
        FlushPending();
        current?.Finish(elapsed, error);
    }

    public void Tick()
    {
        current?.Tick(DateTimeOffset.Now - currentStartedAt);
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
