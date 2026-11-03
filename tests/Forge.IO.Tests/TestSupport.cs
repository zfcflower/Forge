using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Forge.IO.Tests;

internal static class TestSupport
{
    public static readonly TimeSpan TestLimit = TimeSpan.FromSeconds(30);

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
