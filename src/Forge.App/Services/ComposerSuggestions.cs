namespace Forge.App.Services;

/// <summary>What kind of picker a composer token opens.</summary>
internal enum ComposerTokenKind
{
    None,
    Slash,
    Mention,
}

/// <summary>The token under the caret: its kind, where it starts (the trigger character) and the text after the trigger.</summary>
internal readonly record struct ComposerToken(ComposerTokenKind Kind, int Start, string Query)
{
    public static ComposerToken None => new(ComposerTokenKind.None, -1, string.Empty);

    public int Length => Query.Length + 1;
}

/// <summary>
/// One entry of a slash picker: a command or a skill. <see cref="Path"/> is the skill's <c>SKILL.md</c> (empty for a
/// command), so the settings page can say where a skill came from without discovering it a second time.
/// </summary>
internal sealed record SlashEntry(string Name, string Description, bool IsSkill, string Path = "");

/// <summary>
/// The pure logic behind the composer's <c>/</c> and <c>@</c> pickers: finding the token under the caret, filtering
/// commands, skills and workspace files against it, and rewriting the draft when an entry is chosen.
/// </summary>
internal static class ComposerSuggestions
{
    public const int MaxFileResults = 50;

    /// <summary>
    /// The trigger token that ends at <paramref name="caret"/>: a word starting with <c>/</c> or <c>@</c>, where the
    /// trigger is the first character of the text or follows whitespace (so <c>a/b</c> and <c>me@host</c> do not open).
    /// </summary>
    public static ComposerToken FindToken(string? text, int caret)
    {
        if (string.IsNullOrEmpty(text) || caret <= 0 || caret > text.Length)
        {
            return ComposerToken.None;
        }

        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        if (start == caret)
        {
            return ComposerToken.None;
        }

        var trigger = text[start];
        var query = text.Substring(start + 1, caret - start - 1);
        return trigger switch
        {
            '/' when !query.Contains('/', StringComparison.Ordinal) => new ComposerToken(ComposerTokenKind.Slash, start, query),
            '@' when !query.Contains('@', StringComparison.Ordinal) => new ComposerToken(ComposerTokenKind.Mention, start, query),
            _ => ComposerToken.None,
        };
    }

    /// <summary>Entries whose name contains the query (case-insensitive); name-prefix matches first, then commands before skills, then by name.</summary>
    public static IReadOnlyList<SlashEntry> FilterSlash(IEnumerable<SlashEntry> entries, string query)
    {
        ArgumentNullException.ThrowIfNull(entries);
        query ??= string.Empty;
        return entries
            .Select(entry => (Entry: entry, Rank: SlashRank(entry, query)))
            .Where(pair => pair.Rank >= 0)
            .OrderBy(pair => pair.Rank)
            .ThenBy(pair => pair.Entry.IsSkill)
            .ThenBy(pair => pair.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Entry)
            .ToList();
    }

    /// <summary>
    /// Up to <paramref name="limit"/> files matching the query: file-name prefix, then file-name substring, then path
    /// substring, then the query's characters in order anywhere in the path (fuzzy). Shorter paths win ties.
    /// </summary>
    public static IReadOnlyList<string> FilterFiles(IEnumerable<string> files, string query, int limit = MaxFileResults)
    {
        ArgumentNullException.ThrowIfNull(files);
        query = (query ?? string.Empty).Replace('\\', '/');
        return files
            .Select(path => (Path: path, Rank: FileRank(path, query)))
            .Where(pair => pair.Rank >= 0)
            .OrderBy(pair => pair.Rank)
            .ThenBy(pair => pair.Path.Length)
            .ThenBy(pair => pair.Path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, limit))
            .Select(pair => pair.Path)
            .ToList();
    }

    /// <summary>
    /// The draft with <paramref name="token"/> replaced by the trigger, <paramref name="value"/> and one space, plus the
    /// caret position after that space.
    /// </summary>
    public static (string Text, int Caret) Apply(string text, ComposerToken token, string value)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (token.Kind == ComposerTokenKind.None || token.Start < 0 || token.Start + token.Length > text.Length)
        {
            return (text, text.Length);
        }

        var trigger = token.Kind == ComposerTokenKind.Slash ? "/" : "@";
        var after = text[(token.Start + token.Length)..];
        if (after.Length > 0 && char.IsWhiteSpace(after[0]))
        {
            after = after[1..];
        }

        var inserted = trigger + value + " ";
        return (text[..token.Start] + inserted + after, token.Start + inserted.Length);
    }

    private static int SlashRank(SlashEntry entry, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        if (entry.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1 : -1;
    }

    private static int FileRank(string path, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        var slash = path.LastIndexOf('/');
        var name = slash < 0 ? path : path[(slash + 1)..];
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (path.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return IsSubsequence(path, query) ? 3 : -1;
    }

    private static bool IsSubsequence(string path, string query)
    {
        var at = 0;
        foreach (var ch in path)
        {
            if (at < query.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(query[at]))
            {
                at++;
            }
        }

        return at == query.Length;
    }
}

/// <summary>
/// Skills the composer's slash picker offers: every <c>SKILL.md</c> one level under <c>.github/skills</c> and
/// <c>.forge/skills</c> of the workspace and under <c>skills</c> of the user directory. The name comes from the
/// front matter's <c>name:</c> (else the folder name), the description from <c>description:</c>.
/// </summary>
internal static class SkillCatalog
{
    public static IReadOnlyList<SlashEntry> Discover(string? workspace, string? userDirectory)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            roots.Add(Path.Combine(workspace, ".github", "skills"));
            roots.Add(Path.Combine(workspace, ".forge", "skills"));
        }

        if (!string.IsNullOrWhiteSpace(userDirectory))
        {
            roots.Add(Path.Combine(userDirectory, "skills"));
        }

        var found = new List<SlashEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            string[] folders;
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                folders = Directory.GetDirectories(root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
            {
                var file = Path.Combine(folder, "SKILL.md");
                string text;
                try
                {
                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    text = File.ReadAllText(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var (name, description) = ParseFrontMatter(text);
                name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(folder) : name;
                if (names.Add(name))
                {
                    found.Add(new SlashEntry(name, description ?? string.Empty, IsSkill: true, file));
                }
            }
        }

        return found;
    }

    /// <summary>The <c>name</c> and <c>description</c> of a leading <c>---</c> block; folded (<c>&gt;</c>, <c>|</c>) values are joined.</summary>
    internal static (string? Name, string? Description) ParseFrontMatter(string text)
    {
        var lines = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return (null, null);
        }

        string? name = null;
        string? description = null;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim() == "---")
            {
                break;
            }

            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value is ">" or ">-" or "|" or "|-" or ">+" or "|+" || value.Length == 0)
            {
                var parts = new List<string>();
                while (i + 1 < lines.Length && lines[i + 1].Trim() != "---" &&
                       (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0])))
                {
                    i++;
                    if (lines[i].Trim().Length > 0)
                    {
                        parts.Add(lines[i].Trim());
                    }
                }

                value = string.Join(' ', parts);
            }

            value = Unquote(value);
            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
            {
                name = value;
            }
            else if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase))
            {
                description = value;
            }
        }

        return (name, description);
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')
            ? value[1..^1]
            : value;
}
