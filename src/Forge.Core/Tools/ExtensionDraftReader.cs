using System.Text;
using System.Text.Json;

namespace Forge.Core.Tools;

/// <summary>
/// Reads and validates the model arguments of <c>extension_create</c>/<c>extension_update</c>. Everything a draft can get
/// wrong locally is rejected here, before <see cref="IExtensionAuthoring"/> is touched, so a malformed draft never reaches
/// the staging area or the approval card. Parsing uses <see cref="JsonDocument"/> only: no reflection, no serializer.
/// </summary>
internal static class ExtensionDraftReader
{
    internal const string ManifestFileName = "manifest.json";
    internal const string WorkflowFileName = "workflow.json";
    internal const string PermissionKindList = "ReadFile, WriteFile, ExecuteCommand, Network, Database";

    /// <summary>The characters the extension host refuses in an id or version, because both become path segments.</summary>
    private static readonly char[] UnsafeSegmentChars = ['/', '\\', ':', '"', '<', '>', '|', '*', '?'];

    /// <summary>
    /// Parses one submit call. <paramref name="error"/> is empty when the draft is usable, otherwise every problem found,
    /// joined for the model; the caller turns it into an error <see cref="ToolResult"/>.
    /// </summary>
    public static ExtensionDraft? Read(string argumentsJson, bool isUpdate, out string error)
    {
        var errors = new List<string>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException exception)
        {
            error = $"the arguments are not valid JSON: {exception.Message}";
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "the arguments must be a JSON object.";
                return null;
            }

            var id = RequiredSegment(root, "id", errors);
            var version = RequiredSegment(root, "version", errors);
            var kind = ReadKind(root, errors);
            var files = ReadFiles(root, errors);
            var permissions = ReadPermissions(root, errors);
            var dependencies = ReadDependencies(root, errors);
            var tool = ReadTool(root, errors, out var toolPresent);

            if (kind is ExtensionDraftKinds.Workflow)
            {
                if (!toolPresent)
                {
                    errors.Add(
                        "'tool' is required for a workflow: the name, description and input_schema of the single tool the workflow exposes");
                }

                if (!HasWorkflowFile(files))
                {
                    errors.Add($"'files' must contain '{WorkflowFileName}' for a workflow");
                }
            }
            else if (kind is ExtensionDraftKinds.Plugin)
            {
                if (toolPresent)
                {
                    errors.Add("'tool' applies to a workflow only; a plugin declares its tools in code");
                }

                var sources = SourceFileCount(files);
                if (sources != 1)
                {
                    errors.Add($"a plugin needs exactly one .cs file in 'files', found {sources}");
                }
            }

            if (errors.Count > 0)
            {
                error = Describe(errors);
                return null;
            }

            error = string.Empty;
            return new ExtensionDraft(id!, version!, kind!, files, permissions, dependencies, tool, isUpdate);
        }
    }

    /// <summary>Reads the <c>id</c> and <c>version</c> of <c>extension_disable</c>/<c>extension_delete</c>.</summary>
    public static bool TryReadIdAndVersion(string argumentsJson, out string id, out string version, out string error)
    {
        id = string.Empty;
        version = string.Empty;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException exception)
        {
            error = $"the arguments are not valid JSON: {exception.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "the arguments must be a JSON object.";
                return false;
            }

            var errors = new List<string>();
            var readId = RequiredSegment(root, "id", errors);
            var readVersion = RequiredSegment(root, "version", errors);
            if (errors.Count > 0)
            {
                error = Describe(errors);
                return false;
            }

            id = readId!;
            version = readVersion!;
            error = string.Empty;
            return true;
        }
    }

    private static string Describe(List<string> errors)
    {
        if (errors.Count == 1)
        {
            return string.Concat(errors[0], ".");
        }

        var builder = new StringBuilder(errors.Count * 64);
        foreach (var problem in errors)
        {
            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(problem);
        }

        return string.Concat(builder.ToString().TrimEnd(';', ' '), ".");
    }

    private static string? RequiredSegment(JsonElement root, string property, List<string> errors)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            errors.Add($"'{property}' is required and must be a string");
            return null;
        }

        var value = element.GetString()!;
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"'{property}' must not be empty");
            return null;
        }

        if (value is "." or ".." || value.Contains("..", StringComparison.Ordinal)
            || value.AsSpan().IndexOfAny(UnsafeSegmentChars) >= 0)
        {
            errors.Add($"'{property}' may not contain path separators, quotes, angle brackets, '?', '*' or '..'");
            return null;
        }

        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                errors.Add($"'{property}' may not contain control characters");
                return null;
            }
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            errors.Add($"'{property}' may not start or end with whitespace");
            return null;
        }

        return value;
    }

    private static string? ReadKind(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("kind", out var element) || element.ValueKind != JsonValueKind.String)
        {
            errors.Add($"'kind' is required and must be \"{ExtensionDraftKinds.Plugin}\" or \"{ExtensionDraftKinds.Workflow}\"");
            return null;
        }

        var value = element.GetString()!.Trim().ToLowerInvariant();
        if (value is not (ExtensionDraftKinds.Plugin or ExtensionDraftKinds.Workflow))
        {
            errors.Add(
                $"'kind' must be \"{ExtensionDraftKinds.Plugin}\" or \"{ExtensionDraftKinds.Workflow}\", not \"{element.GetString()}\"");
            return null;
        }

        return value;
    }

    private static Dictionary<string, string> ReadFiles(JsonElement root, List<string> errors)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("files", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            errors.Add("'files' is required and must be an object mapping each relative path to its full text");
            return files;
        }

        var entries = 0;
        foreach (var property in element.EnumerateObject())
        {
            entries++;
            if (PathProblem(property.Name) is { } problem)
            {
                errors.Add($"files path '{property.Name}' is not usable: {problem}");
                continue;
            }

            if (string.Equals(property.Name, ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"do not send '{ManifestFileName}': the manifest is generated from id, version, kind, permissions and dependencies");
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                errors.Add($"files['{property.Name}'] must be the file text as a string");
                continue;
            }

            files[property.Name] = property.Value.GetString()!;
        }

        if (entries == 0)
        {
            errors.Add("'files' must contain at least one file");
        }

        return files;
    }

    /// <summary>Why <paramref name="path"/> cannot be a staged path, or null when it is a plain '/'-separated relative path.</summary>
    private static string? PathProblem(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "it is empty";
        }

        if (path.Contains('\\'))
        {
            return "use '/' as the separator";
        }

        if (path.Contains(':'))
        {
            return "it must be relative, without a drive or stream name";
        }

        if (path.StartsWith('/'))
        {
            return "it must be relative";
        }

        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case "":
                    return "it has an empty path segment";
                case ".":
                case "..":
                    return "'.' and '..' segments are not allowed";
            }
        }

        return null;
    }

    private static List<ExtensionPermissionDraft> ReadPermissions(JsonElement root, List<string> errors)
    {
        var permissions = new List<ExtensionPermissionDraft>();
        if (!root.TryGetProperty("permissions", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return permissions;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"'permissions' must be an array of {{\"kind\": one of {PermissionKindList}, \"scope\"?}} objects");
            return permissions;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"every 'permissions' entry must be an object with a 'kind' from {PermissionKindList}");
                continue;
            }

            if (!item.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
            {
                errors.Add($"every 'permissions' entry needs a 'kind' string from {PermissionKindList}");
                continue;
            }

            var requested = kindElement.GetString()!;
            var kind = CanonicalPermission(requested);
            if (kind is null)
            {
                errors.Add($"unknown permission kind '{requested}'; use one of {PermissionKindList}");
                continue;
            }

            string? scope = null;
            if (item.TryGetProperty("scope", out var scopeElement) && scopeElement.ValueKind != JsonValueKind.Null)
            {
                if (scopeElement.ValueKind != JsonValueKind.String)
                {
                    errors.Add($"the 'scope' of permission '{requested}' must be a string");
                    continue;
                }

                var text = scopeElement.GetString();
                scope = string.IsNullOrWhiteSpace(text) ? null : text;
            }

            permissions.Add(new ExtensionPermissionDraft(kind, scope));
        }

        return permissions;
    }

    private static string? CanonicalPermission(string value) => value.Trim().ToLowerInvariant() switch
    {
        "readfile" => "ReadFile",
        "writefile" => "WriteFile",
        "executecommand" => "ExecuteCommand",
        "network" => "Network",
        "database" => "Database",
        _ => null,
    };

    private static List<ExtensionDependencyDraft> ReadDependencies(JsonElement root, List<string> errors)
    {
        var dependencies = new List<ExtensionDependencyDraft>();
        if (!root.TryGetProperty("dependencies", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return dependencies;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            errors.Add("'dependencies' must be an array of {\"name\", \"version\"} objects");
            return dependencies;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors.Add("every 'dependencies' entry must be an object with 'name' and 'version'");
                continue;
            }

            var name = RequiredText(item, "name", "dependency 'name'", errors);
            var version = RequiredText(item, "version", "dependency 'version'", errors);
            if (name is not null && version is not null)
            {
                dependencies.Add(new ExtensionDependencyDraft(name, version));
            }
        }

        return dependencies;
    }

    private static ExtensionToolDraft? ReadTool(JsonElement root, List<string> errors, out bool present)
    {
        present = false;
        if (!root.TryGetProperty("tool", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        present = true;
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add("'tool' must be an object with 'name', 'description' and 'input_schema'");
            return null;
        }

        var name = RequiredText(element, "name", "tool 'name'", errors);
        var description = element.TryGetProperty("description", out var descriptionElement)
            && descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString()!
                : null;
        if (description is null)
        {
            errors.Add("tool 'description' is required and must be a string");
        }

        string? inputSchema = null;
        if (!element.TryGetProperty("input_schema", out var schemaElement) || schemaElement.ValueKind != JsonValueKind.Object)
        {
            errors.Add("tool 'input_schema' is required and must be a JSON Schema object for the tool's arguments");
        }
        else
        {
            inputSchema = schemaElement.GetRawText();
        }

        return name is null || description is null || inputSchema is null
            ? null
            : new ExtensionToolDraft(name, description, inputSchema);
    }

    private static string? RequiredText(JsonElement parent, string property, string label, List<string> errors)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{label} is required and must be a non-empty string");
            return null;
        }

        var value = element.GetString()!.Trim();
        if (value.Length == 0)
        {
            errors.Add($"{label} must not be empty");
            return null;
        }

        return value;
    }

    private static bool HasWorkflowFile(Dictionary<string, string> files)
    {
        foreach (var path in files.Keys)
        {
            if (string.Equals(path, WorkflowFileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int SourceFileCount(Dictionary<string, string> files)
    {
        var count = 0;
        foreach (var path in files.Keys)
        {
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }
}
