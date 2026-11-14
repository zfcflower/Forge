using System.Text.RegularExpressions;
using Xunit;

namespace Forge.Integration.Tests.Terminal;

/// <summary>Shared assertions over a terminal's captured byte stream: exact sequence continuity
/// and lossless in-order delivery of a numbered flood.</summary>
internal static class TerminalAsserts
{
    /// <summary>Every frame from <c>seq</c> 0 with no gap, duplicate or reorder.</summary>
    public static void SequenceIsContinuous(IReadOnlyList<TerminalFrame> frames, string terminalId)
    {
        Assert.True(frames.Count > 1, $"terminal '{terminalId}' delivered only {frames.Count} frame(s); the test needs several frames to prove ordering.");
        for (var index = 0; index < frames.Count; index++)
        {
            Assert.Equal(index, frames[index].Seq);
        }
    }

    /// <summary>The concatenated stream holds <c>LINE-0000001 … LINE-&lt;expected&gt;</c> exactly once,
    /// in order, followed by <paramref name="doneMarker"/>. Any gap, duplicate or reorder fails.</summary>
    public static void FloodIsLosslessInOrder(string text, int expectedLines, string doneMarker)
    {
        var matches = Regex.Matches(text, @"LINE-(\d{7})");
        Assert.Equal(expectedLines, matches.Count);
        for (var index = 0; index < matches.Count; index++)
        {
            Assert.Equal(index + 1, int.Parse(matches[index].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.Contains(doneMarker, text, StringComparison.Ordinal);
    }

    /// <summary>The host's output carries no terminal cleanup failure: a line naming the terminal and
    /// saying it could not / did not / is still running is a teardown defect.</summary>
    public static void NoCleanupFailures(RawTerminalHost host)
    {
        var failures = host.HostOutput
            .Where(line =>
                line.Contains("terminal", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("did not", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("still running", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        Assert.Empty(failures);
    }
}
