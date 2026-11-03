namespace Forge.Plugin.Sdk;

/// <summary>IPC operation names and message shapes for the main program ↔ Extension Host ↔ Plugin Worker
/// topology. The wire format is <c>Forge.Ipc.IpcMessage</c> envelopes; <c>op</c> is one of these names,
/// <c>args</c> is a JSON object with the fields documented per operation, <c>result</c>/<c>error</c> carry the response.</summary>
public static class ExtensionProtocol
{
    // Host → main program (notifications)
    public const string ToolsRegister = "tools.register";     // {pluginId, tools:[ToolDeclaration]}
    public const string ToolsUnregister = "tools.unregister"; // {pluginId}

    // Main program → Host
    public const string PluginExecute = "plugin.execute";     // {pluginId, tool, args, sessionId, workingDir, depth} → {content, isError}
    public const string PluginApprove = "plugin.approve";     // {pluginId, version, sourceHash, dependencyHash, sdkVersion} → {}
    public const string PluginRevoke = "plugin.revoke";       // {pluginId, version} → {}
    public const string PluginsRefresh = "plugins.refresh";   // {} → {}
    public const string HostStop = "host.stop";               // {} → {}

    // Worker/plugin → Host → main program (request: a plugin calls a main-program tool)
    public const string HostToolCall = "host.tool_call";      // {name, args} → {content, isError}

    // Host → Worker
    public const string WorkerLoad = "worker.load";           // {pluginId, version, assemblyPath} → {tools:[ToolDeclaration]}
    public const string WorkerExecute = "worker.execute";     // {pluginId, tool, args, sessionId, workingDir, hostPipeName} → {content, isError}
    public const string WorkerUnload = "worker.unload";       // {pluginId} → {}
    public const string WorkerStop = "worker.stop";           // {} → {}
}
