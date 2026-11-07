using System.Globalization;
using System.Text;

namespace Forge.Bench;

/// <summary>
/// The scratch directory of one run: a fresh directory under the system temp path, deleted when the run ends. Nothing of
/// the bench lives outside it, so a crashed run leaves one directory to remove and a successful run leaves nothing.
/// <para>The 100 MiB payloads that are only read are created with <see cref="FileStream.SetLength"/> and a small text
/// header: NTFS and most Unix file systems then report the length without writing the body, which is what keeps a run of
/// a few seconds from writing gigabytes. The write and copy scenarios are the exceptions - they write real bytes on
/// purpose, because that is the cost they measure.</para>
/// </summary>
internal sealed class BenchWorkspace : IDisposable
{
    private const string Prefix = "forge-bench-";

    public BenchWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>The run's scratch directory; every fixture path is below it.</summary>
    public string Root { get; }

    /// <summary>Absolute path of a fixture, with forward slashes in <paramref name="relative"/> accepted on every platform.</summary>
    public string FileAt(string relative) =>
        Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Creates (if needed) and returns the directory of a fixture path.</summary>
    public string DirectoryAt(string relative)
    {
        var directory = FileAt(relative);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Writes <paramref name="content"/> as UTF-8, without a BOM.</summary>
    public string WriteTextFile(string relative, string content)
    {
        var path = FileAt(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>
    /// Creates a text file of <paramref name="length"/> bytes whose first <paramref name="headerBytes"/> bytes are real
    /// text and whose body is left unwritten (a hole). The file reads back as text with a NUL tail, which keeps the
    /// reader's binary probe happy and the disk untouched.
    /// </summary>
    public string WriteSparseTextFile(string relative, long length, int headerBytes)
    {
        var path = FileAt(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var header = Fixtures.MakeText(headerBytes);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 0);
        stream.SetLength(length);
        var bytes = Encoding.UTF8.GetBytes(header);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
        return path;
    }

    /// <summary>Creates a file of <paramref name="length"/> bytes of real content in 1 MiB chunks: no sparse hole.</summary>
    public string WriteRealFile(string relative, long length)
    {
        var path = FileAt(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var chunk = new byte[1024 * 1024];
        for (var index = 0; index < chunk.Length; index++)
        {
            chunk[index] = (byte)('a' + (index % 26));
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 0);
        for (var written = 0L; written < length; written += chunk.Length)
        {
            stream.Write(chunk, 0, (int)Math.Min(chunk.Length, length - written));
        }

        stream.Flush(flushToDisk: false);
        return path;
    }

    /// <summary>
    /// Builds a synthetic tree of exactly <paramref name="fileCount"/> empty files spread over
    /// <paramref name="directories"/> directories, so a walk has a realistic fan-out and no file content to read.
    /// </summary>
    public string WriteTree(string relative, int fileCount, int directories)
    {
        var root = DirectoryAt(relative);
        var perDirectory = (int)Math.Ceiling(fileCount / (double)directories);
        var created = 0;
        for (var directoryIndex = 0; directoryIndex < directories && created < fileCount; directoryIndex++)
        {
            var directory = Path.Combine(root, string.Create(CultureInfo.InvariantCulture, $"d{directoryIndex:D4}"));
            Directory.CreateDirectory(directory);
            for (var fileIndex = 0; fileIndex < perDirectory && created < fileCount; fileIndex++)
            {
                var path = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"f{fileIndex:D5}.txt"));
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.None);
                created++;
            }
        }

        return root;
    }

    /// <summary>
    /// Builds a repository-shaped tree: <paramref name="fileCount"/> files of roughly <paramref name="fileBytes"/> bytes,
    /// the first <paramref name="filesWithToken"/> of which repeat <paramref name="token"/> on every line.
    /// </summary>
    public string WriteRepository(string relative, int fileCount, int fileBytes, int filesWithToken, string token)
    {
        var root = DirectoryAt(relative);
        var perDirectory = 100;
        var plain = Fixtures.MakeText(fileBytes);
        var marked = Fixtures.MakeText(fileBytes, token);
        var created = 0;
        for (var directoryIndex = 0; created < fileCount; directoryIndex++)
        {
            var directory = Path.Combine(root, string.Create(CultureInfo.InvariantCulture, $"pkg{directoryIndex:D3}"));
            Directory.CreateDirectory(directory);
            for (var fileIndex = 0; fileIndex < perDirectory && created < fileCount; fileIndex++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"src{fileIndex:D4}.cs");
                var path = Path.Combine(directory, name);
                File.WriteAllText(path, created < filesWithToken ? marked : plain);
                created++;
            }
        }

        return root;
    }

    /// <summary>Removes the scratch directory, retrying a few times: a lingering child process can hold a handle for a moment.</summary>
    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                Console.WriteLine($"temp directory removed: {Root}");
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }

        Console.WriteLine($"temp directory could NOT be removed: {Root}");
    }
}
