using System.Globalization;

namespace Forge.Bench;

/// <summary>
/// The child process the output scenarios measure: the bench re-launches itself with <c>--emit &lt;bytes&gt;</c> and writes
/// that many bytes to standard output. Re-using the bench instead of a shell keeps the payload deterministic on every
/// platform (a shell's <c>type</c>/<c>cat</c> behaviour, text mode and buffering are all in the way) and keeps the bytes
/// out of the file system: the child streams from a reused buffer.
/// </summary>
internal static class ChildEmitter
{
    public const string Switch = "--emit";

    private const int ChunkBytes = 64 * 1024;

    /// <summary>
    /// True when this process was started only to emit bytes: no header, no other work. The switch is looked for at the
    /// end, because a run through <c>dotnet exec app.dll</c> carries the assembly path as the first argument.
    /// </summary>
    public static bool IsRequested(string[] args) =>
        args.Length >= 2
        && string.Equals(args[^2], Switch, StringComparison.Ordinal)
        && long.TryParse(args[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
        && bytes >= 0;

    /// <summary>Writes the requested byte count to standard output and exits; nothing else is printed.</summary>
    public static int Run(string[] args)
    {
        var total = long.Parse(args[^1], NumberStyles.None, CultureInfo.InvariantCulture);
        var buffer = new byte[ChunkBytes];
        for (var index = 0; index < buffer.Length; index++)
        {
            buffer[index] = index % 101 == 100 ? (byte)'\n' : (byte)('a' + (index % 26));
        }

        using var stdout = Console.OpenStandardOutput();
        var written = 0L;
        while (written < total)
        {
            var count = (int)Math.Min(buffer.Length, total - written);
            stdout.Write(buffer, 0, count);
            written += count;
        }

        stdout.Flush();
        return 0;
    }

    /// <summary>
    /// How to start this bench again: the apphost (or <c>dotnet</c> when the run went through <c>dotnet exec app.dll</c>,
    /// which is what <c>dotnet run</c> does in some SDKs) plus the leading arguments that make it the same application.
    /// </summary>
    public static (string FileName, string[] Lead) SelfCommand()
    {
        var fileName = Environment.ProcessPath
            ?? throw new InvalidOperationException("The bench has no process path, so it cannot start itself as a child.");
        var entry = Environment.GetCommandLineArgs()[0];
        return entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? (fileName, [entry])
            : (fileName, []);
    }
}
