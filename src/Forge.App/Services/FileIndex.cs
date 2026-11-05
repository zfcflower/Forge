using Forge.Workspace;

namespace Forge.App.Services;

internal sealed record FileEntry(string Name, string RelativePath, bool IsDirectory);

/// <summary>
/// Immutable snapshot of one working directory's file paths, used by the lazy file tree and the changes list. When the
/// directory is a repository the paths come from <c>git ls-files --cached --others --exclude-standard</c>, so
/// <c>.gitignore</c> is honoured; otherwise the directory is walked with the build folders skipped.
/// </summary>
internal sealed class FileIndex
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "node_modules", ".idea", ".forge",
    };

    private const int MaxWalkedFiles = 20000;

    private readonly Dictionary<string, List<FileEntry>> byDirectory = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Child names per directory, so adding a path never scans the sibling list.</summary>
    private readonly Dictionary<string, HashSet<string>> namesByDirectory = new(StringComparer.OrdinalIgnoreCase);

    private FileIndex(int fileCount) => FileCount = fileCount;

    public int FileCount { get; }

    public static async Task<FileIndex> BuildAsync(string root, GitCli git, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
        {
            return new FileIndex(0);
        }

        var paths = new List<string>();
        var listed = false;
        try
        {
            if (await git.IsRepositoryAsync(root, ct).ConfigureAwait(false))
            {
                var result = await git.RunAsync(root, ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], ct).ConfigureAwait(false);
                if (result.Success)
                {
                    foreach (var record in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                    {
                        paths.Add(record.Replace('\\', '/'));
                    }

                    listed = true;
                }
            }
        }
        catch (Exception exception) when (exception is GitException or IOException or InvalidOperationException)
        {
            listed = false;
        }

        if (!listed)
        {
            Walk(root, paths, ct);
        }

        var index = new FileIndex(paths.Count);
        foreach (var path in paths)
        {
            index.Add(path);
        }

        index.Sort();
        return index;
    }

    /// <summary>Direct children of <paramref name="relativeDirectory"/>, directories first, each group by name.</summary>
    public IReadOnlyList<FileEntry> List(string relativeDirectory)
    {
        var key = Normalize(relativeDirectory);
        return byDirectory.TryGetValue(key, out var entries) ? entries : [];
    }

    private void Add(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return;
        }

        var directory = string.Empty;
        for (var i = 0; i < segments.Length; i++)
        {
            var isLeaf = i == segments.Length - 1;
            var name = segments[i];
            var entryPath = directory.Length == 0 ? name : directory + "/" + name;
            if (!byDirectory.TryGetValue(directory, out var children))
            {
                children = [];
                byDirectory[directory] = children;
                namesByDirectory[directory] = new HashSet<string>(StringComparer.Ordinal);
            }

            if (namesByDirectory[directory].Add(name))
            {
                children.Add(new FileEntry(name, entryPath, !isLeaf));
            }

            if (isLeaf)
            {
                return;
            }

            directory = entryPath;
        }
    }

    private void Sort()
    {
        foreach (var children in byDirectory.Values)
        {
            children.Sort(static (left, right) => left.IsDirectory == right.IsDirectory
                ? string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
                : left.IsDirectory ? -1 : 1);
        }
    }

    private static void Walk(string root, List<string> paths, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var count = 0;
        while (pending.Count > 0 && count < MaxWalkedFiles)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var subdirectory in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(subdirectory);
                    if (SkippedDirectories.Contains(name))
                    {
                        continue;
                    }

                    pending.Push(subdirectory);
                }

                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    paths.Add(relative);
                    count++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Normalize(string relativeDirectory) =>
        relativeDirectory.Replace('\\', '/').Trim('/');
}
