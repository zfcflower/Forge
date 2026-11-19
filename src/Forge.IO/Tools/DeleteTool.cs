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
        """{"type":"object","properties":{"path":{"type":"string","description":"File or directory to delete, workspace-relative or absolute."},"recursive":{"type":"boolean","description":"Delete a non-empty directory with everything below it; default false."}},"required":["path"],"additionalProperties":false}""";

    private const string Description =
        "Delete a file or a directory inside the workspace. A non-empty directory needs recursive. A symbolic link is removed " +
        "without touching its target. The workspace root and .git cannot be deleted. Deleted files are not recoverable.";

    protected override async ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var requested = ToolJson.RequiredString(arguments, "path");
        var recursive = ToolJson.OptionalBool(arguments, "recursive", false);
        var root = Root(context);
        var path = FileOps.ResolveEntry(root, requested);
        var display = FileOps.Display(root, path);
        if (!File.Exists(path) && !Directory.Exists(path) && !IsDanglingLink(path))
        {
            return new ToolResult($"delete: '{display}' does not exist.", IsError: true);
        }

        if (FileOps.IsInsideGit(root, path))
        {
            return new ToolResult("delete: the .git directory is not deletable.", IsError: true);
        }

        using (await PathGate.Shared.AcquireAsync(path, ct).ConfigureAwait(false))
        {
            if (!recursive && !FileOps.IsLink(path) && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            {
                return new ToolResult($"delete: directory '{display}' is not empty; pass recursive to delete it with its contents.", IsError: true);
            }

            var stats = FileOps.DeleteTree(path, ct);
            return new ToolResult(
                stats.Directories == 0 && stats.Files <= 1
                    ? $"deleted {display}"
                    : $"deleted {display}: {stats.Files.ToString(CultureInfo.InvariantCulture)} files, " +
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
