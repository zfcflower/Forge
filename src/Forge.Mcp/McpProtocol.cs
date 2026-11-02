namespace Forge.Mcp;

public static class McpProtocol
{
    public const string Version = "2025-06-18";

    public const string Initialize = "initialize";
    public const string Initialized = "notifications/initialized";
    public const string Ping = "ping";
    public const string Cancelled = "notifications/cancelled";
    public const string Progress = "notifications/progress";
    public const string LogMessage = "notifications/message";
    public const string ToolsList = "tools/list";
    public const string ToolsCall = "tools/call";
    public const string ToolsListChanged = "notifications/tools/list_changed";
    public const string ResourcesList = "resources/list";
    public const string ResourcesRead = "resources/read";
    public const string ResourcesListChanged = "notifications/resources/list_changed";
    public const string PromptsList = "prompts/list";
    public const string PromptsGet = "prompts/get";
    public const string PromptsListChanged = "notifications/prompts/list_changed";
    public const string RootsList = "roots/list";
    public const string SamplingCreateMessage = "sampling/createMessage";
    public const string ElicitationCreate = "elicitation/create";

    public const string SessionIdHeader = "Mcp-Session-Id";
    public const string ProtocolVersionHeader = "MCP-Protocol-Version";

    public static string ClientVersion { get; } =
        typeof(McpProtocol).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
