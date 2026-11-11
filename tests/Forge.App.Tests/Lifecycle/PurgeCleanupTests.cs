using Forge.App.Lifecycle;
using Forge.App.Localization;
using Xunit;

namespace Forge.App.Tests.Lifecycle;

/// <summary>
/// The purge cleanup retry (7.5): an id is journaled before its files are deleted and dropped once they are, a failure
/// keeps it for the next start, the startup retry cleans it up then (and never touches a session that exists again), and
/// a journal that cannot be written still lets the cleanup run while saying the retry is lost.
/// </summary>
public sealed class PurgeCleanupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-purge-cleanup-tests", Guid.NewGuid().ToString("n"));
    private readonly List<string> deleted = [];
    private readonly HashSet<string> failing = new(StringComparer.Ordinal);
    private readonly HashSet<string> existing = new(StringComparer.Ordinal);

    public PurgeCleanupTests() => Directory.CreateDirectory(root);

    private string JournalPath => Path.Combine(root, "purge-pending.txt");

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind is not a test failure.
        }
    }

    [Fact]
    public async Task The_journal_keeps_distinct_ids_across_instances_and_disappears_when_empty()
    {
        var journal = new PurgeCleanupJournal(JournalPath);
        Assert.Empty(await journal.ReadAsync(default));

        await journal.AddAsync("s1", default);
        await journal.AddAsync("s2", default);
        await journal.AddAsync("s1", default);

        Assert.Equal(["s1", "s2"], await new PurgeCleanupJournal(JournalPath).ReadAsync(default));
        await journal.RemoveAsync("s1", default);
        await journal.RemoveAsync("absent", default);
        Assert.Equal(["s2"], await journal.ReadAsync(default));
        await journal.RemoveAsync("s2", default);
        Assert.False(File.Exists(JournalPath));
        Assert.False(File.Exists(JournalPath + ".tmp"));
    }

    [Fact]
    public async Task Lines_that_cannot_be_a_session_id_are_ignored_and_never_added()
    {
        // Only what the stores accept (StoragePaths.SafeSegment): anything else would fail every start for good.
        await File.WriteAllLinesAsync(
            JournalPath,
            ["s1", "", "..", "../s2", "a/b", "a\\b", " padded ", "has space", "日本", "trail.", "s1", "a-b_c.d", "s3"]);
        var journal = new PurgeCleanupJournal(JournalPath);

        Assert.Equal(["s1", "a-b_c.d", "s3"], await journal.ReadAsync(default));
        await Assert.ThrowsAsync<ArgumentException>(() => journal.AddAsync("../escape", default));
        await Assert.ThrowsAsync<ArgumentException>(() => journal.AddAsync("..", default));
        await Assert.ThrowsAsync<ArgumentException>(() => journal.AddAsync("has space", default));
    }

    [Fact]
    public async Task An_id_the_journal_refuses_is_still_cleaned_up_and_its_failure_says_it_is_not_retried()
    {
        var cleanup = Cleanup();

        await cleanup.CleanUpAsync("has space", default);
        Assert.Equal(["has space"], deleted);
        Assert.False(File.Exists(JournalPath));

        failing.Add("bad id");
        var failure = await Assert.ThrowsAsync<IOException>(() => cleanup.CleanUpAsync("bad id", default));
        Assert.StartsWith("locked; ", failure.Message, StringComparison.Ordinal);
        Assert.Contains(RemovalStrings.PurgeNotRetried("\u0001").Split('\u0001')[0], failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public async Task Concurrent_writers_lose_no_id()
    {
        var journal = new PurgeCleanupJournal(JournalPath);
        var ids = Enumerable.Range(0, 40).Select(static i => "s" + i).ToArray();

        await Task.WhenAll(ids.Select(id => Task.Run(() => journal.AddAsync(id, default))));

        Assert.Equal(ids.Order(StringComparer.Ordinal), (await journal.ReadAsync(default)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_cleanup_that_succeeds_leaves_nothing_pending()
    {
        var cleanup = Cleanup();

        await cleanup.CleanUpAsync("s1", default);

        Assert.Equal(["s1"], deleted);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public async Task A_cleanup_that_fails_is_reported_kept_and_cleaned_up_by_the_next_start()
    {
        failing.Add("s1");
        var failure = await Assert.ThrowsAsync<IOException>(() => Cleanup().CleanUpAsync("s1", default));
        Assert.Equal("locked", failure.Message);
        Assert.Equal(["s1"], await new PurgeCleanupJournal(JournalPath).ReadAsync(default));

        // The next start: still failing, so it stays and is reported.
        Assert.Equal(["s1: locked"], await Cleanup().RetryPendingAsync(default));
        Assert.Equal(["s1"], await new PurgeCleanupJournal(JournalPath).ReadAsync(default));

        // The start after that: it goes through and leaves the journal.
        failing.Clear();
        Assert.Empty(await Cleanup().RetryPendingAsync(default));
        Assert.Equal(["s1"], deleted);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public async Task The_retry_never_touches_a_session_that_exists_and_keeps_going_past_a_failure()
    {
        var journal = new PurgeCleanupJournal(JournalPath);
        await journal.AddAsync("alive", default);
        await journal.AddAsync("stuck", default);
        await journal.AddAsync("gone", default);
        existing.Add("alive");
        failing.Add("stuck");

        var failures = await Cleanup().RetryPendingAsync(default);

        Assert.Equal(["stuck: locked"], failures);
        Assert.Equal(["gone"], deleted);
        Assert.Equal(["stuck"], await journal.ReadAsync(default));
    }

    [Fact]
    public async Task A_cancelled_retry_keeps_every_id()
    {
        var journal = new PurgeCleanupJournal(JournalPath);
        await journal.AddAsync("s1", default);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Cleanup().RetryPendingAsync(cancelled.Token));

        Assert.Empty(deleted);
        Assert.Equal(["s1"], await journal.ReadAsync(default));
    }

    [Fact]
    public async Task A_journal_that_cannot_be_written_still_cleans_up_and_says_the_retry_is_lost_when_it_fails()
    {
        // A folder where the journal file should be: every write fails.
        Directory.CreateDirectory(JournalPath);
        var cleanup = Cleanup();

        await cleanup.CleanUpAsync("s1", default);
        Assert.Equal(["s1"], deleted);

        failing.Add("s2");
        var failure = await Assert.ThrowsAsync<IOException>(() => cleanup.CleanUpAsync("s2", default));
        var marker = RemovalStrings.PurgeNotRetried("\u0001").Split('\u0001')[0];
        Assert.StartsWith("locked; ", failure.Message, StringComparison.Ordinal);
        Assert.Contains(marker, failure.Message, StringComparison.Ordinal);
    }

    private PurgeCleanup Cleanup() => new(
        new PurgeCleanupJournal(JournalPath),
        (id, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (failing.Contains(id))
            {
                throw new IOException("locked");
            }

            deleted.Add(id);
            return Task.CompletedTask;
        },
        (id, _) => Task.FromResult(existing.Contains(id)));
}
