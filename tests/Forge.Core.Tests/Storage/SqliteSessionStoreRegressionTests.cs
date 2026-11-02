using System.Collections.Concurrent;
using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;
using static Forge.Core.Tests.Storage.StorageTestHelpers;

namespace Forge.Core.Tests.Storage;

public class SqliteSessionStoreRegressionTests
{
    [Fact]
    public async Task Random_cancellation_leaves_no_sequence_gap_and_never_hides_a_commit()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync("D:/ws", "t", default);

        const int attempts = 400;
        var random = new Random(20240501);
        var plan = Enumerable.Range(0, attempts).Select(_ => random.Next(0, 4)).ToArray();
        var returned = new ConcurrentDictionary<long, int>();

        await Task.WhenAll(Enumerable.Range(0, attempts).Select(index => Task.Run(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            if (plan[index] == 0)
            {
                cancellation.Cancel();
            }
            else
            {
                cancellation.CancelAfter(plan[index]);
            }

            try
            {
                var seq = await store.AppendAsync(session.Id, Text($"m{index}"), null, cancellation.Token);
                Assert.True(returned.TryAdd(seq, index));
            }
            catch (OperationCanceledException)
            {
            }
        })));

        var stored = await CollectAsync(store.ReadAsync(session.Id, 1, null, default));
        Assert.Equal(Enumerable.Range(1, stored.Count).Select(i => (long)i), stored.Select(m => m.Seq));
        Assert.Equal(stored.Count, await store.LastSeqAsync(session.Id, default));
        Assert.True(stored.Count >= returned.Count);
        Assert.All(returned, pair =>
        {
            var message = stored.Single(m => m.Seq == pair.Key);
            Assert.Equal(new ContentBlock[] { new TextBlock($"m{pair.Value}") }, message.Message.Content);
        });
    }

    [Fact]
    public async Task Search_treats_fts_operator_syntax_as_literal_terms()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync("D:/ws", "t", default);
        await store.AppendAsync(session.Id, Text("alpha near hello or world"), null, default);
        await store.AppendAsync(session.Id, Text("unrelated text"), null, default);

        var operators = await store.SearchAsync(session.Id, "NEAR(hello) OR * \"", 10, default);
        Assert.Equal(1, Assert.Single(operators).Seq);

        Assert.Empty(await store.SearchAsync(session.Id, "hello\" OR \"unrelated", 10, default));
        Assert.Empty(await store.SearchAsync(session.Id, "* \" ( ) : ^ -", 10, default));
        Assert.Empty(await store.SearchAsync(session.Id, "NEAR(zebra) OR * \"", 10, default));
    }
}
