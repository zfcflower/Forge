using MessagePack;

namespace Forge.Plugin.Sdk;

/// <summary>Extension forms an agent can author. A plugin is C# compiled by the host and run in a worker (L2); a workflow is
/// a declarative <c>workflow.json</c> the host runs with <c>Forge.Workflows</c> and exposes as one tool (L0).</summary>
public static class ExtensionKinds
{
    public const string Plugin = "plugin";
    public const string Workflow = "workflow";
}

/// <summary>A NuGet-style dependency the author declares; it is folded into the dependency hash, so adding one needs approval.</summary>
[MessagePackObject]
public sealed partial record ExtensionDependency([property: Key(0)] string Name, [property: Key(1)] string Version);

/// <summary>The single tool a workflow extension exposes. <see cref="InputSchemaJson"/> is a JSON-schema object text;
/// the call's arguments are bound as <c>$input</c> inside the workflow.</summary>
[MessagePackObject]
public sealed partial record ExtensionToolSpec([property: Key(0)] string Name, [property: Key(1)] string Description, [property: Key(2)] string InputSchemaJson);

/// <summary>
/// <c>extensions.stage</c> arguments. The host writes <see cref="Files"/> into <c>&lt;plugins&gt;/.pending/&lt;id&gt;/</c> and
/// generates <c>manifest.json</c> itself from the structured fields, so an author never writes the manifest directly.
/// <see cref="Files"/> maps a relative path (<c>/</c>-separated) to its full UTF-8 text. A plugin carries exactly one
/// <c>.cs</c> source file; a workflow carries <c>workflow.json</c> and needs <see cref="Tool"/>.
/// </summary>
[MessagePackObject]
public sealed partial record ExtensionStageRequest(
    [property: Key(0)] string PluginId,
    [property: Key(1)] string Version,
    [property: Key(2)] string Kind,
    [property: Key(3)] IReadOnlyDictionary<string, string> Files,
    [property: Key(4)] IReadOnlyList<Permission> Permissions,
    [property: Key(5)] IReadOnlyList<ExtensionDependency> Dependencies,
    [property: Key(6)] ExtensionToolSpec? Tool = null);

/// <summary>One staged file: the new text and, for an update, the text of the live version (null when the file is new).</summary>
[MessagePackObject]
public sealed partial record StagedFile([property: Key(0)] string Path, [property: Key(1)] string Text, [property: Key(2)] string? PreviousText = null);

/// <summary>
/// A staged extension as shown on the approval card. The three hashes are what <c>extensions.commit</c> must echo back:
/// the host recomputes them from the staged bytes and refuses a mismatch. <see cref="Diagnostics"/> holds compile or
/// validation errors; a staged item with diagnostics cannot be committed. <see cref="LiveVersion"/> is the version
/// currently installed under the same id (null for a new extension), so <see cref="IsUpdate"/> is derived from it.
/// </summary>
[MessagePackObject]
public sealed partial record StagedExtension(
    [property: Key(0)] string PluginId,
    [property: Key(1)] string Version,
    [property: Key(2)] string Kind,
    [property: Key(3)] IReadOnlyList<StagedFile> Files,
    [property: Key(4)] IReadOnlyList<Permission> Permissions,
    [property: Key(5)] IReadOnlyList<ExtensionDependency> Dependencies,
    [property: Key(6)] string SourceHash,
    [property: Key(7)] string DependencyHash,
    [property: Key(8)] string SdkVersion,
    [property: Key(9)] IReadOnlyList<string> Diagnostics,
    [property: Key(10)] string? LiveVersion = null,
    [property: Key(11)] ExtensionToolSpec? Tool = null,
    [property: Key(12)] DateTimeOffset? StagedAt = null)
{
    [IgnoreMember]
    public bool IsUpdate => LiveVersion is not null;
}

[MessagePackObject]
public sealed partial record StagedExtensionList([property: Key(0)] IReadOnlyList<StagedExtension> Items);

/// <summary><c>extensions.commit</c>: swaps the staged directory in and approves exactly these hashes.</summary>
[MessagePackObject]
public sealed partial record ExtensionCommitRequest([property: Key(0)] string PluginId, [property: Key(1)] string SourceHash, [property: Key(2)] string DependencyHash, [property: Key(3)] string SdkVersion);

/// <summary>Result of a commit: the plugin's status after approval and the tool names it registered.</summary>
[MessagePackObject]
public sealed partial record ExtensionCommitResult([property: Key(0)] PluginStatusInfo Status, [property: Key(1)] IReadOnlyList<string> ToolNames);

/// <summary><c>extensions.discard</c> arguments.</summary>
[MessagePackObject]
public sealed partial record ExtensionIdRequest([property: Key(0)] string PluginId);

/// <summary><c>plugin.disable</c>/<c>plugin.enable</c>/<c>plugin.delete</c> arguments.</summary>
[MessagePackObject]
public sealed partial record PluginVersionRequest([property: Key(0)] string PluginId, [property: Key(1)] string Version);
