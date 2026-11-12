using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Automations;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Providers;
using Forge.App.Services;
using Forge.Core.Agents;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>Why the turn-start check refused a turn; <see cref="None"/> lets it start.</summary>
internal enum TurnRejection
{
    None,

    /// <summary>The store no longer has the session as active: it was taken over (or archived) elsewhere.</summary>
    TakenOver,

    /// <summary>The store has the session as removed (<see cref="SessionStatus.Deleted"/>) or no longer has it at all.</summary>
    Removed,

    /// <summary>The session's workspace has a merge in progress (<c>MERGE_HEAD</c>).</summary>
    MergeConflict,

    /// <summary>The session's workspace is in the "operation in progress" state (a merge back / sync is running in it).</summary>
    WorkspaceOperating,
}

/// <summary>
/// One open main session: its transcript, its sub-agent tabs, the turn loop and the turn checkpoints. All agent events
/// are routed by <see cref="AgentEvent.AgentId"/>, so a child's text never reaches the parent's bubble.
/// <para>This is the long-lived owner of everything the session shows, so it is also the one subscriber of
/// <see cref="Loc.LanguageChanged"/>: the cards, tool rows, notices and sub-agent labels re-derive their texts when the
/// language changes and are told to re-raise them from here, and the subscription ends with <see cref="Dispose"/>.</para>
/// </summary>
internal sealed class SessionWorkspaceViewModel : ObservableObject, IDisposable, IAutomationSession
{
    /// <summary>How much text of a child agent is parked while its start event is still in flight.</summary>
    private const int EarlyDeltaLimit = 8192;

    /// <summary>Null for a read-only view (a non-active session, or one that was taken over): no turn can start.</summary>
    private SessionRuntime? runtime;
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

    /// <summary>The shell's notification entry; null (a test, a standalone session) means the session says nothing outside its own transcript.</summary>
    private readonly INotifications? notifications;

    /// <summary>Set while the turn-start check reads the store, so a second send cannot slip in before the turn starts.</summary>
    private bool gating;

    // Automation turns (see RunAutomationTurnAsync): set only on the UI thread.
    private volatile bool automationTurn;

    // Root failure of the running turn as the worker thread saw it: the coalesced UI copy can land after the turn's await.
    private volatile string? turnRootFailure;
    private bool turnRan;
    private volatile ProviderQuotaExhausted? turnRootQuota;
    private string? automationBanner;
    private Action? cancelAutomation;
    private RelayCommand? cancelAutomationCommand;
    private long lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;
    private int runningTools;
    private bool automationRejected;
    private string? rootFailure;
    private LocText? lastTurnError;
    private bool isReadOnlyView;
    private bool isHandingOver;
    private AsyncRelayCommand? continueCommand;
    private AsyncRelayCommand? restoreCommand;
    private string? continueBlockedReason;

    /// <param name="runtime">The live runtime, or <see langword="null"/> for a read-only view of a session that is not
    /// <see cref="SessionStatus.Active"/>: the transcript is replayed from the store and nothing can be sent.</param>
    public SessionWorkspaceViewModel(
        SessionInfo session,
        SessionRuntime? runtime,
        TurnCheckpointService? checkpoints,
        ISessionStore? store,
        ProviderServices providers,
        Action<SubAgentViewModel>? subAgentOpened = null,
        ComposerPreferences? composerPreferences = null,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? fileSource = null,
        Func<IReadOnlyList<SlashEntry>>? skillSource = null,
        ISessionInstrumentSources? instrumentSources = null,
        INotifications? notifications = null)
    {
        Info = session;
        this.runtime = runtime;
        isReadOnlyView = runtime is null;
        this.checkpoints = checkpoints;
        this.store = store;
        this.notifications = notifications;
        this.subAgentOpened = subAgentOpened;
        providerModel = providers.Catalog.Default?.DefaultModel ?? string.Empty;
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
            providers,
            ApplyRootDefinition,
            composerPreferences ?? ComposerPreferences.Shared,
            session.WorkspacePath,
            fileSource,
            skillSource);
        ToggleSubAgentsCommand = new RelayCommand(() => SubAgentsCollapsed = !SubAgentsCollapsed);
        Instruments = new SessionInstrumentsViewModel(
            session,
            instrumentSources ?? EmptySessionInstrumentSources.Instance,
            ToolNames,
            () => Composer.Model,
            () => Composer.ProviderId);
        if (!providers.IsConfigured)
        {
            var missing = SessionStrings.ProviderMissing(providers.StatusText.Resolve());
            Transcript.AddNotice(missing, isError: true);
            notifications.Notify(
                NotificationLevel.Error,
                NotificationsStrings.ProviderMissingTitle,
                missing,
                "provider-missing:" + Info.Id);
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
    internal IReadOnlyList<string> ToolNames() => runtime?.ToolNames() ?? [];

    /// <summary>
    /// True for a session that is not the active session of its workspace: opened from history or the recycled rows, or
    /// turned read-only when the turn-start check found it taken over. Nothing can be sent; the tab offers "continue"
    /// (archived) or "restore" (deleted) instead.
    /// </summary>
    public bool IsReadOnlyView => isReadOnlyView;

    /// <summary>The banner of the read-only view; empty for a live session.</summary>
    public string ReadOnlyBanner => !isReadOnlyView
        ? string.Empty
        : Info.Status == SessionStatus.Deleted ? LifecycleStrings.ReadOnlyDeleted : LifecycleStrings.ReadOnlyArchived;

    /// <summary>"Continue this session" of an archived read-only view; null (hidden) otherwise. Set by the shell.</summary>
    public AsyncRelayCommand? ContinueCommand
    {
        get => continueCommand;
        set
        {
            if (Set(ref continueCommand, value))
            {
                OnPropertyChanged(nameof(HasContinue));
            }
        }
    }

    public bool HasContinue => isReadOnlyView && continueCommand is not null && Info.Status != SessionStatus.Deleted;

    /// <summary>"Restore…" of a removed (deleted) read-only view; null (hidden) until the restore flow exists. Set by the shell.</summary>
    public AsyncRelayCommand? RestoreCommand
    {
        get => restoreCommand;
        set
        {
            if (Set(ref restoreCommand, value))
            {
                OnPropertyChanged(nameof(HasRestore));
            }
        }
    }

    public bool HasRestore => isReadOnlyView && restoreCommand is not null && Info.Status == SessionStatus.Deleted;

    /// <summary>
    /// A read-only view that still holds an unsent draft or queued messages (a turn rejected at its start, R6): the bar
    /// says so, because the composer is hidden; "continue" carries them into the live view.
    /// </summary>
    public bool HasPendingComposer => isReadOnlyView && (!string.IsNullOrWhiteSpace(draft) || queued.Count > 0);

    public string PendingComposerNote => HasPendingComposer
        ? LifecycleStrings.PendingComposer(queued.Count + (string.IsNullOrWhiteSpace(draft) ? 0 : 1))
        : string.Empty;

    /// <summary>
    /// Takes the unsent draft and the queued (not interjected) messages out of this view, for the live view that replaces
    /// it ("continue"). Interjections of a read-only view were already turned into queued messages when its turn ended.
    /// </summary>
    internal (string Draft, IReadOnlyList<string> Queued) TakeComposerState()
    {
        var taken = (draft, (IReadOnlyList<string>)[.. queued.Select(static message => message.Text)]);
        queued.Clear();
        reviewWithdrawn.Clear();
        Draft = string.Empty;
        OnQueueChanged();
        return taken;
    }

    /// <summary>Puts a carried draft and queue back (the counterpart of <see cref="TakeComposerState"/>); the queue stays paused.</summary>
    internal void RestoreComposerState((string Draft, IReadOnlyList<string> Queued) state)
    {
        if (!string.IsNullOrWhiteSpace(state.Draft))
        {
            Draft = state.Draft;
        }

        foreach (var text in state.Queued)
        {
            queued.Add(new QueuedMessageViewModel(text, isInterjection: false, RemoveQueued));
        }

        if (state.Queued.Count > 0)
        {
            queuePaused = true;
        }

        OnQueueChanged();
    }

    /// <summary>
    /// Set by the shell when the session cannot continue where it is (6.3): its workspace folder is gone, or git marks the
    /// worktree prunable or does not list it. "Continue" is then disabled and its label carries this reason; null enables it.
    /// </summary>
    public string? ContinueBlockedReason
    {
        get => continueBlockedReason;
        set
        {
            if (Set(ref continueBlockedReason, value))
            {
                OnPropertyChanged(nameof(ContinueLabel));
                continueCommand?.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Label of the continue button: carries the reason when it is disabled.</summary>
    public string ContinueLabel => continueBlockedReason is { } reason
        ? LifecycleStrings.ContinueBlocked(reason)
        : LifecycleStrings.ContinueSession;

    /// <summary>
    /// Read before every turn starts (6.3): the store still has this session as <see cref="SessionStatus.Active"/>, and its
    /// workspace is not in a merge conflict. Null (a headless host, a test) lets every turn start.
    /// </summary>
    internal Func<CancellationToken, Task<TurnRejection>>? TurnGate { get; set; }

    /// <summary>Children of a stored session for the read-only view, which has no runtime to ask; null means none.</summary>
    internal Func<string, CancellationToken, Task<IReadOnlyList<SessionInfo>>>? ChildQuery { get; init; }

    /// <summary>Raised once when a live session turns read-only (taken over): the owner releases its runtime.</summary>
    public event Action<SessionWorkspaceViewModel>? BecameReadOnly;

    /// <summary>
    /// Turns a live session into a read-only view in place: the transcript and the draft stay, the runtime is dropped
    /// (its owner releases it on <see cref="BecameReadOnly"/>) and nothing can be sent any more.
    /// </summary>
    internal void EnterReadOnly(SessionInfo current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (isReadOnlyView || disposed)
        {
            return;
        }

        Info = current;
        isReadOnlyView = true;
        runtime = null;
        OnPropertyChanged(nameof(IsReadOnlyView));
        OnPropertyChanged(nameof(ReadOnlyBanner));
        OnPropertyChanged(nameof(HasContinue));
        OnPropertyChanged(nameof(HasRestore));
        RefreshCanSend();
        OnPropertyChanged(nameof(HasPendingComposer));
        OnPropertyChanged(nameof(PendingComposerNote));
        BecameReadOnly?.Invoke(this);
    }

    public RelayCommand ToggleSubAgentsCommand { get; }

    public event EventHandler? TabTitleChanged;

    public event Action<SessionWorkspaceViewModel>? TurnCompleted;

    /// <summary>A turn passed the gate and started; the flag is true for a turn an automation delivered.</summary>
    public event Action<SessionWorkspaceViewModel, bool>? TurnStarted;

    /// <summary>The root agent's turn failed because a provider quota is used up (raised on the UI thread).</summary>
    public event Action<SessionWorkspaceViewModel, ProviderQuotaExhausted>? QuotaExhausted;

    /// <summary>The running (or just-ended) turn was delivered by an automation; read by <see cref="TurnCompleted"/> handlers.</summary>
    public bool IsAutomationTurn => automationTurn;

    /// <summary>The automation run attached to this session (the banner above the composer); null hides the banner.</summary>
    public string? AutomationBanner
    {
        get => automationBanner;
        private set
        {
            if (Set(ref automationBanner, value))
            {
                OnPropertyChanged(nameof(HasAutomationBanner));
            }
        }
    }

    public bool HasAutomationBanner => automationBanner is not null;

    /// <summary>Cancels the attached run; set by the main window together with the banner text.</summary>
    public RelayCommand CancelAutomationCommand => cancelAutomationCommand ??= new RelayCommand(() => cancelAutomation?.Invoke());

    /// <summary>Shows (or with a null text hides) the automation banner; <paramref name="cancel"/> backs its cancel button.</summary>
    public void SetAutomationBanner(string? text, Action? cancel)
    {
        cancelAutomation = cancel;
        AutomationBanner = text;
    }

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
                OnPropertyChanged(nameof(HasPendingComposer));
                OnPropertyChanged(nameof(PendingComposerNote));
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
        var live = runtime;
        var query = ChildQuery;
        if (live is null && query is null)
        {
            return;
        }

        var children = await BackgroundRead.RunAsync(
                token => live is not null ? live.QueryChildrenAsync(parentSessionId, token) : query!(parentSessionId, token),
                ct)
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
        runtime?.SubAgentControl?.Stop(agentId, SessionStrings.SubAgentStoppedByUser);
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
    private bool EvaluateCanSend() =>
        !isReadOnlyView && !isHandingOver && !isRunning && !gating && (!string.IsNullOrWhiteSpace(draft) || HasSendableQueue());

    private bool HasSendableQueue() => queued.Any(message => !message.IsInterjection);

    private bool CanSendWhileRunning() => !isReadOnlyView && !isHandingOver && isRunning && !string.IsNullOrWhiteSpace(draft);

    /// <summary>
    /// "Handing over" (lifecycle section 2): set once the handover check passed, cleared when the handover finishes,
    /// fails or is cancelled. While set, nothing can be sent or queued; the draft stays where it is.
    /// </summary>
    public bool IsHandingOver
    {
        get => isHandingOver;
        internal set
        {
            if (isHandingOver == value)
            {
                return;
            }

            isHandingOver = value;
            OnPropertyChanged();
            RefreshCanSend();
        }
    }

    /// <summary>
    /// Why the session is busy (lifecycle section 2): a running turn, sub-agents running or waiting for a slot, queued
    /// composer messages, undelivered interjections, or a handover already under way. Empty = not busy. Derived, never
    /// stored; reading it takes nothing out of the queue.
    /// </summary>
    internal IReadOnlyList<string> BusyReasons()
    {
        var reasons = new List<string>(5);
        if (isRunning)
        {
            reasons.Add(HandoverStrings.BusyTurn);
        }

        if (subAgents.Any(static agent => agent.Status is SubAgentStatus.Running or SubAgentStatus.Queued))
        {
            reasons.Add(HandoverStrings.BusySubAgents);
        }

        if (queued.Count > 0)
        {
            reasons.Add(HandoverStrings.BusyQueued);
        }

        if (runtime?.HasUndeliveredInterjections == true)
        {
            reasons.Add(HandoverStrings.BusyInterjections);
        }

        if (isHandingOver)
        {
            reasons.Add(HandoverStrings.BusyHandingOver);
        }

        return reasons;
    }

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
        var typed = text;
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
        if (typed.Length > 0)
        {
            text = Composer.ExpandCommand(text);
        }

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
        text = Composer.ExpandCommand(text);
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

    /// <summary>Idle in the lifecycle sense (section 2) and able to run a turn now: what an automation waits for.</summary>
    public bool IsAutomationIdle => !disposed && !isReadOnlyView && runtime is not null && !gating && BusyReasons().Count == 0;

    /// <summary>The session can no longer take automation turns here: the tab closed, or it was taken over / archived / removed.</summary>
    public bool IsAutomationClosed => disposed || isReadOnlyView || runtime is null;

    /// <summary>
    /// Runs <paramref name="text"/> as a turn on behalf of an automation; the caller checked <see cref="IsAutomationIdle"/>.
    /// A rejected turn never lands in the user's draft or queue: it is reported as withdrawn instead.
    /// </summary>
    public async Task<MailboxResult> RunAutomationTurnAsync(string text, LocText origin, AgentDefinition? agent)
    {
        if (!IsAutomationIdle)
        {
            return new MailboxResult(MailboxStatus.Withdrawn, AutomationStrings.SessionBusyText);
        }

        automationTurn = true;
        automationRejected = false;
        rootFailure = null;
        lastTurnError = null;
        turnRootFailure = null;
        turnRootQuota = null;
        turnRan = false;
        Transcript.AddNotice(origin);
        if (agent is not null)
        {
            ApplyRootDefinition(agent);
        }

        try
        {
            await RunTurnAsync(text).ConfigureAwait(true);
        }
        finally
        {
            automationTurn = false;
            if (agent is not null && !disposed)
            {
                // The override was for this turn only: the session goes back to the agent its composer has selected.
                ApplyRootDefinition(Composer.CurrentDefinition());
            }
        }

        if (automationRejected)
        {
            return new MailboxResult(MailboxStatus.Withdrawn, AutomationStrings.SessionUnavailableText);
        }

        if (disposed && !turnRan)
        {
            return new MailboxResult(MailboxStatus.Withdrawn, AutomationStrings.SessionClosedText);
        }

        if (lastTurnError is { IsEmpty: false } failure)
        {
            return failure.Equals(SessionStrings.Stopped)
                ? new MailboxResult(MailboxStatus.Withdrawn, AutomationStrings.TurnStoppedText)
                : new MailboxResult(MailboxStatus.TurnFailed, failure.Resolve()) { Quota = turnRootQuota };
        }

        if (turnRootQuota is { } quota)
        {
            return new MailboxResult(MailboxStatus.TurnFailed, turnRootFailure ?? quota.Message) { Quota = quota };
        }

        return (turnRootFailure ?? rootFailure) is { } error
            ? new MailboxResult(MailboxStatus.TurnFailed, error)
            : turnRan
                ? new MailboxResult(MailboxStatus.Completed)
                : new MailboxResult(MailboxStatus.Withdrawn, AutomationStrings.SessionUnavailableText);
    }

    /// <summary>Starts a sub-agent under this session outside a turn (automations); its tab appears like a tool-spawned one.</summary>
    public Task<ChildAgentResult> LaunchChildAsync(string kind, string task, CancellationToken ct) =>
        runtime is { } live && !IsAutomationClosed
            ? live.SpawnChildAsync(kind, task, OnAgentEvent, ct)
            : Task.FromException<ChildAgentResult>(new InvalidOperationException(AutomationStrings.SessionUnavailableText));

    private void Interject()
    {
        var text = draft.Trim();
        if (text.Length == 0)
        {
            return;
        }

        Draft = string.Empty;
        text = Composer.ExpandCommand(text);
        var delivered = isRunning && runtime is { } live && live.TryInterject(text);
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
        text = Composer.ExpandCommand(text);
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
        OnPropertyChanged(nameof(HasPendingComposer));
        OnPropertyChanged(nameof(PendingComposerNote));
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
            var undelivered = runtime?.TakeUndeliveredInterjections() ?? [];
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

        if (disposed || isRunning || isReadOnlyView)
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

    /// <summary>Last agent event of the running turn, or the turn start (written off the UI thread).</summary>
    public DateTimeOffset LastActivityAt => new(Interlocked.Read(ref lastActivityTicks), TimeSpan.Zero);

    /// <summary>A tool call of any agent of the running turn has started and not finished (a build, a long command).</summary>
    public bool IsToolRunning => IsRunning && Volatile.Read(ref runningTools) > 0;

    private void ApplyRootDefinition(AgentDefinition? definition)
    {
        if (runtime?.SetRootDefinition(definition) == true)
        {
            rootRebuilt = true;
        }
    }

    private async Task RunTurnAsync(string text)
    {
        if (!await PassesTurnGateAsync(text).ConfigureAwait(true))
        {
            return;
        }

        var live = runtime!;
        turnRan = true;
        if (rootRebuilt)
        {
            rootRebuilt = false;
            agentUsage.Remove("root");
        }

        TurnStarted?.Invoke(this, automationTurn);
        turnStartedAt = DateTimeOffset.Now;
        Interlocked.Exchange(ref lastActivityTicks, turnStartedAt.UtcTicks);
        Interlocked.Exchange(ref runningTools, 0);
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
                    notifications.Notify(
                        NotificationLevel.Warning,
                        NotificationsStrings.TurnFailedTitle,
                        SessionStrings.CheckpointFailed(exception.Message),
                        "checkpoint:" + SessionId);
                }
            }

            await live.RunTurnAsync(text, OnAgentEvent, token).ConfigureAwait(true);
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
                // A turn that ended by itself is worth interrupting the user for; the transcript line alone is missed
                // as soon as another session (or another window) has the focus. A cancellation is the user's own doing.
                if (error is { IsEmpty: false } failure && !failure.Equals(SessionStrings.Stopped))
                {
                    notifications.Notify(NotificationLevel.Error, NotificationsStrings.TurnFailedTitle, failure, "turn-failed:" + SessionId);
                }

                lastTurnError = error;
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
                        notifications.Notify(
                            NotificationLevel.Warning,
                            NotificationsStrings.TurnFailedTitle,
                            SessionStrings.CheckpointFailed(exception.Message),
                            "checkpoint:" + SessionId);
                    notifications.Notify(
                        NotificationLevel.Warning,
                        NotificationsStrings.TurnFailedTitle,
                        SessionStrings.CheckpointFailed(exception.Message),
                        "checkpoint:" + SessionId);
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

    /// <summary>
    /// The turn-start check (6.3). A rejected text is never lost: it goes back into an empty draft, else to the front of
    /// the queue. A session the store no longer has as active turns read-only in place; a merge conflict pauses the queue.
    /// </summary>
    private async Task<bool> PassesTurnGateAsync(string text)
    {
        if (isReadOnlyView || runtime is null)
        {
            Restore(text);
            return false;
        }

        var gate = TurnGate;
        if (gate is null)
        {
            return true;
        }

        gating = true;
        RefreshCanSend();
        TurnRejection rejection;
        try
        {
            rejection = await gate(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            gating = false;
            RefreshCanSend();
        }

        if (disposed)
        {
            return false;
        }

        if (rejection == TurnRejection.None)
        {
            return true;
        }

        if (automationTurn)
        {
            // An automation's text is not the user's: it is reported back as withdrawn, never parked in the composer.
            automationRejected = true;
        }
        else
        {
            Restore(text);
        }

        if (rejection is TurnRejection.TakenOver or TurnRejection.Removed)
        {
            Transcript.AddNotice(LifecycleStrings.TakenOver, isError: true);
            EnterReadOnly(Info with
            {
                Status = rejection == TurnRejection.Removed ? SessionStatus.Deleted : SessionStatus.Archived,
            });
        }
        else
        {
            queuePaused = true;
            Transcript.AddNotice(
                rejection == TurnRejection.WorkspaceOperating ? LifecycleStrings.WorkspaceOperating : LifecycleStrings.MergeConflictPending,
                isError: true);
            OnQueueChanged();
        }

        return false;

        void Restore(string rejected)
        {
            if (string.IsNullOrWhiteSpace(draft))
            {
                Draft = rejected;
            }
            else
            {
                AddQueued(new QueuedMessageViewModel(rejected, isInterjection: false, RemoveQueued), 0);
            }
        }
    }

    /// <summary>Failures of the git snapshot or of the checkpoint store; the turn continues and the card says so.</summary>
    private static bool IsCheckpointFailure(Exception exception) =>
        exception is GitException or InvalidOperationException or IOException or InvalidDataException or ArgumentException or
        DbException or UnauthorizedAccessException;

    private void OnSendFailed(Exception exception)
    {
        Transcript.AddNotice(SessionStrings.TurnFailed(exception.Message), isError: true);
        notifications.Notify(
            NotificationLevel.Error,
            NotificationsStrings.TurnFailedTitle,
            SessionStrings.TurnFailed(exception.Message),
            "turn-failed:" + SessionId);
    }

    /// <summary>Worker-thread event sink; text is merged off-thread, everything else is queued to the frame coalescer.</summary>
    private void OnAgentEvent(AgentEvent value)
    {
        Interlocked.Exchange(ref lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
        if (value is AgentToolStarted)
        {
            Interlocked.Increment(ref runningTools);
        }
        else if (value is AgentToolFinished && Interlocked.Decrement(ref runningTools) < 0)
        {
            Interlocked.Exchange(ref runningTools, 0);
        }

        switch (value)
        {
            case AgentFailed failed when string.Equals(failed.AgentId, "root", StringComparison.Ordinal):
                turnRootFailure = failed.Error;
                coalescer.Enqueue(value);
                break;
            case AgentQuotaExhausted quota when string.Equals(quota.AgentId, "root", StringComparison.Ordinal):
                turnRootQuota = quota.Quota;
                // An automation's own turn gets the quota back in its mailbox result and decides itself (the resume run
                // waits again, others fail); only a user's turn starts a resume run.
                if (!automationTurn)
                {
                    coalescer.Enqueue(value);
                }

                break;
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
                if (string.Equals(failed.AgentId, "root", StringComparison.Ordinal))
                {
                    rootFailure = failed.Error;
                }

                break;
            case AgentQuotaExhausted quota when string.Equals(quota.AgentId, "root", StringComparison.Ordinal):
                QuotaExhausted?.Invoke(this, quota.Quota);
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

        // A clean finish is already visible in the sub-agent tab; a failure or a stop is what the user has to hear about.
        if (finishedEvent.Status is SubAgentStatus.Failed or SubAgentStatus.Stopped)
        {
            notifications.Notify(
                finishedEvent.Status == SubAgentStatus.Failed ? NotificationLevel.Error : NotificationLevel.Warning,
                NotificationsStrings.SubAgentFinishedTitle,
                NotificationsStrings.SubAgentFinished(agent.Kind, agent.StatusText),
                "subagent:" + finishedEvent.AgentId);
        }
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
        OnPropertyChanged(nameof(ReadOnlyBanner));
        OnPropertyChanged(nameof(ContinueLabel));
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
