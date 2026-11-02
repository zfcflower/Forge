using Forge.Abstractions;

namespace Forge.Plugins;

public enum PluginIssueKind
{
    Unknown = 0,
    UnreadableRoot,
    ManifestMissing,
    ManifestInvalid,
    IdMismatch,
    DuplicateId,
    EntryAssemblyLoadFailed,
    EntryTypeInvalid,
    RegisterFailed,
    InvalidRegistration,
    ReservedRegistration,
    DuplicateRegistration,
    ContextNotCollected,
}

/// <summary>A non-fatal plugin problem. Discovery and loading report these instead of throwing.</summary>
public sealed record PluginLoadIssue(string PluginId, PluginIssueKind Kind, string Message, string? Directory = null);

public sealed record DiscoveredPlugin(PluginManifest Manifest, string Directory, string Root)
{
    public string Id => Manifest.Id;
}

public sealed record PluginScanResult(IReadOnlyList<DiscoveredPlugin> Plugins, IReadOnlyList<PluginLoadIssue> Issues)
{
    public static PluginScanResult Empty { get; } = new([], []);
}

/// <summary>
/// Scans plugin roots and reads their manifests. Roots are ordered by ascending precedence: a plugin in a
/// later root overrides the same id from an earlier one, so the global user root comes before the workspace root.
/// </summary>
public sealed class PluginDiscovery
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string[] roots;

    public PluginDiscovery(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var paths = new List<string>();
        foreach (var root in roots)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(root);
            var full = Path.GetFullPath(root);
            if (!paths.Contains(full, PathComparer))
            {
                paths.Add(full);
            }
        }

        this.roots = [.. paths];
    }

    public IReadOnlyList<string> Roots => roots;

    /// <summary>User-level plugins: <c>~/.forge/plugins</c> unless <paramref name="userPluginsDirectory"/> is given.</summary>
    public static string GlobalPluginsDirectory(string? userPluginsDirectory = null) =>
        string.IsNullOrWhiteSpace(userPluginsDirectory)
            ? Path.Combine(PluginPaths.GlobalDirectory, "plugins")
            : Path.GetFullPath(userPluginsDirectory);

    /// <summary>Project plugins: <c>&lt;workspace&gt;/.forge/plugins</c>.</summary>
    public static string WorkspacePluginsDirectory(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        return Path.Combine(Path.GetFullPath(workspacePath), ".forge", "plugins");
    }

    /// <summary>Ascending precedence: global user directory, then the workspace directory.</summary>
    public static IReadOnlyList<string> DefaultRoots(string workspacePath, string? userPluginsDirectory = null) =>
        [GlobalPluginsDirectory(userPluginsDirectory), WorkspacePluginsDirectory(workspacePath)];

    /// <summary>Scans every root; the result is ordered by plugin id and is stable across runs.</summary>
    public PluginScanResult Scan()
    {
        var winners = new Dictionary<string, (DiscoveredPlugin Plugin, int Root)>(StringComparer.Ordinal);
        var issues = new List<PluginLoadIssue>();

        for (var rootIndex = 0; rootIndex < roots.Length; rootIndex++)
        {
            var root = roots[rootIndex];
            if (!Directory.Exists(root))
            {
                continue;
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issues.Add(new PluginLoadIssue(string.Empty, PluginIssueKind.UnreadableRoot, $"Plugin root could not be read: {ex.Message}", root));
                continue;
            }

            Array.Sort(directories, static (left, right) => string.CompareOrdinal(left, right));

            foreach (var directory in directories)
            {
                try
                {
                    ScanDirectory(root, rootIndex, directory, winners, issues);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    issues.Add(new PluginLoadIssue(Path.GetFileName(directory), PluginIssueKind.UnreadableRoot, $"Plugin directory could not be read: {ex.Message}", directory));
                }
            }
        }

        var plugins = new DiscoveredPlugin[winners.Count];
        var index = 0;
        foreach (var entry in winners.Values)
        {
            plugins[index++] = entry.Plugin;
        }

        Array.Sort(plugins, static (left, right) => string.CompareOrdinal(left.Id, right.Id));
        issues.Sort(static (left, right) =>
        {
            var byId = string.CompareOrdinal(left.PluginId, right.PluginId);
            return byId != 0 ? byId : string.CompareOrdinal(left.Message, right.Message);
        });

        return new PluginScanResult(Array.AsReadOnly(plugins), Array.AsReadOnly(issues.ToArray()));
    }

    /// <summary>Same as <see cref="Scan"/>, reading manifests off the calling thread.</summary>
    public Task<PluginScanResult> ScanAsync(CancellationToken ct = default) =>
        Task.Run(Scan, ct);

    private static void ScanDirectory(
        string root,
        int rootIndex,
        string directory,
        Dictionary<string, (DiscoveredPlugin Plugin, int Root)> winners,
        List<PluginLoadIssue> issues)
    {
        var leaf = Path.GetFileName(directory);
        if (leaf.Length == 0 || leaf[0] == '.' || !Directory.Exists(directory))
        {
            return;
        }

        if (!File.Exists(Path.Combine(directory, PluginManifestReader.FileName)))
        {
            if (ContainsAssembly(directory))
            {
                issues.Add(new PluginLoadIssue(leaf, PluginIssueKind.ManifestMissing, $"Directory has assemblies but no {PluginManifestReader.FileName}", directory));
            }

            return;
        }

        var read = PluginManifestReader.Read(directory);
        if (!read.Success)
        {
            issues.Add(new PluginLoadIssue(leaf, PluginIssueKind.ManifestInvalid, read.Message ?? read.Error.ToString(), directory));
            return;
        }

        var manifest = read.Manifest!;
        if (!string.Equals(manifest.Id, leaf, StringComparison.Ordinal))
        {
            issues.Add(new PluginLoadIssue(manifest.Id, PluginIssueKind.IdMismatch, $"Manifest id '{manifest.Id}' differs from directory name '{leaf}'", directory));
        }

        var discovered = new DiscoveredPlugin(manifest, Path.GetFullPath(directory), root);
        if (winners.TryGetValue(manifest.Id, out var existing))
        {
            var incomingWins = rootIndex > existing.Root;
            var shadowed = incomingWins ? existing.Plugin : discovered;
            var winner = incomingWins ? discovered : existing.Plugin;
            issues.Add(new PluginLoadIssue(manifest.Id, PluginIssueKind.DuplicateId, $"Plugin '{manifest.Id}' in {shadowed.Directory} is overridden by {winner.Directory}", shadowed.Directory));
            if (incomingWins)
            {
                winners[manifest.Id] = (discovered, rootIndex);
            }

            return;
        }

        winners[manifest.Id] = (discovered, rootIndex);
    }

    private static bool ContainsAssembly(string directory)
    {
        try
        {
            foreach (var _ in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }
}

/// <summary>Well-known Forge locations used when the host does not pass explicit paths.</summary>
public static class PluginPaths
{
    /// <summary><c>~/.forge</c>.</summary>
    public static string GlobalDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
        ".forge");

    /// <summary>Default host data base for plugin state: <c>~/.forge/data/plugins</c>.</summary>
    public static string DefaultDataDirectory { get; } = Path.Combine(GlobalDirectory, "data", "plugins");
}
