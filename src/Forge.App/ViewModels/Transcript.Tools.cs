using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Services;

namespace Forge.App.ViewModels;

// ToolRowViewModel is split by owner: this file is the row itself (call, status, result text) and the live state a running
// call shares with the resource inspector (wave W8); Transcript.ToolImages.cs holds the result image blocks and thumbnails
// (wave W4).
/// <summary>
/// One tool call: "▸ name · argument summary · ✓" collapsed, full arguments and result when expanded.
/// </summary>
internal sealed partial class ToolRowViewModel(string name, string argumentsJson) : TurnBlockViewModel, ICopyableText
{
    private const int SummaryLimit = 160;

    /// <summary>The most result text a row lays out; a build log of megabytes would stall layout. Copy still gives all of it.</summary>
    private const int ResultDisplayLimit = 24_000;

    /// <summary>A result past this many lines (or characters) gets the "show more" toggle; a short one never needs it.</summary>
    private const int ExpandableLines = 10;

    private static readonly string[] HeadlineKeys = ["command", "cmd", "file_path", "filePath", "path", "pattern", "query", "url", "description", "name"];
    private static readonly string[] PathKeys = ["file_path", "filePath", "path"];

    private string? headline;
    private string? targetPath;
    private bool argumentsRead;
    private bool isResultExpanded;
    private RelayCommand? toggleCommand;
    private RelayCommand? toggleResultCommand;

    private string status = "…";
    private LocText result;
    private bool hasResult;
    private bool isError;
    private bool isExpanded;
    private bool isRunning = true;
    private LiveToolViewModel? live;

    public string Name { get; } = name;

    public string Arguments { get; } = argumentsJson;

    public string ArgumentSummary { get; } = Compact(argumentsJson, SummaryLimit);

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    /// <summary>The tool's answer in the current language; null until it returned something.</summary>
    public string? Result => hasResult ? result.Resolve() : null;

    public bool HasResult => hasResult;

    public bool IsError
    {
        get => isError;
        private set => Set(ref isError, value);
    }

    /// <summary>True until the call returns; the timeline draws a running node (accent, filled, pulsing) while it is.</summary>
    public bool IsRunning => isRunning;

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (Set(ref isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpandedBody));
            }
        }
    }

    /// <summary>
    /// The row itself while it is open, null while it is closed: the content of the slot the open part of the row is built in.
    /// A closed row, which is nearly every row of a long transcript, builds none of the arguments, result, actions, images and
    /// live tail controls (building them hidden cost several times a whole text row at realization).
    /// </summary>
    public object? ExpandedBody => isExpanded ? this : null;

    public RelayCommand ToggleCommand => toggleCommand ??= new RelayCommand(() => IsExpanded = !IsExpanded);

    /// <summary>The timeline node kind this call wears; the card in a tool group repeats that glyph.</summary>
    public TimelineNodeKind Kind => TimelineNodeMap.KindForTool(Name);

    /// <summary>Whether the call carried an input worth a line of its own (an empty object is no input).</summary>
    public bool HasInput => Arguments is not ("{}" or "null" or "");

    /// <summary>
    /// The card's one body line: what went in, or — for a call that carries no input — what came out. The status is never
    /// this line: the card's own status icon already says it.
    /// </summary>
    public string CardBody => IsQuestionCard ? QuestionSummary : HasInput ? Headline : OutputLine;

    /// <summary>The question itself for an <c>ask_user</c> card, in place of the arguments JSON nobody answers.</summary>
    private string QuestionSummary
    {
        get
        {
            var items = QuestionItems;
            return items.Count > 0 && items[0].Prompt.Length > 0 ? items[0].Prompt : Name;
        }
    }

    /// <summary>True once the call returned without failing; the card's check.</summary>
    public bool IsSucceeded => HasResult && !IsError;

    /// <summary>True while the call has produced nothing yet: queued or just started, drawn as a waiting dot.</summary>
    public bool IsWaiting => !IsRunning && !HasResult && !IsError;

    private string OutputLine
    {
        get
        {
            var text = Result;
            if (string.IsNullOrEmpty(text))
            {
                return Status;
            }

            var end = text.IndexOfAny(['\r', '\n']);
            return Compact(end < 0 ? text : text[..end], SummaryLimit);
        }
    }

    // ---------------------------------------------------------------- readable header and result

    /// <summary>
    /// What the call is about, in one line: the command, file, pattern or address out of the arguments when they are JSON with
    /// a well-known key, otherwise the compacted arguments. Read once, when the header is first drawn.
    /// </summary>
    public string Headline
    {
        get
        {
            ReadArguments();
            return headline ?? ArgumentSummary;
        }
    }

    /// <summary>The file the call works on (a path argument), or null; the row offers to open it.</summary>
    public string? TargetPath
    {
        get
        {
            ReadArguments();
            return targetPath;
        }
    }

    public bool HasTarget => TargetPath is not null;

    private void ReadArguments()
    {
        if (argumentsRead)
        {
            return;
        }

        argumentsRead = true;
        try
        {
            using var document = JsonDocument.Parse(Arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var key in HeadlineKeys)
            {
                if (document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
                    value.GetString() is { Length: > 0 } text)
                {
                    headline = Compact(text, SummaryLimit);
                    break;
                }
            }

            foreach (var key in PathKeys)
            {
                if (document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
                    value.GetString() is { Length: > 0 } path)
                {
                    targetPath = path;
                    break;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON (a provider that streams something else): the compacted text is the headline.
        }
    }

    /// <summary>The result as shown: cut at <see cref="ResultDisplayLimit"/> characters so a huge output cannot stall layout.</summary>
    public string? ResultDisplay =>
        hasResult && result.Resolve() is { } full ? (full.Length > ResultDisplayLimit ? full[..ResultDisplayLimit] : full) : null;

    /// <summary>The note under a cut result ("first 24000 of 90000 characters"); empty when nothing was cut.</summary>
    public string ResultNote =>
        hasResult && result.Resolve() is { Length: > ResultDisplayLimit } full
            ? SessionStrings.ToolResultCut(ResultDisplayLimit, full.Length)
            : string.Empty;

    public bool HasResultNote => ResultNote.Length > 0;

    /// <summary>The copy button's text: the whole result, not the cut one.</summary>
    public string CopyText => Result ?? string.Empty;

    /// <summary>A result long enough to be worth a taller view.</summary>
    public bool CanExpandResult
    {
        get
        {
            var text = Result;
            if (text is null)
            {
                return false;
            }

            if (text.Length > 1500)
            {
                return true;
            }

            var lines = 0;
            foreach (var character in text)
            {
                if (character == (char)10 && ++lines >= ExpandableLines)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool IsResultExpanded
    {
        get => isResultExpanded;
        set
        {
            if (Set(ref isResultExpanded, value))
            {
                OnPropertyChanged(nameof(ResultMaxHeight));
                OnPropertyChanged(nameof(ResultToggleText));
            }
        }
    }

    /// <summary>240 while folded; the expanded view is taller but still bounded, with its own scroll bar.</summary>
    public double ResultMaxHeight => isResultExpanded ? 720 : 240;

    public string ResultToggleText => isResultExpanded ? SessionStrings.ToolShowLess : SessionStrings.ToolShowMore;

    public RelayCommand ToggleResultCommand => toggleResultCommand ??= new RelayCommand(() => IsResultExpanded = !IsResultExpanded);

    // ---------------------------------------------------------------- live state of a running call

    /// <summary>
    /// The live state of this call: the streamed output tail and, so far, the elapsed time. Attached once by the session
    /// when the call starts (<see cref="SessionWorkspaceViewModel.TrackTool"/>); null for a call whose start event the
    /// session never saw (a replayed transcript).
    /// </summary>
    internal LiveToolViewModel? Live => live;

    /// <summary>True while the call runs <em>and</em> has streamed anything: the tail block only exists then.</summary>
    public bool HasLiveOutput => live is { IsRunning: true, HasTail: true };

    /// <summary>The trimmed tail (the last <c>InspectorLogTailLines</c> lines), empty when nothing streamed yet.</summary>
    public string LiveOutputText => live?.TailText ?? string.Empty;

    /// <summary>"运行中 · 12.3 秒": how long the call has been running, in the current language.</summary>
    public string LiveElapsedText => live is { IsRunning: true } running ? running.ElapsedText : string.Empty;

    /// <summary>Hands the running call's live state to this row; the row mirrors it until the call finishes.</summary>
    internal void AttachLive(LiveToolViewModel activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (live is not null)
        {
            return;
        }

        live = activity;
        activity.PropertyChanged += OnLiveChanged;
        RaiseLiveText();
    }

    /// <summary>The entry raises only from the UI thread (its own <see cref="LiveToolViewModel.Tick"/>), so is this.</summary>
    private void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LiveToolViewModel.IsRunning):
                // The call returned: nothing of the entry changes for this row any more, so the row stops listening.
                if (sender is LiveToolViewModel finished && !finished.IsRunning)
                {
                    finished.PropertyChanged -= OnLiveChanged;
                }

                RaiseLiveText();
                break;
            case nameof(LiveToolViewModel.TailText):
            case nameof(LiveToolViewModel.HasTail):
                RaiseLiveText();
                break;
            case nameof(LiveToolViewModel.ElapsedText):
                OnPropertyChanged(nameof(LiveElapsedText));
                break;
        }
    }

    private void RaiseLiveText()
    {
        OnPropertyChanged(nameof(HasLiveOutput));
        OnPropertyChanged(nameof(LiveOutputText));
        OnPropertyChanged(nameof(LiveElapsedText));
    }

    // ---------------------------------------------------------------- result

    public void Complete(ToolResult toolResult)
    {
        Complete(LocText.Literal(toolResult.Content), toolResult.IsError);
        CaptureImages(toolResult.Blocks);
    }

    /// <summary>
    /// Hook for the result's image blocks, implemented in <c>Transcript.ToolImages.cs</c> (wave W4). Without an implementation
    /// the call is compiled away.
    /// </summary>
    partial void CaptureImages(IReadOnlyList<ContentBlock> blocks);

    /// <summary>
    /// Closes the row with a result that is not the tool's own text - the placeholder of a call that never came back - so a
    /// language switch still reaches it.
    /// </summary>
    public void Complete(LocText content, bool isError)
    {
        result = content;
        hasResult = !string.IsNullOrEmpty(content.Resolve());
        IsError = isError;
        Status = isError ? "✗" : "✓";
        isRunning = false;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(ResultDisplay));
        OnPropertyChanged(nameof(ResultNote));
        OnPropertyChanged(nameof(HasResultNote));
        OnPropertyChanged(nameof(CanExpandResult));
        OnPropertyChanged(nameof(HasResult));
        RaiseQuestionCard();
    }

    public override void RefreshText()
    {
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(ResultDisplay));
        OnPropertyChanged(nameof(ResultNote));
        OnPropertyChanged(nameof(ResultToggleText));
        OnPropertyChanged(nameof(HasResult));
        RaiseQuestionCard();
        OnPropertyChanged(nameof(LiveElapsedText));
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
/// One tool call that is running right now: the live state the transcript's tool row and the resource inspector both read.
/// <para>Threading is the whole point of this type. <see cref="RecordTail"/> runs on the tool's own output thread (the
/// producer already throttles to a few snapshots per second) and does nothing but publish the latest snapshot into a
/// volatile field: no lock, no allocation, no notification, so a running command never posts to the dispatcher. Every
/// property change is raised from <see cref="Tick"/> and <see cref="Finish"/> on the UI thread; the tail is trimmed there,
/// and only when the snapshot or the line limit actually changed. A closed inspector costs nothing of its own (no timer, no
/// extra trim); the transcript's own row still refreshes on the session clock, which is what that one tick is for. The
/// entry holds at most the producer's 16 KiB snapshot, and releases it in <see cref="Finish"/>.</para>
/// </summary>
internal sealed class LiveToolViewModel : NotifyingObject
{
    private const int SummaryLimit = 160;

    private readonly Func<string?> agentKind;

    /// <summary>Written by the producer thread, read by the UI thread: the latest full snapshot, never a fragment.</summary>
    private string tail = string.Empty;

    /// <summary>The line limit and snapshot <see cref="shownTail"/> was last cut from; UI thread only.</summary>
    private int trimmedLines;

    private string trimmedSource = string.Empty;

    /// <summary>The trimmed snapshot the tail block renders; recut in <see cref="Tick"/>, never on the producer thread.</summary>
    private string shownTail = string.Empty;

    private DateTimeOffset clockAt;
    private bool isRunning = true;
    private bool isExpanded = true;
    private bool isStopRequested;

    /// <summary>Asks the session's runtime to cancel this one call; null for a call that cannot be stopped (a read-only view).</summary>
    private readonly Func<string, string, bool>? stopCall;

    /// <param name="stopCall">
    /// Cancels exactly this call, by (agent id, call id), and answers whether a running call was found - the turn and the
    /// other calls go on (Core gives every call its own token). The session passes one cached delegate for all its calls;
    /// null when nothing can be stopped (no runtime).
    /// </param>
    public LiveToolViewModel(
        string agentId,
        Func<string?> agentKind,
        ToolCall call,
        DateTimeOffset startedAt,
        Func<string, string, bool>? stopCall)
    {
        ArgumentNullException.ThrowIfNull(agentKind);
        ArgumentNullException.ThrowIfNull(call);
        AgentId = agentId;
        CallId = call.Id;
        this.agentKind = agentKind;
        this.stopCall = stopCall;
        Name = call.Name;
        ArgumentSummary = ToolRowViewModel.Compact(call.ArgumentsJson, SummaryLimit);
        StartedAt = startedAt;
        clockAt = startedAt;
        StopCommand = new RelayCommand(RequestStop, () => CanStop);
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    /// <summary>The agent whose tool call this is: <c>"root"</c> or a sub-agent's id.</summary>
    public string AgentId { get; }

    /// <summary>The provider's id of the call; with <see cref="AgentId"/> it is what Core's single-call cancel is keyed by.</summary>
    public string CallId { get; }

    public string Name { get; }

    /// <summary>The call's arguments, compacted the same way the transcript's row does it.</summary>
    public string ArgumentSummary { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// "主 Agent" for the session's own agent, otherwise the running child's kind ("Scout", …). Resolved when read, so a
    /// language switch reaches the label and a child that is still starting resolves as soon as its tab exists.
    /// </summary>
    public string AgentLabel => string.Equals(AgentId, "root", StringComparison.Ordinal)
        ? ResourceInspectorStrings.RootAgent
        : agentKind() is { Length: > 0 } kind ? kind : ResourceInspectorStrings.ChildAgent;

    public bool IsRunning => isRunning;

    /// <summary>How long the call has run so far, re-formatted in the current language on every read.</summary>
    public string ElapsedText => TextFormat.Elapsed(clockAt > StartedAt ? clockAt - StartedAt : TimeSpan.Zero);

    /// <summary>True once the call has streamed anything; the tail block (and its empty hint) hangs off this.</summary>
    public bool HasTail => shownTail.Length > 0;

    /// <summary>The last <c>InspectorLogTailLines</c> lines of that snapshot, cut on the UI thread; empty until output arrives.</summary>
    public string TailText => shownTail;

    /// <summary>Collapsed state of the inspector's row; the transcript's own row has its own. Starts expanded, because the
    /// tail is what the inspector is for.</summary>
    public bool IsExpanded
    {
        get => isExpanded;
        set => SetAndNotify(ref isExpanded, value, nameof(ExpandGlyph));
    }

    public string ExpandGlyph => isExpanded ? "\u25be" : "\u25b8";

    /// <summary>Built once: a getter that returned a new command would hand every binding read its own instance.</summary>
    public RelayCommand ToggleCommand { get; }

    /// <summary>
    /// Stops this one call (a command is killed with its process tree, a spawned child is cancelled); the turn continues and
    /// the model is told the user cancelled the call. Disabled once requested, after the call ended, and without a runtime.
    /// </summary>
    public RelayCommand StopCommand { get; }

    /// <summary>True while a stop would do something: the call runs, can be stopped, and no stop is pending yet.</summary>
    public bool CanStop => isRunning && !isStopRequested && stopCall is not null;

    /// <summary>A stop was accepted and the call is winding down; the row says so until the call's finish removes it.</summary>
    public bool IsStopRequested => isStopRequested;

    private void RequestStop()
    {
        if (!CanStop)
        {
            return;
        }

        // False: the call already returned (its finish is on its way) - nothing to mark, the row goes with that finish.
        if (!stopCall!(AgentId, CallId))
        {
            return;
        }

        isStopRequested = true;
        StopCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(IsStopRequested));
    }

    /// <summary>Tool's output thread: publish the newest snapshot. Dropping an intermediate one is free - it is a snapshot.</summary>
    public void RecordTail(string snapshot) => Volatile.Write(ref tail, snapshot ?? string.Empty);

    /// <summary>
    /// UI thread, once per session clock tick: refresh the elapsed reading and then the tail. The elapsed reading belongs to
    /// the clock alone - the inspector's own interval re-cuts the tail and never touches it, so "how often the list refreshes"
    /// means the output, not a duration that would jump 0.1 s four times a second.
    /// </summary>
    public void Tick(int maxLines, DateTimeOffset now)
    {
        if (!isRunning)
        {
            return;
        }

        clockAt = now > StartedAt ? now : StartedAt;
        OnPropertyChanged(nameof(ElapsedText));
        RefreshTail(maxLines);
    }

    /// <summary>
    /// UI thread: re-cut the tail when the snapshot or <paramref name="maxLines"/> changed. <paramref name="maxLines"/> is the
    /// live <c>AppPreferences.InspectorLogTailLines</c>, so a settings change lands on the next read without touching the
    /// entries. Called by <see cref="Tick"/> (the session clock) and by the inspector's own timer, which is why it is separate.
    /// </summary>
    public void RefreshTail(int maxLines)
    {
        if (!isRunning)
        {
            return;
        }

        var snapshot = Volatile.Read(ref tail);
        if (maxLines == trimmedLines && ReferenceEquals(snapshot, trimmedSource))
        {
            return;
        }

        trimmedSource = snapshot;
        trimmedLines = maxLines;
        // The producer caps a snapshot at 16 KiB, so the inspector only ever cuts lines: maxChars is the whole snapshot,
        // and Keep returns the very same string when the snapshot already fits.
        shownTail = snapshot.Length == 0
            ? string.Empty
            : TextTail.Keep(snapshot, Math.Max(1, maxLines), snapshot.Length);
        OnPropertyChanged(nameof(TailText));
        OnPropertyChanged(nameof(HasTail));
    }

    /// <summary>
    /// UI thread: the call returned (or its turn ended); the live tail and the stop button go away, and the snapshot is
    /// released - a finished row keeps the entry for good (its transcript outlives the call), and the result already carries
    /// the whole output, so holding the last 16 KiB of it for the rest of the session would be pure waste.
    /// </summary>
    public void Finish()
    {
        if (!isRunning)
        {
            return;
        }

        isRunning = false;
        Volatile.Write(ref tail, string.Empty);
        trimmedSource = string.Empty;
        trimmedLines = 0;
        shownTail = string.Empty;
        StopCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(TailText));
        OnPropertyChanged(nameof(HasTail));
    }

    /// <summary>Re-raises the texts a language switch changes; the inspector calls it for every row it shows.</summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(AgentLabel));
        OnPropertyChanged(nameof(ElapsedText));
    }
}