using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// <c>ls</c>: one line per entry below a directory, <c>d path/</c> for a directory and <c>f path</c> for a file, with
/// workspace-relative paths sorted ordinally so two runs print the same listing. Ignore rules deliberately do not
/// apply: an explicitly listed directory shows what is on disk (<see cref="FileWalker"/> still refuses to enter the
/// version control directory). The listing stops at <see cref="ToolOptions.ListMaxEntries"/> entries and says so.
/// </summary>
public sealed class ListTool : ToolBase
{
    private const int DefaultDepth = 1;
    private const int MaxDepth = 5;

    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"Directory to list, relative to the workspace root; default the root."},"depth":{"type":"integer","description":"Levels to descend: 1 lists the direct children; default 1, at most 5."}},"additionalProperties":false}""";

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
            + "Does not apply .gitignore, and never enters .git. Prints `… (truncated)` past the configured entry cap.",
            Schema,
            ToolSideEffect.ReadOnly)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    protected override ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var bounds = options();
        var requested = ToolJson.OptionalString(arguments, "path") ?? ".";
        var depth = ToolJson.OptionalInt(arguments, "depth", DefaultDepth, 1, MaxDepth);
        var root = WorkspacePath.ResolveRealPath(Root(context));
        var start = WorkspacePath.Resolve(root, requested);
        if (File.Exists(start))
        {
            return new ValueTask<ToolResult>(new ToolResult("f " + WorkspacePath.RelativeTo(root, start)));
        }

        if (!Directory.Exists(start))
        {
            return new ValueTask<ToolResult>(new ToolResult($"ls: '{requested}' does not exist.", IsError: true));
        }

        var maximum = Math.Clamp(bounds.ListMaxEntries, 1, ToolOptions.MaxListMaxEntries);
        var entries = new List<WalkEntry>(64);
        var truncated = false;
        foreach (var entry in FileWalker.Walk(root, start, depth, filter: null, ct))
        {
            if (entries.Count == maximum)
            {
                truncated = true;
                break;
            }

            entries.Add(entry);
        }

        entries.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));

        var lines = new List<string>(entries.Count + 2);
        foreach (var entry in entries)
        {
            lines.Add(entry switch
            {
                { IsLink: true } => "l " + entry.RelativePath,
                { IsDirectory: true } => "d " + entry.RelativePath + "/",
                _ => "f " + entry.RelativePath,
            });
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
