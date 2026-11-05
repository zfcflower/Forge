using System.Diagnostics;
using System.Globalization;

namespace Forge.ExtensionHost;

/// <summary>How a plugin call's budget is resolved from the request and the host's <c>--tool-timeout</c> cap.</summary>
internal static class PluginCallLimits
{
    /// <summary>Upper bound a command or prompt budget is clamped to.</summary>
    public const int MaxRequestTimeoutMilliseconds = 600_000;

    /// <summary>The budget of a tool execution: the request's value, capped by <c>--tool-timeout</c> and never below 1 ms.</summary>
    public static TimeSpan ToolTimeout(HostOptions options, int? requested)
    {
        ArgumentNullException.ThrowIfNull(options);
        var limit = options.ToolTimeout > TimeSpan.Zero ? options.ToolTimeout : HostOptions.DefaultToolTimeout;
        var value = requested is > 0 ? TimeSpan.FromMilliseconds(requested.Value) : limit;
        return value < limit ? value : limit;
    }

    /// <summary>The budget of a command or prompt: the request's value, or the configured cap when it carries none, clamped
    /// to 1-600000 ms exactly as before.</summary>
    public static TimeSpan RequestTimeout(HostOptions options, int? requested)
    {
        ArgumentNullException.ThrowIfNull(options);
        var fallback = options.ToolTimeout > TimeSpan.Zero ? options.ToolTimeout : HostOptions.DefaultToolTimeout;
        var value = requested is > 0 ? requested.Value : (int)fallback.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Clamp(value, 1, MaxRequestTimeoutMilliseconds));
    }

    /// <summary>Rounds a remaining budget up to whole milliseconds for the worker, inside [1, <paramref name="max"/>].</summary>
    public static int ToWireMilliseconds(TimeSpan remaining, int max) =>
        Math.Clamp((int)Math.Ceiling(remaining.TotalMilliseconds), 1, max);
}

/// <summary>
/// One absolute deadline for one plugin call, derived once at request entry from the request's own budget and the
/// <c>--tool-timeout</c> cap. The same deadline covers every admission stage of the call - waiting for the plugin gate,
/// the disk hash/approval recheck and the worker load - and the queue for an execution slot, so the plugin's
/// <c>--tool-timeout</c> and the caller's budget are not restarted after a queue. Callers can tell a budget expiry
/// (<see cref="Expired"/>) from a caller cancellation and from the host's shutdown fence; only the former is reported as
/// <c>plugin_queue_timeout</c> before anything was dispatched.
/// <para>
/// The linked <see cref="Token"/> is what the host hands to gate waits, hashing and loading: it is cancelled by the
/// caller, by the shutdown fence and by the deadline, so no stage outlives the call. The deadline timer is kept separate
/// so a callback can distinguish "the caller cancelled" (cooperative grace window, then a kill) from "time is up".
/// </para>
/// </summary>
internal sealed class CallDeadline : IDisposable
{
    private readonly CancellationTokenSource deadline = new();

    private readonly CancellationTokenSource linked;

    private readonly long started = Stopwatch.GetTimestamp();

    public CallDeadline(TimeSpan total, CancellationToken ct, CancellationToken stop)
    {
        Total = total > TimeSpan.Zero ? total : TimeSpan.FromMilliseconds(1);
        deadline.CancelAfter(Total);
        linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop, deadline.Token);
    }

    /// <summary>The whole budget the call was admitted with; messages quote this, never a reset timer.</summary>
    public TimeSpan Total { get; }

    /// <summary>Cancelled by the caller, the shutdown fence or the deadline; used for every stage before dispatch.</summary>
    public CancellationToken Token => linked.Token;

    /// <summary>True when the call's own budget ran out (not a caller cancellation, not the shutdown fence).</summary>
    public bool Expired => deadline.IsCancellationRequested;

    /// <summary>What is left of the budget now; dispatch and the worker get only this much.</summary>
    public TimeSpan Remaining
    {
        get
        {
            var left = Total - Stopwatch.GetElapsedTime(started);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    public string Seconds => (Total.TotalMilliseconds / 1000d).ToString("0.###", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        linked.Dispose();
        deadline.Dispose();
    }
}
