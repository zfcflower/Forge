using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>Writes a workspace text file, atomically by default and by appending when asked.</summary>
public sealed class WriteTool() : ToolBase("write", Description, Schema, ToolSideEffect.Write)
{
    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"File to write, workspace-relative or absolute."},"content":{"type":"string","description":"Text to write, UTF-8."},"append":{"type":"boolean","description":"Append instead of replacing the file."}},"required":["path","content"],"additionalProperties":false}""";

    private const string Description =
        "Write a text file in the workspace as UTF-8. By default the file is replaced atomically, so it is either the old " +
        "or the new content; with append the text is added to the end and the write is not atomic.";

    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        var requested = ToolJson.RequiredString(arguments, "path");
        if (ToolJson.OptionalString(arguments, "content") is not { } content)
        {
            throw new ToolArgumentException("'content' is required.");
        }

        var append = ToolJson.OptionalBool(arguments, "append", false);
        var root = Root(context);
        var path = ResolveTarget(root, requested);
        var display = Display(root, path);
        if (Directory.Exists(path))
        {
            return new ToolResult($"{Definition.Name}: '{display}' is a directory.", IsError: true);
        }

        long written;
        using (await PathGate.Shared.AcquireAsync(path, ct).ConfigureAwait(false))
        {
            written = append
                ? await TextFileWriter.AppendAsync(path, content, ct).ConfigureAwait(false)
                : await TextFileWriter.WriteAsync(path, content, ct).ConfigureAwait(false);
        }

        return new ToolResult($"{(append ? "appended" : "wrote")} {written} bytes to {display}");
    }

    /// <summary>
    /// Resolves a path this call may create. <see cref="WorkspacePath.Resolve"/> stops at the first component that does
    /// not exist, so a file inside a directory that is about to be created would lose its tail; the deepest existing
    /// ancestor is resolved (and checked against the root) and the names below it are appended unchanged.
    /// </summary>
    private static string ResolveTarget(string root, string requested)
    {
        var full = WorkspacePath.Normalize(Path.IsPathRooted(requested) ? requested : Path.Combine(root, requested));
        var existing = full;
        var missing = new List<string>();
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent))
            {
                break;
            }

            missing.Add(Path.GetFileName(existing));
            existing = parent;
        }

        var resolved = WorkspacePath.Resolve(root, existing);
        for (var index = missing.Count - 1; index >= 0; index--)
        {
            resolved = Path.Combine(resolved, missing[index]);
        }

        return resolved;
    }

    private static string Display(string root, string path) =>
        WorkspacePath.RelativeTo(root, path) is { Length: > 0 } relative ? relative : path;
}
