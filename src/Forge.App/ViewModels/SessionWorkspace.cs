using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>
/// One open main session: its transcript, its sub-agent tabs, the turn loop and the turn checkpoints. All agent events
/// are routed by <see cref="AgentEvent.AgentId"/>, so a child's text never reaches the parent's bubble.
/// </summary>
internal sealed class SessionWorkspaceViewModel : ObservableObject, IDisposable
{
    /// <summary>How much text of a child agent is parked while its start event is still in flight.</summary>
    private const int EarlyDeltaLimit = 8192;

    private readonly SessionRuntime runtime;
    private readonly TurnCheckpointService? checkpoints;
    private readonly ISessionStore? store;
    private readonly Action<SubAgentViewModel>? subAgentOpened;
    private readonly string providerModel;
    private readonly ObservableCollection<SubAgentViewModel> subAgents = [];
    private readonly ConcurrentDictionary<string, SubAgentViewModel> subAgentById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TokenUsage> agentUsage = new(StringComparer.Ordinal);
    private readonly ObservableCollection<SubAgentViewModel> finished = [];
    private readonly FinishedSubAgentsViewModel finishedMenu = new();
    private readonly FrameCoalescer<AgentEvent> coalescer;

    /// <summary>Text of a child agent that arrived before its <c>AgentSubAgentStarted</c> reached the dispatcher.</summary>
    private readonly ConcurrentDictionary<string, StringBuilder> earlyDeltas = new(StringComparer.Ordinal);
    private string draft = string.Empty;
    private bool isRunning;
    private bool subAgentsCollapsed;
    private CancellationTokenSource? turn;
    private int turnNumber;
    private DateTimeOffset turnStartedAt;
    private bool disposed;

    public SessionWorkspaceViewModel(
        SessionInfo session,
        SessionRuntime runtime,
        TurnCheckpointService? checkpoints,
        ISessionStore? store,
        ProviderSettings provider,
        Action<SubAgentViewModel>? subAgentOpened = null)
    {
        Info = session;
        this.runtime = runtime;
        this.checkpoints = checkpoints;
        this.store = store;
        this.subAgentOpened = subAgentOpened;
        providerModel = provider.Provider?.Model ?? string.Empty;
        Transcript = new TranscriptViewModel(collapseOlderTurns: false);
        coalescer = new FrameCoalescer<AgentEvent>(HandleOnUi, FlushAllPendingText);
        SubAgents = new ReadOnlyObservableCollection<SubAgentViewModel>(subAgents);
        Finished = new ReadOnlyObservableCollection<SubAgentViewModel>(finished);
        SendCommand = new AsyncRelayCommand(SendAsync, EvaluateCanSend, OnSendFailed);
        CancelCommand = new RelayCommand(Cancel, () => isRunning);
        ToggleSubAgentsCommand = new RelayCommand(() => SubAgentsCollapsed = !SubAgentsCollapsed);
        if (!provider.IsConfigured)
        {
            Transcript.AddNotice(Strings.ProviderMissing + provider.Status, isError: true);
        }
    }

    public SessionInfo Info { get; private set; }

    public string SessionId => Info.Id;

    public string Title => Info.Title;

    public string DocumentsGlyph => "◆";

    /// <summary>Distinct glyph prefix so session, file and changes tabs are told apart at a glance.</summary>
    public string TabTitle => DocumentsGlyph + " " + Info.Title;

    public string WorkDir => Info.WorkspacePath;

    public string WorkDirDisplay => Info.WorkspacePath;

    public string BranchText => string.IsNullOrEmpty(Info.Branch) ? string.Empty : Info.Branch;

    public TranscriptViewModel Transcript { get; }

    public ReadOnlyObservableCollection<SubAgentViewModel> SubAgents { get; }

    public ReadOnlyObservableCollection<SubAgentViewModel> Finished { get; }

    public FinishedSubAgentsViewModel FinishedMenu => finishedMenu;

    /// <summary>Re-binds the finished dropdown to this session after a start, a finish or a history load.</summary>
    public void SyncFinishedMenu() => finishedMenu.Bind(finished, OpenFinished);

    public AsyncRelayCommand SendCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand ToggleSubAgentsCommand { get; }

    public event EventHandler? TabTitleChanged;

    public event Action<SessionWorkspaceViewModel>? TurnCompleted;

    public event Action? SubAgentsChanged;

    /// <summary>Raised when the agent hierarchy or one agent's run state changed; the left panel rebuilds its agent rows.</summary>
    public event Action? AgentTreeChanged;

    public string Draft
    {
        get => draft;
        set
        {
            if (Set(ref draft, value))
            {
                RefreshCanSend();
            }
        }
    }

    public bool IsRunning
    {
        get => isRunning;
        private set
        {
            if (Set(ref isRunning, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                RefreshCanSend();
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsIdle => !isRunning;

    public bool CanSendNow => EvaluateCanSend();

    /// <summary>Set when the user collapses the sub-agent region; this session then never pops it up automatically.</summary>
    public bool SubAgentsCollapsed
    {
        get => subAgentsCollapsed;
        set
        {
            if (Set(ref subAgentsCollapsed, value))
            {
                OnPropertyChanged(nameof(SubAgentsVisible));
                OnPropertyChanged(nameof(SubAgentToggleText));
                SubAgentsChanged?.Invoke();
            }
        }
    }

    public bool HasSubAgents => subAgents.Count > 0;

    /// <summary>True while the region has something to show: a live sub-agent or a finished one in the dropdown.</summary>
    public bool SubAgentsVisible => (subAgents.Count > 0 || finished.Count > 0) && !subAgentsCollapsed;

    /// <summary>Anything to collapse or reopen: a live sub-agent or a finished one in the dropdown.</summary>
    public bool HasSubAgentActivity => subAgents.Count > 0 || finished.Count > 0;

    /// <summary>
    /// The agent hierarchy of this session as flat rows with parent links: the main agent, its sub-agents and their own
    /// children. A session without sub-agents has no agent rows.
    /// </summary>
    public IReadOnlyList<AgentNodeSnapshot> AgentSnapshots()
    {
        if (subAgents.Count == 0 && finished.Count == 0)
        {
            return [];
        }

        var snapshot = new List<AgentNodeSnapshot>(subAgents.Count + finished.Count + 1)
        {
            new("root", null, Strings.MainAgentTitle, Strings.MainAgentTitle, IsRunning ? Strings.SubAgentRunning : string.Empty, IsRunning, true),
        };
        foreach (var agent in subAgents)
        {
            snapshot.Add(SnapshotOf(agent));
        }

        foreach (var agent in finished)
        {
            snapshot.Add(SnapshotOf(agent));
        }

        return snapshot;
    }

    private static AgentNodeSnapshot SnapshotOf(SubAgentViewModel agent) => new(
        agent.AgentId,
        agent.ParentAgentId,
        agent.Kind,
        agent.Task,
        agent.StatusText,
        agent.IsLive,
        false);

    private void OnAgentStateChanged() => AgentTreeChanged?.Invoke();

    /// <summary>The toggle that collapses the region (and, collapsed, shows how many sub-agents are still running).</summary>
    public string SubAgentToggleText => Strings.SubAgentsToggle(subAgents.Count, subAgentsCollapsed);

    public string SubAgentToggleName => Strings.SubAgentsToggleName;

    public string SubAgentToggleTooltip => Strings.SubAgentsToggleTooltip;

    public void Rename(string title)
    {
        Info = Info with { Title = title };
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(TabTitle));
        TabTitleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Rebuilds the transcript and the finished sub-agent list from the session store, reading off the dispatcher.</summary>
    public async Task LoadHistoryAsync(CancellationToken ct)
    {
        if (store is null)
        {
            return;
        }

        try
        {
            var messages = await TranscriptReplay.ReadAsync(store, SessionId, ct).ConfigureAwait(true);
            TranscriptReplay.Apply(Transcript, messages);

            await RestoreSubAgentsAsync(SessionId, "root", ct).ConfigureAwait(true);

            SyncFinishedMenu();

            OnPropertyChanged(nameof(HasSubAgents));
            OnPropertyChanged(nameof(SubAgentsVisible));
            OnPropertyChanged(nameof(HasSubAgentActivity));
            OnPropertyChanged(nameof(SubAgentToggleText));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
        {
            Transcript.AddNotice(Strings.ReadFailed + exception.Message, isError: true);
        }
    }

    /// <summary>
    /// Rebuilds one level of the agent hierarchy and then descends into it: a stored child session's id is the parent
    /// link of its own children, and the children of this session hang under the main agent ("root"). Statuses come from
    /// what the store holds; when it holds nothing conclusive the row says it ended without claiming a result.
    /// </summary>
    private async Task RestoreSubAgentsAsync(string parentSessionId, string parentAgentId, CancellationToken ct)
    {
        var children = await BackgroundRead.RunAsync(token => runtime.QueryChildrenAsync(parentSessionId, token), ct)
            .ConfigureAwait(true);
        foreach (var child in children)
        {
            var status = await BackgroundRead.RunAsync(token => DeriveStoredStatusAsync(child.Id, token), ct)
                .ConfigureAwait(true);
            finished.Add(SubAgentViewModel.FromStored(SessionId, child, parentAgentId, status));
            await RestoreSubAgentsAsync(child.Id, child.Id, ct).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The store keeps no run status for a sub-agent session, so the last stored message decides: a cancelled tool result
    /// means stopped, a failed result or a tool call without its result means failed, a final text answer means completed.
    /// Anything else returns null, which the row shows as a neutral "ended" instead of a result it cannot know.
    /// </summary>
    private async Task<SubAgentStatus?> DeriveStoredStatusAsync(string sessionId, CancellationToken ct)
    {
        if (store is null)
        {
            return null;
        }

        var last = await store.LastSeqAsync(sessionId, ct).ConfigureAwait(false);
        if (last <= 0)
        {
            return null;
        }

        StoredMessage? tail = null;
        await foreach (var message in store.ReadAsync(sessionId, Math.Max(1, last - 3), last, ct).ConfigureAwait(false))
        {
            tail = message;
        }

        return tail is null ? null : DeriveStatus(tail.Message);
    }

    private static SubAgentStatus? DeriveStatus(ChatMessage message)
    {
        switch (message.Role)
        {
            case MessageRole.Tool:
                SubAgentStatus? seen = null;
                foreach (var block in message.Content)
                {
                    if (block is not ToolResultBlock result)
                    {
                        continue;
                    }

                    if (result.Content.TrimStart().StartsWith("Cancelled", StringComparison.OrdinalIgnoreCase))
                    {
                        return SubAgentStatus.Stopped;
                    }

                    if (result.IsError)
                    {
                        seen = SubAgentStatus.Failed;
                    }
                }

                return seen;
            case MessageRole.Assistant:
                var text = false;
                foreach (var block in message.Content)
                {
                    switch (block)
                    {
                        case ToolCallBlock:
                            return SubAgentStatus.Failed;
                        case TextBlock content when content.Text.Trim().Length > 0:
                            text = true;
                            break;
                    }
                }

                return text ? SubAgentStatus.Completed : null;
            default:
                return null;
        }
    }

    public void Tick()
    {
        Transcript.Tick();
        foreach (var agent in subAgents)
        {
            agent.Tick();
        }
    }

    public void Cancel()
    {
        try
        {
            turn?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void StopSubAgent(string agentId)
    {
        runtime.SubAgentControl?.Stop(agentId, Strings.SubAgentStoppedByUser);
    }

    /// <summary>
    /// Opens a finished sub-agent as a read-only tab. Its transcript is replayed from the child session store when the
    /// in-memory one is empty, so it survives a restart.
    /// </summary>
    public void OpenFinished(SubAgentViewModel agent)
    {
        agent.IsReadOnly = true;
        OpenSubAgent(agent);
        if (agent.Transcript.IsEmpty)
        {
            _ = ReplayFinishedAsync(agent);
        }
    }

    private async Task ReplayFinishedAsync(SubAgentViewModel agent)
    {
        if (store is null)
        {
            return;
        }

        try
        {
            var messages = await TranscriptReplay.ReadAsync(store, agent.ChildSessionId, CancellationToken.None).ConfigureAwait(true);
            if (messages.Count == 0)
            {
                agent.Transcript.AddNotice(Strings.FinishedAgentsNoTranscript, isError: true);
                return;
            }

            TranscriptReplay.Apply(agent.Transcript, messages);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
        {
            agent.Transcript.AddNotice(Strings.SubAgentReplayFailed(exception.Message), isError: true);
        }
    }

    private bool EvaluateCanSend() => !isRunning && !string.IsNullOrWhiteSpace(draft);

    private void RefreshCanSend()
    {
        OnPropertyChanged(nameof(CanSendNow));
        SendCommand.NotifyCanExecuteChanged();
    }

    private async Task SendAsync(CancellationToken ignored)
    {
        var text = draft.Trim();
        if (text.Length == 0)
        {
            return;
        }

        Draft = string.Empty;
        turnStartedAt = DateTimeOffset.Now;
        Transcript.AddUserMessage(text, turnStartedAt);
        var card = Transcript.BeginTurn(turnStartedAt);
        turnNumber = card.Round;
        IsRunning = true;

        turn = new CancellationTokenSource();
        var token = turn.Token;
        string? error = null;
        var turnNo = turnNumber;

        // Everything that can fail - the checkpoint read, the checkpoint begin and the turn itself - sits inside this
        // try, so a store or git failure can never leave the session marked as running.
        try
        {
            if (checkpoints is not null)
            {
                try
                {
                    var next = await BackgroundRead.RunAsync(t => checkpoints.NextTurnNumberAsync(SessionId, t), CancellationToken.None)
                        .ConfigureAwait(true);
                    turnNo = Math.Max(turnNo, next);
                    turnNumber = turnNo;
                    await Task.Run(() => checkpoints.BeginAsync(SessionId, turnNo, WorkDir, CancellationToken.None), CancellationToken.None)
                        .ConfigureAwait(true);
                }
                catch (Exception exception) when (IsCheckpointFailure(exception))
                {
                    Transcript.AddNotice(Strings.TurnCheckpointFailed(exception.Message), isError: true);
                }
            }

            await runtime.RunTurnAsync(text, OnAgentEvent, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            error = Strings.Stopped;
        }
        catch (Exception exception) when (IsCheckpointFailure(exception) || exception is ProviderException)
        {
            error = exception.Message;
        }
        finally
        {
            try
            {
                Transcript.EndTurn(DateTimeOffset.Now - turnStartedAt, error);
                if (checkpoints is not null)
                {
                    try
                    {
                        await Task.Run(() => checkpoints.CompleteAsync(SessionId, turnNo, WorkDir, CancellationToken.None), CancellationToken.None)
                            .ConfigureAwait(true);
                    }
                    catch (Exception exception) when (IsCheckpointFailure(exception))
                    {
                        Transcript.AddNotice(Strings.TurnCheckpointFailed(exception.Message), isError: true);
                    }
                }
            }
            finally
            {
                this.turn?.Dispose();
                this.turn = null;
                IsRunning = false;
                TurnCompleted?.Invoke(this);
            }
        }
    }

    /// <summary>Failures of the git snapshot or of the checkpoint store; the turn continues and the card says so.</summary>
    private static bool IsCheckpointFailure(Exception exception) =>
        exception is GitException or InvalidOperationException or IOException or InvalidDataException or ArgumentException or
        DbException or UnauthorizedAccessException;

    private void OnSendFailed(Exception exception) =>
        Transcript.AddNotice(Strings.TurnFailed + exception.Message, isError: true);

    /// <summary>Worker-thread event sink; text is merged off-thread, everything else is queued to the frame coalescer.</summary>
    private void OnAgentEvent(AgentEvent value)
    {
        switch (value)
        {
            case AgentTextDelta delta:
                RouteDelta(delta);
                break;
            case AgentThinkingDelta:
                break;
            default:
                coalescer.Enqueue(value);
                break;
        }
    }

    /// <summary>
    /// Text of an agent whose tab does not exist yet is parked here: a child's first deltas can overtake the
    /// <c>AgentSubAgentStarted</c> that is still queued for the dispatcher, and dropping them loses the start of the answer.
    /// </summary>
    private void RouteDelta(AgentTextDelta delta)
    {
        if (Target(delta.AgentId) is { } target)
        {
            target.QueueDelta(delta.Text);
            coalescer.RequestFlush();
            return;
        }

        var buffer = earlyDeltas.GetOrAdd(delta.AgentId, static _ => new StringBuilder());
        lock (buffer)
        {
            if (buffer.Length < EarlyDeltaLimit)
            {
                buffer.Append(delta.Text, 0, Math.Min(delta.Text.Length, EarlyDeltaLimit - buffer.Length));
            }
        }
    }

    /// <summary>Moves the parked text of a starting agent into its transcript.</summary>
    private void DrainEarlyDeltas(SubAgentViewModel agent)
    {
        if (!earlyDeltas.TryRemove(agent.AgentId, out var buffer))
        {
            return;
        }

        lock (buffer)
        {
            if (buffer.Length > 0)
            {
                agent.Transcript.QueueDelta(buffer.ToString());
                agent.Transcript.FlushPending();
            }
        }
    }

    private TranscriptViewModel? Target(string agentId) =>
        string.Equals(agentId, "root", StringComparison.Ordinal)
            ? Transcript
            : subAgentById.TryGetValue(agentId, out var agent) ? agent.Transcript : null;

    /// <summary>Drains the merged text of every transcript; runs once per coalesced flush and before each event.</summary>
    private void FlushAllPendingText()
    {
        Transcript.FlushPending();
        foreach (var agent in subAgents)
        {
            agent.Transcript.FlushPending();
        }
    }

    private void HandleOnUi(AgentEvent value)
    {
        // Text that is still coalesced belongs above the row this event is about to add.
        FlushAllPendingText();

        switch (value)
        {
            case AgentSubAgentStarted started:
                StartSubAgent(started);
                break;
            case AgentSubAgentQueued queued:
                Find(queued.AgentId)?.MarkQueued();
                OnAgentStateChanged();
                break;
            case AgentSubAgentRunning running:
                Find(running.AgentId)?.MarkRunning(running.At);
                OnAgentStateChanged();
                break;
            case AgentSubAgentSteered steered:
                Find(steered.AgentId)?.MarkSteered(steered.Instruction, steered.DroppedToolCalls);
                OnAgentStateChanged();
                break;
            case AgentSubAgentFollowUpQueued followUp:
                Find(followUp.AgentId)?.MarkFollowUp(followUp.Instruction, followUp.Pending);
                OnAgentStateChanged();
                break;
            case AgentSubAgentFinished subFinished:
                FinishSubAgent(subFinished);
                break;
            case AgentToolStarted started:
                Card(started.AgentId)?.AddTool(started.Call);
                break;
            case AgentToolFinished finishedTool:
                Card(finishedTool.AgentId)?.CompleteTool(finishedTool.Call, finishedTool.Result);
                break;
            case AgentTurnCompleted completed:
                AddTurnUsage(completed);
                break;
            case AgentApprovalDenied denied:
                Card(denied.AgentId)?.AddNotice(Strings.Denied(denied.Call.Name), isError: true);
                break;
            case AgentCompacted compacted:
                Card(compacted.AgentId)?.AddNotice(Strings.Compacted(compacted.TokensBefore, compacted.TokensAfter));
                break;
            case AgentFailed failed:
                Card(failed.AgentId)?.AddNotice(failed.Error, isError: true);
                break;
        }
    }

    /// <summary>
    /// <see cref="AgentTurnCompleted"/> carries the usage accumulated by that agent instance over all its turns, so the
    /// card gets the difference to the previous turn; a provider that reported nothing leaves the card showing "—".
    /// </summary>
    private void AddTurnUsage(AgentTurnCompleted completed)
    {
        var previous = agentUsage.TryGetValue(completed.AgentId, out var stored) ? stored : TokenUsage.Empty;
        agentUsage[completed.AgentId] = completed.Usage;
        var delta = new TokenUsage(
            Math.Max(0, completed.Usage.InputTokens - previous.InputTokens),
            Math.Max(0, completed.Usage.OutputTokens - previous.OutputTokens));
        if (delta.InputTokens == 0 && delta.OutputTokens == 0)
        {
            return;
        }

        Card(completed.AgentId)?.AddUsage(delta, completed.Stop);
    }

    private TurnCardViewModel? Card(string agentId)
    {
        if (string.Equals(agentId, "root", StringComparison.Ordinal))
        {
            return Transcript.Current ?? Transcript.BeginTurn(DateTimeOffset.Now);
        }

        var agent = Find(agentId);
        return agent is null ? null : agent.Transcript.Current ?? agent.Transcript.BeginTurn(DateTimeOffset.Now);
    }

    private SubAgentViewModel? Find(string agentId) =>
        subAgentById.TryGetValue(agentId, out var agent) ? agent : null;

    private void StartSubAgent(AgentSubAgentStarted started)
    {
        if (subAgentById.ContainsKey(started.ChildId))
        {
            return;
        }

        var model = string.IsNullOrWhiteSpace(started.Model) || string.Equals(started.Model, "default", StringComparison.Ordinal)
            ? providerModel
            : started.Model;
        var agent = new SubAgentViewModel(
            SessionId,
            started.ChildId,
            started.ChildSessionId,
            started.Kind,
            started.Task,
            model,
            StartSubAgentStop,
            started.AgentId);
        subAgentById[started.ChildId] = agent;
        subAgents.Add(agent);
        DrainEarlyDeltas(agent);
        OnPropertyChanged(nameof(HasSubAgents));
        OnPropertyChanged(nameof(SubAgentsVisible));
        OnPropertyChanged(nameof(HasSubAgentActivity));
        OnPropertyChanged(nameof(SubAgentToggleText));
        SubAgentsChanged?.Invoke();
        OnAgentStateChanged();
        OpenSubAgent(agent);
    }

    private void StartSubAgentStop(string agentId) => StopSubAgent(agentId);

    /// <summary>Opens a sub-agent tab; a live one only pops the region when the user has not collapsed it for this session.</summary>
    private void OpenSubAgent(SubAgentViewModel agent)
    {
        if (agent.IsReadOnly || !SubAgentsCollapsed)
        {
            subAgentOpened?.Invoke(agent);
        }
    }

    private void FinishSubAgent(AgentSubAgentFinished finishedEvent)
    {
        if (Find(finishedEvent.AgentId) is not { } agent)
        {
            return;
        }

        // The terminal event carries this run's usage even when the child's last turn reported none.
        var previous = agentUsage.TryGetValue(finishedEvent.AgentId, out var stored) ? stored : TokenUsage.Empty;
        var delta = new TokenUsage(
            Math.Max(0, finishedEvent.Usage.InputTokens - previous.InputTokens),
            Math.Max(0, finishedEvent.Usage.OutputTokens - previous.OutputTokens));
        if (delta.InputTokens > 0 || delta.OutputTokens > 0)
        {
            agent.Transcript.Current?.AddUsage(delta, StopReason.EndTurn);
        }

        agent.ApplyUsageIfMissing(finishedEvent.Usage);
        agent.Finish(finishedEvent.Status, finishedEvent.Usage, finishedEvent.Elapsed, finishedEvent.Error);
        subAgentById.TryRemove(finishedEvent.AgentId, out _);
        agentUsage.Remove(finishedEvent.AgentId);
        subAgents.Remove(agent);
        finished.Add(agent);
        SyncFinishedMenu();
        OnPropertyChanged(nameof(HasSubAgents));
        OnPropertyChanged(nameof(SubAgentsVisible));
        OnPropertyChanged(nameof(HasSubAgentActivity));
        OnPropertyChanged(nameof(SubAgentToggleText));
        SubAgentsChanged?.Invoke();
        OnAgentStateChanged();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        coalescer.Dispose();
        Cancel();
        turn?.Dispose();
        turn = null;
        Trace.WriteLine("[session] closed tab " + SessionId);
    }
}

/// <summary>
/// One sub-agent of a session: its status line, its elapsed time and tokens, and its read-only transcript. There is no
/// input box - steering, queueing and stopping belong to the main agent (plus the user's single Stop button).
/// </summary>
internal sealed class SubAgentViewModel : ObservableObject
{
    private const int TabTaskLimit = 18;

    private readonly Action<string>? stopRequested;
    private SubAgentStatus status = SubAgentStatus.Queued;
    private DateTimeOffset startedAt;
    private TimeSpan elapsed;
    private TokenUsage usage = TokenUsage.Empty;
    private bool sawUsage;
    private string? error;
    private string statusNote = Strings.SubAgentWaitingSlot;
    private bool isReadOnly;
    private bool isStopRequested;
    private bool outcomeUnknown;

    public SubAgentViewModel(
        string sessionId,
        string agentId,
        string childSessionId,
        string kind,
        string task,
        string model,
        Action<string>? stopRequested,
        string? parentAgentId = null)
    {
        SessionId = sessionId;
        AgentId = agentId;
        ChildSessionId = childSessionId;
        ParentAgentId = parentAgentId;
        Kind = kind;
        Task = task;
        Model = model;
        this.stopRequested = stopRequested;
        Transcript = new TranscriptViewModel(collapseOlderTurns: true);
        StopCommand = new RelayCommand(RequestStop, () => IsLive && !isStopRequested);
    }

    /// <summary>The main session this sub-agent belongs to; its tab never shows under another session.</summary>
    public string SessionId { get; }

    public string AgentId { get; }

    /// <summary>The session this sub-agent's own messages are stored under; the run id and the session id differ.</summary>
    public string ChildSessionId { get; }

    /// <summary>The agent that spawned this one; null for a child that was spawned by the root agent.</summary>
    public string? ParentAgentId { get; }

    public string Kind { get; }

    public string Task { get; }

    public string Model { get; }

    public string ModelText => Model.Length == 0 ? Strings.NoModel : Model;

    public string TaskSummary => ToolRowViewModel.Compact(Task, 120);

    public TranscriptViewModel Transcript { get; }

    public RelayCommand StopCommand { get; }

    public string TabTitle => (IsReadOnly ? "○ " : "▸ ") + Kind + " · " + ToolRowViewModel.Compact(Task, TabTaskLimit);

    public bool IsReadOnly
    {
        get => isReadOnly;
        set
        {
            if (Set(ref isReadOnly, value))
            {
                OnPropertyChanged(nameof(TabTitle));
                OnPropertyChanged(nameof(ReadOnlyBadgeVisible));
            }
        }
    }

    public bool ReadOnlyBadgeVisible => isReadOnly;

    public string ReadOnlyBadgeText => Strings.ReadOnlyBadge;

    public SubAgentStatus Status
    {
        get => status;
        private set
        {
            if (Set(ref status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(IsLive));
                StopCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusText => outcomeUnknown
        ? Strings.SubAgentEnded
        : status switch
        {
            SubAgentStatus.Queued => Strings.SubAgentQueued,
            SubAgentStatus.Running => Strings.SubAgentRunning,
            SubAgentStatus.Completed => Strings.SubAgentCompleted,
            SubAgentStatus.Stopped => Strings.SubAgentStopped,
            _ => Strings.SubAgentFailed,
        };

    public bool IsFinished => outcomeUnknown || status is SubAgentStatus.Completed or SubAgentStatus.Stopped or SubAgentStatus.Failed;

    public bool IsLive => !IsFinished;

    public string StatusNote
    {
        get => statusNote;
        private set
        {
            if (Set(ref statusNote, value))
            {
                OnPropertyChanged(nameof(HasStatusNote));
            }
        }
    }

    public bool HasStatusNote => statusNote.Length > 0;

    public TokenUsage Usage => usage;

    /// <summary>"模型 · 输入 N · 输出 M"; an unreported side is "—".</summary>
    public string UsageText => ModelText + " · " +
        Strings.TokenSummary(sawUsage ? usage.InputTokens : null, sawUsage ? usage.OutputTokens : null);

    public string ElapsedText =>
        IsFinished || status == SubAgentStatus.Running ? Strings.FormatElapsed(elapsed) : Strings.None;

    public string? Error
    {
        get => error;
        private set => Set(ref error, value);
    }

    public void MarkQueued()
    {
        Status = SubAgentStatus.Queued;
        StatusNote = Strings.SubAgentWaitingSlot;
    }

    /// <summary>Clears the note: the status text itself already shows the running state.</summary>
    public void MarkRunning(DateTimeOffset at)
    {
        startedAt = at;
        Status = SubAgentStatus.Running;
        StatusNote = string.Empty;
    }

    public void MarkSteered(string instruction, int droppedToolCalls)
    {
        StatusNote = Strings.SubAgentSteeredNote(droppedToolCalls);
        Transcript.AddNotice(Strings.SteeredNotice(ToolRowViewModel.Compact(instruction, 160)));
    }

    public void MarkFollowUp(string instruction, int pending)
    {
        StatusNote = Strings.SubAgentFollowUpNote(pending);
        Transcript.AddNotice(Strings.FollowUpNotice(ToolRowViewModel.Compact(instruction, 160)));
    }

    public void ApplyUsageIfMissing(TokenUsage value)
    {
        if (sawUsage || (value.InputTokens == 0 && value.OutputTokens == 0))
        {
            return;
        }

        usage = value;
        sawUsage = true;
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(ElapsedText));
    }

    public void Finish(SubAgentStatus finalStatus, TokenUsage finalUsage, TimeSpan finalElapsed, string? finalError)
    {
        Status = finalStatus;
        if (finalUsage.InputTokens > 0 || finalUsage.OutputTokens > 0)
        {
            usage = finalUsage;
            sawUsage = true;
        }
        else if (!sawUsage)
        {
            usage = finalUsage;
            sawUsage = finalUsage.InputTokens > 0 || finalUsage.OutputTokens > 0;
        }

        elapsed = finalElapsed;
        Error = finalError;
        StatusNote = finalError ?? string.Empty;
        Transcript.EndTurn(finalElapsed, finalError);
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(HasStatusNote));
        StopCommand.NotifyCanExecuteChanged();
    }

    public void Tick()
    {
        if (IsFinished)
        {
            return;
        }

        if (status == SubAgentStatus.Running && startedAt != default)
        {
            elapsed = DateTimeOffset.Now - startedAt;
            OnPropertyChanged(nameof(ElapsedText));
        }

        Transcript.Tick();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(TabTitle));
    }

    /// <summary>
    /// A sub-agent replayed from the store. The parent link is the agent row it hangs under ("root" for a direct child
    /// of the main session, otherwise the id of the ancestor's session). A status the store cannot prove is left unknown,
    /// which reads as a neutral "ended" instead of an invented "completed".
    /// </summary>
    public static SubAgentViewModel FromStored(string sessionId, SessionInfo session, string parentAgentId, SubAgentStatus? status)
    {
        var agent = new SubAgentViewModel(
            sessionId,
            session.Id,
            session.Id,
            session.AgentKind ?? Strings.SubAgentsTitle,
            session.Task ?? session.Title,
            string.Empty,
            stopRequested: null,
            parentAgentId)
        {
            IsReadOnly = true,
        };
        if (status is { } known)
        {
            agent.Finish(known, TokenUsage.Empty, TimeSpan.Zero, null);
        }
        else
        {
            agent.MarkEndedUnknown();
        }

        return agent;
    }

    /// <summary>Ended, with no stored evidence of how; the row then says only that it ended.</summary>
    private void MarkEndedUnknown()
    {
        outcomeUnknown = true;
        StatusNote = string.Empty;
        Transcript.EndTurn(null, null);
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(ElapsedText));
        StopCommand.NotifyCanExecuteChanged();
    }

    public override string ToString() => TabTitle;

    private void RequestStop()
    {
        isStopRequested = true;
        StatusNote = Strings.SubAgentStopRequested;
        StopCommand.NotifyCanExecuteChanged();
        stopRequested?.Invoke(AgentId);
    }
}

/// <summary>
/// The menu at the end of the bottom group's title bar that lists finished sub-agents in finish order. The title bar shows
/// it as a compact button with the item count; a kept item opens its read-only tab.
/// </summary>
internal sealed class FinishedSubAgentsViewModel : ObservableObject
{
    private SubAgentViewModel? selected;
    private Action<SubAgentViewModel>? open;

    public ObservableCollection<SubAgentViewModel> Items { get; } = [];

    public string Title => Strings.FinishedAgentsMenu(Items.Count);

    public string Hint => Strings.FinishedAgentsHint;

    public SubAgentViewModel? Selected
    {
        get => selected;
        set
        {
            if (Set(ref selected, value) && value is not null)
            {
                open?.Invoke(value);
                Dispatcher.UIThread.Post(() =>
                {
                    if (Set(ref selected, null, nameof(Selected)))
                    {
                        OnPropertyChanged(nameof(HasSelection));
                    }
                });
            }

            OnPropertyChanged(nameof(HasSelection));
        }
    }

    /// <summary>Resets after opening, so the same entry can be chosen again.</summary>
    public bool HasSelection => selected is not null;

    /// <summary>Also enables the title-bar button: an empty menu has nothing to list.</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>Points the menu at one session's finished sub-agents; choosing one opens its read-only tab.</summary>
    public void Bind(IEnumerable<SubAgentViewModel> items, Action<SubAgentViewModel> openAction)
    {
        open = openAction;
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Title));
        Selected = null;
    }
}
