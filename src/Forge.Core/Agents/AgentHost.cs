using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Forge.Abstractions;
using Forge.Core.Providers;
using Forge.Core.Prompts;
using Forge.Core.Tools;

namespace Forge.Core.Agents;

/// <summary>
/// Composition of a host: which providers to use per model, which tools and agent kinds exist, how
/// prompts are layered, and where sessions and artifacts live. Everything except
/// <see cref="Providers"/> and <see cref="Layers"/> is optional.
/// </summary>
public sealed record AgentHostOptions
{
    public required Func<ModelSettings, IChatProvider> Providers { get; init; }

    public required IEnumerable<ITool> Tools { get; init; }

    /// <summary>
    /// The same tools grouped into the three tool-array segments (see <see cref="ExternalTools"/>). When set it is what the host
    /// publishes and <see cref="Tools"/> is ignored; when null every tool of <see cref="Tools"/> lands in the conditional segment.
    /// </summary>
    public ExternalTools? ToolSegments { get; init; }

    /// <summary>Extra agent kinds layered on top of <see cref="BuiltInAgents"/>; same name replaces the built-in.</summary>
    public IEnumerable<AgentDefinition>? Agents { get; init; }

    /// <summary>
    /// Resolves <c>spawn_agent</c>'s optional per-spawn model override (<c>model</c>, <c>provider</c>, <c>thinking</c>).
    /// The model catalogue belongs to the app, so Core carries the request and the app answers it; a refusal is an error
    /// result the model can act on. Null = the tool carries no override parameters at all, which is what a host without
    /// an app behind it (a script, a test) gets, and its definition bytes stay as they were.
    /// </summary>
    public Func<SpawnModelRequest, SpawnModelChoice>? SpawnModels { get; init; }

    public required PromptLayers Layers { get; init; }

    public IPromptComposer Composer { get; init; } = new PromptComposer();

    public IApprovalPolicy Policy { get; init; } = new DefaultApprovalPolicy();

    public IApprovalHandler? ApprovalHandler { get; init; }

    /// <summary>The UI side of <c>ask_user</c>; the tool is registered only when this is set.</summary>
    public IUserQuestionHandler? QuestionHandler { get; init; }

    public ISessionStore? Store { get; init; }

    public IArtifactStore? Artifacts { get; init; }

    /// <summary>Registers the <c>todo</c> tool when set; the same store backs the TODO panel.</summary>
    public ITodoStore? Todos { get; init; }

    /// <summary>
    /// Commits a checklist step when the <c>todo</c> tool marks it done (only used together with <see cref="Todos"/>).
    /// Null leaves step commits to the host application, such as the TODO panel.
    /// </summary>
    public IStepCommitter? StepCommitter { get; init; }

    /// <summary>
    /// The git evidence the step completion gate reads (Core has no git, so the app implements this over its own git layer).
    /// Null = no gate: without a probe the <c>todo</c> tool lets every step be marked done. Only used together with
    /// <see cref="Todos"/> and <see cref="Reviews"/>: a host that cannot run a review cannot earn a verdict either.
    /// </summary>
    public IStepChangeProbe? StepChanges { get; init; }

    /// <summary>
    /// Reads the <c>stepReviewRequired</c> preference before every attempt to mark a step done, so a settings change
    /// applies to the next attempt. Null means the gate is always on when <see cref="StepChanges"/> is set.
    /// </summary>
    public Func<bool>? StepGatesEnabled { get; init; }

    /// <summary>The auto-fix loop settings <c>request_review</c> reads on every result; null = defaults. Only used together with <see cref="Reviews"/>.</summary>
    public Func<ReviewFixPolicy>? ReviewFix { get; init; }

    /// <summary>Registers the <c>request_review</c> tool when set; the app implements it over its git layer (Core has no git).</summary>
    public IReviewChangeSetSource? Reviews { get; init; }

    /// <summary>
    /// How much of a change set the review package carries, read when each review is built so a settings change applies to
    /// the next one. Null means <see cref="ReviewPackageLimits.Default"/>. Only used together with <see cref="Reviews"/>.
    /// </summary>
    public Func<ReviewPackageLimits>? ReviewLimits { get; init; }

    /// <summary>
    /// Where <c>request_review</c> records each finished review (wave W17b); the app writes the rows into <c>reviews.db</c>.
    /// Null (no store) records nothing and changes no tool result. Only used together with <see cref="Reviews"/>.
    /// </summary>
    public IReviewConclusionStore? ReviewConclusions { get; init; }

    /// <summary>
    /// Where the step review ledger keeps its verdicts across restarts; the app writes them into <c>reviews.db</c>. Null keeps
    /// them in memory only (a restarted host asks for new reviews).
    /// </summary>
    public IStepReviewStore? StepReviewStore { get; init; }

    /// <summary>
    /// The prompt templates this host was built from (<see cref="Prompts.Templates.PromptTemplateStore.Resolve"/>). The
    /// default compactor takes its summarizer prompt from the <c>compaction.summarizer</c> slot. Null = compiled-in texts.
    /// The system slot is not read from here: it is <see cref="Layers"/>' <see cref="PromptLayers.System"/>.
    /// </summary>
    public Prompts.Templates.PromptTemplateSet? Templates { get; init; }

    /// <summary>Overrides the default <see cref="SummaryCompactor"/> (used when a store is configured).</summary>
    public IContextCompactor? Compactor { get; init; }

    public ITokenCounter TokenCounter { get; init; } = new ApproximateTokenCounter();

    public AgentOptions Options { get; init; } = new();

    /// <summary>
    /// Per-turn text sent as one extra trailing user message after the stable prefix and the persisted
    /// history, such as the current time or workspace state; it is never written to history or to the store.
    /// The argument is the agent id, because the text is per instance: a sub-agent has its own model, window
    /// and budget, and must not be handed the root's numbers.
    /// </summary>
    public Func<string, string?>? DynamicContext { get; init; }

    /// <summary>Registers read_artifact, search_history and read_history when their store is available.</summary>
    public bool IncludeRetrievalTools { get; init; } = true;

    /// <summary>
    /// Writes a name the model asked for through <c>set_title</c> and returns whether it is now the session's title (false
    /// when a person, or an earlier call, already named it). Null - the default - does not offer the tool at all, which is
    /// what a host without a session store wants: the call would have nowhere to land.
    /// </summary>
    public Func<string, CancellationToken, ValueTask<bool>>? RenameSession { get; init; }

    /// <summary>
    /// Tool names switched off from the start (built-in or external); <see cref="AgentHost.SetDisabledTools"/> changes the
    /// set later. A disabled tool is neither offered to the model nor callable through the host.
    /// </summary>
    public IEnumerable<string>? DisabledTools { get; init; }

    /// <summary>
    /// Receives the usage of each instance's first model request in this process (section 8.3.5). Null writes
    /// <see cref="FirstRequestCacheReport.ToString"/> to <see cref="System.Diagnostics.Trace"/>, the host's diagnostics channel.
    /// </summary>
    public Action<FirstRequestCacheReport>? CacheDiagnostics { get; init; }

    /// <summary>
    /// The app's side of session-to-session delivery (forge-core "授权通信"): running a root session's turn, the checkpoint
    /// numbering of a user session's delivered turn, the change line of a turn, and the receipt's way back into a transcript.
    /// Core cannot do any of those itself, so <c>message_session</c> reaches them through this seam. Read at delivery time
    /// (the window installs it after the host settings are built); null leaves the tool answering that nothing is connected,
    /// which is what a host without an app behind it - a script or a test - looks like.
    /// </summary>
    public Func<ISessionMessageEndpoint?>? SessionMessages { get; init; }
}

/// <summary>
/// Facade over one agent runtime: builds the loop, the tool set and the sub-agent scheduler, and exposes
/// a run as one interleaved event stream of the root and every sub-agent it spawns.
/// </summary>
public sealed class AgentHost
{
    private readonly AgentOptions options;
    private readonly AgentCatalog catalog;
    private readonly PromptLayers layers;
    private readonly IPromptComposer composer;
    private readonly SubAgentScheduler scheduler;
    private readonly ToolSurface toolSurface;
    private readonly AgentLoop loop;
    private readonly ISessionStore? store;
    private readonly SessionMessenger messenger;

    /// <summary>
    /// The root session this host belongs to, recorded when the root agent is created or resumed: user-driven sessions
    /// start under it, and their start events name it as the parent. One host carries one main session.
    /// </summary>
    private RootSession? rootSession;

    /// <summary>
    /// The root instance itself, kept so a message that arrives outside a turn (a receipt) can join the history the next
    /// turn sends. Null until the root is created or resumed.
    /// </summary>
    private AgentInstance? rootInstance;

    public AgentHost(AgentHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Options.Compaction.Validate();
        this.options = options.Options;
        catalog = new AgentCatalog(options.Agents);
        layers = options.Layers;
        composer = options.Composer;
        store = options.Store;

        var builtIns = new List<ITool>();
        if (options.IncludeRetrievalTools)
        {
            if (options.Store is { } store)
            {
                builtIns.Add(new SearchHistoryTool(store));
                builtIns.Add(new ReadHistoryTool(store));
            }

            if (options.Artifacts is { } artifacts)
            {
                builtIns.Add(new ReadArtifactTool(artifacts));
            }
        }

        StepReviewLedger? stepReviews = null;
        if (options.Todos is { } todos)
        {
            // One ledger per host: request_review writes a step's verdict into it and the gate reads it back, so both tools
            // must hold the same instance. Without a store it is in memory only, so a restarted host asks for new reviews.
            stepReviews = new StepReviewLedger(options.StepReviewStore);
            // The gate only exists where its own evidence can be obtained: without request_review there is no way to earn
            // an approving verdict, so gating there would make every code step impossible to finish rather than safer.
            var gate = options.Reviews is null
                ? new StepGate(stepReviews)
                : new StepGate(stepReviews, options.StepChanges, options.StepGatesEnabled);
            builtIns.Add(new TodoTool(todos, options.StepCommitter, gate));
        }

        if (options.QuestionHandler is { } questionHandler)
        {
            builtIns.Add(new AskUserTool(questionHandler));
        }

        if (options.RenameSession is { } renameSession)
        {
            builtIns.Add(new SetTitleTool(renameSession));
        }

        scheduler = new SubAgentScheduler(catalog, layers, composer, options.Store, this.options);
        builtIns.Add(new SpawnAgentTool(scheduler, catalog.All, options.SpawnModels));
        builtIns.Add(new SubAgentSteerTool(scheduler.ModelControl));
        builtIns.Add(new SubAgentFollowUpTool(scheduler.ModelControl));
        builtIns.Add(new SubAgentStopTool(scheduler.ModelControl));

        // The one channel between a root and the sessions the user drove under it: its grants, its dispatch queues and its
        // receipts. The tool is part of every host's fixed segment (a constant cost in the cached prefix) and answers by
        // itself for a host whose app seam is missing.
        messenger = new SessionMessenger(
            () => Volatile.Read(ref rootSession) is { } root ? new SessionAnchor(root.SessionId, root.WorkingDirectory) : null,
            scheduler,
            options.Store,
            options.Artifacts,
            options.SessionMessages ?? (static () => null),
            RunUserChildTurnAsync);
        builtIns.Add(new MessageSessionTool(messenger));

        if (options.Reviews is { } reviews)
        {
            builtIns.Add(new RequestReviewTool(
                scheduler, reviews, options.Todos, options.ReviewLimits, options.ReviewConclusions, stepReviews, options.ReviewFix, options.StepChanges));
        }

        var compactor = options.Compactor
                        ?? (options.Store is { } summaryStore
                            ? new SummaryCompactor(
                                summaryStore,
                                options.Providers,
                                options.TokenCounter,
                                summarizerPrompt: options.Templates?.FindSlot(Prompts.Templates.PromptSlotIds.CompactionSummarizer),
                                policy: options.Options.Compaction)
                            : NoopCompactor.Instance);

        toolSurface = new ToolSurface(builtIns);
        if (options.ToolSegments is { } segments)
        {
            toolSurface.ReplaceExternal(segments);
        }
        else
        {
            toolSurface.ReplaceExternal(options.Tools);
        }
        if (options.DisabledTools is { } disabledTools)
        {
            toolSurface.SetDisabled(disabledTools);
        }

        loop = new AgentLoop(new AgentLoopServices(
            options.Providers,
            toolSurface,
            options.Policy,
            options.ApprovalHandler,
            options.Store,
            SessionCatalogs.From(options.Store),
            options.Artifacts,
            compactor,
            options.TokenCounter,
            this.options,
            SubAgentTools.Names,
            options.DynamicContext,
            options.CacheDiagnostics));
        scheduler.Runner = loop.RunAsync;
    }

    /// <summary>
    /// The options the host was built with. <see cref="AgentOptions.Compaction"/> there is the initial policy; the one in
    /// force is <see cref="CompactionPolicy"/>.
    /// </summary>
    public AgentOptions Options => options;

    /// <summary>The compaction policy in force for the root and every sub-agent of this host.</summary>
    public CompactionPolicy CompactionPolicy => loop.CurrentCompaction;

    /// <summary>
    /// Replaces the compaction policy (switch, trigger and target ratios, and the window/max-output resolvers). Validated
    /// first: an invalid policy throws and the current one stays. Thread-safe; running loops read it before their next model
    /// request. It changes no request and resets no state (hysteresis, last prompt size), so the prompt cache is untouched.
    /// </summary>
    public void SetCompactionPolicy(CompactionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        loop.CurrentCompaction = policy;
    }

    public AgentCatalog Catalog => catalog;

    public SubAgentScheduler SubAgents => scheduler;

    /// <summary>Control surface over the live sub-agents of this host; the UI stop button uses the same path as the tools.</summary>
    public ISubAgentControl SubAgentControl => scheduler;

    public ToolRegistry Tools => toolSurface.Tools;

    /// <summary>
    /// This host's session-to-session mailboxes and the user's grants (forge-core "授权通信"): the app writes a grant when the
    /// user picks a session in the composer's <c>@</c> picker and revokes it when the target ends, the root leaves Active or
    /// the user presses the mark's revoke button.
    /// </summary>
    public SessionMessenger SessionMessaging => messenger;

    /// <summary>
    /// Stops one running tool call of this host's tree - the root's or any sub-agent's, keyed by the agent id and call id of
    /// its <see cref="AgentToolStarted"/> - without stopping the turn: a shell command's process tree is killed, a spawned
    /// child is cancelled, and the model receives an error result saying the user cancelled the call, then continues.
    /// Thread-safe and non-blocking (the cancellation callbacks run on the thread pool). False when no such call is running.
    /// </summary>
    public bool CancelToolCall(string agentId, string callId) =>
        !string.IsNullOrEmpty(agentId) && !string.IsNullOrEmpty(callId) && loop.CancelToolCall(agentId, callId);

    /// <summary>
    /// Host side of <c>spawn_agent</c>: starts a <paramref name="kind"/> sub-agent under <paramref name="root"/> outside a
    /// model turn (automations). The child takes a run slot from the same per-tree pool, gets its own sub-agent session
    /// and reports through <paramref name="sink"/> exactly like a tool-spawned child (<see cref="AgentSubAgentStarted"/>
    /// with the root as parent, then the child's events). Returns the child's final text and its usage for this run.
    /// </summary>
    public async Task<ChildAgentResult> SpawnChildAsync(
        AgentInstance root,
        string kind,
        string task,
        IAgentEventSink sink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(sink);
        var context = new ToolContext(root.SessionId, root.WorkingDirectory, root.Depth);
        var outcome = await scheduler.SpawnAsync(kind, task, null, context, sink, root.Id, ct).ConfigureAwait(false);
        if (scheduler.TryGetInstance(outcome.AgentId, out var child))
        {
            return new ChildAgentResult(child.SessionId, outcome.Text, outcome.IsError, child.Usage);
        }

        return new ChildAgentResult(string.Empty, outcome.Text, outcome.IsError, TokenUsage.Empty);
    }

    /// <summary>
    /// Host side of a new agent session: starts a user-driven child session under this host's root session and runs its
    /// first turn. It uses the same tree <see cref="SpawnChildAsync"/> uses - one run slot from the same pool (queued when
    /// they are all busy), the same approval handler and cancellation table - but the caller names the agent
    /// <paramref name="definition"/>, so the app decides the kind and model the user picked, and the session row is written
    /// with <c>child_driver = "user"</c> and the root session as its parent. The sink receives
    /// <see cref="AgentSubAgentStarted"/> with the root session as parent, then the child's own events, then exactly one
    /// <see cref="AgentSubAgentFinished"/> carrying the run's usage and elapsed time. Returns the child's final text.
    /// <para>The message is the user's first message as built (text plus its attachments), so a first turn carries its
    /// images like every later one does through <see cref="RunUserChildTurnAsync"/>.</para>
    /// </summary>
    public Task<SubAgentOutcome> StartUserChildAsync(
        AgentDefinition definition,
        ChatMessage message,
        string? sessionTitle,
        IAgentEventSink sink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(sink);
        var root = Volatile.Read(ref rootSession) ?? throw new InvalidOperationException(
            "This host has no root session yet: create or resume the root agent before starting a user session under it.");
        return scheduler.StartUserSessionAsync(definition, message, sessionTitle, root.SessionId, root.WorkingDirectory, sink, ct);
    }

    /// <summary>
    /// Runs one more turn of a user-driven session as a stream of its events. It is the same run as
    /// <see cref="RunAsync(AgentInstance, ChatMessage, CancellationToken)"/> - same loop, same slot pool, same approval
    /// path - plus the user-session lifecycle: the run joins the cancellation table, so <see cref="SubAgentControl"/> stops
    /// it, and <see cref="AgentSubAgentFinished"/> closes it with this turn's usage and elapsed time. An instance this host
    /// did not register as a user session is not run at all: the stream carries one <see cref="AgentFailed"/> saying why.
    /// </summary>
    public IAsyncEnumerable<AgentEvent> RunUserChildTurnAsync(
        AgentInstance child,
        ChatMessage message,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(message);
        var root = Volatile.Read(ref rootSession);
        return StreamAsync(
            child.Id,
            child.Definition.Model,
            (sink, token) => scheduler.RunUserTurnAsync(child, message, root?.SessionId, sink, token),
            ct);
    }

    /// <summary>
    /// Rebuilds a stored user-driven session as an instance of this host after a restart: the history comes back the way the
    /// compactor leaves it (the same <see cref="SessionHistory"/> path <see cref="ResumeRootAsync"/> takes) and the instance
    /// is registered as user-owned under the <c>session-&lt;n&gt;</c> namespace, so the model's tools still cannot reach it.
    /// Resuming a session this host already holds returns that instance instead of building a second agent for it.
    /// </summary>
    public Task<AgentInstance> ResumeUserChildAsync(
        string sessionId,
        string workingDirectory,
        AgentDefinition definition,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(definition);
        return scheduler.ResumeUserSessionAsync(sessionId, workingDirectory, definition, ct);
    }

    /// <summary>
    /// Forgets the user-driven session instance <paramref name="agentId"/> names, so <see cref="ResumeUserChildAsync"/> builds
    /// that session again from the store instead of handing the held instance back. There is one case for it: the user changed
    /// the agent or the model of their own agent session, and the change takes effect from the next turn (forge-app "Agent 会话
    /// 的界面与接线"). The conversation stays - the rebuild loads it from the store - the new prompt prefix makes the
    /// provider's cache miss once, and a turn already running is untouched: it keeps its instance and its cancellation entry,
    /// so <see cref="SubAgentControl"/> still stops it. False for an id this host does not hold as a user session.
    /// </summary>
    public bool ForgetUserChild(string agentId) => scheduler.ForgetUserSession(agentId);

    /// <summary>
    /// The instance this host holds for the user-driven session <paramref name="sessionId"/>, running or idle; null when it
    /// holds none - a session whose tab was closed and whose host was rebuilt since has to be resumed again.
    /// </summary>
    public AgentInstance? FindUserChild(string sessionId) =>
        scheduler.TryGetUserSession(sessionId, out var instance) ? instance : null;

    /// <summary>
    /// Delivers one message to a session outside a delivery's own turn (forge-core "reply receipt": a receipt that arrived while
    /// the session was idle lands in the transcript as an unread message). It never runs a turn: an idle session gets the
    /// message appended to its history - written to the store and to the live instance's context, so the very next turn reads
    /// it exactly like a message the user typed - and a session that is inside a turn gets it through
    /// <see cref="AgentInstance.Interject(string)"/>, which the loop delivers at that turn's next tool-batch boundary. The
    /// running loop owns the history, so nothing is appended behind its back.
    /// </summary>
    public async Task AppendSessionMessageAsync(string sessionId, string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(text);
        AgentInstance? instance = null;
        if (Volatile.Read(ref rootSession) is { } anchor && string.Equals(anchor.SessionId, sessionId, StringComparison.Ordinal))
        {
            instance = Volatile.Read(ref rootInstance);
        }
        else if (scheduler.TryGetUserSession(sessionId, out var child))
        {
            instance = child;
        }

        if (instance is { IsRunning: true })
        {
            instance.Interject(text);
            return;
        }

        var message = ChatMessage.User(text);
        var seq = store is null
            ? 0
            : await store.AppendAsync(sessionId, message, null, ct).ConfigureAwait(false);
        instance?.Add(message, seq);
    }

    /// <summary>
    /// Stops every run of a user-driven session this host owns - queued or running - drops the messages their mailboxes still
    /// owe (queued or in flight, each one answered by a stopped receipt), and returns how many runs were stopped. The root
    /// session's own turn is not touched: the shell stops that one and calls this for the sessions under it when the user
    /// presses stop on the root.
    /// </summary>
    public int StopUserChildren(string? reason = null)
    {
        var stopped = scheduler.StopUserSessions(reason);
        // The root's stop takes the undelivered messages with it (forge-core "授权通信"): a delivery left in a mailbox would
        // otherwise open its turn later, on a worktree the user has already taken back.
        messenger.CancelPending(reason);
        return stopped;
    }

    /// <summary>
    /// Replaces the externally supplied tools (the <see cref="AgentHostOptions.Tools"/> part; built-in tools such as
    /// retrieval, <c>todo</c> and the sub-agent tools stay). Thread-safe. Running loops pick the new set up before their next
    /// model request, so a tool approved mid-turn is callable in the same turn; the changed definitions break the prompt
    /// cache once. Same-named external tools never shadow a built-in tool.
    /// </summary>
    public void ReplaceExternalTools(IEnumerable<ITool> tools) => toolSurface.ReplaceExternal(tools);

    /// <summary>Replaces the externally supplied tools, keeping their three segments (see <see cref="ExternalTools"/>).</summary>
    public void ReplaceExternalTools(ExternalTools tools) => toolSurface.ReplaceExternal(tools);

    /// <summary>
    /// Switches the named tools off and every other name back on; a no-op when the set is unchanged, so the prompt cache only
    /// breaks when a switch really moved. Applies to the root and every sub-agent of this host from their next model request.
    /// </summary>
    public void SetDisabledTools(IEnumerable<string> names) => toolSurface.SetDisabled(names);

    /// <summary>
    /// Replaces the cap on how many agents of this host run at once (the <c>agents</c> settings section, read before every
    /// turn). A raise lets agents that are already queued start at once, a lower cap only stops new ones from starting until
    /// the running tree is back under it: nothing is interrupted, and no request or tool set changes, so the prompt cache
    /// holds. Thread-safe; a no-op when the cap did not move.
    /// </summary>
    /// <returns>Whether the cap moved.</returns>
    public bool SetMaxParallelSubAgents(int limit) => loop.SetMaxParallelSubAgents(limit);

    /// <summary>
    /// The cap in force on how many agents of this host run at once. For diagnostics: the settings page edits the
    /// value, no product code reads it back.
    /// </summary>
    public int MaxParallelSubAgents => loop.MaxParallelSubAgents;

    /// <summary>Every tool registered with this host, disabled ones included: the Core built-ins, then the external tools.</summary>
    public (IReadOnlyList<ITool> BuiltIns, IReadOnlyList<ITool> External) AllTools => (toolSurface.BuiltIns, toolSurface.External);

    /// <summary>
    /// <c>prefix_fingerprint</c> of what <paramref name="instance"/>'s next request would send ahead of its messages: its
    /// stable system prompt and the tool definitions it sees right now. Equal fingerprints mean the prompt cache of one
    /// session can serve the other; the loop writes the same value back to the session whenever it changes.
    /// </summary>
    public string PrefixFingerprint(AgentInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var registry = toolSurface.Tools.Select(
            instance.Definition.ToolNames,
            instance.Depth < options.MaxSubAgentDepth,
            SubAgentTools.Names,
            instance.Depth == 0,
            instance.Depth == 0 || instance.IsUserSession);
        return PromptFingerprint.Compute(instance.Prompt.StablePrefix, registry.Definitions);
    }

    /// <summary>Root agent of a session: no depth limit in practice, every tool its definition allows.</summary>
    public AgentInstance CreateRoot(string sessionId, string workingDirectory, AgentDefinition? definition = null, string id = "root")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var kind = definition ?? new AgentDefinition("Root", "Main coding agent.", string.Empty, options.DefaultModel);
        var directory = Path.GetFullPath(workingDirectory);
        var root = new AgentInstance(
            id,
            kind,
            sessionId,
            directory,
            0,
            composer.Compose(new PromptInput(layers, kind.Prompt, null)));
        // The host now knows its main session: a user-driven child session is started under this one.
        Volatile.Write(ref rootSession, new RootSession(sessionId, directory));
        Volatile.Write(ref rootInstance, root);
        return root;
    }

    public AgentInstance CreateRoot(string sessionId, string workingDirectory, string kind, string id = "root")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        return CreateRoot(sessionId, workingDirectory, catalog.Get(kind), id);
    }

    /// <summary>
    /// Root agent of a stored session, with its history rebuilt from the store the way the compactor leaves it
    /// (summary view plus the unsummarized tail), so a conversation continues with its context after a restart.
    /// A session created with full inheritance starts with its ancestors' context cut at their inheritance points.
    /// </summary>
    public async Task<AgentInstance> ResumeRootAsync(
        string sessionId,
        string workingDirectory,
        CancellationToken ct,
        AgentDefinition? definition = null)
    {
        var root = CreateRoot(sessionId, workingDirectory, definition);
        if (store is not { } sessionStore)
        {
            return root;
        }

        root.ReplaceHistory(await SessionHistory.LoadAsync(sessionStore, sessionId, ct).ConfigureAwait(false));
        return root;
    }

    /// <summary>
    /// Manual compaction of <paramref name="instance"/> (the <c>/compact</c> command): ignores the threshold and whether
    /// automatic compaction is enabled, summarizes everything but the newest keepRecent tokens, then re-enables automatic
    /// compaction for the instance. The caller must not run a turn of the same instance concurrently. Null when there was
    /// nothing to compact; <see cref="AgentCompacted"/> is reported through <paramref name="sink"/> when it did; throws
    /// <see cref="CompactionFailedException"/> when the summary could not be produced (nothing changed then).
    /// </summary>
    public Task<CompactionResult?> CompactAsync(AgentInstance instance, IAgentEventSink? sink, CancellationToken ct) =>
        CompactAsync(instance, sink, null, ct);

    /// <summary>
    /// <see cref="CompactAsync(AgentInstance, IAgentEventSink?, CancellationToken)"/> with the user's focus text
    /// (<c>/compact &lt;text&gt;</c>), appended to the summarizer prompts as "Additional focus".
    /// </summary>
    public Task<CompactionResult?> CompactAsync(AgentInstance instance, IAgentEventSink? sink, string? instructions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return loop.CompactNowAsync(instance, sink, instructions, ct);
    }

    /// <summary>
    /// Runs one user turn of <paramref name="root"/> and yields every event of the root and of the
    /// sub-agents it spawns. Abandoning the enumeration cancels the run; failures arrive as
    /// <see cref="AgentFailed"/>, cancellation just ends the stream.
    /// </summary>
    public IAsyncEnumerable<AgentEvent> RunAsync(
        AgentInstance root,
        string userText,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userText);
        return RunAsync(root, ChatMessage.User(userText), ct);
    }

    /// <summary>
    /// Runs one user turn whose message is already built (text plus attached images); otherwise the same as the text
    /// overload.
    /// </summary>
    public IAsyncEnumerable<AgentEvent> RunAsync(
        AgentInstance root,
        ChatMessage userMessage,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(userMessage);
        return StreamAsync(
            root.Id,
            root.Definition.Model,
            (sink, token) => loop.RunAsync(root, userMessage, sink, token),
            ct);
    }

    /// <summary>
    /// One run's events as a stream: <paramref name="body"/> runs off the caller's thread into a sink that feeds the channel,
    /// failures arrive as <see cref="AgentFailed"/> (with the account's windows when a provider rejected the request), and
    /// abandoning the enumeration cancels the run. Shared by the root, user-session and any other run of this host.
    /// </summary>
    private async IAsyncEnumerable<AgentEvent> StreamAsync(
        string agentId,
        ModelSettings model,
        Func<IAgentEventSink, CancellationToken, Task> body,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var sink = new ChannelEventSink(channel.Writer);
        var token = cancellation.Token;
        var run = Task.Run(
            async () =>
            {
                try
                {
                    await body(sink, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    // A rejected request still carries the account's windows; report them before the failure so the shell
                    // shows what is actually exhausted instead of only that something failed.
                    if (exception is ProviderException { RateLimit: { } rateLimit })
                    {
                        sink.Emit(new AgentRateLimitUpdated(agentId, rateLimit));
                    }

                    if (exception is ProviderException provider
                        && ProviderQuota.Classify(provider, model, DateTimeOffset.UtcNow) is { } quota)
                    {
                        sink.Emit(new AgentQuotaExhausted(agentId, quota));
                    }

                    sink.Emit(new AgentFailed(agentId, exception.Message));
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            },
            CancellationToken.None);

        try
        {
            await foreach (var value in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                yield return value;
            }
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            channel.Writer.TryComplete();
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>The main session this host was built for; see <see cref="rootSession"/>.</summary>
    private sealed record RootSession(string SessionId, string WorkingDirectory);
}
