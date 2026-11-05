namespace Forge.Integration.Tests;

/// <summary>
/// The two kinds of upper bound this suite uses, kept apart on purpose.
/// <para>
/// A <b>product promise</b> - a script killed when its budget expires, a cancelled caller released, a host that is gone
/// only after its worker is - is asserted with the number the product promises. Those numbers do not grow for a slower
/// machine; the assertion prints the elapsed time it measured instead, so a failure says whether the product missed its
/// promise or the machine was loaded.
/// </para>
/// <para>
/// A <b>safety net</b> only stops a test from hanging forever while it waits for a state the product has to reach (a
/// status row, a registered tool set, a process exit, a host that listens). It is not a claim about speed, so it is
/// derived from what this run actually costs: every host start the helpers observe is recorded here and the limits are
/// a multiple of the slowest one, clamped between the value that is known to be enough on a quiet machine and a ceiling
/// that still fails a run which makes no progress at all - the shape of
/// <c>Forge.IO.Tests.TestSupport.UpdateSeriesLimit</c>: measure, multiply, clamp.
/// </para>
/// </summary>
internal static class TestBudgets
{
    /// <summary>What one host start costs on a quiet machine. Only the seed of the observed figure, which comes from
    /// real starts as the suite runs.</summary>
    private static readonly TimeSpan QuietHostStart = TimeSpan.FromSeconds(1);

    /// <summary>How many host starts one safety net covers. A wait that follows a start inherits the same multiple of
    /// the start's measured cost.</summary>
    private const int StartFactor = 8;

    private static long slowestHostStart = QuietHostStart.Ticks;

    /// <summary>Records one observed host start: process launch to an accepted connection.</summary>
    public static void NoteHostStart(TimeSpan elapsed)
    {
        var ticks = elapsed.Ticks;
        var current = Volatile.Read(ref slowestHostStart);
        while (ticks > current)
        {
            var seen = Interlocked.CompareExchange(ref slowestHostStart, ticks, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    /// <summary>The slowest host start this run has observed, so a failure can name the machine cost it was judged
    /// against.</summary>
    public static TimeSpan ObservedHostStart => TimeSpan.FromTicks(Volatile.Read(ref slowestHostStart));

    /// <summary><see cref="StartFactor" /> slowest observed host starts, kept between <paramref name="floor" /> and
    /// <paramref name="ceiling" />. Use it for a wait, never for a product promise.</summary>
    public static TimeSpan Safety(TimeSpan floor, TimeSpan ceiling)
    {
        var scaled = TimeSpan.FromTicks(ObservedHostStart.Ticks * StartFactor);
        return scaled < floor ? floor : scaled > ceiling ? ceiling : scaled;
    }

    /// <summary>Bound on one host start: launch the process and see it accept a connection on its pipe.</summary>
    public static TimeSpan HostStart => Safety(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(300));

    /// <summary>Bound on one wait for a state a running host has to reach: a status row, a tool set, a worker that
    /// exits.</summary>
    public static TimeSpan Condition => Safety(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120));

    /// <summary>Bound on one whole fixture: several starts, worker loads and calls.</summary>
    public static TimeSpan Fixture => Safety(TimeSpan.FromSeconds(240), TimeSpan.FromMinutes(10));
}
