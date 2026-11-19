using System.Globalization;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// <c>copy</c>: a file, or a directory with everything below it, to a new path inside the workspace. The destination is the
/// exact new path, never "into" an existing directory; missing parent directories are created. An existing destination is an
/// error unless <c>overwrite</c> is set, which replaces files and merges into a directory.
/// </summary>
public sealed class CopyTool() : ToolBase("copy", Description, Schema, ToolSideEffect.Write)
{
    private const string Schema =
        """{"type":"object","properties":{"from":{"type":"string","description":"File or directory to copy, workspace-relative or absolute."},"to":{"type":"string","description":"The exact new path (not the directory to copy into); parent directories are created."},"overwrite":{"type":"boolean","description":"Replace existing files and merge into an existing directory; default false."}},"required":["from","to"],"additionalProperties":false}""";

    private const string Description =
        "Copy a file or a directory tree inside the workspace. 'to' is the new path itself; without overwrite an existing " +
        "'to' is an error. Symbolic links and .git inside a copied directory are skipped and counted.";

    protected override async ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var from = ToolJson.RequiredString(arguments, "from");
        var to = ToolJson.RequiredString(arguments, "to");
        var overwrite = ToolJson.OptionalBool(arguments, "overwrite", false);
        var root = Root(context);
        var source = WorkspacePath.Resolve(root, from);
        var destination = FileOps.ResolveTarget(root, to);
        var sourceIsDirectory = Directory.Exists(source);
        if (!sourceIsDirectory && !File.Exists(source))
        {
            return new ToolResult($"copy: '{FileOps.Display(root, source)}' does not exist.", IsError: true);
        }

        if (FileOps.IsInsideGit(root, destination) || (sourceIsDirectory && FileOps.IsInsideGit(root, source)))
        {
            return new ToolResult("copy: the .git directory is not a copy source or target.", IsError: true);
        }

        if (WorkspacePath.SamePath(source, destination) || (sourceIsDirectory && WorkspacePath.IsUnder(destination, source)))
        {
            return new ToolResult("copy: the destination is the source or lies inside it.", IsError: true);
        }

        var destinationIsDirectory = Directory.Exists(destination);
        var destinationExists = destinationIsDirectory || File.Exists(destination);
        if (destinationExists && sourceIsDirectory != destinationIsDirectory)
        {
            return new ToolResult(
                $"copy: '{FileOps.Display(root, destination)}' exists and is a {(destinationIsDirectory ? "directory" : "file")}.",
                IsError: true);
        }

        if (destinationExists && !overwrite)
        {
            return new ToolResult($"copy: '{FileOps.Display(root, destination)}' already exists; pass overwrite to replace it.", IsError: true);
        }

        using (await PathGate.Shared.AcquireAsync(destination, ct).ConfigureAwait(false))
        {
            if (sourceIsDirectory)
            {
                var stats = FileOps.CopyTree(source, destination, overwrite, ct);
                var skipped = stats.Skipped > 0
                    ? $", skipped {stats.Skipped.ToString(CultureInfo.InvariantCulture)} link/.git entries"
                    : string.Empty;
                return new ToolResult(
                    $"copied {stats.Files.ToString(CultureInfo.InvariantCulture)} files ({stats.Bytes.ToString(CultureInfo.InvariantCulture)} bytes) " +
                    $"in {stats.Directories.ToString(CultureInfo.InvariantCulture)} directories to {FileOps.Display(root, destination)}{skipped}");
            }

            if (Path.GetDirectoryName(destination) is { Length: > 0 } parent)
            {
                Directory.CreateDirectory(parent);
            }

            File.Copy(source, destination, overwrite);
            return new ToolResult(
                $"copied {new FileInfo(destination).Length.ToString(CultureInfo.InvariantCulture)} bytes to {FileOps.Display(root, destination)}");
        }
    }
}
