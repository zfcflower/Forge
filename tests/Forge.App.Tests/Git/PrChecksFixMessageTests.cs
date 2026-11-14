using System.Globalization;
using Forge.App.Git;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins <see cref="PrChecksFixMessage"/>, the English message the checks panel hands to the agent: the <c>pr_checks</c>
/// digest first (<c>Pull request #N</c>, <c>Verdict:</c>, every check with its run and job ids), then one heading plus the
/// tail of the log per failed job, then the one instruction (fix the cause, push to the current branch, do not push to the
/// base, do not merge, do not re-run without a change). The bounds are the reason this file exists: a log keeps exactly
/// <c>tailLines</c> lines and at most <see cref="PrChecksFixMessage.MaxJobLogCharacters"/> characters of its end, the whole
/// log section spends at most <see cref="PrChecksFixMessage.MaxTotalLogCharacters"/> and names what it left out, an
/// unreadable log is reported without dropping the readable ones, and a log the platform already cut says so.
/// </summary>
public sealed class PrChecksFixMessageTests
{
    private const int Number = 12;
    private const string PullRequestUrl = "https://example.test/acme/forge/pull/12";
    private const string CaptureNote = "(the platform cut this log at its capture limit; the tail is shown)";

    private static readonly DateTimeOffset ObservedAt = new(2026, 11, 6, 12, 30, 45, TimeSpan.Zero);

    // ------------------------------------------------------------ shape of the message

    [Fact]
    public void The_message_is_the_digest_then_the_logs_then_the_instruction()
    {
        var build = Check("build", PrCheckBucket.Fail, run: 11, job: 22);
        var lint = Check("lint", PrCheckBucket.Pass, run: 11, job: 33);

        var message = PrChecksFixMessage.Build(
            Number,
            PullRequestUrl,
            Observation(build, lint),
            [new PrChecksJobLog(build, "npm ERR! the build broke", false, null)],
            40);

        Assert.StartsWith("Pull request #12 " + PullRequestUrl + "\n", message, StringComparison.Ordinal);
        Assert.Contains("Verdict: CHECKS FAILED.", message, StringComparison.Ordinal);
        Assert.Contains("== Failed job logs ==", message, StringComparison.Ordinal);
        Assert.Contains("### build (CI) run_id=11 job_id=22", message, StringComparison.Ordinal);
        Assert.Contains("npm ERR! the build broke", message, StringComparison.Ordinal);
        Assert.Contains("== What to do ==", message, StringComparison.Ordinal);
        Assert.Contains("Do not push to the base branch, do not merge the pull request", message, StringComparison.Ordinal);
        Assert.Contains("push it to its upstream", message, StringComparison.Ordinal);

        // The log of a passing check is not part of the message: only the failing ones are handed over.
        Assert.DoesNotContain("### lint", message, StringComparison.Ordinal);
        Assert.DoesNotContain(CaptureNote, message, StringComparison.Ordinal);

        Assert.True(
            Position(message, "Verdict:") < Position(message, "== Failed job logs =="),
            "the verdict belongs to the digest, above the logs");
        Assert.True(
            Position(message, "== Failed job logs ==") < Position(message, "### build (CI)"),
            "the failed logs come after their section heading");
        Assert.True(
            Position(message, "npm ERR! the build broke") < Position(message, "== What to do =="),
            "the instruction closes the message");
    }

    [Fact]
    public void A_failing_check_without_a_log_entry_stays_in_the_digest_and_gets_no_section()
    {
        var build = Check("build", PrCheckBucket.Fail, run: 11, job: 22);
        var lint = Check("lint", PrCheckBucket.Fail, run: 11, job: 33);

        var message = PrChecksFixMessage.Build(
            Number,
            null,
            Observation(build, lint),
            [new PrChecksJobLog(build, "build log", false, null)],
            40);

        Assert.Contains("- [FAIL] build", message, StringComparison.Ordinal);
        Assert.Contains("- [FAIL] lint", message, StringComparison.Ordinal);
        Assert.Contains("job_id=33", message, StringComparison.Ordinal);
        Assert.DoesNotContain("### lint", message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ the bounds of one log

    [Fact]
    public void The_log_keeps_exactly_the_last_lines_the_setting_asks_for()
    {
        var check = Check("build", PrCheckBucket.Fail, run: 11, job: 22);
        var log = string.Join('\n', Enumerable.Range(1, 500).Select(line => "line " + line.ToString(CultureInfo.InvariantCulture)));

        var message = PrChecksFixMessage.Build(
            Number,
            null,
            Observation(check),
            [new PrChecksJobLog(check, log, false, null)],
            3);

        Assert.Contains("line 498\nline 499\nline 500\n", message, StringComparison.Ordinal);
        Assert.DoesNotContain("line 497", message, StringComparison.Ordinal);
    }

    [Fact]
    public void One_huge_line_keeps_its_tail_within_the_character_cap()
    {
        var check = Check("build", PrCheckBucket.Fail, run: 11, job: 22);
        var log = "HEAD-OF-THE-LOG" + new string('x', 20_000) + "THE-FAILURE";

        var message = PrChecksFixMessage.Build(
            Number,
            null,
            Observation(check),
            [new PrChecksJobLog(check, log, false, null)],
            40);

        Assert.Contains("THE-FAILURE", message, StringComparison.Ordinal);
        Assert.DoesNotContain("HEAD-OF-THE-LOG", message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', PrChecksFixMessage.MaxJobLogCharacters + 1), message, StringComparison.Ordinal);
    }

    [Fact]
    public void Logs_beyond_the_total_cap_are_counted_and_left_out()
    {
        var check = Check("build", PrCheckBucket.Fail, run: 11, job: 22);
        var big = new string('y', PrChecksFixMessage.MaxJobLogCharacters - 1);
        var logs = Enumerable.Range(0, 20).Select(_ => new PrChecksJobLog(check, big, false, null)).ToList();

        var message = PrChecksFixMessage.Build(Number, null, Observation(check), logs, 40);

        // Five logs of 3999 characters fit into 20000; the other fifteen are named instead of pasted.
        Assert.Contains("15 more failed job log(s) were left out", message, StringComparison.Ordinal);
        Assert.Equal(5, message.Split(big, StringSplitOptions.None).Length - 1);
        var inputCharacters = logs.Sum(log => log.Tail.Length);
        Assert.True(
            message.Length < inputCharacters / 2,
            $"the message ({message.Length} chars) must stay far below its {inputCharacters} chars of logs");
        Assert.Contains("== What to do ==", message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ logs that did not come back, or came back cut

    [Fact]
    public void An_unreadable_log_is_reported_and_does_not_stop_the_others()
    {
        var build = Check("build", PrCheckBucket.Fail, run: 11, job: 22);
        var lint = Check("lint", PrCheckBucket.Fail, run: 11, job: 33);

        var message = PrChecksFixMessage.Build(
            Number,
            null,
            Observation(build, lint),
            [
                new PrChecksJobLog(build, string.Empty, false, "gh: authentication required"),
                new PrChecksJobLog(lint, "lint: 1 error", false, null),
            ],
            40);

        Assert.Contains("### build\n", message, StringComparison.Ordinal);
        Assert.Contains("The log could not be read: gh: authentication required", message, StringComparison.Ordinal);
        Assert.Contains("### lint (CI) run_id=11 job_id=33", message, StringComparison.Ordinal);
        Assert.Contains("lint: 1 error", message, StringComparison.Ordinal);
        Assert.True(
            Position(message, "The log could not be read:") < Position(message, "### lint (CI)"),
            "an unreadable log must not end the section");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_log_the_platform_cut_carries_the_capture_note(bool truncated)
    {
        var check = Check("build", PrCheckBucket.Fail, run: 11, job: 22);

        var message = PrChecksFixMessage.Build(
            Number,
            null,
            Observation(check),
            [new PrChecksJobLog(check, "the tail of a very long log", truncated, null)],
            40);

        Assert.Equal(truncated, message.Contains(CaptureNote, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ argument checks

    [Fact]
    public void Build_rejects_a_missing_observation_missing_logs_and_a_tail_below_one()
    {
        var check = Check("build", PrCheckBucket.Fail, run: 11, job: 22);
        var observation = Observation(check);
        IReadOnlyList<PrChecksJobLog> logs = [new PrChecksJobLog(check, "tail", false, null)];

        Assert.Throws<ArgumentNullException>(() => PrChecksFixMessage.Build(Number, null, null!, logs, 40));
        Assert.Throws<ArgumentNullException>(() => PrChecksFixMessage.Build(Number, null, observation, null!, 40));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrChecksFixMessage.Build(Number, null, observation, logs, 0));
    }

    // ------------------------------------------------------------ builders

    private static PrChecksObservation Observation(params PrCheck[] checks) =>
        new(
            new PrChecksSnapshot(checks),
            new PrMergeState(
                PrState.Open,
                false,
                PrMergeStateStatus.Clean,
                PrReviewDecision.None,
                PrMergeable.Mergeable,
                "CLEAN",
                string.Empty,
                "MERGEABLE"),
            ObservedAt);

    private static PrCheck Check(string name, PrCheckBucket bucket, long? run = null, long? job = null) =>
        new(name, "CI", string.Empty, bucket, string.Empty, null, null, null, run, job);

    /// <summary>Where <paramref name="value"/> appears in <paramref name="text"/>; a test asserts the order of the parts.</summary>
    private static int Position(string text, string value) => text.IndexOf(value, StringComparison.Ordinal);
}
