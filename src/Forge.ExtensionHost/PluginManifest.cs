using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Approval;
using Forge.Plugin.Sdk;
using Forge.Scripting;

namespace Forge.ExtensionHost;

/// <summary>
/// <c>&lt;plugins&gt;/&lt;id&gt;/manifest.json</c>: the only metadata the host reads from a plugin package.
/// <see cref="EntryAssembly"/> is a file name inside the plugin's output directory, <see cref="Source"/> is either a
/// path relative to the plugin directory or inline C# source, and <see cref="Dependencies"/> feeds
/// <see cref="Hashing.DependencyHash"/>. <see cref="Kind"/> is <c>plugin</c> (the default: C# compiled into an assembly)
/// or <c>workflow</c> (a declarative document <see cref="WorkflowFile"/> the host runs itself through
/// <c>Forge.Workflows</c> and exposes with <see cref="Tool"/>).
/// </summary>
internal sealed record PluginManifest(
    string Id,
    string Version,
    string EntryAssembly,
    string? Source,
    IReadOnlyList<Dependency> Dependencies,
    IReadOnlyList<Permission> Permissions,
    string Directory,
    string Kind = ExtensionKinds.Plugin,
    string WorkflowFile = PluginManifestReader.DefaultWorkflowFileName,
    ExtensionToolSpec? Tool = null)
{
    /// <summary>True when the package is a declarative workflow rather than C# compiled by the host.</summary>
    public bool IsWorkflow => string.Equals(Kind, ExtensionKinds.Workflow, StringComparison.Ordinal);

    /// <summary>File name of the compiled image inside a version directory.</summary>
    public string OutputFileName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(EntryAssembly))
            {
                return VersionedOutputStore.DefaultFileName;
            }

            var name = Path.GetFileName(EntryAssembly);
            return string.IsNullOrWhiteSpace(name) ? VersionedOutputStore.DefaultFileName : name;
        }
    }
}

internal sealed class PluginManifestDocument
{
    public string? Id { get; set; }

    public string? Version { get; set; }

    /// <summary><c>plugin</c> (default) or <c>workflow</c>.</summary>
    public string? Kind { get; set; }

    /// <summary>Workflow document file name, relative to the package directory; only meaningful for kind <c>workflow</c>.</summary>
    public string? Workflow { get; set; }

    /// <summary>The single tool a workflow package exposes.</summary>
    public PluginToolDocument? Tool { get; set; }

    public string? EntryAssembly { get; set; }

    public string? Source { get; set; }

    public List<DependencyDocument>? Dependencies { get; set; }

    /// <summary>Permissions the plugin asks the user to grant; shown before approval and folded into the approval hash.</summary>
    public List<PermissionDocument>? Permissions { get; set; }
}

/// <summary>The tool a workflow package exposes: its name, description and JSON-schema input.</summary>
internal sealed class PluginToolDocument
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Schema as a JSON object, or its text (both are accepted; the host stores and reports it as text).</summary>
    public JsonElement? InputSchema { get; set; }
}

internal sealed class DependencyDocument
{
    public string? Name { get; set; }

    public string? Version { get; set; }
}

internal sealed class PermissionDocument
{
    public JsonElement? Kind { get; set; }

    public string? Scope { get; set; }
}

/// <summary>Reads <c>manifest.json</c>. Every failure is reported through the returned issue kind instead of throwing.</summary>
internal static class PluginManifestReader
{
    public const string FileName = "manifest.json";

    public const int MaxManifestBytes = 64 * 1024;

    /// <summary>Workflow document file name a workflow package uses when its manifest does not name one.</summary>
    public const string DefaultWorkflowFileName = "workflow.json";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 16,
    };

    public static bool TryRead(
        string directory,
        out PluginManifest manifest,
        out PluginIssueKind kind,
        out string error)
    {
        manifest = null!;
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            kind = PluginIssueKind.ManifestMissing;
            error = $"Missing {FileName} in '{directory}'.";
            return false;
        }

        try
        {
            if (new FileInfo(path).Length > MaxManifestBytes)
            {
                kind = PluginIssueKind.ManifestInvalid;
                error = $"'{path}' is larger than {MaxManifestBytes} bytes.";
                return false;
            }

            var document = JsonSerializer.Deserialize<PluginManifestDocument>(File.ReadAllText(path), HostJson.Options);
            if (document is null)
            {
                kind = PluginIssueKind.ManifestInvalid;
                error = $"'{path}' is empty.";
                return false;
            }

            var id = document.Id?.Trim() ?? string.Empty;
            var version = document.Version?.Trim() ?? string.Empty;
            if (!PluginIds.IsSafeSegment(id) || !PluginIds.IsSafeSegment(version))
            {
                kind = PluginIssueKind.ManifestInvalid;
                error = $"'{path}' must declare a usable id and version (1-{PluginIds.MaxSegmentLength} characters, no path separators, no invalid file-name characters, no '..').";
                return false;
            }

            var entryAssembly = document.EntryAssembly?.Trim() ?? string.Empty;
            if (entryAssembly.Length > 0 && entryAssembly.IndexOfAny(['/', '\\']) >= 0)
            {
                kind = PluginIssueKind.ManifestInvalid;
                error = $"'{path}' must declare entryAssembly as a file name, not a path.";
                return false;
            }

            if (!TryReadKind(path, document, out var packageKind, out var workflowFile, out var tool, out var kindError))
            {
                kind = PluginIssueKind.ManifestInvalid;
                error = kindError;
                return false;
            }

            var dependencies = new List<Dependency>(document.Dependencies?.Count ?? 0);
            if (document.Dependencies is { } declared)
            {
                foreach (var dependency in declared)
                {
                    var name = dependency?.Name?.Trim();
                    var dependencyVersion = dependency?.Version?.Trim() ?? string.Empty;
                    if (string.IsNullOrEmpty(name))
                    {
                        kind = PluginIssueKind.ManifestInvalid;
                        error = $"'{path}' declares a dependency without a name.";
                        return false;
                    }

                    // An empty version means "unpinned" and stays a legitimate value: the dependency hash frames name and
                    // version separately, so accepting arbitrary text here cannot make two different declarations collide.
                    dependencies.Add(new Dependency(name, dependencyVersion));
                }
            }

            if (!TryReadPermissions(path, document.Permissions, out var permissions, out var permissionError))
            {
                kind = PluginIssueKind.ManifestInvalid;
                error = permissionError;
                return false;
            }

            manifest = new PluginManifest(
                id,
                version,
                entryAssembly,
                document.Source,
                dependencies.AsReadOnly(),
                permissions,
                Path.GetFullPath(directory),
                packageKind,
                workflowFile,
                tool);
            kind = default;
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            kind = PluginIssueKind.ManifestInvalid;
            error = $"'{path}' could not be read: {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// Reads the package kind. A missing <c>kind</c> is a <c>plugin</c>, so every package written before workflows existed
    /// keeps loading unchanged. A workflow package must name its document (a single safe file name, default
    /// <c>workflow.json</c>) and declare one usable tool: the host publishes that tool and binds the call's arguments as
    /// <c>$input</c>, so a nameless tool or a schema that is not a JSON object is a manifest error rather than a guess.
    /// </summary>
    private static bool TryReadKind(
        string path,
        PluginManifestDocument document,
        out string kind,
        out string workflowFile,
        out ExtensionToolSpec? tool,
        out string error)
    {
        workflowFile = DefaultWorkflowFileName;
        tool = null;
        error = string.Empty;

        var declared = document.Kind?.Trim();
        if (string.IsNullOrEmpty(declared))
        {
            kind = ExtensionKinds.Plugin;
            if (document.Workflow is not null || document.Tool is not null)
            {
                error = $"'{path}' declares a workflow or a tool but no kind; a workflow package must declare kind 'workflow'.";
                return false;
            }

            return true;
        }

        if (string.Equals(declared, ExtensionKinds.Plugin, StringComparison.OrdinalIgnoreCase))
        {
            kind = ExtensionKinds.Plugin;
            if (document.Workflow is not null || document.Tool is not null)
            {
                error = $"'{path}' declares kind 'plugin' but also a workflow or a tool; a workflow package must declare kind 'workflow'.";
                return false;
            }

            return true;
        }

        if (!string.Equals(declared, ExtensionKinds.Workflow, StringComparison.OrdinalIgnoreCase))
        {
            kind = ExtensionKinds.Plugin;
            error = $"'{path}' declares kind '{declared}'; the host implements '{ExtensionKinds.Plugin}' and '{ExtensionKinds.Workflow}'.";
            return false;
        }

        kind = ExtensionKinds.Workflow;
        var declaredFile = document.Workflow?.Trim();
        if (!string.IsNullOrEmpty(declaredFile))
        {
            if (!PluginIds.IsSafeSegment(declaredFile))
            {
                error = $"'{path}' declares workflow '{declaredFile}'; it must be a single file name with no path separators.";
                return false;
            }

            workflowFile = declaredFile;
        }

        if (document.Tool is not { } toolDocument)
        {
            error = $"'{path}' is a workflow package but declares no tool.";
            return false;
        }

        var name = toolDocument.Name?.Trim();
        if (string.IsNullOrEmpty(name) || !PluginIds.IsSafeToolName(name))
        {
            error = $"'{path}' declares workflow tool name '{toolDocument.Name}', which is not a usable tool name.";
            return false;
        }

        if (!TryReadInputSchema(toolDocument.InputSchema, out var schema))
        {
            error = $"'{path}' declares tool '{name}' with an input schema that is not a JSON object.";
            return false;
        }

        tool = new ExtensionToolSpec(name, toolDocument.Description ?? string.Empty, schema);
        return true;
    }

    /// <summary>Accepts the schema either as an embedded JSON object or as the text of one; stores it as text.</summary>
    private static bool TryReadInputSchema(JsonElement? element, out string schema)
    {
        schema = string.Empty;
        if (element is not { } value)
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            schema = value.GetRawText();
            return true;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            schema = text;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads <c>permissions</c>: each entry is <c>{kind, scope?}</c>, where <c>kind</c> is a defined
    /// <see cref="PermissionKind"/> name or its integer ordinal. An unknown kind, an empty scope or a duplicate entry is a
    /// manifest error - the host never guesses a permission the user was not shown. Permissions are metadata only: the
    /// host captures them before approval and never runs plugin code to discover them.
    /// </summary>
    private static bool TryReadPermissions(
        string path,
        List<PermissionDocument>? documents,
        out IReadOnlyList<Permission> permissions,
        out string error)
    {
        permissions = [];
        error = string.Empty;
        if (documents is null || documents.Count == 0)
        {
            return true;
        }

        var parsed = new List<Permission>(documents.Count);
        var seen = new HashSet<(PermissionKind Kind, string Scope)>();
        foreach (var document in documents)
        {
            if (document?.Kind is not { } kindElement)
            {
                error = $"'{path}' declares a permission without a kind.";
                return false;
            }

            PermissionKind permissionKind;
            if (kindElement.ValueKind == JsonValueKind.Number &&
                kindElement.TryGetInt32(out var ordinal) &&
                ordinal >= 0 &&
                ordinal <= (int)PermissionKind.Database)
            {
                permissionKind = (PermissionKind)ordinal;
            }
            else if (kindElement.ValueKind == JsonValueKind.String &&
                Enum.TryParse<PermissionKind>(kindElement.GetString(), ignoreCase: true, out var named) &&
                Enum.IsDefined(named))
            {
                permissionKind = named;
            }
            else
            {
                error = $"'{path}' declares permission kind '{kindElement}' which is not one of {string.Join(", ", Enum.GetNames<PermissionKind>())}.";
                return false;
            }

            var scope = document.Scope?.Trim();
            if (scope is { Length: 0 })
            {
                error = $"'{path}' declares permission '{permissionKind}' with an empty scope.";
                return false;
            }

            if (!seen.Add((permissionKind, scope ?? string.Empty)))
            {
                error = $"'{path}' declares permission '{permissionKind}'{(scope is null ? string.Empty : " for '" + scope + "'")} twice.";
                return false;
            }

            parsed.Add(new Permission(permissionKind, scope));
        }

        permissions = parsed.AsReadOnly();
        return true;
    }
}

/// <summary>Validation helpers for the identifiers that become file system segments, assembly names and pipe names.
/// The rules are the ones <see cref="VersionedOutputStore"/> applies to a path segment, so a manifest the reader accepts
/// can never make the host throw while it composes an output path.</summary>
internal static class PluginIds
{
    public const int MaxSegmentLength = 64;

    private const int MaxPipeTokenLength = 40;

    private static readonly char[] InvalidSegmentChars =
    [
        .. Path.GetInvalidFileNameChars(),
        '/',
        '\\',
        ':',
        '"',
        '<',
        '>',
        '|',
        '*',
        '?',
    ];

    public static bool IsSafeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxSegmentLength)
        {
            return false;
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) || value is "." or "..")
        {
            return false;
        }

        // A leading dot names a host-owned area of the plugins root (.pending, .trash), which discovery skips by design:
        // an id or version with that shape could be installed and then never seen, so it is refused at the source.
        if (value[0] == '.')
        {
            return false;
        }

        if (value.Contains("..", StringComparison.Ordinal) || value.AsSpan().IndexOfAny(InvalidSegmentChars) >= 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character < ' ')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A deterministic assembly name for a plugin id, keeping the id readable when it is already valid.</summary>
    public static string AssemblyName(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var name = Sanitize(id, int.MaxValue);
        return name is "." or ".." ? "plugin" : name;
    }

    /// <summary>
    /// True for a tool name the host is willing to publish. Tool names travel to the main program as identifiers, so they
    /// follow the same character rules as a path segment and are bounded in length.
    /// </summary>
    public static bool IsSafeToolName(string? value) =>
        value is { Length: > 0 and <= MaxSegmentLength } && IsSafeSegment(value);

    /// <summary>Short, separator-free token used inside a per-worker pipe name.</summary>
    public static string PipeToken(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Sanitize(id, MaxPipeTokenLength);
    }

    private static string Sanitize(string value, int maxLength)
    {
        var limit = Math.Min(value.Length, maxLength);
        var buffer = new char[limit];
        for (var i = 0; i < limit; i++)
        {
            var character = value[i];
            buffer[i] = char.IsLetterOrDigit(character) || character is '.' or '_' or '-'
                ? character
                : '_';
        }

        var text = new string(buffer).Trim('.', '_', '-');
        return text.Length == 0 ? "plugin" : text;
    }
}
