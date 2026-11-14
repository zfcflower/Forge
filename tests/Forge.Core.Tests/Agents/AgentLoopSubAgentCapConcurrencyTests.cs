using System.Reflection;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// The cap an <see cref="AgentLoop"/> reports and the limit its slot pool enforces move together: two threads setting
/// different caps at once must never leave them disagreeing, because the readout would then lie about the live cap.
/// The pool is private to the loop, so its real limit is read the only way it shows: by taking permits until none is free.
/// </summary>
public sealed class AgentLoopSubAgentCapConcurrencyTests
{
    private const int Iterations = 60;
    private const int Threads = 8;
    private const int SettersPerThread = 40;

    [Fact]
    public async Task Concurrent_setters_leave_the_reported_cap_equal_to_the_pools_limit()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2.5);
        var rounds = 0;
        for (var iteration = 0; iteration < Iterations && DateTime.UtcNow < deadline; iteration++)
        {
            var host = AgentTestHost.Create(
                new FakeProvider((_, _) => Turn.Say("unused")), options: new AgentOptions { MaxParallelSubAgents = 4 });
            var loop = LoopOf(host);
            using var start = new ManualResetEventSlim(false);

            var workers = Enumerable.Range(0, Threads).Select(seed => Task.Factory.StartNew(
                () =>
                {
                    var random = new Random(seed * 7919 + iteration);
                    start.Wait();
                    for (var step = 0; step < SettersPerThread; step++)
                    {
                        loop.SetMaxParallelSubAgents(random.Next(1, 17));
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)).ToArray();

            start.Set();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));

            var reported = loop.MaxParallelSubAgents;
            Assert.InRange(reported, 1, 16);
            Assert.Equal(reported, PoolLimitOf(loop));
            Assert.Equal(reported, host.MaxParallelSubAgents);
            rounds++;
        }

        Assert.True(rounds >= 5, $"only {rounds} rounds ran inside the time budget");
    }

    [Fact]
    public void Out_of_range_caps_set_concurrently_still_agree_with_the_pool()
    {
        var host = AgentTestHost.Create(
            new FakeProvider((_, _) => Turn.Say("unused")), options: new AgentOptions { MaxParallelSubAgents = 4 });
        var loop = LoopOf(host);

        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = Threads }, index =>
            loop.SetMaxParallelSubAgents(index % 3 == 0 ? -index : index % 20));

        // Zero and negative caps are lifted to a single permit, so the floor holds under contention too.
        Assert.True(loop.MaxParallelSubAgents >= 1);
        Assert.Equal(loop.MaxParallelSubAgents, PoolLimitOf(loop));
    }

    private static AgentLoop LoopOf(AgentHost host)
    {
        var field = typeof(AgentHost).GetField("loop", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<AgentLoop>(field.GetValue(host));
    }

    /// <summary>Takes permits until the pool has none free, which is exactly its limit when nothing else holds one.</summary>
    private static int PoolLimitOf(AgentLoop loop)
    {
        var field = typeof(AgentLoop).GetField("slots", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var pool = Assert.IsType<AgentSlotPool>(field.GetValue(loop));
        var held = new List<AgentSlot>();
        try
        {
            while (pool.TryAcquire() is { } slot)
            {
                held.Add(slot);
                Assert.True(held.Count <= 64, "the pool handed out more permits than any cap allows");
            }

            return held.Count;
        }
        finally
        {
            foreach (var slot in held)
            {
                slot.Dispose();
            }
        }
    }
}
