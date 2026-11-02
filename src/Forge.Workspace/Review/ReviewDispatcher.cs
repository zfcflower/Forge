using System.Globalization;
using System.Text;

namespace Forge.Workspace;

/// <summary>The message text produced for a send, plus the comments it covers.</summary>
public sealed record ReviewDispatch(string Text, IReadOnlyList<long> CommentIds);

/// <summary>
/// Turns review comments into the Markdown message an agent receives. Comments are quoted from the diff with a
/// few context lines, rendered in a stable order, and marked sent once the text has been produced.
/// </summary>
public sealed class ReviewDispatcher
{
    private readonly ReviewStore store;

    public ReviewDispatcher(ReviewStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    public async Task<ReviewDispatch?> BuildSingleAsync(
        long commentId,
        IReadOnlyList<FileDiff>? diff = null,
        int contextLines = 2,
        CancellationToken ct = default)
    {
        var comment = await store.GetAsync(commentId, ct).ConfigureAwait(false);
        if (comment is null)
        {
            return null;
        }

        return new ReviewDispatch(Render([comment], diff, contextLines, numbered: false, heading: "Review comment"), [comment.Id]);
    }

    public async Task<ReviewDispatch?> SendSingleAsync(
        long commentId,
        IReadOnlyList<FileDiff>? diff = null,
        int contextLines = 2,
        CancellationToken ct = default)
    {
        var dispatch = await BuildSingleAsync(commentId, diff, contextLines, ct).ConfigureAwait(false);
        if (dispatch is null)
        {
            return null;
        }

        await store.MarkSentAsync(dispatch.CommentIds, ct).ConfigureAwait(false);
        return dispatch;
    }

    public Task<ReviewDispatch?> BuildAllAsync(
        string workspacePath,
        string sessionId,
        IReadOnlyList<FileDiff>? diff = null,
        int contextLines = 2,
        CancellationToken ct = default) =>
        BuildManyAsync(workspacePath, sessionId, diff, contextLines, ct);

    public async Task<ReviewDispatch?> SendAllAsync(
        string workspacePath,
        string sessionId,
        IReadOnlyList<FileDiff>? diff = null,
        int contextLines = 2,
        CancellationToken ct = default)
    {
        var dispatch = await BuildManyAsync(workspacePath, sessionId, diff, contextLines, ct).ConfigureAwait(false);
        if (dispatch is null)
        {
            return null;
        }

        await store.MarkSentAsync(dispatch.CommentIds, ct).ConfigureAwait(false);
        return dispatch;
    }

    private async Task<ReviewDispatch?> BuildManyAsync(
        string workspacePath,
        string sessionId,
        IReadOnlyList<FileDiff>? diff,
        int contextLines,
        CancellationToken ct)
    {
        var drafts = await store.GetDraftsAsync(workspacePath, sessionId, ct).ConfigureAwait(false);
        if (drafts.Count == 0)
        {
            return null;
        }

        var ordered = drafts
            .OrderBy(static comment => comment.FilePath, StringComparer.Ordinal)
            .ThenBy(static comment => comment.NewLineStart)
            .ThenBy(static comment => comment.Id)
            .ToArray();
        var ids = ordered.Select(static comment => comment.Id).ToArray();
        var heading = $"Review comments ({ordered.Length.ToString(CultureInfo.InvariantCulture)})";
        return new ReviewDispatch(Render(ordered, diff, contextLines, numbered: true, heading), ids);
    }

    private static string Render(
        IReadOnlyList<ReviewComment> comments,
        IReadOnlyList<FileDiff>? diff,
        int contextLines,
        bool numbered,
        string heading)
    {
        var builder = new StringBuilder();
        builder.Append("## ").Append(heading).Append("\n");
        for (var index = 0; index < comments.Count; index++)
        {
            var comment = comments[index];
            builder.Append('\n');
            builder.Append("### ");
            if (numbered)
            {
                builder.Append((index + 1).ToString(CultureInfo.InvariantCulture)).Append(". ");
            }

            builder.Append(LineRange(comment)).Append('\n');
            if (!string.IsNullOrWhiteSpace(comment.BaseCommit))
            {
                builder.Append("base: `").Append(comment.BaseCommit).Append("`\n");
            }

            var file = FindFile(diff, comment.FilePath);
            var quote = file is null ? null : comment.Anchor?.Quote(file, contextLines);
            if (quote is not null)
            {
                var fence = FenceFor(quote.Text);
                builder.Append('\n')
                    .Append(fence).Append("diff\n")
                    .Append(quote.Text).Append('\n')
                    .Append(fence).Append('\n');
                if (quote.State != DiffAnchorState.Exact)
                {
                    builder.Append("\n> Lines may have shifted since this comment was written.\n");
                }
            }

            builder.Append('\n').Append(comment.Body.TrimEnd()).Append('\n');
        }

        return builder.ToString().TrimEnd() + "\n";
    }

    private static string LineRange(ReviewComment comment)
    {
        var range = comment.NewLineStart == comment.NewLineEnd
            ? comment.NewLineStart.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{comment.NewLineStart}-{comment.NewLineEnd}");
        return $"`{comment.FilePath}:{range}`";
    }

    private static FileDiff? FindFile(IReadOnlyList<FileDiff>? diff, string path)
    {
        if (diff is null)
        {
            return null;
        }

        foreach (var file in diff)
        {
            if (string.Equals(file.Path, path, StringComparison.Ordinal))
            {
                return file;
            }
        }

        return null;
    }

    private static string FenceFor(string text)
    {
        var fence = "```";
        while (text.Contains(fence, StringComparison.Ordinal))
        {
            fence += "`";
        }

        return fence;
    }
}
