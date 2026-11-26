using System.Diagnostics;
using System.Globalization;
using Forge.Abstractions;
using Forge.App.Approval;
using Forge.App.Git;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Forge.Core.Prompts.Templates;
using Forge.Core.Providers;
using Forge.Core.Storage;
using Forge.Core.Tools;
using Forge.HostClient;
using Forge.Mcp;
using Forge.Workspace;
using Forge.App.Logging;
using Forge.App.Providers;
using ZLogger;

namespace Forge.App.Agents;

/// <summary>Everything a session runtime needs from the composition root, shared by every session.</summary>
internal sealed record AgentRuntimeServices(
    Func<ModelSettings, IChatProvider> ProviderFactory,
    ExtensionHostClient? HostClient,
    ISessionStore Store,
    ISessionCatalog Catalog,
    ITodoStore Todos,
    ModelSettings Model,
    GitTools? Git = null)
{
    /// <summary>
    /// Every tool this installation registers, with where each belongs on the <c>tools</c> page. The page and the resource
    /// window's tool sizes both read it, so one enumeration decides what a tool is; a session-specific list is not needed and
    /// could disagree.
    /// </summary>
    public IReadOnlyList<ToolDescriptor> Inventory() => ToolCatalog.Describe(this);

    /// <summary>
    /// The shell's approval wiring (queue, remembered rules and the extension authoring adapter). Null when the runtime was
    /// assembled without the composition root, : the session then has no approval handler and an ask is a
    /// denial, exactly as before the queue existed.
    /// </summary>
    public ApprovalServices? Approvals { get; set; }

    /// <summary>
    /// The prompt template store. A session resolves a snapshot from it (for its worktree) when its agent host is built:
    /// the system slot, the agent kinds and the compaction prompt then stay fixed for that host. Null without the composition root: the
    /// compiled-in texts are used.
    /// </summary>
    public Prompts.PromptTemplateService? PromptTemplates { get; set; }

    /// <summary>Agent defaults resolved from settings.json; null when the runtime has no store.</summary>
    public Func<string, ModelSettings?>? AgentDefaults { get; set; }

    /// <summary>
    /// The live provider catalogue (<c>ProviderServices.Catalog</c>): the models tool lists it and a per-spawn model
    /// override is matched against it. Read on every use rather than captured, so a provider saved while a session runs
    /// is visible to the next tool call. Null without the composition root, which then has neither the models tool nor
    /// an override parameter on <c>spawn_agent</c>.
    /// </summary>
    public Func<ProviderCatalog>? Providers { get; set; }

    /// <summary>
    /// Whether the user hid a model from the pickers (<c>preferences.modelsHidden</c>); the models tool skips those, the
    /// same rule the agent-defaults row applies. Null = nothing is hidden.
    /// </summary>
    public Func<string, bool>? IsModelHidden { get; set; }

    /// <summary>
    /// Resolves <c>spawn_agent</c>'s optional per-spawn model override against the catalogue. Null = the tool carries no
    /// <c>model</c>/<c>provider</c>/<c>thinking</c> parameters at all.
    /// </summary>
    public Func<SpawnModelRequest, SpawnModelChoice>? SpawnModels { get; set; }

    /// <summary>
    /// Whether subscription requests claim the official client's identity (<c>claudeWire.enabled</c>). Decides which built-in
    /// system text a session uses when no template supplies one: the neutral one while the claim is on, the named one when the
    /// user turned it off. Defaults to on, which is the product default.
    /// </summary>
    public Func<bool>? ClaimsOfficialIdentity { get; set; }

    /// <summary>
    /// Commits a TODO step when the agent marks it done (handed to <c>AgentHostOptions.StepCommitter</c>). Null when the
    /// runtime was assembled without the composition root, : marking an item done then commits nothing.
    /// </summary>
    public IStepCommitter? StepCommitter { get; set; }

    /// <summary>The git side of <c>request_review</c> (handed to <c>AgentHostOptions.Reviews</c>, which registers the tool). Null without the composition root: the session then has no review tool.</summary>
    public IReviewChangeSetSource? Reviews { get; set; }

    /// <summary>
    /// The git side of the step-completion gate (handed to <c>AgentHostOptions.StepChanges</c>): whether the session's
    /// worktree holds changes a step has to answer for. Null without the composition root, or in a runtime assembled without git: the
    /// gate then does not exist and marking a step done behaves as it did before the gate.
    /// </summary>
    public IStepChangeProbe? StepChanges { get; set; }

    /// <summary>
    /// Whether completing a checklist step that changed code needs an approving review (<c>preferences.stepReviewRequired</c>),
    /// read on every attempt by the step gate. Null = on.
    /// </summary>
    public Func<bool>? StepReviewRequired { get; set; }

    /// <summary>
    /// Whether the agent is offered <c>request_review</c> (<c>preferences.reviewToolEnabled</c>). Read when a host is built,
    /// like the other tool-set decisions: the tool definition is bytes of the cached prefix. Without the tool there is no
    /// step gate either (nothing could earn the verdict). Null = on.
    /// </summary>
    public Func<bool>? ReviewToolEnabled { get; set; }

    /// <summary>The auto-fix loop settings (<c>preferences.reviewAutoFix</c>, <c>reviewAutoFixMaxRounds</c>), read on every review result. Null = defaults.</summary>
    public Func<ReviewFixPolicy>? ReviewFix { get; set; }

    /// <summary>
    /// Whether the default orchestration is injected into the root agent (<c>preferences.orchestrationEnabled</c>). Read
    /// when a host is built, like the prompt templates: a change applies to the next host, never to a cached prefix
    /// mid-turn. Null = on.
    /// </summary>
    public Func<bool>? OrchestrationEnabled { get; set; }

    /// <summary>
    /// The directory the product's own skills were materialized into (<c>&lt;UserData&gt;/builtin-skills/&lt;version&gt;</c>),
    /// published once by the composition root at startup; null when they could not be written. Process-wide because an
    /// installation has one data root. The lowest-priority skill source: a same-named workspace or user skill wins.
    /// </summary>
    public static string? ProductSkillsDirectory { get; set; }

    /// <summary>How much of a change set the review package carries, read per review from the settings (handed to <c>AgentHostOptions.ReviewLimits</c>). Null: the package defaults.</summary>
    public Func<ReviewPackageLimits>? ReviewLimits { get; set; }

    /// <summary>Where <c>request_review</c> records each finished review (handed to <c>AgentHostOptions.ReviewConclusions</c>). Null: nothing is recorded.</summary>
    public IReviewConclusionStore? ReviewConclusions { get; set; }

    /// <summary>
    /// The window's side of session-to-session delivery (forge-core "session messaging"): running a root turn, the checkpoint numbering
    /// of a delivered user-session turn, a turn's change line and the receipt's way into a transcript. Handed to
    /// <see cref="AgentHostOptions.SessionMessages"/> as a lookup because the window is created after this record is built;
    /// null leaves the host without the app behind it (scripts, tests).
    /// </summary>
    public Func<ISessionMessageEndpoint?>? SessionMessages { get; set; }

    /// <summary>Where the step review ledger is persisted (handed to <c>AgentHostOptions.StepReviewStore</c>). Null: in memory only.</summary>
    public IStepReviewStore? StepReviewStore { get; set; }

    /// <summary>
    /// Where oversized tool output is stored (handed to <c>AgentHostOptions.Artifacts</c>, which also adds the tool that
    /// reads it back). Null when the runtime was assembled without the composition root: output then stays inline.
    /// </summary>
    public IArtifactStore? Artifacts { get; set; }

    /// <summary>
    /// How much of one tool result stays inline before the rest is stored as an artifact, and how much preview the model
    /// still sees. Read when a host is built (both live in <c>AgentOptions</c>), so a change applies to the next host rather
    /// than to the next request. Null keeps Core's defaults.
    /// </summary>
    public Func<(int Threshold, int Preview)>? ArtifactLimits { get; set; }

    /// <summary>
    /// The pull request tools (<c>pr_*</c>, <c>pr_screenshots</c>, <c>review_comments</c>, <c>review_comment_resolve</c>),
    /// registered after the git tools. Stateless across sessions like <see cref="Git"/>; empty without the composition root. The
    /// read-only ones pass through the policy's read-only rule, and the write ones name no path either, so the workspace
    /// boundary counts them as inside and they need no card. <c>pr_screenshots</c> – which runs a command from the repository and
    /// then pushes and edits the description – declares <see cref="ToolSideEffect.Execute"/>.
    /// </summary>
    public IReadOnlyList<ITool> PullRequestTools { get; set; } = [];

    /// <summary>
    /// The project registry, used once per host build to tell a general-area session (no git or PR tools) from a repository
    /// one. Null without the composition root: every session is then treated as a repository session.
    /// </summary>
    public IProjectRegistry? Projects { get; set; }

    /// <summary>
    /// The workspace file and shell tools (<c>read</c>, <c>write</c>, <c>edit</c>, <c>ls</c>, <c>find</c>, <c>grep</c>,
    /// <c>bash</c>) from <c>Forge.IO</c>. Stateless across sessions: every call takes the session's worktree from its
    /// <see cref="ToolContext"/>. Empty without the composition root, where an agent therefore cannot touch files at all. The read-only
    /// ones pass through the policy's read-only rule; <c>write</c>/<c>edit</c>/<c>copy</c>/<c>move</c>/<c>delete</c> and <c>bash</c>
    /// (as <see cref="ToolSideEffect.Execute"/>) answer their own scope, so a call that names a path outside the workspace asks
    /// once and one that stays inside needs no card. The read-only four are also the file half of a
    /// <c>run_code</c> script's tool surface (wave W14: read-only is the rule, so nothing a script reaches ever needed a
    /// card the model's own call would have raised); <c>write</c>, <c>edit</c> and <c>bash</c> stay out of it entirely.
    /// </summary>
    public IReadOnlyList<ITool> FileTools { get; set; } = [];

    /// <summary>
    /// The spec-graph tools (<c>spec_get</c>, <c>spec_graph</c>, <c>spec_validate</c>), all read-only. Stateless across sessions
    /// like <see cref="FileTools"/>: every call resolves the graph of its <see cref="ToolContext"/> directory, and the resolver
    /// caches one index per directory for the whole app. Handed to a host only while the session's workspace has specs (or the
    /// orchestration is on); the <c>tools</c> page lists it regardless, because its switch is global.
    /// </summary>
    public SpecTools SpecTools { get; set; } = new();

    // --- web tools (web_fetch / web_search) ---
    /// <summary>
    /// Produces the web tools when a session assembles its tool surface: <c>web_fetch</c> always, <c>web_search</c> only
    /// while a backend is configured with its key or URL. Called again at the start of every turn: when the names it
    /// returns differ from what the session's host holds, the external set is republished (otherwise nothing changes,
    /// so the prompt cache stays). Should be cheap and return shared instances. Null without the composition root: no web tools. Both
    /// are read-only and need no card - the workspace is the boundary, not the network.
    /// </summary>
    public Func<IReadOnlyList<ITool>>? WebTools { get; set; }
    // --- end web tools ---

    /// <summary>
    /// The MCP servers of the app; null when the runtime was assembled without the composition root, which then
    /// has no MCP tools at all. The catalog, the per-turn limit and the search tool all come from here.
    /// </summary>
    public Composition.McpServices? Mcp { get; set; }

    /// <summary>
    /// The <c>tools</c> section's four <c>run_code</c> dials (wave W14): the inner-call concurrency, the two result sizes and
    /// the visible-tool list. A lookup rather than a value because all four are settings, and every consumer reads them on
    /// use: the description states the numbers and the registry filters by the list, so a change reaches the next turn
    /// without rebuilding a session host. Null = <see cref="CodeModeSettings.Default"/>.
    /// </summary>
    public Func<CodeModeSettings>? CodeMode { get; set; }

    /// <summary>
    /// The router the extension host client uses for plugin and workflow <c>host.tool_call</c>; each session registers its
    /// tool set and approval handler here when its host is built. Null without the composition root: nothing is registered.
    /// </summary>
    public SessionToolCallRouter? ToolRouter { get; set; }

    /// <summary>
    /// Names of the tools the user switched off in the Tools settings section; read before every turn and handed to
    /// <see cref="AgentHost.SetDisabledTools"/> (a no-op when unchanged). Null = no stored switch, in which case
    /// only <see cref="SubAgents"/>' switch can disable anything.
    /// </summary>
    public Func<IReadOnlyList<string>>? DisabledTools { get; set; }

    /// <summary>
    /// The user's global tool permission setting (the <c>tools</c> section's 「工作区授权 / 全部授权」); read on every approval
    /// evaluation, so a change reaches the next call of every running session. Null = <see cref="ToolPermissionMode.Workspace"/>.
    /// </summary>
    public Func<ToolPermissionMode>? ToolPermissionMode { get; set; }

    /// <summary>
    /// The <c>agents</c> section's two dials: how many agents of a session tree may run at once, and whether a session may
    /// spawn sub-agents at all. Read when a host is built and again before every turn, so a change reaches a running session
    /// from its next turn: a raised cap lets agents that are already queued start at once, a lower one only stops new ones
    /// from starting, and switching spawning off takes <c>spawn_agent</c> and <c>request_review</c> (it runs a Reviewer
    /// sub-agent) out of the model's tool set while the control tools
    /// a parent already needs (steer, follow-up, stop) stay. Null = <see cref="SubAgentLimits.Default"/> (or
    /// a runtime assembled without the settings store).
    /// </summary>
    public Func<SubAgentLimits>? SubAgents { get; set; }

    /// <summary>
    /// The prompt-cache lifetime policy of a provider (<c>provider.promptCacheTtl</c>) handed to <see cref="AgentOptions.CacheTtl"/>:
    /// <c>"auto"</c>/null, <c>"5m"</c> or <c>"1h"</c>. A lookup rather than a value because the policy belongs to the provider the
    /// session is pointed at, and that can change between hosts.
    /// </summary>
    public Func<string?, string?>? PromptCacheTtl { get; set; }

    // --- compaction (feature B) ---
    /// <summary>Effective context window of a model (the catalog's resolution chain); null = unknown, no automatic compaction.</summary>
    public Func<ModelSettings, int?>? ContextWindow { get; set; }

    /// <summary>Largest output of a model when its settings do not pin one; null = unknown (the reserve is then 20% of the window).</summary>
    public Func<ModelSettings, int?>? MaxOutputTokens { get; set; }

    /// <summary>
    /// The output cap a request for the model is really sent with, when the protocol always carries one — the catalogue's
    /// limit with the adapter's own thinking lift applied. Compaction's output reserve has to be this figure, otherwise a
    /// trigger line can sit above the request's real prompt limit. Null = the protocol sends no cap unless the model pins one.
    /// </summary>
    public Func<ModelSettings, int?>? WireMaxOutputTokens { get; set; }

    /// <summary>
    /// What a model accepts from the catalog, handed to <see cref="AgentOptions.ModelInputs"/>: a model known to take no
    /// image input receives a tool result's text instead of its pictures. A lookup rather than a value because the session
    /// can be pointed at another provider between hosts.
    /// </summary>
    public Func<ModelSettings, ModelInputs?>? ModelInputs { get; set; }

    /// <summary>
    /// Whether tool results may send their images to the model (the <c>chat</c> settings switch), handed to
    /// <see cref="AgentOptions.SendToolImagesToModel"/> as the live lookup it is — the loop asks it before every request, so
    /// a flip applies from the next turn. Null means on.
    /// </summary>
    public Func<bool>? SendToolImages { get; set; }

    /// <summary>
    /// The <c>compaction</c> settings, read when a session's host is built, again before every turn and whenever
    /// <see cref="CompactionChanges"/> reports a save; null = the defaults (or the store's current value when only the store
    /// is set).
    /// </summary>
    public Func<CompactionSettings>? Compaction { get; set; }

    /// <summary>
    /// The settings store whose <see cref="CompactionSettingsStore.Changed"/> makes every live session re-apply the settings
    /// to its host at once, so a running turn uses them from its next model request. Null without the composition root that does not care.
    /// </summary>
    public CompactionSettingsStore? CompactionChanges { get; set; }

    /// <summary>The settings in force: <see cref="Compaction"/>, else the store's current value, else the defaults.</summary>
    internal CompactionSettings CurrentCompaction() =>
        Compaction?.Invoke() ?? CompactionChanges?.Current ?? CompactionSettings.Default;
    // --- end compaction ---

    public static string? UserDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, ".forge");
    }
}

/// <summary>
/// One main session's agent runtime. The <see cref="AgentHost"/> is built once, from the tool surface the extension host
/// has registered at that moment, and reused because the root <see cref="AgentInstance"/> carries the conversation
/// history. A session that already has stored messages is resumed through <see cref="AgentHost.ResumeRootAsync"/>, so a
/// reopened or restarted session continues with its context.
/// </summary>
internal sealed class SessionRuntime : IDisposable, ISessionToolScope, IAgentSessionScope
{
    private readonly AgentRuntimeServices services;
    private readonly Lock gate = new();
    private AgentHost? host;
    private AgentInstance? root;
    private AgentInstance? activeRoot;
    private RootCounters turnBaseline = RootCounters.None;

    /// <summary>
    /// The sink of the session this runtime drives, recorded as each turn starts (the tab's own event funnel). It outlives every turn,
    /// which is what a background child's started/finished events need - the turn that spawned it is gone by then.
    /// </summary>
    private Action<AgentEvent>? sessionSink;
    private AgentDefinition? rootDefinition;

    /// <summary>The orchestration block of the host being built; empty when the default orchestration is off or has no router.</summary>
    private string orchestration = string.Empty;

    /// <summary>
    /// Whether the read-only spec tools are offered to this host: decided once per host build (the tool definitions are bytes
    /// of the cached prefix), true when the orchestration is on or the workspace already holds specs with an id.
    /// </summary>
    private bool specToolsOn;

    /// <summary>False for a general-area session: its folder lives inside the shared General repository, so git tools would show every general session's changes at once.</summary>
    private bool gitToolsOn = true;

    /// <summary>True when the session's repository has a remote; the PR tools are only offered then. Re-decided when FORGE's pairing action reports a remote (<see cref="remoteAdded"/>); a remote added from outside is not noticed.</summary>
    private bool pullRequestToolsOn;

    /// <summary>Set by <see cref="RepositoryRemotes.Added"/> for this session's repository; consumed at the next turn boundary.</summary>
    private bool remoteAdded;

    /// <summary>What the chat can draw (diagrams, images, file links), told to the root; a snapshot per host like <see cref="orchestration"/>.</summary>
    private string chatRendering = string.Empty;

    /// <summary>The prompt layers of the host being built, kept so the resource window can size them.</summary>
    private PromptLayers? builtLayers;

    /// <summary>The model a default root runs on, kept so the decorated root definition is the same agent as the undecorated one.</summary>
    private ModelSettings rootModel;

    /// <summary>The <c>skill</c> tool of this session; one instance, its catalog is read per call.</summary>
    private SkillTool? skillTool;
    private bool rootStale;
    private List<Interjection>? undelivered;
    private SessionApprovalHandler? approvalHandler;
    private SessionQuestionHandler? questionHandler;
    private AgentHost? builtHost;
    private ExtensionHostClient? listenedClient;

    /// <summary>Watches the files the prompt snapshot was built from; replaced by every host build.</summary>
    private PromptSourceWatcher? promptWatcher;

    /// <summary>True once a prompt source changed after the current host's snapshot; cleared by <see cref="HotReloadPrompts"/>.</summary>
    private bool promptsStale;

    /// <summary>A hot reload was asked for: the next turn builds a fresh host (new snapshot) and resumes the root from the store.</summary>
    private bool reloadPending;

    /// <summary>
    /// The one recovery of continuable sub-agents this runtime does, not one per rebuilt host. It is a task rather than a flag
    /// because two callers resolve the host at the same time (a turn and an automation) and the second must wait for the first
    /// instead of spawning past a half-restored pool - an order that would hand out an id a stored child still holds.
    /// </summary>
    private Task? subAgentsRestore;

    /// <summary>The host <see cref="subAgentsRestore"/> recovered into: a rebuild newer than that task gets its own.</summary>
    private AgentHost? restoredHost;

    /// <summary>The rebuild a reload asked for has read the sources and armed its watcher: an edit from here on is real, even while the root agent is still being resumed.</summary>
    private bool reloadBuilt;

    /// <summary>Children started outside a turn (<see cref="SpawnChildAsync"/>) that are still running; they hold the host like a turn does.</summary>
    private int outsideChildren;
    private CompactionSettings? appliedCompaction;
    // Built eagerly / published once: the turn thread (RefreshMcpTools) and a tool thread (the search callback) both reach them,
    // and a second exposure instance created by a race would silently drop the pins the other one holds.
    private readonly McpToolExposure mcpExposure = new();
    private McpToolSearchTool? mcpSearchTool;
    private string mcpFocus = string.Empty;

    /// <summary>The sub-agent cap last handed to the host; an unchanged value never touches it. Guarded by <see cref="gate"/>.</summary>
    private int appliedMaxParallel = SubAgentLimits.DefaultParallel;
    private bool disposed;

    // Sub-agent sessions aliased to this runtime in the router, by child agent id. Guarded by gate.
    private readonly Dictionary<string, string> childSessions = new(StringComparer.Ordinal);

    /// <summary>
    /// The user-driven child sessions of this root (forge-app "Agent 会话的界面与接线"), by session id, with the name their
    /// cards show. Guarded by gate; entries stay for the life of the runtime so a released runtime can withdraw every card.
    /// </summary>
    private readonly Dictionary<string, string> userChildren = new(StringComparer.Ordinal);

    /// <summary>
    /// The user-driven run of this runtime the current flow belongs to. An <see cref="AsyncLocal{T}"/> because an
    /// <c>ApprovalRequest</c> names no session and a child's run shares the root's host; the value is set around that run only,
    /// so the root's own turn and a model-spawned child's run keep asking under the root. One per runtime, so a flow that
    /// reaches another session's tools is never mistaken for one of those sessions' runs.
    /// </summary>
    private readonly AsyncLocal<UserChildRun?> currentUserChild = new();

    /// <summary>One user-driven run on its way; the session id arrives with the run's own <see cref="AgentSubAgentStarted"/>.</summary>
    private sealed class UserChildRun
    {
        public string? SessionId;
    }

    /// <summary>
    /// The trailing line every request of this session carries for its model — that instance's own context budget, keyed by
    /// agent id (a sub-agent has its own window, so it gets its own line, never the root's). Null appends nothing; read when
    /// a host is built, so the shell sets it once the instruments exist.
    /// </summary>
    public Func<string, string?>? ContextBudget { get; set; }

    /// <summary>
    /// Who named this session's title, as the store last accepted it: null until the first write, then the derived
    /// placeholder or the model's own name. Kept here because a write lands mid-turn and the <see cref="Session"/> snapshot
    /// the runtime was built from does not change with it.
    /// </summary>
    private string? titleSource;

    /// <summary>
    /// 1 while the turn now running may still name the session: only the turn that starts its first user message, so a
    /// session is named once and a reopened one never again.
    /// </summary>
    private int namingOpen;

    /// <summary>
    /// The name this session is now called, and who wrote it (<c>derived</c> placeholder or <c>agent</c>), raised after the
    /// store accepted it. The shell relabels its tab and tree row from this; a write the store refused raises nothing.
    /// </summary>
    public event Action<string, string>? TitleNamed;

    public SessionRuntime(AgentRuntimeServices services, SessionInfo session)
    {
        this.services = services;
        Session = session;
        titleSource = session.TitleSource;
        rootModel = services.Model;
        if (services.CompactionChanges is { } changes)
        {
            changes.Changed += OnCompactionChanged;
        }

        RepositoryRemotes.Added += OnRemoteAdded;
    }

    public SessionInfo Session { get; }

    public string SessionId => Session.Id;

    /// <summary>
    /// The <c>settings.json</c> per-agent defaults this runtime's hosts resolve a kind's model with, so a view that has to show
    /// the same model (the composer's agent chip) reads it through the one source instead of a second store. Null without the
    /// composition root, exactly like <see cref="AgentRuntimeServices.AgentDefaults"/>.
    /// </summary>
    internal Func<string, ModelSettings?>? AgentDefaults => services.AgentDefaults;

    /// <summary>Control surface over this host's live sub-agents; null until the host is built.</summary>
    public ISubAgentControl? SubAgentControl
    {
        get
        {
            lock (gate)
            {
                return host?.SubAgentControl;
            }
        }
    }

    /// <summary>
    /// Stops one running tool call (the root's or a sub-agent's) without stopping the turn; see
    /// <see cref="AgentHost.CancelToolCall"/>. Non-blocking, safe from the UI thread. False when the host is not built yet or the
    /// call is no longer running.
    /// </summary>
    public bool CancelToolCall(string agentId, string callId)
    {
        AgentHost? built;
        lock (gate)
        {
            built = host;
        }

        return built?.CancelToolCall(agentId, callId) ?? false;
    }

    /// <summary>Sub-agent sessions this session spawned, ordered by last activity.</summary>
    public Task<IReadOnlyList<SessionInfo>> QueryChildrenAsync(CancellationToken ct) =>
        QueryChildrenAsync(SessionId, ct);

    /// <summary>Sub-agent sessions of any session, so a restored tree can descend below its direct children.</summary>
    public Task<IReadOnlyList<SessionInfo>> QueryChildrenAsync(string parentSessionId, CancellationToken ct) =>
        services.Catalog.QueryAsync(new SessionQuery { ParentSessionId = parentSessionId }, ct);

    public Task RunTurnAsync(string userText, Action<AgentEvent> sink, CancellationToken ct) =>
        RunTurnAsync(userText, [], sink, ct);

    /// <summary>
    /// Writes the placeholder title a turn's first user message describes, unless someone named the session first - the store
    /// decides that, so a rename landing mid-turn wins. Nothing usable in the text (an attachment-only send, a pasted stack
    /// trace) writes nothing and the creation default stays.
    /// </summary>
    public async Task DeriveTitleAsync(string firstUserText, CancellationToken ct)
    {
        if (Volatile.Read(ref titleSource) is not null || SessionTitles.Derive(firstUserText) is not { } title)
        {
            return;
        }

        await TryWriteTitleAsync(title, SessionTitleSource.Derived, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A title someone else wrote - a person through the rename dialog - so a later turn does not try the placeholder again.
    /// The runtime keeps its own copy because the <see cref="Session"/> snapshot it was built from never changes with a write.
    /// </summary>
    public void NoteNamed(string source) => Volatile.Write(ref titleSource, source);

    /// <summary>True while the turn now running may still write the model's name (<c>set_title</c>).</summary>
    internal bool CanName => Volatile.Read(ref namingOpen) == 1;

    /// <summary>
    /// The <c>set_title</c> seam: the name the model asked for, cut to one tab label and written only while this turn may still
    /// name the session - its first one. Null when nothing was written.
    /// </summary>
    private async ValueTask<string?> NameSessionAsync(string title, CancellationToken ct)
    {
        if (!CanName || SessionTitles.Name(title) is not { } name)
        {
            return null;
        }

        return await TryWriteTitleAsync(name, SessionTitleSource.Agent, ct).ConfigureAwait(false) ? name : null;
    }

    /// <summary>True while the title is still the creation default or the placeholder derived from the first message.</summary>
    private bool IsReplaceableTitle => Volatile.Read(ref titleSource) is null or SessionTitleSource.Derived;

    private async ValueTask<bool> TryWriteTitleAsync(string title, string source, CancellationToken ct)
    {
        var applied = await services.Catalog.TryTitleAsync(SessionId, title, source, ct).ConfigureAwait(false);
        if (applied)
        {
            Volatile.Write(ref titleSource, source);
            TitleNamed?.Invoke(title, source);
        }

        return applied;
    }

    /// <summary>
    /// Runs one user turn whose message is <paramref name="userText"/> followed by <paramref name="attachments"/> (images,
    /// files, inlined text files). The text may be empty only when at least one attachment is present.
    /// </summary>
    /// <param name="deriveTitle">Whether this turn's text may write the placeholder title. A user's message may; an automation
    /// turn may not - its session carries the automation's own name and was not created by a person typing a request.</param>
    public async Task RunTurnAsync(
        string userText,
        IReadOnlyList<ContentBlock> attachments,
        Action<AgentEvent> sink,
        CancellationToken ct,
        bool deriveTitle = true)
    {
        ArgumentNullException.ThrowIfNull(userText);
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count == 0)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userText);
        }

        ArgumentNullException.ThrowIfNull(sink);

        // The fallback baseline, in place before this turn's first await and before it resolves its own instance: naming the
        // session, reading its window or resolving the root can still fail or be cancelled, and measuring against what the
        // runtime already held is what stops such a zero-write turn from naming a message it never wrote - an idle delivery
        // may have appended to that instance since the previous turn. A resolved turn overwrites it below.
        var held = ReadRootCounters();
        lock (gate)
        {
            turnBaseline = held;
        }

        var message = UserMessage(userText, attachments);

        // The session's sink outlives this turn and is what a background child's events go to after this run's channel is done.
        Volatile.Write(ref sessionSink, sink);

        // The first user message of an unnamed session is what its placeholder title describes: written here, before the host
        // is resolved, so the tab and the tree row already read as the task while the model streams its first token. A session
        // someone already named is left alone, and a message with nothing usable in it keeps the creation default.
        if (deriveTitle)
        {
            // The orchestration directive travels at the head of the message; the label describes what the person asked for,
            // not the instruction the composer wrapped around it.
            await DeriveTitleAsync(OrchestrationDirective.Strip(userText), ct).ConfigureAwait(false);
        }

        // set_title has one window: the turn that starts this session's first user message. The store says whether that turn
        // ever started, asked again every turn so a first turn that never wrote a message (cancelled, or failed before the
        // user turn landed) leaves the window open - and a session reopened after it already holds a message is neither named
        // nor nudged again. An automation turn never opens the window: the automation named the session.
        Volatile.Write(
            ref namingOpen,
            deriveTitle && IsReplaceableTitle && await services.Store.LastSeqAsync(SessionId, ct).ConfigureAwait(false) == 0
                ? 1
                : 0);

        // What this request is about decides which MCP tools are offered (see RefreshMcpTools); it is read when the tool
        // surface is refreshed at the start of the turn and never stored.
        Volatile.Write(ref mcpFocus, userText);

        // The turn's own baseline replaces the fallback the moment its instance exists: sampled here, on the instance this
        // turn resolved and before it wrote anything, it is both what the row is differenced against and the position the
        // row may name. The view cannot supply it: on the first turn of a runtime (a reopened tab, a fresh session) there
        // is no instance yet, and the first turn after a rebuild (a model, agent or step switch) has another one - either
        // way the identity proof never held, so those rows stayed positionless and read "—" on every replay.
        var (agentHost, agent) = await Task.Run(() => ResolveAsync(), ct).ConfigureAwait(false);
        var (baselineUsage, baselineStream) = agent.ReadCounters();
        lock (gate)
        {
            activeRoot = agent;
            turnBaseline = new RootCounters(baselineUsage, baselineStream, agent, agent.LastPersistedSeq);
        }

        try
        {
            await Task.Run(
                    async () =>
                    {
                        await foreach (var value in agentHost.RunAsync(agent, message, ct).ConfigureAwait(false))
                        {
                            TrackSubAgent(value);
                            sink(value);
                        }
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            // The window belongs to the turn that is ending, whatever became of it: the next turn asks the store again.
            Volatile.Write(ref namingOpen, 0);
            // Interject and this hand-back share the lock, so a message is either delivered by the loop or handed back.
            lock (gate)
            {
                activeRoot = null;
                var left = agent.TakeInterjections();
                if (left.Count > 0)
                {
                    (undelivered ??= []).AddRange(left);
                }
            }
        }
    }

    /// <summary>The user message of a turn: the text block (when there is text), then the attachments in attaching order.</summary>
    internal static ChatMessage UserMessage(string text, IReadOnlyList<ContentBlock> attachments)
    {
        if (attachments.Count == 0)
        {
            return ChatMessage.User(text);
        }

        return new ChatMessage(MessageRole.User, UserContent(text, attachments));
    }

    private static List<ContentBlock> UserContent(string text, IReadOnlyList<ContentBlock> attachments)
    {
        var content = new List<ContentBlock>(attachments.Count + 1);
        if (!string.IsNullOrWhiteSpace(text))
        {
            content.Add(new TextBlock(text));
        }

        content.AddRange(attachments);
        return content;
    }

    /// <summary>
    /// Starts a <paramref name="kind"/> sub-agent under this session's root outside a turn (automations, e.g. the Scout
    /// that analyses a CI failure). Its events reach <paramref name="sink"/> on a worker thread, like a tool-spawned child.
    /// </summary>
    public async Task<ChildAgentResult> SpawnChildAsync(string kind, string task, Action<AgentEvent> sink, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        ArgumentNullException.ThrowIfNull(sink);

        // Counted before the host is resolved, so a reload can never swap the host (and dispose its approval handlers) in between.
        lock (gate)
        {
            outsideChildren++;
        }

        try
        {
            var (agentHost, agent) = await Task.Run(() => ResolveAsync(), ct).ConfigureAwait(false);
            var forward = new ForwardingSink(value =>
            {
                TrackSubAgent(value);
                sink(value);
            });
            return await Task.Run(() => agentHost.SpawnChildAsync(agent, kind, task, forward, ct), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                outsideChildren--;
            }
        }
    }

    private sealed class ForwardingSink(Action<AgentEvent> forward) : IAgentEventSink
    {
        public void Emit(AgentEvent value) => forward(value);
    }

    /// <summary>
    /// Starts a user-driven session under this host's root and runs its first turn (Core: <see cref="AgentHost.StartUserChildAsync"/>).
    /// The caller names the agent (<paramref name="definition"/>), so the tab's kind and model are the user's pick; the
    /// session row, the run slot, the approval path and the cancellation table are the same ones a model spawn uses. The
    /// host is built first: a user session can only start under a root the host already holds, and opening its tab must not
    /// take the focus from the session the user is in.
    /// </summary>
    internal async Task<SubAgentOutcome> StartUserChildAsync(
        AgentDefinition definition,
        string text,
        IReadOnlyList<ContentBlock> attachments,
        string? sessionTitle,
        Action<AgentEvent> sink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(sink);
        // Counted before the host is resolved, exactly like SpawnChildAsync: a prompt reload must not swap the host (and
        // dispose its approval handlers) in between.
        lock (gate)
        {
            outsideChildren++;
        }

        // The run carries its session: the row and the id only exist once the run announced itself, so the holder starts empty
        // and the sink fills it in before any tool of this run can ask for approval. It is set right before the run starts, so
        // the value belongs to the run's flow alone.
        try
        {
            var agentHost = await ResolveHostAsync(ct).ConfigureAwait(false);
            var run = new UserChildRun();
            var forward = new ForwardingSink(value =>
            {
                TrackUserChildRun(value, run, sessionTitle);
                sink(value);
            });
            var outer = currentUserChild.Value;
            currentUserChild.Value = run;
            try
            {
                var message = UserMessage(text, attachments);
                return await Task.Run(() => agentHost.StartUserChildAsync(definition, message, sessionTitle, forward, ct), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                currentUserChild.Value = outer;
            }
        }
        finally
        {
            lock (gate)
            {
                outsideChildren--;
            }
        }
    }

    /// <summary>
    /// One more turn of a user session (Core: <see cref="AgentHost.RunUserChildTurnAsync"/>): one message is one run, so it
    /// queues for a tree slot exactly like the first one and ends with one <see cref="AgentSubAgentFinished"/> carrying that
    /// run's usage. The events reach <paramref name="sink"/> on a worker thread.
    /// </summary>
    internal async Task RunUserChildTurnAsync(
        AgentInstance child,
        string text,
        IReadOnlyList<ContentBlock> attachments,
        Action<AgentEvent> sink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(sink);
        var agentHost = await ResolveHostAsync(ct).ConfigureAwait(false);
        var message = UserMessage(text, attachments);

        // A later turn of a session this runtime already holds: the id is known before the run starts.
        var run = new UserChildRun { SessionId = child.SessionId };
        var outer = currentUserChild.Value;
        currentUserChild.Value = run;
        try
        {
            await Task.Run(
                    async () =>
                    {
                        await foreach (var value in agentHost.RunUserChildTurnAsync(child, message, ct).ConfigureAwait(false))
                        {
                            TrackUserChildRun(value, run, title: null);
                            sink(value);
                        }
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            currentUserChild.Value = outer;
        }
    }

    /// <summary>
    /// Rebuilds a stored user session as an instance of this host (Core: <see cref="AgentHost.ResumeUserChildAsync"/>) after a
    /// restart or a released tab. Idempotent: an instance this host already holds for that session is returned as-is.
    /// </summary>
    internal async Task<AgentInstance> ResumeUserChildAsync(
        string sessionId,
        string workingDirectory,
        AgentDefinition definition,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var agentHost = await ResolveHostAsync(ct).ConfigureAwait(false);
        var child = await agentHost.ResumeUserChildAsync(sessionId, workingDirectory, definition, ct).ConfigureAwait(false);
        RegisterUserChild(sessionId, await StoredTitleAsync(sessionId, definition, ct).ConfigureAwait(false));
        return child;
    }

    /// <summary>
    /// The instance this host holds for the user-driven session <paramref name="sessionId"/>, running or idle; null when it
    /// holds none (a session this host never resumed, or one whose instance was forgotten).
    /// </summary>
    internal AgentInstance? FindUserChild(string sessionId)
    {
        AgentHost? built;
        lock (gate)
        {
            built = host;
        }

        return built?.FindUserChild(sessionId);
    }

    /// <summary>
    /// Drops the instance this host holds for a user session so the next <see cref="ResumeUserChildAsync"/> really rebuilds it
    /// from the store with the definition handed there (Core: <see cref="AgentHost.ForgetUserChild"/>, which also states what
    /// stays untouched). Only the user's own change of an agent session's kind or model asks for it: the tab calls it right
    /// before that resume, so the change takes effect from the next turn and a turn still running keeps its instance.
    /// </summary>
    internal bool ForgetUserChild(string? agentId)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            return false;
        }

        AgentHost? built;
        lock (gate)
        {
            built = host;
        }

        return built?.ForgetUserChild(agentId) ?? false;
    }

    /// <summary>
    /// The name a user-driven child's cards show: the store's title, which is what the tab and the tree row name the session.
    /// The kind's name stands in only when the store has no row to read (a resumed session that vanished).
    /// </summary>
    private async Task<string> StoredTitleAsync(string sessionId, AgentDefinition definition, CancellationToken ct)
    {
        try
        {
            var stored = await services.Store.GetSessionAsync(sessionId, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stored?.Title))
            {
                return stored.Title;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Approval.ZLogWarning($"the title of the agent session '{sessionId}' could not be read: {exception.Message}");
        }

        return definition.Name;
    }

    /// <summary>
    /// One event of a user-driven run. Its own start is emitted ahead of the loop, so the first one names this session: the
    /// id is remembered (the card of a later approval or question belongs to it) and aliased to this runtime in the tool
    /// router, exactly like a model-spawned child, so a plugin or workflow call of that session still resolves against the
    /// root's tools, policy and approval handler. Later starts are the session's own children, which are ordinary sub-agents.
    /// </summary>
    private void TrackUserChildRun(AgentEvent value, UserChildRun run, string? title)
    {
        TrackSubAgent(value);
        if (run.SessionId is null && value is AgentSubAgentStarted { ChildSessionId.Length: > 0 } started)
        {
            run.SessionId = started.ChildSessionId;
            RegisterUserChild(started.ChildSessionId, title);
        }
    }

    /// <summary>Remembers a user-driven child session of this root and the name its cards show.</summary>
    private void RegisterUserChild(string sessionId, string? title)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (!userChildren.ContainsKey(sessionId) || !string.IsNullOrWhiteSpace(title))
            {
                userChildren[sessionId] = string.IsNullOrWhiteSpace(title) ? sessionId : title;
            }
        }
    }

    /// <summary>
    /// Stops every run of a user session under this root, queued or running (Core: <see cref="AgentHost.StopUserChildren"/>),
    /// and returns how many were stopped. The root's own turn is not touched: the shell stops that one itself and calls this
    /// when the user presses stop on the root's tab.
    /// </summary>
    internal int StopUserChildren(string? reason = null)
    {
        AgentHost? built;
        lock (gate)
        {
            built = host;
        }

        return built?.StopUserChildren(reason) ?? 0;
    }

    /// <summary>
    /// Stops the background children spawned from <paramref name="parentSessionId"/> on this host - every one of them when it is
    /// null, which is what a root's stop means (the whole tree). A spawned child (<c>spawn_agent</c> never waits) is not
    /// linked to the turn that started it, so this is the path that ends it; the count is how many runs were asked to stop. Never
    /// throws - a host that was never built holds none.
    /// </summary>
    internal int StopBackgroundChildren(string? parentSessionId, string reason)
    {
        AgentHost? built;
        lock (gate)
        {
            built = host;
        }

        return built?.StopBackgroundChildren(parentSessionId, reason) ?? 0;
    }

    /// <summary>
    /// Stops every background child of this host and refuses new ones: the runtime is being torn down (a released session, a closed
    /// window), so a run whose stop button nobody can reach must not start or keep going on a released store and workspace.
    /// </summary>
    internal int CloseBackgroundChildren(string reason)
    {
        AgentHost? built;
        lock (gate)
        {
            built = host;
        }

        return built?.CloseBackgroundChildren(reason) ?? 0;
    }

    /// <summary>Stops one user session's current run by its agent id - the stop button of its own tab; false when it is not running.</summary>
    internal bool StopUserChild(string agentId, string reason)
    {
        if (string.IsNullOrEmpty(agentId) || SubAgentControl is not { } control)
        {
            return false;
        }

        return control.Stop(agentId, reason) == SubAgentControlResult.Accepted;
    }

    // ---------------------------------------------------------------- session messaging / reply receipts

    /// <summary>The host's mailboxes and grants; null until the host is built, which is when the first turn needs it.</summary>
    internal SessionMessenger? SessionMessaging
    {
        get
        {
            lock (gate)
            {
                return host?.SessionMessaging;
            }
        }
    }

    /// <summary>
    /// The user picked <paramref name="targetSessionId"/> in the composer's <c>@</c> picker of
    /// <paramref name="initiatorSessionId"/>: record the pair in the host's grant table (forge-core "session messaging").
    /// <para>The host may not exist yet - the very first message of a session is composed before its first turn builds one -
    /// so a grant that cannot be written now is kept and applied the moment the host exists. The grant is never dropped:
    /// the user's pick is the authorization, and the message that carries it is already on its way.</para>
    /// </summary>
    internal void GrantSessionMessage(string initiatorSessionId, string targetSessionId, string targetName)
    {
        if (string.IsNullOrEmpty(initiatorSessionId) || string.IsNullOrEmpty(targetSessionId))
        {
            return;
        }

        AgentHost? built;
        lock (gate)
        {
            built = host;
        }

        if (built is null || !TryGrant(built, initiatorSessionId, targetSessionId, targetName))
        {
            lock (gate)
            {
                pendingGrants.Add((initiatorSessionId, targetSessionId, targetName ?? string.Empty));
            }
        }
    }

    /// <summary>Grants waiting for this session's host to be built; applied in <see cref="ResolveAsync"/> right after it is.</summary>
    private readonly List<(string Initiator, string Target, string Name)> pendingGrants = [];

    /// <summary>Raised after grants moved (a queued grant applied, a revoke): the tabs refresh their input marks.</summary>
    internal event Action? SessionGrantsChanged;

    private static bool TryGrant(AgentHost host, string initiator, string target, string name)
    {
        try
        {
            return host.SessionMessaging.Grant(initiator, target, name) is not null;
        }
        catch (InvalidOperationException)
        {
            // The host exists but has no root yet: the grant waits like the ones taken before the build.
            return false;
        }
    }

    /// <summary>Applies the grants taken before this session's host existed. Caller does not hold <see cref="gate"/>.</summary>
    private void ApplyPendingGrants(AgentHost built)
    {
        (string Initiator, string Target, string Name)[] waiting;
        lock (gate)
        {
            if (pendingGrants.Count == 0)
            {
                return;
            }

            waiting = [.. pendingGrants];
            pendingGrants.Clear();
        }

        foreach (var grant in waiting)
        {
            if (!TryGrant(built, grant.Initiator, grant.Target, grant.Name))
            {
                // Still no root: keep it for the next boundary instead of losing the user's pick.
                lock (gate)
                {
                    pendingGrants.Add(grant);
                }
            }
        }

        SessionGrantsChanged?.Invoke();
    }

    /// <summary>The pairs <paramref name="initiatorSessionId"/> holds, for the input area's revocable marks.</summary>
    internal IReadOnlyList<SessionGrant> SessionMessageGrants(string initiatorSessionId) =>
        SessionMessaging?.Authorizations.GrantsOf(initiatorSessionId) ?? [];

    /// <summary>
    /// The user pressed the revoke button of one mark: that one pair goes (the same target may still be granted to another
    /// session of the tree). False when the host is not built or the pair was never granted.
    /// </summary>
    internal bool RevokeSessionMessage(string initiatorSessionId, string targetSessionId)
    {
        var revoked = SessionMessaging?.Authorizations.Revoke(initiatorSessionId, targetSessionId) ?? false;
        if (revoked)
        {
            SessionGrantsChanged?.Invoke();
        }

        return revoked;
    }

    /// <summary>Withdraws every grant that names <paramref name="targetSessionId"/> as its target (it was archived or closed).</summary>
    internal int RevokeTargetSessionMessages(string targetSessionId) =>
        SessionMessaging?.Authorizations.RevokeTarget(targetSessionId) ?? 0;

    /// <summary>Withdraws every grant of this root's tree (it left <see cref="SessionStatus.Active"/>).</summary>
    internal int RevokeRootSessionMessages() =>
        SessionMessaging?.Authorizations.RevokeRoot(SessionId) ?? 0;

    /// <summary>
    /// Hands one receipt to the running turn of <paramref name="sessionId"/> - the root's or a user-driven child's - so the
    /// loop delivers it at the next tool-batch boundary (forge-core "reply receipt"). False when no turn of that session runs, in
    /// which case the receipt goes to the transcript instead (never into a new turn).
    /// </summary>
    internal bool TryInterjectSessionMessage(string sessionId, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (string.Equals(sessionId, SessionId, StringComparison.Ordinal))
        {
            return TryInterject(text) is not null;
        }

        return TryInterjectChild(sessionId, text, []) is not null;
    }

    /// <summary>
    /// Adds one message to a session's history without running a turn (a receipt that arrived while it was idle). The
    /// caller checked that no turn of that session is running.
    /// </summary>
    internal async Task AppendSessionMessageAsync(string sessionId, string text, CancellationToken ct)
    {
        AgentHost? built;
        lock (gate)
        {
            built = host;
        }

        if (built is null)
        {
            // Nothing is loaded: write the message into the store so the next resume reads it like any other user message.
            await services.Store.AppendAsync(sessionId, ChatMessage.User(text), null, ct).ConfigureAwait(false);
            return;
        }

        await built.AppendSessionMessageAsync(sessionId, text, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The host of this session, building it (and resuming the root) when it does not exist yet. Used before a user session
    /// starts, so the runtime is loaded without the root's tab being opened or brought to the front.
    /// </summary>
    private async Task<AgentHost> ResolveHostAsync(CancellationToken ct) =>
        (await Task.Run(() => ResolveAsync(), ct).ConfigureAwait(false)).Host;

    /// <summary>
    /// The definition the root agent runs with from the next turn on: null is the default main agent with the configured
    /// model. A change rebuilds the root from the store before the next turn, so the conversation keeps its context; the
    /// turn already running keeps the definition it started with.
    /// </summary>
    public AgentDefinition? RootDefinition
    {
        get
        {
            lock (gate)
            {
                return rootDefinition;
            }
        }
    }

    /// <summary>Sets <see cref="RootDefinition"/>; returns false when it is unchanged (same kind and model settings).</summary>
    public bool SetRootDefinition(AgentDefinition? definition)
    {
        lock (gate)
        {
            if (SameDefinition(rootDefinition, definition))
            {
                return false;
            }

            rootDefinition = definition;
            rootStale = true;
            return true;
        }
    }

    private static bool SameDefinition(AgentDefinition? left, AgentDefinition? right) =>
        left is null || right is null
            ? left is null && right is null
            : string.Equals(left.Name, right.Name, StringComparison.Ordinal) && left.Model == right.Model &&
              string.Equals(left.Prompt, right.Prompt, StringComparison.Ordinal) &&
              ReferenceEquals(left.ToolNames, right.ToolNames);

    /// <summary>
    /// The root definition with the orchestration block appended to its role text. Only the root gets it: a sub-agent is
    /// handed one scoped task by its parent and must not route or interview. The decoration is applied on the way into
    /// the host, never stored in <see cref="rootDefinition"/>, so <see cref="SameDefinition"/> keeps comparing what the
    /// user chose and a decorated root does not look changed on every turn.
    /// </summary>
    private AgentDefinition? WithOrchestration(AgentDefinition? definition)
    {
        // The orchestration block points at the skill tool, so it needs an agent that can call it; the rendering block does not.
        var extra = orchestration.Length > 0 && OrchestrationPrompt.CanLoadSkills(definition)
            ? chatRendering.Length > 0 ? orchestration + "\n\n" + chatRendering : orchestration
            : chatRendering;
        if (extra.Length == 0)
        {
            return definition;
        }

        // The same agent AgentHost.CreateRoot would build by default, with the blocks as its role text.
        return definition is null
            ? new AgentDefinition("Root", "Main coding agent.", extra, rootModel)
            : definition with
            {
                Prompt = definition.Prompt.Length == 0 ? extra : definition.Prompt + "\n\n" + extra,
            };
    }

    /// <summary>The skills this session can load: the workspace's, the user's, then the product's own (lowest priority).</summary>
    private IReadOnlyList<Services.SlashEntry> DiscoverSkills() =>
        Services.SkillCatalog.Discover(Session.WorkspacePath, AgentRuntimeServices.UserDirectory(), AgentRuntimeServices.ProductSkillsDirectory);

    /// <summary>
    /// Hands a user message to the running root turn; the loop delivers it at the next tool-batch boundary. Null when no
    /// turn is running, in which case the caller should queue the message instead.
    /// </summary>
    public Interjection? TryInterject(string text) => TryInterject(text, []);

    /// <summary>
    /// Hands a user message with attachments (images, files, inlined text files) to the running root turn, delivered with
    /// them at the next tool-batch boundary. The text may be empty only when an attachment is present. Null when no turn
    /// is running. The returned message can be taken back with <see cref="TryWithdraw"/> until the loop delivers it.
    /// </summary>
    public Interjection? TryInterject(string text, IReadOnlyList<ContentBlock> attachments)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count == 0)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
        }

        var content = UserContent(text, attachments);
        lock (gate)
        {
            if (disposed || activeRoot is null)
            {
                return null;
            }

            return activeRoot.Interject(content);
        }
    }

    /// <summary>
    /// Takes back an interjection the loop has not delivered yet (the user withdrew it to edit it). False when it already
    /// reached the model, where the message is part of the history and nothing can undo it.
    /// </summary>
    public bool TryWithdraw(Interjection interjection)
    {
        ArgumentNullException.ThrowIfNull(interjection);
        lock (gate)
        {
            return activeRoot?.Withdraw(interjection) == true;
        }
    }

    /// <summary>
    /// Hands a user message with attachments to the running turn of the user-driven session <paramref name="sessionId"/> (an
    /// agent-session tab's own run), delivered with them at the next tool-batch boundary - the root's very inbox on the root's
    /// own turn, this session's instance here. The text may be empty only when an attachment is present. Null when this host
    /// holds no instance for that session, in which case the caller queues the message as it does for the root.
    /// </summary>
    internal Interjection? TryInterjectChild(string sessionId, string text, IReadOnlyList<ContentBlock> attachments)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count == 0)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
        }

        if (string.IsNullOrEmpty(sessionId) || FindUserChild(sessionId) is not { } child)
        {
            return null;
        }

        return child.Interject(UserContent(text, attachments));
    }

    /// <summary>
    /// Takes back an interjection of a user-driven session's turn the loop has not delivered yet, the child's counterpart of
    /// <see cref="TryWithdraw"/>. False when it already reached the model or that session holds none.
    /// </summary>
    internal bool TryWithdrawChild(string sessionId, Interjection interjection)
    {
        ArgumentNullException.ThrowIfNull(interjection);
        return !string.IsNullOrEmpty(sessionId) && FindUserChild(sessionId)?.Withdraw(interjection) == true;
    }

    // --- compaction (feature B) ---
    /// <summary>
    /// Manual compaction of the root context (<c>/compact [focus]</c>, the context gauge): ignores the threshold and the
    /// automatic-compaction switch, keeps the newest keepRecent tokens verbatim, passes <paramref name="instructions"/> to
    /// the summarizer as additional focus and re-enables automatic compaction. Returns null when nothing was compacted.
    /// Throws <see cref="InvalidOperationException"/> while a turn runs (the caller offers it only when the session is
    /// idle) and <see cref="CompactionFailedException"/> when no usable summary came back.
    /// </summary>
    public async Task<CompactionResult?> CompactNowAsync(Action<AgentEvent> sink, CancellationToken ct) =>
        await CompactNowAsync(sink, null, ct).ConfigureAwait(false);

    /// <inheritdoc cref="CompactNowAsync(Action{AgentEvent}, CancellationToken)"/>
    public async Task<CompactionResult?> CompactNowAsync(Action<AgentEvent> sink, string? instructions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var (agentHost, agent) = await Task.Run(() => ResolveAsync(), ct).ConfigureAwait(false);
        lock (gate)
        {
            if (activeRoot is not null)
            {
                throw new InvalidOperationException("A turn is running.");
            }

            activeRoot = agent;
        }

        try
        {
            return await Task.Run(() => agentHost.CompactAsync(agent, new ForwardingSink(sink), instructions, ct), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(activeRoot, agent))
                {
                    activeRoot = null;
                }
            }
        }
    }
    // --- end compaction ---

    /// <summary>Whether interjections wait to be taken (a busy reason of the handover check); does not take them.</summary>
    public bool HasUndeliveredInterjections
    {
        get
        {
            lock (gate)
            {
                return undelivered is { Count: > 0 };
            }
        }
    }

    /// <summary>Interjections the last turn ended without delivering, oldest first, with their attachments; each is returned once.</summary>
    public IReadOnlyList<Interjection> TakeUndeliveredInterjections()
    {
        lock (gate)
        {
            var taken = undelivered;
            undelivered = null;
            return taken is null ? [] : taken;
        }
    }

    /// <summary>
    /// A sub-agent's tool context carries its own child session id, but it shares this runtime's host, tool set and approval
    /// handler, so its plugin and workflow <c>host.tool_call</c> requests belong to this (root) session. The child id is
    /// aliased to this scope while the child runs: from <see cref="AgentSubAgentStarted"/> (any depth) to
    /// <see cref="AgentSubAgentFinished"/> or disposal. Cheap: one short lock and a dictionary write.
    /// </summary>
    internal void TrackSubAgent(AgentEvent value)
    {
        var router = services.ToolRouter;
        if (router is null)
        {
            return;
        }

        switch (value)
        {
            case AgentSubAgentStarted started when !string.IsNullOrWhiteSpace(started.ChildSessionId):
                lock (gate)
                {
                    if (disposed)
                    {
                        return;
                    }

                    childSessions[started.ChildId] = started.ChildSessionId;
                    router.Register(started.ChildSessionId, this);
                }

                break;
            case AgentSubAgentFinished finished:
                string? sessionId;
                lock (gate)
                {
                    if (!childSessions.Remove(finished.AgentId, out sessionId))
                    {
                        return;
                    }

                    router.Unregister(sessionId, this);
                }

                break;
        }
    }

    /// <summary>
    /// The counters and the position of the instance the last run resolved for its turn, sampled before that turn wrote
    /// anything: what the turn's metrics row is differenced against and the position it may name.
    /// </summary>
    internal RootCounters TurnBaseline
    {
        get
        {
            lock (gate)
            {
                return turnBaseline;
            }
        }
    }

    /// <summary>
    /// The current root instance's cumulative counters, read live (they only move on the agent's own thread, and a turn's end
    /// is read after its event stream finished) and as one value (<see cref="AgentInstance.ReadCounters"/>).
    /// <see cref="RootCounters.None"/> before the root exists.
    /// </summary>
    internal RootCounters ReadRootCounters()
    {
        AgentInstance? current;
        lock (gate)
        {
            current = root;
        }

        if (current is null)
        {
            return RootCounters.None;
        }

        var (usage, stream) = current.ReadCounters();
        return new RootCounters(usage, stream, current, current.LastPersistedSeq);
    }

    /// <summary>
    /// One instance this host holds - the root's, or a user-driven child's - with its cumulative counters, read live (they
    /// only move on that instance's own thread, so a reading before a run and one after it are a turn's share).
    /// <see cref="AgentInstance.ReadCounters"/> hands the pair over as one value: usage and streaming time can never come from
    /// two different requests. False when the host is not built or no longer holds that instance: a caller then keeps its last
    /// reading instead of a zero.
    /// </summary>
    internal bool TryReadAgentCounters(string? agentId, out RootCounters counters)
    {
        if (!string.IsNullOrEmpty(agentId))
        {
            AgentHost? live;
            lock (gate)
            {
                live = host;
            }

            if (live is not null && live.SubAgents.TryGetInstance(agentId, out var instance))
            {
                var (usage, stream) = instance.ReadCounters();
                counters = new RootCounters(usage, stream, instance, instance.LastPersistedSeq);
                return true;
            }
        }

        counters = RootCounters.None;
        return false;
    }

    private async Task<(AgentHost Host, AgentInstance Agent)> ResolveAsync()
    {
        // The host is already built and current: hand it out, but never before a recovery the first caller started is done.
        if (await ReuseBuiltAsync().ConfigureAwait(false) is { } reused)
        {
            return reused;
        }

        AgentHost built;
        AgentInstance agent;
        AgentDefinition? definition;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            // A changed root definition reuses the host (its tools, approval handler and router registration) and only
            // rebuilds the root agent from the store.
            built = host is not null && !reloadPending ? host : Build();
            reloadBuilt = reloadPending;
            definition = rootDefinition;
        }

        agent = await built.ResumeRootAsync(SessionId, Session.WorkspacePath, CancellationToken.None, WithOrchestration(definition)).ConfigureAwait(false);
        lock (gate)
        {
            host = built;
            reloadPending = false;
            reloadBuilt = false;
            root = agent;
            ApplyDisabledTools(host);
            ApplySubAgentLimits(host);
            RefreshWebTools(host);
            RefreshMcpTools(host);
            ApplyCompaction(host);
            rootStale = !SameDefinition(rootDefinition, definition);

            // Subscribed here rather than in Build: a registration that arrives before the host field is set would otherwise
            // be handed to nothing, and the loop built afterwards already reads the current surface.
            if (listenedClient is null && services.HostClient is { } client)
            {
                listenedClient = client;
                client.ToolsChanged += OnToolsChanged;
            }
        }

        // The host now has its root, so the grants the user picked before it existed can be written.
        ApplyPendingGrants(built);

        // Once per runtime: the children a previous process left continuable come back as instances, so the first turn of a
        // restarted session can hand its task to the child that already did it (forge-core "子 Agent 复用").
        await RestoreSubAgentsOnceAsync(built).ConfigureAwait(false);

        return (built, agent);
    }

    /// <summary>
    /// The already-built host, when it is current: the tool and limit refreshes run under the gate and the caller gets the pair
    /// back only after the sub-agent recovery it published has finished - a caller that spawned past a half-restored pool would
    /// hand out an id a stored child still holds. Null means the host has to be (re)built.
    /// </summary>
    private async Task<(AgentHost Host, AgentInstance Agent)?> ReuseBuiltAsync()
    {
        Task? restoring;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (host is null || root is null || rootStale || reloadPending)
            {
                return null;
            }

            ApplyDisabledTools(host);
            ApplySubAgentLimits(host);
            RefreshWebTools(host);
            RefreshPullRequestTools(host);
            RefreshMcpTools(host);
            ApplyCompaction(host);
            ApplyPendingGrants(host);
            if (subAgentsRestore is null)
            {
                return (host, root);
            }

            restoring = subAgentsRestore;
        }

        await restoring.ConfigureAwait(false);
        lock (gate)
        {
            return (host, root);
        }
    }

    /// <summary>
    /// Runs the recovery for this runtime and awaits the same task afterwards. It is bound to the host it recovered into: a
    /// second host built before the first was published gets its own, which skips the ids the store already handed out.
    /// </summary>
    private Task RestoreSubAgentsOnceAsync(AgentHost built)
    {
        lock (gate)
        {
            if (restoredHost != built)
            {
                restoredHost = built;
                subAgentsRestore = built.RestoreSubAgentsAsync(SessionId, CancellationToken.None);
            }

            return subAgentsRestore!;
        }
    }

    /// <summary>
    /// Re-applies the user's tool switches. Whether the model may start a child is the allow-list's call (<c>spawn_agent</c>)
    /// and the depth limit's, nothing else; <c>request_review</c> follows its own entry. Publishes a new tool set only when a
    /// switch moved.
    /// </summary>
    private void ApplyDisabledTools(AgentHost target)
    {
        target.SetDisabledTools(services.DisabledTools is { } disabled ? ToolCatalog.EffectiveDisabled(disabled()) : []);
    }

    /// <summary>
    /// Hands the current sub-agent cap to the host when it differs from the one it last got, so an unchanged value never
    /// touches the host. Caller holds <see cref="gate"/>.
    /// </summary>
    /// <summary>
    /// Pushes the current sub-agent cap into the live host now, not at the next turn: a cap raised while children are queued
    /// must start them at once. A session that has not built its host yet has nothing to update (the build reads the cap).
    /// </summary>
    internal void RefreshSubAgentLimits()
    {
        lock (gate)
        {
            if (host is { } live)
            {
                ApplySubAgentLimits(live);
            }
        }
    }

    private void ApplySubAgentLimits(AgentHost target)
    {
        var limits = CurrentSubAgentLimits();
        if (limits.MaxParallel == appliedMaxParallel)
        {
            return;
        }

        target.SetMaxParallelSubAgents(limits.MaxParallel);
        appliedMaxParallel = limits.MaxParallel;
    }

    /// <summary>The sub-agent dials in force: the settings store's values, or the built-in defaults without one.</summary>
    private SubAgentLimits CurrentSubAgentLimits() => services.SubAgents?.Invoke() ?? SubAgentLimits.Default;

    /// <summary>
    /// Brings the web tools <paramref name="target"/> holds in line with the web settings: when the names the factory
    /// offers now differ from the held ones (<c>web_search</c> appearing because a backend became usable, or vanishing
    /// because it no longer is), the external set is collected afresh and published. Otherwise nothing is published, so
    /// the tool set version (and the prompt cache) survives an unrelated settings edit. Runs at the start of every turn
    /// with the gate held; a turn already running keeps its set until it ends, and <c>web_search</c>'s per-call backend
    /// read answers "not configured" if the backend went away meanwhile. A failure is traced and the next turn retries.
    /// </summary>
    private void RefreshWebTools(AgentHost target)
    {
        if (services.WebTools is not { } webTools)
        {
            return;
        }

        try
        {
            if (!SameWebTools(webTools(), target.AllTools.External))
            {
                target.ReplaceExternalTools(CollectExternalTools());
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Session.ZLogWarning($"the web tools could not be refreshed: {exception.Message}");
        }
    }

    private static bool IsWebTool(string name) =>
        name is Forge.Core.Tools.Web.WebFetchTool.Name or Forge.Core.Tools.Web.WebSearchTool.Name;

    /// <summary>Whether <paramref name="held"/> carries exactly the web tool names of <paramref name="wanted"/>; no allocation.</summary>
    private static bool SameWebTools(IReadOnlyList<ITool> wanted, IReadOnlyList<ITool> held)
    {
        var heldWeb = 0;
        foreach (var tool in held)
        {
            if (IsWebTool(tool.Definition.Name))
            {
                heldWeb++;
            }
        }

        if (heldWeb != wanted.Count)
        {
            return false;
        }

        foreach (var tool in wanted)
        {
            var found = false;
            foreach (var candidate in held)
            {
                if (string.Equals(candidate.Definition.Name, tool.Definition.Name, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Brings the MCP tools <paramref name="target"/> offers in line with the request this turn is about, the way
    /// <see cref="RefreshWebTools"/> does for the web tools: when the selected names differ from the held ones the external set
    /// is published afresh, otherwise nothing is published and the prompt cache survives. Called at the start of every turn with
    /// the gate held; a turn already running keeps its set until it ends.
    /// </summary>
    private void RefreshMcpTools(AgentHost target)
    {
        if (services.Mcp is not { } mcp)
        {
            return;
        }

        try
        {
            var selected = mcpExposure.Select(
                mcp.Manager.Tools,
                mcp.ToolSearchLimit,
                Volatile.Read(ref mcpFocus),
                out var truncated);
            var (builtIns, external) = target.AllTools;
            if (!SameMcpTools(selected, truncated, builtIns, external))
            {
                target.ReplaceExternalTools(CollectExternalTools());
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Session.ZLogWarning($"the MCP tools could not be refreshed: {exception.Message}");
        }
    }

    /// <summary>
    /// Appends the selected MCP tools and, while the limit hides some, the search tool that reaches them. A name already in
    /// <paramref name="into"/> (a plugin or web tool) wins over an MCP tool of the same name: the registry keys by name, so
    /// appending the second one would silently replace a tool the user installed with one a server happens to advertise.
    /// </summary>
    private void AppendMcpTools(List<ITool> into, Composition.McpServices mcp)
    {
        var selected = mcpExposure.Select(
            mcp.Manager.Tools,
            mcp.ToolSearchLimit,
            Volatile.Read(ref mcpFocus),
            out var truncated);
        if (selected.Count == 0 && !truncated)
        {
            return;
        }

        var taken = new HashSet<string>(into.Count + selected.Count + 1, StringComparer.Ordinal);
        foreach (var tool in into)
        {
            taken.Add(tool.Definition.Name);
        }

        foreach (var adapter in selected)
        {
            if (taken.Add(adapter.ExposedName))
            {
                into.Add(adapter);
            }
        }

        if (truncated && taken.Add(McpToolSearchTool.Name))
        {
            into.Add(LazyInitializer.EnsureInitialized(ref mcpSearchTool, () => new McpToolSearchTool(SearchMcpTools)));
        }
    }

    /// <summary>
    /// The search tool's callback: pin the matches, then republish so the next request really offers them. The model is told the
    /// matches are callable, so publishing before returning is what makes the answer true.
    /// </summary>
    private IReadOnlyList<McpToolAdapter> SearchMcpTools(string query, int limit)
    {
        if (services.Mcp is not { } mcp)
        {
            return [];
        }

        // Never pin more than one request may offer: the answer tells the model every match is callable now, and a match past
        // the limit would be pinned but cut from the very next surface.
        var cap = mcp.ToolSearchLimit;
        var matches = mcpExposure.Search(mcp.Manager.Tools, query, cap > 0 ? Math.Min(limit, cap) : limit);
        if (matches.Count > 0)
        {
            OnToolsChanged();
        }

        return matches;
    }

    /// <summary>
    /// Whether <paramref name="held"/> already carries exactly the MCP tools the selection names; no allocation. A held adapter
    /// counts only when its definition and side effect are still the selected one's: a server whose <c>readOnlyTools</c> or
    /// schema changed republishes adapters under the same names, and keeping the old one would offer a stale schema or — worse —
    /// skip the approval a tool that stopped being read-only now needs. A selected name a built-in or a plugin/web tool already
    /// holds is shadowed (see <see cref="AppendMcpTools"/>) and is expected to be absent.
    /// </summary>
    internal static bool SameMcpTools(
        IReadOnlyList<McpToolAdapter> selected,
        bool truncated,
        IReadOnlyList<ITool> builtIns,
        IReadOnlyList<ITool> held)
    {
        var heldCount = 0;
        var heldSearch = false;
        foreach (var tool in held)
        {
            if (tool is McpToolAdapter)
            {
                heldCount++;
            }
            else if (string.Equals(tool.Definition.Name, McpToolSearchTool.Name, StringComparison.Ordinal))
            {
                heldSearch = true;
            }
        }

        if (heldSearch != truncated)
        {
            return false;
        }

        var expected = 0;
        foreach (var adapter in selected)
        {
            var match = Find(held, adapter.ExposedName);
            if (match is McpToolAdapter current)
            {
                if (!ReferenceEquals(current, adapter) &&
                    (current.SideEffect != adapter.SideEffect || !current.Definition.Equals(adapter.Definition)))
                {
                    return false;
                }

                expected++;
            }
            else if (match is null && Find(builtIns, adapter.ExposedName) is null)
            {
                return false;
            }
        }

        return heldCount == expected;
    }

    private static ITool? Find(IReadOnlyList<ITool> tools, string name)
    {
        foreach (var tool in tools)
        {
            if (string.Equals(tool.Definition.Name, name, StringComparison.Ordinal))
            {
                return tool;
            }
        }

        return null;
    }

    /// <summary>
    /// Hands the current <c>compaction</c> settings to the host when they differ from what it last got; the window and
    /// max-output resolvers stay the catalog's. Caller holds <see cref="gate"/>.
    /// </summary>
    private void ApplyCompaction(AgentHost target)
    {
        var settings = services.CurrentCompaction();
        if (settings == appliedCompaction)
        {
            return;
        }

        target.SetCompactionPolicy(settings.ToPolicy(services.ContextWindow, services.MaxOutputTokens, services.WireMaxOutputTokens));
        appliedCompaction = settings;
    }

    /// <summary>A settings save reaches a built host at once, so even a running turn uses it from its next request.</summary>
    private void OnCompactionChanged(object? sender, EventArgs e)
    {
        lock (gate)
        {
            if (!disposed && host is { } current)
            {
                ApplyCompaction(current);
            }
        }
    }

    /// <summary>Whether this session belongs to the General project. Decided once per host build; a lookup failure keeps the git tools.</summary>
    private bool IsGeneralSession()
    {
        if (Session.ProjectId is not { Length: > 0 } projectId || services.Projects is not { } registry)
        {
            return false;
        }

        try
        {
            var list = Task.Run(() => registry.ListAsync(CancellationToken.None)).GetAwaiter().GetResult();
            return list.Any(project => string.Equals(project.Id, projectId, StringComparison.Ordinal) && project.Kind == ProjectKind.General);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Session.ZLogWarning($"the project kind could not be read: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Whether the working directory's repository has a remote, read straight from the git config (no git process). A
    /// directory without a repository has no remote.
    /// </summary>
    private bool ProbeRemote() => RepositoryRemotes.HasRemote(Session.WorkspacePath);

    /// <summary>
    /// FORGE's pairing action added a remote to a repository (the event is keyed by the repository's git config): when it is
    /// this session's repository, the PR group is re-decided at the next turn boundary, never under a running turn.
    /// </summary>
    private void OnRemoteAdded(string configPath)
    {
        if (RepositoryRemotes.SameConfig(configPath, RepositoryRemotes.FindConfig(Session.WorkspacePath)))
        {
            Volatile.Write(ref remoteAdded, true);
        }
    }

    /// <summary>
    /// Adds the PR group when the pairing action gave the repository a remote since the last decision: replaces the external
    /// tools only when the answer moved, so the prefix breaks from the PR group on, never before it. Caller holds <see cref="gate"/>.
    /// </summary>
    private void RefreshPullRequestTools(AgentHost target)
    {
        if (!gitToolsOn || !Volatile.Read(ref remoteAdded))
        {
            return;
        }

        remoteAdded = false;
        var wanted = ProbeRemote();
        if (wanted == pullRequestToolsOn)
        {
            return;
        }

        pullRequestToolsOn = wanted;
        target.ReplaceExternalTools(CollectExternalTools());
    }

    /// <summary>True when the workspace has at least one spec file with an id. Only asked when the orchestration is off.</summary>
    private static bool WorkspaceHasSpecs(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return false;
        }

        try
        {
            return Task.Run(async () => (await new SpecGraph(workspacePath).ListAsync().ConfigureAwait(false)).Count > 0)
                .GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private AgentHost Build()
    {
        // Armed before the sources are read: an edit made while this build runs marks the snapshot stale instead of getting lost.
        StartPromptWatch();
        var handler = services.Approvals?.CreateHandler(Session, this);

        // A retried build (the first resume failed) replaces the handler; the old one must not keep a dead wait alive.
        approvalHandler?.Dispose();
        approvalHandler = handler;
        questionHandler?.Dispose();
        questionHandler = services.Approvals?.CreateQuestionHandler(Session, this);

        // One snapshot per build: the system slot, the agent kinds and the compaction prompt stay fixed for this host.
        var templates = services.PromptTemplates?.Resolve(Session.WorkspacePath)
            ?? PromptTemplateSet.BuiltIn(Prompts.AppPromptTexts.Registry);
        // No template text from the user means the built-in one; which built-in text depends on what the request claims, so the
        // two never contradict each other.
        var systemSlot = templates.Slot(PromptSlotIds.System);
        if (string.IsNullOrWhiteSpace(systemSlot) && services.ClaimsOfficialIdentity is { } claims && !claims())
        {
            systemSlot = BuiltInPrompts.SystemNamed;
        }

        var mainModel = services.AgentDefaults?.Invoke(AgentIds.Main) ?? services.Model;
        var subAgents = CurrentSubAgentLimits();
        var layers = LayerSources.Resolve(systemSlot, AgentRuntimeServices.UserDirectory(), Session.WorkspacePath);
        Volatile.Write(ref builtLayers, layers);

        // One orchestration snapshot per host, like the templates above: the skill index and the routing rule are bytes
        // in the root's cached prefix, so they must not move under a running turn. The product's own skills are the
        // lowest-priority source, so a workspace or user skill of the same name is the one the index lists.
        rootModel = mainModel;
        orchestration = services.OrchestrationEnabled is { } orchestrationEnabled && !orchestrationEnabled()
            ? string.Empty
            : OrchestrationPrompt.Build(DiscoverSkills());
        chatRendering = ChatRenderingPrompt.Build();
        specToolsOn = orchestration.Length > 0 || WorkspaceHasSpecs(Session.WorkspacePath);
        gitToolsOn = !IsGeneralSession();
        // Cleared before the probe: a pairing that finishes while it runs sets the flag again and is re-decided at the next boundary.
        remoteAdded = false;
        pullRequestToolsOn = gitToolsOn && ProbeRemote();
        var compaction = services.CurrentCompaction();
        var defaults = new AgentOptions();
        var artifactLimits = services.ArtifactLimits?.Invoke()
                             ?? (defaults.ArtifactThresholdChars, defaults.ArtifactPreviewChars);
        var toolSegments = BuildTools();
        var built = new AgentHost(
            new AgentHostOptions
            {
                Providers = services.ProviderFactory,
                Tools = toolSegments.All,
                ToolSegments = toolSegments,
                DisabledTools = services.DisabledTools is { } disabledTools ? ToolCatalog.EffectiveDisabled(disabledTools()) : null,
                Layers = layers,
                Policy = Policy,
                ApprovalHandler = handler,
                QuestionHandler = questionHandler,
                Store = services.Store,
                Todos = services.Todos,
                StepCommitter = services.StepCommitter,
                // A General session has no git tools and its repository is shared by every General session, so a review or a
                // step gate there would diff and probe other sessions' changes.
                Reviews = !gitToolsOn || (services.ReviewToolEnabled is { } reviewToolEnabled && !reviewToolEnabled()) ? null : services.Reviews,
                StepChanges = gitToolsOn ? services.StepChanges : null,
                StepGatesEnabled = services.StepReviewRequired,
                ReviewFix = services.ReviewFix,
                ReviewLimits = services.ReviewLimits,
                ReviewConclusions = services.ReviewConclusions,
                StepReviewStore = services.StepReviewStore,
                Artifacts = services.Artifacts,
                SessionMessages = services.SessionMessages,
                // A background child outlives the turn that started it, so its events go to the session's own sink, not only to that
                // turn's channel (which is completed as soon as the turn ends).
                SessionEvents = value => Volatile.Read(ref sessionSink)?.Invoke(value),
                Templates = templates,
                Agents = ResolveAgentDefaults(templates),
                SpawnModels = services.SpawnModels,
                // The model's own budget line: a dynamic suffix, never part of the cached prefix or the history.
                DynamicContext = ContextBudget,
                // set_title: the tool exists in every host this runtime builds, so the tool segment stays byte-stable.
                RenameSession = NameSessionAsync,
                Options = new AgentOptions
                {
                    DefaultModel = mainModel,
                    MaxParallelSubAgents = subAgents.MaxParallel,
                    // The policy belongs to the provider this session is pointed at; "auto" is resolved per request afterwards.
                    CacheTtl = services.PromptCacheTtl?.Invoke(mainModel.ProviderId),
                    // compaction (feature B): thresholds relative to each model's effective window. Later saves reach
                    // this host through ApplyCompaction.
                    Compaction = compaction.ToPolicy(services.ContextWindow, services.MaxOutputTokens, services.WireMaxOutputTokens),
                    ArtifactThresholdChars = artifactLimits.Item1,
                    ArtifactPreviewChars = artifactLimits.Item2,
                    SendToolImagesToModel = services.SendToolImages ?? (static () => true),
                    ModelInputs = services.ModelInputs,
                },
            });
        appliedCompaction = compaction;
        appliedMaxParallel = subAgents.MaxParallel;

        // A reload rebuilds the host for the same session: the kinds and the prompt layers are new, but the sub-agents that
        // are still alive belong to the conversation, not to the host that spawned them (forge-core "子 Agent 复用").
        if (host is not null)
        {
            built.SubAgents.TakeOverFrom(host.SubAgents);
        }

        // From here on a plugin or workflow run of this session can call tools through this session's tool set and handler.
        builtHost = built;
        services.ToolRouter?.Register(SessionId, this);
        return built;
    }

    /// <summary>Raised (on any thread) when <see cref="PromptsStale"/> changes: a prompt source changed, or a reload was requested.</summary>
    public event Action? PromptsStaleChanged;

    /// <summary>
    /// Whether a template, agent, skill or <c>AGENTS.md</c> file changed after this session's agent host took its prompt
    /// snapshot. False before the first turn (nothing was snapshotted yet).
    /// </summary>
    public bool PromptsStale
    {
        get
        {
            lock (gate)
            {
                return promptsStale;
            }
        }
    }

    /// <summary>
    /// The text of one prompt slot for this session's workspace, or null when the host has no template service. Read when the
    /// session sends a message on the user's behalf (continuing a failed turn), so an edited slot is what goes out, exactly as
    /// the quota automation reads its own slot.
    /// </summary>
    public string? PromptText(string slotId) => services.PromptTemplates?.Resolve(Session.WorkspacePath).Slot(slotId);

    /// <summary>
    /// Re-reads every prompt source: from the next turn on the session runs on a fresh agent host (templates, system slot,
    /// layers, agent kinds, skill index) and the root agent is resumed from the store, so the conversation keeps its context.
    /// The prompt cache of the conversation is lost once (the prefix changed). A running turn is not touched; it ends on the
    /// snapshot it started with. Before the first turn there is nothing to reload (the first build reads the files).
    /// </summary>
    public bool HotReloadPrompts()
    {
        lock (gate)
        {
            // A running turn or child keeps its host (its approval and question handlers live there); the stale flag stays so the
            // reload can be applied once the turn has ended.
            if (disposed || host is null || activeRoot is not null || outsideChildren > 0)
            {
                return false;
            }

            reloadPending = true;
            reloadBuilt = false;
            promptsStale = false;
        }

        PromptsStaleChanged?.Invoke();
        return true;
    }

    /// <summary>True once the first turn built a host (before that there is nothing to reload).</summary>
    internal bool HasHost
    {
        get
        {
            lock (gate)
            {
                return !disposed && host is not null;
            }
        }
    }

    /// <summary>False while a turn or an outside-turn child holds the host; the same condition <see cref="HotReloadPrompts"/> refuses on.</summary>
    internal bool CanHotReload
    {
        get
        {
            lock (gate)
            {
                return !disposed && host is not null && activeRoot is null && outsideChildren == 0;
            }
        }
    }

    private void StartPromptWatch()
    {
        var previous = promptWatcher;
        var watcher = new PromptSourceWatcher(AgentRuntimeServices.UserDirectory(), Session.WorkspacePath);
        watcher.Changed += () => OnPromptSourceChanged(watcher);
        promptWatcher = watcher;
        promptsStale = false;
        watcher.Arm();
        if (previous is not null)
        {
            // Disposed off the build path: a callback of the old watcher may be waiting for the gate this runs under.
            _ = Task.Run(previous.Dispose);
        }
    }

    private void OnPromptSourceChanged(PromptSourceWatcher source)
    {
        lock (gate)
        {
            // A replaced watcher is disposed off-thread, so for a moment it can still signal an edit the new snapshot has read.
            if (disposed || !ReferenceEquals(source, promptWatcher) || (reloadPending && !reloadBuilt) || promptsStale)
            {
                return;
            }

            promptsStale = true;
        }

        PromptsStaleChanged?.Invoke();
    }

    /// <summary>
    /// Every agent kind of the template snapshot (built-ins, possibly overridden, then user agents) with its
    /// <c>settings.json</c> defaults substituted for the placeholder <c>ModelSettings("default", ...)</c>. An agent whose file
    /// sets an explicit model keeps it. The substituted <see cref="ModelSettings"/> carries the entry's provider id, so an
    /// agent pinned to another provider is routed there by the host's provider factory. Without the defaults store
    /// the snapshot's definitions are used as they are.
    /// </summary>
    private IReadOnlyList<AgentDefinition> ResolveAgentDefaults(PromptTemplateSet templates)
    {
        if (services.AgentDefaults is not { } resolve)
        {
            return templates.Agents;
        }

        var resolved = new List<AgentDefinition>(templates.Agents.Count);
        foreach (var (id, definition, explicitThinking) in templates.ResolvedAgents)
        {
            // The settings key is the template id (the file name), never derived from the name, so every agent has one.
            if (string.Equals(definition.Model.Model, "default", StringComparison.OrdinalIgnoreCase) &&
                resolve(id) is { } model)
            {
                // A file that sets thinking keeps it; the settings default then only picks the model and provider.
                resolved.Add(definition with { Model = explicitThinking ? model with { Thinking = definition.Model.Thinking } : model });
            }
            else
            {
                resolved.Add(definition);
            }
        }

        return resolved;
    }

    /// <summary>
    /// What this session's tools, MCP servers and prompt sources cost in context, read from the host that was built (the same
    /// snapshot the model is sent); empty until the first turn built the host. The size is the definition text, not a call count.
    /// <para>The catalog those tools are named by is <see cref="ToolCatalog.Describe"/> — the settings page's own list — so the
    /// window and the page can never disagree about what a tool is or where it belongs. Tools the catalog does not know are
    /// left unsized rather than guessed at.</para>
    /// </summary>
    internal ResourceUsage DescribeUsage()
    {
        AgentHost? current;
        lock (gate)
        {
            current = disposed ? null : builtHost;
        }

        if (current is null)
        {
            return ResourceUsage.Empty;
        }

        var (builtIns, external) = current.AllTools;
        var (groups, servers) = ResourceUsageBuilder.Tools(ToolCatalog.Describe(services), [.. builtIns, .. external]);
        return new ResourceUsage(groups, servers, ResourceUsageBuilder.Prompts(Volatile.Read(ref builtLayers), orchestration, chatRendering));
    }

    // ISessionToolScope: what the router reads. The tool set is the live one (built-ins plus the current external tools).
    IApprovalPolicy ISessionToolScope.Policy => Policy;

    IApprovalHandler? ISessionToolScope.ApprovalHandler
    {
        get
        {
            lock (gate)
            {
                return approvalHandler;
            }
        }
    }

    /// <summary>
    /// A plugin or workflow call of a child session reaches this scope through the child's alias; the requester names that
    /// session so its card can sit in its own tab, while the tools and the policy stay this root's.
    /// </summary>
    ValueTask<ApprovalDecision> ISessionToolScope.RequestApprovalAsync(string requesterSessionId, ApprovalRequest request, CancellationToken ct)
    {
        SessionApprovalHandler? handler;
        lock (gate)
        {
            handler = disposed ? null : approvalHandler;
        }

        return handler is null
            ? ValueTask.FromResult(ApprovalDecision.Deny)
            : handler.RequestAsync(request, requesterSessionId, ct);
    }

    // IAgentSessionScope: what this runtime's approval and question handlers read about its user-driven child sessions.
    string? IAgentSessionScope.CurrentChildSessionId => currentUserChild.Value?.SessionId is { Length: > 0 } id ? id : null;

    string? IAgentSessionScope.ChildSessionTitle(string sessionId)
    {
        lock (gate)
        {
            return userChildren.TryGetValue(sessionId, out var title) ? title : null;
        }
    }

    IReadOnlyList<string> IAgentSessionScope.ChildSessionIds
    {
        get
        {
            lock (gate)
            {
                return [.. userChildren.Keys];
            }
        }
    }

    ITool? ISessionToolScope.FindTool(string toolName)
    {
        AgentHost? current;
        lock (gate)
        {
            current = disposed ? null : builtHost;
        }

        return current is not null && current.Tools.TryGet(toolName, out var tool) ? tool : null;
    }

    /// <summary>
    /// The tools that come from outside Core: the extension host's plugin tools, the <c>run_code</c> proxy and, once the
    /// approval wiring exists, the five extension tools, then the git tools. Recomputed on every registration change so a hot reload reaches
    /// the loop before its next model request.
    /// <para>
    /// The <c>run_code</c> proxy gets its own registry, collected in the same pass (wave W14): the extension host's plugin
    /// tools (whole, write and execute ones included, minus names the session already owns) plus every <b>read-only</b> tool
    /// of the workspace, web and MCP families (see <see cref="CodeModeTools"/>). <c>write</c>, <c>edit</c>, <c>bash</c>, the
    /// git and PR tools and the extension authoring tools stay out of that registry entirely, so the script cannot even name
    /// them; an inner call still goes through the session's approval path. The MCP half is the per-server read-only allowlist
    /// (<c>readOnlyTools</c>) restricted to the adapters this turn offers; the MCP search tool is not an adapter and is left
    /// out (a script has no prompt budget to save). The registry also honours the user's tool switches live.
    /// </para>
    /// </summary>
    private ExternalTools CollectExternalTools()
    {
        // Three segments, in this order in the tool array: the always-present tools, the conditional built-ins, then what comes
        // from outside the product. A change in a later segment (an MCP server connecting, a plugin installed) leaves every
        // earlier definition where it was, so the prompt cache only breaks from that position on.
        var fixedTools = new List<ITool>();
        var conditional = new List<ITool>();
        var plugins = services.HostClient?.Tools ?? [];
        IReadOnlyList<ITool> web = [];
        fixedTools.AddRange(services.FileTools);
        fixedTools.Add(skillTool ??= new SkillTool(DiscoverSkills));
        if (services.Providers is { } providerCatalog)
        {
            fixedTools.Add(new ModelsTool(providerCatalog, services.IsModelHidden));
        }
        if (services.HostClient is not null && services.Approvals?.Extensions is { } extensions)
        {
            conditional.AddRange(ExtensionTools.Create(extensions));
        }

        if (services.Git is { } git)
        {
            if (gitToolsOn)
            {
                conditional.AddRange(git.All);
            }
        }

        if (specToolsOn)
        {
            conditional.AddRange(services.SpecTools.All);
        }

        if (pullRequestToolsOn)
        {
            conditional.AddRange(services.PullRequestTools);
        }

        if (services.WebTools is { } webTools)
        {
            web = webTools();
            conditional.AddRange(web);
        }

        // A plugin tool never shadows a tool the product owns: the names above win, as they did when plugins came first in
        // one list and the later duplicate replaced the earlier.
        var tools = new List<ITool>(fixedTools.Count + conditional.Count + plugins.Count + 8);
        tools.AddRange(fixedTools);
        tools.AddRange(conditional);
        var owned = new HashSet<string>(tools.Count, StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            owned.Add(tool.Definition.Name);
        }

        var outside = new List<ITool>();
        if (services.HostClient is not null)
        {
            foreach (var plugin in plugins)
            {
                if (!owned.Contains(plugin.Definition.Name))
                {
                    outside.Add(plugin);
                }
            }
        }

        tools.AddRange(outside);

        // Last, so every tool above keeps its name when an MCP server advertises the same one (AppendMcpTools drops the MCP
        // copy), and because the selection is the only part of this list that changes on its own.
        var mcpTools = new List<ITool>();
        if (services.Mcp is { } mcp)
        {
            var mcpStart = tools.Count;
            AppendMcpTools(tools, mcp);
            for (var index = mcpStart; index < tools.Count; index++)
            {
                outside.Add(tools[index]);
            }

            foreach (var tool in tools)
            {
                if (tool is McpToolAdapter { SideEffect: ToolSideEffect.ReadOnly })
                {
                    mcpTools.Add(tool);
                }
            }
        }

        // The script surface is decided in one place, from the same sources this list is built from (see CodeModeTools).
        var scriptTools = CodeModeTools.Collect(plugins, services.FileTools, web, mcpTools, ReservedScriptNames(web));

        // A script needs something to call: with nothing to fan out over, run_code would be a prompt line that can only
        // compute. Its own surface is not part of the loop's tools, only the proxy holds it.
        CodeModeToolRegistry? surface = null;
        if (services.HostClient is { } hostClient && scriptTools.Count > 0)
        {
            surface = new CodeModeToolRegistry(scriptTools, CurrentCodeMode, services.DisabledTools);
            conditional.Add(hostClient.CreateRunCodeTool(surface, RunCodeOptions()));
        }

        LogCodeModeSurface(scriptTools, surface);
        return new ExternalTools(fixedTools, conditional, outside);
    }

    /// <summary>The last surface diagnostic written, so a republish that changes nothing stays silent.</summary>
    private string? codeModeDiagnostic;

    /// <summary>
    /// Why <c>run_code</c> is, or is not, in this session's tool set — on one line, once per change of the answer. The three
    /// layers that can remove it fail quietly: a JIT layout that cannot be located leaves <see cref="services.HostClient"/>
    /// null, an empty candidate set drops the proxy, and a stored tool switch removes individual callables. Without this line
    /// all three read as "the model never chose it", which is the one conclusion that must not be reached by accident.
    /// </summary>
    private void LogCodeModeSurface(IReadOnlyList<ITool> candidates, CodeModeToolRegistry? surface)
    {
        var hidden = new List<string>();
        if (surface is not null)
        {
            foreach (var candidate in candidates)
            {
                if (!surface.TryGet(candidate.Definition.Name, out _))
                {
                    hidden.Add(candidate.Definition.Name);
                }
            }
        }

        var text = "codemode host=" + (services.HostClient is null ? "absent" : "ready")
            + " run_code=" + (surface is null ? "absent" : "registered")
            + " candidates=" + candidates.Count.ToString(CultureInfo.InvariantCulture)
            + " callable=" + (surface?.Tools.Count ?? 0).ToString(CultureInfo.InvariantCulture)
            + " hidden=" + (hidden.Count == 0 ? "none" : string.Join('/', hidden));
        if (string.Equals(text, codeModeDiagnostic, StringComparison.Ordinal))
        {
            return;
        }

        codeModeDiagnostic = text;
        AppLogs.Session.ZLogInformation($"{text}");
    }

    /// <summary>
    /// The names a plugin tool may not take on the script surface: every name the session resolves to something other than
    /// a plugin tool (Core built-ins, the approval policy's rules, the file, git, PR, web and extension authoring tools).
    /// In the session's registry such a name belongs to the built-in; on the script surface the plugin's tool would be
    /// executed under the built-in's approval rule, so the plugin copy is left out there.
    /// </summary>
    private HashSet<string> ReservedScriptNames(IReadOnlyList<ITool> web)
    {
        var reserved = new HashSet<string>(SessionRules.Keys, StringComparer.Ordinal);
        reserved.UnionWith(ToolCatalog.CoreNames);
        reserved.UnionWith([ExtensionToolNames.List, ExtensionToolNames.Create, ExtensionToolNames.Update, ExtensionToolNames.Disable, ExtensionToolNames.Delete]);
        AddNames(reserved, services.FileTools);
        reserved.Add(SkillTool.Name);
        reserved.Add(ModelsTool.Name);
        AddNames(reserved, services.PullRequestTools);
        AddNames(reserved, web);
        if (services.Git is { } git)
        {
            AddNames(reserved, git.All);
        }

        return reserved;

        static void AddNames(HashSet<string> into, IReadOnlyList<ITool> tools)
        {
            foreach (var tool in tools)
            {
                into.Add(tool.Definition.Name);
            }
        }
    }

    /// <summary>The four script dials of the <c>tools</c> section right now; the defaults when the runtime has no store.</summary>
    private CodeModeSettings CurrentCodeMode() => services.CodeMode?.Invoke() ?? CodeModeSettings.Default;

    /// <summary>
    /// The <c>run_code</c> options: the script budgets read live from the <c>tools</c> section, so a settings change reaches
    /// the next turn's description (it states the numbers) and every later script without a new session host. The two
    /// timeouts keep the proxy's own defaults; they are not user-facing dials.
    /// </summary>
    private RunCodeProxyOptions RunCodeOptions()
    {
        if (services.CodeMode is null)
        {
            return new RunCodeProxyOptions();
        }

        return new RunCodeProxyOptions { ExecutionProvider = () => CurrentCodeMode().ToExecution() };
    }

    /// <summary>
    /// A plugin was registered or unregistered (or an approved extension went live): hand the new external tool surface to
    /// the running loop. A host event must never take the app down, so a failure is traced and the next change retries.
    /// </summary>
    private void OnToolsChanged()
    {
        AgentHost? target;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            target = host;
        }

        if (target is null)
        {
            return;
        }

        try
        {
            target.ReplaceExternalTools(CollectExternalTools());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Session.ZLogWarning($"the external tool surface could not be replaced: {exception.Message}");
        }
    }

    /// <summary>The tool surface one session runs with (see <see cref="CollectExternalTools"/>).</summary>
    internal ExternalTools BuildTools() => CollectExternalTools();

    /// <summary>
    /// The host's currently published tool registry: a new instance exactly when a new tool set was published, so its identity
    /// can be compared across turns to prove that the prompt cache was (or was not) invalidated. Null before the first turn.
    /// </summary>
    internal ToolRegistry? PublishedTools
    {
        get
        {
            lock (gate)
            {
                return disposed ? null : builtHost?.Tools;
            }
        }
    }

    public void Dispose()
    {
        SessionApprovalHandler? handler;
        ExtensionHostClient? client;
        SessionQuestionHandler? questionDisposable;
        string[] aliases;
        // A spawned child outlives the turn that started it, so dropping the host is not enough to end it: it is asked to
        // stop here, before the store and the tool router this runtime shares are released under it.
        if (CloseBackgroundChildren("the session is closing") > 0)
        {
            AppLogs.Session.ZLogInformation($"{SessionId}: background sub-agents stopped with the runtime");
        }

        lock (gate)
        {
            disposed = true;
            RepositoryRemotes.Added -= OnRemoteAdded;
            aliases = [.. childSessions.Values];
            childSessions.Clear();
            builtHost = null;
            host = null;
            root = null;
            promptWatcher?.Dispose();
            promptWatcher = null;
            handler = approvalHandler;
            approvalHandler = null;
            questionDisposable = questionHandler;
            questionHandler = null;
            client = listenedClient;
            listenedClient = null;
        }

        if (services.CompactionChanges is { } changes)
        {
            changes.Changed -= OnCompactionChanged;
        }

        services.ToolRouter?.Unregister(SessionId, this);
        foreach (var alias in aliases)
        {
            services.ToolRouter?.Unregister(alias, this);
        }

        if (client is not null)
        {
            client.ToolsChanged -= OnToolsChanged;
        }

        // Releasing the runtime withdraws this session's approval cards; every waiting loop then sees a denial.
        handler?.Dispose();
        questionDisposable?.Dispose();
    }

    /// <summary>
    /// Tool rules that need no approval card: the session checklist (the app's own state, not a workspace file) and the
    /// extension tools that only stage a draft or switch a plugin off - their real gate is the plugin approval card the
    /// authoring service puts in front of the user. <c>extension_delete</c> keeps asking: that card is the deletion
    /// confirmation. Every other call goes through <see cref="DefaultApprovalPolicy"/>, which allows a read-only call and any
    /// write or execution that stays inside the workspace.
    /// <para>The git, PR and web tools are deliberately absent: they name no path, so they are inside the workspace by the
    /// policy's reading and need no card. <c>pr_screenshots</c> declares <see cref="ToolSideEffect.Execute"/> because it runs a
    /// command the repository declares, and that command's paths are judged like any other execution.</para>
    /// </summary>
    private static readonly Dictionary<string, ApprovalMode> SessionRules = CreateRules();

    /// <summary>
    /// The policy this session's agent loop and its plugin/workflow calls evaluate: the rules above, then the workspace
    /// boundary and the user's global mode. Built once per runtime; the mode is read from the services on every evaluation, so
    /// the settings page reaches a running session without a restart.
    /// </summary>
    private IApprovalPolicy? policy;

    private IApprovalPolicy Policy => policy ??= CreatePolicy(services.ToolPermissionMode);

    /// <summary>
    /// The session policy for a given mode source (null means the workspace mode). Built in one place so the rules the app
    /// really runs are the rules a test can pin - a hand-written rule table in a test would pass while the session's own table
    /// lost a confirmation.
    /// </summary>
    internal static IApprovalPolicy CreatePolicy(Func<ToolPermissionMode>? mode) =>
        new DefaultApprovalPolicy { ToolRules = SessionRules, Mode = mode };

    private static Dictionary<string, ApprovalMode> CreateRules()
    {
        var rules = new Dictionary<string, ApprovalMode>(StringComparer.Ordinal)
        {
            [TodoTool.Name] = ApprovalMode.Allow,
            [AskUserTool.Name] = ApprovalMode.Allow,

            // Naming the session is the app's own tab label, not workspace state: it writes no file, moves no branch, and
            // the model is asked to do it once, so a card here would be an interruption with nothing to decide.
            [SetTitleTool.Name] = ApprovalMode.Allow,

            // Delivering a session message is what the user's own @ mention authorized: the approval card belongs to the
            // target's tools, which go through this session's policy like every other call of that turn (forge-core
            // "session messaging": the delivery itself raises no approval card).
            [SessionReplies.ToolName] = ApprovalMode.Allow,

            // The deletion confirmation, and the only extension tool that keeps one: it names no path, so without this rule the
            // workspace boundary would let it through in both modes, and a plugin version's removal is irreversible.
            [ExtensionToolNames.Delete] = ApprovalMode.Ask,
        };
        foreach (var name in ExtensionToolNames.AllowedWithoutAsking)
        {
            rules[name] = ApprovalMode.Allow;
        }

        return rules;
    }

}

/// <summary>Stands in for a provider when none is configured, so a turn fails with one clear sentence instead of a null.</summary>

/// <summary>The root agent instance's cumulative usage and model-streaming time, with the instance they belong to (a rebuilt root is a different one, counting from zero) and the seq of the last message that instance persisted — the turn's exact stream position, 0 when nothing did.</summary>
internal readonly record struct RootCounters(TokenUsage Usage, TimeSpan Stream, object? Instance, long LastSeq = 0)
{
    public static RootCounters None { get; } = new(TokenUsage.Empty, TimeSpan.Zero, null);
}
