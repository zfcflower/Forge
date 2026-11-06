using Xunit;

namespace Forge.Workspace.Tests.Git;

public sealed class GitReviewLedgerTests
{
    private static string SeedRepo(RemoteSandbox sandbox)
    {
        var repo = sandbox.Clone("work");
        sandbox.CommitFile(repo, "file.txt", "content\n", "seed");
        return repo;
    }

    [Fact]
    public void Unknown_session_and_path_reads_null()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedRepo(sandbox);

        Assert.Null(new GitReviewLedger().LastReviewed("no-session", repo));
    }

    [Fact]
    public void Recorded_head_is_read_back_for_the_same_session_and_path()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedRepo(sandbox);
        var ledger = new GitReviewLedger();

        ledger.RecordReviewed("session-1", repo, "abc123");

        Assert.Equal("abc123", ledger.LastReviewed("session-1", repo));
    }

    [Fact]
    public void Different_sessions_are_isolated()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedRepo(sandbox);
        var ledger = new GitReviewLedger();

        ledger.RecordReviewed("session-a", repo, "aaa");
        ledger.RecordReviewed("session-b", repo, "bbb");

        Assert.Equal("aaa", ledger.LastReviewed("session-a", repo));
        Assert.Equal("bbb", ledger.LastReviewed("session-b", repo));
    }

    [Fact]
    public void Trailing_separator_and_case_variants_map_to_the_same_entry()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedRepo(sandbox);
        var ledger = new GitReviewLedger();

        ledger.RecordReviewed("session-1", repo, "abc123");

        Assert.Equal("abc123", ledger.LastReviewed("session-1", repo + Path.DirectorySeparatorChar));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("abc123", ledger.LastReviewed("session-1", repo.ToUpperInvariant()));
            Assert.Equal(
                "abc123",
                ledger.LastReviewed("session-1", repo.ToUpperInvariant() + Path.DirectorySeparatorChar));
        }
    }

    [Fact]
    public void Concurrent_writers_do_not_throw_and_leave_a_written_value()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var sandbox = new RemoteSandbox();
        var repo = SeedRepo(sandbox);
        var ledger = new GitReviewLedger();
        var commits = Enumerable.Range(0, 64).Select(static i => "commit-" + i).ToList();

        Parallel.For(0, commits.Count, i => ledger.RecordReviewed("session-1", repo, commits[i]));

        var last = ledger.LastReviewed("session-1", repo);
        Assert.NotNull(last);
        Assert.Contains(last, commits, StringComparer.Ordinal);
    }
}
