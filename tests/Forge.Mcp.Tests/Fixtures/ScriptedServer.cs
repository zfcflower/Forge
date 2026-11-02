using System.Text.Json;
using System.Collections.Concurrent;

namespace Forge.Mcp.Tests.Fixtures;

/// <summary>Answers client requests by method name; notifications and responses get no reply.</summary>
internal sealed class ScriptedServer
{
    public const string InitializeResultJson =
        """{"protocolVersion":"2025-06-18","capabilities":{"tools":{"listChanged":true}},"serverInfo":{"name":"fake","version":"1.2.3"}}""";

    public const string ToolsListResultJson =
        """{"tools":[{"name":"echo","description":"Echoes text","inputSchema":{"type":"object","properties":{"text":{"type":"string"}}}}]}""";

    private readonly Dictionary<string, Func<JsonElement, string?>> handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> methods = new();

    public ScriptedServer()
    {
        On(McpProtocol.Initialize, _ => InitializeResultJson);
        On(McpProtocol.ToolsList, _ => ToolsListResultJson);
        On(McpProtocol.ToolsCall, root => """{"content":[{"type":"text","text":"ok"}]}""");
        On(McpProtocol.Ping, _ => JsonRpc.EmptyObject);
    }

    public IReadOnlyList<string> RequestMethods => methods.ToArray();

    public ScriptedServer On(string method, Func<JsonElement, string?> handler)
    {
        handlers[method] = handler;
        return this;
    }

    public IReadOnlyList<string> Respond(JsonElement root)
    {
        if (!root.TryGetProperty("id", out _) || !root.TryGetProperty("method", out var methodElement))
        {
            return [];
        }

        var method = methodElement.GetString() ?? string.Empty;
        methods.Enqueue(method);
        var id = JsonRpc.IdOf(root);
        if (!handlers.TryGetValue(method, out var handler))
        {
            return [JsonRpc.Error(id, McpErrorCodes.MethodNotFound, "Method not found: " + method)];
        }

        try
        {
            var result = handler(root);
            return result is null ? [] : [JsonRpc.Result(id, result)];
        }
        catch (ScriptedRpcError error)
        {
            return [JsonRpc.Error(id, error.Code, error.Message, error.DataJson)];
        }
    }
}

internal sealed class ScriptedRpcError(int code, string message, string? dataJson = null) : Exception(message)
{
    public int Code { get; } = code;

    public string? DataJson { get; } = dataJson;
}
