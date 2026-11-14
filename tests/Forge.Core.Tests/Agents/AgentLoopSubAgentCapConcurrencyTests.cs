using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// The public contract of <see cref="AgentHost.SetMaxParallelSubAgents"/>: it reports whether the cap moved, floors the cap at
/// one, and the readout is the cap in force. The loop keeps no copy of the cap (the pool holds the only one), so the readout
/// cannot disagree with what is enforced; what is left to pin is that concurrent setters leave one of the values that were set.
/// </summary>
public sealed class AgentLoopSubAgentCapConcurrencyTests
{
    private const int Threads = 8;

    private static AgentHost NewHost(int cap = 4) =>
        AgentTestHost.Create(new FakeProvider((_, _) => Turn.Say("unused")), options: new AgentOptions { MaxParallelSubAgents = cap });

    [Fact]
    public void Setting_the_cap_reports_true_only_when_it_moved()
    {
        var host = NewHost(4);
        Assert.Equal(4, host.MaxParallelSubAgents);

        Assert.False(host.SetMaxParallelSubAgents(4));
        Assert.True(host.SetMaxParallelSubAgents(7));
        Assert.Equal(7, host.MaxParallelSubAgents);
        Assert.False(host.SetMaxParallelSubAgents(7));
        Assert.True(host.SetMaxParallelSubAgents(2));
        Assert.Equal(2, host.MaxParallelSubAgents);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-9)]
    public void A_cap_below_one_is_floored_to_one(int requested)
    {
        var host = NewHost(4);

        Assert.True(host.SetMaxParallelSubAgents(requested));
        Assert.Equal(1, host.MaxParallelSubAgents);

        // The floor is the cap now, so asking for another value that floors the same way is not a move.
        Assert.False(host.SetMaxParallelSubAgents(requested - 1));
        Assert.False(host.SetMaxParallelSubAgents(1));
        Assert.Equal(1, host.MaxParallelSubAgents);
    }

    [Fact]
    public void A_host_built_with_a_non_positive_cap_starts_at_one()
    {
        var host = NewHost(0);

        Assert.Equal(1, host.MaxParallelSubAgents);
        Assert.False(host.SetMaxParallelSubAgents(1));
    }

    [Fact]
    public void Concurrent_setters_leave_a_cap_one_of_them_set()
    {
        var host = NewHost(4);
        var moves = 0;

        // Every call sets a value in 5..9, none equal to the start (4), so the final cap is one that was really requested
        // and at least the first call moved it.
        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = Threads }, index =>
        {
            var value = 5 + (index % 5);
            if (host.SetMaxParallelSubAgents(value))
            {
                Interlocked.Increment(ref moves);
            }
        });

        Assert.InRange(host.MaxParallelSubAgents, 5, 9);
        Assert.InRange(moves, 1, 400);
        Assert.False(host.SetMaxParallelSubAgents(host.MaxParallelSubAgents));
    }

    [Fact]
    public void Out_of_range_caps_set_concurrently_keep_the_floor()
    {
        var host = NewHost(4);

        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = Threads }, index =>
            host.SetMaxParallelSubAgents(index % 3 == 0 ? -index : index % 20));

        Assert.InRange(host.MaxParallelSubAgents, 1, 19);
        Assert.False(host.SetMaxParallelSubAgents(host.MaxParallelSubAgents));
    }
}
