namespace Forge.Plugin.Sdk;

/// <summary>IPC operation names for the main program ↔ Extension Host ↔ Plugin Worker topology. The wire format is
/// <c>Forge.Ipc</c> MessagePack frames; each comment names the argument → result payload types (SDK types here, the
/// host-internal ones in <c>Forge.Ipc.Protocol</c>).</summary>
public static class ExtensionProtocol
{
    // Host → main program (notifications)
    public const string ToolsRegister = "tools.register";     // ToolsRegisterNotification
    public const string ToolsUnregister = "tools.unregister"; // ExtensionIdRequest

    public const string ContributionsRegister = "contributions.register"; // PluginContributionsNotification; replaces the complete snapshot for this plugin
    public const string ContributionsUnregister = "contributions.unregister"; // ExtensionIdRequest; removes every non-tool contribution

    // Main program → Host
    public const string PluginsStatus = "plugins.status"; // IpcEmpty → PluginStatusResponse; no direct database access by the main program
    public const string CodeExecute = "code.execute"; // CodeExecutionRequest → CodeExecutionResult
    public const string PluginCommand = "plugin.command"; // CommandExecutionRequest → CommandOutput
    public const string PluginPrompt = "plugin.prompt"; // PromptExecutionRequest → PromptOutput
    public const string PluginExecute = "plugin.execute";     // PluginExecuteRequest → ToolResult
    public const string PluginApprove = "plugin.approve";     // PluginApproveRequest → IpcEmpty
    public const string PluginRevoke = "plugin.revoke";       // PluginVersionRequest → IpcEmpty
    public const string PluginsRefresh = "plugins.refresh";   // IpcEmpty → IpcEmpty
    public const string HostStop = "host.stop";               // IpcEmpty → IpcEmpty

    // Main program → Host: agent-authored extensions (ExtensionAuthoring.cs). Staged content lives in <plugins>/.pending/<id>/,
    // is never loaded, and only becomes live through extensions.commit with the hashes the user approved.
    public const string ExtensionsStage = "extensions.stage";     // ExtensionStageRequest → StagedExtension
    public const string ExtensionsPending = "extensions.pending"; // IpcEmpty → StagedExtensionList
    public const string ExtensionsCommit = "extensions.commit";   // ExtensionCommitRequest → ExtensionCommitResult; approval_mismatch when the staged bytes differ
    public const string ExtensionsDiscard = "extensions.discard"; // ExtensionIdRequest → IpcEmpty
    public const string PluginDisable = "plugin.disable";         // PluginVersionRequest → IpcEmpty; unregisters tools and stops the worker
    public const string PluginEnable = "plugin.enable";           // PluginVersionRequest → IpcEmpty; runs again only if the approved hashes still match
    public const string PluginDelete = "plugin.delete";           // PluginVersionRequest → IpcEmpty; marks the version uninstalled (blocked for good) and removes the directory

    // Worker/plugin → Host → main program (request: a plugin calls a main-program tool)
    public const string HostToolCall = "host.tool_call";      // HostToolCallRequest (worker/executor → host) or HostToolCallForward (host → main program) → ToolResult; the context is the bound run's, never a worker-supplied field

    // Host → Worker
    public const string WorkerCommand = "worker.command"; // CommandExecutionRequest → CommandOutput
    public const string WorkerPrompt = "worker.prompt"; // PromptExecutionRequest → PromptOutput
    public const string WorkerLoad = "worker.load";           // WorkerLoadRequest → WorkerLoadResponse
    public const string WorkerExecute = "worker.execute";     // WorkerExecuteRequest → ToolResult
    public const string WorkerUnload = "worker.unload";       // ExtensionIdRequest → IpcEmpty
    public const string WorkerStop = "worker.stop";           // IpcEmpty → IpcEmpty
}
