namespace Forge.Workspace;

public enum SpecEdgeKind { Parent, DependsOn, References, Implements, Covers }

/// <summary>Identity of one indexed spec; <see cref="Path"/> is workspace-relative with forward slashes.</summary>
public sealed record SpecNodeSummary(string Id, string Type, string? Title, string? Status, string Path);

/// <summary>A declared link; <see cref="Target"/> is null when the target id is not in the index.</summary>
public sealed record SpecLink(SpecEdgeKind Kind, string TargetId, SpecNodeSummary? Target);

public sealed record SpecNodeView(
    SpecNodeSummary Node,
    SpecFrontmatter Frontmatter,
    IReadOnlyList<SpecLink> ForwardLinks,
    IReadOnlyList<SpecLink> ReverseLinks)
{
    /// <summary>Ids this spec declares but that do not exist, kept so callers can report them without re-validating.</summary>
    public IReadOnlyList<string> DanglingTargets => ForwardLinks.Where(static link => link.Target is null).Select(static link => link.TargetId).ToArray();

    public bool HasDanglingTargets => ForwardLinks.Any(static link => link.Target is null);
}

public sealed record SpecGrepQuery
{
    public required string Pattern { get; init; }

    public bool Regex { get; init; }

    public bool IgnoreCase { get; init; } = true;

    /// <summary>Restricts to specs whose <c>type</c> equals this value.</summary>
    public string? Type { get; init; }

    public string? Tag { get; init; }

    public string? Parent { get; init; }

    public string? DependsOn { get; init; }

    public int Limit { get; init; } = 200;

    public int Offset { get; init; }
}

public sealed record SpecMatch(string Id, string Path, int Line, string Text);

public sealed record SpecGrepPage(IReadOnlyList<SpecMatch> Matches, int Total, int? NextOffset);

public enum SpecIssueCode { DuplicateId, DanglingParent, DanglingLink, ParentCycle }

public sealed record SpecIssue(SpecIssueCode Code, string Message, string? SpecId, string? Path);

public sealed record SpecValidationResult(IReadOnlyList<SpecIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public enum SpecGraphDirection { Subtree, Ancestors, Neighbors }

public sealed record SpecGraphNode(SpecNodeSummary Node, int Depth);

/// <summary>
/// One traversed link, recorded in the direction it is declared (<see cref="FromId"/> declares
/// <see cref="Kind"/> towards <see cref="ToId"/>); <see cref="IsReverse"/> marks links the traversal walked
/// backwards, such as the children of a parent.
/// </summary>
public sealed record SpecGraphEdgeLink(string FromId, SpecEdgeKind Kind, string ToId, bool IsReverse);

public sealed record SpecGraphSlice(
    string RootId,
    SpecGraphDirection Direction,
    int Depth,
    IReadOnlyList<SpecGraphNode> Nodes,
    IReadOnlyList<SpecGraphEdgeLink> Edges);

public sealed record SpecCreateRequest
{
    /// <summary>Workspace-relative or absolute path; must end in <c>.md</c> and stay inside the workspace.</summary>
    public required string Path { get; init; }

    public required string Id { get; init; }

    public required string Type { get; init; }

    public required string Title { get; init; }

    public string? Status { get; init; }

    public string? Parent { get; init; }

    public IReadOnlyList<string>? DependsOn { get; init; }

    public IReadOnlyList<string>? References { get; init; }

    public IReadOnlyList<string>? Implements { get; init; }

    public IReadOnlyList<string>? Covers { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    public string? Body { get; init; }
}

/// <summary>Frontmatter edit; keys not listed are left untouched, including fields the parser does not know.</summary>
public sealed record SpecFrontmatterUpdate
{
    /// <summary>Scalar fields to set; a null value removes the field.</summary>
    public IReadOnlyDictionary<string, string?>? Set { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Add { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Remove { get; init; }
}

public sealed record SpecRefreshResult(int FilesScanned, int SpecsIndexed, int Added, int Updated, int Removed);

public sealed record SpecGraphOptions
{
    public IReadOnlyList<string> SkippedDirectories { get; init; } =
        [".git", "bin", "obj", "node_modules", ".forge-worktrees", ".vs", ".idea", "dist", "target"];

    public string Extension { get; init; } = ".md";

    /// <summary>Files above this size are not read; specs are small documents.</summary>
    public int MaxFileBytes { get; init; } = 2 * 1024 * 1024;
}
