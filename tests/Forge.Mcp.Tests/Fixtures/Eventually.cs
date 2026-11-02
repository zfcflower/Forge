using System.Diagnostics;

namespace Forge.Mcp.Tests.Fixtures;

internal static class Eventually
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public static async Task<bool> Async(Func<bool> condition, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            if (condition())
            {
                return true;
            }

            if (Stopwatch.GetElapsedTime(start) > limit)
            {
                return false;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    public static async Task AssertAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        if (!await Async(condition, timeout).ConfigureAwait(false))
        {
            throw new Xunit.Sdk.XunitException("Condition not met: " + because);
        }
    }
}
