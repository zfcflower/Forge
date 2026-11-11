using System.Globalization;
using System.Text;

namespace Forge.Workspace;

/// <summary>
/// <see cref="IPrPlatform"/> over the GitHub CLI – the only implementation. Every call spawns <c>gh</c> in the given
/// working directory through <see cref="GhCli"/> and reads the platform fresh; nothing is cached. User text (titles,
/// bodies, replies) never becomes part of a command string: bodies go through a temporary file or a GraphQL variable
/// (<c>-f</c>, which is never interpreted), and selectors that gh could parse as an option are rejected. Pull request
/// merging is deliberately absent.
/// </summary>
public sealed class GitHubPlatform : IPrPlatform
{
    private const string PullRequestFields =
        "number,url,state,title,body,headRefName,baseRefName,headRefOid,isDraft,mergeStateStatus,reviewDecision,isCrossRepository";

    private const string MergeStateFields = "mergeStateStatus,isDraft,reviewDecision,mergeable,state";
    private const string CheckFields = "name,state,bucket,link,workflow,startedAt,completedAt,description";
    private const string RepositoryFields = "owner,name,url";

    // Variables are passed with -f/-F, never spliced into these strings.
    private const string ThreadsQuery =
        "query($owner:String!,$name:String!,$number:Int!,$cursor:String){repository(owner:$owner,name:$name){pullRequest(number:$number){" +
        "reviewThreads(first:100,after:$cursor){pageInfo{hasNextPage endCursor}nodes{id isResolved isOutdated path line startLine originalLine diffSide resolvedBy{login} " +
        "comments(first:100){pageInfo{hasNextPage endCursor} nodes{id databaseId author{login} body createdAt url}}}}}}}";

    private const string ThreadCommentsQuery =
        "query($id:ID!,$cursor:String){node(id:$id){... on PullRequestReviewThread{comments(first:100,after:$cursor){" +
        "pageInfo{hasNextPage endCursor} nodes{id databaseId author{login} body createdAt url}}}}}";

    private const string ReplyMutation =
        "mutation($threadId:ID!,$body:String!){addPullRequestReviewThreadReply(input:{pullRequestReviewThreadId:$threadId,body:$body}){" +
        "comment{id databaseId author{login} body createdAt url}}}";

    private const string ResolveMutation =
        "mutation($threadId:ID!){resolveReviewThread(input:{threadId:$threadId}){thread{id isResolved}}}";

    private const int MaxInlineBodyCharacters = 8000;
    private const int MaxMessageDetailCharacters = 500;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly GhCli gh;

    public GitHubPlatform(GhCli gh)
    {
        ArgumentNullException.ThrowIfNull(gh);
        this.gh = gh;
    }

    /// <summary>Cap on a returned job log; the end of the log is kept when it is exceeded.</summary>
    public int MaxLogCharacters { get; init; } = 8 * 1024 * 1024;

    /// <summary>Cap on a returned pull request diff; the beginning of the diff is kept when it is exceeded.</summary>
    public int MaxDiffCharacters { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// Safety bound on the pages of review threads read for one pull request (100 threads per page); a pull request that
    /// needs more is refused instead of returning a partial list. Overridable so the refusal can be tested without
    /// producing 100 pages.
    /// </summary>
    public int MaxThreadPages { get; init; } = 100;

    /// <summary>
    /// Safety bound on the extra comment pages read for one review thread (100 comments per page); a thread that needs more
    /// is refused instead of returning a partial thread.
    /// </summary>
    public int MaxCommentPages { get; init; } = 50;

    public string Name => "GitHub";

    public async Task<PrPlatformAvailability> GetAvailabilityAsync(string workingDirectory, CancellationToken ct = default)
    {
        var result = await gh.GetAvailabilityAsync(workingDirectory, ct).ConfigureAwait(false);
        var state = result.State switch
        {
            GhAvailability.NotInstalled => PrPlatformState.NotInstalled,
            GhAvailability.Ready => PrPlatformState.Ready,
            _ => PrPlatformState.NotAuthenticated,
        };
        return new PrPlatformAvailability(state, result.Detail, result.Host, result.Failure);
    }

    public async Task<PrResult<PullRequestInfo>> ViewPullRequestAsync(string workingDirectory, string? selector = null, CancellationToken ct = default)
    {
        var arguments = new List<string> { "pr", "view" };
        if (!string.IsNullOrWhiteSpace(selector))
        {
            if (LooksLikeOption(selector))
            {
                return Invalid<PullRequestInfo>($"'{selector}' is not a valid pull request number, URL or branch.");
            }

            arguments.Add(selector.Trim());
        }

        arguments.Add("--json");
        arguments.Add(PullRequestFields);

        var result = await gh.RunAsync(workingDirectory, arguments, null, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return Failure<PullRequestInfo>(result, "Could not read the pull request.");
        }

        var info = GhParsers.ParsePullRequest(result.StandardOutput);
        return info is null
            ? Malformed<PullRequestInfo>(result, "Could not read the pull request.")
            : PrResult<PullRequestInfo>.Ok(info);
    }

    public async Task<PrResult<PrReference>> CreatePullRequestAsync(
        string workingDirectory,
        string title,
        string body,
        string baseBranch,
        string headBranch,
        bool draft = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Invalid<PrReference>("A pull request needs a title.");
        }

        if (string.IsNullOrWhiteSpace(baseBranch) || LooksLikeOption(baseBranch))
        {
            return Invalid<PrReference>($"'{baseBranch}' is not a valid base branch.");
        }

        if (string.IsNullOrWhiteSpace(headBranch) || LooksLikeOption(headBranch))
        {
            return Invalid<PrReference>($"'{headBranch}' is not a valid head branch.");
        }

        ArgumentNullException.ThrowIfNull(body);

        string? bodyFile = null;
        try
        {
            bodyFile = NewTemporaryPath();
            await File.WriteAllTextAsync(bodyFile, body, Utf8NoBom, ct).ConfigureAwait(false);

            var arguments = new List<string>
            {
                "pr", "create",
                "--title", title,
                "--body-file", bodyFile,
                "--base", baseBranch.Trim(),
                "--head", headBranch.Trim(),
            };
            if (draft)
            {
                arguments.Add("--draft");
            }

            var result = await gh.RunAsync(workingDirectory, arguments, null, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return Failure<PrReference>(result, "Could not create the pull request.");
            }

            return ReferenceFrom(result, "Could not create the pull request.", fallbackNumber: null);
        }
        finally
        {
            DeleteQuietly(bodyFile);
        }
    }

    public async Task<PrResult<PrReference>> EditPullRequestAsync(
        string workingDirectory,
        int number,
        string? title = null,
        string? body = null,
        CancellationToken ct = default)
    {
        if (number <= 0)
        {
            return Invalid<PrReference>($"{number} is not a valid pull request number.");
        }

        if (title is null && body is null)
        {
            return Invalid<PrReference>("Nothing to edit: give a new title, a new body, or both.");
        }

        if (title is not null && string.IsNullOrWhiteSpace(title))
        {
            return Invalid<PrReference>("A pull request title cannot be blank.");
        }

        string? bodyFile = null;
        try
        {
            var arguments = new List<string> { "pr", "edit", Number(number) };
            if (title is not null)
            {
                arguments.Add("--title");
                arguments.Add(title);
            }

            if (body is not null)
            {
                bodyFile = NewTemporaryPath();
                await File.WriteAllTextAsync(bodyFile, body, Utf8NoBom, ct).ConfigureAwait(false);
                arguments.Add("--body-file");
                arguments.Add(bodyFile);
            }

            var result = await gh.RunAsync(workingDirectory, arguments, null, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return Failure<PrReference>(result, "Could not edit the pull request.");
            }

            return ReferenceFrom(result, "Could not edit the pull request.", number);
        }
        finally
        {
            DeleteQuietly(bodyFile);
        }
    }

    public async Task<PrResult<PrChecksSnapshot>> GetChecksAsync(string workingDirectory, int number, CancellationToken ct = default)
    {
        if (number <= 0)
        {
            return Invalid<PrChecksSnapshot>($"{number} is not a valid pull request number.");
        }

        var arguments = new List<string> { "pr", "checks", Number(number), "--json", CheckFields };
        var result = await gh.RunAsync(workingDirectory, arguments, null, ct).ConfigureAwait(false);

        // gh exits 8 while checks are pending and 1 when some failed, and prints the JSON either way: whenever stdout
        // is a valid JSON array it is the answer, whatever the exit code says.
        if (!result.TimedOut && !result.StartFailed)
        {
            var text = result.StandardOutput.Trim();
            if (text.StartsWith('['))
            {
                var checks = GhParsers.ParseChecks(text);
                if (checks is not null)
                {
                    return PrResult<PrChecksSnapshot>.Ok(new PrChecksSnapshot(checks));
                }
            }

            // No checks registered (yet) is an empty read, not an error. Depending on the gh version it arrives as a
            // message with a non-zero exit code or as an empty result.
            if (text.Length == 0 && result.StandardError.Contains("no checks reported", StringComparison.OrdinalIgnoreCase))
            {
                return PrResult<PrChecksSnapshot>.Ok(new PrChecksSnapshot([]));
            }

            if (result.Success)
            {
                return text.Length == 0
                    ? PrResult<PrChecksSnapshot>.Ok(new PrChecksSnapshot([]))
                    : Malformed<PrChecksSnapshot>(result, "Could not read the checks.");
            }
        }

        return Failure<PrChecksSnapshot>(result, "Could not read the checks.");
    }

    public async Task<PrResult<PrMergeState>> GetMergeStateAsync(string workingDirectory, int number, CancellationToken ct = default)
    {
        if (number <= 0)
        {
            return Invalid<PrMergeState>($"{number} is not a valid pull request number.");
        }

        var arguments = new List<string> { "pr", "view", Number(number), "--json", MergeStateFields };
        var result = await gh.RunAsync(workingDirectory, arguments, null, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return Failure<PrMergeState>(result, "Could not read the merge state.");
        }

        var state = GhParsers.ParseMergeState(result.StandardOutput);
        return state is null
            ? Malformed<PrMergeState>(result, "Could not read the merge state.")
            : PrResult<PrMergeState>.Ok(state);
    }

    public async Task<PrResult<PrReference>> MergePullRequestAsync(
        string workingDirectory,
        int number,
        PrMergeMethod method,
        bool deleteBranch = false,
        CancellationToken ct = default)
    {
        if (number <= 0)
        {
            return Invalid<PrReference>($"{number} is not a valid pull request number.");
        }

        var arguments = new List<string> { "pr", "merge", Number(number) };
        arguments.Add(method switch
        {
            PrMergeMethod.Squash => "--squash",
            PrMergeMethod.Rebase => "--rebase",
            _ => "--merge",
        });

        if (deleteBranch)
        {
            arguments.Add("--delete-branch");
        }

        var result = await gh.RunAsync(workingDirectory, arguments, null, ct).ConfigureAwait(false);
        return result.Success
            ? PrResult<PrReference>.Ok(new PrReference(number, string.Empty), result.StandardOutput.Trim())
            : Failure<PrReference>(result, "Could not merge the pull request.");
    }

    public async Task<PrResult<PrText>> GetFailedJobLogAsync(string workingDirectory, long? runId, long jobId, CancellationToken ct = default)
    {
        if (jobId <= 0 || runId is <= 0)
        {
            return Invalid<PrText>("The run and job ids must be positive numbers.");
        }

        var arguments = new List<string> { "run", "view" };
        if (runId is { } run)
        {
            arguments.Add(run.ToString(CultureInfo.InvariantCulture));
        }

        arguments.Add("--job");
        arguments.Add(jobId.ToString(CultureInfo.InvariantCulture));
        arguments.Add("--log-failed");

        var options = new GhRunOptions { MaxCapturedCharacters = MaxLogCharacters, KeepTail = true };
        var result = await gh.RunAsync(workingDirectory, arguments, options, ct).ConfigureAwait(false);
        return result.Success
            ? PrResult<PrText>.Ok(new PrText(result.StandardOutput, result.IsTruncated))
            : Failure<PrText>(result, "Could not read the job log.");
    }

    public async Task<PrResult<PrText>> GetPullRequestDiffAsync(string workingDirectory, int number, CancellationToken ct = default)
    {
        if (number <= 0)
        {
            return Invalid<PrText>($"{number} is not a valid pull request number.");
        }

        var arguments = new List<string> { "pr", "diff", Number(number), "--color=never" };
        var options = new GhRunOptions { MaxCapturedCharacters = MaxDiffCharacters };
        var result = await gh.RunAsync(workingDirectory, arguments, options, ct).ConfigureAwait(false);
        return result.Success
            ? PrResult<PrText>.Ok(new PrText(result.StandardOutput, result.IsTruncated))
            : Failure<PrText>(result, "Could not read the pull request diff.");
    }

    public async Task<PrResult<IReadOnlyList<PrReviewThread>>> GetReviewThreadsAsync(string workingDirectory, int number, CancellationToken ct = default)
    {
        if (number <= 0)
        {
            return Invalid<IReadOnlyList<PrReviewThread>>($"{number} is not a valid pull request number.");
        }

        var repository = await ResolveRepositoryAsync(workingDirectory, ct).ConfigureAwait(false);
        if (repository.Value is not { } repo)
        {
            return Forward<GhRepository, IReadOnlyList<PrReviewThread>>(repository);
        }

        var threads = new List<PrReviewThread>();
        string? cursor = null;
        for (var page = 0; ; page++)
        {
            if (page >= MaxThreadPages)
            {
                return PrResult<IReadOnlyList<PrReviewThread>>.Fail(
                    GitRemoteFailureKind.Other,
                    $"The pull request has more than {MaxThreadPages * 100} review threads; refusing to return a partial list.");
            }

            var fields = new List<string> { "-f", "owner=" + repo.Owner, "-f", "name=" + repo.Name, "-F", "number=" + Number(number) };
            if (cursor is not null)
            {
                fields.Add("-f");
                fields.Add("cursor=" + cursor);
            }

            var response = await RunGraphQlAsync(workingDirectory, repo.Host, ThreadsQuery, fields, ct).ConfigureAwait(false);
            if (!response.Success)
            {
                return Failure<IReadOnlyList<PrReviewThread>>(response, "Could not read the review threads.");
            }

            var parsed = GhParsers.ParseThreadsPage(response.StandardOutput);
            if (parsed is null)
            {
                return FromGraphQlErrors<IReadOnlyList<PrReviewThread>>(response, "Could not read the review threads.");
            }

            foreach (var item in parsed.Threads)
            {
                var thread = item.Thread;
                if (item.CommentsHaveNextPage)
                {
                    var completed = await CompleteCommentsAsync(workingDirectory, repo.Host, item, ct).ConfigureAwait(false);
                    if (completed.Value is not { } full)
                    {
                        return Forward<PrReviewThread, IReadOnlyList<PrReviewThread>>(completed);
                    }

                    thread = full;
                }

                threads.Add(thread);
            }

            if (!parsed.HasNextPage)
            {
                break;
            }

            if (string.IsNullOrEmpty(parsed.EndCursor) || string.Equals(parsed.EndCursor, cursor, StringComparison.Ordinal))
            {
                return PrResult<IReadOnlyList<PrReviewThread>>.Fail(
                    GitRemoteFailureKind.Other,
                    "Could not read the review threads: the platform reported more pages but gave no usable cursor.");
            }

            cursor = parsed.EndCursor;
        }

        return PrResult<IReadOnlyList<PrReviewThread>>.Ok(threads);
    }

    public async Task<PrResult<PrReviewComment>> ReplyToReviewThreadAsync(string workingDirectory, string threadId, string body, CancellationToken ct = default)
    {
        if (!IsUsableId(threadId))
        {
            return Invalid<PrReviewComment>("The review thread id is missing or malformed.");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return Invalid<PrReviewComment>("A reply needs a body.");
        }

        var repository = await ResolveRepositoryAsync(workingDirectory, ct).ConfigureAwait(false);
        if (repository.Value is not { } repo)
        {
            return Forward<GhRepository, PrReviewComment>(repository);
        }

        string? bodyFile = null;
        try
        {
            var fields = new List<string> { "-f", "threadId=" + threadId };
            if (body.Length > MaxInlineBodyCharacters)
            {
                // A very long reply would risk the operating system's command-line limit; gh reads it from a file instead.
                bodyFile = NewTemporaryPath();
                await File.WriteAllTextAsync(bodyFile, body, Utf8NoBom, ct).ConfigureAwait(false);
                fields.Add("-F");
                fields.Add("body=@" + bodyFile);
            }
            else
            {
                fields.Add("-f");
                fields.Add("body=" + body);
            }

            var response = await RunGraphQlAsync(workingDirectory, repo.Host, ReplyMutation, fields, ct).ConfigureAwait(false);
            if (!response.Success)
            {
                return Failure<PrReviewComment>(response, "Could not post the reply.");
            }

            var comment = GhParsers.ParseReplyComment(response.StandardOutput);
            return comment is null
                ? FromGraphQlErrors<PrReviewComment>(response, "Could not post the reply.")
                : PrResult<PrReviewComment>.Ok(comment);
        }
        finally
        {
            DeleteQuietly(bodyFile);
        }
    }

    public async Task<PrResult<PrThreadResolution>> ResolveReviewThreadAsync(string workingDirectory, string threadId, CancellationToken ct = default)
    {
        if (!IsUsableId(threadId))
        {
            return Invalid<PrThreadResolution>("The review thread id is missing or malformed.");
        }

        var repository = await ResolveRepositoryAsync(workingDirectory, ct).ConfigureAwait(false);
        if (repository.Value is not { } repo)
        {
            return Forward<GhRepository, PrThreadResolution>(repository);
        }

        var fields = new List<string> { "-f", "threadId=" + threadId };
        var response = await RunGraphQlAsync(workingDirectory, repo.Host, ResolveMutation, fields, ct).ConfigureAwait(false);
        if (!response.Success)
        {
            return Failure<PrThreadResolution>(response, "Could not resolve the review thread.");
        }

        var resolution = GhParsers.ParseThreadResolution(response.StandardOutput);
        return resolution is null
            ? FromGraphQlErrors<PrThreadResolution>(response, "Could not resolve the review thread.")
            : PrResult<PrThreadResolution>.Ok(resolution);
    }

    /// <summary>Fetches the remaining comment pages of one thread and returns the thread with all of them.</summary>
    private async Task<PrResult<PrReviewThread>> CompleteCommentsAsync(
        string workingDirectory,
        string? host,
        GhThreadWithCursor item,
        CancellationToken ct)
    {
        var comments = new List<PrReviewComment>(item.Thread.Comments);
        var cursor = item.CommentsEndCursor;
        for (var page = 0; ; page++)
        {
            if (page >= MaxCommentPages)
            {
                return PrResult<PrReviewThread>.Fail(
                    GitRemoteFailureKind.Other,
                    $"A review thread has more than {MaxCommentPages * 100} comments; refusing to return a partial thread.");
            }

            if (string.IsNullOrEmpty(cursor))
            {
                return PrResult<PrReviewThread>.Fail(
                    GitRemoteFailureKind.Other,
                    "Could not read all comments of a review thread: the platform reported more pages but gave no usable cursor.");
            }

            var fields = new List<string> { "-f", "id=" + item.Thread.Id, "-f", "cursor=" + cursor };
            var response = await RunGraphQlAsync(workingDirectory, host, ThreadCommentsQuery, fields, ct).ConfigureAwait(false);
            if (!response.Success)
            {
                return Failure<PrReviewThread>(response, "Could not read all comments of a review thread.");
            }

            var parsed = GhParsers.ParseCommentsPage(response.StandardOutput);
            if (parsed is null)
            {
                return FromGraphQlErrors<PrReviewThread>(response, "Could not read all comments of a review thread.");
            }

            comments.AddRange(parsed.Comments);
            if (!parsed.HasNextPage)
            {
                return PrResult<PrReviewThread>.Ok(item.Thread with { Comments = comments });
            }

            if (string.IsNullOrEmpty(parsed.EndCursor) || string.Equals(parsed.EndCursor, cursor, StringComparison.Ordinal))
            {
                return PrResult<PrReviewThread>.Fail(
                    GitRemoteFailureKind.Other,
                    "Could not read all comments of a review thread: the platform reported more pages but gave no usable cursor.");
            }

            cursor = parsed.EndCursor;
        }
    }

    /// <summary>Asks gh which repository (and host) the working directory belongs to, the way every other gh command resolves it.</summary>
    private async Task<PrResult<GhRepository>> ResolveRepositoryAsync(string workingDirectory, CancellationToken ct)
    {
        var arguments = new List<string> { "repo", "view", "--json", RepositoryFields };
        var result = await gh.RunAsync(workingDirectory, arguments, null, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return Failure<GhRepository>(result, "Could not determine the GitHub repository of this directory.");
        }

        var repository = GhParsers.ParseRepository(result.StandardOutput);
        return repository is null
            ? Malformed<GhRepository>(result, "Could not determine the GitHub repository of this directory.")
            : PrResult<GhRepository>.Ok(repository);
    }

    private Task<GhCommandResult> RunGraphQlAsync(
        string workingDirectory,
        string? host,
        string query,
        List<string> fields,
        CancellationToken ct)
    {
        var arguments = new List<string>(fields.Count + 6) { "api", "graphql" };
        if (!string.IsNullOrEmpty(host))
        {
            arguments.Add("--hostname");
            arguments.Add(host);
        }

        arguments.Add("-f");
        arguments.Add("query=" + query);
        arguments.AddRange(fields);
        return gh.RunAsync(workingDirectory, arguments, null, ct);
    }

    private static PrResult<PrReference> ReferenceFrom(GhCommandResult result, string action, int? fallbackNumber)
    {
        var url = GhParsers.FindPullRequestUrl(result.StandardOutput);
        if (url is not null)
        {
            var number = GhParsers.ParsePullRequestNumber(url) ?? fallbackNumber;
            if (number is { } known)
            {
                return PrResult<PrReference>.Ok(new PrReference(known, url));
            }
        }

        if (fallbackNumber is { } expected)
        {
            // An edit succeeded (exit code 0) but printed no address; the number the caller gave is still right.
            return PrResult<PrReference>.Ok(new PrReference(expected, string.Empty));
        }

        return Malformed<PrReference>(result, action);
    }

    private static PrResult<T> Invalid<T>(string message) =>
        PrResult<T>.Fail(GitRemoteFailureKind.Other, message);

    /// <summary>Re-reports a failed intermediate step (repository lookup, comment page) as a failure of the whole call.</summary>
    private static PrResult<TOut> Forward<TIn, TOut>(PrResult<TIn> failed) =>
        PrResult<TOut>.Fail(failed.Status.Failure, failed.Status.Message, failed.Status.StandardError, failed.Reason);

    /// <summary>Turns a failed gh invocation into a result: classified kind, the stderr text in the message, the raw stderr attached.</summary>
    private static PrResult<T> Failure<T>(GhCommandResult result, string action)
    {
        var rawError = result.StandardError.Trim();
        if (result.StartFailed)
        {
            return PrResult<T>.Fail(
                GitRemoteFailureKind.Other,
                $"{action} The GitHub CLI (gh) could not be started: {rawError} Install it from https://cli.github.com/ and make sure it is on PATH.",
                string.Empty,
                PrFailureReason.PlatformNotInstalled);
        }

        if (result.TimedOut)
        {
            return PrResult<T>.Fail(GitRemoteFailureKind.TimedOut, $"{action} The GitHub CLI timed out.", rawError);
        }

        var kind = GhFailureClassifier.Classify(result);
        var reason = GhFailureClassifier.IsPullRequestNotFound(result)
            ? PrFailureReason.PullRequestNotFound
            : GhFailureClassifier.IsPullRequestAlreadyExists(result)
                ? PrFailureReason.PullRequestAlreadyExists
                : PrFailureReason.None;

        var detail = Shorten(rawError.Length > 0 ? rawError : result.StandardOutput);
        var message = detail.Length == 0
            ? $"{action} gh exited with code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}."
            : $"{action} {detail}";
        return PrResult<T>.Fail(kind == GitRemoteFailureKind.None ? GitRemoteFailureKind.Other : kind, message, rawError, reason);
    }

    /// <summary>The call exited 0 but its output was not what was asked for (or carried GraphQL errors instead of data).</summary>
    private static PrResult<T> Malformed<T>(GhCommandResult result, string action) =>
        FromGraphQlErrors<T>(result, action);

    private static PrResult<T> FromGraphQlErrors<T>(GhCommandResult result, string action)
    {
        var rawError = result.StandardError.Trim();
        var errors = GhParsers.ParseGraphQlErrors(result.StandardOutput);
        if (errors.Count == 0)
        {
            return PrResult<T>.Fail(
                GitRemoteFailureKind.Other,
                $"{action} The GitHub CLI returned output in an unexpected format.",
                rawError);
        }

        var text = string.Join(' ', errors);
        var kind = GhFailureClassifier.ClassifyText(text);
        var reason = GhFailureClassifier.IsPullRequestNotFoundText(text) ? PrFailureReason.PullRequestNotFound : PrFailureReason.None;
        return PrResult<T>.Fail(kind, $"{action} {Shorten(text)}", rawError, reason);
    }

    private static string Shorten(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= MaxMessageDetailCharacters ? trimmed : trimmed[..MaxMessageDetailCharacters] + "…";
    }

    private static string Number(int number) => number.ToString(CultureInfo.InvariantCulture);

    /// <summary>A leading '-' would make gh parse a positional value as a flag.</summary>
    private static bool LooksLikeOption(string value) => value.TrimStart().StartsWith('-');

    private static bool IsUsableId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        foreach (var character in id)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    private static string NewTemporaryPath() =>
        Path.Combine(Path.GetTempPath(), "forge-gh-" + Guid.NewGuid().ToString("n") + ".md");

    private static void DeleteQuietly(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
