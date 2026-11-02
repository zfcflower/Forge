using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Forge.Mcp;

/// <summary>One MCP server session: initialize handshake, tool/resource/prompt calls and change notifications.</summary>
public sealed class McpClient : IAsyncDisposable
{
    private readonly McpClientOptions options;
    private JsonRpcPeer peer;
    private readonly SemaphoreSlim connectGate = new(1, 1);
    private McpServerCapabilities capabilities = McpServerCapabilities.None;
    private volatile bool initialized;
    private volatile bool closed;
    private int disposed;

    public McpClient(IMcpTransport transport, McpClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        Transport = transport;
        this.options = options ?? new McpClientOptions();
        peer = CreatePeer();
    }

    public IMcpTransport Transport { get; }

    public McpClientOptions Options => options;

    public McpServerInfo? ServerInfo { get; private set; }

    public McpServerCapabilities Capabilities => capabilities;

    public string? ProtocolVersion { get; private set; }

    public string? Instructions { get; private set; }

    public bool IsInitialized => initialized;

    public bool IsConnected => initialized && !closed;

    public event Action? ToolsChanged;

    public event Action? ResourcesChanged;

    public event Action? PromptsChanged;

    public event Action<McpNotification>? NotificationReceived;

    public event Action<Exception?>? Closed;

    public async Task ConnectAsync(CancellationToken ct)
    {
        await connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            if (peer.IsClosed)
            {
                await peer.DisposeAsync().ConfigureAwait(false);
                peer = CreatePeer();
            }

            closed = false;
            await Transport.StartAsync(ct).ConfigureAwait(false);

            using var document = await peer.RequestAsync(McpProtocol.Initialize, WriteInitializeParams, ct).ConfigureAwait(false);
            var result = ResultOf(document, McpProtocol.Initialize);

            ProtocolVersion = ReadString(result, "protocolVersion") ?? options.ProtocolVersion;
            capabilities = result.TryGetProperty("capabilities", out var caps) ? McpServerCapabilities.Parse(caps) : McpServerCapabilities.None;
            ServerInfo = result.TryGetProperty("serverInfo", out var info) && info.ValueKind == JsonValueKind.Object
                ? new McpServerInfo(ReadString(info, "name") ?? "unknown", ReadString(info, "version") ?? "0.0.0", ReadString(info, "title"))
                : null;
            Instructions = ReadString(result, "instructions");

            Transport.SetProtocolVersion(ProtocolVersion);
            await peer.NotifyAsync(McpProtocol.Initialized, null, ct).ConfigureAwait(false);
            initialized = true;

            try
            {
                await Transport.StartServerStreamAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is McpException or HttpRequestException)
            {
            }
        }
        finally
        {
            connectGate.Release();
        }
    }

    public async Task PingAsync(CancellationToken ct)
    {
        using var document = await peer.RequestAsync(McpProtocol.Ping, null, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) =>
        await ListAllAsync(McpProtocol.ToolsList, "tools", ParseTool, ct).ConfigureAwait(false);

    public async IAsyncEnumerable<McpToolInfo> EnumerateToolsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var tool in EnumeratePagesAsync(McpProtocol.ToolsList, "tools", ParseTool, ct).ConfigureAwait(false))
        {
            yield return tool;
        }
    }

    public async Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) =>
        await ListAllAsync(McpProtocol.ResourcesList, "resources", ParseResource, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) =>
        await ListAllAsync(McpProtocol.PromptsList, "prompts", ParsePrompt, ct).ConfigureAwait(false);

    public async Task<McpToolCallResult> CallToolAsync(string name, string? argumentsJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var arguments = JsonText.Compact(argumentsJson);
        using var document = await peer.RequestAsync(McpProtocol.ToolsCall, writer =>
        {
            writer.WriteString("name", name);
            writer.WritePropertyName("arguments");
            JsonText.WriteCompact(writer, arguments);
        }, ct).ConfigureAwait(false);

        return McpContentFlattener.ToolResult(ResultOf(document, McpProtocol.ToolsCall));
    }

    public async Task<McpResourceResult> ReadResourceAsync(string uri, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        using var document = await peer.RequestAsync(McpProtocol.ResourcesRead,
            writer => writer.WriteString("uri", uri), ct).ConfigureAwait(false);
        return McpContentFlattener.ResourceResult(ResultOf(document, McpProtocol.ResourcesRead));
    }

    public async Task<McpPromptResult> GetPromptAsync(
        string name,
        IReadOnlyDictionary<string, string?>? arguments,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var document = await peer.RequestAsync(McpProtocol.PromptsGet, writer =>
        {
            writer.WriteString("name", name);
            if (arguments is { Count: > 0 })
            {
                writer.WritePropertyName("arguments");
                writer.WriteStartObject();
                foreach (var (key, value) in arguments)
                {
                    writer.WriteString(key, value ?? string.Empty);
                }

                writer.WriteEndObject();
            }
        }, ct).ConfigureAwait(false);

        return McpContentFlattener.PromptResult(ResultOf(document, McpProtocol.PromptsGet));
    }

    public async Task SendNotificationAsync(string method, Action<Utf8JsonWriter>? writeParams, CancellationToken ct) =>
        await peer.NotifyAsync(method, writeParams, ct).ConfigureAwait(false);

    public async Task CloseAsync(CancellationToken ct)
    {
        closed = true;
        initialized = false;
        await Transport.StopAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        closed = true;
        initialized = false;
        await peer.DisposeAsync().ConfigureAwait(false);
        if (options.DisposeTransport)
        {
            await Transport.DisposeAsync().ConfigureAwait(false);
        }

        connectGate.Dispose();
    }

    /// <summary>A dropped peer stays closed for good, so a new connection needs a new one.</summary>
    private JsonRpcPeer CreatePeer()
    {
        var created = new JsonRpcPeer(Transport, options.RequestTimeout)
        {
            ServerRequestHandler = HandleServerRequest,
            NotificationHandler = HandleNotification,
        };
        created.Closed += OnPeerClosed;
        return created;
    }

    private void WriteInitializeParams(Utf8JsonWriter writer)
    {
        writer.WriteString("protocolVersion", options.ProtocolVersion);
        writer.WritePropertyName("capabilities");
        writer.WriteStartObject();
        writer.WritePropertyName("roots");
        writer.WriteStartObject();
        writer.WriteBoolean("listChanged", false);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WritePropertyName("clientInfo");
        writer.WriteStartObject();
        writer.WriteString("name", options.ClientName);
        writer.WriteString("version", options.ClientVersion);
        writer.WriteEndObject();
    }

    private string? HandleServerRequest(string method, JsonElement parameters)
    {
        switch (method)
        {
            case McpProtocol.Ping:
                return "{}";
            case McpProtocol.RootsList:
                return RootsJson();
            default:
                return null;
        }
    }

    private string RootsJson()
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("roots");
            writer.WriteStartArray();
            foreach (var root in options.Roots)
            {
                writer.WriteStartObject();
                writer.WriteString("uri", root.Uri);
                if (root.Name is { } name)
                {
                    writer.WriteString("name", name);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private void HandleNotification(string method, JsonElement parameters)
    {
        switch (method)
        {
            case McpProtocol.ToolsListChanged:
                ToolsChanged?.Invoke();
                break;
            case McpProtocol.ResourcesListChanged:
                ResourcesChanged?.Invoke();
                break;
            case McpProtocol.PromptsListChanged:
                PromptsChanged?.Invoke();
                break;
        }

        if (NotificationReceived is { } handler)
        {
            var json = parameters.ValueKind == JsonValueKind.Undefined ? "{}" : parameters.GetRawText();
            handler(new McpNotification(method, json));
        }
    }

    private void OnPeerClosed(Exception? error)
    {
        closed = true;
        initialized = false;
        Closed?.Invoke(error);
    }

    private async Task<List<T>> ListAllAsync<T>(
        string method,
        string property,
        Func<JsonElement, T> parse,
        CancellationToken ct)
    {
        var items = new List<T>();
        await foreach (var item in EnumeratePagesAsync(method, property, parse, ct).ConfigureAwait(false))
        {
            items.Add(item);
        }

        return items;
    }

    private async IAsyncEnumerable<T> EnumeratePagesAsync<T>(
        string method,
        string property,
        Func<JsonElement, T> parse,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string? cursor = null;
        for (var page = 0; page < Math.Max(1, options.MaxListPages); page++)
        {
            var requestCursor = cursor;
            using var document = await peer.RequestAsync(method, requestCursor is null
                ? null
                : writer => writer.WriteString("cursor", requestCursor), ct).ConfigureAwait(false);

            var result = ResultOf(document, method);
            if (result.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in array.EnumerateArray())
                {
                    yield return parse(element);
                }
            }

            cursor = ReadString(result, "nextCursor");
            if (string.IsNullOrEmpty(cursor))
            {
                yield break;
            }
        }
    }

    private static JsonElement ResultOf(JsonDocument document, string method)
    {
        if (document.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            return result;
        }

        throw new McpProtocolException(method, McpErrorCodes.InvalidRequest, "the response carried no result object");
    }

    private static McpToolInfo ParseTool(JsonElement element) => new(
        ReadString(element, "name") ?? throw new McpProtocolException(McpProtocol.ToolsList, McpErrorCodes.InvalidParams, "tool without a name"),
        ReadString(element, "description"),
        element.TryGetProperty("inputSchema", out var schema) && schema.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            ? JsonText.Compact(schema.GetRawText())
            : """{"type":"object","properties":{}}""",
        ReadString(element, "title"));

    private static McpResourceInfo ParseResource(JsonElement element) => new(
        ReadString(element, "uri") ?? throw new McpProtocolException(McpProtocol.ResourcesList, McpErrorCodes.InvalidParams, "resource without a uri"),
        ReadString(element, "name"),
        ReadString(element, "description"),
        ReadString(element, "mimeType"),
        ReadSize(element));

    private static long? ReadSize(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("size", out var size))
        {
            return null;
        }

        return size.ValueKind switch
        {
            JsonValueKind.Number => size.TryGetInt64(out var parsed) ? parsed : null,
            JsonValueKind.String => long.TryParse(size.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
            _ => null,
        };
    }

    private static McpPromptInfo ParsePrompt(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new McpProtocolException(McpProtocol.PromptsList, McpErrorCodes.InvalidParams, "prompt without a name");
        }

        IReadOnlyList<McpPromptArgument>? arguments = null;
        if (element.TryGetProperty("arguments", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            var list = new List<McpPromptArgument>(array.GetArrayLength());
            foreach (var argument in array.EnumerateArray())
            {
                list.Add(new McpPromptArgument(
                    ReadString(argument, "name") ?? "argument",
                    ReadString(argument, "description"),
                    argument.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True));
            }

            arguments = list;
        }

        return new McpPromptInfo(
            ReadString(element, "name") ?? throw new McpProtocolException(McpProtocol.PromptsList, McpErrorCodes.InvalidParams, "prompt without a name"),
            ReadString(element, "description"),
            arguments);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
