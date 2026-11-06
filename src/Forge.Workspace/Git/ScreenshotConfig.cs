using System.Globalization;
using System.Text.Json;

namespace Forge.Workspace;

/// <summary>
/// How a project takes its own screenshots, declared in <c>.forge/screenshots.json</c> on the session branch:
/// <see cref="Command"/> writes PNG files into <see cref="OutputDirectory"/> (a headless or end-to-end test, for example).
/// </summary>
/// <param name="Command">Shell command line, run in the worktree root.</param>
/// <param name="OutputDirectory">Directory the command writes PNG files into, relative to the worktree root, with forward slashes.</param>
/// <param name="Timeout">How long one run of the command may take.</param>
public sealed record ScreenshotConfig(string Command, string OutputDirectory, TimeSpan Timeout);

/// <summary>Whether a worktree has a usable screenshot declaration.</summary>
public enum ScreenshotConfigState
{
    /// <summary>The file does not exist; the project declares nothing.</summary>
    Missing,

    /// <summary>The file exists and is valid.</summary>
    Valid,

    /// <summary>The file exists but cannot be used; <see cref="ScreenshotConfigResult.Error"/> says why.</summary>
    Invalid,
}

/// <summary>Outcome of reading <c>.forge/screenshots.json</c>; a malformed file is a result, never an exception.</summary>
public sealed record ScreenshotConfigResult(ScreenshotConfigState State, ScreenshotConfig? Config, string? Error)
{
    public static ScreenshotConfigResult Missing { get; } = new(ScreenshotConfigState.Missing, null, null);

    public static ScreenshotConfigResult Ok(ScreenshotConfig config) => new(ScreenshotConfigState.Valid, config, null);

    public static ScreenshotConfigResult Invalid(string error) => new(ScreenshotConfigState.Invalid, null, error);
}

/// <summary>
/// Reads and validates <c>.forge/screenshots.json</c>. The file is read from the session branch's own worktree (the base
/// branch may not have it yet). Shape: <c>{"command": "...", "output": "relative/dir", "timeoutSeconds": 300}</c>;
/// <c>command</c> and <c>output</c> are required, <c>timeoutSeconds</c> defaults to 300 and must be 10 to 3600; any other
/// property is an error, so a misspelt key is not silently ignored.
/// </summary>
public static class ScreenshotConfigLoader
{
    /// <summary>Path of the declaration relative to the worktree root.</summary>
    public const string RelativePath = ".forge/screenshots.json";

    public const int DefaultTimeoutSeconds = 300;
    public const int MinTimeoutSeconds = 10;
    public const int MaxTimeoutSeconds = 3600;

    private const int MaxFileBytes = 64 * 1024;
    private const int MaxCommandLength = 8 * 1024;

    /// <summary>Reads the declaration from <paramref name="worktree"/>; <see cref="ScreenshotConfigState.Missing"/> when there is none.</summary>
    public static ScreenshotConfigResult Load(string worktree)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktree);
        var path = Path.Combine(worktree, ".forge", "screenshots.json");
        try
        {
            if (!File.Exists(path))
            {
                return ScreenshotConfigResult.Missing;
            }

            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes)
            {
                return ScreenshotConfigResult.Invalid($"{RelativePath} is larger than {MaxFileBytes / 1024} KiB; it should only hold the command, the output directory and a timeout.");
            }

            return Parse(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ScreenshotConfigResult.Invalid($"{RelativePath} could not be read: {exception.Message}");
        }
    }

    /// <summary>Validates the text of a declaration.</summary>
    public static ScreenshotConfigResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException exception)
        {
            return ScreenshotConfigResult.Invalid($"{RelativePath} is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ScreenshotConfigResult.Invalid($"{RelativePath} must be a JSON object like {{\"command\": \"...\", \"output\": \"dir\", \"timeoutSeconds\": 300}}.");
            }

            string? command = null;
            string? output = null;
            var timeoutSeconds = DefaultTimeoutSeconds;
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "command":
                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            return ScreenshotConfigResult.Invalid($"{RelativePath}: \"command\" must be a string.");
                        }

                        command = property.Value.GetString();
                        break;
                    case "output":
                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            return ScreenshotConfigResult.Invalid($"{RelativePath}: \"output\" must be a string (a directory relative to the repository root).");
                        }

                        output = property.Value.GetString();
                        break;
                    case "timeoutSeconds":
                        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out timeoutSeconds))
                        {
                            return ScreenshotConfigResult.Invalid($"{RelativePath}: \"timeoutSeconds\" must be a whole number of seconds.");
                        }

                        break;
                    default:
                        return ScreenshotConfigResult.Invalid(
                            $"{RelativePath}: unknown property \"{property.Name}\"; allowed properties are command, output and timeoutSeconds.");
                }
            }

            if (string.IsNullOrWhiteSpace(command))
            {
                return ScreenshotConfigResult.Invalid($"{RelativePath}: \"command\" is required and must not be blank.");
            }

            if (command.Length > MaxCommandLength)
            {
                return ScreenshotConfigResult.Invalid($"{RelativePath}: \"command\" is longer than {MaxCommandLength} characters; put the logic in a script.");
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                return ScreenshotConfigResult.Invalid($"{RelativePath}: \"output\" is required: the directory the command writes its PNG files into.");
            }

            if (NormalizeOutput(output) is not { } normalized)
            {
                return ScreenshotConfigResult.Invalid(
                    $"{RelativePath}: \"output\" ('{output}') must be a directory inside the repository: relative, no '..', and not the repository root or .git.");
            }

            if (timeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
            {
                return ScreenshotConfigResult.Invalid(
                    $"{RelativePath}: \"timeoutSeconds\" is {timeoutSeconds.ToString(CultureInfo.InvariantCulture)}; it must be between " +
                    $"{MinTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} and {MaxTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}.");
            }

            return ScreenshotConfigResult.Ok(new ScreenshotConfig(command.Trim(), normalized, TimeSpan.FromSeconds(timeoutSeconds)));
        }
    }

    /// <summary>
    /// The output directory with forward slashes and without leading or trailing separators, or null when it is not a
    /// directory strictly inside the repository (rooted, drive-qualified, climbing with <c>..</c>, the root itself, or under
    /// <c>.git</c>).
    /// </summary>
    internal static string? NormalizeOutput(string output)
    {
        var text = output.Trim().Replace('\\', '/');
        if (text.Length == 0 || text[0] == '/' || text.Contains(':', StringComparison.Ordinal) || text.IndexOf('\0') >= 0)
        {
            return null;
        }

        var segments = new List<string>();
        foreach (var segment in text.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == ".." || segment.TrimEnd() != segment || segment.EndsWith('.'))
            {
                return null;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0 || string.Equals(segments[0], ".git", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return string.Join('/', segments);
    }
}
