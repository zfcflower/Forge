using System.Text.Json;

namespace Forge.Mcp.Tests.Fixtures;

internal static class JsonRpc
{
    public const string EmptyObject = "{}";

    public static string Result(string idJson, string resultJson) =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"result\":{resultJson}}}";

    public static string Error(string idJson, int code, string message, string? dataJson = null) =>
        dataJson is null
            ? $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"error\":{{\"code\":{code},\"message\":\"{message}\"}}}}"
            : $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"error\":{{\"code\":{code},\"message\":\"{message}\",\"data\":{dataJson}}}}}";

    public static string Notification(string method, string? paramsJson = null) =>
        paramsJson is null
            ? $"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\"}}"
            : $"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\",\"params\":{paramsJson}}}";

    public static string Request(string idJson, string method, string? paramsJson = null) =>
        paramsJson is null
            ? $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"method\":\"{method}\"}}"
            : $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"method\":\"{method}\",\"params\":{paramsJson}}}";

    public static string IdOf(JsonElement root) => root.GetProperty("id").GetRawText();

    public static string? MethodOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("method", out var method) ? method.GetString() : null;
    }

    public static JsonElement ParamsOf(JsonElement root) =>
        root.TryGetProperty("params", out var parameters) ? parameters : default;
}
