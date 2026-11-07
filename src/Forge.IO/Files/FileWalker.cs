using System.IO.Enumeration;

namespace Forge.IO;

/// <summary>
/// One entry a walk visits: its full path, its workspace-relative form with forward slashes, whether it is a directory,
/// and whether it is a link (symbolic link, junction or any other reparse point).
/// </summary>
public readonly record struct WalkEntry(string FullPath, string RelativePath, bool IsDirectory, bool IsLink = false);

/// <summary>
/// Decides whether a walk enters an entry. The path is workspace-relative and uses forward slashes; the callback is made
/// once per entry, so an implementation that parses rules must cache the per-directory state it accumulates.
/// </summary>
public interface IPathFilter
{
    bool IsIgnored(string relativePath, bool isDirectory);
}

/// <summary>
/// Depth-first file system traversal below a starting directory. Lazy (only one directory level is classified at a time),
/// skips the version control directory, and never walks through a link: a linked directory is yielded as an entry but not
/// entered, so walking a workspace cannot enumerate files outside it. Within a level the directories come first and the
/// files after them, which keeps <c>ls</c> readable without sorting.
/// <para>Entry type and the link bit come from the directory enumeration itself (<see cref="FileSystemEnumerable{T}"/> over
/// <see cref="FileSystemEntry"/>), so classifying a level costs no extra stat per entry.</para>
/// </summary>
public static class FileWalker
{
    /// <summary>The name of the version control directory, which no walk enters.</summary>
    public const string VcsDirectory = ".git";

    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.PlatformDefault,
    };

    /// <summary>
    /// Walks <paramref name="startDirectory"/> (full path, must be inside <paramref name="root"/>), yielding entries at
    /// most <paramref name="maxDepth"/> levels below it: depth 1 is its direct children. <paramref name="maxDepth"/> is
    /// clamped to <c>[0, 64]</c>.
    /// </summary>
    /// <param name="filter">Decides which entries are skipped; <c>null</c> keeps everything.</param>
    public static IEnumerable<WalkEntry> Walk(
        string root,
        string startDirectory,
        int maxDepth,
        IPathFilter? filter,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var depthLimit = Math.Clamp(maxDepth, 0, 64);
        if (depthLimit == 0)
        {
            yield break;
        }

        var start = WorkspacePath.RelativeTo(root, startDirectory);
        var pending = new Stack<(string Directory, string Relative, int Depth)>();
        pending.Push((startDirectory, start, 0));

        var level = new Level();
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, relative, depth) = pending.Pop();
            if (depth >= depthLimit)
            {
                continue;
            }

            level.Clear();
            Classify(directory, relative, depth + 1, filter, level);

            for (var index = 0; index < level.Directories.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return level.Directories[index];
            }

            for (var index = 0; index < level.Files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return level.Files[index];
            }

            // Pushed in reverse so the subtree of the first sub-directory is walked next: a depth-first order that also
            // groups a directory's subtree together, which is what a reader of `ls -R` expects.
            for (var index = level.Pending.Count - 1; index >= 0; index--)
            {
                pending.Push(level.Pending[index]);
            }
        }
    }

    /// <summary>
    /// Classifies one directory level: its entries in enumeration order, its sub-directories to descend into, and the
    /// filter applied once per entry. A linked directory is reported but never queued.
    /// </summary>
    private static void Classify(string directory, string relative, int childDepth, IPathFilter? filter, Level level)
    {
        FileSystemEnumerable<Entry> entries;
        try
        {
            entries = new FileSystemEnumerable<Entry>(
                directory,
                static (ref FileSystemEntry source) => new Entry(
                    source.ToFullPath(),
                    source.FileName.ToString(),
                    source.IsDirectory,
                    (source.Attributes & FileAttributes.ReparsePoint) != 0),
                WalkOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An unreadable directory is skipped, not fatal: a walk of a big tree must not die on one permission error.
            return;
        }

        try
        {
            foreach (var entry in entries)
            {
                if (entry.Name.Length == 0 || string.Equals(entry.Name, VcsDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var childRelative = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                if (filter is not null && filter.IsIgnored(childRelative, entry.IsDirectory))
                {
                    continue;
                }

                var walked = new WalkEntry(entry.FullPath, childRelative, entry.IsDirectory, entry.IsLink);
                if (!entry.IsDirectory)
                {
                    level.Files.Add(walked);
                    continue;
                }

                level.Directories.Add(walked);
                if (!entry.IsLink)
                {
                    level.Pending.Add((entry.FullPath, childRelative, childDepth));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    private readonly record struct Entry(string FullPath, string Name, bool IsDirectory, bool IsLink);

    /// <summary>The buffers one directory level is classified into; reused across levels to keep a walk allocation light.</summary>
    private sealed class Level
    {
        public List<WalkEntry> Directories { get; } = new(16);

        public List<WalkEntry> Files { get; } = new(64);

        public List<(string Directory, string Relative, int Depth)> Pending { get; } = new(16);

        public void Clear()
        {
            Directories.Clear();
            Files.Clear();
            Pending.Clear();
        }
    }
}
