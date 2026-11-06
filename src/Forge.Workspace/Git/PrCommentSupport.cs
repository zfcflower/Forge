using System.Globalization;
using System.Text;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>The thread read the three PR comment tools start from: the pull request, its threads, and the one asked for.</summary>
internal sealed record PrCommentLookup(
    PullRequestInfo? PullRequest,
    IReadOnlyList<PrReviewThread>? Threads,
    PrReviewThread? Thread,
    ToolResult? Error);

/// <summary>
/// What the PR comment tools share: the platform, git, the ledger and the way the current user is found. Every method
/// reads the platform fresh – nothing is cached between calls.
/// </summary>
internal sealed class PrCommentEnvironment(
    GitCli git,
    IPrPlatform platform,
    GitToolSettings settings,
    PrReplyLedger ledger,
    PrUserResolver? users)
{
    public GitCli Git { get; } = git;

    public IPrPlatform Platform { get; } = platform;

    public GitToolSettings Settings { get; } = settings;

    public PrReplyLedger Ledger { get; } = ledger;

    /// <summary>The pull request of the current branch and all its review threads, read now. Failures come back as the tool error.</summary>
    public async Task<PrCommentLookup> ReadThreadsAsync(string directory, CancellationToken ct)
    {
        var view = await Platform.ViewPullRequestAsync(directory, null, ct).ConfigureAwait(false);
        if (!view.Success || view.Value is null)
        {
            return new PrCommentLookup(null, null, null, Failure(view, "Could not read the pull request of this branch."));
        }

        var threads = await Platform.GetReviewThreadsAsync(directory, view.Value.Number, ct).ConfigureAwait(false);
        if (!threads.Success || threads.Value is null)
        {
            return new PrCommentLookup(view.Value, null, null, Failure(threads, "Could not read the review threads."));
        }

        return new PrCommentLookup(view.Value, threads.Value, null, null);
    }

    /// <summary>Like <see cref="ReadThreadsAsync"/>, then picks <paramref name="threadId"/>; an unknown id is a tool error.</summary>
    public async Task<PrCommentLookup> ReadThreadAsync(string directory, string threadId, CancellationToken ct)
    {
        var lookup = await ReadThreadsAsync(directory, ct).ConfigureAwait(false);
        if (lookup.Error is not null || lookup.Threads is null || lookup.PullRequest is null)
        {
            return lookup;
        }

        foreach (var thread in lookup.Threads)
        {
            if (string.Equals(thread.Id, threadId, StringComparison.Ordinal))
            {
                return lookup with { Thread = thread };
            }
        }

        return lookup with
        {
            Error = new ToolResult(
                $"Thread '{threadId}' is not a review thread of pull request #{lookup.PullRequest.Number.ToString(CultureInfo.InvariantCulture)}. " +
                "Copy the id from pr_comments.",
                IsError: true),
        };
    }

    /// <summary>
    /// The login the tools act as: the resolver's answer, else what the ledger learned from a reply it posted. Null when
    /// neither is known. Never throws except for cancellation – a failing lookup only means less evidence.
    /// </summary>
    public async Task<string?> CurrentUserAsync(string directory, CancellationToken ct)
    {
        if (users is not null)
        {
            try
            {
                var login = await users(directory, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(login))
                {
                    return login;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Falls through to what the ledger knows.
            }
        }

        return Ledger.CurrentUser;
    }

    /// <summary>The verdict reply of <paramref name="thread"/>, looking the user up only when a marker reply exists at all.</summary>
    public async Task<PrReplyRecord?> EvaluateAsync(string directory, PrReviewThread thread, CancellationToken ct)
    {
        if (!PrReplyLedger.HasMarkerReply(thread))
        {
            return null;
        }

        var user = await CurrentUserAsync(directory, ct).ConfigureAwait(false);
        return Ledger.Evaluate(thread, user);
    }

    /// <summary>Turns a failed platform call into the tool error: what failed, the classification and the next step.</summary>
    public static ToolResult Failure<T>(PrResult<T> result, string what)
    {
        if (result.PullRequestNotFound)
        {
            return new ToolResult(
                "No pull request exists for this branch yet, so there are no review comments.\nNext: create it with pr_create.",
                IsError: true);
        }

        string? hint = null;
        if (result.Reason == PrFailureReason.PlatformNotInstalled)
        {
            hint = "Install the GitHub CLI (https://cli.github.com/) and run gh auth login. Local git tools are unaffected.";
        }
        else if (result.Status.Failure == GitRemoteFailureKind.AuthenticationFailed)
        {
            hint = "The GitHub CLI is not logged in or lacks access to this repository. Run gh auth login (or gh auth refresh), then retry. Local git tools are unaffected.";
        }

        var status = result.Status.Failure == GitRemoteFailureKind.None
            ? result.Status with { Failure = GitRemoteFailureKind.Other }
            : result.Status;
        var report = GitToolFailure.ToToolResult(status, hint);
        return report with { Content = what + " " + report.Content };
    }

    /// <summary>The network timeout the settings ask for; zero or negative keeps the git default.</summary>
    public GitNetworkOptions NetworkOptions()
    {
        var timeout = Settings.NetworkTimeout();
        return timeout > TimeSpan.Zero ? new GitNetworkOptions { Timeout = timeout } : new GitNetworkOptions();
    }
}

/// <summary>Plain-text rendering of review threads for the model.</summary>
internal static class PrCommentText
{
    private const string CommentIndent = "     ";

    public static string Timestamp(DateTimeOffset? value) =>
        value is { } at ? at.UtcDateTime.ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture) : "unknown time";

    public static string Location(PrReviewThread thread)
    {
        if (string.IsNullOrEmpty(thread.Path))
        {
            return "(no file)";
        }

        var text = new StringBuilder(thread.Path);
        if (thread.Line is { } line)
        {
            text.Append(':');
            if (thread.StartLine is { } start && start != line)
            {
                text.Append(start.ToString(CultureInfo.InvariantCulture)).Append('-');
            }

            text.Append(line.ToString(CultureInfo.InvariantCulture));
        }
        else if (thread.OriginalLine is { } original)
        {
            text.Append(" (was line ").Append(original.ToString(CultureInfo.InvariantCulture)).Append(')');
        }

        if (string.Equals(thread.DiffSide, "LEFT", StringComparison.OrdinalIgnoreCase))
        {
            text.Append(" [old side]");
        }

        return text.ToString();
    }

    public static string VerdictText(PrReplyRecord record) =>
        record.Verdict == PrReplyVerdict.Apply
            ? $"apply ({Short(record.Commit)})"
            : PrReplyLedger.VerdictName(record.Verdict);

    public static string Short(string? sha) =>
        string.IsNullOrEmpty(sha) ? "?" : sha.Length <= 7 ? sha : sha[..7];

    public static void AppendThread(StringBuilder text, PrReviewThread thread, PrReplyRecord? verdict)
    {
        text.Append("Thread ").Append(thread.Id).Append(" [").Append(thread.IsResolved ? "resolved" : "unresolved");
        if (thread.IsOutdated)
        {
            text.Append(", outdated");
        }

        text.Append("] ").Append(Location(thread)).Append('\n');

        if (thread.IsResolved && !string.IsNullOrEmpty(thread.ResolvedBy))
        {
            text.Append("  resolved by ").Append(thread.ResolvedBy).Append('\n');
        }

        if (verdict is not null)
        {
            text.Append("  your last verdict: ").Append(VerdictText(verdict)).Append('\n');
        }
        else if (!thread.IsResolved)
        {
            text.Append("  no verdict from you yet\n");
        }

        for (var index = 0; index < thread.Comments.Count; index++)
        {
            var comment = thread.Comments[index];
            text.Append("  ").Append((index + 1).ToString(CultureInfo.InvariantCulture)).Append(". ")
                .Append(comment.Author ?? "(unknown author)")
                .Append(" · ").Append(Timestamp(comment.CreatedAt)).Append('\n');
            AppendIndented(text, comment.Body);
        }
    }

    private static void AppendIndented(StringBuilder text, string body)
    {
        var normalized = body.ReplaceLineEndings("\n").TrimEnd('\n');
        if (normalized.Length == 0)
        {
            text.Append(CommentIndent).Append("(empty)\n");
            return;
        }

        foreach (var line in normalized.Split('\n'))
        {
            text.Append(CommentIndent).Append(line).Append('\n');
        }
    }
}
