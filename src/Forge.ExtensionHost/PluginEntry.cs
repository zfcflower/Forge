using Forge.Approval;
using Forge.Plugin.Sdk;

namespace Forge.ExtensionHost;

/// <summary>Why a plugin could not be made runnable. Issues never stop the host: they are kept on the entry, logged to
/// stderr, and recomputed by the next refresh.</summary>
internal enum PluginIssueKind
{
    PluginsRootMissing = 0,
    PluginsRootUnreadable,
    DirectoryNameInvalid,
    ManifestMissing,
    ManifestInvalid,
    IdMismatch,
    DuplicateId,
    SourceMissing,
    CompileFailed,
    EntryAssemblyMissing,
    LoadFailed,
}

/// <summary>A non-fatal plugin problem.</summary>
internal sealed record PluginIssue(string PluginId, PluginIssueKind Kind, string Message, string? Directory = null)
{
    public override string ToString() => Kind + (string.IsNullOrEmpty(PluginId) ? ": " : $" [{PluginId}]: ") + Message;
}

/// <summary>
/// Discovery result for one plugin directory plus the runtime state the host keeps for it. The discovery fields are
/// immutable snapshots of what is on disk; <see cref="Decision"/>, <see cref="AssemblyPath"/> and <see cref="Tools"/>
/// change as approvals and workers do.
/// </summary>
internal sealed class PluginEntry(PluginManifest manifest)
{
    public PluginManifest Manifest { get; } = manifest;

    public string Id => Manifest.Id;

    public string Version => Manifest.Version;

    public string Directory => Manifest.Directory;

    public List<PluginIssue> Issues { get; } = [];

    /// <summary>Hash the approval covers: the normalized source, or the assembly bytes for a prebuilt plugin.</summary>
    public string SourceHash { get; set; } = string.Empty;

    public string DependencyHash { get; set; } = string.Empty;

    public string SdkVersion { get; set; } = string.Empty;

    /// <summary>File <see cref="SourceHash"/> was computed from; <c>null</c> when the source is inline.</summary>
    public string? ContentPath { get; set; }

    public bool ContentIsBinary { get; set; }

    public string? AssemblyPath { get; set; }

    public ApprovalDecision Decision { get; set; } = ApprovalDecision.Block("the plugin has not been evaluated");

    public IReadOnlyList<ToolDeclaration> Tools { get; set; } = [];

    public bool HasContent => AssemblyPath is not null && SourceHash.Length > 0;

    public bool IsRunnable => HasContent && Decision.IsAllowed;
}
