using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// <c>find</c>: every entry below a directory whose workspace-relative path matches a glob, in walk order (the search
/// is not sorted, so it never buffers a whole tree). Ignore rules apply through <see cref="GitIgnore"/> to a search of the
/// workspace root only — a path the caller named is searched as it is, the rule <c>ls</c> follows, and that also means an
/// ignored directory is never entered below the root. Directories are printed with a trailing <c>/</c>. The listing stops at
/// the requested limit and says so.
/// </summary>
public sealed class FindTool : ToolBase, IBoundedOutputTool
{
    private const string DefaultPattern = "*";

    private const string Schema =
        """{"type":"object","properties":{"pattern":{"type":"string","description":"Path glob: `*` matches inside one path segment, `**` across segments, `?` one character, `[...]` a class. A pattern without `/` matches a name at any depth; default `*`."},"path":{"type":"string","description":"Directory to search, relative to the workspace root; default the root."},"depth":{"type":"integer","description":"Levels to descend; default 16, at most 64."},"limit":{"type":"integer","description":"Maximum entries; default 1000, at most 10000."}},"additionalProperties":false}""";

    private readonly Func<ToolOptions> options;

    public FindTool()
        : this(ToolOptions.Default)
    {
    }

    public FindTool(ToolOptions options)
        : this(Fixed(options))
    {
    }

    public FindTool(Func<ToolOptions> options)
        : base(
            "find",
            "Find files and directories by path glob below a directory, one workspace-relative path per line, in walk "
            + "order. Applies .gitignore and the build-output directory list below the path it is given, never to that "
            + "path itself: a path named in full is searched as-is, like `ls`. Directories end with `/`. Past the limit "
            + "it reports the total count.",
            Schema,
            ToolSideEffect.ReadOnly)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    protected override ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var bounds = options();
        var pattern = ToolJson.OptionalString(arguments, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
        {
            pattern = DefaultPattern;
        }

        var requested = ToolJson.OptionalString(arguments, "path") ?? ".";
        var depth = ToolJson.OptionalInt(
            arguments,
            "depth",
            Math.Clamp(ToolOptions.DefaultWalkDepth, 1, ToolOptions.MaxWalkDepth),
            1,
            ToolOptions.MaxWalkDepth);
        var limit = ToolJson.OptionalInt(
            arguments,
            "limit",
            Math.Clamp(bounds.FindMaxResults, 1, ToolOptions.MaxFindMaxResults),
            1,
            ToolOptions.MaxFindMaxResults);

        var matcher = GlobMatcher.Compile(pattern);
        var root = WorkspacePath.ResolveRealPath(Root(context));
        var start = WorkspacePath.Resolve(root, requested);
        if (!Directory.Exists(start))
        {
            return new ValueTask<ToolResult>(new ToolResult(
                File.Exists(start) ? $"find: '{requested}' is a file, not a directory." : $"find: '{requested}' does not exist.",
                IsError: true));
        }

        // The rules apply below the path the caller named, never to it: naming `obj` still searches it, which is what
        // `grep` and `ls` do too.
        var ignore = new SearchFilter(root, bounds.IgnoredDirectories);
        var lines = new List<string>(Math.Min(limit, 256));
        var walked = 0;
        var omitted = 0;
        foreach (var entry in FileWalker.Walk(root, start, depth, ignore, ct))
        {
            walked++;
            // A link is reported as a file by the enumeration but never searched: ripgrep does not follow links either,
            // so skipping it keeps the two backends' result sets equal.
            if (entry.IsLink)
            {
                continue;
            }

            if (!matcher.IsPathMatch(entry.RelativePath))
            {
                continue;
            }

            if (lines.Count == limit)
            {
                // Past the limit entries are only counted, so the answer can say how many there are.
                omitted++;
                continue;
            }

            lines.Add(entry.IsDirectory ? entry.RelativePath + "/" : entry.RelativePath);
        }

        if (omitted > 0)
        {
            // Past the limit entries are only counted, so the answer can say how many there are and what to do next.
            lines.Add($"… (truncated: showing {limit} of {limit + omitted} entries; raise limit or narrow pattern)");
        }

        if (lines.Count == 0)
        {
            // "(empty)" alone could not tell "your glob matched nothing" from "nothing was walked at all"; the count and the
            // one likely reason separate them. Same rule the grep answer follows.
            lines.Add(walked == 0
                ? "no entries: the directory is empty or excluded by .gitignore — verify with ls"
                : $"no entry matched '{pattern}' (walked {walked} entries)");
        }

        return new ValueTask<ToolResult>(new ToolResult(BoundedOutput.Join(lines, "Narrow with `pattern` or `path`.")));
    }

    private static Func<ToolOptions> Fixed(ToolOptions options) => () => options;
}
