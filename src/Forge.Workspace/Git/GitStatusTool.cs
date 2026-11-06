using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Reads the worktree state the agent needs before committing, pushing or resolving conflicts.</summary>
internal sealed class GitStatusTool(GitCli git) : GitToolBase(
    git,
    GitTools.StatusName,
    "Report the worktree's git state: current branch, HEAD, upstream with ahead/behind since the last fetch, remotes, " +
    "any in-progress rebase/merge, conflicted files, and the changed files (first 200). Takes no arguments.",
    """{"type":"object","properties":{},"additionalProperties":false}""",
    ToolSideEffect.ReadOnly)
{
    private const int MaxChangeLines = 200;

    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments);
        var directory = context.WorkingDirectory;

        var status = await Git.GetBranchStatusAsync(directory, ct).ConfigureAwait(false);
        var changes = await Git.GetStatusAsync(directory, ct).ConfigureAwait(false);
        var conflicts = await Git.GetConflictStateAsync(directory, ct).ConfigureAwait(false);
        var remotes = await Git.GetRemotesAsync(directory, ct).ConfigureAwait(false);

        var text = new StringBuilder(1024);
        AppendBranch(text, status);

        if (remotes.Count == 0)
        {
            text.Append("Remotes: none\n");
        }
        else
        {
            text.Append("Remotes: ");
            for (var i = 0; i < remotes.Count; i++)
            {
                var remote = remotes[i];
                if (i > 0)
                {
                    text.Append(", ");
                }

                text.Append(remote.Name).Append(" -> ").Append(remote.FetchUrl);
                if (remote.PushUrl is { } push && !string.Equals(push, remote.FetchUrl, StringComparison.Ordinal))
                {
                    text.Append(" (push ").Append(push).Append(')');
                }
            }

            text.Append('\n');
        }

        if (conflicts.InProgress)
        {
            text.Append("Operation: ").Append(GitCli.OperationName(conflicts.Operation)).Append(" in progress\n");
        }

        if (conflicts.ConflictedPaths.Count > 0)
        {
            text.Append("Conflicted files: ");
            var listed = Math.Min(conflicts.ConflictedPaths.Count, MaxChangeLines);
            for (var i = 0; i < listed; i++)
            {
                if (i > 0)
                {
                    text.Append(", ");
                }

                text.Append(conflicts.ConflictedPaths[i]);
            }

            if (conflicts.ConflictedPaths.Count > listed)
            {
                text.Append(", … and ")
                    .Append((conflicts.ConflictedPaths.Count - listed).ToString(CultureInfo.InvariantCulture))
                    .Append(" more");
            }

            text.Append('\n');
        }

        AppendChanges(text, changes);
        return new ToolResult(text.ToString().TrimEnd('\n'));
    }

    private static void AppendBranch(StringBuilder text, GitBranchStatus status)
    {
        if (status.IsDetached)
        {
            text.Append("Branch: (detached HEAD");
            if (status.HeadCommit is { } detachedHead)
            {
                text.Append(" at ").Append(detachedHead);
            }

            text.Append(")\n");
        }
        else
        {
            text.Append("Branch: ").Append(status.Branch ?? "(unknown)").Append('\n');
        }

        text.Append("HEAD: ").Append(status.HeadCommit ?? "(no commits yet)").Append('\n');
        if (status.Upstream is null)
        {
            text.Append("Upstream: none\n");
            return;
        }

        text.Append("Upstream: ").Append(status.Upstream);
        if (status.UpstreamGone)
        {
            text.Append(" (gone: the remote branch no longer exists)");
        }
        else if (status.Ahead is { } ahead && status.Behind is { } behind)
        {
            text.Append(", ahead ").Append(ahead).Append(", behind ").Append(behind).Append(" (as of the last fetch)");
        }

        text.Append('\n');
    }

    private static void AppendChanges(StringBuilder text, IReadOnlyList<GitStatusEntry> changes)
    {
        var visible = 0;
        foreach (var change in changes)
        {
            if (change.Kind != GitStatusKind.Ignored)
            {
                visible++;
            }
        }

        if (visible == 0)
        {
            text.Append("Changes: none (working tree clean)");
            return;
        }

        text.Append("Changes: ").Append(visible.ToString(CultureInfo.InvariantCulture)).Append('\n');
        var lines = new List<string>(Math.Min(visible, MaxChangeLines) + 1);
        foreach (var change in changes)
        {
            if (change.Kind == GitStatusKind.Ignored)
            {
                continue;
            }

            if (lines.Count == MaxChangeLines)
            {
                lines.Add("  … and " + (visible - MaxChangeLines).ToString(CultureInfo.InvariantCulture) + " more");
                break;
            }

            var line = "  " + Code(change) + " " + change.Path;
            lines.Add(change.OriginalPath is { } original ? line + " (from " + original + ")" : line);
        }

        text.Append(string.Join('\n', lines));
    }

    private static string Code(GitStatusEntry entry) => entry.Kind switch
    {
        GitStatusKind.Untracked => "??",
        GitStatusKind.Ignored => "!!",
        GitStatusKind.Unmerged => "UU",
        _ => new string([Letter(entry.IndexStatus), Letter(entry.WorkTreeStatus)]),
    };

    private static char Letter(GitEntryStatus status) => status switch
    {
        GitEntryStatus.Modified => 'M',
        GitEntryStatus.Added => 'A',
        GitEntryStatus.Deleted => 'D',
        GitEntryStatus.Renamed => 'R',
        GitEntryStatus.Copied => 'C',
        GitEntryStatus.TypeChanged => 'T',
        GitEntryStatus.Unmerged => 'U',
        GitEntryStatus.Untracked => '?',
        GitEntryStatus.Ignored => '!',
        _ => ' ',
    };
}
