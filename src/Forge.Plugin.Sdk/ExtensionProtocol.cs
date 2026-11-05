namespace Forge.Plugin.Sdk;

/// <summary>IPC operation names and message shapes for the main program ↔ Extension Host ↔ Plugin Worker
/// topology. The wire format is <c>Forge.Ipc.IpcMessage</c> envelopes; <c>op</c> is one of these names,
/// <c>args</c> is a JSON object with the fields documented per operation, <c>result</c>/<c>error</c> carry the response.</summary>
public static class ExtensionProtocol
{
    // Host → main program (notifications)
    public const string ToolsRegister = "tools.register";     // {pluginId, tools:[ToolDeclaration]}
    public const string ToolsUnregister = "tools.unregister"; // {pluginId}

    public const string ContributionsRegister = "contributions.register"; // PluginContributionsNotification; replaces the complete snapshot for this plugin
    public const string ContributionsUnregister = "contributions.unregister"; // {pluginId}; removes every non-tool contribution

    // Main program → Host
    public const string PluginsStatus = "plugins.status"; // {} → PluginStatusResponse; no direct database access by the main program
    public const string CodeExecute = "code.execute"; // CodeExecutionRequest → CodeExecutionResult
    public const string PluginCommand = "plugin.command"; // CommandExecutionRequest → CommandOutput
    public const string PluginPrompt = "plugin.prompt"; // PromptExecutionRequest → PromptOutput
    public const string PluginExecute = "plugin.execute";     // {pluginId, tool, args, sessionId, workingDir, depth} → {content, isError}
    public const string PluginApprove = "plugin.approve";     // {pluginId, version, sourceHash, dependencyHash, sdkVersion} → {}
    public const string PluginRevoke = "plugin.revoke";       // {pluginId, version} → {}
    public const string PluginsRefresh = "plugins.refresh";   // {} → {}
    public const string HostStop = "host.stop";               // {} → {}

    // Worker/plugin → Host → main program (request: a plugin calls a main-program tool)
    public const string HostToolCall = "host.tool_call";      // {name, args, sessionId, workingDir, depth} → {content, isError}; context is the originating plugin invocation's, never a shared field

    // Host → Worker
    public const string WorkerCommand = "worker.command"; // CommandExecutionRequest → CommandOutput
    public const string WorkerPrompt = "worker.prompt"; // PromptExecutionRequest → PromptOutput
    public const string WorkerLoad = "worker.load";           // {pluginId, version, assemblyPath, assemblySha256, files:{relativeName: sha256}} → {tools:[ToolDeclaration]}; the worker must load exactly the bytes that match assemblySha256 and reject any file of the plugin directory not listed or not matching
    public const string WorkerExecute = "worker.execute";     // {pluginId, tool, args, sessionId, workingDir, depth, hostPipeName} → {content, isError}
    public const string WorkerUnload = "worker.unload";       // {pluginId} → {}
    public const string WorkerStop = "worker.stop";           // {} → {}
}
