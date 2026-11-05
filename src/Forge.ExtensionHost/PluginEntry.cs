using Forge.Approval;
using Forge.Plugin.Sdk;
using Forge.Workflows;

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
    LegacyPluginPackage,
    IdMismatch,
    DuplicateId,
    SourceMissing,
    CompileFailed,
    EntryAssemblyMissing,
    LoadFailed,
    InvalidPackage,
    WorkflowInvalid,
}

/// <summary>A non-fatal plugin problem.</summary>
internal sealed record PluginIssue(string PluginId, PluginIssueKind Kind, string Message, string? Directory = null)
{
    public override string ToString() => Kind + (string.IsNullOrEmpty(PluginId) ? ": " : $" [{PluginId}]: ") + Message;
}

/// <summary>
/// The bytes a plugin is loaded from: the entry assembly plus every file of its loadable directory, each identified by
/// the SHA-256 of the bytes that were read. <see cref="Files"/> is keyed by the file name relative to
/// <see cref="Directory"/> and is what travels to the worker in <c>worker.load</c>.
/// </summary>
internal sealed record PluginImage(
    string Directory,
    string AssemblyFileName,
    string AssemblySha256,
    IReadOnlyDictionary<string, string> Files)
{
    public string AssemblyPath => Path.Combine(Directory, AssemblyFileName);
}

/// <summary>What the approval is evaluated against: the image on disk now (null for a workflow package, which loads no
/// assembly) and the two hashes derived from it.</summary>
internal sealed record PluginContent(PluginImage? Image, string SourceHash, string DependencyHash);

/// <summary>One approval evaluation plus the content it was evaluated against, so a caller can dispatch exactly the
/// bytes it checked.</summary>
internal sealed record PluginEvaluation(
    ApprovalDecision Decision,
    PluginImage? Image,
    string SourceHash,
    string DependencyHash);

/// <summary>
/// Discovery result for one plugin directory plus the runtime state the host keeps for it. The discovery fields are
/// immutable snapshots of what is on disk; <see cref="Decision"/>, <see cref="Image"/> and <see cref="Tools"/>
/// change as approvals and workers do.
/// </summary>
internal sealed class PluginEntry(PluginManifest manifest)
{
    public PluginManifest Manifest { get; } = manifest;

    public string Id => Manifest.Id;

    public string Version => Manifest.Version;

    public string Directory => Manifest.Directory;

    /// <summary>True when this package is a declarative workflow the host runs itself, not C# compiled into an assembly.</summary>
    public bool IsWorkflow => Manifest.IsWorkflow;

    /// <summary>The parsed workflow of a workflow package; null when it is a plugin or the document failed to parse.</summary>
    public WorkflowDefinition? Workflow { get; set; }

    /// <summary>
    /// The one tool a workflow package publishes. A workflow has no assembly and no worker, so this declaration is all the
    /// main program ever sees; a plugin gets its declarations from <c>worker.load</c> instead.
    /// </summary>
    public IReadOnlyList<ToolDeclaration> WorkflowTools => Manifest.Tool is { } tool
        ? [new ToolDeclaration(tool.Name, tool.Description, tool.InputSchemaJson, ToolSideEffect.Execute)]
        : [];

    /// <summary>The contribution set a workflow package publishes: the tool plus the declared permissions, nothing else.</summary>
    public PluginContributionSet WorkflowContributions => new()
    {
        Tools = WorkflowTools,
        Permissions = [.. Manifest.Permissions],
    };

    public List<PluginIssue> Issues { get; } = [];

    /// <summary>Hash the approval covers: the normalized source, or the assembly bytes for a prebuilt plugin.</summary>
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>Manifest dependencies folded together with the digests of <see cref="Image"/>'s files, so replacing any
    /// file the plugin loads invalidates the approval.</summary>
    public string DependencyHash { get; set; } = string.Empty;

    public string SdkVersion { get; set; } = string.Empty;

    /// <summary>File <see cref="SourceHash"/> was computed from; <c>null</c> when the source is inline.</summary>
    public string? ContentPath { get; set; }

    /// <summary>Inline source text from the manifest, kept so the hash can be recomputed per execution.</summary>
    public string? InlineSource { get; set; }

    public bool ContentIsBinary { get; set; }

    /// <summary>The bytes this plugin is loaded from, hashed at discovery and re-hashed before every load.</summary>
    public PluginImage? Image { get; set; }

    public string? AssemblyPath => Image?.AssemblyPath;

    public ApprovalDecision Decision { get; set; } = ApprovalDecision.Block("the plugin has not been evaluated");

    public IReadOnlyList<ToolDeclaration> Tools { get; set; } = [];

    /// <summary>The complete non-tool contribution set the loaded worker reported for this plugin.</summary>
    public PluginContributionSet Contributions { get; set; } = new();

    public bool HasContent => SourceHash.Length > 0 && (Image is not null || (IsWorkflow && Workflow is not null));

    public bool IsRunnable => HasContent && Decision.IsAllowed;
}
