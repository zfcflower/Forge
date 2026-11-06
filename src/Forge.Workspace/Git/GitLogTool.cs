using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Lists recent commits so the agent can judge the repository's commit style before writing one.</summary>
internal sealed class GitLogTool(GitCli git) : GitToolBase(
    git,
    GitTools.LogName,
    "List recent commits newest first, one line each (short sha and subject). Read the last 20 subjects before " +
    "writing a commit subject, and follow the repository's own style: Conventional Commits if that is what it uses, " +
    "prose otherwise.",
    """
    {"type":"object","properties":{"revision":{"type":"string","description":"Revision or range to log, e.g. HEAD or main..HEAD; default HEAD."},"limit":{"type":"integer","description":"Commits to list; default 20, at most 200."}},"additionalProperties":false}
    """,
    ToolSideEffect.ReadOnly)
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 200;

    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments, "revision", "limit");

        var revision = GitToolJson.String(arguments, "revision");
        var limit = Math.Clamp(GitToolJson.Integer(arguments, "limit", DefaultLimit), 1, MaxLimit);
        var commits = await Git.GetLogAsync(
            context.WorkingDirectory,
            new GitLogOptions
            {
                Revision = string.IsNullOrWhiteSpace(revision) ? null : revision,
                Limit = limit,
            },
            ct).ConfigureAwait(false);

        if (commits.Count == 0)
        {
            return new ToolResult("No commits matched.");
        }

        var text = new StringBuilder(commits.Count * 60);
        foreach (var commit in commits)
        {
            text.Append(commit.ShortId).Append(' ').Append(commit.Subject).Append('\n');
        }

        return new ToolResult(text.ToString(0, text.Length - 1));
    }
}
