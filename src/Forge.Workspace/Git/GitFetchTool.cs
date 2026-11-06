using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Updates the remote-tracking refs and reports the new divergence; it never touches the working tree.</summary>
internal sealed class GitFetchTool(GitCli git, GitToolSettings settings) : GitToolBase(
    git,
    GitTools.FetchName,
    "Fetch from the remote (all remotes when none is named), pruning remote branches that were deleted, then report " +
    "the new ahead/behind. Read-only for the working tree: it only updates remote-tracking refs.",
    """
    {"type":"object","properties":{"remote":{"type":"string","description":"Remote to fetch, e.g. origin; default every remote."}},"additionalProperties":false}
    """,
    ToolSideEffect.ReadOnly)
{
    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments, "remote");

        var remote = GitToolJson.String(arguments, "remote");
        var result = await Git.FetchAsync(
            context.WorkingDirectory,
            string.IsNullOrWhiteSpace(remote) ? null : remote,
            NetworkOptions(settings),
            ct).ConfigureAwait(false);

        if (!result.Success)
        {
            return GitToolFailure.ToToolResult(result);
        }

        var status = await Git.GetBranchStatusAsync(context.WorkingDirectory, ct).ConfigureAwait(false);
        return new ToolResult(result.Message + "\n" + BranchLine(status));
    }
}
