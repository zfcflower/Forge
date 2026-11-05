using System.Diagnostics;
using System.Text;
using Forge.App.Terminal.Vt;
using Xunit;
using Xunit.Abstractions;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// A loose throughput budget for the emulator: 20 MiB of mixed text (wraps plus occasional
/// SGR) fed in 64 KiB chunks to an 80x24 screen with 10000 scrollback lines must complete
/// well under the threshold. The threshold is deliberately generous so CI timing jitter
/// cannot flake it; the measured value is reported for the record.
/// </summary>
public sealed class TerminalPerformanceTests
{
    private readonly ITestOutputHelper output;

    public TerminalPerformanceTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public void Twenty_megabytes_feed_completes_within_budget()
    {
        const int totalBytes = 20 * 1024 * 1024;
        const int chunkBytes = 64 * 1024;
        byte[] payload = BuildPayload(totalBytes);

        var emulator = VtHelpers.Create(80, 24, 10000, out _);
        var timer = Stopwatch.StartNew();
        for (int offset = 0; offset < payload.Length; offset += chunkBytes)
        {
            int count = Math.Min(chunkBytes, payload.Length - offset);
            emulator.Feed(new ReadOnlySpan<byte>(payload, offset, count));
        }

        timer.Stop();

        double megabytesPerSecond = totalBytes / 1024.0 / 1024.0 / Math.Max(timer.Elapsed.TotalSeconds, 1e-9);
        output.WriteLine($"Fed {totalBytes} bytes in {timer.Elapsed.TotalSeconds:F2}s ({megabytesPerSecond:F1} MiB/s).");

        // Sanity: the emulator actually consumed the stream (cursor moved, scrollback grew).
        Assert.True(emulator.ScrollbackCount > 0);
        Assert.True(emulator.Version > 0);
        Assert.True(
            timer.Elapsed < TimeSpan.FromSeconds(5),
            $"Feeding 20 MiB took {timer.Elapsed.TotalSeconds:F2}s, over the 5s budget ({megabytesPerSecond:F1} MiB/s).");
    }

    private static byte[] BuildPayload(int totalBytes)
    {
        var builder = new StringBuilder(totalBytes + 1024);
        int line = 0;
        while (builder.Length < totalBytes)
        {
            line++;
            if (line % 50 == 0)
            {
                builder.Append("\u001b[38;5;200mcolored-line-");
                builder.Append(line);
                builder.Append("\u001b[0m padding text to fill the row width 0123456789\r\n");
            }
            else
            {
                builder.Append("The quick brown fox jumps over the lazy dog 0123456789 ");
                builder.Append(line % 1000);
                builder.Append("\r\n");
            }
        }

        byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
        if (bytes.Length > totalBytes)
        {
            byte[] trimmed = new byte[totalBytes];
            Buffer.BlockCopy(bytes, 0, trimmed, 0, totalBytes);
            return trimmed;
        }

        return bytes;
    }
}
