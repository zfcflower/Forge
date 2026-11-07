namespace Forge.IO;

/// <summary>
/// The ignore rules of one workspace, with full <c>.gitignore</c> semantics: <c>#</c> comments, <c>\</c> escapes,
/// escaped trailing spaces, <c>!</c> negation, a leading <c>/</c> or an inner separator anchoring a pattern to the
/// directory of the file it came from, a trailing <c>/</c> restricting a pattern to directories, and <c>**</c>.
/// <para>Rules come from every directory's <c>.gitignore</c> (parents first, deeper files later, later rules winning
/// inside one file) plus the root <c>.git/info/exclude</c>, which has the lowest precedence. The global
/// <c>core.excludesFile</c> is deliberately not read: that needs the git configuration.</para>
/// <para>The type is an <see cref="IPathFilter"/> for <see cref="FileWalker"/> and is safe to call from several
/// threads: the per-directory rule stacks are cached behind a lock, so a walk reads each <c>.gitignore</c> once.</para>
/// </summary>
public sealed class GitIgnore : IPathFilter
{
    private const string RulesFileName = ".gitignore";

    private readonly string root;
    private readonly Lock gate = new();
    private readonly Dictionary<string, RuleNode> nodes;
    private readonly Dictionary<string, RuleNode>.AlternateLookup<ReadOnlySpan<char>> lookup;
    private readonly RuleNode rootNode;

    /// <summary>Loads (lazily) the ignore rules that apply below <paramref name="root"/>.</summary>
    public GitIgnore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        this.root = WorkspacePath.Normalize(root);
        nodes = new Dictionary<string, RuleNode>(WorkspacePath.Comparer);
        lookup = nodes.GetAlternateLookup<ReadOnlySpan<char>>();

        var exclude = new RuleNode(
            parent: null,
            ReadRules(Path.Combine(this.root, FileWalker.VcsDirectory, "info", "exclude")),
            baseOffset: 0);
        rootNode = new RuleNode(
            exclude,
            ReadRules(Path.Combine(this.root, RulesFileName)),
            baseOffset: 0);
        nodes[string.Empty] = rootNode;
    }

    /// <summary>The workspace root the rules belong to.</summary>
    public string Root => root;

    /// <inheritdoc />
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        var separator = relativePath.LastIndexOf('/');
        var directory = separator < 0 ? default : relativePath.AsSpan(0, separator);
        var node = Node(directory);
        var path = relativePath.AsSpan();
        for (var current = node; current is not null; current = current.Parent)
        {
            var rules = current.Rules;
            var candidate = path[current.BaseOffset..];
            for (var index = rules.Length - 1; index >= 0; index--)
            {
                var rule = rules[index];
                if (rule.DirectoryOnly && !isDirectory)
                {
                    continue;
                }

                if (rule.Matcher.IsPathMatch(candidate))
                {
                    return !rule.Negated;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="relativePath"/> or one of its parent directories is ignored. A walk never needs this
    /// (an ignored directory is not entered, so nothing below it is ever offered), but an enumeration that only reports
    /// files, such as the ripgrep backend, does: git takes an ignored directory with everything inside it.
    /// </summary>
    public bool IsIgnoredWithAncestors(string relativePath, bool isDirectory)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        // Ancestors first: an ignored directory takes everything inside it with it, which is exactly what a walk does
        // by not entering the directory, so a deeper negation cannot re-include something inside it.
        var end = relativePath.Length;
        while (true)
        {
            var separator = relativePath.LastIndexOf('/', end - 1);
            if (separator <= 0)
            {
                break;
            }

            if (IsIgnored(relativePath[..separator], isDirectory: true))
            {
                return true;
            }

            end = separator;
        }

        return IsIgnored(relativePath, isDirectory);
    }

    private RuleNode Node(ReadOnlySpan<char> relativeDirectory)
    {
        lock (gate)
        {
            if (lookup.TryGetValue(relativeDirectory, out var cached))
            {
                return cached;
            }

            var relative = relativeDirectory.ToString();
            var separator = relative.LastIndexOf('/');
            var parent = separator < 0 ? rootNode : Node(relative.AsSpan(0, separator));
            var baseOffset = relative.Length == 0 ? 0 : relative.Length + 1;
            var directory = relative.Length == 0 ? root : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            var node = new RuleNode(parent, ReadRules(Path.Combine(directory, RulesFileName)), baseOffset);
            nodes[relative] = node;
            return node;
        }
    }

    private static Rule[] ReadRules(string file)
    {
        string text;
        try
        {
            if (!File.Exists(file))
            {
                return [];
            }

            text = File.ReadAllText(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }

        return Parse(text);
    }

    private static Rule[] Parse(string text)
    {
        var rules = new List<Rule>();
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            var newline = remaining.IndexOf('\n');
            var line = newline < 0 ? remaining : remaining[..newline];
            remaining = newline < 0 ? default : remaining[(newline + 1)..];

            if (!line.IsEmpty && line[^1] == '\r')
            {
                line = line[..^1];
            }

            if (TryParseLine(line) is { } rule)
            {
                rules.Add(rule);
            }
        }

        return rules.Count == 0 ? [] : rules.ToArray();
    }

    private static Rule? TryParseLine(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty || line[0] == '#')
        {
            return null;
        }

        var negated = false;
        if (line[0] == '!')
        {
            negated = true;
            line = line[1..];
        }

        line = TrimUnescapedTrailingSpaces(line);
        var directoryOnly = false;
        if (!line.IsEmpty && line[^1] == '/')
        {
            directoryOnly = true;
            line = line[..^1];
        }

        if (line.IsEmpty)
        {
            return null;
        }

        GlobMatcher matcher;
        try
        {
            matcher = GlobMatcher.Compile(line.ToString());
        }
        catch (ArgumentException)
        {
            return null;
        }

        return new Rule(matcher, negated, directoryOnly);
    }

    /// <summary>Drops the spaces at the end of a line unless a backslash quotes them.</summary>
    private static ReadOnlySpan<char> TrimUnescapedTrailingSpaces(ReadOnlySpan<char> line)
    {
        var end = line.Length;
        while (end > 0 && line[end - 1] == ' ')
        {
            var backslashes = 0;
            while (backslashes < end - 1 && line[end - 2 - backslashes] == '\\')
            {
                backslashes++;
            }

            if ((backslashes & 1) != 0)
            {
                break;
            }

            end--;
        }

        return line[..end];
    }

    private sealed class Rule(GlobMatcher matcher, bool negated, bool directoryOnly)
    {
        public GlobMatcher Matcher { get; } = matcher;

        public bool Negated { get; } = negated;

        public bool DirectoryOnly { get; } = directoryOnly;
    }

    private sealed class RuleNode(RuleNode? parent, Rule[] rules, int baseOffset)
    {
        public RuleNode? Parent { get; } = parent;

        public Rule[] Rules { get; } = rules;

        /// <summary>Length of the workspace-relative directory plus its separator, so rules can be matched below it.</summary>
        public int BaseOffset { get; } = baseOffset;
    }
}
