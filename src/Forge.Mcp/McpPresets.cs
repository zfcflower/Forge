namespace Forge.Mcp;

/// <summary>
/// A well-known MCP server the settings page can prefill an editor with. Only the command line is data here: the title, the
/// note and the "pick a directory" hint are user-facing text and live in the app's string table, keyed by <see cref="Id"/>.
/// </summary>
/// <param name="NeedsPath">
/// True when the command line needs a directory the user must choose: <see cref="PathToken"/> in <paramref name="Args"/> is
/// replaced by it before the entry is written.
/// </param>
public sealed record McpPreset(string Id, string Command, IReadOnlyList<string> Args, string? Url = null, bool NeedsPath = false);

/// <summary>
/// The built-in preset catalogue: the servers people add first, with the command line each one's own documentation gives.
/// A preset only fills the editor — nothing is written until the user saves it, so a stale command line is visible and fixable
/// instead of silently configured.
/// </summary>
public static class McpPresets
{
    /// <summary>Where a directory the user picks goes into a preset's arguments.</summary>
    public const string PathToken = "{path}";

    /// <summary>Every preset, in the order the page lists them (most useful first).</summary>
    public static IReadOnlyList<McpPreset> All { get; } =
    [
        new("filesystem", "npx", ["-y", "@modelcontextprotocol/server-filesystem", PathToken], NeedsPath: true),
        new("context7", "npx", ["-y", "@upstash/context7-mcp@latest"]),
        new("memory", "npx", ["-y", "@modelcontextprotocol/server-memory"]),
        new("playwright", "npx", ["-y", "@playwright/mcp@latest"]),
        new("git", "uvx", ["mcp-server-git", "--repository", PathToken], NeedsPath: true),
        new("fetch", "uvx", ["mcp-server-fetch"]),
        new("time", "uvx", ["mcp-server-time"]),
    ];

    public static McpPreset? Find(string id)
    {
        foreach (var preset in All)
        {
            if (string.Equals(preset.Id, id, StringComparison.Ordinal))
            {
                return preset;
            }
        }

        return null;
    }

    /// <summary>
    /// An entry prefilled from a preset: the directory fills <see cref="PathToken"/>, and an argument that is only the token
    /// is dropped when no directory was chosen (a preset with a dangling placeholder would not start).
    /// </summary>
    public static McpServerEntry ToEntry(McpPreset preset, string name, string? path)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var replacement = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        var args = new List<string>(preset.Args.Count);
        foreach (var argument in preset.Args)
        {
            if (string.Equals(argument, PathToken, StringComparison.Ordinal))
            {
                if (replacement is not null)
                {
                    args.Add(replacement);
                }

                continue;
            }

            args.Add(Replace(argument, replacement));
        }

        return new McpServerEntry
        {
            Name = string.IsNullOrWhiteSpace(name) ? preset.Id : name.Trim(),
            Command = preset.Command,
            Args = args,
            Url = preset.Url,
        };
    }

    private static string Replace(string argument, string? replacement) =>
        replacement is null ? argument : argument.Replace(PathToken, replacement, StringComparison.Ordinal);
}
