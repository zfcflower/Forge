using System.Globalization;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// <c>move</c>: renames or moves files and directories inside the workspace, the whole of what <c>mv</c> does, each as one
/// rename (not a copy and delete). One source without a pattern goes to the exact path <c>to</c>; several sources, a pattern,
/// or a <c>to</c> that ends with a separator go into the directory <c>to</c>. Missing parent directories are created. A link
/// is moved as the link. Everything is checked before anything moves. An existing destination is an error unless
/// <c>overwrite</c> is set, which replaces an existing file only.
/// </summary>
public sealed class MoveTool() : ToolBase("move", Description, Schema, ToolSideEffect.Write)
{
    private const string Schema =
        """{"type":"object","properties":{"from":{"description":"What to move: a path, a glob pattern (* ? [a-z] ** {a,b}) or an array of them; workspace-relative or absolute.","anyOf":[{"type":"string"},{"type":"array","items":{"type":"string"}}]},"to":{"type":"string","description":"The exact new path; end it with / to move into that directory instead (required for several sources or a pattern). Parent directories are created."},"overwrite":{"type":"boolean","description":"Replace an existing destination file; default false. A destination directory is never replaced."}},"required":["from","to"],"additionalProperties":false}""";

    private const string Description =
        "Move or rename files and directories inside the workspace (mv). 'from' takes paths, glob patterns or an array; a single plain path " +
        "goes to the exact path 'to', anything else goes into the directory 'to/'. Existing destinations are errors unless overwrite; " +
        "an existing directory is never replaced.";

    protected override async ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var froms = ToolJson.RequiredStrings(arguments, "from");
        var to = ToolJson.RequiredString(arguments, "to");
        var overwrite = ToolJson.OptionalBool(arguments, "overwrite", false);
        var root = Root(context);
        var sources = PathPatterns.Expand(root, froms, plain => FileOps.ResolveEntry(root, plain), includeLinks: true, ct);
        var into = to[^1] is '/' or '\\';
        var several = sources.Count > 1 || froms.Any(PathPatterns.IsPattern);
        if (several && !into)
        {
            return new ToolResult("move: several sources or a pattern go into a directory; end 'to' with / (the directory they are moved into).", IsError: true);
        }

        var destination = FileOps.ResolveTarget(root, to);
        var pairs = new List<(string Source, string Target)>();
        if (into)
        {
            if (File.Exists(destination))
            {
                return new ToolResult($"move: '{FileOps.Display(root, destination)}' is a file, not a directory to move into.", IsError: true);
            }

            var names = new HashSet<string>(WorkspacePath.Comparer);
            foreach (var source in sources)
            {
                var name = Path.GetFileName(source);
                if (!names.Add(name))
                {
                    return new ToolResult($"move: two sources are both named '{name}'.", IsError: true);
                }

                pairs.Add((source, Path.Combine(destination, name)));
            }
        }
        else
        {
            pairs.Add((sources[0], destination));
        }

        foreach (var (source, target) in pairs)
        {
            if (Check(root, source, target, overwrite) is { } problem)
            {
                return new ToolResult(problem, IsError: true);
            }
        }

        using (await FileOps.AcquireAllAsync(pairs.SelectMany(pair => new[] { pair.Source, pair.Target }), ct).ConfigureAwait(false))
        {
            foreach (var (source, target) in pairs)
            {
                ct.ThrowIfCancellationRequested();
                if (Path.GetDirectoryName(target) is { Length: > 0 } parent)
                {
                    Directory.CreateDirectory(parent);
                }

                if (Directory.Exists(source) && !FileOps.IsLink(source))
                {
                    Directory.Move(source, target);
                }
                else
                {
                    File.Move(source, target, overwrite);
                }
            }
        }

        return new ToolResult(
            pairs.Count == 1
                ? $"moved {FileOps.Display(root, pairs[0].Source)} to {FileOps.Display(root, pairs[0].Target)}"
                : $"moved {pairs.Count.ToString(CultureInfo.InvariantCulture)} items into {FileOps.Display(root, destination)}");
    }

    private static string? Check(string root, string source, string destination, bool overwrite)
    {
        var sourceIsDirectory = Directory.Exists(source) && !FileOps.IsLink(source);
        if (!sourceIsDirectory && !File.Exists(source) && !Directory.Exists(source))
        {
            return $"move: '{FileOps.Display(root, source)}' does not exist.";
        }

        if (FileOps.IsInsideGit(root, source) || FileOps.IsInsideGit(root, destination))
        {
            return "move: the .git directory is not a move source or target.";
        }

        var sameEntry = string.Equals(source, destination, WorkspacePath.Comparison);
        if (sameEntry && string.Equals(source, destination, StringComparison.Ordinal))
        {
            return "move: the destination is the source.";
        }

        if (sourceIsDirectory && !sameEntry && WorkspacePath.IsUnder(destination, source))
        {
            return "move: a directory cannot move into itself.";
        }

        if (!sameEntry)
        {
            var destinationIsDirectory = Directory.Exists(destination);
            if (destinationIsDirectory || (File.Exists(destination) && (sourceIsDirectory || !overwrite)))
            {
                var kind = destinationIsDirectory ? "directory" : "file";
                var hint = destinationIsDirectory || sourceIsDirectory ? string.Empty : "; pass overwrite to replace it";
                return $"move: '{FileOps.Display(root, destination)}' already exists as a {kind}{hint}.";
            }
        }

        return null;
    }
}
