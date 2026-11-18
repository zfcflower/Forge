using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Git;

/// <summary>
/// Where a repository's remotes live and who hears when FORGE adds one. The remotes of a repository are in the git config of
/// its common directory (a worktree shares the main repository's), so that config file identifies the repository: every
/// session and project row of the same repository sees the same <see cref="Added"/> event. Only FORGE's own pairing action
/// raises it; a remote added from outside (a terminal, the model's shell) is not watched.
/// </summary>
internal static class RepositoryRemotes
{
    /// <summary>Raised with the repository's config path after the pairing action added a remote. Handlers must not block.</summary>
    public static event Action<string>? Added;

    /// <summary>Tells every listener that <paramref name="configPath"/>'s repository now has a remote.</summary>
    public static void RaiseAdded(string configPath)
    {
        // One handler throwing must not keep the other sessions from hearing about the remote.
        foreach (var handler in Added?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<string>)handler)(configPath);
            }
            catch (Exception exception)
            {
                AppLogs.Session.ZLogWarning($"a remote-added listener failed: {exception.Message}");
            }
        }
    }

    /// <summary>Whether two config paths name the same file.</summary>
    public static bool SameConfig(string? left, string? right) =>
        left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The git config that holds the remotes: <c>.git/config</c> of the repository the directory belongs to; a worktree's
    /// <c>.git</c> file points (through <c>gitdir</c> and <c>commondir</c>) at the main repository's config. Null when the
    /// directory is not in a repository or the paths cannot be read.
    /// </summary>
    public static string? FindConfig(string workingDirectory)
    {
        try
        {
            for (var directory = new DirectoryInfo(workingDirectory); directory is not null; directory = directory.Parent)
            {
                var dotGit = Path.Combine(directory.FullName, ".git");
                string? common = null;
                if (Directory.Exists(dotGit))
                {
                    common = dotGit;
                }
                else if (File.Exists(dotGit))
                {
                    var line = File.ReadLines(dotGit).FirstOrDefault(static l => l.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
                    if (line is not null)
                    {
                        var gitDir = Path.GetFullPath(Path.Combine(directory.FullName, line["gitdir:".Length..].Trim()));
                        var commonFile = Path.Combine(gitDir, "commondir");
                        common = File.Exists(commonFile)
                            ? Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonFile).Trim()))
                            : gitDir;
                    }
                }

                if (common is not null)
                {
                    var config = Path.Combine(common, "config");
                    return File.Exists(config) ? config : null;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A malformed "gitdir:" line (a path git itself would refuse) means "no repository found", never a failed caller.
        }

        return null;
    }

    /// <summary>
    /// Whether the config file has a <c>[remote "name"]</c> section. Section names are case-insensitive in git; a remote
    /// configured through an included file or <c>config.worktree</c> is not seen (rare, and the PR tools then stay off).
    /// </summary>
    public static bool ConfigHasRemote(string configPath)
    {
        try
        {
            foreach (var raw in File.ReadLines(configPath))
            {
                var line = raw.AsSpan().TrimStart();
                if (line.Length > 8 && line[0] == '[' && line[1..].StartsWith("remote", StringComparison.OrdinalIgnoreCase))
                {
                    // [remote "name"], or the deprecated [remote.name] spelling git still reads.
                    var rest = line[7..].TrimStart();
                    if (rest.Length > 0 && (rest[0] == '"' || (rest[0] == '.' && line[7] == '.')))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogs.Session.ZLogWarning($"the git config could not be read: {exception.Message}");
        }

        return false;
    }

    /// <summary>Whether the directory belongs to a repository that has a remote.</summary>
    public static bool HasRemote(string workingDirectory) =>
        FindConfig(workingDirectory) is { } config && ConfigHasRemote(config);
}
