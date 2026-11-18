using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Agents;

/// <summary>
/// Tells a session when a file its prompts were built from changed after the agent host took its snapshot: the template
/// folders (<c>commands</c>, <c>agents</c>, <c>prompts</c>), the skill folders and the <c>AGENTS.md</c> layers, in the user
/// folder (<c>~/.forge</c>) and in the workspace. It never reloads anything itself (a reload breaks the cached prefix, so the
/// user decides); it only raises <see cref="Changed"/> once per <see cref="Arm"/>, so a burst of saves is one signal.
/// <para>No per-turn polling: the operating system's change notifications are the only trigger. A folder that does not exist
/// when the watch starts is not watched (a folder created later is noticed after the next reload or session).</para>
/// </summary>
internal sealed class PromptSourceWatcher : IDisposable
{
    private static readonly string[] WatchedFolders = ["commands", "agents", "prompts", "skills"];

    private readonly List<FileSystemWatcher> watchers = [];
    private int armed;

    public PromptSourceWatcher(string? userDirectory, string? workspace)
    {
        if (userDirectory is not null)
        {
            Watch(userDirectory, recursive: true, layerFileOnly: false);
        }

        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var forge = Path.Combine(workspace, ".forge");
            Watch(forge, recursive: true, layerFileOnly: false);
            Watch(Path.Combine(workspace, ".github", "skills"), recursive: true, layerFileOnly: false);
            Watch(workspace, recursive: false, layerFileOnly: true);
        }
    }

    /// <summary>Raised on a thread-pool thread, once after each <see cref="Arm"/>, when a watched file changed.</summary>
    public event Action? Changed;

    /// <summary>Starts listening for the next change; the snapshot the session holds is current as of this call.</summary>
    public void Arm() => Volatile.Write(ref armed, 1);

    private void Watch(string directory, bool recursive, bool layerFileOnly)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.Size,
            };
            var root = directory;
            void OnEvent(string? path)
            {
                if (path is not null && (layerFileOnly ? IsLayerFile(path) : Relevant(root, path)) && Interlocked.CompareExchange(ref armed, 0, 1) == 1)
                {
                    Changed?.Invoke();
                }
            }

            watcher.Changed += (_, e) => OnEvent(e.FullPath);
            watcher.Created += (_, e) => OnEvent(e.FullPath);
            watcher.Deleted += (_, e) => OnEvent(e.FullPath);
            watcher.Renamed += (_, e) => OnEvent(e.FullPath);
            watcher.Error += (_, e) => AppLogs.Session.ZLogWarning($"the prompt watcher of '{root}' failed: {e.GetException().Message}");
            watcher.EnableRaisingEvents = true;
            watchers.Add(watcher);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            AppLogs.Session.ZLogWarning($"the prompt sources of '{directory}' cannot be watched: {exception.Message}");
        }
    }

    /// <summary>
    /// Whether a changed path is a prompt source: an <c>AGENTS.md</c>, or a Markdown file under one of the template or skill
    /// folders of <paramref name="root"/>. Skill folders hold supporting files too, so any change under <c>skills</c> counts.
    /// </summary>
    private static bool IsLayerFile(string path) => string.Equals(Path.GetFileName(path), "AGENTS.md", StringComparison.OrdinalIgnoreCase);

    internal static bool Relevant(string root, string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, "AGENTS.md", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        var slash = relative.IndexOf('/');
        var first = slash < 0 ? relative : relative[..slash];
        // The root of a ".github/skills" watch is the skills folder itself, so everything under it is a skill file.
        if (string.Equals(Path.GetFileName(root), "skills", StringComparison.OrdinalIgnoreCase))
        {
            return slash >= 0 || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        }

        foreach (var folder in WatchedFolders)
        {
            if (string.Equals(first, folder, StringComparison.OrdinalIgnoreCase))
            {
                return slash >= 0 && (first == "skills" || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
            }
        }

        return false;
    }

    public void Dispose()
    {
        Changed = null;
        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }

        watchers.Clear();
    }
}
