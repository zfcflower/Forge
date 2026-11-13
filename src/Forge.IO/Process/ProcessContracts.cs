namespace Forge.IO;

/// <summary>Why a process stopped.</summary>
public enum ProcessEndReason
{
    /// <summary>The process exited on its own.</summary>
    Exited,

    /// <summary>The timeout elapsed and the process tree was killed.</summary>
    TimedOut,
}

/// <summary>One process to start. Nothing here is interpreted by a shell: arguments are passed as a vector.</summary>
public sealed record ProcessRequest
{
    /// <summary>Executable to start: an absolute path, or a name the operating system resolves on <c>PATH</c>.</summary>
    public required string FileName { get; init; }

    /// <summary>Arguments in order; never joined into a command line.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>Bounded wait; on expiry the whole process tree is killed and <see cref="ProcessResult.EndReason"/> says so.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    public int MaxStdoutBytes { get; init; } = 512 * 1024;

    public int MaxStderrBytes { get; init; } = 512 * 1024;

    /// <summary>
    /// The complete environment the child sees, relative to the parent's: a value of <c>null</c> removes the variable and
    /// any other value sets or overrides it. <c>null</c> means "inherit unchanged".
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>
    /// Receives the latest tail of the live output (stdout and stderr merged in arrival order) while the child runs: a snapshot
    /// of at most <see cref="OutputTailChars"/> characters, at most once per <see cref="OutputInterval"/>, in order (a later call
    /// never carries older text than an earlier one), with a trailing call when output stops mid-interval while the child is
    /// still running. Output still unreported when the child exits is dropped: the result carries all of it. Called on a pump or timer
    /// thread, under the tail's lock; must not block. Null costs nothing: no decoding, no timer.
    /// </summary>
    public Action<string>? OnOutput { get; init; }

    public TimeSpan OutputInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public int OutputTailChars { get; init; } = 16 * 1024;
}

/// <summary>What one process run did. Output is captured as bytes with a hard cap per stream, then decoded once.</summary>
public sealed record ProcessResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    long StdoutBytes,
    long StderrBytes,
    bool StdoutTruncated,
    bool StderrTruncated,
    ProcessEndReason EndReason,
    TimeSpan Duration)
{
    public bool Success => EndReason == ProcessEndReason.Exited && ExitCode == 0;
}

/// <summary>Starts a process, captures both output streams without a cap that can wedge the child, and cleans up the tree.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> ExecuteAsync(ProcessRequest request, CancellationToken cancellationToken);
}
