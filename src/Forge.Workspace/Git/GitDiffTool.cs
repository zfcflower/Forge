using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Shows what the branch changes against its base plus everything not committed yet, for self-review.</summary>
internal sealed class GitDiffTool(GitCli git, GitReviewLedger ledger) : GitToolBase(
    git,
    GitTools.DiffName,
    "Show the changes of this branch: git diff <base>...HEAD followed by the uncommitted working-tree diff, with " +
    "untracked files included. base defaults to the repository's default branch. A read shows whole-file diffs up to " +
    "about 24,000 characters and names every file it left out; read those with paths. Pass stat_only for just the file " +
    "list. PR creation requires that every changed file was shown at the current HEAD (reads with paths count), " +
    "starting from one read without paths.",
    """
    {"type":"object","properties":{"base":{"type":"string","description":"Commit or ref to compare against, e.g. origin/main; default the repository's default branch."},"paths":{"type":"array","items":{"type":"string"},"description":"Limit the diff to these paths."},"stat_only":{"type":"boolean","description":"Return per-file line counts instead of the diff text; default false."}},"additionalProperties":false}
    """,
    ToolSideEffect.ReadOnly)
{
    /// <summary>The most diff text one read shows; a single file larger than this is still shown whole when it comes first.</summary>
    internal const int MaxChars = 24_000;

    /// <summary>
    /// A file whose diff is longer than this is moved out of the tool result into an artifact by the agent loop (its default
    /// <c>ArtifactThresholdChars</c>), so the model sees only a preview and the gate cannot tell what it read afterwards. Such
    /// a file is still shown first, but it is neither counted as covered nor required: it is named in the output so the model
    /// reads the whole artifact, and the gate does not wait for a coverage it can never observe.
    /// </summary>
    internal const int SpillChars = 32_000;

    private const int MaxListed = 100;

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
        var ticket = ledger.BeginRead();
        DiffRead? read = null;
        try
        {
            if (statOnly)
            {
                text = await StatAsync(directory, range, paths, head, ct).ConfigureAwait(false);
            }
            else
            {
                read = await TextAsync(directory, range, paths, head, ct).ConfigureAwait(false);
                text = read.Text;
            }
        }
        catch (GitOutputTruncatedException)
        {
            return new ToolResult(
                $"The diff against {baseName ?? "the working tree"} is larger than git's capture limit; narrow it with " +
                "paths, or read stat_only first.",
                IsError: true);
        }

        if (usedDefaultBase && read is not null && head is not null)
        {
            // Record the HEAD the diff was computed from, not a re-read one: a commit that landed while the diff was
            // being built is not part of what the agent just read.
            if (paths.Count == 0)
            {
                ledger.RecordFullRead(context.SessionId, directory, head, ticket, read.All, read.Shown);
            }
            else
            {
                ledger.RecordShown(context.SessionId, directory, head, ticket, read.Shown);
            }
        }

        return new ToolResult(text.Length == 0
            ? baseName is null
                ? "No commits yet, and nothing uncommitted."
                : $"No changes against {baseName} and nothing uncommitted."
            : text);
    }

    /// <summary>What one text read produced: the text, every changed file it found and those it showed in full.</summary>
    private sealed record DiffRead(string Text, IReadOnlyList<string> All, IReadOnlyList<string> Shown);

    private sealed class Unit(string path)
    {
        public string Path { get; } = path;

        public List<(bool Committed, FileDiff File)> Pieces { get; } = [];
    }

    private async Task<DiffRead> TextAsync(
        string directory,
        string? range,
        IReadOnlyList<string> paths,
        string? head,
        CancellationToken ct)
    {
        // The file lists are independent git calls, so they run side by side; untracked files come with the uncommitted one.
        var committedTask = range is null
            ? Task.FromResult<IReadOnlyList<FileDiff>>([])
            : Git.GetDiffAsync(directory, new GitDiffOptions { From = range, Paths = paths }, ct);
        var stagedTask = head is null
            ? Git.GetDiffAsync(directory, new GitDiffOptions { Staged = true, Paths = paths }, ct)
            : Task.FromResult<IReadOnlyList<FileDiff>>([]);
        var uncommittedTask = Git.GetDiffAsync(
            directory,
            new GitDiffOptions { From = head, Paths = paths, IncludeUntracked = true },
            ct);
        await Task.WhenAll(committedTask, stagedTask, uncommittedTask).ConfigureAwait(false);

        // One unit per path: a file changed both in commits and in the worktree is shown (or left out) as a whole, so
        // "shown" means every change of that file was shown.
        var units = new List<Unit>();
        var byPath = new Dictionary<string, Unit>(StringComparer.Ordinal);
        void Add(bool committed, IReadOnlyList<FileDiff> files)
        {
            foreach (var file in files)
            {
                if (!byPath.TryGetValue(file.Path, out var unit))
                {
                    unit = new Unit(file.Path);
                    byPath[file.Path] = unit;
                    units.Add(unit);
                }

                unit.Pieces.Add((committed, file));
            }
        }

        Add(true, committedTask.Result);
        Add(false, stagedTask.Result);
        Add(false, uncommittedTask.Result);

        var blocks = new List<string>(units.Count);
        var used = 0;
        var fit = 0;
        for (; fit < units.Count; fit++)
        {
            var block = RenderUnit(units[fit], range);
            if (fit > 0 && used + block.Length > MaxChars)
            {
                break;
            }

            blocks.Add(block);
            used += block.Length;
        }

        // The "not shown" footer counts against the cap too: give up the last shown files until both fit (the first stays).
        var footer = new StringBuilder();
        while (fit < units.Count)
        {
            footer.Clear();
            AppendLeftOut(footer, fit, units.Count, units.GetRange(fit, units.Count - fit));
            if (fit <= 1 || used + footer.Length <= MaxChars)
            {
                break;
            }

            fit--;
            used -= blocks[fit].Length;
            blocks.RemoveAt(fit);
        }

        var text = new StringBuilder(used + footer.Length);
        var shown = new List<string>(fit);
        string? oversized = null;
        for (var i = 0; i < fit; i++)
        {
            if (blocks[i].Length > SpillChars)
            {
                oversized = units[i].Path;
                text.Append("# ").Append(oversized).Append(": this diff is longer than ")
                    .Append(SpillChars.ToString(CultureInfo.InvariantCulture))
                    .Append(" characters, so it may arrive as a preview with an artifact id; read the whole artifact with read_artifact. It is not tracked as covered by the pull-request gate.\n");
            }
            else
            {
                shown.Add(units[i].Path);
            }

            text.Append(blocks[i]);
        }

        text.Append(footer);

        var all = new List<string>(units.Count);
        foreach (var unit in units)
        {
            if (!string.Equals(unit.Path, oversized, StringComparison.Ordinal))
            {
                all.Add(unit.Path);
            }
        }

        return new DiffRead(text.ToString(), all, shown);
    }

    private static string RenderUnit(Unit unit, string? range)
    {
        var block = new StringBuilder(512);
        foreach (var (committed, file) in unit.Pieces)
        {
            block.Append(committed ? "# committed, " + range : "# uncommitted").Append('\n').Append(UnifiedDiffText.Render(file));
        }

        return block.ToString();
    }

    private static void AppendLeftOut(StringBuilder text, int shownCount, int totalCount, List<Unit> left)
    {
        if (text.Length > 0 && text[^1] != '\n')
        {
            text.Append('\n');
        }

        text.Append("\n# ").Append(shownCount.ToString(CultureInfo.InvariantCulture)).Append(" of ")
            .Append(totalCount.ToString(CultureInfo.InvariantCulture))
            .Append(" files shown; this read is capped at about ").Append(MaxChars.ToString(CultureInfo.InvariantCulture))
            .Append(" characters. Not shown yet (read them with git_diff and paths; a file is covered once its diff was shown):\n");
        var listed = 0;
        foreach (var unit in left)
        {
            if (listed == MaxListed)
            {
                text.Append("  ... and ").Append((left.Count - listed).ToString(CultureInfo.InvariantCulture))
                    .Append(" more; use stat_only for the whole list.\n");
                break;
            }

            var added = 0;
            var removed = 0;
            var binary = false;
            foreach (var (_, file) in unit.Pieces)
            {
                added += file.AddedLineCount;
                removed += file.RemovedLineCount;
                binary |= file.IsBinary;
            }

            text.Append("  ").Append(unit.Path).Append(" | ");
            if (binary)
            {
                text.Append("binary");
            }
            else
            {
                text.Append('+').Append(added.ToString(CultureInfo.InvariantCulture))
                    .Append(" -").Append(removed.ToString(CultureInfo.InvariantCulture));
            }

            text.Append('\n');
            listed++;
        }
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
