using System.Diagnostics;
using Forge.Abstractions;
using Forge.App.Approval;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Forge.Core.Providers;
using Forge.Core.Tools;
using Forge.HostClient;
using Forge.Workspace;

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
    /// assembled without the composition root, as in a unit test: the session then has no approval handler and an ask is a
    /// denial, exactly as before the queue existed.
    /// </summary>
    public ApprovalServices? Approvals { get; set; }

    /// <summary>
    /// Commits a TODO step when the agent marks it done (handed to <c>AgentHostOptions.StepCommitter</c>). Null when the
    /// runtime was assembled without the composition root, as in a unit test: marking an item done then commits nothing.
    /// </summary>
    public IStepCommitter? StepCommitter { get; set; }

    /// <summary>
    /// The router the extension host client uses for plugin and workflow <c>host.tool_call</c>; each session registers its
    /// tool set and approval handler here when its host is built. Null in a unit test: nothing is registered.
    /// </summary>
    public SessionToolCallRouter? ToolRouter { get; set; }

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
    private SessionApprovalHandler? approvalHandler;
    private AgentHost? builtHost;
    private ExtensionHostClient? listenedClient;
    private bool disposed;

    // Sub-agent sessions aliased to this runtime in the router, by child agent id. Guarded by gate.
    private readonly Dictionary<string, string> childSessions = new(StringComparer.Ordinal);

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
                        TrackSubAgent(value);
                        sink(value);
                    }
                },
                CancellationToken.None)
            .ConfigureAwait(false);
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
        var handler = services.Approvals?.CreateHandler(Session);

        // A retried build (the first resume failed) replaces the handler; the old one must not keep a dead wait alive.
        approvalHandler?.Dispose();
        approvalHandler = handler;

        var layers = LayerSources.Resolve(BuiltInPrompts.System, AgentRuntimeServices.UserDirectory(), Session.WorkspacePath);
        var built = new AgentHost(
            new AgentHostOptions
            {
                Providers = services.ProviderFactory,
                Tools = BuildTools(),
                Layers = layers,
                Policy = SessionPolicy,
                ApprovalHandler = handler,
                Store = services.Store,
                Todos = services.Todos,
                StepCommitter = services.StepCommitter,
                Options = new AgentOptions { DefaultModel = services.Model },
            });

        // From here on a plugin or workflow run of this session can call tools through this session's tool set and handler.
        builtHost = built;
        services.ToolRouter?.Register(SessionId, this);
        return built;
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
    /// </summary>
    private List<ITool> CollectExternalTools()
    {
        var tools = new List<ITool>();
        if (services.HostClient is { } client)
        {
            tools.AddRange(client.Tools);
            if (tools.Count > 0)
            {
                // Scripts only see extension-host tools: a call from inside run_code never goes through the per-call
                // approval presentation, so the git write tools (git_branch, git_sync) must stay out of its registry.
                tools.Add(client.CreateRunCodeTool(new SnapshotToolRegistry(client.Tools)));
            }

            if (services.Approvals?.Extensions is { } extensions)
            {
                tools.AddRange(ExtensionTools.Create(extensions));
            }
        }

        if (services.Git is { } git)
        {
            tools.AddRange(git.All);
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

    /// <summary>The tool surface one session runs with (see <see cref="CollectExternalTools"/>); tests read it directly.</summary>
    internal IReadOnlyList<ITool> BuildTools() => CollectExternalTools();

    public void Dispose()
    {
        SessionApprovalHandler? handler;
        ExtensionHostClient? client;
        string[] aliases;
        lock (gate)
        {
            disposed = true;
            aliases = [.. childSessions.Values];
            childSessions.Clear();
            builtHost = null;
            host = null;
            root = null;
            handler = approvalHandler;
            approvalHandler = null;
            client = listenedClient;
            listenedClient = null;
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
    }

    /// <summary>
    /// Tool rules that need no approval card: the session checklist (the app's own state, not a workspace file) and the
    /// extension tools that only stage a draft or switch a plugin off - their real gate is the plugin approval card the
    /// authoring service puts in front of the user. <c>extension_delete</c> keeps asking: that card is the deletion
    /// confirmation. Every other write or execution goes to the session's <see cref="SessionApprovalHandler"/>.
    /// <para>The git tools are deliberately absent: the read-only ones pass through
    /// <see cref="DefaultApprovalPolicy.AutoAllowReadOnly"/>, the write ones (git_branch, git_sync) always ask.</para>
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
