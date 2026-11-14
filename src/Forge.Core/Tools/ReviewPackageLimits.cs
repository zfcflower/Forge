namespace Forge.Core.Tools;

/// <summary>
/// How much of a change set one review package carries. The host reads its settings into one of these at the start of
/// each review (see <c>AgentHostOptions.ReviewLimits</c>), so a change applies to the next package without a restart.
/// The defaults are what the package used when these were fixed constants; Core has no preferences of its own.
/// </summary>
public sealed record ReviewPackageLimits
{
    public const int DefaultDiffChars = 48_000;
    public const int DefaultCommitLines = 50;
    public const int DefaultFileLines = 200;
    public const int DefaultFieldChars = 2_000;

    /// <summary>The package's own defaults.</summary>
    public static ReviewPackageLimits Default { get; } = new();

    /// <summary>Cap on the diff inside the child's task message.</summary>
    public int DiffChars { get; init; } = DefaultDiffChars;

    /// <summary>
    /// Cap on the commit lines and on the file lines, independently: the files are what a review is about, so a long
    /// commit history must not eat their budget. Both say how many lines they left out.
    /// </summary>
    public int CommitLines { get; init; } = DefaultCommitLines;

    public int FileLines { get; init; } = DefaultFileLines;

    /// <summary>Cap per intent, summary, verification, focus, commit subject and the narrowed-to path list.</summary>
    public int FieldChars { get; init; } = DefaultFieldChars;

    /// <summary>The same limits with every cap at least 1, so a host that hands in zero cannot make the renderer index before a string.</summary>
    internal ReviewPackageLimits Sanitized() => new()
    {
        DiffChars = Math.Max(1, DiffChars),
        CommitLines = Math.Max(0, CommitLines),
        FileLines = Math.Max(0, FileLines),
        FieldChars = Math.Max(1, FieldChars),
    };
}
