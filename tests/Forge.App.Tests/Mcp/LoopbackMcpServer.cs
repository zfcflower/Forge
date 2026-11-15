using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Forge.Mcp;

namespace Forge.App.Tests.Mcp;

/// <summary>
/// In-memory <see cref="IMcpTransport"/>: every outgoing payload is handed to <see cref="Responder"/> and whatever it
/// returns is delivered back on one background pump, in order. The same shape as the loopback fixture of Forge.Mcp.Tests,
/// cut to what the app-level suites need.
/// </summary>
internal sealed class LoopbackMcpTransport : IMcpTransport
{
    private readonly ConcurrentQueue<string> sent = new();
    private readonly Channel<Action> inbound = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task pump;
    private int connected;

    public LoopbackMcpTransport(string name)
    {
        Name = name;
        pump = Task.Run(PumpAsync);
    }

    public string Name { get; }

    public bool IsConnected => Volatile.Read(ref connected) != 0;

    /// <summary>Answers one JSON-RPC payload; a notification (no id) gets no reply.</summary>
    public Func<JsonElement, IEnumerable<string>>? Responder { get; set; }

    /// <summary>Every payload this transport was handed, oldest first, across every session it served.</summary>
    public IReadOnlyList<string> Sent => sent.ToArray();

    /// <summary>Counts the payloads whose method is <paramref name="method"/>; a test reads the list traffic from it.</summary>
    public int CountSent(string method) =>
        Sent.Count(payload => payload.Contains('"' + method + '"', StringComparison.Ordinal));

    /// <summary>Awaited inside <see cref="StopAsync"/>: lets a test park a user action mid-stop and observe it.</summary>
    public Func<Task>? StopHook { get; set; }

    public event Action<ReadOnlyMemory<byte>>? MessageReceived;

    public event Action<Exception?>? Closed;

    public Task StartAsync(CancellationToken ct)
    {
        Volatile.Write(ref connected, 1);
        return Task.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct, long? requestId = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConnected)
        {
            throw new McpConnectionClosedException("The loopback transport is not connected.");
        }

        sent.Enqueue(Encoding.UTF8.GetString(payload.Span));
        if (Responder is { } responder)
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(payload.Span));
            foreach (var reply in responder(document.RootElement).ToArray())
            {
                inbound.Writer.TryWrite(() => MessageReceived?.Invoke(Encoding.UTF8.GetBytes(reply)));
            }
        }

        return ValueTask.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (StopHook is { } hook)
        {
            await hook().ConfigureAwait(false);
        }

        Volatile.Write(ref connected, 0);
        Closed?.Invoke(null);
    }

    /// <summary>Delivers a server-originated payload (a notification, a response or a request) asynchronously.</summary>
    public void Push(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        inbound.Writer.TryWrite(() => MessageReceived?.Invoke(bytes));
    }

    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref connected, 0);
        inbound.Writer.TryComplete();
        await pump.ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        await foreach (var action in inbound.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            action();
        }
    }
}

/// <summary>
/// Answers client requests by method name. Requests get one JSON-RPC result; notifications and responses get nothing.
/// </summary>
internal sealed class ScriptedMcpServer
{
    public const string InitializeResultJson =
        """{"protocolVersion":"2025-06-18","capabilities":{"tools":{"listChanged":true}},"serverInfo":{"name":"fake","version":"1.0.0"}}""";

    /// <summary>The same handshake with the resource capability set, for the resource browser's tests.</summary>
    public const string InitializeWithResourcesJson =
        """{"protocolVersion":"2025-06-18","capabilities":{"tools":{"listChanged":true},"resources":{"subscribe":true,"listChanged":true}},"serverInfo":{"name":"fake","version":"1.0.0"}}""";

    private readonly Dictionary<string, Func<JsonElement, string?>> handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<JsonElement, string?>> errors = new(StringComparer.Ordinal);
    private readonly string toolsListJson;

    public ScriptedMcpServer(IEnumerable<(string Name, string? Description)>? tools = null)
    {
        toolsListJson = ToolsListJson(tools ?? [("echo", "Echoes text")]);
        On(McpProtocol.Initialize, _ => InitializeResultJson);
        On(McpProtocol.ToolsList, _ => toolsListJson);
        On(McpProtocol.ToolsCall, _ => """{"content":[{"type":"text","text":"ok"}]}""");
        On(McpProtocol.Ping, _ => "{}");
    }

    public ScriptedMcpServer On(string method, Func<JsonElement, string?> handler)
    {
        handlers[method] = handler;
        return this;
    }

    /// <summary>
    /// Answers one method with a JSON-RPC error body (e.g. <c>{"code":-32002,"message":"nope"}</c>); a null return falls
    /// back to the normal handler, so one method can fail for one argument only.
    /// </summary>
    public ScriptedMcpServer OnError(string method, Func<JsonElement, string?> error)
    {
        errors[method] = error;
        return this;
    }

    public IReadOnlyList<string> Respond(JsonElement root)
    {
        if (!root.TryGetProperty("id", out _) || !root.TryGetProperty("method", out var methodElement))
        {
            return [];
        }

        var method = methodElement.GetString() ?? string.Empty;
        if (errors.TryGetValue(method, out var error) && error(root) is { } body)
        {
            var idJson = root.GetProperty("id").GetRawText();
            return [$"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"error\":{body}}}"];
        }

        if (!handlers.TryGetValue(method, out var handler))
        {
            return [Result(root.GetProperty("id").GetRawText(), """{"error":{"code":-32601,"message":"method not found"}}""")];
        }

        var result = handler(root);
        return result is null ? [] : [Result(root.GetProperty("id").GetRawText(), result)];
    }

    private static string Result(string idJson, string resultJson) =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"result\":{resultJson}}}";

    private static string ToolsListJson(IEnumerable<(string Name, string? Description)> tools)
    {
        var array = new JsonArray();
        foreach (var (name, description) in tools)
        {
            var tool = new JsonObject { ["name"] = name, ["inputSchema"] = new JsonObject { ["type"] = "object" } };
            if (!string.IsNullOrEmpty(description))
            {
                tool["description"] = description;
            }

            array.Add(tool);
        }

        return new JsonObject { ["tools"] = array }.ToJsonString();
    }
}

/// <summary>
/// An <see cref="McpServerManager"/> whose transports are loopbacks answered by <see cref="ScriptedMcpServer"/> scripts,
/// started so its <see cref="Tools"/> carry real <see cref="McpToolAdapter"/> instances. The app tests have no other
/// public way to build an adapter: the constructor is internal to Forge.Mcp, which grants no InternalsVisibleTo.
/// </summary>
internal sealed class ReadyMcpCatalog : IAsyncDisposable
{
    private readonly List<LoopbackMcpTransport> transports = [];

    private ReadyMcpCatalog(McpConfig config, Func<string, ScriptedMcpServer> serverFor)
    {
        Manager = new McpServerManager(config, new McpServerManagerOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(10),
            InitialReconnectDelay = TimeSpan.FromMilliseconds(10),
            MaxReconnectAttempts = 0,
            TransportFactory = server =>
            {
                var transport = new LoopbackMcpTransport(server.Name) { Responder = serverFor(server.Name).Respond };
                transports.Add(transport);
                return transport;
            },
        });
    }

    public McpServerManager Manager { get; }

    /// <summary>The manager's current tools, ordered by exposed name.</summary>
    public IReadOnlyList<McpToolAdapter> Tools => Manager.Tools;

    /// <summary>The loopback transports created so far; the Forge.Mcp.Tests fixture exposes the same surface.</summary>
    public IReadOnlyList<LoopbackMcpTransport> Transports
    {
        get
        {
            lock (transports)
            {
                return transports.ToArray();
            }
        }
    }

    public static async Task<ReadyMcpCatalog> StartAsync(
        McpConfig config,
        Func<string, ScriptedMcpServer>? serverFor = null,
        CancellationToken ct = default)
    {
        var catalog = new ReadyMcpCatalog(config, serverFor ?? (static _ => new ScriptedMcpServer()));
        await catalog.Manager.StartAllAsync(ct);
        return catalog;
    }

    /// <summary>One stdio server exposing the given tools; the common shape of the exposure tests.</summary>
    public static Task<ReadyMcpCatalog> OneServerAsync(
        string serverName,
        IReadOnlyList<(string Name, string? Description)> tools,
        CancellationToken ct = default)
    {
        var script = new ScriptedMcpServer(tools);
        var config = new McpConfig([new McpServerConfig { Name = serverName, Command = "fake-" + serverName }], []);
        return StartAsync(config, _ => script, ct);
    }

    public async ValueTask DisposeAsync() => await Manager.DisposeAsync();
}
