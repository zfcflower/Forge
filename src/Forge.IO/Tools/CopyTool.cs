using System.Globalization;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// <c>copy</c>: files and directories (with everything below them) to a new place inside the workspace, the whole of what
/// <c>cp</c> does. One source without a pattern goes to the exact path <c>to</c>; several sources, a pattern, or a
/// <c>to</c> that ends with a separator go <i>into</i> the directory <c>to</c>, each under its own name. Missing parent
/// directories are created. Everything is checked before anything is copied. An existing destination is an error unless
/// <c>overwrite</c> is set, which replaces files and merges into directories.
/// </summary>
public sealed class CopyTool() : ToolBase("copy", Description, Schema, ToolSideEffect.Write)
{
    private const string Schema =
        """{"type":"object","properties":{"from":{"description":"What to copy: a path, a glob pattern (* ? [a-z] ** {a,b}) or an array of them; workspace-relative or absolute.","anyOf":[{"type":"string"},{"type":"array","items":{"type":"string"}}]},"to":{"type":"string","description":"The exact new path; end it with / to copy into that directory instead (required for several sources or a pattern). Parent directories are created."},"overwrite":{"type":"boolean","description":"Replace existing files and merge into existing directories; default false."}},"required":["from","to"],"additionalProperties":false}""";

    private const string Description =
        "Copy files and directories inside the workspace (cp -r). 'from' takes paths, glob patterns or an array; a single plain path goes to " +
        "the exact path 'to', anything else goes into the directory 'to/'. Existing destinations are errors unless overwrite. " +
        "Symbolic links and .git inside a copied directory are skipped and counted.";

    protected override async ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var froms = ToolJson.RequiredStrings(arguments, "from");
        var to = ToolJson.RequiredString(arguments, "to");
        var overwrite = ToolJson.OptionalBool(arguments, "overwrite", false);
        var root = Root(context);
        var sources = PathPatterns.Expand(root, froms, plain => WorkspacePath.Resolve(root, plain, rootIsReal: true), includeLinks: false, ct);
        var into = to[^1] is '/' or '\\';
        var several = sources.Count > 1 || froms.Any(PathPatterns.IsPattern);
        if (several && !into)
        {
            return new ToolResult("copy: several sources or a pattern go into a directory; end 'to' with / (the directory they are copied into).", IsError: true);
        }

        var destination = FileOps.ResolveTarget(root, to);
        var pairs = new List<(string Source, string Target)>();
        if (into)
        {
            if (File.Exists(destination))
            {
                return new ToolResult($"copy: '{FileOps.Display(root, destination)}' is a file, not a directory to copy into.", IsError: true);
            }

            var names = new HashSet<string>(WorkspacePath.Comparer);
            foreach (var source in sources)
            {
                var name = Path.GetFileName(source);
                if (!names.Add(name))
                {
                    return new ToolResult($"copy: two sources are both named '{name}'.", IsError: true);
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

        using (await FileOps.AcquireAllAsync(pairs.Select(pair => pair.Target), ct).ConfigureAwait(false))
        {
            var total = new TreeStats(0, 0, 0, 0);
            foreach (var (source, target) in pairs)
            {
                ct.ThrowIfCancellationRequested();
                if (Directory.Exists(source))
                {
                    var part = FileOps.CopyTree(source, target, overwrite, ct);
                    total = new TreeStats(total.Files + part.Files, total.Directories + part.Directories, total.Bytes + part.Bytes, total.Skipped + part.Skipped);
                    continue;
                }

                if (Path.GetDirectoryName(target) is { Length: > 0 } parent)
                {
                    Directory.CreateDirectory(parent);
                }

                File.Copy(source, target, overwrite);
                total = new TreeStats(total.Files + 1, total.Directories, total.Bytes + new FileInfo(target).Length, total.Skipped);
            }

            var shown = FileOps.Display(root, pairs.Count == 1 ? pairs[0].Target : destination);
            var skipped = total.Skipped > 0 ? $", skipped {total.Skipped.ToString(CultureInfo.InvariantCulture)} link/.git entries" : string.Empty;
            var directories = total.Directories > 0 ? $" in {total.Directories.ToString(CultureInfo.InvariantCulture)} directories" : string.Empty;
            return new ToolResult(
                $"copied {total.Files.ToString(CultureInfo.InvariantCulture)} files ({total.Bytes.ToString(CultureInfo.InvariantCulture)} bytes){directories} to {shown}{skipped}");
        }
    }

    private static string? Check(string root, string source, string destination, bool overwrite)
    {
        var sourceIsDirectory = Directory.Exists(source);
        if (!sourceIsDirectory && !File.Exists(source))
        {
            return $"copy: '{FileOps.Display(root, source)}' does not exist.";
        }

        if (FileOps.IsInsideGit(root, destination) || FileOps.IsInsideGit(root, source))
        {
            return "copy: the .git directory is not a copy source or target.";
        }

        if (string.Equals(source, destination, WorkspacePath.Comparison) || (sourceIsDirectory && WorkspacePath.IsUnder(destination, source)))
        {
            return "copy: the destination is the source or lies inside it.";
        }

        var destinationIsDirectory = Directory.Exists(destination);
        var destinationExists = destinationIsDirectory || File.Exists(destination);
        if (destinationExists && sourceIsDirectory != destinationIsDirectory)
        {
            return $"copy: '{FileOps.Display(root, destination)}' exists and is a {(destinationIsDirectory ? "directory" : "file")}.";
        }

        return destinationExists && !overwrite
            ? $"copy: '{FileOps.Display(root, destination)}' already exists; pass overwrite to replace it."
            : null;
    }
}
