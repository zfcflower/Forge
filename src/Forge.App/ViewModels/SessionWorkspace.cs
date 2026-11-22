using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Automations;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.Services;
using Forge.Core.Agents;
using Forge.Core.Providers;
using Forge.Core.Storage;
using Forge.Workspace;
using Forge.App.Logging;
using ZLogger;

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
internal sealed partial class SessionWorkspaceViewModel : NotifyingObject, IDisposable, IAutomationSession, IAgentMessageHost
{
    /// <summary>How much text of a child agent is parked while its start event is still in flight.</summary>
    private const int EarlyDeltaLimit = 8192;

    /// <summary>Null for a read-only view (a non-active session, or one that was taken over): no turn can start.</summary>
    private SessionRuntime? runtime;
    private readonly TurnCheckpointService? checkpoints;
    private readonly ISessionStore? store;
    private readonly Action<SubAgentViewModel>? subAgentOpened;
    private readonly Action? openSession;
    private readonly string providerModel;

    /// <summary>The live preferences this session reads values from (the inspector's line limit); null lists the defaults.</summary>
    private readonly AppPreferences? appPreferences;
    private readonly ObservableCollection<SubAgentViewModel> subAgents = [];
    private readonly ConcurrentDictionary<string, SubAgentViewModel> subAgentById = new(StringComparer.Ordinal);

    /// <summary>
    /// Ids of the children <see cref="LaunchChildAsync"/> started outside a turn (the CI chain's Scout, a prompt automation).
    /// Written from the launching thread before the start event reaches the UI, read when the row is created.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> automationChildIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TokenUsage> agentUsage = new(StringComparer.Ordinal);

    /// <summary>
    /// The same per-agent accumulation for the model's streaming time, kept beside <see cref="agentUsage"/> and updated at
    /// the same moment: an <c>AgentTurnCompleted</c> carries the instance's cumulative stream time, so the difference to the
    /// previous event of that agent is what its card adds (see <see cref="AddTurnUsage"/>). Written and read on the UI
    /// thread only, like the token dictionary; cleared where that one is cleared.
    /// </summary>
    private readonly Dictionary<string, TimeSpan> agentStream = new(StringComparer.Ordinal);
    private readonly ObservableCollection<SubAgentViewModel> finished = [];
    private readonly FinishedSubAgentsViewModel finishedMenu = new();
    private readonly FrameCoalescer<AgentEvent> coalescer;

    /// <summary>Text of a child agent that arrived before its <c>AgentSubAgentStarted</c> reached the dispatcher.</summary>
    private readonly ConcurrentDictionary<string, StringBuilder> earlyDeltas = new(StringComparer.Ordinal);

    /// <summary>Reasoning of a child agent parked for the same reason as <see cref="earlyDeltas"/>.</summary>
    private readonly ConcurrentDictionary<string, StringBuilder> earlyThinking = new(StringComparer.Ordinal);
    private readonly ObservableCollection<QueuedMessageViewModel> queued = [];
    private string draft = string.Empty;
    private bool isRunning;

    /// <summary>The message a redirect sends once the cancelled turn has wound down.</summary>
    private ComposerPrompt? redirectText;

    /// <summary>Set by Stop: the queue waits until the user sends again.</summary>
    private bool queuePaused;
    private readonly ConditionalWeakTable<QueuedMessageViewModel, Action> reviewWithdrawn = new();

    /// <summary>The root agent is rebuilt for the next turn, so its accumulated usage starts again from zero.</summary>
    private bool rootRebuilt;
    private bool subAgentsCollapsed;
    private bool subAgentRegionOpen;
    private bool canToggleSubAgents;
    private CancellationTokenSource? turn;
    private int turnNumber;

    /// <summary>The full-prefix rewrites of this session inside the last hour, and whether the user was already told.</summary>
    private readonly List<(DateTimeOffset At, int Tokens)> cacheRewrites = [];
    private bool cacheRewriteNotified;
    private const int CacheRewriteNoticeThreshold = 3;
    private DateTimeOffset turnStartedAt;

    /// <summary>
    /// The app's turn-metrics writer, reached through <see cref="ISessionInstrumentSources"/> (the shell's one app-level
    /// service block); null for a view built without one, which then records nothing.
    /// </summary>
    private readonly TurnMetricsRecorder? metricsRecorder;

    /// <summary>Tool calls of each live sub-agent's current turn, counted on the dispatcher where the events are ordered;
    /// exchanged into that turn's metrics row when the turn completes.</summary>
    private readonly ConcurrentDictionary<string, int> subAgentToolCalls = new();

    /// <summary>The card each live sub-agent's last completed turn closed: the run-end flush may still owe it usage.</summary>
    private readonly ConcurrentDictionary<string, TurnCardViewModel> subAgentLastCards = new();

    /// <summary>
    /// Each run's usage baseline: the instance counter as it stood when the run's <c>AgentSubAgentStarted</c> arrived. A
    /// resumed child keeps one <c>agentUsage</c> entry across its runs, and each run's unreported remainder - what
    /// <c>AgentSubAgentFinished.Usage</c> carries, itself a per-run difference Core took - is measured against this.
    /// </summary>
    private readonly ConcurrentDictionary<string, TokenUsage> subAgentRunStart = new();

    /// <summary>
    /// The session's sources, kept for the replay seeding only: the metrics table the dividers seed from and the model
    /// metadata the cost line prices with. Live turns never read through it.
    /// </summary>
    private readonly ISessionInstrumentSources instrumentSources;

    /// <summary>
    /// The root instance's counters as the turn began (tokens, streaming time, and which instance they belong to). The turn-end
    /// difference is the metrics row's snapshot. They are read from the instance itself, not from events: a turn that failed
    /// or was stopped never sends <c>AgentTurnCompleted</c>, yet its calls already moved the counters, and an event-fed copy
    /// would charge that spend to the next turn.
    /// </summary>
    private RootCounters turnStartCounters = RootCounters.None;

    /// <summary>Tool calls the root agent started during this turn; incremented on the event thread, reset when the turn
    /// begins. Counted there — not in TrackTool — so a Background flush landing after the turn's finally cannot miss a
    /// call.</summary>
    private int turnToolCalls;

    /// <summary>The card of the turn now running (or just ended). The root's usage lands on the card the turn used, not on
    /// whatever <c>Transcript.Current</c> happens to be when the coalescer's Background flush arrives — a quick next
    /// message would otherwise hand the previous turn's tokens to the new card.</summary>
    private TurnCardViewModel? turnCard;

    private bool disposed;

    /// <summary>The thread the session was built on (the UI thread); a language notification from another one is not ours.</summary>
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    /// <summary>The shell's notification entry; null (a standalone session) means the session says nothing outside its own transcript.</summary>
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
    private Task? historyLoad;
    private bool historyLoaded;
    private bool isHistoryLoading;
    private long historyBeforeSeq = long.MaxValue;
    private bool hasOlderHistory;
    private bool loadingOlderHistory;
    private bool isHandingOver;
    private AsyncRelayCommand? continueCommand;
    private AsyncRelayCommand? compactCommand;
    private AsyncRelayCommand? restoreCommand;
    private string? continueBlockedReason;

    /// <param name="runtime">The live runtime, or <see langword="null"/> for a read-only view of a session that is not
    /// <see cref="SessionStatus.Active"/>: the transcript is replayed from the store and nothing can be sent.</param>
    /// <param name="preferences">The live preferences the composer filters hidden models with and the inspector reads its
    /// output-tail limit from; <see langword="null"/> (a standalone view) lists every model and uses the defaults.</param>
    /// <param name="openInspector">Opens the resource window for this session (the strip's resource button);
    /// <see langword="null"/> (a standalone view) leaves the button disabled instead of offering a dead entry.</param>
    /// <param name="openSession">Brings this session's own tab to the front; the sub-agent breadcrumb's way back to its
    /// parent, and <see langword="null"/> hides that action.</param>
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
        INotifications? notifications = null,
        AppPreferences? preferences = null,
        Action? openInspector = null,
        Action? openSession = null,
        AgentSessionLaunch? agentSession = null,
        Func<IReadOnlyList<SessionMentionTarget>>? sessionMentions = null,
        Action<string, string>? sessionReceipt = null,
        Func<string?>? rootTitle = null)
    {
        Info = session;
        this.agentSession = agentSession;
        // A tab opened from the store starts at the child's stored row: its id and its name are what the tree row shows.
        agentChildSessionId = agentSession is { IsDraft: false } ? session.Id : null;
        agentName = agentSession is { IsDraft: false } ? session.Title : null;
        agentOrdinal = agentSession?.Ordinal ?? 0;
        liveRootTitle = rootTitle;
        this.runtime = runtime;
        isReadOnlyView = runtime is null;
        this.checkpoints = checkpoints;
        this.store = store;
        this.notifications = notifications;
        this.subAgentOpened = subAgentOpened;
        this.openSession = openSession;
        appPreferences = preferences;
        providerModel = providers.Catalog.Default?.DefaultModel ?? string.Empty;
        var instrumentSourcesResolved = instrumentSources ?? EmptySessionInstrumentSources.Instance;
        metricsRecorder = instrumentSourcesResolved.TurnMetricsRecorder;
        this.instrumentSources = instrumentSourcesResolved;
        Transcript = new TranscriptViewModel(collapseOlderTurns: false);

        // A failed turn that ran no tool is replayed through this session's own turn entry. The button itself lives on the
        // transcript's footnote row, so it belongs to every transcript; these two hooks are the whole of what a session
        // contributes — a session kind driven another way (a hand-created agent session) installs its own and gets the same
        // button with no transcript change.
        Transcript.CanRetryTurn = _ => IsRetryableTurn();
        Transcript.RetryTurn = RetryTurnAsync;
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
            skillSource,
            notifications: notifications,
            notificationScope: session.Id,
            appPreferences: preferences,
            // The same settings.json defaults the session's agent hosts resolve a kind's model with, so the agent chip names
            // the model the instance runs on rather than the session's own.
            agentDefaults: runtime?.AgentDefaults);
        Composer.AttachmentsChanged += RefreshCanSend;
        if (agentSession is not null)
        {
            // The chips switch to their agent-session mode here, not only when the view binds: the kind a reopened session ran
            // with has to be selected before anything reads the definition, and the view's later assignment is a no-op then.
            Composer.IsAgentSession = true;
            SelectAgentSessionKind(session.AgentKind);
            // A draft's name is the derivation by construction; a row opened from the store is recognised as still carrying its
            // default name by its shape (the store keeps no "renamed" bit), and from here on the flag alone decides.
            agentNameIsDerived = agentSession.IsDraft || TryReadDefaultName(session.Title, out agentOrdinal);
            // A kind switch renames the tab while the name is still the derivation's (see OnComposerPropertyChanged).
            Composer.PropertyChanged += OnComposerPropertyChanged;
        }
        ToggleSubAgentsCommand = new RelayCommand(() => SubAgentsCollapsed = !SubAgentsCollapsed);
        Instruments = new SessionInstrumentsViewModel(
            session,
            instrumentSourcesResolved,
            () => Composer.Model,
            () => Composer.ProviderId,
            openInspector);
        if (runtime is not null)
        {
            // The budget line is the model's view of the very gauge this panel shows; the preference can turn it off.
            runtime.ContextBudget = BudgetLine;
        }
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
        if (runtime is not null)
        {
            runtime.PromptsStaleChanged += OnRuntimePromptsStaleChanged;
            // The host can be built (and its grants written) after this tab was opened: the marks follow the table.
            runtime.SessionGrantsChanged += OnSessionGrantsChanged;
        }

        // The @ picker's session group (forge-core "session messaging"): what this tab may address, owned by the shell because it is
        // the side that knows every open and stored session of the root.
        SetSessionMentionSource(sessionMentions);
        // The shell's receipt path (forge-core "reply receipt"): a receipt that arrives at a turn boundary it never reached is
        // stored and drawn through the very path a delivered one takes, never drawn alone.
        receiptSink = sessionReceipt;
    }

    public SessionInfo Info { get; private set; }

    /// <summary>True when a prompt source changed after this session's agent host took its snapshot (and no reload was asked for since).</summary>
    public bool PromptsStale => runtime?.PromptsStale == true;

    /// <summary>Whether the prompt reload button applies: a live session, not a read-only view.</summary>
    public bool CanReloadPrompts => !disposed && !isReadOnlyView && runtime is not null;

    /// <summary>Hot reload: the next turn runs on freshly read prompts (see <see cref="SessionRuntime.HotReloadPrompts"/>).</summary>
    internal bool HotReloadPrompts() => runtime?.HotReloadPrompts() ?? false;

    /// <summary>Raised on the UI thread when <see cref="PromptsStale"/> may have changed.</summary>
    internal event Action? PromptsStaleChanged;

    /// <summary>The grant table moved (a pick applied when the host was built, or a revoke): re-read the input area's marks.</summary>
    private void OnSessionGrantsChanged() => Forge.App.Approval.UiThreadDispatcher.Instance.Post(RefreshSessionAuthorizations);

    private void OnRuntimePromptsStaleChanged() => Forge.App.Approval.UiThreadDispatcher.Instance.Post(() =>
    {
        if (disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(PromptsStale));
        PromptsStaleChanged?.Invoke();
        RunningResourcesChanged?.Invoke(this, EventArgs.Empty);
        if (PromptsStale)
        {
            // Said once per change: the flag only rises again after a reload (or a new host) cleared it.
            notifications.Notify(
                NotificationLevel.Info,
                SessionStrings.PromptsChangedTitle,
                SessionStrings.PromptsChangedBody(Info.Title),
                "prompts-stale:" + Info.Id);
        }
    });

    public string SessionId => Info.Id;

    public string Title => agentSession is { } launch
        ? CurrentAgentRootTitle(launch) + AgentSessionLabel.Separator + AgentSessionName
        : Info.Title;

    /// <summary>
    /// The tab's label. The session tab's marker is not part of it: the tab strip draws that one as a chat bubble icon for
    /// session tabs, so the title stays plain text for tooltips, automation and window titles. The unread mark goes in front
    /// of any session's label: an ended agent-session turn and a receipt another session delivered to a root raise the same
    /// one, and <see cref="Title"/> already spells an agent session's own label.
    /// </summary>
    public string TabTitle => (agentUnread ? AgentSessionLabel.UnreadMark : string.Empty) + Title;

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

    /// <summary>
    /// The context gauge's click: the same manual compaction as <c>/compact</c>, offered only while
    /// <see cref="CanCompactNow"/> holds (re-evaluated with every send-state change).
    /// </summary>
    public AsyncRelayCommand CompactCommand => compactCommand ??= new AsyncRelayCommand(_ => CompactNowAsync(), () => CanCompactNow);

    /// <summary>Messages waiting for the running turn: interjections not yet delivered, then the queue in sending order.</summary>
    public ReadOnlyObservableCollection<QueuedMessageViewModel> Queued { get; }

    public bool HasQueued => queued.Count > 0;

    /// <summary>True after Stop while messages wait: the queue resumes with the next send.</summary>
    public bool QueuePaused => queuePaused && queued.Count > 0 && !isRunning;

    public ComposerViewModel Composer { get; }

    /// <summary>The readout above the input: usage, the resource button, the project and the git markers.</summary>
    public SessionInstrumentsViewModel Instruments { get; }

    /// <summary>
    /// The trailing line a given agent reads about its own budget (the same measurement its gauge shows), or null when the
    /// preference turned it off or that agent has no instruments yet. Called on every request, from the agent loop's thread:
    /// the line is an immutable string, and the agent id selects the instance's own numbers.
    /// </summary>
    private string? BudgetLine(string agentId) =>
        appPreferences?.ChatContextBudget == false ? null : InstrumentsOf(agentId)?.ContextBudgetLine;

    /// <summary>Names of the tools this session can call; empty until its first turn built the agent host.</summary>
    internal IReadOnlyList<string> ToolNames() => runtime?.ToolNames() ?? [];

    /// <summary>The session's tool catalog for the Tools settings section; empty until the first turn built the host.</summary>
    internal IReadOnlyList<Forge.Abstractions.ToolDescriptor> DescribeTools() => runtime?.DescribeTools() ?? [];

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
        set => SetAndNotify(ref continueCommand, value, nameof(HasContinue));
    }

    public bool HasContinue => isReadOnlyView && continueCommand is not null && Info.Status != SessionStatus.Deleted;

    /// <summary>"Restore…" of a removed (deleted) read-only view; null (hidden) until the restore flow exists. Set by the shell.</summary>
    public AsyncRelayCommand? RestoreCommand
    {
        get => restoreCommand;
        set => SetAndNotify(ref restoreCommand, value, nameof(HasRestore));
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
    internal ComposerCarry TakeComposerState()
    {
        var taken = new ComposerCarry(
            draft,
            [.. queued.Select(static message => message.Prompt)],
            Composer.TakeAttachments());
        queued.Clear();
        reviewWithdrawn.Clear();
        Draft = string.Empty;
        OnQueueChanged();
        return taken;
    }

    /// <summary>Text-only form of <see cref="RestoreComposerState(ComposerCarry)"/>.</summary>
    internal void RestoreComposerState((string Draft, IReadOnlyList<string> Queued) state) =>
        RestoreComposerState(new ComposerCarry(state.Draft, [.. state.Queued.Select(ComposerPrompt.FromText)], []));

    /// <summary>Puts a carried draft and queue back (the counterpart of <see cref="TakeComposerState"/>); the queue stays paused.</summary>
    internal void RestoreComposerState(ComposerCarry state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!string.IsNullOrWhiteSpace(state.Draft))
        {
            Draft = state.Draft;
        }

        Composer.RestoreAttachments(state.DraftAttachments);
        foreach (var prompt in state.Queued)
        {
            queued.Add(new QueuedMessageViewModel(prompt, isInterjection: false, WithdrawQueued));
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
            if (SetAndNotify(ref continueBlockedReason, value, nameof(ContinueLabel)))
            {
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
    /// workspace is not in a merge conflict. Null (a headless host) lets every turn start.
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
        if (runtime is not null)
        {
            runtime.PromptsStaleChanged -= OnRuntimePromptsStaleChanged;
            runtime.SessionGrantsChanged -= OnSessionGrantsChanged;
        }

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
        private set => SetAndNotify(ref automationBanner, value, nameof(HasAutomationBanner));
    }

    public bool HasAutomationBanner => automationBanner is not null;

    /// <summary>Cancels the attached run; set by the main window together with the banner text.</summary>
    public RelayCommand CancelAutomationCommand => cancelAutomationCommand ??= new RelayCommand(() => cancelAutomation?.Invoke());

    /// <summary>Shows (or with a null text hides) the automation banner; <paramref name="cancel"/> backs its cancel button.</summary>
    public void SetAutomationBanner(string? text, Action? cancel)
    {
        cancelAutomation = cancel;
        AutomationBanner = text;
        Transcript.RefreshRetry();
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
            if (SetAndNotify(ref isRunning, value, nameof(IsIdle)))
            {
                RefreshCanSend();
                CancelCommand.NotifyCanExecuteChanged();
                // The window's prompt reload button follows the turn: it is refused while one runs.
                RunningResourcesChanged?.Invoke(this, EventArgs.Empty);
                // Either turn boundary drops what this agent left behind: at the end because a cancelled call never reports a
                // finish of its own, at the start so a snapshot parked for the previous turn cannot seed a row of this one. A
                // live child's entries are left alone - it may be streaming outside the turn (see ClearTurnTools).
                ClearTurnTools();
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
                SubAgentsChanged?.Invoke();
            }
        }
    }

    public bool HasSubAgents => subAgents.Count > 0;

    /// <summary>
    /// Whether the workbench's bottom group is showing its body for this session. The shell mirrors it from the layout, which
    /// folds a group with nothing docked on its own; the composer's chip paints its accent from here, so it never claims an
    /// open area the user cannot see, and <see cref="SubAgentsCollapsed"/> stays this session's own wish.
    /// </summary>
    public bool SubAgentRegionOpen
    {
        get => subAgentRegionOpen;
        internal set => Set(ref subAgentRegionOpen, value);
    }

    /// <summary>
    /// Whether the bottom group holds a tab this session can show or hide. The chip is drawn from
    /// <see cref="HasSubAgentActivity"/> but clickable only from here: with nothing docked the group has no body, so a click
    /// could not do anything and the chip says so instead of swallowing it.
    /// </summary>
    public bool CanToggleSubAgents
    {
        get => canToggleSubAgents;
        internal set => Set(ref canToggleSubAgents, value);
    }

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
        RaiseRunningResources();
    }

    public void Rename(string title)
    {
        if (agentSession is not null && agentSelfRenames.Remove(title))
        {
            // The store echoing a name this tab wrote itself when the agent kind changed: the name is already on the view, and the
            // store agreeing with it is not the user renaming anything - the flag that lets the name follow the kind must stay.
            return;
        }

        Info = Info with { Title = title };
        if (agentSession is not null)
        {
            agentName = title;
            OnPropertyChanged(nameof(AgentSessionName));
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(TabTitle));
        TabTitleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Replays the stored history and the agent roster at most once, and never throws. Both entry points - this one on first
    /// activation and <see cref="LoadHistoryAsync"/> for a tab the shell is about to show - share that one pass.
    /// <para>Only a read-only tab can be restored without it: the window used to replay every remembered session's whole
    /// transcript on the UI thread before it was usable, while only the tab on screen was ever looked at. Those tabs are
    /// loaded by the first activation (the shell calls this from <c>ActiveSession</c>), which shows the waiting state of
    /// <see cref="IsHistoryLoading"/> while it runs. A tab that can run turns is never deferred: a live turn would append rows
    /// under a replay that is still to come.</para>
    /// </summary>
    public Task EnsureHistoryAsync(CancellationToken ct) =>
        disposed || historyLoaded ? Task.CompletedTask : historyLoad ??= ReplayHistoryAsync(ct);

    /// <summary>True while the deferred replay of a restored tab is running; the transcript shows its waiting state.</summary>
    public bool IsHistoryLoading
    {
        get => isHistoryLoading;
        private set => Set(ref isHistoryLoading, value);
    }

    /// <summary>Whether the store still holds turns older than the page on screen (the transcript's 「载入更早」 strip).</summary>
    public bool HasOlderHistory
    {
        get => hasOlderHistory;
        private set => Set(ref hasOlderHistory, value);
    }

    /// <summary>
    /// Reads the page before the oldest round on screen and puts it in front of the transcript. The caller scrolls the round
    /// that was first back to the top afterwards, so reading continues where it was.
    /// </summary>
    public async Task LoadOlderHistoryAsync(CancellationToken ct)
    {
        if (store is null || !HasOlderHistory || loadingOlderHistory)
        {
            return;
        }

        loadingOlderHistory = true;
        try
        {
            var page = await TranscriptReplay.ReadPageAsync(store, SessionId, historyBeforeSeq, HistoryPageMessages, ct)
                .ConfigureAwait(true);
            if (page.Messages.Count == 0)
            {
                HasOlderHistory = false;
                return;
            }

            var (older, _) = TranscriptReplay.ReplayPage(page);
            Transcript.PrependRounds(older);
            historyBeforeSeq = page.Messages[0].Seq;
            HasOlderHistory = page.HasMore;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
        {
            Transcript.AddNotice(SessionStrings.ReadFailed(exception.Message), isError: true);
        }
        finally
        {
            loadingOlderHistory = false;
        }
    }

    /// <summary>Messages per history page: a page is the unit the transcript loads, reads and prepends.</summary>
    internal const int HistoryPageMessages = 100;

    private async Task ReplayHistoryAsync(CancellationToken ct)
    {
        IsHistoryLoading = true;
        try
        {
            await LoadHistoryAsync(ct).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A deferred replay has no caller to observe it: log rather than leave an unobserved task exception behind.
            AppLogs.Session.ZLogWarning($"history replay failed: {exception.Message}");
        }
        finally
        {
            // One attempt per tab: a store that failed once is not asked again on every activation.
            historyLoaded = true;
            IsHistoryLoading = false;
        }
    }

    /// <summary>
    /// Rebuilds the transcript and the finished sub-agent list from the session store, reading off the dispatcher. A tab that
    /// was restored at start-up runs this on first activation instead of during restore: see <see cref="EnsureHistoryAsync"/>.
    /// <para>One pass per tab, whichever entry point asks first. The caller that owns the pass awaits it here; one that arrives
    /// after the pass was claimed - a tab the shell has already shown, whose activation fired
    /// <see cref="EnsureHistoryAsync"/> - returns at once and is filled by that pass through the view's own loading state,
    /// because a second replay would append the same rounds to the transcript and the same stored children to the finished
    /// list on top of the first copy, which is what drew a session's sub-agent rows twice in the project tree.</para>
    /// </summary>
    public async Task LoadHistoryAsync(CancellationToken ct)
    {
        if (store is null || disposed || historyLoaded)
        {
            return;
        }

        // Set before the reads: one attempt per tab, whether it succeeds or fails.
        historyLoaded = true;
        try
        {
            var page = await TranscriptReplay
                .ReadPageAsync(store, SessionId, long.MaxValue, HistoryPageMessages, ct)
                .ConfigureAwait(true);
            Transcript.SeedRoundCounter(page.TurnsBefore);
            Instruments.SeedHistory(TranscriptReplay.Apply(Transcript, page.Messages));
            historyBeforeSeq = page.Messages.Count > 0 ? page.Messages[0].Seq : 1;
            HasOlderHistory = page.HasMore;

            // The divider tooltips price with the composer's current choice when a replayed row names no model;
            // live turns resolve the same way, so a model switch re-prices what the next hover shows.
            Transcript.ResolveCost = ResolveTurnCost;

            await RestoreSubAgentsAsync(SessionId, "root", ct).ConfigureAwait(true);

            SyncFinishedMenu();

            OnPropertyChanged(nameof(HasSubAgents));
            OnPropertyChanged(nameof(HasSubAgentActivity));
            RaiseSubAgentCounters();

            // The replayed roster is what the project tree draws its agent rows (and the session's chevron) from, so the
            // owners have to hear about it: without this a restored session kept no agent rows until something else
            // refreshed the whole tree.
            SubAgentsChanged?.Invoke();

            // The grants live in the host's memory, so a reopened tab of the same root reads back the marks it had; a tab
            // whose host was never built shows none, which is exactly what a restart means for this table.
            RefreshSessionAuthorizations();
            OnPropertyChanged(nameof(HasSessionAuthorizations));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
        {
            Transcript.AddNotice(SessionStrings.ReadFailed(exception.Message), isError: true);
        }
    }

    /// <summary>
    /// Prices one turn's snapshot for a divider tooltip at the model's own rates; null when the model is unknown or the
    /// lookup fails. A row without a recorded model falls back to the composer's current choice — usually the model that
    /// ran the turn — and a standalone view has no sources at all, so its tooltips read "—".
    /// </summary>
    private string? ResolveTurnCost(TokenUsage usage, string? provider, string? model)
    {
        try
        {
            var metadata = instrumentSources.ModelMetadata(
                string.IsNullOrEmpty(model) ? Composer.Model : model,
                string.IsNullOrEmpty(provider) ? Composer.ProviderId : provider);
            return SessionUsageMath.Cost(usage, metadata.Pricing) is { } cost
                ? TurnDividerText.FormatCost(cost)
                : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rebuilds one level of the agent hierarchy and then descends into it: a stored child session's id is the parent
    /// link of its own children, and the children of this session hang under the main agent ("root"). Statuses come from
    /// what the store holds; when it holds nothing conclusive the row says it ended without claiming a result.
    /// </summary>
    /// <summary>
    /// The one replay sequence both a main session's open and a history sub-agent's open run: read the stored messages,
    /// draw them, and seed the dividers from the turn metrics the session recorded. Returns the usage the replay summed,
    /// for the caller's own instruments to seed. Reading and seeding failures are guarded here once: metrics only fill
    /// blanks the replay cannot, so a lost read leaves "—", never a notice.
    /// </summary>
    private async Task<TokenUsage> ReplayIntoAsync(TranscriptViewModel transcript, string sessionId, CancellationToken ct)
    {
        if (store is null)
        {
            return TokenUsage.Empty;
        }

        var messages = await TranscriptReplay.ReadAsync(store, sessionId, ct).ConfigureAwait(true);
        if (messages.Count == 0)
        {
            return TokenUsage.Empty;
        }

        var total = TranscriptReplay.Apply(transcript, messages);
        if (instrumentSources.TurnMetrics is { } turnMetrics)
        {
            try
            {
                transcript.SeedTurnMetrics(await turnMetrics.ListAsync(sessionId, ct).ConfigureAwait(true));
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException
                or ObjectDisposedException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
            {
                AppLogs.Session.ZLogWarning($"turn metrics seed skipped: {exception.GetType().Name}: {exception.Message}");
            }
        }

        return total;
    }

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
            // A session the user opened under this one (an "Agent session") is a session row of its own beside the root, with
            // its own tab: listing it here would draw the same session twice and hang the sub-agents it spawned under the
            // root instead of under its own row. The tree plans those rows from the child links itself (PlanAgentSessions).
            if (string.Equals(child.ChildDriver, AgentSessionKinds.ChildDriverUser, StringComparison.Ordinal))
            {
                continue;
            }

            var status = await BackgroundRead.RunAsync(token => DeriveStoredStatusAsync(child.Id, token), ct)
                .ConfigureAwait(true);
            finished.Add(SubAgentViewModel.FromStored(SessionId, child, parentAgentId, status, Title, openSession, instrumentSources));
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
        foreach (var agent in subAgents)
        {
            agent.Tick();
        }

        TickLiveTools();
    }

    /// <summary>
    /// Stops the running turn; queued messages stay and wait for the next send. On a root tab the stop reaches the whole tree:
    /// the user sessions opened under this root stop with it, and an interjection they never saw is not delivered later.
    /// </summary>
    public void Cancel()
    {
        queuePaused = true;
        redirectText = null;
        CancelTurn();
        if (agentSession is null)
        {
            // The root's stop reaches the whole tree: the agent sessions it hosts stop with it (the same control surface the
            // model's stop tool uses). Disposing the view is not a stop: closing a root tab leaves its agent sessions running,
            // and their runtime is kept alive for exactly that.
            if (!disposed)
            {
                runtime?.StopUserChildren(SessionStrings.SubAgentStoppedByUser);
            }
        }
        else if (!disposed && Volatile.Read(ref agentAgentId) is { Length: > 0 } agentId)
        {
            // The child's own stop goes through the host's control surface as well, so the run ends the way a stop always
            // ends (partial text kept, the session reported as stopped) instead of being cut off at the token.
            runtime?.StopUserChild(agentId, SessionStrings.SubAgentStoppedByUser);
        }

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
        // Picking one from the finished list is an explicit "show me": a minimized bottom group opens.
        SubAgentsCollapsed = false;
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
            var total = await ReplayIntoAsync(agent.Transcript, agent.ChildSessionId, CancellationToken.None).ConfigureAwait(true);
            if (agent.Transcript.IsEmpty)
            {
                // The store kept nothing for this child: the tab says so rather than showing an empty timeline.
                agent.Transcript.AddNotice(SessionStrings.FinishedAgentsNoTranscript, isError: true);
                return;
            }

            agent.Instruments.SeedHistory(total);
            if (agent.Transcript.Current is { ModelName: { Length: > 0 } storedModel } last)
            {
                agent.SeedStoredModel(last.ProviderName, storedModel);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
        {
            agent.Transcript.AddNotice(SessionStrings.SubAgentReplayFailed(exception.Message), isError: true);
        }
    }

    /// <summary>
    /// Idle with a draft to send, or with queued messages waiting (an empty send resumes the queue). A draft whose
    /// attachments the current model or provider cannot take blocks sending until they are removed (the composer shows why).
    /// </summary>
    private bool EvaluateCanSend() =>
        !isReadOnlyView && !isHandingOver && !isRunning && !gating && !compacting &&
        (HasDraft() ? !Composer.AttachmentsBlocked : HasSendableQueue());

    /// <summary>Something typed or attached: either is enough to send.</summary>
    private bool HasDraft() => !string.IsNullOrWhiteSpace(draft) || Composer.HasAttachments;

    /// <summary>Takes the draft (trimmed, command-expanded text and the attachments) out of the composer; null when empty.</summary>
    private ComposerPrompt? TakeDraft()
    {
        if (!HasDraft())
        {
            return null;
        }

        var text = draft.Trim();
        Draft = string.Empty;
        // The session mentions the user picked travel with the message (queue, interjection and redirect alike) and are the
        // whole authorization for it: a mention taken here is a grant, one the user deleted from the text is not.
        var mentions = Composer.TakeMentions(text);
        AuthorizeMentions(mentions);
        return new ComposerPrompt(Composer.ExpandCommand(text), Composer.TakeAttachments(), Composer.TakeOrchestration())
        {
            Mentions = mentions,
        };
    }

    private bool HasSendableQueue() => queued.Any(message => !message.IsInterjection);

    private bool CanSendWhileRunning() => !isReadOnlyView && !isHandingOver && isRunning && HasDraft() && !Composer.AttachmentsBlocked;

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
        if (isRunning || compacting)
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

        // Every input of CanCompactNow (running, gating, handing over, compacting, read-only) changes through here.
        OnPropertyChanged(nameof(CanCompactNow));
        compactCommand?.NotifyCanExecuteChanged();

        // Same inputs decide whether a failed turn may be replayed.
        Transcript.RefreshRetry();
    }

    private async Task SendAsync(CancellationToken ignored)
    {
        var typed = draft.Trim();
        if (TryParseCompactCommand(typed, out var focus) && !Composer.HasAttachments)
        {
            Draft = string.Empty;
            queuePaused = false;
            await CompactNowAsync(focus).ConfigureAwait(true);
            return;
        }

        ComposerPrompt prompt;
        if (TakeDraft() is { } composed)
        {
            prompt = composed;
        }
        else if (TakeQueued() is { } next)
        {
            prompt = next;
            typed = next.Text;
        }
        else
        {
            return;
        }

        queuePaused = false;
        await RunTurnAsync(prompt).ConfigureAwait(true);
    }

    // --- compaction (feature B) ---
    /// <summary>The composer command that compacts the root context now instead of sending a message.</summary>
    public const string CompactCommandText = "/compact";

    private bool compacting;

    internal static bool IsCompactCommand(string text) => TryParseCompactCommand(text, out _);

    /// <summary>
    /// <c>/compact</c> (command word in any case) alone or followed by whitespace and focus text; <paramref name="focus"/> is
    /// that text trimmed, null when there is none. <c>/compactfoo</c> is not the command.
    /// </summary>
    internal static bool TryParseCompactCommand(string text, out string? focus)
    {
        focus = null;
        var trimmed = text.AsSpan().Trim();
        if (!trimmed.StartsWith(CompactCommandText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = trimmed[CompactCommandText.Length..];
        if (rest.IsEmpty)
        {
            return true;
        }

        if (!char.IsWhiteSpace(rest[0]))
        {
            return false;
        }

        var remainder = rest.Trim();
        focus = remainder.IsEmpty ? null : remainder.ToString();
        return true;
    }

    /// <summary>Whether a manual compaction can start: a live, idle session (no turn, no compaction under way).</summary>
    public bool CanCompactNow => !disposed && !isReadOnlyView && runtime is not null && !isRunning && !gating && !isHandingOver && !compacting;

    /// <summary>
    /// Manual compaction (<c>/compact [focus]</c>, the context gauge): summarizes everything but the newest keepRecent tokens
    /// regardless of the threshold, with <paramref name="focus"/> as additional summarizer focus, and re-enables automatic
    /// compaction. Sending stays blocked while it runs; the outcome is a notice.
    /// </summary>
    public async Task CompactNowAsync(string? focus = null)
    {
        if (disposed || isReadOnlyView || runtime is not { } live)
        {
            return;
        }

        if (!CanCompactNow)
        {
            Transcript.AddNotice(CompactionStrings.Busy(), isError: true);
            return;
        }

        compacting = true;
        RefreshCanSend();
        AgentContextMeasured? measured = null;
        try
        {
            // The measurement arrives on a worker thread; it is only read after the await, back on the UI thread.
            var result = await live.CompactNowAsync(
                value =>
                {
                    if (value is AgentContextMeasured context && string.Equals(context.AgentId, "root", StringComparison.Ordinal))
                    {
                        Volatile.Write(ref measured, context);
                    }
                },
                focus,
                CancellationToken.None).ConfigureAwait(true);
            if (result is null)
            {
                Transcript.AddNotice(CompactionStrings.Nothing());
            }
            else
            {
                Transcript.AddNotice(SessionStrings.Compacted(result.TokensBefore, result.TokensAfter));
                if (Volatile.Read(ref measured) is { } context)
                {
                    Instruments.NoteContext(context.AgentId, context.ContextTokens, context.UsableTokens, context.TriggerTokens);
                }
                else
                {
                    Instruments.NoteCompacted(result.TokensAfter);
                }
            }
        }
        catch (InvalidOperationException)
        {
            Transcript.AddNotice(CompactionStrings.Busy(), isError: true);
        }
        catch (Exception exception) when (exception is CompactionFailedException or ProviderException or IOException or DbException or ObjectDisposedException)
        {
            Transcript.AddNotice(CompactionStrings.Failed(exception.Message), isError: true);
        }
        finally
        {
            compacting = false;
            RefreshCanSend();
            ScheduleNext();
        }
    }
    // --- end compaction ---

    private void Queue()
    {
        if (TakeDraft() is not { } prompt)
        {
            return;
        }

        AddQueued(new QueuedMessageViewModel(prompt, isInterjection: false, WithdrawQueued), queued.Count);
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

        var message = new QueuedMessageViewModel(text.Trim(), isInterjection: false, WithdrawQueued);
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

    /// <summary>False once nothing may be delivered here any more; the same condition as <see cref="IsAutomationClosed"/>.</summary>
    public bool CanHandOff => !IsAutomationClosed;

    /// <summary>
    /// Delivers a prepared message (the checks panel's fix request, a Mermaid block's failed diagram): a turn when the session
    /// is idle, otherwise the composer's draft, which is what a busy session can accept without a second turn starting behind
    /// the user's back. Returns true when a turn ran.
    /// </summary>
    public async Task<bool> HandMessageAsync(string message, LocText origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!CanHandOff)
        {
            return false;
        }

        if (!IsAutomationIdle)
        {
            QueueHandOff(message);
            return false;
        }

        try
        {
            var result = await RunAutomationTurnAsync(message, origin, agent: null).ConfigureAwait(true);
            if (result.Status is MailboxStatus.Completed or MailboxStatus.TurnFailed)
            {
                return true;
            }

            // Withdrawn between the idle check and the turn (the tab was taken over, archived or removed): the message was
            // not delivered and must not be lost, so it goes where a draft would.
            QueueHandOff(message);
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Session.ZLogWarning($"hand-off failed: {exception.Message}");
            QueueHandOff(message);
            return false;
        }
    }

    /// <summary>Queues a handed-over message after the user's draft instead of replacing it.</summary>
    private void QueueHandOff(string message) => Draft = ComposerDraft.Append(Draft, message);

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
            await RunTurnAsync(ComposerPrompt.FromText(text)).ConfigureAwait(true);
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
            ? live.SpawnChildAsync(kind, task, OnAutomationChildEvent, ct)
            : Task.FromException<ChildAgentResult>(new InvalidOperationException(AutomationStrings.SessionUnavailableText));

    /// <summary>Remembers which child an automation launched (its direct child of the session's agent), then handles the event as usual.</summary>
    private void OnAutomationChildEvent(AgentEvent value)
    {
        if (value is AgentSubAgentStarted { AgentId: "root" } started)
        {
            automationChildIds[started.ChildId] = 0;
        }

        OnAgentEvent(value);
    }

    private void Interject()
    {
        if (TakeDraft() is not { } prompt)
        {
            return;
        }

        // Attachments travel with the interjection: the loop delivers text and attachments together at the next tool-batch
        // boundary. When no turn takes it, the message is queued normally. The chip keeps the instance, so the message can be
        // withdrawn (back into the composer) right up to the moment the loop hands it to the model. An agent-session tab never
        // interjects: the root's interjection inbox belongs to the root's own conversation, and the child's run has no such
        // boundary yet.
        var delivered = agentSession is null && isRunning && runtime is { } live
            ? live.TryInterject(prompt.Text, prompt.Attachments)
            : null;
        var front = queued.TakeWhile(message => message.IsInterjection).Count();
        AddQueued(
            new QueuedMessageViewModel(prompt, isInterjection: delivered is not null, WithdrawQueued, delivered),
            delivered is not null ? front : 0);
        if (!isRunning)
        {
            ScheduleNext();
        }
    }

    private void Redirect()
    {
        if (TakeDraft() is not { } prompt)
        {
            return;
        }

        if (!isRunning)
        {
            queuePaused = false;
            AddQueued(new QueuedMessageViewModel(prompt, isInterjection: false, WithdrawQueued), 0);
            ScheduleNext();
            return;
        }

        redirectText = prompt;
        CancelTurn();
    }

    private void AddQueued(QueuedMessageViewModel message, int index)
    {
        queued.Insert(Math.Clamp(index, 0, queued.Count), message);
        OnQueueChanged();
    }

    /// <summary>
    /// Takes a waiting message back into the composer: a queued one leaves the queue, a pending interjection leaves the
    /// running turn as well (only while the loop has not delivered it — once the model has it, nothing can undo it), and both
    /// hand their text and attachments back for editing. The text appends to what the user already typed, never replaces it.
    /// </summary>
    private void WithdrawQueued(QueuedMessageViewModel message)
    {
        // Checked before anything is taken: a refusal has to leave both the chip and the composer exactly as they were.
        if (!CanRestoreAttachments(message))
        {
            RefuseWithdraw(SessionStrings.ComposerWithdrawAttachmentsFull, "withdraw-attachments");
            return;
        }

        if (message.Interjection is { } interjection && runtime?.TryWithdraw(interjection) != true)
        {
            // The loop took it at a tool-batch boundary: the chip goes away on its own when AgentInterjected lands, so the
            // message is not restored (that would send it twice) and the user is told why nothing came back.
            RefuseWithdraw(SessionStrings.ComposerInterjectDelivered, "interject-delivered");
            return;
        }

        queued.Remove(message);
        OnQueueChanged();
        NotifyWithdrawn(message);

        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            Draft = ComposerDraft.Append(Draft, message.Text);
        }

        Composer.RestoreAttachments(message.Attachments);
    }

    /// <summary>
    /// Whether the composer can take everything this message carries back. Images and files are capped per message and a
    /// withdrawal never drops one silently — an attachment lost on the way back would be gone for good.
    /// </summary>
    private bool CanRestoreAttachments(QueuedMessageViewModel message)
    {
        if (!message.HasAttachments)
        {
            return true;
        }

        var images = 0;
        var files = 0;
        foreach (var block in message.Attachments)
        {
            if (block is ImageBlock)
            {
                images++;
            }
            else
            {
                files++;
            }
        }

        return Composer.Attachments.Count + images <= ImageAttachmentRules.MaxImagesPerMessage
            && Composer.Files.Count + files <= FileAttachmentRules.MaxFilesPerMessage;
    }

    /// <summary>Says why a waiting message stays where it is; the message itself is untouched.</summary>
    private void RefuseWithdraw(LocText reason, string key) =>
        notifications.Notify(
            NotificationLevel.Warning,
            NotificationsStrings.WithdrawRefusedTitle,
            reason,
            key + ":" + SessionId);

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
    private ComposerPrompt? TakeQueued()
    {
        var next = queued.FirstOrDefault(message => !message.IsInterjection);
        if (next is null)
        {
            return null;
        }

        queued.Remove(next);
        reviewWithdrawn.Remove(next);
        OnQueueChanged();
        return next.Prompt;
    }

    /// <summary>
    /// After a turn: interjections the agent never reached go back to the front of the queue, then the redirect message
    /// or the head of the queue is sent on the next dispatcher pass (unless Stop paused the queue).
    /// </summary>
    private void ScheduleNext()
    {
        if (!isRunning)
        {
            // The undelivered interjections are the root instance's inbox, so only the root's own tab may take them: an agent
            // session's run has its own leftovers, and they are routed by TakeUndeliveredReplies. A child tab that took them
            // would draw the root's receipts in its own transcript and lose them to the session they belong to.
            IReadOnlyList<Interjection> undelivered = agentSession is null
                ? runtime?.TakeUndeliveredInterjections() ?? []
                : [];
            foreach (var leftover in queued.Where(message => message.IsInterjection).ToList())
            {
                queued.Remove(leftover);
            }

            for (var i = undelivered.Count - 1; i >= 0; i--)
            {
                var leftover = undelivered[i];
                if (SessionReplyViewModel.IsReply(leftover.Text))
                {
                    // A receipt the running turn did not reach its boundary for: it goes through the shell's idle path
                    // (written to the history, then drawn) and never becomes the next queued turn - that is the unattended
                    // A->B->A loop. Drawing it alone would lose it to the next turn's context and to a restart.
                    DeliverSessionReply(leftover.Text);
                    continue;
                }

                queued.Insert(
                    0,
                    new QueuedMessageViewModel(
                        new ComposerPrompt(leftover.Text, [.. leftover.Attachments]),
                        isInterjection: false,
                        WithdrawQueued));
            }

            OnQueueChanged();
        }

        if (disposed || isRunning || isReadOnlyView || compacting)
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

    private async Task RunNextAsync(ComposerPrompt? redirect)
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
                AddQueued(new QueuedMessageViewModel(redirect, isInterjection: false, WithdrawQueued), 0);
            }

            return;
        }

        var prompt = redirect ?? (queuePaused ? null : TakeQueued());
        if (prompt is null)
        {
            return;
        }

        try
        {
            await RunTurnAsync(prompt).ConfigureAwait(true);
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
        // An agent-session tab runs its own child, not the session's root: handing the composer's pick to the runtime would
        // change the root agent of the tab the user never touched. The child's kind stays the one its tab was opened with
        // (switching it rebuilds the instance, which the composer's chip columns own).
        if (agentSession is not null)
        {
            return;
        }

        if (runtime?.SetRootDefinition(definition) == true)
        {
            rootRebuilt = true;
        }
    }

    private async Task RunTurnAsync(ComposerPrompt prompt)
    {
        if (agentSession is not null)
        {
            await RunAgentSessionTurnAsync(prompt).ConfigureAwait(true);
            return;
        }

        var text = prompt.Text;
        if (!await PassesTurnGateAsync(prompt).ConfigureAwait(true))
        {
            return;
        }

        var live = runtime!;
        turnRan = true;

        // This turn's own failure starts empty. The field is written on the agent's thread as each event arrives (and cleared
        // here), so what the turn end reads belongs to this turn: a stale text would make a turn that went through look failed.
        turnRootFailure = null;
        if (rootRebuilt)
        {
            rootRebuilt = false;
            agentUsage.Remove("root");
            agentStream.Remove("root");
        }

        TurnStarted?.Invoke(this, automationTurn);
        turnStartedAt = DateTimeOffset.Now;
        Interlocked.Exchange(ref lastActivityTicks, turnStartedAt.UtcTicks);
        Interlocked.Exchange(ref runningTools, 0);
        turnStartCounters = live.ReadRootCounters();
        turnToolCalls = 0;
        Instruments.RefreshCounters();
        Transcript.AddUserMessage(text, turnStartedAt, prompt.Attachments);
        var card = Transcript.BeginTurn(turnStartedAt);
        turnCard = card;

        // Kept on the card, not in a field: a retry re-sends exactly this turn's text and attachments, and a card that never
        // had a prompt (a replayed one) must not offer one.
        card.Prompt = prompt;
        card.NoteModel(
            Composer.ProviderId is { Length: > 0 } provider ? provider : null,
            Composer.Model is { Length: > 0 } model ? model : null);
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
                    NoteCheckpointFailure(SessionId, exception);
                }
            }

            await live.RunTurnAsync(OrchestrationDirective.Apply(prompt.Orchestration, text), prompt.Attachments, OnAgentEvent, token).ConfigureAwait(true);
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
                var turnElapsed = DateTimeOffset.Now - turnStartedAt;
                Transcript.EndTurn(turnElapsed, error);

                // The turn's own numbers, handed over as one struct channel write: no await, no query, no formatting, and a
                // recorder that is missing or has no table costs nothing. The denominator of the strip counts every turn,
                // so it is told here (a failed or unreported turn is still a turn the averages are spread over) - with the
                // turn's own clock, so the summary's duration grows with the window instead of waiting for a reopen.
                Instruments.NoteTurnEnded(turnNo, turnElapsed);
                RecordTurnMetrics(turnNo, error);
                if (checkpoints is not null)
                {
                    try
                    {
                        await Task.Run(() => checkpoints.CompleteAsync(SessionId, turnNo, WorkDir, CancellationToken.None), CancellationToken.None)
                            .ConfigureAwait(true);
                    }
                    catch (Exception exception) when (IsCheckpointFailure(exception))
                    {
                        NoteCheckpointFailure(SessionId, exception);
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
    private async Task<bool> PassesTurnGateAsync(ComposerPrompt text)
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

        void Restore(ComposerPrompt rejected)
        {
            if (!HasDraft())
            {
                Draft = rejected.Text;
                Composer.RestoreAttachments(rejected.Attachments);
            }
            else
            {
                AddQueued(new QueuedMessageViewModel(rejected, isInterjection: false, WithdrawQueued), 0);
            }
        }
    }

    /// <summary>Failures of the git snapshot or of the checkpoint store; the turn continues and the card says so.</summary>
    private static bool IsCheckpointFailure(Exception exception) =>
        exception is GitException or InvalidOperationException or IOException or InvalidDataException or ArgumentException or
        DbException or UnauthorizedAccessException;

    /// <summary>
    /// A checkpoint write or read for <paramref name="sessionId"/> failed: the turn itself goes on, the transcript says so
    /// inline, and the user is told once as a warning - a turn whose diff went missing is otherwise silent.
    /// </summary>
    private void NoteCheckpointFailure(string sessionId, Exception exception)
    {
        Transcript.AddNotice(SessionStrings.CheckpointFailed(exception.Message), isError: true);
        notifications.Notify(
            NotificationLevel.Warning,
            NotificationsStrings.TurnFailedTitle,
            SessionStrings.CheckpointFailed(exception.Message),
            "checkpoint:" + sessionId);
    }

    private void OnSendFailed(Exception exception)
    {
        Transcript.AddNotice(SessionStrings.TurnFailed(exception.Message), isError: true);
        notifications.Notify(
            NotificationLevel.Error,
            NotificationsStrings.TurnFailedTitle,
            SessionStrings.TurnFailed(exception.Message),
            "turn-failed:" + SessionId);
    }

    /// <summary>
    /// Whether the failed turn's retry may run now: the session has to be writable and quiet, and no automation run may be
    /// attached — a <c>builtin.quota-resume</c> waiting for the reset already is the retry, and two would fight over the turn.
    /// </summary>
    private bool IsRetryableTurn() =>
        !disposed && !isReadOnlyView && !isRunning && !gating && !compacting && !isHandingOver && !HasAutomationBanner;

    /// <summary>
    /// Replays the failed turn: the same text and attachments the user sent, as one new user message and one new turn, through
    /// the ordinary turn path — so the turn gate, the queue's pause state and every failure path stay exactly as they are.
    /// </summary>
    private async Task RetryTurnAsync(TurnCardViewModel card)
    {
        if (card.Prompt is not { } prompt || !IsRetryableTurn())
        {
            return;
        }

        try
        {
            await RunTurnAsync(prompt).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The turn's own failures are already reported by RunTurnAsync; this is everything else (store, checkpoints host).
            OnSendFailed(exception);
        }
    }

    /// <summary>Worker-thread event sink; text is merged off-thread, everything else is queued to the frame coalescer.</summary>
    private void OnAgentEvent(AgentEvent value)
    {
        Interlocked.Exchange(ref lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
        if (value is AgentToolStarted)
        {
            Interlocked.Increment(ref runningTools);
            if (string.Equals(value.AgentId, "root", StringComparison.Ordinal))
            {
                // Counted here — on the event thread — rather than in TrackTool, so a deferred Background flush cannot
                // make the turn's finally miss a call.
                Interlocked.Increment(ref turnToolCalls);
            }
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
                RouteDelta(delta.AgentId, delta.Text, earlyDeltas, static (transcript, text) => transcript.QueueDelta(text));
                break;
            case AgentThinkingDelta thinking:
                // Reasoning is content of the agent's own turn (wave W17a), routed and parked exactly like its reply text.
                RouteDelta(
                    thinking.AgentId,
                    thinking.Text,
                    earlyThinking,
                    static (transcript, text) => transcript.QueueThinkingDelta(text));
                break;
            case AgentToolProgress progress:
                // Not queued: a snapshot supersedes the one before it, the producer already throttles, and the UI reads the
                // latest one on its own tick (see SessionWorkspace.Tools.cs).
                NoteToolTail(progress.AgentId, progress.Call, progress.Tail);
                break;
            default:
                coalescer.Enqueue(value);
                break;
        }
    }

    /// <summary>
    /// One delta of an agent whose tab may not exist yet: it is parked in <paramref name="early"/> when the tab is missing,
    /// because a child's first deltas can overtake the <c>AgentSubAgentStarted</c> that is still queued for the dispatcher,
    /// and dropping them loses the start of the answer or of the reasoning.
    /// </summary>
    private void RouteDelta(
        string agentId,
        string text,
        ConcurrentDictionary<string, StringBuilder> early,
        Action<TranscriptViewModel, string> merge)
    {
        if (Target(agentId) is { } target)
        {
            merge(target, text);
            coalescer.RequestFlush();
            return;
        }

        var buffer = early.GetOrAdd(agentId, static _ => new StringBuilder());
        lock (buffer)
        {
            if (buffer.Length < EarlyDeltaLimit)
            {
                buffer.Append(text, 0, Math.Min(text.Length, EarlyDeltaLimit - buffer.Length));
            }
        }
    }

    /// <summary>Moves the parked text and reasoning of a starting agent into its transcript, oldest first.</summary>
    private void DrainEarlyDeltas(SubAgentViewModel agent)
    {
        Drain(agent, earlyDeltas, static (transcript, text) => transcript.QueueDelta(text));
        Drain(agent, earlyThinking, static (transcript, text) => transcript.QueueThinkingDelta(text));
        agent.Transcript.FlushPending();

        static void Drain(SubAgentViewModel agent, ConcurrentDictionary<string, StringBuilder> early, Action<TranscriptViewModel, string> merge)
        {
            if (!early.TryRemove(agent.AgentId, out var buffer))
            {
                return;
            }

            lock (buffer)
            {
                if (buffer.Length > 0)
                {
                    merge(agent.Transcript, buffer.ToString());
                }
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
                // The row always appears; the live state only for a call that may still be running (see TrackTool: the session's
                // own agent needs a turn, a live child does not). The session clock reads the tail, so nothing is dispatched.
                if (Card(started.AgentId)?.AddTool(started.Call) is { } row && TrackTool(started.AgentId, started.Call) is { } activity)
                {
                    row.AttachLive(activity);
                }

                // The child's per-turn tool count lives here, on the dispatcher, where the events arrive in order: counting on
                // the producer thread would let a next turn's first call race ahead of the flush that closes this one.
                if (!string.Equals(started.AgentId, "root", StringComparison.Ordinal))
                {
                    subAgentToolCalls.AddOrUpdate(started.AgentId, 1, static (_, count) => count + 1);
                }

                break;
            case AgentToolFinished finishedTool:
                FinishTool(finishedTool.AgentId, finishedTool.Call.Id);
                Card(finishedTool.AgentId)?.CompleteTool(finishedTool.Call, finishedTool.Result);
                break;
            case AgentTurnCompleted completed:
                // The turn-start snapshots, read before AddTurnUsage replaces the instance counters: the child's turn row
                // differences the same pair the root's row does.
                var startUsage = agentUsage.TryGetValue(completed.AgentId, out var turnStartUsage) ? turnStartUsage : TokenUsage.Empty;
                var startStream = agentStream.TryGetValue(completed.AgentId, out var turnStartStream) ? turnStartStream : TimeSpan.Zero;
                AddTurnUsage(completed);
                CloseSubAgentTurn(completed, startUsage, startStream);
                break;
            case AgentNotice notice:
                // A note about how the turn went, not a failure: a toast, never an error card.
                notifications?.Notify(
                    NotificationLevel.Warning,
                    SessionStrings.ProviderNoticeTitle,
                    LocText.Literal(notice.Text),
                    "provider-notice:" + SessionId);
                Card(notice.AgentId)?.AddNotice(LocText.Literal(notice.Text), isError: false);
                break;
            case AgentRateLimitUpdated limit:
                // The account's windows, taken from whichever agent's request reported them: the meter is per account, not
                // per agent. The root's strip prices against the composer's provider; a child's tab reads the account of
                // the provider its own model runs on.
                InstrumentsOf(limit.AgentId)?.NoteRateLimit(
                    string.Equals(limit.AgentId, "root", StringComparison.Ordinal) ? Composer.ProviderId : Find(limit.AgentId)?.ProviderId,
                    limit.RateLimit);
                break;
            case AgentBudgetWait waiting:
                // A wait on the provider's local budget is why the turn is quiet, not something to interrupt the user
                // with: a line on the running turn, never a toast and never an error. Card() begins the root's turn when
                // no card exists yet, so the first request's wait has a place to show itself too.
                Card(waiting.AgentId)?.AddNotice(
                    RateLimitStrings.Waiting(waiting.ProviderId, waiting.Wait), isError: false);
                break;
            case AgentApprovalDenied denied:
                Card(denied.AgentId)?.AddNotice(
                    SessionStrings.Denied(denied.Call.Name), isError: true, kind: TimelineNodeKind.Approval);
                break;
            case AgentCompacted compacted:
                Card(compacted.AgentId)?.AddNotice(SessionStrings.Compacted(compacted.TokensBefore, compacted.TokensAfter));
                break;
            case AgentContextMeasured context:
                // A sub-agent has its own window and its own budget: its tab reads the same event Core sent it, through
                // the same instruments class the root's strip reads the root's from.
                var instruments = InstrumentsOf(context.AgentId);
                instruments?.NoteContext(context.AgentId, context.ContextTokens, context.UsableTokens, context.TriggerTokens);
                instruments?.NoteCacheTtl(context.CacheTtl);
                break;
            case AgentContextReduced reduced:
                // The measurement that follows settles which side of the receipts/summary split this pass counts on.
                InstrumentsOf(reduced.AgentId)?.NoteReduction(
                    reduced.AgentId,
                    reduced.TokensBefore,
                    reduced.TokensAfter,
                    reduced.DroppedResults,
                    reduced.ReadTokens,
                    reduced.BashTokens,
                    reduced.ImageTokens,
                    reduced.OtherTokens);
                break;
            case AgentCompactionStalled stalled:
                Card(stalled.AgentId)?.AddNotice(
                    stalled.Reason == CompactionStallReason.Irreducible
                        ? CompactionStrings.Blocked(stalled.ContextTokens, stalled.TriggerTokens)
                        : CompactionStrings.Stalled(stalled.ContextTokens, stalled.TriggerTokens),
                    isError: true);
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
        var attachments = interjected.Attachments ?? [];
        if (FindInterjectionChip(queued, interjected.Text, attachments) is { } chip)
        {
            queued.Remove(chip);
            OnQueueChanged();
        }

        if (SessionReplyViewModel.IsReply(interjected.Text))
        {
            // A session reply that reached this turn at its tool-batch boundary: the model reads it as a user message (the
            // loop persisted it), and the transcript shows it as the card it is rather than as a plain interjected notice.
            ShowSessionReply(interjected.Text, unseen: false);
            return;
        }

        // Marked as the user's own words: the note reads at body size in the primary colour, not as a muted caption.
        Card(interjected.AgentId)?.AddNotice(
            SessionStrings.ComposerInterjectedNotice(interjected.Text, attachments),
            fromUser: true);
    }

    /// <summary>
    /// The pending interjection chip a delivered interjection belongs to: the first one with the same typed text (blank
    /// reads as none) and the very same attachment blocks — the loop reports the instances the chip handed over, so two
    /// attachment-only interjections are told apart. When no chip holds those instances (a chip carried over from another
    /// view), the first with the same text and attachment count is taken.
    /// </summary>
    internal static QueuedMessageViewModel? FindInterjectionChip(
        IEnumerable<QueuedMessageViewModel> chips,
        string text,
        IReadOnlyList<ContentBlock> attachments)
    {
        QueuedMessageViewModel? fallback = null;
        foreach (var chip in chips)
        {
            if (!chip.IsInterjection || !SameTypedText(chip.Text, text) || chip.Attachments.Count != attachments.Count)
            {
                continue;
            }

            if (SameBlocks(chip.Attachments, attachments))
            {
                return chip;
            }

            fallback ??= chip;
        }

        return fallback;
    }

    private static bool SameTypedText(string chipText, string deliveredText) =>
        string.IsNullOrWhiteSpace(chipText)
            ? string.IsNullOrWhiteSpace(deliveredText)
            : string.Equals(chipText, deliveredText, StringComparison.Ordinal);

    private static bool SameBlocks(IReadOnlyList<ContentBlock> left, IReadOnlyList<ContentBlock> right)
    {
        for (var index = 0; index < left.Count; index++)
        {
            if (!ReferenceEquals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <see cref="AgentTurnCompleted"/> carries the usage accumulated by that agent instance over all its turns, so the
    /// card gets the difference to the previous turn; a provider that reported nothing leaves the card showing "—".
    /// The same event carries the instance's cumulative streaming time, differenced the same way into the card's
    /// <see cref="TurnCardViewModel.StreamElapsed"/> — the divider's output rate divides by that, never by the wall clock.
    /// <para>The metrics row does not read this difference: it differences two snapshots of the instance counter itself
    /// (the root instance's live counters against <see cref="turnStartCounters"/>), so the row and the card agree but do not
    /// depend on the same flush timing, and a failed or stopped turn still records what it spent.</para>
    /// </summary>
    private void AddTurnUsage(AgentTurnCompleted completed)
    {
        var previous = agentUsage.TryGetValue(completed.AgentId, out var stored) ? stored : TokenUsage.Empty;
        agentUsage[completed.AgentId] = completed.Usage;
        var root = string.Equals(completed.AgentId, "root", StringComparison.Ordinal);
        var delta = new TokenUsage(
            Math.Max(0, completed.Usage.InputTokens - previous.InputTokens),
            Math.Max(0, completed.Usage.OutputTokens - previous.OutputTokens),
            Math.Max(0, completed.Usage.CacheReadTokens - previous.CacheReadTokens),
            Math.Max(0, completed.Usage.CacheWriteTokens - previous.CacheWriteTokens),
            Math.Max(0, completed.Usage.CacheWrite1hTokens - previous.CacheWrite1hTokens));

        // The same difference for the streaming time. The counter is kept in step before the "nothing reported" return
        // below, so a later event's difference can never swallow this event's time, and the card is addressed without
        // Card(): a completion whose tokens stayed unknown still streamed, and it must not conjure a turn card just to add
        // a duration. Time only moves forward, so a counter that went backwards contributes nothing.
        var previousStream = agentStream.TryGetValue(completed.AgentId, out var streamed) ? streamed : TimeSpan.Zero;
        agentStream[completed.AgentId] = completed.StreamElapsed;
        if (completed.StreamElapsed > previousStream)
        {
            (root ? turnCard : Find(completed.AgentId)?.Transcript.Current)?.NoteStream(completed.StreamElapsed - previousStream);
        }

        // Nothing reported is not the same as zero spent: the strip and the card keep "—", and the metrics row keeps null, so
        // a turn whose only non-zero column is the hour-lived cache write still counts as reported.
        if (delta.InputTokens == 0 && delta.OutputTokens == 0 && delta.CacheReadTokens == 0 && delta.CacheWriteTokens == 0
            && delta.CacheWrite1hTokens == 0)
        {
            return;
        }

        // The strip reports the root agent's spend (the context window and the price belong to that model); a sub-agent's
        // usage stays on its own tab, and its turn card still gets its own delta here. The root's card is the turn's own
        // card (see turnCard): a Background flush arriving after the next turn began must not misdeliver the tokens.
        Instruments.AddTurn(completed.AgentId, delta);
        if (root)
        {
            // The session's tool set only exists once the first turn built the agent host, so the denominator is refreshed
            // after the turn that created it instead of before.
            Instruments.RefreshCounters();
            turnCard?.AddUsage(delta, completed.Stop);
        }
        else
        {
            Card(completed.AgentId)?.AddUsage(delta, completed.Stop);
            Find(completed.AgentId)?.Instruments.AddTurn(completed.AgentId, delta);
        }
        NoteFullCacheRewrite(completed.AgentId, delta);
    }

    /// <summary>
    /// Closes a live sub-agent's turn where the root's turn closes: the card keeps its own divider, its elapsed is the turn's
    /// own wall clock (from the completion event, slot-acquire to finish), and one metrics row is recorded under the child's
    /// session id - the row a reopened tab replays its elapsed, rate, status and model from, the same table the root's turns
    /// use. A turn that showed nothing on screen leaves no card to close; its run-end Finish still ends the transcript.
    /// </summary>
    private void CloseSubAgentTurn(AgentTurnCompleted completed, TokenUsage turnStartUsage, TimeSpan turnStartStream)
    {
        if (string.Equals(completed.AgentId, "root", StringComparison.Ordinal) || Find(completed.AgentId) is not { } agent)
        {
            return;
        }

        var card = agent.Transcript.Current;
        agent.Transcript.EndIsolatedTurn(completed.EndedAt - completed.StartedAt, null);
        if (card is null)
        {
            return;
        }

        subAgentLastCards[agent.AgentId] = card;
        agent.Instruments.NoteTurnEnded(card.Round, completed.EndedAt - completed.StartedAt);
        // Consumed here whether or not a row is written: a recorder that is not listening must not let one turn's calls
        // pile onto the next turn's first row.
        var tools = subAgentToolCalls.TryRemove(agent.AgentId, out var calls) ? calls : 0;
        if (metricsRecorder is { Enabled: true } recorder)
        {
            try
            {
                recorder.Record(TurnMetricsRowFactory.BuildForOutcome(
                    agent.ChildSessionId,
                    card.Round,
                    completed.StartedAt,
                    completed.EndedAt,
                    completed.Stop == StopReason.MaxTokens ? TurnMetricStatus.Limit : TurnMetricStatus.Completed,
                    error: null,
                    completed.Usage,
                    turnStartUsage,
                    completed.StreamElapsed,
                    turnStartStream,
                    tools,
                    agent.ProviderId,
                    agent.Model.Length == 0 ? null : agent.Model));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The writer promises never to throw; this belt stays for the same reason the root's does.
                AppLogs.Usage.ZLogWarning($"sub-agent turn metrics row not queued: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Hands the turn's metrics to the app's background writer: one struct channel write, synchronous, and the only cost the
    /// turn end pays for its own metrics. The provider and the model are the composer's current choice by reference (the same
    /// ones the turn card and the strip price with); the error text is the one the footnote already shows; the two stream
    /// snapshots are field reads, so the stream duration costs the turn end one subtraction.
    /// </summary>
    private void RecordTurnMetrics(int turn, LocText? error)
    {
        if (metricsRecorder is not { Enabled: true } recorder)
        {
            return;
        }

        try
        {
            // The agent is rebuilt during a turn (agent or model switch): the new instance starts from zero, so the turn's
            // start snapshot of the old one means nothing against it.
            var end = runtime?.ReadRootCounters() ?? RootCounters.None;
            var start = ReferenceEquals(end.Instance, turnStartCounters.Instance) ? turnStartCounters : RootCounters.None;
            recorder.Record(TurnMetricsRowFactory.Build(
                SessionId,
                turn,
                turnStartedAt,
                DateTimeOffset.UtcNow,
                error,
                turnRootFailure,
                end.Usage,
                start.Usage,
                end.Stream,
                start.Stream,
                turnToolCalls,
                Composer.ProviderId is { Length: > 0 } provider ? provider : null,
                Composer.Model is { Length: > 0 } model ? model : null));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The writer promises never to throw, but a row is never worth a turn: this is the belt to that suspender.
            AppLogs.Usage.ZLogWarning($"turn metrics row not queued: {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// The expensive shape, in one place: the root agent's turn wrote cache and read none of it back. A sub-agent's own spend and the
    /// session's first turn (which has nothing to read yet) do not count.
    /// </summary>
    internal static bool CountsAsFullRewrite(string agentId, TokenUsage delta) =>
        string.Equals(agentId, "root", StringComparison.Ordinal)
        && delta.CacheWriteTokens > 0
        && delta.CacheReadTokens == 0;

    /// <summary>
    /// Watches for the expensive shape: a turn that wrote the whole prefix and read nothing from the cache. When that happens three
    /// times inside an hour the user is told once, with the number of tokens involved — a full rewrite costs 1.25× (five minutes) or
    /// 2× (an hour) the input price, and after the auto lifetime was added it usually means the prefix changed, not the lifetime.
    /// </summary>
    private void NoteFullCacheRewrite(string agentId, TokenUsage delta)
    {
        if (!CountsAsFullRewrite(agentId, delta) || cacheRewriteNotified || turnNumber <= 1)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        cacheRewrites.RemoveAll(at => now - at.At > TimeSpan.FromHours(1));
        cacheRewrites.Add((now, delta.CacheWriteTokens));
        if (cacheRewrites.Count < CacheRewriteNoticeThreshold)
        {
            return;
        }

        cacheRewriteNotified = true;
        var written = cacheRewrites.Sum(entry => entry.Tokens);
        notifications?.Notify(
            NotificationLevel.Info,
            SessionStrings.CacheRewriteTitle,
            SessionStrings.CacheRewriteBody(written),
            "cache-rewrite:" + SessionId);
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

    /// <summary>
    /// The part of a run's usage its own turns never reported: the run's difference, minus the counter's move since the
    /// baseline the run began at, clamped at zero per column. <paramref name="reported"/> is the instance counter at the
    /// last event the App saw; a counter that moved backwards contributes nothing.
    /// </summary>
    internal static TokenUsage UnreportedRunUsage(TokenUsage runUsage, TokenUsage reported, TokenUsage runStart)
    {
        var thisRun = new TokenUsage(
            Math.Max(0, reported.InputTokens - runStart.InputTokens),
            Math.Max(0, reported.OutputTokens - runStart.OutputTokens),
            Math.Max(0, reported.CacheReadTokens - runStart.CacheReadTokens),
            Math.Max(0, reported.CacheWriteTokens - runStart.CacheWriteTokens),
            Math.Max(0, reported.CacheWrite1hTokens - runStart.CacheWrite1hTokens));
        // The cache columns ride along (the terminal event carries them; its hour-lived share it does not), so a failed
        // run's partial cache read still reaches the cumulative reading's hit rate.
        return new TokenUsage(
            Math.Max(0, runUsage.InputTokens - thisRun.InputTokens),
            Math.Max(0, runUsage.OutputTokens - thisRun.OutputTokens),
            Math.Max(0, runUsage.CacheReadTokens - thisRun.CacheReadTokens),
            Math.Max(0, runUsage.CacheWriteTokens - thisRun.CacheWriteTokens));
    }

    /// <summary>The instruments that own an agent's events: the session's own strip for the root, the child's tab otherwise.</summary>
    private SessionInstrumentsViewModel? InstrumentsOf(string agentId) =>
        string.Equals(agentId, "root", StringComparison.Ordinal)
            ? Instruments
            : Find(agentId)?.Instruments;

    private SubAgentViewModel? Find(string agentId) =>
        subAgentById.TryGetValue(agentId, out var agent) ? agent : null;

    private void StartSubAgent(AgentSubAgentStarted started)
    {
        // The baseline first: every run differences its own tokens against the counter as it stands here, so a resumed
        // child never re-counts what its earlier runs already reported.
        subAgentRunStart[started.ChildId] = agentUsage.TryGetValue(started.ChildId, out var known) ? known : TokenUsage.Empty;

        if (subAgentById.ContainsKey(started.ChildId))
        {
            return;
        }

        // A resumed child (the parent handing the same agent its next task) comes back as the tab it already was: its
        // transcript, round counter, cumulative usage reading and turn-metric rows all continue - a fresh tab would
        // restart the rounds and collide (child session, turn 1) with the rows its earlier runs already wrote.
        if (TakeFinishedSubAgent(started.ChildId, started.ChildSessionId) is { } resumed)
        {
            resumed.IsReadOnly = false;
            resumed.MarkResumed();
            subAgentById[started.ChildId] = resumed;
            subAgents.Add(resumed);
            resumed.Transcript.AddUserMessage(started.Task, started.At);
            DrainEarlyDeltas(resumed);
            OnPropertyChanged(nameof(HasSubAgents));
            OnPropertyChanged(nameof(HasSubAgentActivity));
            RaiseSubAgentCounters();
            SubAgentsChanged?.Invoke();
            OnAgentStateChanged();
            OpenSubAgent(resumed);
            // The menu holds a snapshot of the list, not the list itself, so pulling a tab back out of the history has
            // to be told - or the count still lists a tab that is live again.
            SyncFinishedMenu();
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
            started.AgentId,
            Title,
            openSession,
            instrumentSources,
            started.ProviderId)
        {
            IsAutomation = automationChildIds.TryRemove(started.ChildId, out _),
        };
        // The task is a user message - Core persists it as one, so a replayed tab draws it - and the live tab draws it
        // here too: one transcript, the same rows live as replayed.
        agent.Transcript.AddUserMessage(started.Task, started.At);
        subAgentById[started.ChildId] = agent;
        subAgents.Add(agent);
        DrainEarlyDeltas(agent);
        OnPropertyChanged(nameof(HasSubAgents));
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

    /// <summary>
    /// Pulls a finished child's tab back out of the finished menu, keeping its transcript and every counter: a resumed run
    /// continues them. A row replayed from the store is keyed by the child session it ran in, a live one by the run id the
    /// host gave it - a child spawned before this tab was loaded is in the menu as the former and resumes as the latter, so
    /// both ids identify it. The finished list is in run-end order and small; a linear scan is the whole cost.
    /// </summary>
    private SubAgentViewModel? TakeFinishedSubAgent(string childId, string childSessionId)
    {
        for (var index = 0; index < finished.Count; index++)
        {
            var stored = finished[index];
            if (string.Equals(stored.AgentId, childId, StringComparison.Ordinal)
                || (childSessionId.Length > 0 && string.Equals(stored.ChildSessionId, childSessionId, StringComparison.Ordinal)))
            {
                finished.RemoveAt(index);
                return stored;
            }
        }

        return null;
    }

    private void FinishSubAgent(AgentSubAgentFinished finishedEvent)
    {
        if (Find(finishedEvent.AgentId) is not { } agent)
        {
            return;
        }

        // The terminal event carries THIS RUN's usage (a difference Core took before the run began), so the unreported
        // part is what this run's own turns did not already count: the counter's move since the run started. A completed
        // run leaves nothing; one that failed mid-turn still owes its partial spend to the open card and the reading.
        var runStart = subAgentRunStart.TryGetValue(finishedEvent.AgentId, out var baseline) ? baseline : TokenUsage.Empty;
        var reported = agentUsage.TryGetValue(finishedEvent.AgentId, out var counter) ? counter : TokenUsage.Empty;
        var delta = UnreportedRunUsage(finishedEvent.Usage, reported, runStart);
        if (delta.InputTokens > 0 || delta.OutputTokens > 0)
        {
            (agent.Transcript.Current ?? (subAgentLastCards.TryGetValue(finishedEvent.AgentId, out var lastCard) ? lastCard : null))
                ?.AddUsage(delta, StopReason.EndTurn);
            agent.Instruments.AddTurn(finishedEvent.AgentId, delta);
        }
        subAgentRunStart.TryRemove(finishedEvent.AgentId, out _);

        // A failed or stopped run had no completion event, so its turn never reached the count through the per-turn close;
        // the run's own elapsed is that turn's clock (queueing included, which is how the run measured it).
        if (agent.Transcript.Current is { } openCard)
        {
            agent.Instruments.NoteTurnEnded(openCard.Round, finishedEvent.Elapsed);
        }

        agent.Finish(finishedEvent.Status, finishedEvent.Elapsed, finishedEvent.Error);
        subAgentToolCalls.TryRemove(finishedEvent.AgentId, out _);
        subAgentLastCards.TryRemove(finishedEvent.AgentId, out _);

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
        FinishToolsOf(finishedEvent.AgentId);
        // The usage and stream counters stay: a resumed run differences its turns against them, so its first turn reports
        // only the new task's tokens and not the whole instance cumulative again.
        subAgents.Remove(agent);
        finished.Add(agent);
        SyncFinishedMenu();
        OnPropertyChanged(nameof(HasSubAgents));
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
        if (runtime is not null)
        {
            runtime.PromptsStaleChanged -= OnRuntimePromptsStaleChanged;
            runtime.SessionGrantsChanged -= OnSessionGrantsChanged;
        }

        ClearLiveTools();
        ReleaseInspectorListeners();
        Instruments.Dispose();
        foreach (var agent in subAgents)
        {
            agent.DisposeInstruments();
        }

        foreach (var agent in finished)
        {
            agent.DisposeInstruments();
        }

        Composer.Dispose();
        foreach (var message in queued.ToArray())
        {
            NotifyWithdrawn(message);
        }
        coalescer.Dispose();
        Cancel();
        turn?.Dispose();
        turn = null;
        AppLogs.Session.ZLogInformation($"closed tab {SessionId}");
    }

    /// <summary>
    /// The language switched. Every text of this session is derived in a getter, so nothing is cached and nothing has to be
    /// rebuilt: the session's own labels are re-raised here and each card, tool row, notice and sub-agent tab re-raises
    /// its own - the cards themselves never subscribe to <see cref="Loc.LanguageChanged"/>, or every one of them would
    /// hold a reference to the static event after the tab closed.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A switch raised on another thread belongs to a window that is gone: the transcript, the cards and
        // the sub-agent tabs are all bound on the thread that built the session.
        if (disposed || Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        OnPropertyChanged(nameof(ReadOnlyBanner));
        OnPropertyChanged(nameof(ContinueLabel));
        OnPropertyChanged(nameof(SubAgentLabel));
        foreach (var mark in SessionAuthorizations)
        {
            mark.RefreshText();
        }
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
internal sealed class SubAgentViewModel : NotifyingObject
{
    private readonly Action<string>? stopRequested;
    private readonly string kind;
    private readonly string parentTitle;
    private readonly Action? backToParent;
    private SubAgentStatus status = SubAgentStatus.Queued;
    private DateTimeOffset startedAt;
    private TimeSpan elapsed;
    private LocText error;
    private LocText statusNote = SessionStrings.SubAgentWaitingSlot;
    private bool isReadOnly;
    private bool isStopRequested;
    private bool outcomeUnknown;

    /// <param name="stopRequested">Asks the host to stop this run; null when the row has nothing to stop (a replay).</param>
    /// <param name="parentAgentId">The agent that spawned this one; null for a direct child of the main session.</param>
    /// <param name="parentTitle">The title of the session (or agent) this tab hangs under; the breadcrumb's first segment.</param>
    /// <param name="backToParent">Switch the shell back to the parent's tab; null hides the breadcrumb's way back.</param>
    /// <param name="sources">The shared instrument sources; null (a standalone view) reads a null object.</param>
    /// <param name="providerId">The connected provider serving this child's model, as the start event carried it.</param>
    /// <param name="childInfo">The child's own stored session row, when the caller has one; a synthetic stand-in otherwise.</param>
    public SubAgentViewModel(
        string sessionId,
        string agentId,
        string childSessionId,
        string kind,
        string task,
        string model,
        Action<string>? stopRequested,
        string? parentAgentId = null,
        string? parentTitle = null,
        Action? backToParent = null,
        ISessionInstrumentSources? sources = null,
        string? providerId = null,
        SessionInfo? childInfo = null)
    {
        SessionId = sessionId;
        AgentId = agentId;
        ChildSessionId = childSessionId;
        ParentAgentId = parentAgentId;
        this.kind = kind;
        this.parentTitle = parentTitle ?? string.Empty;
        this.backToParent = backToParent;
        Task = task;
        this.model = model;
        this.stopRequested = stopRequested;
        Transcript = new TranscriptViewModel(collapseOlderTurns: true);
        // Every turn card this transcript begins names the model that ran it: the run's model from
        // AgentSubAgentStarted, the same stamp the root's turns carry, so the turn's divider line reads the same.
        Transcript.StampTurnModel = card => card.NoteModel(providerId, model.Length == 0 ? null : model);
        StopCommand = new RelayCommand(RequestStop, () => CanStop);
        // A sub-agent is its own session with its own spend, context window and account meter, so it reads them off the
        // same instruments the root's strip uses - one class, one rendering, one set of strings - scoped to this child's
        // agent id and fed by the events routed to it. Built on first use: the history rows a session restores would
        // otherwise pay a turn-metrics read each, for readouts only an opened tab ever reads.
        this.providerId = providerId;
        instrumentsSession = childInfo ?? new SessionInfo(ChildSessionId, string.Empty, kind, DateTimeOffset.UnixEpoch);
        instrumentsSources = sources ?? EmptySessionInstrumentSources.Instance;
        BackToParentCommand = new RelayCommand(
            () => this.backToParent?.Invoke(),
            () => HasBackToParent);
    }

    /// <summary>The main session this sub-agent belongs to; its tab never shows under another session.</summary>
    public string SessionId { get; }

    public string AgentId { get; }

    /// <summary>The session this sub-agent's own messages are stored under; the run id and the session id differ.</summary>
    public string ChildSessionId { get; }

    /// <summary>The agent that spawned this one; null for a child that was spawned by the root agent.</summary>
    public string? ParentAgentId { get; }

    /// <summary>
    /// True for an agent at the first level: one the session's own agent spawned. Its parent link is the main agent's
    /// <c>"root"</c> (or absent, for a child replayed from a store row without a parent), which is what tells the first
    /// level apart from an agent hanging under another one - the two levels wear different icons.
    /// </summary>
    public bool IsFirstLevel => ParentAgentId is not { Length: > 0 } || string.Equals(ParentAgentId, "root", StringComparison.Ordinal);

    /// <summary>
    /// True for a child an automation started outside any turn (the CI chain's Scout, a prompt automation); false for one the
    /// session's own agent spawned. The resource window lists only the former, because the latter already have a tab and the
    /// sub-agent strip.
    /// </summary>
    public bool IsAutomation { get; init; }

    /// <summary>An automation child that is queued or running: the rows the resource window lists and that hold back a prompt reload.</summary>
    internal bool IsLiveAutomation => IsAutomation && IsLive;

    /// <summary>The kind the store recorded; a stored child without one is named after the region, in the current language.</summary>
    public string Kind => kind.Length == 0 ? SessionStrings.SubAgentsTitle : kind;

    public string Task { get; }

    /// <summary>The model this child runs. A live child's comes from the start event; a stored row's is seeded from its
    /// own last recorded turn, so its instruments price and window against the model that actually ran.</summary>
    public string Model => model;

    private string model;

    private string? providerId;

    public string TaskSummary => ToolRowViewModel.Compact(Task, 120);

    public TranscriptViewModel Transcript { get; }

    public RelayCommand StopCommand { get; }

    /// <summary>
    /// True while stopping this run would do something: it holds a run slot and no stop was requested yet. A queued child has
    /// no run to stop (<c>SubAgentControl.Stop</c> answers "not found" for it), so the buttons hide instead of doing nothing.
    /// </summary>
    public bool CanStop => status == SubAgentStatus.Running && !isStopRequested;

    /// <summary>
    /// The name the tab and the breadcrumb share. A live child's id is Core's kind-prefixed counter ("Worker-3"): it
    /// already says what it is and stays the word the model resumes it by, so the id alone - the task text moved to the
    /// tab's and the breadcrumb's hover. A kind Core had to sanitize lost its name in the id, so the kind rides along in
    /// the kind·id form. A stored row's live id died with the process that issued it and can never be resumed again:
    /// showing it would be a number that means nothing, so the kind alone names it.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (FromStoredRow || AgentId.Length == 0)
            {
                return Kind;
            }

            return AgentId.StartsWith(Kind, StringComparison.OrdinalIgnoreCase)
                ? AgentId
                : Kind + " · " + AgentId;
        }
    }

    /// <summary>True when this row came from the store rather than a live start event.</summary>
    internal bool FromStoredRow { get; init; }

    // The marker is the tab's own at-a-glance state: ○ a read-only history tab, ▸ one that is (or was last) live. The
    // breadcrumb's badge and the status word say the same thing, but the tab strip shows only this line - the marker is
    // how a tab tells its state without being open.
    public string TabTitle => (IsReadOnly ? "○ " : "▸ ") + DisplayName;

    // ---------------------------------------------------------------- breadcrumb

    /// <summary>The session this sub-agent runs under, as its tab title reads it: the breadcrumb's first segment.</summary>
    public string ParentSessionTitle => parentTitle;

    /// <summary>The sub-agent's own name in the breadcrumb: the same word its tab is titled by.</summary>
    public string BreadcrumbName => DisplayName;

    /// <summary>True when there is a parent tab to switch back to and the shell wired the way there.</summary>
    public bool HasBackToParent => backToParent is not null && parentTitle.Length > 0;

    /// <summary>"Back to &lt;parent title&gt;": the parent segment's tooltip and accessible name.</summary>
    public string BackToParentText => SessionStrings.BreadcrumbBackToParent(ParentSessionTitle);

    /// <summary>Selects the parent's tab again; the sub-agent tab stays where it is.</summary>
    public RelayCommand BackToParentCommand { get; }

    public bool IsReadOnly
    {
        get => isReadOnly;
        set
        {
            if (SetAndNotify(ref isReadOnly, value, nameof(TabTitle)))
            {
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
            if (SetAndNotify(ref status, value, nameof(StatusText)))
            {
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(IsLive));
                OnPropertyChanged(nameof(EndedCompleted));
                OnPropertyChanged(nameof(EndedFailed));
                OnPropertyChanged(nameof(EndedStopped));
                OnPropertyChanged(nameof(CanStop));
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

    /// <summary>
    /// How the run ended, one flag per colour the finished menu's dot wears. The dot carries the state and the words move to
    /// the row's tooltip, so a list of finished agents reads at a glance instead of spelling the same status out on every row.
    /// </summary>
    public bool EndedCompleted => !outcomeUnknown && status == SubAgentStatus.Completed;

    public bool EndedFailed => !outcomeUnknown && status == SubAgentStatus.Failed;

    public bool EndedStopped => !outcomeUnknown && status == SubAgentStatus.Stopped;

    /// <summary>
    /// When the run acquired a run slot (Core's <c>AgentSubAgentRunning.At</c>); <see cref="DateTimeOffset.MinValue"/> while it
    /// still waits for one. The elapsed reading counts from here, and the resource inspector orders its rows by it.
    /// </summary>
    public DateTimeOffset StartedAt => startedAt;

    public bool IsLive => !IsFinished;

    /// <summary>The one-line note beside the status, in the current language; empty when there is nothing to say.</summary>
    public string StatusNote => statusNote.Resolve();

    public bool HasStatusNote => !statusNote.IsEmpty;

    public string ElapsedText =>
        IsFinished || status == SubAgentStatus.Running ? TextFormat.Elapsed(elapsed) : TextFormat.None;

    /// <summary>The connected provider serving this child's model, as the start event carried it (null: the default).</summary>
    public string? ProviderId => providerId;

    /// <summary>The child's own readings - the same instruments class the root's strip uses, scoped to this agent.</summary>
    public SessionInstrumentsViewModel Instruments => instruments ??= new SessionInstrumentsViewModel(
        instrumentsSession,
        instrumentsSources,
        () => model,
        () => providerId,
        agentId: AgentId);

    /// <summary>Whether the child's instruments were ever built: the lazy gate the session's close respects.</summary>
    internal bool InstrumentsBuilt => instruments is not null;

    /// <summary>Releases the child's instruments, and only if they were ever built.</summary>
    internal void DisposeInstruments() => instruments?.Dispose();

    private SessionInstrumentsViewModel? instruments;
    private readonly SessionInfo instrumentsSession;
    private readonly ISessionInstrumentSources instrumentsSources;

    /// <summary>
    /// A stored row learns who it ran from its own last recorded turn: the replay already seeded that turn's divider with
    /// the metrics row's provider and model, and the instruments read the same pair so the window and the cost of a history
    /// tab are the model that actually ran, not an empty guess.
    /// </summary>
    public void SeedStoredModel(string? provider, string storedModel)
    {
        providerId = provider;
        model = storedModel;
    }

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

    public void Finish(SubAgentStatus finalStatus, TimeSpan finalElapsed, string? finalError)
    {
        Status = finalStatus;
        elapsed = finalElapsed;
        error = finalError is null ? default : LocText.Literal(finalError);
        SetStatusNote(error);
        // Isolated: the run is over, nothing more may append to its turn's card - a resumed run's next content must
        // begin a fresh one instead of piling into the closed (and possibly failed) card that "current" still names.
        Transcript.EndIsolatedTurn(finalElapsed, error);
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(ElapsedText));
        StopCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The parent handed this child its next task: the per-run state starts clean. The stop gate reopens (a stop that was
    /// requested closed it for that run's life), the last run's error stops being the footnote, the unknown-outcome mark
    /// lifts - the new run will say how it went. The task and the model stay: a resumed run continues the same work.
    /// </summary>
    internal void MarkResumed()
    {
        isStopRequested = false;
        error = default;
        outcomeUnknown = false;
        SetStatusNote(default);
        OnPropertyChanged(nameof(Error));
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
        // The child's own instruments follow the language switch by themselves (they subscribe like the root's do).
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(ReadOnlyBadgeText));
        OnPropertyChanged(nameof(BreadcrumbName));
        OnPropertyChanged(nameof(BackToParentText));
        Transcript.RefreshText();
    }

    /// <summary>
    /// A sub-agent replayed from the store. The parent link is the agent row it hangs under ("root" for a direct child
    /// of the main session, otherwise the id of the ancestor's session). A status the store cannot prove is left unknown,
    /// which reads as a neutral "ended" instead of an invented "completed".
    /// </summary>
    public static SubAgentViewModel FromStored(
        string sessionId,
        SessionInfo session,
        string parentAgentId,
        SubAgentStatus? status,
        string? parentTitle = null,
        Action? backToParent = null,
        ISessionInstrumentSources? sources = null)
    {
        var agent = new SubAgentViewModel(
            sessionId,
            session.Id,
            session.Id,
            session.AgentKind ?? string.Empty,
            session.Task ?? session.Title,
            string.Empty,
            stopRequested: null,
            parentAgentId,
            parentTitle,
            backToParent,
            sources,
            childInfo: session)
        {
            IsReadOnly = true,
            FromStoredRow = true,
        };
        if (status is { } known)
        {
            agent.Finish(known, TimeSpan.Zero, null);
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
        Transcript.EndIsolatedTurn(null, null);
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(EndedCompleted));
        OnPropertyChanged(nameof(EndedFailed));
        OnPropertyChanged(nameof(EndedStopped));
        OnPropertyChanged(nameof(ElapsedText));
        StopCommand.NotifyCanExecuteChanged();
    }

    public override string ToString() => TabTitle;

    private void RequestStop()
    {
        isStopRequested = true;
        SetStatusNote(SessionStrings.SubAgentStopRequested);
        OnPropertyChanged(nameof(CanStop));
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

    /// <summary>Opens one item's read-only tab. The rows are a menu, so nothing stays selected: picking the same entry
    /// again works without a reset step.</summary>
    public void Open(SubAgentViewModel item) => open?.Invoke(item);

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
    }
}
