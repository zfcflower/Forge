using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.Core.Agents;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>
/// One open main session: its transcript, its sub-agent tabs, the turn loop and the turn checkpoints. All agent events
/// are routed by <see cref="AgentEvent.AgentId"/>, so a child's text never reaches the parent's bubble.
/// <para>This is the long-lived owner of everything the session shows, so it is also the one subscriber of
/// <see cref="Loc.LanguageChanged"/>: the cards, tool rows, notices and sub-agent labels re-derive their texts when the
/// language changes and are told to re-raise them from here, and the subscription ends with <see cref="Dispose"/>.</para>
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
    private readonly ObservableCollection<QueuedMessageViewModel> queued = [];
    private string draft = string.Empty;
    private bool isRunning;

    /// <summary>The message a redirect sends once the cancelled turn has wound down.</summary>
    private string? redirectText;

    /// <summary>Set by Stop: the queue waits until the user sends again.</summary>
    private bool queuePaused;
    private readonly ConditionalWeakTable<QueuedMessageViewModel, Action> reviewWithdrawn = new();

    /// <summary>The root agent is rebuilt for the next turn, so its accumulated usage starts again from zero.</summary>
    private bool rootRebuilt;
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
        Action<SubAgentViewModel>? subAgentOpened = null,
        ComposerPreferences? composerPreferences = null,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? fileSource = null,
        Func<IReadOnlyList<SlashEntry>>? skillSource = null,
        ISessionInstrumentSources? instrumentSources = null)
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
        QueueCommand = new RelayCommand(Queue, CanSendWhileRunning);
        InterjectCommand = new RelayCommand(Interject, CanSendWhileRunning);
        RedirectCommand = new RelayCommand(Redirect, CanSendWhileRunning);
        Queued = new ReadOnlyObservableCollection<QueuedMessageViewModel>(queued);
        Composer = new ComposerViewModel(
            provider,
            ApplyRootDefinition,
            composerPreferences ?? ComposerPreferences.Shared,
            session.WorkspacePath,
            () => ProviderSettings.Load(AppPaths.Create().SettingsPath),
            fileSource,
            skillSource);
        ToggleSubAgentsCommand = new RelayCommand(() => SubAgentsCollapsed = !SubAgentsCollapsed);
        Instruments = new SessionInstrumentsViewModel(
            session,
            instrumentSources ?? EmptySessionInstrumentSources.Instance,
            runtime.ToolNames,
            () => Composer.Model);
        if (!provider.IsConfigured)
        {
            Transcript.AddNotice(SessionStrings.ProviderMissing(provider.Status), isError: true);
        }

        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public SessionInfo Info { get; private set; }

    public string SessionId => Info.Id;

    public string Title => Info.Title;

    /// <summary>
    /// The tab's label. The session tab's marker is not part of it: the tab strip draws that one as a chat bubble icon for
    /// session tabs, so the title stays plain text for tooltips, automation and window titles.
    /// </summary>
    public string TabTitle => Info.Title;

    public string WorkDir => Info.WorkspacePath;

    public string BranchText => string.IsNullOrEmpty(Info.Branch) ? string.Empty : Info.Branch;

    public TranscriptViewModel Transcript { get; }

    public ReadOnlyObservableCollection<SubAgentViewModel> SubAgents { get; }

    public ReadOnlyObservableCollection<SubAgentViewModel> Finished { get; }

    public FinishedSubAgentsViewModel FinishedMenu => finishedMenu;

    /// <summary>Re-binds the finished dropdown to this session after a start, a finish or a history load.</summary>
    public void SyncFinishedMenu() => finishedMenu.Bind(finished, OpenFinished);

    public AsyncRelayCommand SendCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>While a turn runs: keep the draft in the queue; it is sent when the turn (and the ones before it) ends.</summary>
    public RelayCommand QueueCommand { get; }

    /// <summary>While a turn runs: hand the draft to the running agent at its next tool batch, else put it first in the queue.</summary>
    public RelayCommand InterjectCommand { get; }

    /// <summary>While a turn runs: stop it and send the draft right after.</summary>
    public RelayCommand RedirectCommand { get; }

    /// <summary>Messages waiting for the running turn: interjections not yet delivered, then the queue in sending order.</summary>
    public ReadOnlyObservableCollection<QueuedMessageViewModel> Queued { get; }

    public bool HasQueued => queued.Count > 0;

    /// <summary>True after Stop while messages wait: the queue resumes with the next send.</summary>
    public bool QueuePaused => queuePaused && queued.Count > 0 && !isRunning;

    public ComposerViewModel Composer { get; }

    /// <summary>The readout above the input: usage, the four counters, the project and the git markers.</summary>
    public SessionInstrumentsViewModel Instruments { get; }

    /// <summary>Names of the tools this session can call; empty until its first turn built the agent host.</summary>
    internal IReadOnlyList<string> ToolNames() => runtime.ToolNames();

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
            new(
                "root",
                null,
                SessionStrings.MainAgentTitle,
                SessionStrings.MainAgentTitle,
                IsRunning ? SessionStrings.SubAgentRunning : string.Empty,
                IsRunning,
                true),
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

    /// <summary>The chip's label: just the noun. The count is the badge's job, so the label never repeats a number.</summary>
    public string SubAgentLabel => SessionStrings.SubAgentsLabel;

    /// <summary>
    /// Sub-agents that hold a run slot right now. A queued child is waiting for one and a finished one has left the live
    /// list, so neither is counted: the badge answers "how many are working", not "how many exist".
    /// </summary>
    public int RunningSubAgentCount
    {
        get
        {
            var running = 0;
            foreach (var agent in subAgents)
            {
                if (agent.Status == SubAgentStatus.Running)
                {
                    running++;
                }
            }

            return running;
        }
    }

    /// <summary>The badge is drawn only while something runs; an idle session shows the bare label.</summary>
    public bool HasRunningSubAgents => RunningSubAgentCount > 0;

    /// <summary>
    /// The chip's accessible name. Assistive technology replaces the button's content with this name, so the badge's number
    /// has to appear here or the count would be invisible to it.
    /// </summary>
    public string SubAgentToggleName => RunningSubAgentCount > 0
        ? SessionStrings.SubAgentsRunningName(RunningSubAgentCount)
        : SessionStrings.SubAgentsToggleName;

    public string SubAgentToggleTooltip => SessionStrings.SubAgentsToggleTooltip;

    /// <summary>
    /// Re-raises the chip's badge reading. A status change is not a layout change, so this deliberately does not touch
    /// <see cref="SubAgentsChanged"/>: a queued child acquiring a slot must not rebuild the bottom group.
    /// </summary>
    private void RaiseSubAgentCounters()
    {
        OnPropertyChanged(nameof(RunningSubAgentCount));
        OnPropertyChanged(nameof(HasRunningSubAgents));
        OnPropertyChanged(nameof(SubAgentToggleName));
    }

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
            Instruments.SeedHistory(TranscriptReplay.Apply(Transcript, messages));

            await RestoreSubAgentsAsync(SessionId, "root", ct).ConfigureAwait(true);

            SyncFinishedMenu();

            OnPropertyChanged(nameof(HasSubAgents));
            OnPropertyChanged(nameof(SubAgentsVisible));
            OnPropertyChanged(nameof(HasSubAgentActivity));
            RaiseSubAgentCounters();

            // The replayed roster is what the project tree draws its agent rows (and the session's chevron) from, so the
            // owners have to hear about it: without this a restored session kept no agent rows until something else
            // refreshed the whole tree.
            SubAgentsChanged?.Invoke();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
        {
            Transcript.AddNotice(SessionStrings.ReadFailed(exception.Message), isError: true);
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

    /// <summary>Stops the running turn; queued messages stay and wait for the next send.</summary>
    public void Cancel()
    {
        queuePaused = true;
        redirectText = null;
        CancelTurn();
        RefreshCanSend();
    }

    private void CancelTurn()
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
        runtime.SubAgentControl?.Stop(agentId, SessionStrings.SubAgentStoppedByUser);
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
                agent.Transcript.AddNotice(SessionStrings.FinishedAgentsNoTranscript, isError: true);
                return;
            }

            TranscriptReplay.Apply(agent.Transcript, messages);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
        {
            agent.Transcript.AddNotice(SessionStrings.SubAgentReplayFailed(exception.Message), isError: true);
        }
    }

    /// <summary>Idle with a draft to send, or with queued messages waiting (an empty send resumes the queue).</summary>
    private bool EvaluateCanSend() => !isRunning && (!string.IsNullOrWhiteSpace(draft) || HasSendableQueue());

    private bool HasSendableQueue() => queued.Any(message => !message.IsInterjection);

    private bool CanSendWhileRunning() => isRunning && !string.IsNullOrWhiteSpace(draft);

    private void RefreshCanSend()
    {
        OnPropertyChanged(nameof(CanSendNow));
        OnPropertyChanged(nameof(QueuePaused));
        SendCommand.NotifyCanExecuteChanged();
        QueueCommand.NotifyCanExecuteChanged();
        InterjectCommand.NotifyCanExecuteChanged();
        RedirectCommand.NotifyCanExecuteChanged();
    }

    private async Task SendAsync(CancellationToken ignored)
    {
        var text = draft.Trim();
        if (text.Length > 0)
        {
            Draft = string.Empty;
        }
        else if (TakeQueued() is { } next)
        {
            text = next;
        }
        else
        {
            return;
        }

        queuePaused = false;
        Instruments.NoteDraft(text);
        await RunTurnAsync(text).ConfigureAwait(true);
    }

    private void Queue()
    {
        var text = draft.Trim();
        if (text.Length == 0)
        {
            return;
        }

        Draft = string.Empty;
        AddQueued(new QueuedMessageViewModel(text, isInterjection: false, RemoveQueued), queued.Count);
        if (!isRunning)
        {
            ScheduleNext();
        }
    }

    public bool TryQueueReview(string text, Action? withdrawn = null)
    {
        if (disposed || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var message = new QueuedMessageViewModel(text.Trim(), isInterjection: false, RemoveQueuedReview);
        if (withdrawn is not null)
        {
            reviewWithdrawn.Add(message, withdrawn);
        }
        AddQueued(message, queued.Count);
        if (!isRunning)
        {
            ScheduleNext();
        }

        return true;
    }

    private void Interject()
    {
        var text = draft.Trim();
        if (text.Length == 0)
        {
            return;
        }

        Draft = string.Empty;
        var delivered = isRunning && runtime.TryInterject(text);
        var front = queued.TakeWhile(message => message.IsInterjection).Count();
        AddQueued(new QueuedMessageViewModel(text, delivered, RemoveQueued), delivered ? front : 0);
        if (!isRunning)
        {
            ScheduleNext();
        }
    }

    private void Redirect()
    {
        var text = draft.Trim();
        if (text.Length == 0)
        {
            return;
        }

        Draft = string.Empty;
        if (!isRunning)
        {
            queuePaused = false;
            AddQueued(new QueuedMessageViewModel(text, isInterjection: false, RemoveQueued), 0);
            ScheduleNext();
            return;
        }

        redirectText = text;
        CancelTurn();
    }

    private void AddQueued(QueuedMessageViewModel message, int index)
    {
        queued.Insert(Math.Clamp(index, 0, queued.Count), message);
        OnQueueChanged();
    }

    private void RemoveQueued(QueuedMessageViewModel message)
    {
        if (queued.Remove(message))
        {
            OnQueueChanged();
        }
    }

    private void RemoveQueuedReview(QueuedMessageViewModel message)
    {
        if (queued.Remove(message))
        {
            OnQueueChanged();
            NotifyWithdrawn(message);
        }
    }

    private void NotifyWithdrawn(QueuedMessageViewModel message)
    {
        if (reviewWithdrawn.TryGetValue(message, out var withdrawn))
        {
            reviewWithdrawn.Remove(message);
            withdrawn();
        }
    }

    private void OnQueueChanged()
    {
        OnPropertyChanged(nameof(HasQueued));
        RefreshCanSend();
    }

    /// <summary>Removes and returns the first queued (not interjected) message.</summary>
    private string? TakeQueued()
    {
        var next = queued.FirstOrDefault(message => !message.IsInterjection);
        if (next is null)
        {
            return null;
        }

        queued.Remove(next);
        reviewWithdrawn.Remove(next);
        OnQueueChanged();
        return next.Text;
    }

    /// <summary>
    /// After a turn: interjections the agent never reached go back to the front of the queue, then the redirect message
    /// or the head of the queue is sent on the next dispatcher pass (unless Stop paused the queue).
    /// </summary>
    private void ScheduleNext()
    {
        if (!isRunning)
        {
            var undelivered = runtime.TakeUndeliveredInterjections();
            foreach (var leftover in queued.Where(message => message.IsInterjection).ToList())
            {
                queued.Remove(leftover);
            }

            for (var i = undelivered.Count - 1; i >= 0; i--)
            {
                queued.Insert(0, new QueuedMessageViewModel(undelivered[i], isInterjection: false, RemoveQueued));
            }

            OnQueueChanged();
        }

        if (disposed || isRunning)
        {
            return;
        }

        var redirect = redirectText;
        redirectText = null;
        if (redirect is null && (queuePaused || !HasSendableQueue()))
        {
            return;
        }

        Dispatcher.UIThread.Post(() => _ = RunNextAsync(redirect));
    }

    private async Task RunNextAsync(string? redirect)
    {
        if (disposed)
        {
            return;
        }

        if (isRunning)
        {
            // Something else started in between: the redirect waits in the queue like any other message.
            if (redirect is not null)
            {
                AddQueued(new QueuedMessageViewModel(redirect, isInterjection: false, RemoveQueued), 0);
            }

            return;
        }

        var text = redirect ?? (queuePaused ? null : TakeQueued());
        if (text is null)
        {
            return;
        }

        try
        {
            await RunTurnAsync(text).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            OnSendFailed(exception);
        }
    }

    private void ApplyRootDefinition(AgentDefinition? definition)
    {
        if (runtime.SetRootDefinition(definition))
        {
            rootRebuilt = true;
        }
    }

    private async Task RunTurnAsync(string text)
    {
        if (rootRebuilt)
        {
            rootRebuilt = false;
            agentUsage.Remove("root");
        }

        turnStartedAt = DateTimeOffset.Now;
        Instruments.RefreshCounters();
        Instruments.RefreshSources();
        Transcript.AddUserMessage(text, turnStartedAt);
        var card = Transcript.BeginTurn(turnStartedAt);
        turnNumber = card.Round;
        IsRunning = true;

        turn = new CancellationTokenSource();
        var token = turn.Token;
        LocText? error = null;
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
                    Transcript.AddNotice(SessionStrings.CheckpointFailed(exception.Message), isError: true);
                }
            }

            await runtime.RunTurnAsync(text, OnAgentEvent, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            error = SessionStrings.Stopped;
        }
        catch (Exception exception) when (IsCheckpointFailure(exception) || exception is ProviderException)
        {
            error = LocText.Literal(exception.Message);
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
                        Transcript.AddNotice(SessionStrings.CheckpointFailed(exception.Message), isError: true);
                    }
                }
            }
            finally
            {
                this.turn?.Dispose();
                this.turn = null;
                IsRunning = false;
                TurnCompleted?.Invoke(this);
                ScheduleNext();
            }
        }
    }

    /// <summary>Failures of the git snapshot or of the checkpoint store; the turn continues and the card says so.</summary>
    private static bool IsCheckpointFailure(Exception exception) =>
        exception is GitException or InvalidOperationException or IOException or InvalidDataException or ArgumentException or
        DbException or UnauthorizedAccessException;

    private void OnSendFailed(Exception exception) =>
        Transcript.AddNotice(SessionStrings.TurnFailed(exception.Message), isError: true);

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
                RaiseSubAgentCounters();
                OnAgentStateChanged();
                break;
            case AgentSubAgentRunning running:
                Find(running.AgentId)?.MarkRunning(running.At);
                RaiseSubAgentCounters();
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
                Instruments.NoteToolUsed(started.Call.Name);
                Card(started.AgentId)?.AddTool(started.Call);
                break;
            case AgentToolFinished finishedTool:
                Card(finishedTool.AgentId)?.CompleteTool(finishedTool.Call, finishedTool.Result);
                break;
            case AgentTurnCompleted completed:
                AddTurnUsage(completed);
                break;
            case AgentApprovalDenied denied:
                Card(denied.AgentId)?.AddNotice(SessionStrings.Denied(denied.Call.Name), isError: true);
                break;
            case AgentCompacted compacted:
                Card(compacted.AgentId)?.AddNotice(SessionStrings.Compacted(compacted.TokensBefore, compacted.TokensAfter));
                break;
            case AgentInterjected interjected:
                OnInterjected(interjected);
                break;
            case AgentFailed failed:
                Card(failed.AgentId)?.AddNotice(LocText.Literal(failed.Error), isError: true);
                break;
        }
    }

    private void OnInterjected(AgentInterjected interjected)
    {
        var chip = queued.FirstOrDefault(message =>
            message.IsInterjection && string.Equals(message.Text, interjected.Text, StringComparison.Ordinal));
        if (chip is not null)
        {
            queued.Remove(chip);
            OnQueueChanged();
        }

        Card(interjected.AgentId)?.AddNotice(SessionStrings.ComposerInterjectedNotice(interjected.Text));
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
            Math.Max(0, completed.Usage.OutputTokens - previous.OutputTokens),
            Math.Max(0, completed.Usage.CacheReadTokens - previous.CacheReadTokens),
            Math.Max(0, completed.Usage.CacheWriteTokens - previous.CacheWriteTokens));
        if (delta.InputTokens == 0 && delta.OutputTokens == 0 && delta.CacheReadTokens == 0 && delta.CacheWriteTokens == 0)
        {
            return;
        }

        // The strip reports the root agent's spend (the context window and the price belong to that model); a sub-agent's
        // usage stays on its own tab, and its turn card still gets its own delta here.
        Instruments.AddTurn(completed.AgentId, delta);
        if (string.Equals(completed.AgentId, "root", StringComparison.Ordinal))
        {
            // The session's tool set only exists once the first turn built the agent host, so the denominator is refreshed
            // after the turn that created it instead of before.
            Instruments.RefreshCounters();
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
        RaiseSubAgentCounters();
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
        RaiseSubAgentCounters();
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
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        Instruments.Dispose();
        foreach (var message in queued.ToArray())
        {
            NotifyWithdrawn(message);
        }
        coalescer.Dispose();
        Cancel();
        turn?.Dispose();
        turn = null;
        Trace.WriteLine("[session] closed tab " + SessionId);
    }

    /// <summary>
    /// The language switched. Every text of this session is derived in a getter, so nothing is cached and nothing has to be
    /// rebuilt: the session's own labels are re-raised here and each card, tool row, notice and sub-agent tab re-raises
    /// its own - the cards themselves never subscribe to <see cref="Loc.LanguageChanged"/>, or every one of them would
    /// hold a reference to the static event after the tab closed.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SubAgentLabel));
        OnPropertyChanged(nameof(SubAgentToggleName));
        OnPropertyChanged(nameof(SubAgentToggleTooltip));
        Transcript.RefreshText();
        foreach (var agent in subAgents)
        {
            agent.RefreshText();
        }

        foreach (var agent in finished)
        {
            agent.RefreshText();
        }

        finishedMenu.RefreshText();
        Composer.RefreshText();
        foreach (var message in queued)
        {
            message.RefreshText();
        }

        // The left panel's agent rows copy the sub-agents' status texts, so they have to be rebuilt in the new language.
        AgentTreeChanged?.Invoke();
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
    private readonly string kind;
    private SubAgentStatus status = SubAgentStatus.Queued;
    private DateTimeOffset startedAt;
    private TimeSpan elapsed;
    private TokenUsage usage = TokenUsage.Empty;
    private bool sawUsage;
    private LocText error;
    private LocText statusNote = SessionStrings.SubAgentWaitingSlot;
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
        this.kind = kind;
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

    /// <summary>The kind the store recorded; a stored child without one is named after the region, in the current language.</summary>
    public string Kind => kind.Length == 0 ? SessionStrings.SubAgentsTitle : kind;

    public string Task { get; }

    public string Model { get; }

    public string ModelText => Model.Length == 0 ? SessionStrings.NoModel : Model;

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

    public string ReadOnlyBadgeText => SessionStrings.ReadOnlyBadge;

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
        ? SessionStrings.SubAgentEnded
        : status switch
        {
            SubAgentStatus.Queued => SessionStrings.SubAgentQueued,
            SubAgentStatus.Running => SessionStrings.SubAgentRunning,
            SubAgentStatus.Completed => SessionStrings.SubAgentCompleted,
            SubAgentStatus.Stopped => SessionStrings.SubAgentStopped,
            _ => SessionStrings.SubAgentFailed,
        };

    public bool IsFinished => outcomeUnknown || status is SubAgentStatus.Completed or SubAgentStatus.Stopped or SubAgentStatus.Failed;

    public bool IsLive => !IsFinished;

    /// <summary>The one-line note beside the status, in the current language; empty when there is nothing to say.</summary>
    public string StatusNote => statusNote.Resolve();

    public bool HasStatusNote => !statusNote.IsEmpty;

    public TokenUsage Usage => usage;

    /// <summary>"模型 · 输入 N · 输出 M"; an unreported side is "—".</summary>
    public string UsageText => ModelText + " · " +
        SessionStrings.TokenSummary(sawUsage ? usage.InputTokens : null, sawUsage ? usage.OutputTokens : null);

    public string ElapsedText =>
        IsFinished || status == SubAgentStatus.Running ? TextFormat.Elapsed(elapsed) : TextFormat.None;

    /// <summary>The run's failure message, untranslated; null when the run did not fail.</summary>
    public string? Error => error.IsEmpty ? null : error.Resolve();

    public void MarkQueued()
    {
        Status = SubAgentStatus.Queued;
        SetStatusNote(SessionStrings.SubAgentWaitingSlot);
    }

    /// <summary>Clears the note: the status text itself already shows the running state.</summary>
    public void MarkRunning(DateTimeOffset at)
    {
        startedAt = at;
        Status = SubAgentStatus.Running;
        SetStatusNote(default);
    }

    public void MarkSteered(string instruction, int droppedToolCalls)
    {
        SetStatusNote(SessionStrings.SubAgentSteeredNote(droppedToolCalls));
        Transcript.AddNotice(SessionStrings.SteeredNotice(ToolRowViewModel.Compact(instruction, 160)));
    }

    public void MarkFollowUp(string instruction, int pending)
    {
        SetStatusNote(SessionStrings.SubAgentFollowUpNote(pending));
        Transcript.AddNotice(SessionStrings.FollowUpNotice(ToolRowViewModel.Compact(instruction, 160)));
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
        error = finalError is null ? default : LocText.Literal(finalError);
        SetStatusNote(error);
        Transcript.EndTurn(finalElapsed, error);
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(ElapsedText));
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

    /// <summary>
    /// Re-raises every text this tab shows after a language switch. The values are all derived in the getters, so this only
    /// notifies; the owning session calls it for each of its sub-agents.
    /// </summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusNote));
        OnPropertyChanged(nameof(HasStatusNote));
        OnPropertyChanged(nameof(ModelText));
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(ReadOnlyBadgeText));
        Transcript.RefreshText();
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
            session.AgentKind ?? string.Empty,
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
        SetStatusNote(default);
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
        SetStatusNote(SessionStrings.SubAgentStopRequested);
        StopCommand.NotifyCanExecuteChanged();
        stopRequested?.Invoke(AgentId);
    }

    /// <summary>Sets the note; it keeps the key and its arguments, so a later language switch re-renders it.</summary>
    private void SetStatusNote(LocText value)
    {
        if (statusNote.Equals(value))
        {
            return;
        }

        statusNote = value;
        OnPropertyChanged(nameof(StatusNote));
        OnPropertyChanged(nameof(HasStatusNote));
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

    /// <summary><c>已结束 (3)</c>: derived on every read so a language switch only has to re-raise it.</summary>
    public string Title => SessionStrings.FinishedAgentsMenu(Items.Count);

    public string Hint => SessionStrings.FinishedAgentsHint;

    /// <summary>Re-raises the texts after a language switch; the owning session calls this.</summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Hint));
    }

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
