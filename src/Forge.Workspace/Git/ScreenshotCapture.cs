using System.Globalization;
using System.Text;

namespace Forge.Workspace;

/// <summary>
/// Takes the "before" and "after" screenshots of a change by running the project's own command twice: in the session worktree
/// (after) and in a temporary detached worktree of the base commit (before). Shared by the <c>pr_screenshots</c> tool and the
/// app's screenshot button.
/// <para>
/// The command comes from <c>.forge/screenshots.json</c> on the session branch (see <see cref="ScreenshotConfigLoader"/>) or from
/// the caller; it runs through the platform shell with the worktree as its directory and with
/// <c>FORGE_SCREENSHOT_DIR</c> (the directory to write PNG files into) and <c>FORGE_SCREENSHOT_SIDE</c> (<c>before</c> or
/// <c>after</c>) in its environment. With a configuration file that directory is the configured <c>output</c> inside the
/// worktree; without one it is a temporary directory outside every worktree, so an ad-hoc command pollutes nothing.
/// </para>
/// <para>
/// Images are matched by file name: <c>home.png</c> on both sides becomes <c>home-before.png</c> and <c>home-after.png</c>. An
/// image on one side only is kept and the pair marks the other side missing. A side that fails (non-zero exit, timeout) fails the
/// whole capture with its exit code and output tail and returns no images. Whatever the run left in the worktrees is removed
/// again, and the temporary worktree is removed in a <c>finally</c> block (<c>worktree remove --force</c> plus <c>prune</c>),
/// whether the capture succeeded, failed or was cancelled.
/// </para>
/// </summary>
public sealed class ScreenshotCapture(GitCli git, ScreenshotCaptureOptions? options = null)
{
    public const string OutputDirectoryVariable = "FORGE_SCREENSHOT_DIR";
    public const string SideVariable = "FORGE_SCREENSHOT_SIDE";
    public const int MaxImagesPerSide = 40;
    public const long MaxImageBytes = 10L * 1024 * 1024;

    internal const string CannotCaptureMessage =
        "Cannot capture screenshots automatically: this branch has no .forge/screenshots.json and no command was given. " +
        "The project has to declare how to take screenshots ({\"command\": \"...\", \"output\": \"dir\"}), or a one-off command " +
        "that writes PNG files into the directory named by the FORGE_SCREENSHOT_DIR environment variable has to be passed in.";

    private const string TempPrefix = "forge-screenshots-";
    private const int MaxDepth = 3;
    private const int MaxNotes = 12;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly GitCli git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly ScreenshotCaptureOptions options = options ?? new ScreenshotCaptureOptions();

    /// <summary>
    /// Runs the capture. <paramref name="worktree"/> is the session worktree, <paramref name="baseRef"/> the base to compare with
    /// (for example <c>origin/main</c>; fetching it first is the caller's business), <paramref name="commandOverride"/> replaces the
    /// configured command (null or blank means "use the configuration"). Cancellation throws after everything was cleaned up.
    /// </summary>
    public async Task<ScreenshotCaptureResult> CaptureAsync(
        string worktree,
        string baseRef,
        string? commandOverride,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktree);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);
        worktree = WorkspacePaths.Normalize(worktree);

        var loaded = ScreenshotConfigLoader.Load(worktree);
        if (loaded.State == ScreenshotConfigState.Invalid)
        {
            return ScreenshotCaptureResult.Failed(loaded.Error!);
        }

        var config = loaded.Config;
        var command = string.IsNullOrWhiteSpace(commandOverride) ? config?.Command : commandOverride.Trim();
        if (command is null)
        {
            return ScreenshotCaptureResult.Failed(CannotCaptureMessage);
        }

        var timeout = options.Timeout ?? config?.Timeout ?? TimeSpan.FromSeconds(ScreenshotConfigLoader.DefaultTimeoutSeconds);
        var baseCommit = await git.RevParseAsync(worktree, baseRef + "^{commit}", ct).ConfigureAwait(false);
        if (baseCommit is null)
        {
            return ScreenshotCaptureResult.Failed(
                $"Base '{baseRef}' does not resolve to a commit in this repository; fetch it first (git_fetch).");
        }

        var notes = new List<string>();
        var ancestry = await git.RunAsync(worktree, ["merge-base", "--is-ancestor", baseCommit, "HEAD"], ct).ConfigureAwait(false);
        if (!ancestry.Success && ancestry.ExitCode == 1 && !ancestry.TimedOut)
        {
            notes.Add(
                $"{baseRef} is not an ancestor of HEAD: the branch is behind its base, so the \"before\" images show a newer base than " +
                "this branch was built on. Run pr_sync first if that matters.");
        }

        var root = Path.Combine(options.TempRoot ?? Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N")[..12]);
        var images = Path.Combine(root, "images");
        Directory.CreateDirectory(images);

        var keep = false;
        try
        {
            var result = await CaptureCoreAsync(worktree, baseRef, baseCommit, command, config, timeout, root, images, notes, ct)
                .ConfigureAwait(false);
            keep = result.Success;
            return result;
        }
        finally
        {
            if (keep)
            {
                ScreenshotFileSystem.DeleteDirectory(Path.Combine(root, "raw"));
                ScreenshotFileSystem.DeleteDirectory(Path.Combine(root, "base"));
            }
            else
            {
                ScreenshotFileSystem.DeleteDirectory(root);
            }
        }
    }

    /// <summary>
    /// Deletes the directory of a finished capture (<see cref="ScreenshotCaptureResult.CaptureDirectory"/>). Refuses any directory
    /// that this class did not create, so a wrong argument can never remove something else; never throws.
    /// </summary>
    public static void DeleteCaptureDirectory(string? captureDirectory)
    {
        if (string.IsNullOrWhiteSpace(captureDirectory))
        {
            return;
        }

        var name = WorkspacePaths.FileName(captureDirectory);
        if (name.StartsWith(TempPrefix, StringComparison.Ordinal))
        {
            ScreenshotFileSystem.DeleteDirectory(captureDirectory);
        }
    }

    private async Task<ScreenshotCaptureResult> CaptureCoreAsync(
        string worktree,
        string baseRef,
        string baseCommit,
        string command,
        ScreenshotConfig? config,
        TimeSpan timeout,
        string root,
        string images,
        List<string> notes,
        CancellationToken ct)
    {
        var after = await RunSideAsync(
            new SideRequest("after", worktree, worktree, command, config?.OutputDirectory, Path.Combine(root, "raw", "after"), timeout, images, CleanOutput: true),
            ct).ConfigureAwait(false);
        notes.AddRange(after.Notes);
        if (after.Failure is not null)
        {
            return ScreenshotCaptureResult.Failed(after.Failure, after.Report, null, notes);
        }

        var checkout = Path.Combine(root, "base");
        try
        {
            try
            {
                await git.AddDetachedWorktreeAsync(worktree, checkout, baseCommit, ct).ConfigureAwait(false);
            }
            catch (GitException exception)
            {
                return ScreenshotCaptureResult.Failed(
                    $"Could not create the temporary worktree of {baseRef} for the \"before\" images: {exception.Message}",
                    after.Report,
                    null,
                    notes);
            }

            var overlaid = CopyOverlay(worktree, checkout, notes);
            if (overlaid > 0)
            {
                notes.Add(
                    $"Copied {overlaid.ToString(CultureInfo.InvariantCulture)} helper file(s) from this branch into the temporary base worktree " +
                    "(only where the base has no such file) so the capture command can run there.");
            }

            var before = await RunSideAsync(
                new SideRequest("before", checkout, worktree, command, config?.OutputDirectory, Path.Combine(root, "raw", "before"), timeout, images, CleanOutput: false),
                ct).ConfigureAwait(false);
            notes.AddRange(before.Notes);
            if (before.Failure is not null)
            {
                return ScreenshotCaptureResult.Failed(before.Failure, after.Report, before.Report, notes);
            }

            var pairs = Pair(before.Images, after.Images);
            if (pairs.Count == 0)
            {
                return ScreenshotCaptureResult.Failed(NoImagesMessage(config?.OutputDirectory), after.Report, before.Report, notes);
            }

            return new ScreenshotCaptureResult(true, Summarize(pairs), pairs, root, after.Report, before.Report, notes);
        }
        finally
        {
            await RemoveWorktreeAsync(worktree, checkout).ConfigureAwait(false);
        }
    }

    private async Task<SideOutcome> RunSideAsync(SideRequest request, CancellationToken ct)
    {
        var notes = new List<string>();
        var outputDirectory = request.OutputRelative is null
            ? request.RawDirectory
            : Path.Combine(request.Checkout, request.OutputRelative.Replace('/', Path.DirectorySeparatorChar));

        if (request.OutputRelative is not null)
        {
            try
            {
                WorkspacePaths.EnsurePlainPathInside(request.Checkout, outputDirectory, "output");
            }
            catch (ArgumentException exception)
            {
                return SideOutcome.Fail($"The output directory '{request.OutputRelative}' is not usable: {exception.Message}", null, notes);
            }
        }

        if (File.Exists(outputDirectory))
        {
            return SideOutcome.Fail($"The output directory '{request.OutputRelative ?? outputDirectory}' is a file.", null, notes);
        }

        var existedBefore = Directory.Exists(outputDirectory);
        var snapshot = ScreenshotFileSystem.Snapshot(outputDirectory);
        var createdParents = request.CleanOutput && request.OutputRelative is not null
            ? MissingParents(request.Checkout, outputDirectory)
            : [];
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [OutputDirectoryVariable] = outputDirectory,
                [SideVariable] = request.Side,
            };
            var run = await ScreenshotShell.RunAsync(request.Command, request.Checkout, environment, request.Timeout, ct).ConfigureAwait(false);
            var report = new ScreenshotRunReport(request.Side, run.ExitCode, run.TimedOut, run.Duration, run.OutputTail);
            if (!report.Succeeded)
            {
                return SideOutcome.Fail(DescribeFailure(report), report, notes);
            }

            var collected = Collect(ScreenshotFileSystem.NewFiles(outputDirectory, snapshot), request.Side, notes);
            var copied = new List<ScreenshotImageFile>(collected.Count);
            foreach (var image in collected)
            {
                var destination = Path.Combine(request.ImagesDirectory, image.Name + "-" + request.Side + ".png");
                File.Copy(image.FullPath, destination, overwrite: false);
                copied.Add(image with { FullPath = destination });
            }

            return new SideOutcome(null, report, copied, notes);
        }
        finally
        {
            if (request.CleanOutput && request.OutputRelative is not null)
            {
                await CleanOutputAsync(request, outputDirectory, existedBefore, snapshot, createdParents).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Takes back what the run left in the session worktree. A directory that did not exist before and has nothing tracked in it
    /// goes entirely; otherwise only the new untracked files go. Tracked files are never touched. Best effort: the capture result
    /// does not depend on it.
    /// </summary>
    private async Task CleanOutputAsync(
        SideRequest request,
        string outputDirectory,
        bool existedBefore,
        Dictionary<string, FileStamp> snapshot,
        IReadOnlyList<string> createdParents)
    {
        try
        {
            var relative = request.OutputRelative!;
            if (!existedBefore)
            {
                var tracked = await git.ListTrackedFilesAsync(request.GitDirectory, [relative], CancellationToken.None).ConfigureAwait(false);
                if (tracked.Count == 0)
                {
                    ScreenshotFileSystem.DeleteDirectory(outputDirectory);
                    RemoveEmptyDirectories(createdParents);
                    return;
                }
            }

            var leftovers = ScreenshotFileSystem.NewFiles(outputDirectory, snapshot);
            if (leftovers.Count == 0)
            {
                return;
            }

            var paths = new List<string>(leftovers.Count);
            foreach (var file in leftovers)
            {
                paths.Add(relative + "/" + file.Relative);
            }

            var trackedFiles = new HashSet<string>(
                await git.ListTrackedFilesAsync(request.GitDirectory, paths, CancellationToken.None).ConfigureAwait(false),
                StringComparer.Ordinal);
            for (var i = 0; i < leftovers.Count; i++)
            {
                if (!trackedFiles.Contains(paths[i]))
                {
                    ScreenshotFileSystem.DeleteFile(leftovers[i].FullPath);
                }
            }
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException)
        {
            // Leaving files behind is a nuisance, not a failure of the capture; git status shows them to the user.
        }
    }

    /// <summary>The directories between <paramref name="root"/> and <paramref name="directory"/> that do not exist yet, deepest first.</summary>
    private static List<string> MissingParents(string root, string directory)
    {
        var missing = new List<string>();
        var parent = Path.GetDirectoryName(directory);
        while (parent is not null && WorkspacePaths.IsUnder(parent, root) && !WorkspacePaths.Equals(parent, root) && !Directory.Exists(parent))
        {
            missing.Add(parent);
            parent = Path.GetDirectoryName(parent);
        }

        return missing;
    }

    /// <summary>Removes the given directories when they are empty by now; a directory that holds something the command made stays.</summary>
    private static void RemoveEmptyDirectories(IReadOnlyList<string> directories)
    {
        foreach (var directory in directories)
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private static List<ScreenshotImageFile> Collect(IReadOnlyList<ScreenshotFileEntry> files, string side, List<string> notes)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var images = new List<ScreenshotImageFile>();
        var ordered = files.OrderBy(static file => file.Relative, StringComparer.Ordinal);
        foreach (var file in ordered)
        {
            if (!file.Relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (file.Relative.Count(static c => c == '/') > MaxDepth)
            {
                AddNote(notes, $"Skipped {side} image '{file.Relative}': more than {MaxDepth.ToString(CultureInfo.InvariantCulture)} directories deep.");
                continue;
            }

            if (file.Length > MaxImageBytes)
            {
                AddNote(notes, $"Skipped {side} image '{file.Relative}': larger than {MaxImageBytes / (1024 * 1024)} MiB.");
                continue;
            }

            if (!HasPngSignature(file.FullPath))
            {
                AddNote(notes, $"Skipped {side} file '{file.Relative}': it is not a PNG image (wrong file signature).");
                continue;
            }

            var name = SafeName(file.Relative);
            if (!names.Add(name))
            {
                AddNote(notes, $"Skipped {side} image '{file.Relative}': another image already maps to the name '{name}'.");
                continue;
            }

            if (images.Count >= MaxImagesPerSide)
            {
                AddNote(notes, $"Kept the first {MaxImagesPerSide.ToString(CultureInfo.InvariantCulture)} {side} images; the rest were ignored.");
                break;
            }

            images.Add(new ScreenshotImageFile(name, file.FullPath));
        }

        return images;
    }

    private static List<ScreenshotPair> Pair(IReadOnlyList<ScreenshotImageFile> before, IReadOnlyList<ScreenshotImageFile> after)
    {
        var byName = new SortedDictionary<string, (string? Before, string? After)>(StringComparer.Ordinal);
        foreach (var image in before)
        {
            byName[image.Name] = (image.FullPath, null);
        }

        foreach (var image in after)
        {
            byName[image.Name] = byName.TryGetValue(image.Name, out var existing) ? (existing.Before, image.FullPath) : (null, image.FullPath);
        }

        var pairs = new List<ScreenshotPair>(byName.Count);
        foreach (var (name, sides) in byName)
        {
            pairs.Add(new ScreenshotPair(name, sides.Before, sides.After));
        }

        return pairs;
    }

    /// <summary>Copies the overlay paths into the base worktree where the base has no file of that name; returns the file count.</summary>
    private int CopyOverlay(string worktree, string checkout, List<string> notes)
    {
        var copied = 0;
        foreach (var path in options.OverlayPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var source = WorkspacePaths.Normalize(Path.Combine(worktree, path));
            if (!WorkspacePaths.IsUnder(source, worktree) || WorkspacePaths.Equals(source, worktree))
            {
                AddNote(notes, $"Ignored helper path '{path}': it is not inside the worktree.");
                continue;
            }

            var relative = Path.GetRelativePath(worktree, source);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0].Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                AddNote(notes, $"Ignored helper path '{path}': .git is never copied.");
                continue;
            }

            try
            {
                WorkspacePaths.EnsurePlainPathInside(worktree, source, "overlay");
                copied += File.Exists(source)
                    ? CopyFileIfAbsent(source, Path.Combine(checkout, relative))
                    : Directory.Exists(source) ? CopyDirectoryIfAbsent(source, Path.Combine(checkout, relative)) : 0;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                AddNote(notes, $"Could not copy helper path '{path}' into the base worktree: {exception.Message}");
            }
        }

        return copied;
    }

    private static int CopyFileIfAbsent(string source, string destination)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: false);
        return 1;
    }

    private static int CopyDirectoryIfAbsent(string source, string destination)
    {
        var copied = 0;
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(source, "*", enumeration))
        {
            copied += CopyFileIfAbsent(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }

        return copied;
    }

    private async Task RemoveWorktreeAsync(string worktree, string checkout)
    {
        try
        {
            await git.RemoveWorktreeAsync(worktree, checkout, force: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (GitException)
        {
            // Fall through to deleting the directory by hand; prune below drops the stale registration.
        }

        if (Directory.Exists(checkout))
        {
            ScreenshotFileSystem.DeleteDirectory(checkout);
        }

        try
        {
            await git.PruneWorktreesAsync(worktree, CancellationToken.None).ConfigureAwait(false);
        }
        catch (GitException)
        {
        }
    }

    private static string DescribeFailure(ScreenshotRunReport report)
    {
        var text = new StringBuilder(512);
        text.Append("The screenshot command ");
        if (report.TimedOut)
        {
            text.Append("timed out after ").Append(Seconds(report.Duration)).Append(" and its process tree was killed");
        }
        else
        {
            text.Append("failed with exit code ").Append(report.ExitCode.ToString(CultureInfo.InvariantCulture));
        }

        text.Append(report.Side == "before"
            ? " on the \"before\" side (the temporary worktree of the base)."
            : " on the \"after\" side (this worktree).");
        if (!string.IsNullOrWhiteSpace(report.OutputTail))
        {
            text.Append("\nLast output:\n").Append(report.OutputTail);
        }

        if (report.Side == "before")
        {
            text.Append("\nThe command also has to run on the base: files that exist only on this branch are missing there.");
        }

        return text.ToString();
    }

    private static string NoImagesMessage(string? outputRelative)
    {
        var where = outputRelative is null
            ? "the directory named by the FORGE_SCREENSHOT_DIR environment variable"
            : $"'{outputRelative}' (also available as FORGE_SCREENSHOT_DIR)";
        return $"The screenshot command succeeded on both sides but wrote no PNG files into {where}. " +
            "Nothing was captured; check that the command writes its images there.";
    }

    private static string Summarize(List<ScreenshotPair> pairs)
    {
        var both = pairs.Count(static pair => pair.HasBefore && pair.HasAfter);
        var afterOnly = pairs.Count(static pair => !pair.HasBefore);
        var beforeOnly = pairs.Count(static pair => !pair.HasAfter);
        var text = $"Captured {pairs.Count.ToString(CultureInfo.InvariantCulture)} screen(s): {both.ToString(CultureInfo.InvariantCulture)} with before and after";
        if (afterOnly > 0)
        {
            text += $", {afterOnly.ToString(CultureInfo.InvariantCulture)} after only";
        }

        if (beforeOnly > 0)
        {
            text += $", {beforeOnly.ToString(CultureInfo.InvariantCulture)} before only";
        }

        return text + ".";
    }

    private static string Seconds(TimeSpan duration) =>
        duration.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s";

    private static void AddNote(List<string> notes, string note)
    {
        if (notes.Count < MaxNotes)
        {
            notes.Add(note);
        }
        else if (notes.Count == MaxNotes)
        {
            notes.Add("... more notes omitted.");
        }
    }

    private static bool HasPngSignature(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[8];
            return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length && header.SequenceEqual(PngSignature);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The path below the output directory without its extension, made safe for a tree entry and a URL; directories join with <c>__</c>.</summary>
    internal static string SafeName(string relativePath)
    {
        var text = relativePath.Replace('\\', '/');
        if (text.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4];
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character == '/')
            {
                builder.Append("__");
            }
            else if (char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('_');
            }
        }

        if (builder.Length > 0 && builder[0] == '.')
        {
            builder[0] = '_';
        }

        if (builder.Length > 100)
        {
            builder.Length = 100;
        }

        return builder.Length == 0 ? "image" : builder.ToString();
    }

    private sealed record SideRequest(
        string Side,
        string Checkout,
        string GitDirectory,
        string Command,
        string? OutputRelative,
        string RawDirectory,
        TimeSpan Timeout,
        string ImagesDirectory,
        bool CleanOutput);

    private sealed record SideOutcome(
        string? Failure,
        ScreenshotRunReport? Report,
        IReadOnlyList<ScreenshotImageFile> Images,
        IReadOnlyList<string> Notes)
    {
        public static SideOutcome Fail(string message, ScreenshotRunReport? report, IReadOnlyList<string> notes) =>
            new(message, report, [], notes);
    }

    private sealed record ScreenshotImageFile(string Name, string FullPath);
}

internal readonly record struct FileStamp(long Length, long WriteTicks);

internal sealed record ScreenshotFileEntry(string FullPath, string Relative, long Length);

/// <summary>File system helpers for the screenshot step; none of them throws for an expected I/O problem.</summary>
internal static class ScreenshotFileSystem
{
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    /// <summary>Length and write time of every file below <paramref name="directory"/>, keyed by its path relative to it with forward slashes.</summary>
    public static Dictionary<string, FileStamp> Snapshot(string directory)
    {
        var stamps = new Dictionary<string, FileStamp>(StringComparer.Ordinal);
        if (!Directory.Exists(directory))
        {
            return stamps;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", Recursive))
            {
                var info = new FileInfo(path);
                stamps[Relative(directory, path)] = new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return stamps;
    }

    /// <summary>Files below <paramref name="directory"/> that were not in the snapshot or whose length or write time changed since.</summary>
    public static List<ScreenshotFileEntry> NewFiles(string directory, Dictionary<string, FileStamp> snapshot)
    {
        var files = new List<ScreenshotFileEntry>();
        if (!Directory.Exists(directory))
        {
            return files;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", Recursive))
            {
                var info = new FileInfo(path);
                var relative = Relative(directory, path);
                var stamp = new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
                if (!snapshot.TryGetValue(relative, out var before) || before != stamp)
                {
                    files.Add(new ScreenshotFileEntry(path, relative, info.Length));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return files;
    }

    public static void DeleteFile(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Deletes a directory tree, clearing read-only flags first and retrying a few times because on Windows a process that was just
    /// killed can hold a file for a moment. Links inside are removed as links; their targets are left alone.
    /// </summary>
    public static void DeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(path, "*", Recursive))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }

    private static string Relative(string directory, string path) =>
        Path.GetRelativePath(directory, path).Replace('\\', '/');
}
