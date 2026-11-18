using System.Diagnostics;
using Forge.Abstractions;
using Forge.App.Approval;
using Forge.App.Git;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Forge.Core.Prompts.Templates;
using Forge.Core.Providers;
using Forge.Core.Tools;
using Forge.HostClient;
using Forge.Mcp;
using Forge.Workspace;
using Forge.App.Logging;
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

    /// <summary>Where the step review ledger is persisted (handed to <c>AgentHostOptions.StepReviewStore</c>). Null: in memory only.</summary>
    public IStepReviewStore? StepReviewStore { get; set; }

    /// <summary>
    /// Where oversized tool output is stored (handed to <c>AgentHostOptions.Artifacts</c>, which also adds the tool that
    /// reads it back). Null when the runtime was assembled without the composition root: output then stays inline.
    /// </summary>
    public IArtifactStore? Artifacts { get; set; }

    /// <summary>
    /// The pull request tools (<c>pr_*</c>, <c>pr_screenshots</c>, <c>review_comments</c>, <c>review_comment_resolve</c>),
    /// registered after the git tools. Stateless across sessions like <see cref="Git"/>; empty without the composition root. Read-only ones
    /// pass through <see cref="DefaultApprovalPolicy.AutoAllowReadOnly"/>, the write ones always ask, and
    /// <c>pr_screenshots</c> – which runs a command from the repository and then pushes and edits the description – asks with
    /// its <see cref="ToolSideEffect.Execute"/>.
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
    /// ones pass through <see cref="DefaultApprovalPolicy.AutoAllowReadOnly"/>; <c>write</c>/<c>edit</c> ask and
    /// <c>bash</c> asks with <see cref="ToolSideEffect.Execute"/>. The read-only four are also the file half of a
    /// <c>run_code</c> script's tool surface (wave W14: read-only is the rule, so nothing a script reaches ever needed a
    /// card the model's own call would have raised); <c>write</c>, <c>edit</c> and <c>bash</c> stay out of it entirely.
    /// </summary>
    public IReadOnlyList<ITool> FileTools { get; set; } = [];

    // --- web tools (web_fetch / web_search) ---
    /// <summary>
    /// Produces the web tools when a session assembles its tool surface: <c>web_fetch</c> always, <c>web_search</c> only
    /// while a backend is configured with its key or URL. Called again at the start of every turn: when the names it
    /// returns differ from what the session's host holds, the external set is republished (otherwise nothing changes,
    /// so the prompt cache stays). Should be cheap and return shared instances. Null without the composition root: no web tools. Both
    /// always ask for approval (see <c>CreateRules</c>): network egress can carry data out.
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
internal sealed class SessionRuntime : IDisposable, ISessionToolScope
{
    private readonly AgentRuntimeServices services;
    private readonly Lock gate = new();
    private AgentHost? host;
    private AgentInstance? root;
    private AgentInstance? activeRoot;
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

    public SessionRuntime(AgentRuntimeServices services, SessionInfo session)
    {
        this.services = services;
        Session = session;
        rootModel = services.Model;
        if (services.CompactionChanges is { } changes)
        {
            changes.Changed += OnCompactionChanged;
        }

        RepositoryRemotes.Added += OnRemoteAdded;
    }

    public SessionInfo Session { get; }

    public string SessionId => Session.Id;

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
    /// Runs one user turn whose message is <paramref name="userText"/> followed by <paramref name="attachments"/> (images,
    /// files, inlined text files). The text may be empty only when at least one attachment is present.
    /// </summary>
    public async Task RunTurnAsync(
        string userText, IReadOnlyList<ContentBlock> attachments, Action<AgentEvent> sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userText);
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count == 0)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userText);
        }

        ArgumentNullException.ThrowIfNull(sink);
        var message = UserMessage(userText, attachments);

        // What this request is about decides which MCP tools are offered (see RefreshMcpTools); it is read when the tool
        // surface is refreshed at the start of the turn and never stored.
        Volatile.Write(ref mcpFocus, userText);

        var (agentHost, agent) = await Task.Run(() => ResolveAsync(), ct).ConfigureAwait(false);
        lock (gate)
        {
            activeRoot = agent;
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
    /// Hands a user message to the running root turn; the loop delivers it at the next tool-batch boundary. False when no
    /// turn is running, in which case the caller should queue the message instead.
    /// </summary>
    public bool TryInterject(string text) => TryInterject(text, []);

    /// <summary>
    /// Hands a user message with attachments (images, files, inlined text files) to the running root turn, delivered with
    /// them at the next tool-batch boundary. The text may be empty only when an attachment is present. False when no turn
    /// is running.
    /// </summary>
    public bool TryInterject(string text, IReadOnlyList<ContentBlock> attachments)
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
                return false;
            }

            activeRoot.Interject(content);
            return true;
        }
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

    private async Task<(AgentHost Host, AgentInstance Agent)> ResolveAsync()
    {
        AgentHost built;
        AgentInstance agent;
        AgentDefinition? definition;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (host is not null && root is not null && !rootStale && !reloadPending)
            {
                ApplyDisabledTools(host);
                ApplySubAgentLimits(host);
                RefreshWebTools(host);
                RefreshPullRequestTools(host);
                RefreshMcpTools(host);
                ApplyCompaction(host);
                return (host, root);
            }

            // A changed root definition reuses the host (its tools, approval handler and router registration) and only
            // rebuilds the root agent from the store.
            built = host is not null && !reloadPending ? host : Build();
            definition = rootDefinition;
        }

        agent = await built.ResumeRootAsync(SessionId, Session.WorkspacePath, CancellationToken.None, WithOrchestration(definition)).ConfigureAwait(false);
        lock (gate)
        {
            host = built;
            reloadPending = false;
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

            return (host, agent);
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

        target.SetCompactionPolicy(settings.ToPolicy(services.ContextWindow, services.MaxOutputTokens));
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

    private readonly SpecTools specTools = new();

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
        var handler = services.Approvals?.CreateHandler(Session);

        // A retried build (the first resume failed) replaces the handler; the old one must not keep a dead wait alive.
        approvalHandler?.Dispose();
        approvalHandler = handler;
        questionHandler?.Dispose();
        questionHandler = services.Approvals?.CreateQuestionHandler(Session);

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
        pullRequestToolsOn = gitToolsOn && ProbeRemote();
        remoteAdded = false;
        var compaction = services.CurrentCompaction();
        var toolSegments = BuildTools();
        var built = new AgentHost(
            new AgentHostOptions
            {
                Providers = services.ProviderFactory,
                Tools = toolSegments.All,
                ToolSegments = toolSegments,
                DisabledTools = services.DisabledTools is { } disabledTools ? ToolCatalog.EffectiveDisabled(disabledTools()) : null,
                Layers = layers,
                Policy = SessionPolicy,
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
                Templates = templates,
                Agents = ResolveAgentDefaults(templates),
                Options = new AgentOptions
                {
                    DefaultModel = mainModel,
                    MaxParallelSubAgents = subAgents.MaxParallel,
                    // The policy belongs to the provider this session is pointed at; "auto" is resolved per request afterwards.
                    CacheTtl = services.PromptCacheTtl?.Invoke(mainModel.ProviderId),
                    // compaction (feature B): thresholds relative to each model's effective window. Later saves reach
                    // this host through ApplyCompaction.
                    Compaction = compaction.ToPolicy(services.ContextWindow, services.MaxOutputTokens),
                    SendToolImagesToModel = services.SendToolImages ?? (static () => true),
                    ModelInputs = services.ModelInputs,
                },
            });
        appliedCompaction = compaction;
        appliedMaxParallel = subAgents.MaxParallel;

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
            promptsStale = false;
        }

        PromptsStaleChanged?.Invoke();
        return true;
    }

    private void StartPromptWatch()
    {
        var previous = promptWatcher;
        var watcher = new PromptSourceWatcher(AgentRuntimeServices.UserDirectory(), Session.WorkspacePath);
        watcher.Changed += OnPromptSourceChanged;
        promptWatcher = watcher;
        promptsStale = false;
        watcher.Arm();
        if (previous is not null)
        {
            // Disposed off the build path: a callback of the old watcher may be waiting for the gate this runs under.
            _ = Task.Run(previous.Dispose);
        }
    }

    private void OnPromptSourceChanged()
    {
        lock (gate)
        {
            if (disposed || reloadPending || promptsStale)
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
    /// Names of the tools this session can call right now, sorted ordinally. Empty until the first turn built the agent
    /// host: building it here would register the scope and subscribe to the extension host as a side effect of a readout.
    /// </summary>
    internal IReadOnlyList<string> ToolNames()
    {
        lock (gate)
        {
            if (disposed || builtHost is null)
            {
                return [];
            }

            var definitions = builtHost.Tools.Definitions;
            var names = new string[definitions.Count];
            for (var i = 0; i < definitions.Count; i++)
            {
                names[i] = definitions[i].Name;
            }

            return names;
        }
    }

    /// <summary>
    /// Every tool this session registers, switched-off ones included, with where it comes from; the Tools settings section
    /// groups them and offers a switch on each built-in. Empty until the first turn built the agent host.
    /// </summary>
    internal IReadOnlyList<ToolDescriptor> DescribeTools()
    {
        AgentHost? current;
        lock (gate)
        {
            current = disposed ? null : builtHost;
        }

        if (current is null)
        {
            return [];
        }

        var (builtIns, external) = current.AllTools;
        var descriptors = new List<ToolDescriptor>(builtIns.Count + 32);
        foreach (var tool in builtIns)
        {
            descriptors.Add(ToolCatalog.DescribeCore(tool.Definition.Name));
        }

        // Classified by the list each tool came from rather than by name, so a plugin cannot pose as a built-in. Collected
        // afresh (not read from the host) because the host keeps no record of the source list.
        DescribeExternal(descriptors);

        // The web tools are built per host (web_search only while a backend is configured), so they are read from what the
        // host actually holds instead of calling the factory again.
        foreach (var tool in external)
        {
            if (IsWebTool(tool.Definition.Name))
            {
                descriptors.Add(new ToolDescriptor(tool.Definition.Name, ToolOrigin.BuiltIn, ToolGroups.Web));
            }
        }

        return descriptors;
    }

    private void DescribeExternal(List<ToolDescriptor> into)
    {
        if (services.HostClient is { } client)
        {
            foreach (var tool in client.Tools)
            {
                into.Add(new ToolDescriptor(tool.Definition.Name, ToolOrigin.Extension, ToolGroups.Extensions, CanDisable: false));
            }

            // The same decision CollectExternalTools made when it published the tools: run_code exists exactly while a
            // script has something to call, which is not the same as "a plugin registered tools".
            if (Volatile.Read(ref scriptSurface) is not null)
            {
                into.Add(new ToolDescriptor(ToolCatalog.RunCode, ToolOrigin.BuiltIn, ToolGroups.Extensions));
            }

            if (services.Approvals?.Extensions is not null)
            {
                foreach (var name in (string[])[ExtensionToolNames.List, ExtensionToolNames.Create, ExtensionToolNames.Update, ExtensionToolNames.Disable, ExtensionToolNames.Delete])
                {
                    into.Add(new ToolDescriptor(name, ToolOrigin.BuiltIn, ToolGroups.Extensions));
                }
            }
        }

        if (services.Git is { } git)
        {
            foreach (var tool in git.All)
            {
                into.Add(new ToolDescriptor(tool.Definition.Name, ToolOrigin.BuiltIn, ToolGroups.Git));
            }
        }

        foreach (var tool in services.FileTools)
        {
            into.Add(new ToolDescriptor(tool.Definition.Name, ToolOrigin.BuiltIn, ToolGroups.Files));
        }

        into.Add(new ToolDescriptor(SkillTool.Name, ToolOrigin.BuiltIn, ToolGroups.Session));

        foreach (var tool in services.PullRequestTools)
        {
            into.Add(new ToolDescriptor(tool.Definition.Name, ToolOrigin.BuiltIn, ToolGroups.PullRequests));
        }

        // Every ready MCP tool, not only the ones this turn offers: the Tools section is where a user looks to see what a server
        // provides, and a trimmed list there would read as a missing tool.
        if (services.Mcp is { } mcp)
        {
            foreach (var adapter in mcp.Manager.Tools)
            {
                into.Add(new ToolDescriptor(
                    adapter.ExposedName,
                    ToolOrigin.Mcp,
                    adapter.ServerName,
                    adapter.ServerName,
                    CanDisable: false));
            }
        }
    }

    // ISessionToolScope: what the router reads. The tool set is the live one (built-ins plus the current external tools).
    IApprovalPolicy ISessionToolScope.Policy => SessionPolicy;

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
            conditional.AddRange(specTools.All);
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
        // compute. Its own surface is not part of the loop's tools, so this list is also the only writer of it. The
        // outcome is kept (scriptSurface) so the Tools settings page lists run_code by this same decision.
        CodeModeToolRegistry? surface = null;
        if (services.HostClient is { } hostClient && scriptTools.Count > 0)
        {
            surface = new CodeModeToolRegistry(scriptTools, CurrentCodeMode, services.DisabledTools);
            conditional.Add(hostClient.CreateRunCodeTool(surface, RunCodeOptions()));
        }

        Volatile.Write(ref scriptSurface, surface);
        return new ExternalTools(fixedTools, conditional, outside);
    }

    /// <summary>The script registry of the last collection, or null when that collection produced no <c>run_code</c>.</summary>
    private CodeModeToolRegistry? scriptSurface;

    /// <summary>The registry the live <c>run_code</c> resolves inner calls against; null while there is no <c>run_code</c>.</summary>
    internal IToolRegistry? ScriptSurface => Volatile.Read(ref scriptSurface);

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
    /// confirmation. Every other write or execution goes to the session's <see cref="SessionApprovalHandler"/>.
    /// <para>The git tools are deliberately absent: the read-only ones pass through
    /// <see cref="DefaultApprovalPolicy.AutoAllowReadOnly"/>, the write ones (git_branch, git_sync, pr_create, pr_sync, pr_edit, pr_comment_reply, pr_comment_resolve, review_comment_resolve) always ask, and so does
    /// <c>pr_screenshots</c>: its side effect is <see cref="ToolSideEffect.Execute"/>, because it runs a command the repository
    /// declares, and the push and description edit that follow are the write half of the same card.</para>
    /// </summary>
    private static readonly Dictionary<string, ApprovalMode> SessionRules = CreateRules();

    private static readonly IApprovalPolicy SessionPolicy = new DefaultApprovalPolicy { ToolRules = SessionRules };

    private static Dictionary<string, ApprovalMode> CreateRules()
    {
        var rules = new Dictionary<string, ApprovalMode>(StringComparer.Ordinal)
        {
            [TodoTool.Name] = ApprovalMode.Allow,
            [AskUserTool.Name] = ApprovalMode.Allow,

            // Read-only, but a prompt-injected URL or query can carry workspace content out, so these always ask.
            [Forge.Core.Tools.Web.WebFetchTool.Name] = ApprovalMode.Ask,
            [Forge.Core.Tools.Web.WebSearchTool.Name] = ApprovalMode.Ask,
        };
        foreach (var name in ExtensionToolNames.AllowedWithoutAsking)
        {
            rules[name] = ApprovalMode.Allow;
        }

        return rules;
    }

}

/// <summary>Stands in for a provider when none is configured, so a turn fails with one clear sentence instead of a null.</summary>
