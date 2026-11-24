namespace Forge.IO;

/// <summary>
/// What a walk skips: the workspace's ignore rules (<see cref="GitIgnore"/>) plus the directories named as build output
/// (<see cref="Tools.ToolOptions.IgnoredDirectories"/>). Both apply to everything below the path a caller named and never
/// to that path itself, so searching <c>src</c> prunes <c>src/**/obj</c> while searching <c>obj</c> still searches it.
/// <para>The rule used to be "a named path is searched as it is", which made a search of <c>src</c> walk every
/// <c>bin</c>/<c>obj</c> below it (61% of the files in this repository). The name list is a preference, not a constant,
/// because the conventional output directory differs by toolchain.</para>
/// </summary>
public sealed class SearchFilter : IPathFilter
{
    private readonly GitIgnore rules;
    private readonly HashSet<string> names;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> lookup;

    /// <param name="root">The workspace root the ignore rules and the reported paths belong to.</param>
    /// <param name="ignoredDirectories">Directory names never descended into; empty disables the name rule.</param>
    public SearchFilter(string root, IReadOnlyList<string> ignoredDirectories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(ignoredDirectories);
        rules = new GitIgnore(root);
        names = new HashSet<string>(ignoredDirectories, StringComparer.OrdinalIgnoreCase);
        lookup = names.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <inheritdoc />
    public bool IsIgnored(string relativePath, bool isDirectory) =>
        (isDirectory && IsBuildOutput(Name(relativePath))) || rules.IsIgnored(relativePath, isDirectory);

    /// <summary>
    /// True when the path or one of its parent directories is skipped. A walk never needs this (a skipped directory is not
    /// entered), but ripgrep reports files only, so a file below <c>obj</c> has to be recognized by its ancestors.
    /// </summary>
    public bool IsIgnoredWithAncestors(string relativePath, bool isDirectory) =>
        rules.IsIgnoredWithAncestors(relativePath, isDirectory) || HasBuildOutputAncestor(relativePath, isDirectory);

    /// <summary>The name of one directory path, without the separators around it.</summary>
    private static ReadOnlySpan<char> Name(ReadOnlySpan<char> relativePath)
    {
        var separator = relativePath.LastIndexOfAny('/', '\\');
        return separator < 0 ? relativePath : relativePath[(separator + 1)..];
    }

    private bool HasBuildOutputAncestor(ReadOnlySpan<char> relativePath, bool isDirectory)
    {
        if (names.Count == 0)
        {
            return false;
        }

        var end = relativePath.Length;
        while (end > 0)
        {
            var start = relativePath[..end].LastIndexOfAny('/', '\\') + 1;
            if ((isDirectory || start < end) && IsBuildOutput(relativePath[start..end]))
            {
                return true;
            }

            end = start == 0 ? 0 : start - 1;
        }

        return false;
    }

    private bool IsBuildOutput(ReadOnlySpan<char> name) => name.Length > 0 && lookup.Contains(name);
}
