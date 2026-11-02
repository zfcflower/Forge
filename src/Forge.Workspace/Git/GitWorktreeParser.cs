namespace Forge.Workspace;

internal static class GitWorktreeParser
{
    public static IReadOnlyList<GitWorktree> Parse(string porcelain)
    {
        var worktrees = new List<GitWorktree>();
        string? path = null;
        string? head = null;
        string? branch = null;
        bool detached = false, bare = false, locked = false, prunable = false;
        string? lockReason = null, prunableReason = null;

        void Flush()
        {
            if (path is not null)
            {
                worktrees.Add(new GitWorktree(
                    WorkspacePaths.Normalize(path),
                    head,
                    branch,
                    detached,
                    locked,
                    lockReason,
                    prunable,
                    prunableReason,
                    bare));
            }

            path = null;
            head = null;
            branch = null;
            detached = false;
            bare = false;
            locked = false;
            prunable = false;
            lockReason = null;
            prunableReason = null;
        }

        foreach (var rawLine in porcelain.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            var space = line.IndexOf(' ');
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? string.Empty : line[(space + 1)..];
            switch (key)
            {
                case "worktree":
                    Flush();
                    path = GitQuoting.Unquote(value);
                    break;
                case "HEAD":
                    head = value;
                    break;
                case "branch":
                    branch = value.StartsWith("refs/heads/", StringComparison.Ordinal) ? value["refs/heads/".Length..] : value;
                    break;
                case "detached":
                    detached = true;
                    break;
                case "bare":
                    bare = true;
                    break;
                case "locked":
                    locked = true;
                    lockReason = value.Length == 0 ? null : value;
                    break;
                case "prunable":
                    prunable = true;
                    prunableReason = value.Length == 0 ? null : value;
                    break;
            }
        }

        Flush();
        return worktrees;
    }
}
