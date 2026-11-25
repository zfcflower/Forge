using System.Text;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// Turns the path arguments of <c>copy</c>, <c>move</c> and <c>delete</c> into real paths: a plain path is resolved as the
/// tool's own rule says, a pattern is expanded against the workspace the way a shell would have expanded it before
/// <c>cp</c>, <c>mv</c> or <c>rm</c> saw it. <c>*</c>, <c>?</c>, <c>[..]</c>, <c>**</c> and <c>{a,b}</c> are understood; a name
/// that starts with a dot is only matched by a segment that itself starts with a dot, and the .git directory is never
/// matched or entered. A match below another match is dropped (the directory already covers it).
/// </summary>
internal static class PathPatterns
{
    private static readonly char[] Wildcards = ['*', '?', '[', '{'];

    internal static bool IsPattern(string argument) => argument.AsSpan().IndexOfAny(Wildcards) >= 0;

    /// <summary>
    /// The literal part a pattern starts from: the text before its first wildcard, cut back to the last separator
    /// (<c>src/**/*.cs</c> → <c>src</c>); empty when the pattern starts at the workspace root. Used by
    /// <see cref="CallScope"/> to judge a pattern's directory without expanding it.
    /// </summary>
    internal static string LiteralPrefix(string pattern)
    {
        var wildcard = pattern.AsSpan().IndexOfAny(Wildcards);
        if (wildcard < 0)
        {
            return pattern;
        }

        if (wildcard == 0)
        {
            return string.Empty;
        }

        var separator = pattern.LastIndexOfAny(['/', '\\'], wildcard - 1);
        return separator < 0 ? pattern[..wildcard] : pattern[..separator];
    }

    /// <summary>The paths the arguments name, in order, without duplicates; a pattern that matches nothing is an error.</summary>
    /// <param name="resolvePlain">Resolves a path that is not a pattern; it decides whether a final link is followed.</param>
    /// <param name="includeLinks">Whether a link found by a pattern is a match (move and delete act on the link itself; copy skips links).</param>
    /// <param name="allowOutside">Expands patterns outside the workspace too, for the one call the user allowed across it.</param>
    internal static List<string> Expand(
        string root,
        IReadOnlyList<string> arguments,
        Func<string, string> resolvePlain,
        bool includeLinks,
        CancellationToken ct,
        bool allowOutside = false)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(WorkspacePath.Comparer);
        foreach (var argument in arguments)
        {
            if (!IsPattern(argument))
            {
                var path = resolvePlain(argument);
                if (seen.Add(path))
                {
                    result.Add(path);
                }

                continue;
            }

            var matches = Match(root, argument, includeLinks, allowOutside, ct);
            if (matches.Count == 0)
            {
                throw new ToolArgumentException($"the pattern '{argument}' matches nothing.");
            }

            foreach (var match in matches)
            {
                if (seen.Add(match))
                {
                    result.Add(match);
                }
            }
        }

        // A match inside another match is covered by it: copying, moving or deleting the directory already includes it.
        result.Sort(StringComparer.Ordinal);
        var kept = new List<string>(result.Count);
        foreach (var path in result)
        {
            if (kept.Count > 0 && WorkspacePath.IsUnder(path, kept[^1]) && !string.Equals(path, kept[^1], WorkspacePath.Comparison))
            {
                continue;
            }

            kept.Add(path);
        }

        return kept;
    }

    private static List<string> Match(string root, string pattern, bool includeLinks, bool allowOutside, CancellationToken ct)
    {
        var matches = new List<string>();
        foreach (var alternative in ExpandBraces(Path.DirectorySeparatorChar == '/' ? pattern : pattern.Replace(Path.DirectorySeparatorChar, '/')))
        {
            var anchor = Path.IsPathRooted(alternative) ? Path.GetPathRoot(alternative.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty : string.Empty;
            var segments = alternative[anchor.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            var literal = 0;
            while (literal < segments.Length - 1 && segments[literal].AsSpan().IndexOfAny(['*', '?', '[']) < 0)
            {
                literal++;
            }

            var prefix = anchor + string.Join('/', segments.Take(literal));
            var baseDirectory = prefix.Length == 0 ? root : WorkspacePath.Resolve(root, prefix, rootIsReal: true, allowOutside);
            if (!Directory.Exists(baseDirectory))
            {
                continue;
            }

            var rest = segments.Skip(literal).ToArray();
            var matcher = GlobMatcher.Compile(string.Join('/', rest));
            var deep = rest.Contains("**");
            var allowDot = rest.Any(segment => segment.StartsWith('.'));
            Walk(baseDirectory, string.Empty, deep ? 64 : rest.Length, matcher, allowDot, includeLinks, matches, ct);
        }

        return matches;
    }

    private static void Walk(
        string directory,
        string relative,
        int depthLeft,
        GlobMatcher matcher,
        bool allowDot,
        bool includeLinks,
        List<string> matches,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            var name = entry.Name;
            if (name.Equals(".git", WorkspacePath.Comparison) || (!allowDot && name.StartsWith('.')))
            {
                continue;
            }

            var isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
            var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
            var path = relative.Length == 0 ? name : relative + "/" + name;
            if (matcher.IsMatch(path) && (includeLinks || !isLink))
            {
                matches.Add(entry.FullName);
            }

            if (isDirectory && !isLink && depthLeft > 1)
            {
                Walk(entry.FullName, path, depthLeft - 1, matcher, allowDot, includeLinks, matches, ct);
            }
        }
    }

    /// <summary>Expands every <c>{a,b}</c> group (nested groups too) into the patterns it stands for.</summary>
    internal static List<string> ExpandBraces(string pattern)
    {
        var open = pattern.IndexOf('{');
        if (open < 0)
        {
            return [pattern];
        }

        var depth = 0;
        var close = -1;
        var commas = new List<int>();
        for (var index = open; index < pattern.Length; index++)
        {
            switch (pattern[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    break;
                case ',' when depth == 1:
                    commas.Add(index);
                    break;
            }

            if (depth == 0)
            {
                close = index;
                break;
            }
        }

        if (close < 0 || commas.Count == 0)
        {
            return [pattern];
        }

        var head = pattern[..open];
        var tail = pattern[(close + 1)..];
        var result = new List<string>();
        var start = open + 1;
        foreach (var stop in commas.Append(close))
        {
            foreach (var item in ExpandBraces(pattern[start..stop]))
            {
                result.AddRange(ExpandBraces(new StringBuilder(head).Append(item).Append(tail).ToString()));
            }

            start = stop + 1;
        }

        return result;
    }
}
