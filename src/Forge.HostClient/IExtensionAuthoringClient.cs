using Forge.Plugin.Sdk;

namespace Forge.HostClient;

/// <summary>
/// Agent-authored extensions over IPC. Staging only writes the host's pending area; nothing staged ever runs until
/// <see cref="CommitExtensionAsync"/> is called with the hashes the user saw on the approval card, and only the main
/// program's approval UI calls that. Every method throws when the client is not running or the host answers with an error
/// (<c>IpcRemoteException</c> carries the host error code such as <c>approval_mismatch</c>); an invalid response throws
/// <see cref="InvalidDataException"/> and caller cancellation propagates as <see cref="OperationCanceledException"/>.
/// </summary>
public interface IExtensionAuthoringClient
{
    /// <summary>Writes a draft to <c>&lt;plugins&gt;/.pending/&lt;id&gt;/</c> (replacing an earlier draft of the same id) and
    /// returns what the approval card shows, including compile or validation diagnostics.</summary>
    Task<StagedExtension> StageExtensionAsync(ExtensionStageRequest request, CancellationToken ct);

    /// <summary>Every staged draft, so pending approvals survive a restart.</summary>
    Task<IReadOnlyList<StagedExtension>> GetPendingExtensionsAsync(CancellationToken ct);

    /// <summary>Swaps the staged draft in and approves exactly these hashes; the live version keeps running until then.</summary>
    Task<ExtensionCommitResult> CommitExtensionAsync(ExtensionCommitRequest request, CancellationToken ct);

    /// <summary>Drops a staged draft; the live version (if any) is untouched.</summary>
    Task DiscardExtensionAsync(string pluginId, CancellationToken ct);

    Task DisablePluginAsync(string pluginId, string version, CancellationToken ct);

    /// <summary>Re-enables a disabled version; it runs again only if its approved hashes still match the bytes on disk.</summary>
    Task EnablePluginAsync(string pluginId, string version, CancellationToken ct);

    /// <summary>Uninstalls a version for good (the same id and version stay blocked) and removes its directory.</summary>
    Task DeletePluginAsync(string pluginId, string version, CancellationToken ct);
}
