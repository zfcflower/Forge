using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Agents;

/// <summary>
/// Tells a session when a file its prompts were built from changed after the agent host took its snapshot: the template
/// folders (<c>commands</c>, <c>agents</c>, <c>prompts</c>), the skill folders and the <c>AGENTS.md</c> layers, in the user
/// folder (<c>~/.forge</c>) and in the workspace. It never reloads anything itself (a reload breaks the cached prefix, so the
/// user decides); it only raises <see cref="Changed"/> once per <see cref="Arm"/>, so a burst of saves is one signal.
/// <para>No per-turn polling: the operating system's change notifications are the only trigger. Only the watched folders are
/// watched, never the rest of <c>~/.forge</c> (logs and stores). A folder that does not exist yet is covered by a
/// non-recursive watch on its nearest existing ancestor; when the folder appears the real watch is created and the change is
/// signalled. A watcher error (a buffer overflow loses events) is signalled as a change too, since what was lost is unknown.</para>
/// </summary>
internal sealed class PromptSourceWatcher : IDisposable
{
    private static readonly string[] WatchedFolders = ["commands", "agents", "prompts", "skills"];

    private readonly object gate = new();
    private readonly List<Target> targets = [];
    private readonly Dictionary<string, FileSystemWatcher> live = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    private int armed;

    public PromptSourceWatcher(string? userDirectory, string? workspace)
    {
        if (!string.IsNullOrWhiteSpace(userDirectory))
        {
            AddForgeRoot(userDirectory);
        }

        if (!string.IsNullOrWhiteSpace(workspace))
        {
            AddForgeRoot(Path.Combine(workspace, ".forge"));
            var skills = Path.Combine(workspace, ".github", "skills");
            targets.Add(new Target(skills, skills, Recursive: true, LayerOnly: false));
            targets.Add(new Target(workspace, workspace, Recursive: false, LayerOnly: true));
        }

        Sync();
    }

    /// <summary>Raised on a thread-pool thread, once after each <see cref="Arm"/>, when a watched file changed.</summary>
    public event Action? Changed;

    /// <summary>Starts listening for the next change; the snapshot the session holds is current as of this call.</summary>
    public void Arm() => Volatile.Write(ref armed, 1);

    /// <summary>
    /// Whether a changed path is a prompt source: an <c>AGENTS.md</c>, a watched folder itself (created, deleted or renamed
    /// as a whole), or a Markdown file under one of the template folders of <paramref name="root"/>. Skill folders hold
    /// supporting files too, so any change under <c>skills</c> counts.
    /// </summary>
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
                return slash < 0 || first == "skills" || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    public void Dispose()
    {
        FileSystemWatcher[] watchers;
        lock (gate)
        {
            disposed = true;
            Changed = null;
            watchers = [.. live.Values];
            live.Clear();
        }

        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }
    }

    private void AddForgeRoot(string forge)
    {
        // The root itself, non-recursive: AGENTS.md, and a watched folder being created or removed as a whole.
        targets.Add(new Target(forge, forge, Recursive: false, LayerOnly: false));
        foreach (var folder in WatchedFolders)
        {
            targets.Add(new Target(Path.Combine(forge, folder), forge, Recursive: true, LayerOnly: false));
        }
    }

    /// <summary>
    /// Makes the live watchers match the targets: the folder itself when it exists, otherwise a non-recursive watch on its
    /// nearest existing ancestor, so that its creation is noticed. Safe to call again after any structural event.
    /// </summary>
    private void Sync()
    {
        var wanted = new Dictionary<string, (string Directory, bool Recursive, Target? Target)>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (Directory.Exists(target.Directory))
            {
                wanted[target.Directory + "|" + target.Recursive] = (target.Directory, target.Recursive, target);
                continue;
            }

            var ancestor = Path.GetDirectoryName(target.Directory);
            while (!string.IsNullOrEmpty(ancestor) && !Directory.Exists(ancestor))
            {
                ancestor = Path.GetDirectoryName(ancestor);
            }

            if (!string.IsNullOrEmpty(ancestor))
            {
                wanted.TryAdd(ancestor + "|False|chain", (ancestor, false, null));
            }
        }

        var stale = new List<FileSystemWatcher>();
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            foreach (var key in live.Keys.Where(key => !wanted.ContainsKey(key)).ToArray())
            {
                stale.Add(live[key]);
                live.Remove(key);
            }

            foreach (var (key, entry) in wanted)
            {
                if (live.ContainsKey(key))
                {
                    continue;
                }

                var watcher = Create(key, entry.Directory, entry.Recursive, entry.Target);
                if (watcher is not null)
                {
                    live[key] = watcher;
                }
            }
        }

        foreach (var watcher in stale)
        {
            watcher.Dispose();
        }
    }

    private FileSystemWatcher? Create(string key, string directory, bool recursive, Target? target)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.Size,
            };

            // A chain watcher (target is null) only waits for a missing folder, or one of its parents, to appear.
            void OnEvent(string? path, bool structural)
            {
                if (path is null)
                {
                    return;
                }

                bool relevant;
                if (target is null)
                {
                    relevant = targets.Exists(t => IsOnTheWayTo(path, t.Directory));
                }
                else
                {
                    relevant = target.LayerOnly ? IsLayerFile(path) : Relevant(target.Root, path);
                }

                if (!relevant)
                {
                    return;
                }

                if (structural)
                {
                    Task.Run(Sync);
                }

                Signal();
            }

            watcher.Changed += (_, e) => OnEvent(e.FullPath, structural: false);
            watcher.Created += (_, e) => OnEvent(e.FullPath, structural: true);
            watcher.Deleted += (_, e) => OnEvent(e.FullPath, structural: true);
            watcher.Renamed += (_, e) =>
            {
                OnEvent(e.FullPath, structural: true);
                OnEvent(e.OldFullPath, structural: true);
            };
            watcher.Error += (_, e) =>
            {
                AppLogs.Session.ZLogWarning($"the prompt watcher of '{directory}' failed: {e.GetException().Message}");
                lock (gate)
                {
                    if (live.Remove(key, out var broken))
                    {
                        Task.Run(broken.Dispose);
                    }
                }

                // What a failed watcher missed is unknown, so the prompts count as changed; Sync builds the watcher again.
                Signal();
                Task.Run(Sync);
            };
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            AppLogs.Session.ZLogWarning($"the prompt sources of '{directory}' cannot be watched: {exception.Message}");
            return null;
        }
    }

    private void Signal()
    {
        if (Interlocked.CompareExchange(ref armed, 0, 1) == 1)
        {
            Changed?.Invoke();
        }
    }

    private static bool IsLayerFile(string path) => string.Equals(Path.GetFileName(path), "AGENTS.md", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="directory"/> is <paramref name="path"/> itself or lies below it.</summary>
    private static bool IsOnTheWayTo(string path, string directory)
    {
        var relative = Path.GetRelativePath(path, directory);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private sealed record Target(string Directory, string Root, bool Recursive, bool LayerOnly);
}
