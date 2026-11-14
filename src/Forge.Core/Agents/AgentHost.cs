using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Forge.Abstractions;
using Forge.Core.Providers;
using Forge.Core.Prompts;

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

    /// <summary>Extra agent kinds layered on top of <see cref="BuiltInAgents"/>; same name replaces the built-in.</summary>
    public IEnumerable<AgentDefinition>? Agents { get; init; }

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
    /// </summary>
    public Func<string?>? DynamicContext { get; init; }

    /// <summary>Registers read_artifact, search_history and read_history when their store is available.</summary>
    public bool IncludeRetrievalTools { get; init; } = true;

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

        if (options.Todos is { } todos)
        {
            builtIns.Add(new TodoTool(todos, options.StepCommitter));
        }

        if (options.QuestionHandler is { } questionHandler)
        {
            builtIns.Add(new AskUserTool(questionHandler));
        }

        scheduler = new SubAgentScheduler(catalog, layers, composer, options.Store, this.options);
        builtIns.Add(new SpawnAgentTool(scheduler, catalog.All));
        builtIns.Add(new SubAgentSteerTool(scheduler));
        builtIns.Add(new SubAgentFollowUpTool(scheduler));
        builtIns.Add(new SubAgentStopTool(scheduler));

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
        toolSurface.ReplaceExternal(options.Tools);
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
    /// Stops one running tool call of this host's tree - the root's or any sub-agent's, keyed by the agent id and call id of
    /// its <see cref="AgentToolStarted"/> - without stopping the turn: a shell command's process tree is killed, a spawned
    /// child is cancelled, and the model receives an error result saying the user cancelled the call, then continues.
    /// Thread-safe and non-blocking (the cancellation callbacks run on the thread pool). False when no such call is running.
    /// </summary>
    public bool CancelToolCall(string agentId, string callId) =>
        !string.IsNullOrEmpty(agentId) && !string.IsNullOrEmpty(callId) && loop.CancelToolCall(agentId, callId);

    /// <summary>Tool calls of this tree that can be stopped right now; zero between calls. For tests and diagnostics.</summary>
    internal int RunningToolCallCount => loop.RunningToolCallCount;

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
    /// Replaces the externally supplied tools (the <see cref="AgentHostOptions.Tools"/> part; built-in tools such as
    /// retrieval, <c>todo</c> and the sub-agent tools stay). Thread-safe. Running loops pick the new set up before their next
    /// model request, so a tool approved mid-turn is callable in the same turn; the changed definitions break the prompt
    /// cache once. Same-named external tools never shadow a built-in tool.
    /// </summary>
    public void ReplaceExternalTools(IEnumerable<ITool> tools) => toolSurface.ReplaceExternal(tools);

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
    /// The cap in force on how many agents of this host run at once. For tests and diagnostics: the settings page edits the
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
            instance.Depth == 0);
        return PromptFingerprint.Compute(instance.Prompt.StablePrefix, registry.Definitions);
    }

    /// <summary>Root agent of a session: no depth limit in practice, every tool its definition allows.</summary>
    public AgentInstance CreateRoot(string sessionId, string workingDirectory, AgentDefinition? definition = null, string id = "root")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var kind = definition ?? new AgentDefinition("Root", "Main coding agent.", string.Empty, options.DefaultModel);
        return new AgentInstance(
            id,
            kind,
            sessionId,
            Path.GetFullPath(workingDirectory),
            0,
            composer.Compose(new PromptInput(layers, kind.Prompt, null)));
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
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentInstance root,
        ChatMessage userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(userMessage);

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
                    await loop.RunAsync(root, userMessage, sink, token).ConfigureAwait(false);
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
                        sink.Emit(new AgentRateLimitUpdated(root.Id, rateLimit));
                    }

                    if (exception is ProviderException provider
                        && ProviderQuota.Classify(provider, root.Definition.Model, DateTimeOffset.UtcNow) is { } quota)
                    {
                        sink.Emit(new AgentQuotaExhausted(root.Id, quota));
                    }

                    sink.Emit(new AgentFailed(root.Id, exception.Message));
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
}
