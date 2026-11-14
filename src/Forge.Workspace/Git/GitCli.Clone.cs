using System.Globalization;

namespace Forge.Workspace;

/// <summary>
/// The step of a clone git reports progress for. The order is the order the steps run in, so a later phase never reports
/// a smaller number than an earlier one did.
/// </summary>
public enum GitClonePhase
{
    /// <summary>Nothing but the destination message yet (<c>Cloning into 'name'...</c>); there is no total to divide by.</summary>
    Connecting = 0,

    /// <summary>The remote counts what it will send (<c>remote: Enumerating objects</c>); a total, no percent.</summary>
    Enumerating = 1,

    /// <summary>The remote counts the objects (<c>remote: Counting objects</c>).</summary>
    Counting = 2,

    /// <summary>The remote compresses the objects (<c>remote: Compressing objects</c>).</summary>
    Compressing = 3,

    /// <summary>Objects travel over the wire (<c>Receiving objects</c>): the phase that takes the time on a real network.</summary>
    Receiving = 4,

    /// <summary>The receiver resolves deltas (<c>Resolving deltas</c>).</summary>
    Resolving = 5,

    /// <summary>The worktree is written (<c>Updating files</c>).</summary>
    Updating = 6,
}

/// <summary>
/// One progress update of <see cref="GitCli.CloneAsync"/>. Git writes these to stderr and rewrites the same line in place
/// with a carriage return, so they arrive several times a second; <see cref="Percent"/> is the phase's own percentage, not
/// an overall one, because the phases have no common denominator.
/// </summary>
/// <param name="Phase">Which step is running.</param>
/// <param name="Percent">The phase's own percentage when git printed one, otherwise <c>null</c>.</param>
/// <param name="Detail">
/// What git appended after the percentage (<c>978.98 KiB | 3.82 MiB/s, done.</c>), trimmed and capped; empty when the line
/// carried nothing but the phase and the count.
/// </param>
public sealed record GitCloneProgress(GitClonePhase Phase, int? Percent, string Detail)
{
    /// <summary>Longest detail kept; git's own tail is short, and a pathological line must not reach the UI intact.</summary>
    public const int MaxDetailCharacters = 120;

    /// <summary>
    /// Reads one stderr line of <c>git clone --progress</c>. Returns false for every line that says nothing about progress
    /// (<c>remote: Total 54 (delta 3)</c>, a transport warning, the empty line after a rewrite), so a caller can report
    /// exactly the updates and nothing else. Accepts the <c>remote: </c> prefix, the padding git puts between the counter and
    /// the percentage, a trailing <c>done.</c> and surrounding carriage returns.
    /// </summary>
    public static bool TryParse(string? text, out GitCloneProgress progress)
    {
        progress = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var line = text.AsSpan().Trim();
        if (line.StartsWith("remote: ", StringComparison.Ordinal))
        {
            line = line["remote: ".Length..];
        }

        if (line.StartsWith("Cloning into ", StringComparison.Ordinal))
        {
            progress = new GitCloneProgress(GitClonePhase.Connecting, null, TrimDetail(line));
            return true;
        }

        var phase = PhaseOf(line);
        if (phase is null)
        {
            return false;
        }

        var percent = PercentOf(line);
        progress = new GitCloneProgress(phase.Value, percent, DetailOf(line, percent));
        return true;
    }

    private static GitClonePhase? PhaseOf(ReadOnlySpan<char> line)
    {
        if (line.StartsWith("Enumerating objects", StringComparison.Ordinal))
        {
            return GitClonePhase.Enumerating;
        }

        if (line.StartsWith("Counting objects", StringComparison.Ordinal))
        {
            return GitClonePhase.Counting;
        }

        if (line.StartsWith("Compressing objects", StringComparison.Ordinal))
        {
            return GitClonePhase.Compressing;
        }

        if (line.StartsWith("Receiving objects", StringComparison.Ordinal))
        {
            return GitClonePhase.Receiving;
        }

        if (line.StartsWith("Resolving deltas", StringComparison.Ordinal))
        {
            return GitClonePhase.Resolving;
        }

        if (line.StartsWith("Updating files", StringComparison.Ordinal) || line.StartsWith("Checking out files", StringComparison.Ordinal))
        {
            return GitClonePhase.Updating;
        }

        return null;
    }

    private static int? PercentOf(ReadOnlySpan<char> line)
    {
        var percent = line.IndexOf('%');
        if (percent <= 0)
        {
            return null;
        }

        var start = percent;
        while (start > 0 && char.IsAsciiDigit(line[start - 1]))
        {
            start--;
        }

        // The remote can put any text after "remote: ", so the digits before the sign are parsed defensively
        // instead of trusted: a run long enough to overflow must not throw on the stderr reader thread.
        return start == percent || !int.TryParse(line[start..percent], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? null
            : value;
    }

    /// <summary>
    /// What git appended after the counter. The counter is the percentage plus its <c>(done/total)</c> bracket, or a bare
    /// count when the line has no percentage, and a comma follows it: <c>Receiving objects: 100% (54/54), 1.2 MiB | 3 MiB/s,
    /// done.</c> yields <c>1.2 MiB | 3 MiB/s, done.</c> and <c>Enumerating objects: 54, done.</c> yields <c>done.</c>.
    /// </summary>
    private static string DetailOf(ReadOnlySpan<char> line, int? percent)
    {
        var colon = line.IndexOf(':');
        if (colon < 0)
        {
            return string.Empty;
        }

        var rest = line[(colon + 1)..].TrimStart();
        if (percent is not null)
        {
            var percentSign = rest.IndexOf('%');
            if (percentSign >= 0)
            {
                rest = rest[(percentSign + 1)..].TrimStart();
            }
        }
        else if (!rest.IsEmpty && char.IsAsciiDigit(rest[0]))
        {
            var count = 0;
            while (count < rest.Length && char.IsAsciiDigit(rest[count]))
            {
                count++;
            }

            rest = rest[count..];
        }

        if (!rest.IsEmpty && rest[0] == '(')
        {
            var close = rest.IndexOf(')');
            if (close >= 0)
            {
                rest = rest[(close + 1)..];
            }
        }

        rest = rest.TrimStart();
        if (rest.StartsWith(", "))
        {
            rest = rest[2..];
        }

        return TrimDetail(rest);
    }

    /// <summary>Trims what git appended and caps it for display; a pathological line must not reach the UI intact.</summary>
    private static string TrimDetail(ReadOnlySpan<char> text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > MaxDetailCharacters)
        {
            trimmed = trimmed[..MaxDetailCharacters];
        }

        return new string(trimmed);
    }
}

/// <summary>Per-clone knobs for <see cref="GitCli.CloneAsync"/>.</summary>
public sealed record GitCloneOptions
{
    /// <summary>
    /// Receives every progress update as git writes it. Called from the thread that reads git's stderr, so a UI handler
    /// must marshal to its own thread; an exception thrown here is swallowed (a broken reporter must not break a clone).
    /// </summary>
    public Action<GitCloneProgress>? OnProgress { get; init; }

    /// <summary>Overrides <see cref="GitCli.DefaultTimeout"/>; <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> disables it.</summary>
    public TimeSpan? Timeout { get; init; }
}

public sealed partial class GitCli
{
    /// <summary>
    /// Clones <paramref name="url"/> into <paramref name="destination"/> with progress reporting, cancellation and a
    /// timeout. Git runs with <c>--progress</c>, so it reports even though stderr is a pipe, and its in-place rewrites
    /// (<c>\r</c>) are split into individual updates.
    /// <para>
    /// The destination's parent is created when missing; <paramref name="destination"/> itself must be absent or an empty
    /// directory, otherwise the call refuses with <see cref="InvalidOperationException"/> rather than letting git write into
    /// a directory that holds something. A clone that fails or is cancelled removes the destination when this call created
    /// it, so an interrupted clone leaves no half-filled repository behind; a directory that was already there is left alone.
    /// Failure (non-zero exit, timeout) throws <see cref="GitException"/>; cancellation kills the process tree and throws
    /// <see cref="OperationCanceledException"/>.
    /// </para>
    /// </summary>
    public Task CloneAsync(
        string url,
        string destination,
        GitCloneOptions? options = null,
        CancellationToken ct = default) =>
        CloneCoreAsync(url, destination, options ?? new GitCloneOptions(), ct);

    private async Task CloneCoreAsync(string url, string destination, GitCloneOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        // The '--' on the command line is the second half of the injection guard; the first is refusing a URL that
        // starts with '-', which git would otherwise read as an option.
        var repositoryUrl = url.Trim();
        if (repositoryUrl.StartsWith('-'))
        {
            throw new ArgumentException(
                $"'{url}' is not a valid repository URL: a leading '-' would be parsed as a git option.",
                nameof(url));
        }

        if (!Path.IsPathRooted(destination))
        {
            throw new ArgumentException($"'{destination}' must be an absolute path.", nameof(destination));
        }

        var target = Path.GetFullPath(destination);
        var existed = Directory.Exists(target);
        if (existed && !IsEmptyDirectory(target))
        {
            throw new InvalidOperationException(
                $"The clone destination '{target}' already exists and is not empty, so a clone cannot be written into it.");
        }

        // Git runs from the destination's parent (it must exist) and creates the destination itself.
        var parent = Path.GetDirectoryName(target);
        if (parent is not null)
        {
            Directory.CreateDirectory(parent);
        }

        var arguments = Args("clone", "--progress", "--", repositoryUrl, target);

        // Both pumps parse: git writes the progress to stderr, but routing stdout through the same filter costs nothing
        // and keeps the callback contract independent of which stream a future git version chooses.
        var filter = options.OnProgress is null ? null : new CloneProgressFilter(options.OnProgress);
        var runOptions = new GitRunOptions
        {
            Timeout = options.Timeout,
            CarriageReturnEndsLine = true,
            OnStandardOutputLine = filter is null ? null : filter.OnLine,
            OnStandardErrorLine = filter is null ? null : filter.OnLine,
        };

        try
        {
            var result = await RunCoreAsync(
                parent ?? target,
                arguments,
                runOptions,
                environment: null,
                ct,
                standardInput: null,
                keepCompletedResult: false).ConfigureAwait(false);
            if (!result.Success)
            {
                throw Failure(result, arguments);
            }
        }
        catch
        {
            // An interrupted clone must not leave a half-filled repository behind, but a directory that was already
            // there (an empty one the caller prepared) is not this call's to delete.
            if (!existed)
            {
                await DeleteDirectoryQuietlyAsync(target).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Reports each update once and in order. Git redraws a line in place, so a phase repeats its current percentage many
    /// times and occasionally redraws a smaller one; the percentage is the phase's own (the phases share no common
    /// denominator), so the high-water mark is tracked per phase. Both pumps call in, which makes the state shared.
    /// </summary>
    private sealed class CloneProgressFilter(Action<GitCloneProgress> report)
    {
        private const int NeverReported = -2;
        private const int NoPercent = -1;

        // One slot per phase; NoPercent stands in for a line git printed without a percentage.
        private readonly int[] highWater = InitializeHighWater();
        private readonly object gate = new();

        public void OnLine(string? line)
        {
            if (!GitCloneProgress.TryParse(line, out var progress))
            {
                return;
            }

            lock (gate)
            {
                var slot = (int)progress.Phase;
                var percent = progress.Percent ?? NoPercent;
                if (percent <= highWater[slot])
                {
                    return;
                }

                highWater[slot] = percent;
            }

            // Outside the lock: the reporter is caller code and must not block the pump that feeds it. It is also
            // untrusted: a broken reporter must not fail the clone.
            try
            {
                report(progress);
            }
            catch (Exception)
            {
            }
        }

        private static int[] InitializeHighWater()
        {
            var marks = new int[(int)GitClonePhase.Updating + 1];
            Array.Fill(marks, NeverReported);
            return marks;
        }
    }

    private static bool IsEmptyDirectory(string directory)
    {
        foreach (var _ in Directory.EnumerateFileSystemEntries(directory))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Best-effort removal of a directory tree this call created. git writes its pack and object files read-only, so
    /// their attributes are cleared before the delete; a file a scanner still holds open, a kill whose file handles the
    /// operating system has not released yet or a permission problem must not replace the clone's real failure with a
    /// cleanup failure, so the retries are short and bounded and everything left is swallowed.
    /// </summary>
    private static async Task DeleteDirectoryQuietlyAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }

                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
    }
}
