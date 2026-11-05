using System.Diagnostics;
using Forge.Abstractions;
using Forge.App.Approval;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Forge.Core.Providers;
using Forge.Core.Tools;
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
    /// <summary>
    /// The shell's approval wiring (queue, remembered rules and the extension authoring adapter). Null when the runtime was
    /// assembled without the composition root, as in a unit test: the session then has no approval handler and an ask is a
    /// denial, exactly as before the queue existed.
    /// </summary>
    public ApprovalServices? Approvals { get; set; }

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
    private SessionApprovalHandler? approvalHandler;
    private ExtensionHostClient? listenedClient;
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

    private AgentHost Build()
    {
        var tools = CollectExternalTools();
        var handler = services.Approvals?.CreateHandler(Session);

        // A retried build (the first resume failed) replaces the handler; the old one must not keep a dead wait alive.
        approvalHandler?.Dispose();
        approvalHandler = handler;

        var layers = LayerSources.Resolve(BuiltInPrompts.System, AgentRuntimeServices.UserDirectory(), Session.WorkspacePath);
        return new AgentHost(
            new AgentHostOptions
            {
                Providers = services.ProviderFactory,
                Tools = tools,
                Layers = layers,
                Policy = SessionPolicy,
                ApprovalHandler = handler,
                Store = services.Store,
                Todos = services.Todos,
                Options = new AgentOptions { DefaultModel = services.Model },
            });
    }

    /// <summary>
    /// The tools that come from outside Core: the extension host's plugin tools, the <c>run_code</c> proxy and, once the
    /// approval wiring exists, the five extension tools. Recomputed on every registration change so a hot reload reaches
    /// the loop before its next model request.
    /// </summary>
    private List<ITool> CollectExternalTools()
    {
        var tools = new List<ITool>();
        if (services.HostClient is not { } client)
        {
            return tools;
        }

        tools.AddRange(client.Tools);
        if (tools.Count > 0)
        {
            tools.Add(client.CreateRunCodeTool(new SnapshotToolRegistry(client.Tools)));
        }

        if (services.Approvals?.Extensions is { } extensions)
        {
            tools.AddRange(ExtensionTools.Create(extensions));
        }

        return tools;
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
            Trace.WriteLine("[session] the external tool surface could not be replaced: " + exception.Message);
        }
    }

    public void Dispose()
    {
        SessionApprovalHandler? handler;
        ExtensionHostClient? client;
        lock (gate)
        {
            disposed = true;
            host = null;
            root = null;
            handler = approvalHandler;
            approvalHandler = null;
            client = listenedClient;
            listenedClient = null;
        }

        if (client is not null)
        {
            client.ToolsChanged -= OnToolsChanged;
        }

        // Releasing the runtime withdraws this session's approval cards; every waiting loop then sees a denial.
        handler?.Dispose();
    }

    /// <summary>
    /// Tool rules that need no approval card: the session checklist (the app's own state, not a workspace file) and the
    /// extension tools that only stage a draft or switch a plugin off - their real gate is the plugin approval card the
    /// authoring service puts in front of the user. <c>extension_delete</c> keeps asking: that card is the deletion
    /// confirmation. Every other write or execution goes to the session's <see cref="SessionApprovalHandler"/>.
    /// </summary>
    private static readonly IApprovalPolicy SessionPolicy = CreatePolicy();

    private static IApprovalPolicy CreatePolicy()
    {
        var rules = new Dictionary<string, ApprovalMode>(StringComparer.Ordinal)
        {
            [TodoTool.Name] = ApprovalMode.Allow,
        };
        foreach (var name in ExtensionToolNames.AllowedWithoutAsking)
        {
            rules[name] = ApprovalMode.Allow;
        }

        return new DefaultApprovalPolicy { ToolRules = rules };
    }

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
