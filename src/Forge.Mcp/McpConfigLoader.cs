using System.Collections.Frozen;
using System.Text;
using System.Text.Json;

namespace Forge.Mcp;

/// <summary>
/// Reads <c>~/.forge/mcp.json</c> and <c>&lt;workspace&gt;/.forge/mcp.json</c>; a project entry replaces the
/// global entry of the same name. Broken entries become diagnostics instead of failures.
/// </summary>
public static class McpConfigLoader
{
    /// <summary>Largest accepted <c>timeoutSeconds</c>; anything above it is rejected as a misconfiguration.</summary>
    public const double MaxTimeoutSeconds = 3600;

    public static string GlobalPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".forge", "mcp.json");

    public static string ProjectPath(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        return Path.Combine(workspacePath, ".forge", "mcp.json");
    }

    public static McpConfig Load(string? workspacePath = null, IReadOnlyDictionary<string, string>? environment = null) =>
        LoadFrom(GlobalPath, workspacePath, environment);

    /// <summary>Same as <see cref="Load"/> with an explicit global file, for relocated or portable configs.</summary>
    public static McpConfig LoadFrom(
        string? globalPath,
        string? workspacePath,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var servers = new List<McpServerConfig>();
        var diagnostics = new List<McpConfigDiagnostic>();
        if (!string.IsNullOrWhiteSpace(globalPath))
        {
            LoadInto(globalPath, environment, servers, diagnostics);
        }

        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            LoadInto(ProjectPath(workspacePath), environment, servers, diagnostics);
        }

        return new McpConfig(servers, diagnostics);
    }

    public static McpConfig Parse(string json, string source, IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        var servers = new List<McpServerConfig>();
        var diagnostics = new List<McpConfigDiagnostic>();
        ParseServers(json, source, environment, servers, diagnostics);
        return new McpConfig(servers, diagnostics);
    }

    public static string ExpandVariables(string value, IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Expand(value, environment ?? EnvironmentVariables(), null);
    }

    private static void LoadInto(
        string path,
        IReadOnlyDictionary<string, string>? environment,
        List<McpServerConfig> servers,
        List<McpConfigDiagnostic> diagnostics)
    {
        string json;
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new McpConfigDiagnostic(path, null, $"Could not read the file: {ex.Message}"));
            return;
        }

        ParseServers(json, path, environment, servers, diagnostics);
    }

    private static void ParseServers(
        string json,
        string source,
        IReadOnlyDictionary<string, string>? environment,
        List<McpServerConfig> servers,
        List<McpConfigDiagnostic> diagnostics)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            diagnostics.Add(new McpConfigDiagnostic(source, null, $"Invalid JSON: {ex.Message}"));
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new McpConfigDiagnostic(source, null, "The document root is not an object."));
                return;
            }

            if (!root.TryGetProperty("mcpServers", out var entries))
            {
                diagnostics.Add(new McpConfigDiagnostic(source, null, "No \"mcpServers\" object."));
                return;
            }

            if (entries.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new McpConfigDiagnostic(source, null, "\"mcpServers\" is not an object."));
                return;
            }

            foreach (var entry in entries.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                {
                    diagnostics.Add(new McpConfigDiagnostic(source, entry.Name, "The server entry is not an object."));
                    continue;
                }

                var config = ParseServer(entry.Name, entry.Value, source, environment, diagnostics);
                if (config is null)
                {
                    continue;
                }

                servers.RemoveAll(existing => string.Equals(existing.Name, config.Name, StringComparison.Ordinal));
                servers.Add(config);
            }
        }
    }

    private static McpServerConfig? ParseServer(
        string name,
        JsonElement element,
        string source,
        IReadOnlyDictionary<string, string>? environment,
        List<McpConfigDiagnostic> diagnostics)
    {
        try
        {
            var command = ReadOptionalString(element, "command");
            var url = ReadOptionalString(element, "url");

            if (command is null && url is null)
            {
                diagnostics.Add(new McpConfigDiagnostic(source, name, "Neither \"command\" nor \"url\" is set."));
                return null;
            }

            // A streamable-HTTP server endpoint is always http(s): a bare "/mcp" is an absolute file URI on Unix and
            // a relative one on Windows, so UriKind.Absolute alone accepts values no transport can use.
            if (url is not null &&
                (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            {
                diagnostics.Add(new McpConfigDiagnostic(source, name, $"\"url\" is not an absolute http or https URI: {url}"));
                return null;
            }

            if (command is not null && url is not null)
            {
                diagnostics.Add(new McpConfigDiagnostic(source, name, "Both \"command\" and \"url\" are set; using \"url\"."));
                command = null;
            }

            var readOnly = ReadStringArray(element, "readOnlyTools");
            var disabled = element.TryGetProperty("disabled", out var disabledElement) &&
                           disabledElement.ValueKind is JsonValueKind.True;

            return new McpServerConfig
            {
                Name = name,
                Command = command,
                Args = ReadStringArray(element, "args"),
                Env = ReadPairs(element, "env", source, name, environment, diagnostics),
                WorkingDirectory = ReadOptionalString(element, "cwd"),
                Url = url,
                Headers = ReadPairs(element, "headers", source, name, environment, diagnostics),
                Disabled = disabled,
                ReadOnlyTools = readOnly,
                Timeout = ReadTimeout(element, source, name, diagnostics),
                Source = source,
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or ArgumentException)
        {
            diagnostics.Add(new McpConfigDiagnostic(source, name, $"Invalid server entry: {ex.Message}"));
            return null;
        }
    }

    private static TimeSpan? ReadTimeout(JsonElement element, string source, string name, List<McpConfigDiagnostic> diagnostics)
    {
        if (!element.TryGetProperty("timeoutSeconds", out var timeout) ||
            timeout.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (timeout.ValueKind != JsonValueKind.Number ||
            !timeout.TryGetDouble(out var seconds) ||
            !double.IsFinite(seconds) ||
            seconds <= 0 ||
            seconds > MaxTimeoutSeconds)
        {
            diagnostics.Add(new McpConfigDiagnostic(
                source,
                name,
                $"\"timeoutSeconds\" must be a finite number greater than 0 and at most {MaxTimeoutSeconds:0}; using the default timeout."));
            return null;
        }

        var ticks = seconds * TimeSpan.TicksPerSecond;
        if (!double.IsFinite(ticks) || ticks < 1 || ticks > TimeSpan.MaxValue.Ticks)
        {
            diagnostics.Add(new McpConfigDiagnostic(
                source,
                name,
                $"\"timeoutSeconds\" is outside the supported range; using the default timeout."));
            return null;
        }

        return TimeSpan.FromTicks((long)ticks);
    }

    private static string? ReadOptionalString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
            {
                list.Add(text);
            }
        }

        return list;
    }

    private static FrozenDictionary<string, string> ReadPairs(
        JsonElement element,
        string property,
        string source,
        string name,
        IReadOnlyDictionary<string, string>? environment,
        List<McpConfigDiagnostic> diagnostics)
    {
        if (!element.TryGetProperty(property, out var pairs) || pairs.ValueKind != JsonValueKind.Object)
        {
            return FrozenDictionary<string, string>.Empty;
        }

        var lookup = environment ?? EnvironmentVariables();
        var map = new Dictionary<string, string>(pairs.GetPropertyCount(), StringComparer.Ordinal);
        foreach (var pair in pairs.EnumerateObject())
        {
            var raw = pair.Value.ValueKind switch
            {
                JsonValueKind.String => pair.Value.GetString() ?? string.Empty,
                JsonValueKind.Null => string.Empty,
                _ => pair.Value.GetRawText(),
            };

            map[pair.Name] = Expand(raw, lookup, variable => diagnostics.Add(new McpConfigDiagnostic(
                source,
                name,
                $"Environment variable '{variable}' is not set; expanded to an empty string.")));
        }

        return map.Count == 0 ? FrozenDictionary<string, string>.Empty : map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static string Expand(string value, IReadOnlyDictionary<string, string> lookup, Action<string>? onMissing)
    {
        if (value.IndexOf("${", StringComparison.Ordinal) < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var index = 0;
        while (index < value.Length)
        {
            var start = value.IndexOf("${", index, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(value, index, value.Length - index);
                break;
            }

            var end = value.IndexOf('}', start + 2);
            if (end < 0)
            {
                builder.Append(value, index, value.Length - index);
                break;
            }

            builder.Append(value, index, start - index);
            var variable = value[(start + 2)..end];
            if (lookup.TryGetValue(variable, out var replacement))
            {
                builder.Append(replacement);
            }
            else
            {
                onMissing?.Invoke(variable);
            }

            index = end + 1;
        }

        return builder.ToString();
    }

    private static FrozenDictionary<string, string> EnvironmentVariables()
    {
        var map = new Dictionary<string, string>(64, StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                map[key] = value;
            }
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
