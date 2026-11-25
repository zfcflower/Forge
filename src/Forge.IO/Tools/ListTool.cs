using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// <c>ls</c>: one line per entry below a directory, <c>d path/</c> for a directory and <c>f path</c> for a file, with
/// workspace-relative paths (absolute ones for a directory outside the workspace) sorted ordinally so two runs print the same
/// listing. Ignore rules deliberately do not
/// apply: an explicitly listed directory shows what is on disk (<see cref="FileWalker"/> still refuses to enter the
/// version control directory). The listing stops at <see cref="ToolOptions.ListMaxEntries"/> entries and says so.
/// </summary>
public sealed class ListTool : ToolBase, IBoundedOutputTool
{
    private const int DefaultDepth = 1;
    private const int MaxDepth = 5;

    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"Directory to list, relative to the workspace root or absolute (inside or outside it); default the root."},"depth":{"type":"integer","description":"Levels to descend: 1 lists the direct children; default 1, at most 5."}},"additionalProperties":false}""";

    private readonly Func<ToolOptions> options;

    public ListTool()
        : this(ToolOptions.Default)
    {
    }

    public ListTool(ToolOptions options)
        : this(Fixed(options))
    {
    }

    public ListTool(Func<ToolOptions> options)
        : base(
            "ls",
            "List a directory: one line per entry, `d <path>/` for directories and `f <path>` for files, sorted by path. "
            + "Does not apply .gitignore, and never enters .git. Paths are workspace-relative, and absolute for a directory "
            + "outside the workspace (reading there needs no approval). Past the configured entry cap it reports the total count.",
            Schema,
            ToolSideEffect.ReadOnly)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    protected override ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var bounds = options();
        var requested = ToolJson.OptionalPath(arguments, "path");
        var depth = ToolJson.OptionalInt(arguments, "depth", DefaultDepth, 1, MaxDepth);
        var root = WorkspacePath.ResolveRealPath(Root(context));
        var start = WorkspacePath.ResolveReadable(root, requested);
        var (anchor, absolute) = FileOps.ReadFrame(root, start);
        if (File.Exists(start))
        {
            return new ValueTask<ToolResult>(new ToolResult("f " + FileOps.Display(root, start)));
        }

        if (!Directory.Exists(start))
        {
            return new ValueTask<ToolResult>(new ToolResult($"ls: '{requested}' does not exist.", IsError: true));
        }

        var maximum = Math.Clamp(bounds.ListMaxEntries, 1, ToolOptions.MaxListMaxEntries);
        var entries = new List<WalkEntry>(64);
        var omitted = 0;
        foreach (var entry in FileWalker.Walk(anchor, start, depth, filter: null, ct))
        {
            if (entries.Count == maximum)
            {
                omitted++;
                continue;
            }

            entries.Add(entry);
        }

        entries.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));

        var lines = new List<string>(entries.Count + 2);
        foreach (var entry in entries)
        {
            var shown = absolute ? entry.FullPath : entry.RelativePath;
            lines.Add(entry switch
            {
                { IsLink: true } => "l " + shown,
                { IsDirectory: true } => "d " + shown + "/",
                _ => "f " + shown,
            });
        }

        if (omitted > 0)
        {
            lines.Add($"… (truncated: showing {maximum} of {maximum + omitted} entries)");
        }

        if (lines.Count == 0)
        {
            lines.Add("(empty)");
        }

        return new ValueTask<ToolResult>(new ToolResult(BoundedOutput.Join(lines, "List a subdirectory with `path` instead.")));
    }

    private static Func<ToolOptions> Fixed(ToolOptions options) => () => options;
}
