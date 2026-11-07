namespace Forge.IO.Tools;

/// <summary>
/// The resource bounds of the file and shell tools. Every value has a hard upper bound here (a preference cannot raise
/// it), because these numbers are what keeps one runaway call from eating the session's memory or an hour of wall clock.
/// Reading them per call is cheap; the tools take a factory so a preference change applies without a restart.
/// </summary>
public sealed record ToolOptions
{
    public const int DefaultReadMaxBytes = 256 * 1024;
    public const int MaxReadMaxBytes = 1024 * 1024;
    public const int DefaultReadMaxLines = 2000;
    public const int MaxReadMaxLines = 20000;
    public const int DefaultReadLineOffset = 1;

    /// <summary>An <c>edit</c> reads and rewrites the whole file; past this size it refuses instead of doubling memory.</summary>
    public const int DefaultEditMaxFileBytes = 8 * 1024 * 1024;
    public const int MaxEditMaxFileBytes = 32 * 1024 * 1024;

    public const int DefaultShellCaptureBytes = 512 * 1024;
    public const int MaxShellCaptureBytes = 4 * 1024 * 1024;
    public const int DefaultShellTimeoutSeconds = 120;
    public const int MinShellTimeoutSeconds = 1;
    public const int MaxShellTimeoutSeconds = 600;
    public const int DefaultMaxConcurrentProcesses = 8;

    public const int DefaultGrepMaxResults = 1000;
    public const int MaxGrepMaxResults = 10000;
    public const int DefaultGrepMaxFileBytes = 8 * 1024 * 1024;
    public const int DefaultGrepContextLines = 0;
    public const int MaxGrepContextLines = 5;
    public const int DefaultSearchConcurrency = 8;

    public const int DefaultFindMaxResults = 1000;
    public const int MaxFindMaxResults = 10000;
    public const int DefaultListMaxEntries = 10000;
    public const int MaxListMaxEntries = 50000;
    public const int DefaultWalkDepth = 16;
    public const int MaxWalkDepth = 64;

    public static ToolOptions Default { get; } = new();

    public int ReadMaxBytes { get; init; } = DefaultReadMaxBytes;

    public int ReadMaxLines { get; init; } = DefaultReadMaxLines;

    public int EditMaxFileBytes { get; init; } = DefaultEditMaxFileBytes;

    public int ShellCaptureBytes { get; init; } = DefaultShellCaptureBytes;

    public int ShellTimeoutSeconds { get; init; } = DefaultShellTimeoutSeconds;

    public int MaxConcurrentProcesses { get; init; } = DefaultMaxConcurrentProcesses;

    public int GrepMaxResults { get; init; } = DefaultGrepMaxResults;

    public int GrepMaxFileBytes { get; init; } = DefaultGrepMaxFileBytes;

    public int FindMaxResults { get; init; } = DefaultFindMaxResults;

    public int ListMaxEntries { get; init; } = DefaultListMaxEntries;

    /// <summary>File reads <c>grep</c> runs at once; clamped to <c>[1, DefaultSearchConcurrency]</c> by the engine.</summary>
    public int SearchConcurrency { get; init; } = DefaultSearchConcurrency;

    /// <summary>
    /// An optional ripgrep executable. Empty or missing means the managed search engine is used; the tool never falls
    /// back to an <c>rg</c> found inside the workspace.
    /// </summary>
    public string? RipgrepPath { get; init; }

    /// <summary>An optional shell for script-mode commands; empty means the platform default is resolved.</summary>
    public string? ShellPath { get; init; }
}
