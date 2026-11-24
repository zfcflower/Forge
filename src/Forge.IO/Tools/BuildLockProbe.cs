namespace Forge.IO.Tools;

/// <summary>
/// Fails a build before it starts when a file the build must replace is held open. MSBuild answers that case by retrying
/// the copy ten times, one second apart, and only then failing: 77 seconds and a 119 KB warning log in production, when
/// the first warning already named the holding process.
/// <para>Only <c>dotnet build</c>, <c>publish</c> and <c>test</c> with a project path are probed, and only that project's
/// own <c>bin</c> tree. Anything the parser cannot read, a missing <c>bin</c>, or any failure other than a sharing
/// violation lets the command run as it always did: a probe must never be the reason a build did not happen.</para>
/// </summary>
internal static class BuildLockProbe
{
    /// <summary>How many files of an output directory are tried; the running app holds the ones that matter.</summary>
    private const int MaxFilesPerDirectory = 64;

    /// <summary>How many assemblies one project may have (one per target framework and configuration).</summary>
    private const int MaxAssemblies = 4;

    /// <summary>The first locked file of the build's own output, or null when nothing is held.</summary>
    public static string? Find(IReadOnlyList<string> argv, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (ProjectPath(argv, out var project) is not { } relative)
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(Path.Combine(workingDirectory, relative));
            var directory = Path.GetDirectoryName(full);
            if (directory is null || !Directory.Exists(Path.Combine(directory, "bin")))
            {
                return null;
            }

            var name = Path.GetFileNameWithoutExtension(full);
            var assemblies = Directory
                .EnumerateFiles(Path.Combine(directory, "bin"), name + ".dll", SearchOption.AllDirectories)
                .Take(MaxAssemblies)
                .ToArray();
            var probed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in assemblies)
            {
                if (Path.GetDirectoryName(assembly) is not { } folder || !probed.Add(folder))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(folder, "*.dll").Take(MaxFilesPerDirectory))
                {
                    if (IsLocked(file))
                    {
                        return file;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            // A probe that cannot look is not evidence of a lock.
        }

        return null;
    }

    /// <summary>
    /// The project argument of a <c>dotnet build|publish|test</c> command line, or null for anything else (another
    /// program, another verb, a solution, a directory).
    /// </summary>
    private static string? ProjectPath(IReadOnlyList<string> argv, out string project)
    {
        project = string.Empty;
        var name = Path.GetFileName(argv[0]);
        if (!string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var verb = -1;
        for (var index = 1; index < argv.Count; index++)
        {
            if (argv[index].StartsWith('-') || argv[index].StartsWith('/'))
            {
                continue;
            }

            verb = index;
            break;
        }

        if (verb < 0 || argv[verb] is not ("build" or "publish" or "test"))
        {
            return null;
        }

        for (var index = verb + 1; index < argv.Count; index++)
        {
            var argument = argv[index];
            if (string.Equals(argument, "--project", StringComparison.OrdinalIgnoreCase))
            {
                return index + 1 < argv.Count && IsProjectFile(argv[index + 1], out project) ? project : null;
            }

            if (!argument.StartsWith('-') && !argument.StartsWith('/') && IsProjectFile(argument, out project))
            {
                return project;
            }
        }

        project = string.Empty;
        return null;
    }

    private static bool IsProjectFile(string argument, out string project)
    {
        project = argument;
        return argument.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
               || argument.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
               || argument.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when another process holds the file open — the one case MSBuild's copy step cannot win.</summary>
    private static bool IsLocked(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // A read-only or protected file is not a sharing lock, and refusing the build over it would be wrong.
            return false;
        }
    }
}
