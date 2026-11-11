using System.Globalization;
using System.Text.Json;

namespace Forge.Workspace;

/// <summary>A repository as gh resolves it from the working directory.</summary>
internal sealed record GhRepository(string? Host, string Owner, string Name);

/// <summary>One page of review threads; <see cref="Threads"/> carry the first page of each thread's comments.</summary>
internal sealed record GhThreadsPage(
    IReadOnlyList<GhThreadWithCursor> Threads,
    bool HasNextPage,
    string? EndCursor);

/// <summary>A thread with the cursor to fetch the rest of its comments when the first page did not hold them all.</summary>
internal sealed record GhThreadWithCursor(PrReviewThread Thread, bool CommentsHaveNextPage, string? CommentsEndCursor);

/// <summary>One page of comments of a single thread.</summary>
internal sealed record GhCommentsPage(IReadOnlyList<PrReviewComment> Comments, bool HasNextPage, string? EndCursor);

/// <summary>
/// Pure parsers over gh's output: text in, platform-neutral model out, no process and no I/O. Malformed or empty input
/// yields null (never an exception) and an unknown enum string yields the <c>Unknown</c> member, so a gh upgrade that
/// adds a state cannot break a read. Method names are unique (no overloads) so tests can reach them by name.
/// </summary>
internal static class GhParsers
{
    private const string RunsSegment = "/actions/runs/";
    private const string PullSegment = "/pull/";

    public static PullRequestInfo? ParsePullRequest(string json)
    {
        var dto = Deserialize(json, GhJsonContext.Default.GhPullRequestDto);
        if (dto is null || dto.Number is not { } number || string.IsNullOrEmpty(dto.Url))
        {
            return null;
        }

        return new PullRequestInfo(
            number,
            dto.Url,
            MapState(dto.State),
            dto.Title ?? string.Empty,
            dto.Body ?? string.Empty,
            dto.HeadRefName ?? string.Empty,
            dto.BaseRefName ?? string.Empty,
            dto.HeadRefOid ?? string.Empty,
            dto.IsDraft ?? false,
            MapMergeStateStatus(dto.MergeStateStatus),
            MapReviewDecision(dto.ReviewDecision))
        {
            IsCrossRepository = dto.IsCrossRepository ?? false,
        };
    }

    public static PrMergeState? ParseMergeState(string json)
    {
        var dto = Deserialize(json, GhJsonContext.Default.GhPullRequestDto);
        if (dto is null)
        {
            return null;
        }

        return new PrMergeState(
            MapState(dto.State),
            dto.IsDraft ?? false,
            MapMergeStateStatus(dto.MergeStateStatus),
            MapReviewDecision(dto.ReviewDecision),
            MapMergeable(dto.Mergeable),
            dto.MergeStateStatus ?? string.Empty,
            dto.ReviewDecision ?? string.Empty,
            dto.Mergeable ?? string.Empty);
    }

    /// <summary>Parses the JSON array of <c>gh pr checks --json</c>; an empty array is an empty list, malformed input is null.</summary>
    public static IReadOnlyList<PrCheck>? ParseChecks(string json)
    {
        var dtos = Deserialize(json, GhJsonContext.Default.ListGhCheckDto);
        if (dtos is null)
        {
            return null;
        }

        var checks = new List<PrCheck>(dtos.Count);
        foreach (var dto in dtos)
        {
            if (dto is null)
            {
                continue;
            }

            var (runId, jobId) = ParseRunAndJob(dto.Link);
            checks.Add(new PrCheck(
                dto.Name ?? string.Empty,
                dto.Workflow ?? string.Empty,
                dto.State ?? string.Empty,
                MapCheckBucket(dto.Bucket, dto.State),
                dto.Description ?? string.Empty,
                string.IsNullOrEmpty(dto.Link) ? null : dto.Link,
                ParseTimestamp(dto.StartedAt),
                ParseTimestamp(dto.CompletedAt),
                runId,
                jobId));
        }

        return checks;
    }

    /// <summary>Parses <c>gh repo view --json owner,name,url</c>.</summary>
    public static GhRepository? ParseRepository(string json)
    {
        var dto = Deserialize(json, GhJsonContext.Default.GhRepositoryDto);
        if (dto is null || string.IsNullOrEmpty(dto.Name) || string.IsNullOrEmpty(dto.Owner?.Login))
        {
            return null;
        }

        string? host = null;
        if (Uri.TryCreate(dto.Url, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
        {
            host = uri.Host;
        }

        return new GhRepository(host, dto.Owner.Login, dto.Name);
    }

    /// <summary>Parses one page of the <c>reviewThreads</c> query; null when the response holds no thread connection.</summary>
    public static GhThreadsPage? ParseThreadsPage(string json)
    {
        var connection = Deserialize(json, GhJsonContext.Default.GhThreadsResponseDto)?.Data?.Repository?.PullRequest?.ReviewThreads;
        if (connection is null)
        {
            return null;
        }

        var threads = new List<GhThreadWithCursor>(connection.Nodes?.Count ?? 0);
        foreach (var node in connection.Nodes ?? [])
        {
            if (node is null || string.IsNullOrEmpty(node.Id))
            {
                continue;
            }

            var comments = ConvertComments(node.Comments?.Nodes);
            var thread = new PrReviewThread(
                node.Id,
                node.IsResolved ?? false,
                node.IsOutdated ?? false,
                node.Path,
                node.Line,
                node.StartLine,
                node.OriginalLine,
                node.DiffSide,
                node.ResolvedBy?.Login,
                comments);
            threads.Add(new GhThreadWithCursor(
                thread,
                node.Comments?.PageInfo?.HasNextPage ?? false,
                node.Comments?.PageInfo?.EndCursor));
        }

        return new GhThreadsPage(threads, connection.PageInfo?.HasNextPage ?? false, connection.PageInfo?.EndCursor);
    }

    /// <summary>Parses one page of the single-thread <c>comments</c> query; null when the response holds no such node.</summary>
    public static GhCommentsPage? ParseCommentsPage(string json)
    {
        var connection = Deserialize(json, GhJsonContext.Default.GhThreadCommentsResponseDto)?.Data?.Node?.Comments;
        if (connection is null)
        {
            return null;
        }

        return new GhCommentsPage(
            ConvertComments(connection.Nodes),
            connection.PageInfo?.HasNextPage ?? false,
            connection.PageInfo?.EndCursor);
    }

    /// <summary>The comment created by <c>addPullRequestReviewThreadReply</c>; null when the response holds none.</summary>
    public static PrReviewComment? ParseReplyComment(string json)
    {
        var dto = Deserialize(json, GhJsonContext.Default.GhReplyResponseDto)?.Data?.AddPullRequestReviewThreadReply?.Comment;
        return dto is null ? null : ConvertComment(dto);
    }

    /// <summary>The thread state returned by <c>resolveReviewThread</c>; null when the response holds none.</summary>
    public static PrThreadResolution? ParseThreadResolution(string json)
    {
        var thread = Deserialize(json, GhJsonContext.Default.GhResolveResponseDto)?.Data?.ResolveReviewThread?.Thread;
        return thread is null || string.IsNullOrEmpty(thread.Id)
            ? null
            : new PrThreadResolution(thread.Id, thread.IsResolved ?? false);
    }

    /// <summary>The messages of a GraphQL response's <c>errors</c> array (gh exits non-zero for them, but a zero exit is not trusted).</summary>
    public static IReadOnlyList<string> ParseGraphQlErrors(string json)
    {
        var errors = Deserialize(json, GhJsonContext.Default.GhErrorsResponseDto)?.Errors;
        if (errors is null || errors.Count == 0)
        {
            return [];
        }

        var messages = new List<string>(errors.Count);
        foreach (var error in errors)
        {
            if (!string.IsNullOrWhiteSpace(error?.Message))
            {
                messages.Add(error.Message);
            }
        }

        return messages;
    }

    /// <summary>
    /// Run and job id of a workflow job link, <c>https://host/owner/repo/actions/runs/&lt;run&gt;/job/&lt;job&gt;</c>.
    /// A link to the run alone yields only the run id; anything else (an external status, a blank link) yields neither.
    /// </summary>
    public static (long? RunId, long? JobId) ParseRunAndJob(string? link)
    {
        if (string.IsNullOrEmpty(link))
        {
            return (null, null);
        }

        var at = link.IndexOf(RunsSegment, StringComparison.Ordinal);
        if (at < 0)
        {
            return (null, null);
        }

        var rest = link.AsSpan(at + RunsSegment.Length);
        var runId = ReadNumber(ref rest);
        if (runId is null)
        {
            return (null, null);
        }

        long? jobId = null;
        if (rest.StartsWith("/job/", StringComparison.Ordinal))
        {
            rest = rest[5..];
            jobId = ReadNumber(ref rest);
        }
        else if (rest.StartsWith("/jobs/", StringComparison.Ordinal))
        {
            rest = rest[6..];
            jobId = ReadNumber(ref rest);
        }

        return (runId, jobId);
    }

    /// <summary>The number in a <c>.../pull/&lt;n&gt;</c> address, or null.</summary>
    public static int? ParsePullRequestNumber(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var at = url.LastIndexOf(PullSegment, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var rest = url.AsSpan(at + PullSegment.Length);
        return ReadNumber(ref rest) is { } number && number <= int.MaxValue ? (int)number : null;
    }

    /// <summary>The last line of <paramref name="output"/> that is a pull request address, or null.</summary>
    public static string? FindPullRequestUrl(string output)
    {
        var lines = output.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("http", StringComparison.OrdinalIgnoreCase) && ParsePullRequestNumber(line) is not null)
            {
                return line;
            }
        }

        return null;
    }

    public static PrState MapState(string? value) => Match(value) switch
    {
        "OPEN" => PrState.Open,
        "CLOSED" => PrState.Closed,
        "MERGED" => PrState.Merged,
        _ => PrState.Unknown,
    };

    public static PrMergeStateStatus MapMergeStateStatus(string? value) => Match(value) switch
    {
        "CLEAN" => PrMergeStateStatus.Clean,
        "HAS_HOOKS" => PrMergeStateStatus.HasHooks,
        "BEHIND" => PrMergeStateStatus.Behind,
        "DIRTY" => PrMergeStateStatus.Dirty,
        "BLOCKED" => PrMergeStateStatus.Blocked,
        "UNSTABLE" => PrMergeStateStatus.Unstable,
        "DRAFT" => PrMergeStateStatus.Draft,
        _ => PrMergeStateStatus.Unknown,
    };

    /// <summary>Empty or missing means no review is required (<see cref="PrReviewDecision.None"/>); an unfamiliar value is <c>Unknown</c>.</summary>
    public static PrReviewDecision MapReviewDecision(string? value) => Match(value) switch
    {
        "" => PrReviewDecision.None,
        "APPROVED" => PrReviewDecision.Approved,
        "CHANGES_REQUESTED" => PrReviewDecision.ChangesRequested,
        "REVIEW_REQUIRED" => PrReviewDecision.ReviewRequired,
        _ => PrReviewDecision.Unknown,
    };

    public static PrMergeable MapMergeable(string? value) => Match(value) switch
    {
        "MERGEABLE" => PrMergeable.Mergeable,
        "CONFLICTING" => PrMergeable.Conflicting,
        _ => PrMergeable.Unknown,
    };

    /// <summary>
    /// Bucket from gh's <c>bucket</c> field; when that is missing or unfamiliar, derived from the raw check <c>state</c>.
    /// A state that is neither known pending, passing, failing nor skipped stays <see cref="PrCheckBucket.Unknown"/> –
    /// an uncertain check is never reported as a pass.
    /// </summary>
    public static PrCheckBucket MapCheckBucket(string? bucket, string? state)
    {
        switch (Match(bucket))
        {
            case "PASS":
                return PrCheckBucket.Pass;
            case "FAIL":
                return PrCheckBucket.Fail;
            case "PENDING":
                return PrCheckBucket.Pending;
            case "SKIPPING":
                return PrCheckBucket.Skipping;
            case "CANCEL":
                return PrCheckBucket.Cancel;
        }

        return Match(state) switch
        {
            "SUCCESS" => PrCheckBucket.Pass,
            "FAILURE" or "ERROR" or "TIMED_OUT" or "ACTION_REQUIRED" or "STARTUP_FAILURE" => PrCheckBucket.Fail,
            "PENDING" or "QUEUED" or "IN_PROGRESS" or "WAITING" or "REQUESTED" or "EXPECTED" => PrCheckBucket.Pending,
            "SKIPPED" or "NEUTRAL" or "STALE" => PrCheckBucket.Skipping,
            "CANCELLED" => PrCheckBucket.Cancel,
            _ => PrCheckBucket.Unknown,
        };
    }

    /// <summary>A timestamp, or null when blank, malformed, or the zero time gh prints for "not yet".</summary>
    public static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            || parsed.Year <= 1)
        {
            return null;
        }

        return parsed;
    }

    private static string Match(string? value) =>
        value is null ? string.Empty : value.Trim().ToUpperInvariant();

    private static IReadOnlyList<PrReviewComment> ConvertComments(List<GhCommentDto?>? nodes)
    {
        if (nodes is null || nodes.Count == 0)
        {
            return [];
        }

        var comments = new List<PrReviewComment>(nodes.Count);
        foreach (var node in nodes)
        {
            if (node is not null && !string.IsNullOrEmpty(node.Id))
            {
                comments.Add(ConvertComment(node));
            }
        }

        return comments;
    }

    private static PrReviewComment ConvertComment(GhCommentDto dto) =>
        new(dto.Id ?? string.Empty, dto.DatabaseId, dto.Author?.Login, dto.Body ?? string.Empty, ParseTimestamp(dto.CreatedAt), dto.Url);

    private static long? ReadNumber(ref ReadOnlySpan<char> text)
    {
        var length = 0;
        while (length < text.Length && char.IsAsciiDigit(text[length]))
        {
            length++;
        }

        if (length == 0 || !long.TryParse(text[..length], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        text = text[length..];
        return value;
    }

    private static T? Deserialize<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Extracts the host from the address of a git remote.</summary>
internal static class GhHostParser
{
    /// <summary>
    /// Host of a remote address: <c>https://host/...</c>, <c>ssh://git@host:22/...</c>, <c>git://host/...</c> or the
    /// scp-like <c>git@host:owner/repo</c>. Null for a local path (including a Windows drive path) or anything without a host.
    /// </summary>
    public static string? FromRemoteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var text = url.Trim();
        if (text.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : null;
        }

        // scp-like syntax: [user@]host:path. A leading '/' or '.', or a single letter before ':' (a drive), is a local path.
        if (text[0] is '/' or '.' or '\\' or '~')
        {
            return null;
        }

        var colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1)
        {
            return null;
        }

        var authority = text[..colon];
        var at = authority.LastIndexOf('@');
        var host = at >= 0 ? authority[(at + 1)..] : authority;
        if (host.Length < 2 || host.Contains('/') || host.Contains('\\'))
        {
            return null;
        }

        return host;
    }
}
