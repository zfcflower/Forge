using System.Text.Json.Serialization;

namespace Forge.Plugin.Sdk;

/// <summary>Extension forms an agent can author. A plugin is C# compiled by the host and run in a worker (L2); a workflow is
/// a declarative <c>workflow.json</c> the host runs with <c>Forge.Workflows</c> and exposes as one tool (L0).</summary>
public static class ExtensionKinds
{
    public const string Plugin = "plugin";
    public const string Workflow = "workflow";
}

/// <summary>A NuGet-style dependency the author declares; it is folded into the dependency hash, so adding one needs approval.</summary>
public sealed record ExtensionDependency(string Name, string Version);

/// <summary>The single tool a workflow extension exposes. <see cref="InputSchemaJson"/> is a JSON-schema object text;
/// the call's arguments are bound as <c>$input</c> inside the workflow.</summary>
public sealed record ExtensionToolSpec(string Name, string Description, string InputSchemaJson);

/// <summary>
/// <c>extensions.stage</c> arguments. The host writes <see cref="Files"/> into <c>&lt;plugins&gt;/.pending/&lt;id&gt;/</c> and
/// generates <c>manifest.json</c> itself from the structured fields, so an author never writes the manifest directly.
/// <see cref="Files"/> maps a relative path (<c>/</c>-separated) to its full UTF-8 text. A plugin carries exactly one
/// <c>.cs</c> source file; a workflow carries <c>workflow.json</c> and needs <see cref="Tool"/>.
/// </summary>
public sealed record ExtensionStageRequest(
    string PluginId,
    string Version,
    string Kind,
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<Permission> Permissions,
    IReadOnlyList<ExtensionDependency> Dependencies,
    ExtensionToolSpec? Tool = null);

/// <summary>One staged file: the new text and, for an update, the text of the live version (null when the file is new).</summary>
public sealed record StagedFile(string Path, string Text, string? PreviousText = null);

/// <summary>
/// A staged extension as shown on the approval card. The three hashes are what <c>extensions.commit</c> must echo back:
/// the host recomputes them from the staged bytes and refuses a mismatch. <see cref="Diagnostics"/> holds compile or
/// validation errors; a staged item with diagnostics cannot be committed. <see cref="LiveVersion"/> is the version
/// currently installed under the same id (null for a new extension), so <see cref="IsUpdate"/> is derived from it.
/// </summary>
public sealed record StagedExtension(
    string PluginId,
    string Version,
    string Kind,
    IReadOnlyList<StagedFile> Files,
    IReadOnlyList<Permission> Permissions,
    IReadOnlyList<ExtensionDependency> Dependencies,
    string SourceHash,
    string DependencyHash,
    string SdkVersion,
    IReadOnlyList<string> Diagnostics,
    string? LiveVersion = null,
    ExtensionToolSpec? Tool = null,
    DateTimeOffset? StagedAt = null)
{
    public bool IsUpdate => LiveVersion is not null;
}

public sealed record StagedExtensionList(IReadOnlyList<StagedExtension> Items);

/// <summary><c>extensions.commit</c>: swaps the staged directory in and approves exactly these hashes.</summary>
public sealed record ExtensionCommitRequest(string PluginId, string SourceHash, string DependencyHash, string SdkVersion);

/// <summary>Result of a commit: the plugin's status after approval and the tool names it registered.</summary>
public sealed record ExtensionCommitResult(PluginStatusInfo Status, IReadOnlyList<string> ToolNames);

/// <summary><c>extensions.discard</c> arguments.</summary>
public sealed record ExtensionIdRequest(string PluginId);

/// <summary><c>plugin.disable</c>/<c>plugin.enable</c>/<c>plugin.delete</c> arguments.</summary>
public sealed record PluginVersionRequest(string PluginId, string Version);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ExtensionStageRequest))]
[JsonSerializable(typeof(StagedExtension))]
[JsonSerializable(typeof(StagedExtensionList))]
[JsonSerializable(typeof(ExtensionCommitRequest))]
[JsonSerializable(typeof(ExtensionCommitResult))]
[JsonSerializable(typeof(ExtensionIdRequest))]
[JsonSerializable(typeof(PluginVersionRequest))]
public partial class ExtensionAuthoringJsonContext : JsonSerializerContext
{
}
