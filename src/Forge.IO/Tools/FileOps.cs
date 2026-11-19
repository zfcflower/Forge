using System.IO.Enumeration;
using System.Runtime.ExceptionServices;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>What a tree copy or delete touched, for the one-line answer.</summary>
internal readonly record struct TreeStats(int Files, int Directories, long Bytes, int Skipped);

/// <summary>
/// The path rules and tree walks <c>copy</c>, <c>move</c> and <c>delete</c> share. Every walk enumerates a directory once
/// through <see cref="FileSystemEnumerable{T}"/>, which hands over the type and the link flag the operating system already
/// returned with the name, so no entry costs an extra stat; a link is never followed, only skipped (copy) or removed itself
/// (delete), and the version control directory is never entered.
/// </summary>
internal static class FileOps
{
    private const string GitDirectory = ".git";

    private static readonly EnumerationOptions Everything = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Resolves a path this call may create. <see cref="WorkspacePath.Resolve"/> stops at the first component that does
    /// not exist, so a file inside a directory that is about to be created would lose its tail; the deepest existing
    /// ancestor is resolved (and checked against the root) and the names below it are appended unchanged. A final
    /// component that is a link is followed, so writing through it cannot leave the workspace.
    /// </summary>
    internal static string ResolveTarget(string root, string requested)
    {
        var full = WorkspacePath.Normalize(Path.IsPathRooted(requested) ? requested : Path.Combine(root, requested));
        var existing = full;
        var missing = new List<string>();
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent))
            {
                break;
            }

            missing.Add(Path.GetFileName(existing));
            existing = parent;
        }

        var resolved = WorkspacePath.Resolve(root, existing, rootIsReal: true);
        for (var index = missing.Count - 1; index >= 0; index--)
        {
            resolved = Path.Combine(resolved, missing[index]);
        }

        return resolved;
    }

    /// <summary>
    /// Resolves the entry a call names without following a link in its last component, so removing or moving a link acts on
    /// the link and not on what it points at. The directory holding it must be inside the workspace; the workspace root
    /// itself is refused.
    /// </summary>
    internal static string ResolveEntry(string root, string requested)
    {
        var full = Path.TrimEndingDirectorySeparator(
            WorkspacePath.Normalize(Path.IsPathRooted(requested) ? requested : Path.Combine(root, requested)));
        if (string.Equals(full, WorkspacePath.Normalize(root), WorkspacePath.Comparison))
        {
            throw new ToolArgumentException("the workspace root itself cannot be the target.");
        }

        var name = Path.GetFileName(full);
        var parent = Path.GetDirectoryName(full);
        if (name.Length == 0 || parent is null)
        {
            throw new ToolArgumentException($"'{requested}' is not a file or directory path.");
        }

        var entry = Path.Combine(WorkspacePath.Resolve(root, parent, rootIsReal: true), name);
        return string.Equals(entry, WorkspacePath.Normalize(root), WorkspacePath.Comparison)
            ? throw new ToolArgumentException("the workspace root itself cannot be the target.")
            : entry;
    }

    internal static string Display(string root, string path) =>
        WorkspacePath.RelativeTo(root, path) is { Length: > 0 } relative ? relative : path;

    /// <summary>True when the path is the version control directory or below it; its files are the repository, not workspace content.</summary>
    internal static bool IsInsideGit(string root, string path)
    {
        var relative = WorkspacePath.RelativeTo(root, path);
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Equals(GitDirectory, WorkspacePath.Comparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True for a symbolic link, a junction or any other reparse point; it is what the entry itself is, not what it points at.</summary>
    internal static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>The entries of one directory, each with its type and link flag from the same directory read.</summary>
    private static List<(string Name, bool IsDirectory, bool IsLink, long Length)> Entries(string directory)
    {
        var entries = new FileSystemEnumerable<(string, bool, bool, long)>(
            directory,
            static (ref FileSystemEntry entry) => (
                entry.FileName.ToString(),
                entry.IsDirectory,
                (entry.Attributes & FileAttributes.ReparsePoint) != 0,
                entry.Length),
            Everything);
        return [.. entries];
    }

    /// <summary>A tree with at least this many files is copied by several threads: below it the pool's start cost outweighs the gain.</summary>
    private const int ParallelCopyThreshold = 64;

    private const int ParallelDeleteThreshold = 64;

    /// <summary>Copies a directory tree into <paramref name="destination"/>, which exists or is created; links and .git entries are skipped.</summary>
    internal static TreeStats CopyTree(string source, string destination, bool overwrite, CancellationToken ct)
    {
        var bytes = 0L;
        var skipped = 0;
        var directories = 0;
        var files = new List<(string From, string To)>();
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((source, destination));
        while (pending.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(current.Destination);
            directories++;
            foreach (var (name, isDirectory, isLink, length) in Entries(current.Source))
            {
                if (isLink || name.Equals(GitDirectory, WorkspacePath.Comparison))
                {
                    skipped++;
                }
                else if (isDirectory)
                {
                    pending.Push((Path.Combine(current.Source, name), Path.Combine(current.Destination, name)));
                }
                else
                {
                    files.Add((Path.Combine(current.Source, name), Path.Combine(current.Destination, name)));
                    bytes += length;
                }
            }
        }

        // Every directory exists now, so the files are independent of each other.
        if (files.Count >= ParallelCopyThreshold)
        {
            ForEachParallel(files, ct, file => File.Copy(file.From, file.To, overwrite));
        }
        else
        {
            foreach (var (from, to) in files)
            {
                ct.ThrowIfCancellationRequested();
                File.Copy(from, to, overwrite);
            }
        }

        return new TreeStats(files.Count, directories, bytes, skipped);
    }

    /// <summary>Deletes a file, a link (the link only) or a directory with everything below it; read-only files are made writable first.</summary>
    internal static TreeStats DeleteTree(string path, CancellationToken ct)
    {
        var attributes = File.GetAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            DeleteLink(path, isDirectory);
            return new TreeStats(isDirectory ? 0 : 1, 0, 0, 0);
        }

        if (!isDirectory)
        {
            var size = new FileInfo(path).Length;
            DeleteFile(path);
            return new TreeStats(1, 0, size, 0);
        }

        var directories = 0;
        var bytes = 0L;
        var links = 0;
        var doomed = new List<string>();
        var order = new Stack<string>();
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            order.Push(directory);
            foreach (var (name, entryIsDirectory, isLink, length) in Entries(directory))
            {
                var child = Path.Combine(directory, name);
                if (entryIsDirectory && !isLink)
                {
                    pending.Push(child);
                    continue;
                }

                if (isLink && entryIsDirectory)
                {
                    // A directory link is removed like a directory on Windows, so it is not one of the files deleted in parallel.
                    DeleteLink(child, true);
                    links++;
                    continue;
                }

                doomed.Add(child);
                bytes += length;
            }
        }

        var files = doomed.Count + links;
        if (doomed.Count >= ParallelDeleteThreshold)
        {
            ForEachParallel(doomed, ct, DeleteFile);
        }
        else
        {
            foreach (var file in doomed)
            {
                DeleteFile(file);
            }
        }

        // Directories deepest first: a parent is only empty once everything below it is gone.
        while (order.TryPop(out var emptied))
        {
            try
            {
                Directory.Delete(emptied, false);
            }
            catch (UnauthorizedAccessException)
            {
                File.SetAttributes(emptied, FileAttributes.Normal | FileAttributes.Directory);
                Directory.Delete(emptied, false);
            }

            directories++;
        }

        return new TreeStats(files, directories, bytes, 0);
    }

    /// <summary>Runs the action on several threads; a failure surfaces as the first thread's own exception, which is what the tool reports, not an aggregate that would escape it.</summary>
    private static void ForEachParallel<T>(List<T> items, CancellationToken ct, Action<T> action)
    {
        try
        {
            Parallel.ForEach(
                items,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) },
                action);
        }
        catch (AggregateException aggregate) when (aggregate.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(aggregate.InnerExceptions[0]).Throw();
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    private static void DeleteLink(string path, bool isDirectory)
    {
        // A directory link on Windows is removed like a directory (the target stays); everywhere else unlink does it.
        if (isDirectory && OperatingSystem.IsWindows())
        {
            Directory.Delete(path, false);
        }
        else
        {
            DeleteFile(path);
        }
    }
}
