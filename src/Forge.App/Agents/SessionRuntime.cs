using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Forge.Core.Providers;
using Forge.HostClient;

namespace Forge.App.Agents;

/// <summary>Everything a session runtime needs from the composition root, shared by every session.</summary>
internal sealed record AgentRuntimeServices(
    Func<ModelSettings, IChatProvider> ProviderFactory,
    ExtensionHostClient? HostClient,
    ISessionStore Store,
    ISessionCatalog Catalog,
    ITodoStore Todos,
    ModelSettings Model)
{
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
internal sealed class SessionRuntime : IDisposable
{
    private readonly AgentRuntimeServices services;
    private readonly Lock gate = new();
    private AgentHost? host;
    private AgentInstance? root;
    private bool disposed;

    public SessionRuntime(AgentRuntimeServices services, SessionInfo session)
    {
        this.services = services;
        Session = session;
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

    /// <summary>Sub-agent sessions this session spawned, ordered by last activity.</summary>
    public Task<IReadOnlyList<SessionInfo>> QueryChildrenAsync(CancellationToken ct) =>
        QueryChildrenAsync(SessionId, ct);

    /// <summary>Sub-agent sessions of any session, so a restored tree can descend below its direct children.</summary>
    public Task<IReadOnlyList<SessionInfo>> QueryChildrenAsync(string parentSessionId, CancellationToken ct) =>
        services.Catalog.QueryAsync(new SessionQuery { ParentSessionId = parentSessionId }, ct);

    public async Task RunTurnAsync(string userText, Action<AgentEvent> sink, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);
        ArgumentNullException.ThrowIfNull(sink);

        var (agentHost, agent) = await Task.Run(() => ResolveAsync(), ct).ConfigureAwait(false);
        await Task.Run(
                async () =>
                {
                    await foreach (var value in agentHost.RunAsync(agent, userText, ct).ConfigureAwait(false))
                    {
                        sink(value);
                    }
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<(AgentHost Host, AgentInstance Agent)> ResolveAsync()
    {
        AgentHost built;
        AgentInstance agent;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (host is not null && root is not null)
            {
                return (host, root);
            }

            built = Build();
        }

        agent = await built.ResumeRootAsync(SessionId, Session.WorkspacePath, CancellationToken.None).ConfigureAwait(false);
        lock (gate)
        {
            host ??= built;
            root = agent;
            return (host, agent);
        }
    }

    private AgentHost Build()
    {
        var tools = new List<ITool>();
        if (services.HostClient is { } client)
        {
            tools.AddRange(client.Tools);
            if (tools.Count > 0)
            {
                tools.Add(client.CreateRunCodeTool(new SnapshotToolRegistry(client.Tools)));
            }
        }

        var layers = LayerSources.Resolve(BuiltInPrompts.System, AgentRuntimeServices.UserDirectory(), Session.WorkspacePath);
        return new AgentHost(
            new AgentHostOptions
            {
                Providers = services.ProviderFactory,
                Tools = tools,
                Layers = layers,
                Policy = TodoPolicy,
                ApprovalHandler = null,
                Store = services.Store,
                Todos = services.Todos,
                Options = new AgentOptions { DefaultModel = services.Model },
            });
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            host = null;
            root = null;
        }
    }

    /// <summary>
    /// The session checklist is the app's own state, not a file in the workspace, so the <c>todo</c> tool is allowed
    /// without an approval dialog; every other write still asks, and without a handler an ask is a denial.
    /// </summary>
    private static readonly IApprovalPolicy TodoPolicy = new DefaultApprovalPolicy
    {
        ToolRules = new Dictionary<string, ApprovalMode>(StringComparer.Ordinal)
        {
            [TodoTool.Name] = ApprovalMode.Allow,
        },
    };

    /// <summary>The immutable tool surface a script may call; a snapshot, so a later registration cannot mutate it.</summary>
    private sealed class SnapshotToolRegistry : IToolRegistry
    {
        private readonly Dictionary<string, ITool> byName = new(StringComparer.Ordinal);

        public SnapshotToolRegistry(IReadOnlyList<ITool> tools)
        {
            foreach (var tool in tools)
            {
                byName[tool.Definition.Name] = tool;
            }

            Tools = [.. byName.Values];
        }

        public IReadOnlyList<ITool> Tools { get; }

        public bool TryGet(string name, out ITool tool) => byName.TryGetValue(name, out tool!);
    }
}

/// <summary>Stands in for a provider when none is configured, so a turn fails with one clear sentence instead of a null.</summary>
internal sealed class UnconfiguredChatProvider : IChatProvider
{
    public static UnconfiguredChatProvider Instance { get; } = new();

    public string Id => "unconfigured";

    public IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, CancellationToken ct) =>
        throw new ProviderException(
            "No model provider is configured. Add a provider section to settings.json and reload the app.", retryable: false);
}
