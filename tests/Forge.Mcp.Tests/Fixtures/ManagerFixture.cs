namespace Forge.Mcp.Tests.Fixtures;

/// <summary>Builds an <see cref="McpServerManager"/> whose transports are loopbacks answered by per-server scripts.</summary>
internal sealed class ManagerFixture : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly List<LoopbackTransport> transports = [];
    private readonly Func<string, ScriptedServer> serverFor;
    private readonly Func<McpServerConfig, LoopbackTransport>? transportFor;

    public ManagerFixture(
        McpConfig config,
        Func<string, ScriptedServer>? serverFor = null,
        int maxReconnectAttempts = 5,
        TimeSpan? initialReconnectDelay = null,
        TimeSpan? requestTimeout = null,
        int maxResultChars = 64 * 1024,
        Func<McpServerConfig, LoopbackTransport>? transportFor = null)
    {
        this.serverFor = serverFor ?? (_ => new ScriptedServer());
        this.transportFor = transportFor;
        Manager = new McpServerManager(config, new McpServerManagerOptions
        {
            RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5),
            InitialReconnectDelay = initialReconnectDelay ?? TimeSpan.FromMilliseconds(20),
            MaxReconnectDelay = TimeSpan.FromSeconds(10),
            MaxReconnectAttempts = maxReconnectAttempts,
            MaxResultChars = maxResultChars,
            TransportFactory = Create,
        });
    }

    public McpServerManager Manager { get; }

    public IReadOnlyList<LoopbackTransport> Transports
    {
        get
        {
            lock (gate)
            {
                return transports.ToArray();
            }
        }
    }

    public int TransportCount(string name) => Transports.Count(transport => transport.Name == name);

    public static McpServerConfig Stdio(string name, bool disabled = false, IReadOnlyList<string>? readOnlyTools = null, TimeSpan? timeout = null) => new()
    {
        Name = name,
        Command = "fake-" + name,
        Disabled = disabled,
        ReadOnlyTools = readOnlyTools ?? [],
        Timeout = timeout,
    };

    public static McpConfig Of(params McpServerConfig[] servers) => new(servers, []);

    public async ValueTask DisposeAsync() => await Manager.DisposeAsync();

    private IMcpTransport Create(McpServerConfig config)
    {
        LoopbackTransport transport;
        if (transportFor is not null)
        {
            transport = transportFor(config);
        }
        else
        {
            transport = new LoopbackTransport(config.Name);
            transport.Responder = serverFor(config.Name).Respond;
        }

        lock (gate)
        {
            transports.Add(transport);
        }

        return transport;
    }
}
