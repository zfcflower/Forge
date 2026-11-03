using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalConcurrencyTests
{
    [Fact]
    public void Two_processes_run_200_store_updates_each_with_exact_totals_and_no_busy()
    {
        using var directory = new TempApprovalDirectory();
        using (var seed = new ApprovalStore(directory.Path))
        {
            seed.SetApproved("shared.plugin", "1.0.0", "seed", "dep", "10.0.100");
        }

        using var first = ApprovalChildProcess.Start(ApprovalChildProcess.StressMode, directory.Path, "a", 200);
        using var second = ApprovalChildProcess.Start(ApprovalChildProcess.StressMode, directory.Path, "b", 200);
        var firstResult = ApprovalStoreTestSupport.WaitForChild(first, TimeSpan.FromMinutes(3));
        var secondResult = ApprovalStoreTestSupport.WaitForChild(second, TimeSpan.FromMinutes(3));

        Assert.True(firstResult.ExitCode == 0, "first child: " + firstResult.Output);
        Assert.True(secondResult.ExitCode == 0, "second child: " + secondResult.Output);
        Assert.Contains("busy=0", firstResult.Output, StringComparison.Ordinal);
        Assert.Contains("fail=0", firstResult.Output, StringComparison.Ordinal);
        Assert.Contains("busy=0", secondResult.Output, StringComparison.Ordinal);
        Assert.Contains("fail=0", secondResult.Output, StringComparison.Ordinal);

        using var store = new ApprovalStore(directory.Path);
        Assert.Equal(401, store.Count);
        Assert.Equal("hash-199", store.Get("a.plugin.199", "1.0.0")?.SourceHash);
        Assert.Equal("hash-199", store.Get("b.plugin.199", "1.0.0")?.SourceHash);
        Assert.NotNull(store.Get("shared.plugin", "1.0.0"));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Parallel_tasks_on_one_store_lose_no_update_and_match_the_table()
    {
        using var directory = new TempApprovalDirectory();
        using var store = new ApprovalStore(directory.Path);
        const int taskCount = 8;
        const int perTask = 25;
        var errors = new ConcurrentBag<Exception>();
        var workers = Enumerable.Range(0, taskCount).Select(task => Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < perTask; i++)
                {
                    store.Upsert(ApprovalFixtures.ApprovedRecord() with
                    {
                        PluginId = $"{task}.plugin.{i}",
                        Version = "1.0.0",
                        SourceHash = $"hash-{task}-{i}",
                    });
                }
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        })).ToArray();

        try
        {
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            Assert.Fail("all workers finished");
        }

        Assert.Empty(errors);
        Assert.Equal(taskCount * perTask, store.Count);

        using var raw = ApprovalStoreTestSupport.OpenDatabase(directory.Path);
        try
        {
            Assert.Equal(store.Count, long.Parse(ApprovalStoreTestSupport.Scalar(raw, "SELECT COUNT(*) FROM plugin_approval") ?? "-1", System.Globalization.CultureInfo.InvariantCulture));
            using var keys = raw.CreateCommand();
            keys.CommandText = "SELECT plugin_id, version FROM plugin_approval";
            using var reader = keys.ExecuteReader();
            var tableKeys = new HashSet<(string PluginId, string Version)>();
            while (reader.Read())
            {
                tableKeys.Add((reader.GetString(0), reader.GetString(1)));
            }

            var snapshotKeys = new HashSet<(string PluginId, string Version)>(
                store.List().Select(record => (record.PluginId, record.Version)));
            Assert.Equal(tableKeys, snapshotKeys);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        for (var task = 0; task < taskCount; task++)
        {
            for (var i = 0; i < perTask; i++)
            {
                Assert.Equal($"hash-{task}-{i}", store.Get($"{task}.plugin.{i}", "1.0.0")?.SourceHash);
            }
        }
    }

    [Fact]
    public async Task Parallel_stores_on_one_path_keep_every_distinct_row()
    {
        using var directory = new TempApprovalDirectory();
        using var first = new ApprovalStore(directory.Path);
        using var second = new ApprovalStore(directory.Path);
        using var third = new ApprovalStore(directory.Path);
        using var fourth = new ApprovalStore(directory.Path);
        var stores = new[] { first, second, third, fourth };
        const int perStore = 25;
        var errors = new ConcurrentBag<Exception>();
        var workers = stores.Select((store, index) => Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < perStore; i++)
                {
                    store.SetApproved($"s{index}.plugin.{i}", "1.0.0", $"hash-{index}-{i}", "dep", "10.0.100");
                }

                store.SetApproved("shared.plugin", "1.0.0", $"writer-{index}", "dep", "10.0.100");
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        })).ToArray();

        try
        {
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            Assert.Fail("all stores finished");
        }

        Assert.Empty(errors);

        using var check = new ApprovalStore(directory.Path);
        Assert.Equal(stores.Length * perStore + 1, check.Count);
        for (var index = 0; index < stores.Length; index++)
        {
            for (var i = 0; i < perStore; i++)
            {
                Assert.Equal($"hash-{index}-{i}", check.Get($"s{index}.plugin.{i}", "1.0.0")?.SourceHash);
            }
        }

        Assert.NotNull(check.Get("shared.plugin", "1.0.0"));
        Assert.Equal(check.Count, ApprovalStoreTestSupport.TableRowCount(directory.Path, "plugin_approval"));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Parallel_tasks_updating_one_row_on_one_store_all_land()
    {
        using var directory = new TempApprovalDirectory();
        using var store = new ApprovalStore(directory.Path);
        store.SetApproved("shared.plugin", "1.0.0", "seed", "dep", "10.0.100");
        const int taskCount = 4;
        const int perTask = 50;
        var errors = new ConcurrentBag<Exception>();
        var workers = Enumerable.Range(0, taskCount).Select(task => Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < perTask; i++)
                {
                    store.SetApproved("shared.plugin", "1.0.0", $"writer-{task}-{i}", "dep", "10.0.100");
                }
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        })).ToArray();

        try
        {
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            Assert.Fail("all writers finished");
        }

        Assert.Empty(errors);
        var final = store.Get("shared.plugin", "1.0.0");
        Assert.NotNull(final);
        Assert.StartsWith("writer-", final.SourceHash, StringComparison.Ordinal);
        Assert.Equal(1, store.Count);
    }
}
