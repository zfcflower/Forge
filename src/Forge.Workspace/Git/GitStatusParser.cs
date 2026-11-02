namespace Forge.Workspace;

internal static class GitStatusParser
{
    public static IReadOnlyList<GitStatusEntry> Parse(string porcelain)
    {
        var entries = new List<GitStatusEntry>();
        if (string.IsNullOrEmpty(porcelain))
        {
            return entries;
        }

        var tokens = porcelain.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length < 2)
            {
                continue;
            }

            switch (token[0])
            {
                case '1':
                {
                    var fields = token.Split(' ', 9);
                    if (fields.Length < 9)
                    {
                        continue;
                    }

                    entries.Add(new GitStatusEntry(fields[8], GitStatusKind.Ordinary, Status(fields[1][0]), Status(fields[1][1])));
                    break;
                }

                case '2':
                {
                    var fields = token.Split(' ', 10);
                    if (fields.Length < 10)
                    {
                        continue;
                    }

                    var originalPath = i + 1 < tokens.Length ? tokens[++i] : null;
                    var index = Status(fields[1][0]);
                    var kind = index == GitEntryStatus.Copied ? GitStatusKind.Copied : GitStatusKind.Renamed;
                    entries.Add(new GitStatusEntry(fields[9], kind, index, Status(fields[1][1]), originalPath));
                    break;
                }

                case 'u':
                {
                    var fields = token.Split(' ', 11);
                    if (fields.Length < 11)
                    {
                        continue;
                    }

                    entries.Add(new GitStatusEntry(fields[10], GitStatusKind.Unmerged, GitEntryStatus.Unmerged, GitEntryStatus.Unmerged));
                    break;
                }

                case '?':
                {
                    entries.Add(new GitStatusEntry(token[2..], GitStatusKind.Untracked, GitEntryStatus.Unmodified, GitEntryStatus.Untracked));
                    break;
                }

                case '!':
                {
                    entries.Add(new GitStatusEntry(token[2..], GitStatusKind.Ignored, GitEntryStatus.Unmodified, GitEntryStatus.Ignored));
                    break;
                }
            }
        }

        return entries;
    }

    private static GitEntryStatus Status(char code) => code switch
    {
        'M' => GitEntryStatus.Modified,
        'T' => GitEntryStatus.TypeChanged,
        'A' => GitEntryStatus.Added,
        'D' => GitEntryStatus.Deleted,
        'R' => GitEntryStatus.Renamed,
        'C' => GitEntryStatus.Copied,
        'U' => GitEntryStatus.Unmerged,
        '?' => GitEntryStatus.Untracked,
        '!' => GitEntryStatus.Ignored,
        _ => GitEntryStatus.Unmodified,
    };
}
