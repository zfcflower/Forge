namespace Forge.App.Providers;

/// <summary>
/// Whether the Claude Code command is installed on this machine. The subscription login reuses the CLI's own credentials,
/// so without the CLI there is nothing to reuse and the login button must say so instead of failing later.
/// </summary>
internal static class ClaudeCodeInstallation
{
    /// <summary>The executable names the official installers put on the path, per platform.</summary>
    private static readonly string[] WindowsNames = ["claude.exe", "claude.cmd", "claude.bat", "claude"];

    private static readonly string[] OtherNames = ["claude"];

    /// <summary>
    /// True when one of the <paramref name="searchDirectories"/> holds the command. The defaults are the <c>PATH</c> entries
    /// plus the native installer's <c>~/.local/bin</c>, which a freshly installed shell may not have yet.
    /// </summary>
    public static bool IsInstalled(IEnumerable<string>? searchDirectories = null, string? homeDirectory = null)
    {
        var names = OperatingSystem.IsWindows() ? WindowsNames : OtherNames;
        var directories = searchDirectories ?? DefaultDirectories(homeDirectory);
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var name in names)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory.Trim(), name)))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is not an installation; keep searching the rest.
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> DefaultDirectories(string? homeDirectory)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return entry;
        }

        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".local", "bin");
        }
    }
}
