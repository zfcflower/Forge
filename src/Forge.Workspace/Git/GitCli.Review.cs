using System.Globalization;

namespace Forge.Workspace;

/// <summary>One commit of the review commit list (newest first).</summary>
public sealed record GitCommitInfo(string Sha, string ShortSha, string Subject, DateTimeOffset AuthorDate, IReadOnlyList<string> ParentShas);

public sealed partial class GitCli
{
    /// <summary>Context-line count that makes git emit whole files: every unchanged line ends up inside the single hunk.</summary>
    public const int FullContextLines = 1_000_000;

    /// <summary>The well-known empty tree object; git resolves it even when the object database does not contain it.</summary>
    public const string EmptyTreeSha = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    private const string CommitLogFormat = "--format=%H%x1f%s%x1f%aI%x1f%P%x1e";

    /// <summary>
    /// Commits reachable from <paramref name="head"/> but not from <paramref name="exclude"/> (git log exclude..head),
    /// newest first, at most <paramref name="limit"/>. First-parent order is not forced; merges are included.
    /// An empty <paramref name="exclude"/> lists everything reachable from <paramref name="head"/>.
    /// </summary>
    public async Task<IReadOnlyList<GitCommitInfo>> GetCommitsAsync(
        string workingDirectory,
        string exclude,
        string head,
        int limit = 200,
        CancellationToken ct = default)
    {
        Reference(head, nameof(head));
        var range = head;
        if (!string.IsNullOrWhiteSpace(exclude))
        {
            range = Reference(exclude, nameof(exclude)) + ".." + head;
        }

        var arguments = Args(
            "log",
            "--no-color",
            "--no-show-signature",
            "--max-count=" + Math.Max(1, limit).ToString(CultureInfo.InvariantCulture),
            CommitLogFormat,
            "--end-of-options",
            range);
        var output = await RunCheckedAsync(workingDirectory, arguments, ct).ConfigureAwait(false);
        var commits = new List<GitCommitInfo>();
        foreach (var record in output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.TrimStart('\n', '\r').Split('\x1f', 4);
            if (fields.Length < 4 || fields[0].Length == 0)
            {
                continue;
            }

            var sha = fields[0];
            var parents = fields[3].Trim();
            commits.Add(new GitCommitInfo(
                sha,
                sha.Length > 7 ? sha[..7] : sha,
                fields[1],
                DateTimeOffset.TryParse(fields[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : default,
                parents.Length == 0 ? [] : parents.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        }

        return commits;
    }

    /// <summary>
    /// Like <see cref="GetTreeDiffAsync(string, string, string, CancellationToken)"/> (also accepts commit-ish on either side)
    /// with an explicit context-line count. <paramref name="fromTree"/> null means the empty tree (root commit).
    /// </summary>
    public async Task<IReadOnlyList<FileDiff>> GetTreeDiffAsync(
        string workingDirectory,
        string? fromTree,
        string toTree,
        int contextLines,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contextLines);
        var text = await GetTreeDiffTextCoreAsync(workingDirectory, fromTree ?? EmptyTreeSha, toTree, contextLines, ct).ConfigureAwait(false);
        return UnifiedDiffParser.Parse(text);
    }
}
