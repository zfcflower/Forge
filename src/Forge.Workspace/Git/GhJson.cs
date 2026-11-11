using System.Text.Json.Serialization;

namespace Forge.Workspace;

// Wire shapes of everything gh prints as JSON. They are deliberately loose: every member is nullable or defaulted,
// unknown members are ignored, and enum-like values stay strings so an unfamiliar one can never fail the whole document
// (GhParsers maps them with an Unknown fallback). Dates stay strings for the same reason: gh prints the zero time
// "0001-01-01T00:00:00Z" for "not yet", and one malformed date must not discard a list of checks.

internal sealed class GhPullRequestDto
{
    public int? Number { get; set; }

    public string? Url { get; set; }

    public string? State { get; set; }

    public string? Title { get; set; }

    public string? Body { get; set; }

    public string? HeadRefName { get; set; }

    public string? BaseRefName { get; set; }

    public string? HeadRefOid { get; set; }

    public bool? IsDraft { get; set; }

    public string? MergeStateStatus { get; set; }

    public string? ReviewDecision { get; set; }

    public string? Mergeable { get; set; }

    public bool? IsCrossRepository { get; set; }
}

internal sealed class GhCheckDto
{
    public string? Name { get; set; }

    public string? State { get; set; }

    public string? Bucket { get; set; }

    public string? Link { get; set; }

    public string? Workflow { get; set; }

    public string? StartedAt { get; set; }

    public string? CompletedAt { get; set; }

    public string? Description { get; set; }
}

internal sealed class GhRepositoryDto
{
    public string? Name { get; set; }

    public string? Url { get; set; }

    public GhActorDto? Owner { get; set; }
}

internal sealed class GhActorDto
{
    public string? Login { get; set; }
}

internal sealed class GhGraphQlErrorDto
{
    public string? Type { get; set; }

    public string? Message { get; set; }
}

/// <summary>Only the <c>errors</c> array of any GraphQL response, to read failures that arrive with a zero exit code.</summary>
internal sealed class GhErrorsResponseDto
{
    public List<GhGraphQlErrorDto?>? Errors { get; set; }
}

internal sealed class GhPageInfoDto
{
    public bool? HasNextPage { get; set; }

    public string? EndCursor { get; set; }
}

internal sealed class GhCommentDto
{
    public string? Id { get; set; }

    public long? DatabaseId { get; set; }

    public GhActorDto? Author { get; set; }

    public string? Body { get; set; }

    public string? CreatedAt { get; set; }

    public string? Url { get; set; }
}

internal sealed class GhCommentConnectionDto
{
    public GhPageInfoDto? PageInfo { get; set; }

    public List<GhCommentDto?>? Nodes { get; set; }
}

internal sealed class GhThreadDto
{
    public string? Id { get; set; }

    public bool? IsResolved { get; set; }

    public bool? IsOutdated { get; set; }

    public string? Path { get; set; }

    public int? Line { get; set; }

    public int? StartLine { get; set; }

    public int? OriginalLine { get; set; }

    public string? DiffSide { get; set; }

    public GhActorDto? ResolvedBy { get; set; }

    public GhCommentConnectionDto? Comments { get; set; }
}

internal sealed class GhThreadConnectionDto
{
    public GhPageInfoDto? PageInfo { get; set; }

    public List<GhThreadDto?>? Nodes { get; set; }
}

internal sealed class GhThreadsPullRequestDto
{
    public GhThreadConnectionDto? ReviewThreads { get; set; }
}

internal sealed class GhThreadsRepositoryDto
{
    public GhThreadsPullRequestDto? PullRequest { get; set; }
}

internal sealed class GhThreadsDataDto
{
    public GhThreadsRepositoryDto? Repository { get; set; }
}

internal sealed class GhThreadsResponseDto
{
    public GhThreadsDataDto? Data { get; set; }

    public List<GhGraphQlErrorDto?>? Errors { get; set; }
}

internal sealed class GhThreadNodeDto
{
    public GhCommentConnectionDto? Comments { get; set; }
}

internal sealed class GhThreadCommentsDataDto
{
    public GhThreadNodeDto? Node { get; set; }
}

internal sealed class GhThreadCommentsResponseDto
{
    public GhThreadCommentsDataDto? Data { get; set; }

    public List<GhGraphQlErrorDto?>? Errors { get; set; }
}

internal sealed class GhReplyPayloadDto
{
    public GhCommentDto? Comment { get; set; }
}

internal sealed class GhReplyDataDto
{
    public GhReplyPayloadDto? AddPullRequestReviewThreadReply { get; set; }
}

internal sealed class GhReplyResponseDto
{
    public GhReplyDataDto? Data { get; set; }

    public List<GhGraphQlErrorDto?>? Errors { get; set; }
}

internal sealed class GhResolvedThreadDto
{
    public string? Id { get; set; }

    public bool? IsResolved { get; set; }
}

internal sealed class GhResolvePayloadDto
{
    public GhResolvedThreadDto? Thread { get; set; }
}

internal sealed class GhResolveDataDto
{
    public GhResolvePayloadDto? ResolveReviewThread { get; set; }
}

internal sealed class GhResolveResponseDto
{
    public GhResolveDataDto? Data { get; set; }

    public List<GhGraphQlErrorDto?>? Errors { get; set; }
}

/// <summary>
/// Source-generated metadata for every gh JSON shape; reflection-based serialization is unavailable under Native AOT.
/// Member names match case-insensitively and unknown members are skipped.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
[JsonSerializable(typeof(GhPullRequestDto))]
[JsonSerializable(typeof(List<GhCheckDto?>))]
[JsonSerializable(typeof(GhRepositoryDto))]
[JsonSerializable(typeof(GhErrorsResponseDto))]
[JsonSerializable(typeof(GhThreadsResponseDto))]
[JsonSerializable(typeof(GhThreadCommentsResponseDto))]
[JsonSerializable(typeof(GhReplyResponseDto))]
[JsonSerializable(typeof(GhResolveResponseDto))]
internal sealed partial class GhJsonContext : JsonSerializerContext;
