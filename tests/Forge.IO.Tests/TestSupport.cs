using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Forge.IO.Tests;

internal static class TestSupport
{
    public static readonly TimeSpan TestLimit = TimeSpan.FromSeconds(30);

    /// <summary>Ceiling for the delay between a cancellation request and the <see cref="OperationCanceledException" />
    /// of a cancelled replace retry. The retry loop checks the token before every attempt and its wait is cancellable,
    /// so the delay is one file system call plus one millisecond; a single call can still be stalled by the machine
    /// (real-time scanning), which is why the ceiling is seconds rather than milliseconds. A loop that ignored the
    /// token would instead run into the retry budget it is given.</summary>
    public static readonly TimeSpan CancelLatencyLimit = TimeSpan.FromSeconds(5);

    // A series of store updates is serialized by the store's cross-process lock and every update ends in one fsync, so
    // its wall time is the number of updates times what one update costs on the machine running the test. The probe
    // below measures that cost in the same directory; the factor covers lock handoff and the contention of eight
    // instances, the floor keeps the historical 30 s limit on a fast disk and the ceiling still fails a run that makes
    // no progress at all.
    private const int ProbeUpdates = 6;
    private const int LimitFactor = 8;

    private static readonly TimeSpan MaximumSeriesLimit = TimeSpan.FromMinutes(2);

    private const UnixFileMode UnixWritable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>Makes <paramref name="path"/> read-only the way each platform reports it: the attribute on
    /// Windows, mode 0444 on Unix.</summary>
    public static void MakeReadOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    public static void MakeWritable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, FileAttributes.Normal);
            return;
        }

        File.SetUnixFileMode(path, UnixWritable);
    }

    public static UnixFileMode UnixMode(string path) =>
        OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(path);

    /// <summary>Asserts the file still carries the read-only state <see cref="MakeReadOnly"/> set.</summary>
    public static void AssertStillReadOnly(string path)
    {
        Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0, $"'{path}' lost its ReadOnly attribute");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const UnixFileMode write = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        Assert.Equal(0, (int)(File.GetUnixFileMode(path) & write));
    }

    public static byte[] Encode(int count) =>
        JsonSerializer.SerializeToUtf8Bytes(new TestState { Count = count }, TestJsonContext.Default.TestState);

    public static TestState Decode(byte[] bytes) =>
        JsonSerializer.Deserialize(bytes, TestJsonContext.Default.TestState) ?? throw new InvalidDataException("Document is null.");

    /// <summary>Limit for a test that performs <paramref name="updates" /> serialized durable updates on this machine:
    /// eight times the median cost of one <see cref="AtomicJsonStore{T}.Update" /> measured in <paramref name="directory" />
    /// plus <paramref name="allowance" /> for work the probe does not cover (a child process start, for example), kept
    /// between <see cref="TestLimit" /> and two minutes. Returns the measured cost so a failure can report it.</summary>
    public static (TimeSpan PerUpdate, TimeSpan Limit) UpdateSeriesLimit(string directory, int updates, TimeSpan allowance = default)
    {
        var store = NewStore(System.IO.Path.Combine(directory, "update-cost-probe.json"));
        var samples = new List<TimeSpan>(ProbeUpdates);
        for (var probe = 0; probe < ProbeUpdates; probe++)
        {
            var start = Stopwatch.GetTimestamp();
            store.Update(state =>
            {
                state.Count++;
                return state;
            });
            // The first update also creates the file and its lock file.
            if (probe > 0)
            {
                samples.Add(Stopwatch.GetElapsedTime(start));
            }
        }

        samples.Sort();
        var perUpdate = samples[samples.Count / 2];
        return (perUpdate, SeriesLimit(perUpdate, updates, allowance));
    }

    /// <summary>Part of <see cref="UpdateSeriesLimit" /> that does not touch the disk, so the bounds can be tested
    /// with the costs of disks this machine does not have.</summary>
    public static TimeSpan SeriesLimit(TimeSpan perUpdate, int updates, TimeSpan allowance = default) =>
        TimeSpan.FromTicks(Math.Clamp(
            (perUpdate.Ticks * updates * LimitFactor) + allowance.Ticks,
            TestLimit.Ticks,
            MaximumSeriesLimit.Ticks));

    public static AtomicJsonStore<TestState> NewStore(string path, TimeSpan? lockTimeout = null, AtomicFileOptions? fileOptions = null) =>
        new(path, TestJsonContext.Default.TestState, lockTimeout, fileOptions);
}

internal sealed class TestState
{
    public int Count { get; set; }

    public string Label { get; set; } = string.Empty;
}

[JsonSerializable(typeof(TestState))]
internal sealed partial class TestJsonContext : JsonSerializerContext
{
}

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-io-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string[] TemporaryFiles() => Directory.GetFiles(Path, "*.tmp", SearchOption.AllDirectories);

    public void Dispose()
    {
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)
                && Stopwatch.GetElapsedTime(start) < TestSupport.TestLimit)
            {
                Thread.Sleep(10);
            }
        }
    }
}
