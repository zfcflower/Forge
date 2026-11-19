using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// <c>move</c>: renames or moves a file or directory inside the workspace in one operation (a rename on the same volume,
/// not a copy and delete). The destination is the exact new path; missing parent directories are created. A link is moved as
/// the link. An existing destination is an error unless <c>overwrite</c> is set, which replaces an existing file only.
/// </summary>
public sealed class MoveTool() : ToolBase("move", Description, Schema, ToolSideEffect.Write)
{
    private const string Schema =
        """{"type":"object","properties":{"from":{"type":"string","description":"File or directory to move, workspace-relative or absolute."},"to":{"type":"string","description":"The exact new path (not the directory to move into); parent directories are created."},"overwrite":{"type":"boolean","description":"Replace an existing destination file; default false. A destination directory is never replaced."}},"required":["from","to"],"additionalProperties":false}""";

    private const string Description =
        "Move or rename a file or directory inside the workspace. 'to' is the new path itself; without overwrite an existing " +
        "'to' is an error, and an existing directory is never replaced.";

    protected override async ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var from = ToolJson.RequiredString(arguments, "from");
        var to = ToolJson.RequiredString(arguments, "to");
        var overwrite = ToolJson.OptionalBool(arguments, "overwrite", false);
        var root = Root(context);
        var source = FileOps.ResolveEntry(root, from);
        var destination = FileOps.ResolveTarget(root, to);
        var sourceIsDirectory = Directory.Exists(source);
        if (!sourceIsDirectory && !File.Exists(source))
        {
            return new ToolResult($"move: '{FileOps.Display(root, source)}' does not exist.", IsError: true);
        }

        if (FileOps.IsInsideGit(root, source) || FileOps.IsInsideGit(root, destination))
        {
            return new ToolResult("move: the .git directory is not a move source or target.", IsError: true);
        }

        var sameEntry = WorkspacePath.SamePath(source, destination);
        if (sameEntry && string.Equals(source, destination, StringComparison.Ordinal))
        {
            return new ToolResult("move: the destination is the source.", IsError: true);
        }

        if (sourceIsDirectory && !sameEntry && WorkspacePath.IsUnder(destination, source))
        {
            return new ToolResult("move: a directory cannot move into itself.", IsError: true);
        }

        if (!sameEntry)
        {
            var destinationIsDirectory = Directory.Exists(destination);
            if (destinationIsDirectory || (File.Exists(destination) && (sourceIsDirectory || !overwrite)))
            {
                var kind = destinationIsDirectory ? "directory" : "file";
                var hint = destinationIsDirectory || sourceIsDirectory ? string.Empty : "; pass overwrite to replace it";
                return new ToolResult($"move: '{FileOps.Display(root, destination)}' already exists as a {kind}{hint}.", IsError: true);
            }
        }

        // The two paths are taken in a fixed order, so two moves in opposite directions cannot wait on each other.
        var sourceFirst = string.CompareOrdinal(source, destination) <= 0;
        using (await PathGate.Shared.AcquireAsync(sourceFirst ? source : destination, ct).ConfigureAwait(false))
        using (sameEntry ? null : await PathGate.Shared.AcquireAsync(sourceFirst ? destination : source, ct).ConfigureAwait(false))
        {
            if (Path.GetDirectoryName(destination) is { Length: > 0 } parent)
            {
                Directory.CreateDirectory(parent);
            }

            if (sourceIsDirectory)
            {
                Directory.Move(source, destination);
            }
            else
            {
                File.Move(source, destination, overwrite);
            }
        }

        return new ToolResult($"moved {FileOps.Display(root, source)} to {FileOps.Display(root, destination)}");
    }
}
