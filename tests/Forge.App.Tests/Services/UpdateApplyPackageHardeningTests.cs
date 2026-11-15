using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// The archive safety bounds <see cref="UpdatePackages"/> enforces on top of the SHA-256 check: a manifest is a remote
/// document, so a verified-but-hostile archive must still be refused. A tar.gz link or device entry, a zip reparse point,
/// a zip entry whose compression ratio blows past the limit, an entry count past the cap, an entry that lies about its
/// declared size, and a total that only looks small while the declared sizes are believed are all rejected; setuid/setgid/
/// sticky bits are masked down to the plain permission bits so a hostile archive cannot smuggle a setuid binary in.
/// </summary>
public sealed class UpdateApplyPackageHardeningTests
{
    [Fact]
    public void A_tar_gz_symlink_entry_is_refused()
    {
        using var tree = new TempTree("forge-package-link-");
        var package = Path.Combine(tree.Root, "Forge-1.2.0-linux-x64.tar.gz");
        using (var stream = File.Create(package))
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link.txt") { LinkName = "target.txt" });
        }

        var error = Assert.Throws<InvalidDataException>(() => UpdatePackages.Extract(package, Path.Combine(tree.Root, "payload")));
        Assert.Contains("link or device", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(tree.Root, "payload", "link.txt")));
    }

    [Fact]
    public void A_tar_gz_device_entry_is_refused()
    {
        using var tree = new TempTree("forge-package-device-");
        var package = Path.Combine(tree.Root, "Forge-1.2.0-linux-x64.tar.gz");
        using (var stream = File.Create(package))
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Fifo, "pipe"));
        }

        var error = Assert.Throws<InvalidDataException>(() => UpdatePackages.Extract(package, Path.Combine(tree.Root, "payload")));
        Assert.Contains("link or device", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(tree.Root, "payload", "pipe")));
    }

    [Fact]
    public void A_zip_reparse_point_entry_is_refused()
    {
        using var tree = new TempTree("forge-package-reparse-");
        var package = Path.Combine(tree.Root, "Forge-1.2.0-win-x64.zip");
        using (var stream = File.Create(package))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("link.txt");
            entry.ExternalAttributes = (int)FileAttributes.ReparsePoint;
            using (var writer = new StreamWriter(entry.Open(), Encoding.UTF8))
            {
                writer.Write("target");
            }
        }

        var error = Assert.Throws<InvalidDataException>(() => UpdatePackages.Extract(package, Path.Combine(tree.Root, "payload")));
        Assert.Contains("link", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(tree.Root, "payload", "link.txt")));
    }

    [Fact]
    public void A_zip_entry_whose_declared_size_lies_is_refused_at_the_actual_byte_count()
    {
        // The central directory claims 1 byte for an entry that really inflates to 200 KB: every bound that reads the declared
        // length is satisfied, so the bytes themselves are the only witness. The shipped limits are hundreds of megabytes, so
        // the limits are passed in tight — the same defect needs the same proof, not a gigabyte of writes. The assertion is the
        // outcome, not which guard spoke first (the archive reader validates an entry against its declaration too, exactly as
        // in the extension extractor's test for this defect): the lie is refused and nothing past the bound is kept.
        using var tree = new TempTree("forge-package-lie-");
        var package = Path.Combine(tree.Root, "Forge-1.2.0-win-x64.zip");
        WriteZip(package, ("big.bin", new byte[200 * 1024]));
        PatchDeclaredUncompressedSizes(package, 1);

        var limits = new UpdatePackageLimits(
            MaxEntryBytes: 64 * 1024,
            MaxTotalBytes: 1024 * 1024,
            MaxCompressionRatio: 1000,
            MaxEntryCount: 100);
        var destination = Path.Combine(tree.Root, "payload");

        var error = Assert.Throws<InvalidDataException>(() => UpdatePackages.Extract(package, destination, limits));

        Assert.NotEmpty(error.Message);
        var target = Path.Combine(destination, "big.bin");
        var written = File.Exists(target) ? new FileInfo(target).Length : 0;
        Assert.True(written <= limits.MaxEntryBytes, $"the unpack wrote {written} bytes past a {limits.MaxEntryBytes} byte bound");
        Assert.True(written < 200 * 1024, $"the whole lying entry landed on disk ({written} bytes)");
    }

    [Fact]
    public void A_zip_entry_is_refused_at_the_counted_total_rather_than_the_declared_sum()
    {
        // Three entries of 100 KB against a 200 KB total: no single one breaks the per-file bound, and the sum of the declared
        // sizes is only a sum if the declared sizes are believed — the total has to be counted from the bytes written.
        using var tree = new TempTree("forge-package-total-");
        var package = Path.Combine(tree.Root, "Forge-1.2.0-win-x64.zip");
        var payload = new byte[100 * 1024];
        WriteZip(package, ("one.bin", payload), ("two.bin", payload), ("three.bin", payload));

        var limits = new UpdatePackageLimits(
            MaxEntryBytes: 512 * 1024,
            MaxTotalBytes: 200 * 1024,
            MaxCompressionRatio: 1000,
            MaxEntryCount: 100);
        var destination = Path.Combine(tree.Root, "payload");

        var error = Assert.Throws<InvalidDataException>(() => UpdatePackages.Extract(package, destination, limits));

        Assert.Contains("total limit", error.Message, StringComparison.Ordinal);
        var written = Directory.EnumerateFiles(destination).Sum(file => new FileInfo(file).Length);
        Assert.True(written <= limits.MaxTotalBytes, $"the copy wrote {written} bytes past a {limits.MaxTotalBytes} byte bound");
    }

    [Fact]
    public void A_zip_entry_that_expands_past_the_compression_ratio_is_refused()
    {
        using var tree = new TempTree("forge-package-ratio-");
        var package = Path.Combine(tree.Root, "Forge-1.2.0-win-x64.zip");
        using (var stream = File.Create(package))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("zeros.bin");
            using (var writer = entry.Open())
            {
                var zeros = new byte[300 * 1024];
                writer.Write(zeros);
            }
        }

        var error = Assert.Throws<InvalidDataException>(() => UpdatePackages.Extract(package, Path.Combine(tree.Root, "payload")));
        Assert.Contains("compression-ratio", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(tree.Root, "payload", "zeros.bin")));
    }

    [Fact]
    public void A_zip_with_more_entries_than_the_cap_is_refused_before_anything_lands()
    {
        using var tree = new TempTree("forge-package-count-");
        var package = Path.Combine(tree.Root, "Forge-1.2.0-win-x64.zip");
        using (var stream = File.Create(package))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            for (var i = 0; i < UpdatePackages.MaxEntryCount + 1; i++)
            {
                archive.CreateEntry("e" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var destination = Path.Combine(tree.Root, "payload");
        var error = Assert.Throws<InvalidDataException>(() => UpdatePackages.Extract(package, destination));
        Assert.Contains(UpdatePackages.MaxEntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public void A_tar_entry_with_setuid_bits_is_masked_to_the_permission_bits()
    {
        using var tree = new TempTree("forge-package-setuid-");
        var package = Path.Combine(tree.Root, "Forge-1.2.0-linux-x64.tar.gz");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.SetUser;
        using (var stream = File.Create(package))
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "payload.txt") { Mode = mode };
            entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes("content"));
            writer.WriteEntry(entry);
        }

        var destination = Path.Combine(tree.Root, "payload");
        UpdatePackages.Extract(package, destination);

        var target = Path.Combine(destination, "payload.txt");
        Assert.Equal("content", File.ReadAllText(target));
        if (!OperatingSystem.IsWindows())
        {
            var applied = File.GetUnixFileMode(target);
            Assert.False(applied.HasFlag(UnixFileMode.SetUser), "the setuid bit must not survive the unpack");
            Assert.True(applied.HasFlag(UnixFileMode.UserExecute), "the ordinary execute bit is kept");
        }
    }


    private sealed class TempTree : IDisposable
    {
        public TempTree(string prefix) => Root = TestTempFiles.NewDirectory(prefix);

        public string Root { get; }

        public void Dispose() => TestTempFiles.DeleteTree(Root);
    }

    /// <summary>Writes a zip of deflated entries: the payload a hostile archive would use to inflate past its declared size.</summary>
    private static void WriteZip(string path, params (string Name, byte[] Content)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var output = entry.Open();
            output.Write(content, 0, content.Length);
        }
    }

    /// <summary>
    /// Rewrites the uncompressed size of every central-directory record, leaving the stored payloads alone: the archive now
    /// declares a size that is not the size of its own data. Reading the same lie is what
    /// <c>ExtensionRepositoryService</c>'s extractor is pinned against; the two extractors are allowed to diverge, so each
    /// carries its own pattern.
    /// </summary>
    private static void PatchDeclaredUncompressedSizes(string path, uint declaredSize) =>
        PatchCentralDirectoryField(path, offset: 24, declaredSize);

    /// <summary>Rewrites one 4-byte field of every central-directory record (20 = compressed size, 24 = uncompressed size).</summary>
    private static void PatchCentralDirectoryField(string path, int offset, uint declaredSize)
    {
        var bytes = File.ReadAllBytes(path);
        var records = 0;
        for (var index = 0; index + 46 <= bytes.Length; index++)
        {
            if (bytes[index] != 0x50 || bytes[index + 1] != 0x4B ||
                bytes[index + 2] != 0x01 || bytes[index + 3] != 0x02)
            {
                continue;
            }

            BitConverter.TryWriteBytes(bytes.AsSpan(index + offset, 4), declaredSize);
            records++;
        }

        if (records == 0)
        {
            throw new InvalidOperationException("the zip has no central directory record");
        }

        File.WriteAllBytes(path, bytes);
    }
}
