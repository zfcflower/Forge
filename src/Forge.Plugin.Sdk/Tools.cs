using System.Text.Json;
using MessagePack;

namespace Forge.Plugin.Sdk;

public enum ToolSideEffect { ReadOnly, Write, Execute }

/// <summary>Static metadata for a plugin tool. The input schema is passed through to the provider verbatim.</summary>
[MessagePackObject]
public sealed partial record ToolDeclaration([property: Key(0)] string Name, [property: Key(1)] string Description, [property: Key(2)] string InputSchemaJson, [property: Key(3)] ToolSideEffect SideEffect);

/// <summary>Result of a tool execution.</summary>
[MessagePackObject]
public sealed partial record ToolResult([property: Key(0)] string Content, [property: Key(1)] bool IsError = false);

/// <summary>Execution context handed to a plugin tool; <see cref="Host"/> is the only way to reach
/// main-program tools (routed over IPC, never in-process).</summary>
public sealed record ToolInvocation(
    string SessionId,
    string WorkingDirectory,
    JsonElement Arguments,
    IHostFacade Host,
    ExtensionCallContext? Context = null);

/// <summary>A plugin tool: metadata plus its execution body, run inside the Worker process.</summary>
public interface IPluginTool
{
    ToolDeclaration Declaration { get; }
    Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct);
}

/// <summary>A result returned by a main-program tool called through <see cref="IHostFacade"/>.</summary>
public sealed record HostToolResult(string Content, bool IsError = false);

/// <summary>Facade for a plugin to call main-program tools. Every call is permission-checked and
/// approved by the main program; the plugin process never touches the registry directly.</summary>
public interface IHostFacade
{
    Task<HostToolResult> CallToolAsync(string name, JsonElement arguments, CancellationToken ct);
}

/// <summary>A capability the plugin asks the user to grant; shown at approval time.</summary>
[MessagePackObject]
public sealed partial record Permission([property: Key(0)] PermissionKind Kind, [property: Key(1)] string? Scope = null);

public enum PermissionKind { ReadFile, WriteFile, ExecuteCommand, Network, Database }
