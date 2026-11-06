using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// Minimal smoke coverage of the pure gh parsers, over output shapes captured from gh 2.x. The full suite lives with the
/// test agent; these pin the contract the platform layer relies on: tolerant of unknown values, never throwing.
/// </summary>
public sealed class GhParserSmokeTests
{
    private const string ChecksJson = """
        [
          {"name":"build","state":"SUCCESS","bucket":"pass","link":"https://github.com/acme/forge/actions/runs/1001/job/2002","workflow":"CI","startedAt":"2026-05-01T10:00:00Z","completedAt":"2026-05-01T10:05:00Z","description":"","event":"pull_request"},
          {"name":"lint","state":"FAILURE","bucket":"fail","link":"https://github.com/acme/forge/actions/runs/1001/job/2003","workflow":"CI","startedAt":"2026-05-01T10:00:00Z","completedAt":"0001-01-01T00:00:00Z","description":"1 error"},
          {"name":"deploy","state":"IN_PROGRESS","bucket":"pending","link":"https://example.com/status/9","workflow":"","startedAt":"0001-01-01T00:00:00Z","completedAt":"0001-01-01T00:00:00Z","description":""},
          {"name":"future","state":"SOMETHING_NEW","bucket":"brand-new-bucket","link":"","workflow":"CI","description":""}
        ]
        """;

    [Fact]
    public void Checks_are_mapped_with_run_and_job_ids_and_zero_times_dropped()
    {
        var checks = Internals.InvokeStatic<IReadOnlyList<PrCheck>?>("GhParsers", "ParseChecks", ChecksJson);

        Assert.NotNull(checks);
        Assert.Equal(4, checks.Count);

        Assert.Equal(PrCheckBucket.Pass, checks[0].Bucket);
        Assert.Equal(1001, checks[0].RunId);
        Assert.Equal(2002, checks[0].JobId);
        Assert.NotNull(checks[0].CompletedAt);

        Assert.Equal(PrCheckBucket.Fail, checks[1].Bucket);
        Assert.Equal(2003, checks[1].JobId);
        Assert.Null(checks[1].CompletedAt);

        Assert.Equal(PrCheckBucket.Pending, checks[2].Bucket);
        Assert.Null(checks[2].RunId);
        Assert.Null(checks[2].StartedAt);
        Assert.Equal("https://example.com/status/9", checks[2].Link);

        // An unfamiliar bucket and state never turns into a pass and never throws.
        Assert.Equal(PrCheckBucket.Unknown, checks[3].Bucket);
        Assert.Null(checks[3].Link);
    }

    [Fact]
    public void Empty_array_is_an_empty_list_and_garbage_is_null()
    {
        var empty = Internals.InvokeStatic<IReadOnlyList<PrCheck>?>("GhParsers", "ParseChecks", "[]");
        Assert.NotNull(empty);
        Assert.Empty(empty);

        Assert.Null(Internals.InvokeStatic<IReadOnlyList<PrCheck>?>("GhParsers", "ParseChecks", "no checks reported on the 'x' branch"));
        Assert.Null(Internals.InvokeStatic<IReadOnlyList<PrCheck>?>("GhParsers", "ParseChecks", string.Empty));
    }

    [Fact]
    public void Pull_request_ignores_unknown_fields_and_maps_enums()
    {
        const string json = """
            {"number":42,"url":"https://github.com/acme/forge/pull/42","state":"OPEN","title":"Add it","body":"text",
             "headRefName":"feature","baseRefName":"main","headRefOid":"abc123","isDraft":false,
             "mergeStateStatus":"HAS_HOOKS","reviewDecision":"","somethingGhAddedLater":{"x":1}}
            """;

        var info = Internals.InvokeStatic<PullRequestInfo?>("GhParsers", "ParsePullRequest", json);

        Assert.NotNull(info);
        Assert.Equal(42, info.Number);
        Assert.Equal(PrState.Open, info.State);
        Assert.Equal(PrMergeStateStatus.HasHooks, info.MergeStateStatus);
        Assert.Equal(PrReviewDecision.None, info.ReviewDecision);
        Assert.Equal("feature", info.HeadRefName);
    }

    [Theory]
    [InlineData("CLEAN", PrMergeStateStatus.Clean)]
    [InlineData("HAS_HOOKS", PrMergeStateStatus.HasHooks)]
    [InlineData("BEHIND", PrMergeStateStatus.Behind)]
    [InlineData("DIRTY", PrMergeStateStatus.Dirty)]
    [InlineData("BLOCKED", PrMergeStateStatus.Blocked)]
    [InlineData("UNSTABLE", PrMergeStateStatus.Unstable)]
    [InlineData("DRAFT", PrMergeStateStatus.Draft)]
    [InlineData("UNKNOWN", PrMergeStateStatus.Unknown)]
    [InlineData("SOMETHING_NEW", PrMergeStateStatus.Unknown)]
    [InlineData("", PrMergeStateStatus.Unknown)]
    [InlineData(null, PrMergeStateStatus.Unknown)]
    public void Merge_state_status_maps_with_unknown_fallback(string? value, PrMergeStateStatus expected) =>
        Assert.Equal(expected, Internals.InvokeStatic<PrMergeStateStatus>("GhParsers", "MapMergeStateStatus", value));

    [Theory]
    [InlineData("pass", "SUCCESS", PrCheckBucket.Pass)]
    [InlineData("fail", "FAILURE", PrCheckBucket.Fail)]
    [InlineData("pending", "QUEUED", PrCheckBucket.Pending)]
    [InlineData("skipping", "SKIPPED", PrCheckBucket.Skipping)]
    [InlineData("cancel", "CANCELLED", PrCheckBucket.Cancel)]
    [InlineData(null, "TIMED_OUT", PrCheckBucket.Fail)]
    [InlineData("", "STARTUP_FAILURE", PrCheckBucket.Fail)]
    [InlineData("whatever", "whatever", PrCheckBucket.Unknown)]
    public void Check_bucket_maps_from_bucket_then_state(string? bucket, string? state, PrCheckBucket expected) =>
        Assert.Equal(expected, Internals.InvokeStatic<PrCheckBucket>("GhParsers", "MapCheckBucket", bucket, state));

    [Theory]
    [InlineData("https://github.com/acme/forge/actions/runs/77/job/88", 77L, 88L)]
    [InlineData("https://github.com/acme/forge/actions/runs/77/jobs/88", 77L, 88L)]
    [InlineData("https://github.com/acme/forge/actions/runs/77", 77L, null)]
    [InlineData("https://ci.example.com/build/5", null, null)]
    [InlineData("", null, null)]
    [InlineData(null, null, null)]
    public void Run_and_job_are_read_from_workflow_links(string? link, long? run, long? job)
    {
        var (runId, jobId) = Internals.InvokeStatic<(long? RunId, long? JobId)>("GhParsers", "ParseRunAndJob", link);

        Assert.Equal(run, runId);
        Assert.Equal(job, jobId);
    }

    [Theory]
    [InlineData("https://github.com/acme/forge/pull/42", 42)]
    [InlineData("https://ghe.example.com/acme/forge/pull/7/files", 7)]
    [InlineData("https://github.com/acme/forge/issues/42", null)]
    [InlineData(null, null)]
    public void Pull_request_number_is_read_from_its_address(string? url, int? expected) =>
        Assert.Equal(expected, Internals.InvokeStatic<int?>("GhParsers", "ParsePullRequestNumber", url));

    [Fact]
    public void Created_pull_request_address_is_found_after_progress_lines()
    {
        const string output = "Warning: 1 uncommitted change\n\nCreating pull request for feature into main in acme/forge\n\nhttps://github.com/acme/forge/pull/9\n";

        Assert.Equal(
            "https://github.com/acme/forge/pull/9",
            Internals.InvokeStatic<string?>("GhParsers", "FindPullRequestUrl", output));
    }

    [Fact]
    public void Merge_state_keeps_the_raw_text_next_to_the_mapped_enum()
    {
        const string json = """{"state":"OPEN","isDraft":true,"mergeStateStatus":"BRAND_NEW","reviewDecision":"REVIEW_REQUIRED","mergeable":"CONFLICTING"}""";

        var state = Internals.InvokeStatic<PrMergeState?>("GhParsers", "ParseMergeState", json);

        Assert.NotNull(state);
        Assert.True(state.IsDraft);
        Assert.Equal(PrMergeStateStatus.Unknown, state.MergeStateStatus);
        Assert.Equal("BRAND_NEW", state.MergeStateStatusText);
        Assert.Equal(PrReviewDecision.ReviewRequired, state.ReviewDecision);
        Assert.Equal(PrMergeable.Conflicting, state.Mergeable);
    }

    [Theory]
    [InlineData("https://github.com/acme/forge.git", "github.com")]
    [InlineData("https://ghe.example.com:8443/acme/forge", "ghe.example.com")]
    [InlineData("git@github.com:acme/forge.git", "github.com")]
    [InlineData("ssh://git@ghe.example.com:22/acme/forge.git", "ghe.example.com")]
    [InlineData("git://github.com/acme/forge.git", "github.com")]
    [InlineData("/srv/git/forge.git", null)]
    [InlineData("C:\\repos\\forge", null)]
    [InlineData("../forge", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Host_is_extracted_from_remote_addresses(string? url, string? expected) =>
        Assert.Equal(expected, Internals.InvokeStatic<string?>("GhHostParser", "FromRemoteUrl", url));
}
