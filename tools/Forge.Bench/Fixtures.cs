using System.Globalization;
using System.Text;
using Forge.IO;

namespace Forge.Bench;

/// <summary>
/// The fixtures every scenario shares: text payloads, sparse and real large files, synthetic trees, the 10k-file
/// repository, the shared <see cref="ProcessRunner"/>, and the optional ripgrep executable. Each fixture is created on
/// first use inside <see cref="BenchWorkspace"/> and reused afterwards, so the setup cost lands in a scenario's prepare
/// step (or its warmup) instead of its measurements.
/// </summary>
internal sealed class Fixtures
{
    /// <summary>Every generated line is exactly this many bytes, terminator included, so a size maps to a line count.</summary>
    public const int LineLength = 64;

    /// <summary>Literal present in every line of <see cref="RepositoryFilesWithToken"/> repository files.</summary>
    public const string HitToken = "NEEDLE_HIT_TOKEN";

    /// <summary>Literal present in no file of the repository.</summary>
    public const string MissToken = "NEEDLE_ABSENT_TOKEN";

    public const int RepositoryFiles = 10_000;
    public const int RepositoryFileBytes = 1_400;
    public const int RepositoryFilesWithToken = 20;

    private readonly Dictionary<string, string> created = new(StringComparer.OrdinalIgnoreCase);
    private string? ripgrepPath;
    private bool ripgrepProbed;

    public Fixtures(BenchWorkspace workspace)
    {
        Workspace = workspace;
        Runner = new ProcessRunner();
    }

    public BenchWorkspace Workspace { get; }

    /// <summary>The runner the process, output, grep and concurrency scenarios share; its gate is the product default of 8.</summary>
    public ProcessRunner Runner { get; }

    /// <summary>Lines in a fixture of <paramref name="byteCount"/> bytes, which is what a grep or read window sees.</summary>
    public static int LinesOf(int byteCount) => Math.Max(1, byteCount / LineLength);

    /// <summary>
    /// Builds plain ASCII lines of exactly <see cref="LineLength"/> bytes, each carrying <paramref name="marker"/> in its
    /// head. ASCII keeps byte counts and character counts equal, so a fixture of N bytes is also N bytes of UTF-8.
    /// </summary>
    public static string MakeText(int byteCount, string? marker = null)
    {
        var lines = LinesOf(byteCount);
        var builder = new StringBuilder(lines * LineLength);
        for (var index = 0; index < lines; index++)
        {
            var head = marker is null
                ? string.Create(CultureInfo.InvariantCulture, $"line {index:D7} ")
                : string.Create(CultureInfo.InvariantCulture, $"line {index:D7} {marker} ");
            builder.Append(head);
            builder.Append('x', Math.Max(1, LineLength - 1 - head.Length));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>A text file of exactly <paramref name="byteCount"/> bytes (a multiple of <see cref="LineLength"/>).</summary>
    public string TextFile(string relative, int byteCount)
    {
        var path = Workspace.FileAt(relative);
        if (!created.ContainsKey(relative))
        {
            Workspace.WriteTextFile(relative, MakeText(byteCount));
            created[relative] = path;
        }

        return path;
    }

    /// <summary>A text file whose first 16 KiB are real text and whose body is a hole; see <see cref="BenchWorkspace.WriteSparseTextFile"/>.</summary>
    public string SparseTextFile(string relative, long length)
    {
        var path = Workspace.FileAt(relative);
        if (!created.ContainsKey(relative))
        {
            Workspace.WriteSparseTextFile(relative, length, 16 * 1024);
            created[relative] = path;
        }

        return path;
    }

    /// <summary>A file of <paramref name="length"/> bytes of real content, for the copy scenario.</summary>
    public string RealFile(string relative, long length)
    {
        var path = Workspace.FileAt(relative);
        if (!created.ContainsKey(relative))
        {
            Workspace.WriteRealFile(relative, length);
            created[relative] = path;
        }

        return path;
    }

    /// <summary>A tree of <paramref name="fileCount"/> empty files, for the walk scenarios.</summary>
    public string Tree(string relative, int fileCount)
    {
        var path = Workspace.FileAt(relative);
        if (!created.ContainsKey(relative))
        {
            path = Workspace.WriteTree(relative, fileCount, directories: Math.Max(1, fileCount / 2000));
            created[relative] = path;
        }

        return path;
    }

    /// <summary>The 10k-file repository the grep scenarios search.</summary>
    public string Repository()
    {
        const string relative = "repo-10k";
        var path = Workspace.FileAt(relative);
        if (!created.ContainsKey(relative))
        {
            path = Workspace.WriteRepository(
                relative,
                RepositoryFiles,
                RepositoryFileBytes,
                RepositoryFilesWithToken,
                HitToken);
            created[relative] = path;
        }

        return path;
    }

    /// <summary>
    /// The ripgrep executable when one is on <c>PATH</c> and answers <c>--version</c> with exit code 0, otherwise
    /// <c>null</c>. The probe runs once per process; the answer is printed so the run's reports say whether the parity
    /// comparison ran at all.
    /// </summary>
    public async Task<string?> RipgrepAsync(CancellationToken cancellationToken)
    {
        if (ripgrepProbed)
        {
            return ripgrepPath;
        }

        ripgrepProbed = true;
        ripgrepPath = await LocateRipgrepAsync(cancellationToken).ConfigureAwait(false);
        Console.Error.WriteLine($"ripgrep: {ripgrepPath ?? "(not found)"}");
        return ripgrepPath;
    }

    private async Task<string?> LocateRipgrepAsync(CancellationToken cancellationToken)
    {
        var executable = OperatingSystem.IsWindows() ? "rg.exe" : "rg";
        IEnumerable<string>? directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var directory in directories.Concat([AppContext.BaseDirectory]))
        {
            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(directory, executable));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (File.Exists(candidate) && await VersionSucceedsAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<bool> VersionSucceedsAsync(string executable, CancellationToken cancellationToken)
    {
        var result = await Runner.ExecuteAsync(
            new ProcessRequest
            {
                FileName = executable,
                Arguments = ["--version"],
                WorkingDirectory = Workspace.Root,
                Timeout = TimeSpan.FromSeconds(15),
                MaxStdoutBytes = 64 * 1024,
                MaxStderrBytes = 64 * 1024,
            },
            cancellationToken).ConfigureAwait(false);
        return result.Success && result.Stdout.Contains("ripgrep", StringComparison.OrdinalIgnoreCase);
    }
}
