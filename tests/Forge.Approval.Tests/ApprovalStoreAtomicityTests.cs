using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalStoreAtomicityTests
{
    private const int RewriteCount = 150;
    private const int ReaderCount = 4;
    private const int WriterThreadCount = 8;
    private const int RecordsPerWriterThread = 25;
    private const int LargeRecordCount = 200;
    private const int ConcurrentMutationCount = 16;

    [Fact]
    public async Task Readers_never_observe_a_missing_or_partial_state_file_during_rewrites()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path, "version 0");

        var outcome = await RewriteWhileReading(store, allowTransientDenial: true);

        Assert.Empty(outcome.Failures);
        Assert.True(outcome.SuccessfulReads >= ReaderCount);
        Assert.Equal(Hashing.SourceHash($"version {RewriteCount}"), store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.SourceHash);
    }

    [Fact]
    public async Task Readers_never_get_access_denied_while_the_state_file_is_replaced()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path, "version 0");

        var outcome = await RewriteWhileReading(store, allowTransientDenial: false);

        Assert.Empty(outcome.Failures);
    }

    [Fact]
    public async Task Readers_and_writer_survive_a_large_state_file()
    {
        using var directory = new TempApprovalDirectory();
        var store = LargeStateStore(directory.Path);
        Assert.True(new FileInfo(store.FilePath).Length >= 80_000, "the state file should be a realistic size");

        var outcome = await RewriteWhileReading(
            store,
            allowTransientDenial: false,
            expectedRecords: LargeRecordCount,
            pluginId: LargePluginId(0),
            version: LargeVersion,
            continueOnWriteFailure: true);

        Assert.Empty(outcome.Failures);
        Assert.Equal(0, outcome.WriteFailures);
        Assert.Equal(RewriteCount, outcome.SuccessfulWrites);
        Assert.True(outcome.SuccessfulReads >= ReaderCount);
        Assert.Equal(LargeRecordCount, store.Count);
        Assert.Equal(LargeRecordCount, new ApprovalStore(directory.Path).Count);
        Assert.Equal(Hashing.SourceHash($"version {RewriteCount}"), store.Get(LargePluginId(0), LargeVersion)?.SourceHash);
    }

    [Fact]
    public async Task Concurrent_writers_all_land_in_memory_and_on_disk()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);

        var writers = Enumerable.Range(0, WriterThreadCount).Select(plugin => Task.Run(() =>
        {
            for (var version = 0; version < RecordsPerWriterThread; version++)
            {
                store.Upsert(ApprovalFixtures.ApprovedRecord() with
                {
                    PluginId = $"plugin.{plugin}",
                    Version = $"1.{version}.0",
                });
            }
        })).ToArray();
        await Task.WhenAll(writers);

        const int expected = WriterThreadCount * RecordsPerWriterThread;
        Assert.Equal(expected, store.Count);
        Assert.Equal(expected, new ApprovalStore(directory.Path).Count);
    }

    [Fact]
    public async Task Concurrent_mutations_on_one_store_lose_no_update_and_match_the_file()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);

        var mutations = Enumerable.Range(0, ConcurrentMutationCount).Select(index => Task.Run(() =>
            store.SetApproved(
                $"plugin.{index:D2}",
                "1.0.0",
                Hashing.SourceHash($"parallel version {index}"),
                ApprovalFixtures.ApprovedDependencyHash,
                ApprovalFixtures.Sdk))).ToArray();
        await Task.WhenAll(mutations);

        Assert.Equal(ConcurrentMutationCount, store.Count);
        var mutated = mutations.Select(mutation => mutation.Result?.PluginId).ToArray();
        Assert.DoesNotContain(mutated, id => id is null);
        Assert.Equal(ConcurrentMutationCount, mutated.Distinct().Count());

        var inMemory = Describe(store.List());
        var onDisk = Describe(new ApprovalStore(directory.Path).List());
        Assert.Equal(inMemory, onDisk);
        Assert.Equal(ConcurrentMutationCount, onDisk.Length);
        for (var index = 0; index < ConcurrentMutationCount; index++)
        {
            Assert.Equal(
                Hashing.SourceHash($"parallel version {index}"),
                store.Get($"plugin.{index:D2}", "1.0.0")?.SourceHash);
            Assert.Equal(
                Hashing.SourceHash($"parallel version {index}"),
                new ApprovalStore(directory.Path).Get($"plugin.{index:D2}", "1.0.0")?.SourceHash);
        }
    }

    [Fact]
    public void Two_instances_on_one_file_are_last_writer_wins()
    {
        using var directory = new TempApprovalDirectory();
        var first = new ApprovalStore(directory.Path);
        var second = new ApprovalStore(directory.Path);

        first.Upsert(ApprovalFixtures.ApprovedRecord() with { PluginId = "plugin.first" });
        second.Upsert(ApprovalFixtures.ApprovedRecord() with { PluginId = "plugin.second" });

        Assert.Equal(1, first.Count);
        Assert.Equal(1, second.Count);
        Assert.NotNull(first.Get("plugin.first"));
        Assert.Null(first.Get("plugin.second"));

        var restarted = new ApprovalStore(directory.Path);
        Assert.Equal(1, restarted.Count);
        Assert.NotNull(restarted.Get("plugin.second"));
        Assert.Null(restarted.Get("plugin.first"));

        first.Reload();
        Assert.Equal(1, first.Count);
        Assert.Null(first.Get("plugin.first"));
    }

    [Fact]
    public void Successful_mutations_leave_only_the_state_file_in_the_directory()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path);
        store.Disable(ApprovalFixtures.PluginId, ApprovalFixtures.Version);
        store.Enable(ApprovalFixtures.PluginId, ApprovalFixtures.Version);
        store.Revoke(ApprovalFixtures.PluginId, ApprovalFixtures.Version);

        Assert.Equal([store.FilePath], Directory.GetFiles(directory.Path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void Failed_write_removes_its_temporary_file_and_rethrows()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        Directory.CreateDirectory(store.FilePath);

        var exception = Record.Exception(() => store.SetApproved(
            ApprovalFixtures.PluginId,
            ApprovalFixtures.Version,
            Hashing.SourceHash(ApprovalFixtures.Source),
            ApprovalFixtures.ApprovedDependencyHash,
            ApprovalFixtures.Sdk));

        Assert.True(exception is IOException or UnauthorizedAccessException, exception?.GetType().FullName);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void Failed_write_does_not_leave_the_approval_active_in_memory()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        Directory.CreateDirectory(store.FilePath);
        var candidate = ApprovalFixtures.Candidate();

        _ = Record.Exception(() => store.SetApproved(
            ApprovalFixtures.PluginId,
            ApprovalFixtures.Version,
            candidate.SourceHash,
            ApprovalFixtures.ApprovedDependencyHash,
            ApprovalFixtures.Sdk));

        Assert.False(ApprovalEngine.Evaluate(candidate, store).IsAllowed);
    }

    [Fact]
    public void A_non_race_write_failure_is_not_retried()
    {
        using var directory = new TempApprovalDirectory();
        var store = new ApprovalStore(directory.Path);
        Directory.CreateDirectory(store.FilePath);
        var budget = TimeSpan.FromSeconds(5);

        var stopwatch = Stopwatch.StartNew();
        var exception = Record.Exception(() => store.SetApproved(
            ApprovalFixtures.PluginId,
            ApprovalFixtures.Version,
            Hashing.SourceHash(ApprovalFixtures.Source),
            ApprovalFixtures.ApprovedDependencyHash,
            ApprovalFixtures.Sdk));
        stopwatch.Stop();

        Assert.True(exception is IOException or UnauthorizedAccessException, exception?.GetType().FullName);
        Assert.True(stopwatch.Elapsed < budget / 10, $"the failure took {stopwatch.ElapsedMilliseconds} ms, so it was retried");
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Cancelling_before_the_write_leaves_the_file_and_memory_untouched()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path, "version 0");
        var before = File.ReadAllBytes(store.FilePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Record.Exception(() => store.SetApproved(
            ApprovalFixtures.PluginId,
            ApprovalFixtures.Version,
            Hashing.SourceHash("version 1"),
            ApprovalFixtures.ApprovedDependencyHash,
            ApprovalFixtures.Sdk,
            cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(before, File.ReadAllBytes(store.FilePath));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        Assert.Equal(Hashing.SourceHash("version 0"), store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.SourceHash);
    }

    [Fact]
    public void Write_fails_loudly_when_a_reader_holds_the_state_file_beyond_the_budget()
    {
        using var directory = new TempApprovalDirectory();
        var budget = TimeSpan.FromMilliseconds(150);
        var store = new ApprovalStore(directory.Path, budget);
        store.SetApproved(
            ApprovalFixtures.PluginId,
            ApprovalFixtures.Version,
            Hashing.SourceHash("version 0"),
            ApprovalFixtures.ApprovedDependencyHash,
            ApprovalFixtures.Sdk);
        var before = File.ReadAllBytes(store.FilePath);
        var recordsBefore = Describe(store.List());

        byte[] after;
        Exception? exception;
        var stopwatch = Stopwatch.StartNew();
        using (var held = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            exception = Record.Exception(() => store.SetApproved(
                ApprovalFixtures.PluginId,
                ApprovalFixtures.Version,
                Hashing.SourceHash("version 1"),
                ApprovalFixtures.ApprovedDependencyHash,
                ApprovalFixtures.Sdk));
            stopwatch.Stop();
        }

        after = File.ReadAllBytes(store.FilePath);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"the write returned after {stopwatch.ElapsedMilliseconds} ms");
        if (!OperatingSystem.IsWindows())
        {
            Assert.Null(exception);
            Assert.Equal(Hashing.SourceHash("version 1"), store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.SourceHash);
            return;
        }

        Assert.IsType<UnauthorizedAccessException>(exception);
        Assert.True(stopwatch.Elapsed >= budget, $"the write gave up after {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(recordsBefore, Describe(store.List()));
        Assert.Equal(before, after);
        Assert.Single(JsonDocument.Parse(after).RootElement.EnumerateArray());
        Assert.Equal(Hashing.SourceHash("version 0"), store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.SourceHash);
    }

    [Fact]
    public void Cancelling_the_replace_wait_leaves_the_file_and_memory_untouched()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path, "version 0");
        var before = File.ReadAllBytes(store.FilePath);
        var recordsBefore = Describe(store.List());
        using var cancellation = new CancellationTokenSource();

        Exception? exception;
        var stopwatch = Stopwatch.StartNew();
        using (var held = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
            exception = Record.Exception(() => store.SetApproved(
                ApprovalFixtures.PluginId,
                ApprovalFixtures.Version,
                Hashing.SourceHash("version 1"),
                ApprovalFixtures.ApprovedDependencyHash,
                ApprovalFixtures.Sdk,
                cancellation.Token));
            stopwatch.Stop();
        }

        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Null(exception);
            Assert.Equal(Hashing.SourceHash("version 1"), store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.SourceHash);
            return;
        }

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"the wait ended after {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(recordsBefore, Describe(store.List()));
        Assert.Equal(before, File.ReadAllBytes(store.FilePath));
        Assert.Equal(Hashing.SourceHash("version 0"), store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.SourceHash);
    }

    [Fact]
    public void Reload_fails_within_its_bounded_read_retries_when_the_file_cannot_be_opened()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path);

        Exception? exception;
        var stopwatch = Stopwatch.StartNew();
        using (var held = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            exception = Record.Exception(store.Reload);
            stopwatch.Stop();
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.Null(exception);
            Assert.Equal(1, store.Count);
            return;
        }

        Assert.NotNull(exception);
        Assert.True(exception is IOException or UnauthorizedAccessException, exception.GetType().FullName);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"the read gave up after {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Reload_on_a_missing_state_file_keeps_the_empty_store_contract()
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path);
        File.Delete(store.FilePath);

        store.Reload();

        Assert.Equal(0, store.Count);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(2500)]
    public void Write_survives_a_reader_that_holds_the_state_file_for_a_while(int holdMilliseconds)
    {
        using var directory = new TempApprovalDirectory();
        var store = ApprovalFixtures.StoreWithApproval(directory.Path, "version 0");
        using var holding = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using var held = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            holding.Set();
            Thread.Sleep(holdMilliseconds);
        });
        holder.Start();
        holding.Wait();

        var stopwatch = Stopwatch.StartNew();
        store.SetApproved(
            ApprovalFixtures.PluginId,
            ApprovalFixtures.Version,
            Hashing.SourceHash("version 1"),
            ApprovalFixtures.ApprovedDependencyHash,
            ApprovalFixtures.Sdk);
        stopwatch.Stop();
        holder.Join();

        Assert.Equal(Hashing.SourceHash("version 1"), store.Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.SourceHash);
        Assert.Equal(
            Hashing.SourceHash("version 1"),
            new ApprovalStore(directory.Path).Get(ApprovalFixtures.PluginId, ApprovalFixtures.Version)?.SourceHash);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(
                stopwatch.Elapsed >= TimeSpan.FromMilliseconds(holdMilliseconds - 100),
                $"the write returned after {stopwatch.ElapsedMilliseconds} ms");
        }

        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    private const string LargeVersion = "1.0.0";

    private static string LargePluginId(int index) => $"plugin.{index:D3}";

    private static ApprovalStore LargeStateStore(string directory)
    {
        var store = new ApprovalStore(directory);
        for (var index = 0; index < LargeRecordCount; index++)
        {
            store.Upsert(ApprovalFixtures.ApprovedRecord() with
            {
                PluginId = LargePluginId(index),
                Version = LargeVersion,
            });
        }

        return store;
    }

    private static string[] Describe(IReadOnlyList<PluginRecord> records) =>
        [.. records.Select(record => $"{record.PluginId}|{record.Version}|{record.SourceHash}|{record.ApprovedHash}|{record.Status}|{record.UpdatedAt:O}")];

    private static async Task<RewriteOutcome> RewriteWhileReading(
        ApprovalStore store,
        bool allowTransientDenial,
        int expectedRecords = 1,
        string pluginId = ApprovalFixtures.PluginId,
        string version = ApprovalFixtures.Version,
        bool continueOnWriteFailure = false)
    {
        var failures = new ConcurrentBag<string>();
        var successfulReads = 0;
        var successfulWrites = 0;
        var writeFailures = 0;
        using var readersReady = new CountdownEvent(ReaderCount);
        using var stop = new ManualResetEventSlim(false);

        var readers = Enumerable.Range(0, ReaderCount).Select(_ => Task.Run(() =>
        {
            readersReady.Signal();
            do
            {
                if (ReadStateFile(store.FilePath, failures, allowTransientDenial, expectedRecords))
                {
                    Interlocked.Increment(ref successfulReads);
                }
            }
            while (!stop.IsSet);
        })).ToArray();

        readersReady.Wait();
        for (var rewrite = 1; rewrite <= RewriteCount; rewrite++)
        {
            try
            {
                store.SetApproved(
                    pluginId,
                    version,
                    Hashing.SourceHash($"version {rewrite}"),
                    ApprovalFixtures.ApprovedDependencyHash,
                    ApprovalFixtures.Sdk);
                successfulWrites++;
            }
            catch (Exception exception) when (continueOnWriteFailure)
            {
                Interlocked.Increment(ref writeFailures);
                failures.Add($"write {rewrite} failed: {exception.GetType().Name}: {exception.Message}");
            }
        }
        stop.Set();
        await Task.WhenAll(readers);

        return new RewriteOutcome(failures.ToArray(), Volatile.Read(ref successfulReads), successfulWrites, Volatile.Read(ref writeFailures));
    }

    private static bool ReadStateFile(string path, ConcurrentBag<string> failures, bool allowTransientDenial, int expectedRecords)
    {
        byte[] bytes;
        try
        {
            bytes = allowTransientDenial ? ReadBytes(path) : ReadBytesAcrossReplace(path);
        }
        catch (UnauthorizedAccessException) when (allowTransientDenial)
        {
            return false;
        }
        catch (IOException exception)
        {
            failures.Add($"state file unreadable: {exception.Message}");
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var count = document.RootElement.GetArrayLength();
            if (count != expectedRecords)
            {
                failures.Add($"state file holds {count} records, expected {expectedRecords}");
            }
        }
        catch (JsonException exception)
        {
            failures.Add($"state file is partial: {exception.Message}");
            return false;
        }

        return true;
    }

    private static byte[] ReadBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Retries only the Windows replace race: <c>File.Move(overwrite)</c> leaves the old state file delete-pending while
    /// a reader still holds it, and a new open is refused with access denied although the file name stays listed.
    /// Any other failure, or the final denial, propagates unchanged.
    /// </summary>
    private static byte[] ReadBytesAcrossReplace(string path)
    {
        const int attempts = 5;
        const int retryDelayMilliseconds = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return ReadBytes(path);
            }
            catch (UnauthorizedAccessException) when (attempt < attempts && IsReplaceRaceDenial(path))
            {
                Thread.Sleep(retryDelayMilliseconds);
            }
        }
    }

    private static bool IsReplaceRaceDenial(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var directory = Path.GetDirectoryName(path);
        return directory is not null && Directory.EnumerateFiles(directory, Path.GetFileName(path)).Any();
    }

    private sealed record RewriteOutcome(
        IReadOnlyCollection<string> Failures,
        int SuccessfulReads,
        int SuccessfulWrites,
        int WriteFailures);
}
