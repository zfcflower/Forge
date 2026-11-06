using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Creates, switches, retargets and deletes branches, refusing the cases that would lose work.</summary>
internal sealed class GitBranchTool(GitCli git) : GitToolBase(
    git,
    GitTools.BranchName,
    "Create, switch, re-target the upstream of, or delete a branch. create takes name and an optional start_point; " +
    "switch refuses when tracked files have uncommitted changes, and creates a tracking branch from origin/<name> " +
    "when only the remote one exists; set_upstream takes upstream (e.g. origin/main) and an optional name; delete " +
    "refuses the current branch and any branch checked out in a worktree, and refuses unmerged commits unless force " +
    "is true.",
    """
    {"type":"object","properties":{"operation":{"type":"string","enum":["create","switch","set_upstream","delete"],"description":"What to do."},"name":{"type":"string","description":"Branch name; required for create, switch and delete, optional (current branch) for set_upstream."},"start_point":{"type":"string","description":"Where to create the branch; default HEAD."},"upstream":{"type":"string","description":"Upstream ref for set_upstream, e.g. origin/main."},"force":{"type":"boolean","description":"Delete even when the branch is not fully merged; default false."}},"required":["operation"],"additionalProperties":false}
    """,
    ToolSideEffect.Write)
{
    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments, "operation", "name", "start_point", "upstream", "force");

        var operation = GitToolJson.RequiredString(arguments, "operation");
        var directory = context.WorkingDirectory;
        switch (operation)
        {
            case "create":
            {
                var result = await Git.CreateBranchAsync(
                    directory,
                    GitToolJson.RequiredString(arguments, "name"),
                    GitToolJson.String(arguments, "start_point"),
                    switchTo: false,
                    ct).ConfigureAwait(false);
                return Report(result);
            }

            case "switch":
                return Report(await Git.SwitchBranchAsync(
                    directory,
                    GitToolJson.RequiredString(arguments, "name"),
                    ct).ConfigureAwait(false));

            case "set_upstream":
            {
                var upstream = GitToolJson.RequiredString(arguments, "upstream");
                if (await Git.RevParseAsync(directory, upstream, ct).ConfigureAwait(false) is null)
                {
                    return new ToolResult(
                        $"Upstream '{upstream}' does not resolve in this repository; fetch first, or use an existing " +
                        "remote-tracking branch such as origin/<branch>.",
                        IsError: true);
                }

                var branch = GitToolJson.String(arguments, "name");
                return Report(await Git.SetUpstreamAsync(
                    directory,
                    string.IsNullOrWhiteSpace(branch) ? null : branch,
                    upstream,
                    ct).ConfigureAwait(false));
            }

            case "delete":
                return await DeleteAsync(directory, arguments, ct).ConfigureAwait(false);

            default:
                return new ToolResult(
                    $"Unknown operation '{operation}'; use create, switch, set_upstream or delete.",
                    IsError: true);
        }
    }

    private async Task<ToolResult> DeleteAsync(string directory, JsonElement arguments, CancellationToken ct)
    {
        var branch = GitToolJson.RequiredString(arguments, "name");
        var force = GitToolJson.Boolean(arguments, "force", false);

        var status = await Git.GetBranchStatusAsync(directory, ct).ConfigureAwait(false);
        if (string.Equals(status.Branch, branch, StringComparison.Ordinal))
        {
            return new ToolResult(
                $"'{branch}' is the current branch; switch to another branch before deleting it.",
                IsError: true);
        }

        foreach (var worktree in await Git.ListWorktreesAsync(directory, ct).ConfigureAwait(false))
        {
            if (string.Equals(worktree.Branch, branch, StringComparison.Ordinal))
            {
                return new ToolResult(
                    $"'{branch}' is checked out in the worktree at {worktree.Path}; remove or re-point that worktree " +
                    "before deleting the branch.",
                    IsError: true);
            }
        }

        try
        {
            await Git.DeleteBranchAsync(directory, branch, force, ct).ConfigureAwait(false);
        }
        catch (GitException exception)
        {
            return GitToolFailure.FromException(
                exception,
                $"Could not delete branch '{branch}'.",
                force
                    ? null
                    : "git refuses to delete a branch whose commits are not merged anywhere. Merge it first, or pass force: true when you are certain those commits are no longer needed.");
        }

        return new ToolResult($"Deleted branch {branch}.");
    }
}
