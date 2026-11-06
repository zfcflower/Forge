using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Shows what the branch changes against its base plus everything not committed yet, for self-review.</summary>
internal sealed class GitDiffTool(GitCli git, GitReviewLedger ledger) : GitToolBase(
    git,
    GitTools.DiffName,
    "Show the changes of this branch: git diff <base>...HEAD followed by the uncommitted working-tree diff, with " +
    "untracked files included. base defaults to the repository's default branch. Narrow with paths, or pass " +
    "stat_only for just the file list. A full read (no paths, stat_only false) records HEAD as self-reviewed, which " +
    "PR creation requires.",
    """
    {"type":"object","properties":{"base":{"type":"string","description":"Commit or ref to compare against, e.g. origin/main; default the repository's default branch."},"paths":{"type":"array","items":{"type":"string"},"description":"Limit the diff to these paths."},"stat_only":{"type":"boolean","description":"Return per-file line counts instead of the diff text; default false."}},"additionalProperties":false}
    """,
    ToolSideEffect.ReadOnly)
{
    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments, "base", "paths", "stat_only");

        var directory = context.WorkingDirectory;
        var paths = GitToolJson.Strings(arguments, "paths");
        var statOnly = GitToolJson.Boolean(arguments, "stat_only", false);
        var requested = GitToolJson.String(arguments, "base");
        var usedDefaultBase = string.IsNullOrWhiteSpace(requested);
        var baseName = usedDefaultBase
            ? await Git.GetDefaultBranchAsync(directory, ct).ConfigureAwait(false)
            : requested;
        var head = await Git.RevParseAsync(directory, "HEAD", ct).ConfigureAwait(false);

        string? range = null;
        if (baseName is null)
        {
            // A repository without commits has no base to compare with; the uncommitted tree is the whole change.
            if (head is not null)
            {
                return new ToolResult(
                    "No default branch could be resolved (no origin/HEAD, main, master or trunk); pass base explicitly.",
                    IsError: true);
            }
        }
        else
        {
            if (baseName[0] == '-')
            {
                return new ToolResult(
                    $"Base '{baseName}' is not a revision: a leading '-' would be read as a git option.",
                    IsError: true);
            }

            if (await Git.RevParseAsync(directory, baseName, ct).ConfigureAwait(false) is null)
            {
                return new ToolResult(
                    $"Base '{baseName}' does not resolve in this repository; fetch first, or pass base explicitly.",
                    IsError: true);
            }

            range = baseName + "...HEAD";
        }

        string text;
        try
        {
            text = statOnly
                ? await StatAsync(directory, range, paths, head, ct).ConfigureAwait(false)
                : await TextAsync(directory, range, paths, head, ct).ConfigureAwait(false);
        }
        catch (GitOutputTruncatedException)
        {
            return new ToolResult(
                $"The diff against {baseName ?? "the working tree"} is larger than git's capture limit; narrow it with " +
                "paths, or read stat_only first.",
                IsError: true);
        }

        if (usedDefaultBase && !statOnly && paths.Count == 0)
        {
            // Record the HEAD the diff was computed from, not a re-read one: a commit that landed while the diff was
            // being built is not part of what the agent just read.
            if (head is not null)
            {
                ledger.RecordReviewed(context.SessionId, directory, head);
            }
        }

        return new ToolResult(text.Length == 0
            ? baseName is null
                ? "No commits yet, and nothing uncommitted."
                : $"No changes against {baseName} and nothing uncommitted."
            : text);
    }

    private async Task<string> TextAsync(
        string directory,
        string? range,
        IReadOnlyList<string> paths,
        string? head,
        CancellationToken ct)
    {
        var text = new StringBuilder(8 * 1024);

        if (range is not null)
        {
            AppendBlock(text, await Git.GetDiffTextAsync(
                directory,
                new GitDiffOptions { From = range, Paths = paths },
                ct).ConfigureAwait(false));
        }

        AppendBlock(text, await Git.GetDiffTextAsync(
            directory,
            new GitDiffOptions { From = head, Paths = paths },
            ct).ConfigureAwait(false));

        if (head is null)
        {
            // An unborn branch has nothing to compare HEAD with, so staged files only show in the cached diff.
            AppendBlock(text, await Git.GetDiffTextAsync(
                directory,
                new GitDiffOptions { Staged = true, Paths = paths },
                ct).ConfigureAwait(false));
        }

        var status = await Git.GetStatusAsync(directory, includeUntracked: true, ct).ConfigureAwait(false);
        var untracked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in status)
        {
            if (entry.Kind == GitStatusKind.Untracked)
            {
                untracked.Add(entry.Path);
            }
        }

        if (untracked.Count > 0)
        {
            var files = await Git.GetDiffAsync(
                directory,
                new GitDiffOptions { From = head, Paths = paths, IncludeUntracked = true },
                ct).ConfigureAwait(false);
            foreach (var file in files)
            {
                if (untracked.Contains(file.Path))
                {
                    AppendBlock(text, UnifiedDiffText.Render(file));
                }
            }
        }

        return text.ToString();
    }

    private async Task<string> StatAsync(
        string directory,
        string? range,
        IReadOnlyList<string> paths,
        string? head,
        CancellationToken ct)
    {
        var text = new StringBuilder(512);
        if (range is not null)
        {
            text.Append("# committed, ").Append(range).Append('\n');
            UnifiedDiffText.AppendStat(text, await Git.GetDiffAsync(
                directory,
                new GitDiffOptions { From = range, Paths = paths },
                ct).ConfigureAwait(false));
        }

        text.Append(head is null ? "# uncommitted\n" : "# uncommitted, against HEAD\n");
        UnifiedDiffText.AppendStat(text, await Git.GetDiffAsync(
            directory,
            new GitDiffOptions { From = head, Paths = paths, IncludeUntracked = true },
            ct).ConfigureAwait(false));

        return text.ToString();
    }

    private static void AppendBlock(StringBuilder text, string block)
    {
        if (block.Length == 0)
        {
            return;
        }

        if (text.Length > 0 && text[^1] != '\n')
        {
            text.Append('\n');
        }

        text.Append(block);
        if (text[^1] != '\n')
        {
            text.Append('\n');
        }
    }
}
