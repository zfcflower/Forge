namespace Forge.HostClient;

/// <summary>Tuning for an <see cref="ExtensionHostClient"/>. Everything here is optional; the defaults match
/// <see cref="Forge.Ipc.IpcPipeOptions"/> and <see cref="Forge.Ipc.IpcPeerOptions"/>.</summary>
public sealed record ExtensionHostClientOptions
{
    /// <summary>Passed to the host as <c>--plugins &lt;dir&gt;</c>; omitted when null or empty.</summary>
    public string? PluginsDirectory { get; init; }

    /// <summary>Passed to the host as <c>--data &lt;dir&gt;</c>; omitted when null or empty.</summary>
    public string? DataDirectory { get; init; }

    public Forge.Ipc.IpcPipeOptions PipeOptions { get; init; } = new();

    /// <summary>Deadline for one request/response round trip (tool execution included); null or non-positive
    /// means no deadline.</summary>
    public TimeSpan? RequestTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>How long <c>host.stop</c> may take before the process is torn down anyway.</summary>
    public TimeSpan StopRequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a stopped host process has to exit on its own before its process tree is killed.</summary>
    public TimeSpan ProcessExitTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Working directory used for <c>host.tool_call</c> relays only when the request carries no
    /// <c>workingDir</c> at all (an old host); null means the main program's current directory. It must be fully
    /// qualified, otherwise relayed calls fail closed instead of running in it.</summary>
    public string? HostToolWorkingDirectory { get; init; }
}
