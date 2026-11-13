namespace Forge.App.Tests;

/// <summary>The bounded pump the view-model suites use: poll a condition, yield the thread between polls, never sleep unbounded.</summary>
internal static class TestPump
{
    private const int DelayMilliseconds = 5;

    /// <summary>Polls <paramref name="condition"/> at most <paramref name="iterations"/> times and returns its last answer.</summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, int iterations = 400)
    {
        for (var i = 0; i < iterations; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(DelayMilliseconds);
        }

        return condition();
    }
}
