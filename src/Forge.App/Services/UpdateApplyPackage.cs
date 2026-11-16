using System.Buffers;
using System.Formats.Tar;
using System.IO.Compression;

namespace Forge.App.Services;

/// <summary>
/// The bounds of one unpack. The shipped values are the constants of <see cref="UpdatePackages"/>; a caller passes tighter ones
/// so that "an entry that lies about its declared size" can be proven to be refused at the byte that breaks the bound,
/// without first writing half a gigabyte.
/// </summary>
internal sealed record UpdatePackageLimits(long MaxEntryBytes, long MaxTotalBytes, long MaxCompressionRatio, int MaxEntryCount)
{
    public static UpdatePackageLimits Default { get; } = new(
        UpdatePackages.MaxEntryBytes,
        UpdatePackages.MaxTotalBytes,
        UpdatePackages.MaxCompressionRatio,
        UpdatePackages.MaxEntryCount);
}

/// <summary>
/// Unpacks a verified update package into the staging tree. The format is chosen by the file name — <c>.zip</c> is what
/// the Windows artifacts are published as, <c>.tar.gz</c>/<c>.tgz</c> is what the Unix artifacts are — so one build can
/// install either. Unix permissions are kept from the archive: a <c>Forge.App</c> that lost its execute bit could not be
/// started, and that is exactly the file the swap moves.
/// <para>
/// The bytes are already SHA-256 verified, but the archive is still treated as hostile input: no absolute paths, no
/// <c>..</c>, no links or devices, and per-file, total, entry-count and compression-ratio limits. This mirrors the
/// extension package bounds in <c>ExtensionRepositoryService</c>; the shared limits are not lifted into one helper because
/// that file belongs to the extension wave and this one to the installer, and the two are allowed to diverge.
/// </para>
/// </summary>
internal static class UpdatePackages
{
    /// <summary>An app directory is a few hundred files; a hostile archive must not become millions.</summary>
    public const int MaxEntryCount = 65536;

    public const long MaxEntryBytes = 512L * 1024 * 1024;

    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;

    public const long MaxCompressionRatio = 100;

    /// <summary>The permission bits an archive may carry over: read/write/execute for the three classes.</summary>
    private const int PermissionMask = 0x1FF;

    /// <summary>Above the large-object-heap threshold, so the buffer is pooled rather than rented from the GC.</summary>
    private const int CopyBufferSize = 81920;

    public static void Extract(string packagePath, string destination) =>
        Extract(packagePath, destination, UpdatePackageLimits.Default);

    public static void Extract(string packagePath, string destination, UpdatePackageLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(limits);

        var name = Path.GetFileName(packagePath);
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);

        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ExtractZip(packagePath, root, limits);
            return;
        }

        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            ExtractTarGz(packagePath, root);
            return;
        }

        throw new InvalidDataException("'" + name + "' is neither a .zip nor a .tar.gz package.");
    }

    /// <summary>
    /// Unpacks a zip. The declared sizes of an entry are attacker-controlled metadata, so they only get a cheap first refusal
    /// (<c>entry.Length</c> and <c>entry.CompressedLength</c> reject an honestly oversized package before it is unpacked); the
    /// bounds that actually hold are counted from the bytes that arrive on the stream and are checked before each write. The
    /// compression ratio is measured against the archive file's own size, which no header can understate.
    /// <para>
    /// The runtime's archive reader happens to validate an entry against its own declaration before it hands the bytes over, so
    /// a lying central directory is often refused by the reader first; the counter is what makes the bounds true where it does
    /// not, and it stops an oversized entry at the bound instead of after the whole entry has been written.
    /// </para>
    /// </summary>
    private static void ExtractZip(string packagePath, string root, UpdatePackageLimits limits)
    {
        var archiveBytes = new FileInfo(packagePath).Length;
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count > limits.MaxEntryCount)
        {
            throw new InvalidDataException($"the package has {archive.Entries.Count} entries, more than {limits.MaxEntryCount}.");
        }

        // The ceiling of how much the whole archive may expand to: compressed data cannot be larger than the file that carries
        // it, so this bound survives any lie in the central directory. It is the one guard that would still stop a hostile
        // entry on a runtime whose archive reader hands the bytes over without validating them against the declaration (which
        // is what "declared 1 byte, wrote 200 KB" was).
        var maxExpanded = archiveBytes * limits.MaxCompressionRatio;

        long total = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            foreach (var entry in archive.Entries)
            {
                var relative = Normalize(entry.FullName);
                if (relative is null)
                {
                    continue;
                }

                if (IsLink(entry))
                {
                    throw new InvalidDataException("the package contains a link '" + entry.FullName + "'.");
                }

                if (entry.Length > limits.MaxEntryBytes)
                {
                    throw new InvalidDataException("'" + entry.FullName + "' is larger than the per-file limit.");
                }

                if (entry.Length > 0 && entry.CompressedLength > 0 &&
                    entry.Length / entry.CompressedLength > limits.MaxCompressionRatio)
                {
                    throw new InvalidDataException("'" + entry.FullName + "' expands past the compression-ratio limit.");
                }

                var target = TargetPath(root, relative);
                if (IsDirectoryEntry(entry))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                long written = 0;
                using (var input = entry.Open())
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        written += read;
                        total += read;

                        // Checked before the bytes are written: an entry that lies about its size stops at the bound instead of
                        // first producing a file the limits never accounted for.
                        if (written > limits.MaxEntryBytes)
                        {
                            throw new InvalidDataException("'" + entry.FullName + "' is larger than the per-file limit.");
                        }

                        if (total > limits.MaxTotalBytes)
                        {
                            throw new InvalidDataException("the package expands past the total limit.");
                        }

                        if (total > maxExpanded)
                        {
                            throw new InvalidDataException("'" + entry.FullName + "' expands past the compression-ratio limit.");
                        }

                        output.Write(buffer, 0, read);
                    }
                }

                if (!OperatingSystem.IsWindows())
                {
                    var mode = (entry.ExternalAttributes >> 16) & PermissionMask;
                    if (mode != 0)
                    {
                        File.SetUnixFileMode(target, (UnixFileMode)mode);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ExtractTarGz(string packagePath, string root)
    {
        using var file = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        var entries = 0;
        long total = 0;
        while (reader.GetNextEntry() is { } entry)
        {
            if (++entries > MaxEntryCount)
            {
                throw new InvalidDataException($"the package has more than {MaxEntryCount} entries.");
            }

            if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink or TarEntryType.CharacterDevice
                or TarEntryType.BlockDevice or TarEntryType.Fifo)
            {
                throw new InvalidDataException("the package contains a link or device '" + entry.Name + "'.");
            }

            var relative = Normalize(entry.Name);
            if (relative is null)
            {
                continue;
            }

            if (entry.Length > MaxEntryBytes)
            {
                throw new InvalidDataException("'" + entry.Name + "' is larger than the per-file limit.");
            }

            total += entry.Length;
            if (total > MaxTotalBytes)
            {
                throw new InvalidDataException("the package expands past the total limit.");
            }

            var target = TargetPath(root, relative);
            if (entry.EntryType is TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (entry.DataStream is null)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            entry.DataStream.CopyTo(output);
            output.Flush();

            if (!OperatingSystem.IsWindows())
            {
                var mode = (int)entry.Mode & PermissionMask;
                if (mode != 0)
                {
                    File.SetUnixFileMode(target, (UnixFileMode)mode);
                }
            }
        }
    }

    /// <summary>
    /// The entry name as a relative path below the destination, or null for the archive's own root entry. A name that is
    /// absolute, carries <c>..</c>, or holds a volume separator is refused: that is the zip-slip escape.
    /// </summary>
    private static string? Normalize(string fullName)
    {
        var relative = fullName.Replace('\\', '/');
        while (relative.StartsWith("./", StringComparison.Ordinal))
        {
            relative = relative[2..];
        }

        if (relative.Length == 0 || relative == ".")
        {
            return null;
        }

        if (relative.StartsWith('/') || Path.IsPathRooted(relative) || (relative.Length > 1 && relative[1] == ':'))
        {
            throw new InvalidDataException("the package contains the absolute path '" + fullName + "'.");
        }

        foreach (var segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw new InvalidDataException("the package contains the unsafe path '" + fullName + "'.");
            }
        }

        return relative;
    }

    /// <summary>The absolute target of a normalized entry name; the containment check is a second lock on the same door.</summary>
    private static string TargetPath(string root, string relative)
    {
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!UpdateApplyPaths.IsStrictlyInside(target, root))
        {
            throw new InvalidDataException("the package entry '" + relative + "' escapes the staging directory.");
        }

        return target;
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || entry.Length == 0 && entry.Name.Length == 0;

    private static bool IsLink(ZipArchiveEntry entry)
    {
        if (!OperatingSystem.IsWindows())
        {
            // On Unix the file mode travels in the high 16 bits; S_IFLNK (0120000 octal) marks a symbolic link.
            return ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;
        }

        return (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }
}
