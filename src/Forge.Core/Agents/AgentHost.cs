using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Forge.Abstractions;
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

    public ISessionStore? Store { get; init; }

    public IArtifactStore? Artifacts { get; init; }

    /// <summary>Registers the <c>todo</c> tool when set; the same store backs the TODO panel.</summary>
    public ITodoStore? Todos { get; init; }

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
    private readonly ToolRegistry toolRegistry;
    private readonly AgentLoop loop;
    private readonly ISessionStore? store;

    public AgentHost(AgentHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Options;
        catalog = new AgentCatalog(options.Agents);
        layers = options.Layers;
        composer = options.Composer;
        store = options.Store;

        var tools = new List<ITool>(options.Tools);
        if (options.IncludeRetrievalTools)
        {
            if (options.Store is { } store)
            {
                tools.Add(new SearchHistoryTool(store));
                tools.Add(new ReadHistoryTool(store));
            }

            if (options.Artifacts is { } artifacts)
            {
                tools.Add(new ReadArtifactTool(artifacts));
            }
        }

        if (options.Todos is { } todos)
        {
            tools.Add(new TodoTool(todos));
        }

        scheduler = new SubAgentScheduler(catalog, layers, composer, options.Store, this.options);
        tools.Add(new SpawnAgentTool(scheduler, catalog.All));
        tools.Add(new SubAgentSteerTool(scheduler));
        tools.Add(new SubAgentFollowUpTool(scheduler));
        tools.Add(new SubAgentStopTool(scheduler));

        var compactor = options.Compactor
                        ?? (options.Store is { } summaryStore
                            ? new SummaryCompactor(summaryStore, options.Providers, options.TokenCounter, this.options.CompactionTargetRatio)
                            : NoopCompactor.Instance);

        toolRegistry = new ToolRegistry(tools);
        loop = new AgentLoop(new AgentLoopServices(
            options.Providers,
            toolRegistry,
            options.Policy,
            options.ApprovalHandler,
            options.Store,
            SessionCatalogs.From(options.Store),
            options.Artifacts,
            compactor,
            options.TokenCounter,
            this.options,
            SubAgentTools.Names,
            options.DynamicContext));
        scheduler.Runner = loop.RunAsync;
    }

    public AgentOptions Options => options;

    public AgentCatalog Catalog => catalog;

    public SubAgentScheduler SubAgents => scheduler;

    /// <summary>Control surface over the live sub-agents of this host; the UI stop button uses the same path as the tools.</summary>
    public ISubAgentControl SubAgentControl => scheduler;

    public ToolRegistry Tools => toolRegistry;

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
    /// Runs one user turn of <paramref name="root"/> and yields every event of the root and of the
    /// sub-agents it spawns. Abandoning the enumeration cancels the run; failures arrive as
    /// <see cref="AgentFailed"/>, cancellation just ends the stream.
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentInstance root,
        string userText,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(userText);

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
                    await loop.RunAsync(root, ChatMessage.User(userText), sink, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
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
