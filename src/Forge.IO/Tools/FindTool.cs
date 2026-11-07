using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// <c>find</c>: every entry below a directory whose workspace-relative path matches a glob, in walk order (the search
/// is not sorted, so it never buffers a whole tree). Ignore rules apply through <see cref="GitIgnore"/>, which also
/// means an ignored directory is never entered. Directories are printed with a trailing <c>/</c>. The listing stops at
/// the requested limit and says so.
/// </summary>
public sealed class FindTool : ToolBase
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
            + "order. Applies .gitignore; directories end with `/`. Prints `… (truncated)` when the limit is reached.",
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

        var ignore = new GitIgnore(root);
        var lines = new List<string>(Math.Min(limit, 256));
        var truncated = false;
        foreach (var entry in FileWalker.Walk(root, start, depth, ignore, ct))
        {
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
                truncated = true;
                break;
            }

            lines.Add(entry.IsDirectory ? entry.RelativePath + "/" : entry.RelativePath);
        }

        if (truncated)
        {
            lines.Add("… (truncated)");
        }

        if (lines.Count == 0)
        {
            lines.Add("(empty)");
        }

        return new ValueTask<ToolResult>(new ToolResult(string.Join('\n', lines)));
    }

    private static Func<ToolOptions> Fixed(ToolOptions options) => () => options;
}
