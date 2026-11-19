using System.Globalization;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// <c>delete</c>: removes a file, a link (the link only, never what it points at), or a directory inside the workspace. A
/// directory that is not empty needs <c>recursive</c>, so a wrong path costs an error and not a tree. The workspace root and
/// the .git directory are never deleted.
/// </summary>
public sealed class DeleteTool() : ToolBase("delete", Description, Schema, ToolSideEffect.Write)
{
    private const string Schema =
        """{"type":"object","properties":{"path":{"description":"What to delete: a path, a glob pattern (* ? [a-z] ** {a,b}) or an array of them; workspace-relative or absolute.","anyOf":[{"type":"string"},{"type":"array","items":{"type":"string"}}]},"recursive":{"type":"boolean","description":"Delete non-empty directories with everything below them; default false."}},"required":["path"],"additionalProperties":false}""";

    private const string Description =
        "Delete files and directories inside the workspace (rm -r). 'path' takes paths, glob patterns or an array. A non-empty directory " +
        "needs recursive. A symbolic link is removed without touching its target. The workspace root and .git cannot be deleted. " +
        "Deleted files are not recoverable. " +
        "A pattern skips dot-names unless its segment starts with a dot, and matching nothing is an error.";

    protected override async ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var requested = ToolJson.RequiredStrings(arguments, "path");
        var recursive = ToolJson.OptionalBool(arguments, "recursive", false);
        var root = Root(context);
        var paths = PathPatterns.Expand(root, requested, plain => FileOps.ResolveEntry(root, plain), includeLinks: true, ct);
        foreach (var path in paths)
        {
            var display = FileOps.Display(root, path);
            if (!File.Exists(path) && !Directory.Exists(path) && !IsDanglingLink(path))
            {
                return new ToolResult($"delete: '{display}' does not exist.", IsError: true);
            }

            if (FileOps.IsInsideGit(root, path))
            {
                return new ToolResult("delete: the .git directory is not deletable.", IsError: true);
            }

            if (!recursive && !FileOps.IsLink(path) && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            {
                return new ToolResult($"delete: directory '{display}' is not empty; pass recursive to delete it with its contents.", IsError: true);
            }
        }

        using (await FileOps.AcquireAllAsync(paths, ct).ConfigureAwait(false))
        {
            var stats = FileOps.DeleteMany(paths, ct);
            var shown = paths.Count == 1
                ? FileOps.Display(root, paths[0])
                : $"{paths.Count.ToString(CultureInfo.InvariantCulture)} items";
            return new ToolResult(
                stats.Directories == 0 && stats.Files <= 1
                    ? $"deleted {shown}"
                    : $"deleted {shown}: {stats.Files.ToString(CultureInfo.InvariantCulture)} files, " +
                      $"{stats.Directories.ToString(CultureInfo.InvariantCulture)} directories ({stats.Bytes.ToString(CultureInfo.InvariantCulture)} bytes)");
        }
    }

    /// <summary>A link whose target is gone: neither <see cref="File.Exists"/> nor <see cref="Directory.Exists"/> sees it, but it is there and can be removed.</summary>
    private static bool IsDanglingLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
