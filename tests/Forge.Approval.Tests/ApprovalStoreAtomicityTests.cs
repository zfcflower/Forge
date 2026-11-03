using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalStoreAtomicityTests
{
    private const int RewriteCount = 150;
    private const int ReaderCount = 4;
    private const int WriterThreadCount = 8;
    private const int RecordsPerWriterThread = 25;

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

    private static async Task<RewriteOutcome> RewriteWhileReading(ApprovalStore store, bool allowTransientDenial)
    {
        var failures = new ConcurrentBag<string>();
        var successfulReads = 0;
        using var readersReady = new CountdownEvent(ReaderCount);
        using var stop = new ManualResetEventSlim(false);

        var readers = Enumerable.Range(0, ReaderCount).Select(_ => Task.Run(() =>
        {
            readersReady.Signal();
            do
            {
                if (ReadStateFile(store.FilePath, failures, allowTransientDenial))
                {
                    Interlocked.Increment(ref successfulReads);
                }
            }
            while (!stop.IsSet);
        })).ToArray();

        readersReady.Wait();
        for (var version = 1; version <= RewriteCount; version++)
        {
            store.SetApproved(
                ApprovalFixtures.PluginId,
                ApprovalFixtures.Version,
                Hashing.SourceHash($"version {version}"),
                ApprovalFixtures.ApprovedDependencyHash,
                ApprovalFixtures.Sdk);
        }
        stop.Set();
        await Task.WhenAll(readers);

        return new RewriteOutcome(failures.ToArray(), Volatile.Read(ref successfulReads));
    }

    private static bool ReadStateFile(string path, ConcurrentBag<string> failures, bool allowTransientDenial)
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
            if (count != 1)
            {
                failures.Add($"state file holds {count} records, expected 1");
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

    private sealed record RewriteOutcome(IReadOnlyCollection<string> Failures, int SuccessfulReads);
}
