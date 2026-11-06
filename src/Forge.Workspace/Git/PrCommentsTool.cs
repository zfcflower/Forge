using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Lists the review threads of this branch's pull request, with every comment, the verdict already replied and optionally the full diff.</summary>
internal sealed class PrCommentsTool(PrCommentEnvironment environment) : GitToolBase(
    environment.Git,
    PrCommentTools.ListName,
    "Read the review threads of this branch's pull request: thread id, resolved/outdated, path:line, every comment " +
    "oldest first, and the verdict you already replied with. Unresolved threads only by default; all=true lists resolved " +
    "ones too. include_diff=true appends the full pull request diff, which you should read completely before judging a " +
    "single comment. Read pr_workflow (phase review-comments) first: judge every thread (apply, push_back or clarify) " +
    "before you change code, then answer with pr_comment_reply and close with pr_comment_resolve. Read-only.",
    """
    {"type":"object","properties":{"all":{"type":"boolean","description":"Also list resolved threads; default false (unresolved only)."},"include_diff":{"type":"boolean","description":"Append the full unified diff of the pull request; default false."}},"additionalProperties":false}
    """,
    ToolSideEffect.ReadOnly)
{
    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments, "all", "include_diff");
        var all = GitToolJson.Boolean(arguments, "all", false);
        var includeDiff = GitToolJson.Boolean(arguments, "include_diff", false);
        var directory = context.WorkingDirectory;

        var lookup = await environment.ReadThreadsAsync(directory, ct).ConfigureAwait(false);
        if (lookup.Error is not null)
        {
            return lookup.Error;
        }

        var pull = lookup.PullRequest!;
        var threads = lookup.Threads!;
        var number = pull.Number.ToString(CultureInfo.InvariantCulture);
        var unresolved = 0;
        foreach (var thread in threads)
        {
            if (!thread.IsResolved)
            {
                unresolved++;
            }
        }

        var text = new StringBuilder(1024);
        text.Append("Pull request #").Append(number).Append(' ').Append(StateName(pull.State)).Append(": ")
            .Append(pull.HeadRefName).Append(" into ").Append(pull.BaseRefName);
        if (!string.IsNullOrEmpty(pull.Url))
        {
            text.Append(' ').Append(pull.Url);
        }

        text.Append('\n');
        text.Append(threads.Count.ToString(CultureInfo.InvariantCulture)).Append(" review thread(s): ")
            .Append(unresolved.ToString(CultureInfo.InvariantCulture)).Append(" unresolved, ")
            .Append((threads.Count - unresolved).ToString(CultureInfo.InvariantCulture)).Append(" resolved. ")
            .Append(all ? "Showing all threads.\n" : "Showing unresolved threads; pass all=true to include resolved ones.\n");

        var shown = 0;
        foreach (var thread in threads)
        {
            if (thread.IsResolved && !all)
            {
                continue;
            }

            shown++;
            var verdict = await environment.EvaluateAsync(directory, thread, ct).ConfigureAwait(false);
            text.Append('\n');
            PrCommentText.AppendThread(text, thread, verdict);
        }

        if (shown == 0)
        {
            text.Append(threads.Count == 0
                ? "\nThere are no review threads on this pull request.\n"
                : "\nThere are no unresolved review threads.\n");
        }
        else
        {
            text.Append("\nNext: judge each unresolved thread first (apply, push_back or clarify); only then change code, ")
                .Append("push, and answer with pr_comment_reply. See pr_workflow, phase review-comments.\n");
        }

        if (includeDiff)
        {
            await AppendDiffAsync(text, directory, pull.Number, ct).ConfigureAwait(false);
        }

        return new ToolResult(text.ToString());
    }

    private async Task AppendDiffAsync(StringBuilder text, string directory, int number, CancellationToken ct)
    {
        var diff = await environment.Platform.GetPullRequestDiffAsync(directory, number, ct).ConfigureAwait(false);
        text.Append("\n== Pull request diff ==\n");
        if (!diff.Success || diff.Value is null)
        {
            text.Append("WARNING: the diff could not be read, so it is NOT included: ")
                .Append(diff.Status.Message).Append('\n');
            return;
        }

        text.Append(diff.Value.Text);
        if (diff.Value.Text.Length > 0 && diff.Value.Text[^1] != '\n')
        {
            text.Append('\n');
        }

        if (diff.Value.Truncated)
        {
            text.Append("WARNING: the diff was cut at the size cap; read the remaining files with git_diff or the file tools.\n");
        }
    }

    private static string StateName(PrState state) => state switch
    {
        PrState.Open => "(open)",
        PrState.Closed => "(closed)",
        PrState.Merged => "(merged)",
        _ => "(state unknown)",
    };
}
