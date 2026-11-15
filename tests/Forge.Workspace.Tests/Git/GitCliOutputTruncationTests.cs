using Forge.Abstractions;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// The diff honesty contract: when git's output passes the capture limit, the structured interfaces throw
/// <see cref="GitOutputTruncatedException"/> instead of handing back partial data as a complete result, and the diff
/// tool translates that into an error result - never a truncated diff, and never a self-review record.
/// </summary>
public sealed class GitCliOutputTruncationTests
{
    private const int CaptureLimit = 200;

    private static string Big(int lines)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < lines; i++)
        {
            builder.Append("line of text that goes past the capture limit number ").Append(i).Append('\n');
        }

        return builder.ToString();
    }

    [Fact]
    public async Task Diff_text_beyond_the_capture_limit_throws_GitOutputTruncatedException()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var repo = new TestRepo();
        repo.CommitFile("big.txt", Big(lines: 5), "seed");
        repo.Write("big.txt", Big(lines: 10));
        var git = new GitCli { MaxCapturedCharacters = CaptureLimit };

        var exception = await Assert.ThrowsAsync<GitOutputTruncatedException>(
            () => git.GetDiffTextAsync(repo.Root));

        Assert.Contains("capture limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_tool_reports_a_capture_limit_hit_as_an_error_without_recording_a_review()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var repo = new TestRepo();
        repo.CommitFile("big.txt", Big(lines: 5), "seed");
        repo.Write("big.txt", Big(lines: 10));
        var git = new GitCli { MaxCapturedCharacters = CaptureLimit };
        var ledger = new GitReviewLedger();
        var tools = new GitTools(git, new GitToolSettings(), ledger);

        var result = await tools.Diff.InvokeAsync(
            new ToolCall(Guid.NewGuid().ToString("n"), GitTools.DiffName, "{}"),
            new ToolContext("session-1", repo.Root),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("capture limit", result.Content, StringComparison.Ordinal);
        Assert.Contains("stat_only", result.Content, StringComparison.Ordinal);

        // A diff the caller never saw in full must not count as a self-review for the PR gate.
        Assert.Null(ledger.LastReviewed("session-1", repo.Root));
    }
}
