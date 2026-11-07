using System.Text;
using Forge.IO;

namespace Forge.Bench;

/// <summary>
/// The <c>write</c> command: <see cref="TextFileWriter.WriteAsync"/> (same-directory temporary file, fsync, rename -
/// the durability the tool promises) against <see cref="File.WriteAllBytesAsync(string, byte[], CancellationToken)"/>,
/// which overwrites in place and does not fsync. The comparison therefore measures the price of atomicity as much as the
/// price of the write, and the row says so.
/// </summary>
internal static class WriteScenarios
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Atomic(fixtures, "1KiB", 1 << 10);
        yield return Atomic(fixtures, "1MiB", 1 << 20);
    }

    private static BenchScenario Atomic(Fixtures fixtures, string label, int byteCount)
    {
        return new BenchScenario(
            $"write.atomic.{label}",
            "write",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var path = fixtures.Workspace.FileAt($"write/atomic-{label}.txt");
                fixtures.Workspace.DirectoryAt("write");
                var content = Fixtures.MakeText(byteCount);
                var payload = Utf8.GetBytes(content);
                return Task.FromResult(new BenchCase
                {
                    Run = async cancellationToken =>
                    {
                        var written = await TextFileWriter.WriteAsync(path, content, cancellationToken).ConfigureAwait(false);
                        return new OpResult(written, Items: 1, written);
                    },
                    Baseline = async cancellationToken =>
                    {
                        await File.WriteAllBytesAsync(path, payload, cancellationToken).ConfigureAwait(false);
                        return new OpResult(payload.Length, Items: 1, payload.Length);
                    },
                    BaselineName = "File.WriteAllBytesAsync",
                    ProcessWideAllocation = true,
                    Note = "atomic write = temp file + fsync + rename; the baseline overwrites in place without fsync",
                });
            });
    }
}
