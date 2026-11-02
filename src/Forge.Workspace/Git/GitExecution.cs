namespace Forge.Workspace;

/// <summary>Outcome of one <c>git</c> invocation. A failing exit code is reported, not thrown.</summary>
public sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut = false)
{
    public bool Success => !TimedOut && ExitCode == 0;

    /// <summary>Captured output hit the capture limit, so it is only a prefix of what git wrote.</summary>
    public bool IsTruncated { get; init; }
}

/// <summary>A typed helper could not use the command result: non-zero exit or timeout.</summary>
public class GitException : Exception
{
    public GitException(string message, IReadOnlyList<string> arguments, int exitCode, string standardError)
        : base(message)
    {
        Arguments = arguments;
        ExitCode = exitCode;
        StandardError = standardError;
    }

    public IReadOnlyList<string> Arguments { get; }

    public int ExitCode { get; }

    public string StandardError { get; }
}

/// <summary>A typed helper refused to return output that the capture limit cut off.</summary>
public sealed class GitOutputTruncatedException : GitException
{
    public GitOutputTruncatedException(string message, IReadOnlyList<string> arguments, int exitCode, string standardError)
        : base(message, arguments, exitCode, standardError)
    {
    }
}

/// <summary>Per-invocation knobs for <see cref="GitCli.RunAsync(string, IReadOnlyList{string}, GitRunOptions, CancellationToken)"/>.</summary>
public sealed record GitRunOptions
{
    /// <summary>Overrides <see cref="GitCli.DefaultTimeout"/>; <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> disables it.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Overrides <see cref="GitCli.MaxCapturedCharacters"/> for this call.</summary>
    public int? MaxCapturedCharacters { get; init; }

    /// <summary>
    /// Called for every stdout line as it arrives, while the text is still captured up to the cap. Lines are split
    /// on LF only and are cut off past 1 MiB, so a pathological line cannot grow the buffer without bound.
    /// </summary>
    public Action<string>? OnStandardOutputLine { get; init; }

    public Action<string>? OnStandardErrorLine { get; init; }
}
