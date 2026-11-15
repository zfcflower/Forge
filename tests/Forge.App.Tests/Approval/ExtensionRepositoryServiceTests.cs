using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Approval;

public class ExtensionRepositoryServiceTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1", VersionComparison.Older)]
    [InlineData("1.0.0", "1.0.0", VersionComparison.Same)]
    [InlineData("1.0.1", "1.0.0", VersionComparison.Newer)]
    [InlineData("1.0.0", "2.0.0", VersionComparison.Older)]
    [InlineData("10.0.0", "9.9.9", VersionComparison.Newer)]
    // System.Version semantics: an absent build field is -1, so "1.0" is lower than "1.0.0" even though the two mean the
    // same thing to a human. Pinned because the index and the installed version are written by different people.
    [InlineData("1.0", "1.0.0", VersionComparison.Older)]
    [InlineData("1.0.0", "1.0", VersionComparison.Newer)]
    [InlineData("not-a-version", "1.0.0", VersionComparison.Incomparable)]
    [InlineData("1.0.0", "not-a-version", VersionComparison.Incomparable)]
    [InlineData(null, "1.0.0", VersionComparison.Incomparable)]
    public void CompareVersions_orders_by_numeric_fields(string? installed, string? available, VersionComparison expected)
    {
        Assert.Equal(expected, ExtensionRepositoryService.CompareVersions(installed, available));
    }

    [Fact]
    public async Task FetchIndexAsync_rejects_an_unknown_schema()
    {
        using var service = new ExtensionRepositoryService(Http("""{ "schema": 2, "packages": [] }"""));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.FetchIndexAsync("https://example.test/repo.json", CancellationToken.None));

        Assert.Contains("schema", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchIndexAsync_rejects_a_package_without_a_usable_sha256()
    {
        using var service = new ExtensionRepositoryService(Http(
            """{ "schema": 1, "packages": [{ "id": "a", "version": "1.0.0", "url": "https://example.test/a.zip", "sha256": "x" }] }"""));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.FetchIndexAsync("https://example.test/repo.json", CancellationToken.None));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchIndexAsync_returns_validated_packages()
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData("zip"u8));
        using var service = new ExtensionRepositoryService(Http(
            $$"""{ "schema": 1, "name": "forge", "packages": [{ "id": "a", "version": "1.0.0", "url": "https://example.test/a.zip", "sha256": "{{sha}}" }] }"""));

        var index = await service.FetchIndexAsync("https://example.test/repo.json", CancellationToken.None);

        Assert.Equal("forge", index.Name);
        var package = Assert.Single(index.Packages);
        Assert.Equal("a", package.Id);
        Assert.Equal(sha, package.Sha256);
    }

    [Fact]
    public async Task FetchIndexAsync_rejects_a_duplicate_package_id()
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData("zip"u8));
        var first = $$"""{ "id": "a", "version": "1.0.0", "url": "https://example.test/a-1.zip", "sha256": "{{sha}}" }""";
        var second = $$"""{ "id": "a", "version": "2.0.0", "url": "https://example.test/a-2.zip", "sha256": "{{sha}}" }""";
        using var service = new ExtensionRepositoryService(
            Http($$"""{ "schema": 1, "packages": [{{first}}, {{second}}] }"""));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.FetchIndexAsync("https://example.test/repo.json", CancellationToken.None));

        Assert.Contains("'a' twice", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchIndexAsync_rejects_an_index_without_a_package_list()
    {
        using var service = new ExtensionRepositoryService(Http("""{ "schema": 1, "name": "forge" }"""));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.FetchIndexAsync("https://example.test/repo.json", CancellationToken.None));

        Assert.Contains("no package list", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchIndexAsync_reads_an_index_with_no_packages_as_an_empty_repository()
    {
        using var service = new ExtensionRepositoryService(Http("""{ "schema": 1, "name": "empty", "packages": [] }"""));

        var index = await service.FetchIndexAsync("https://example.test/repo.json", CancellationToken.None);

        // A repository that really has nothing is not the same answer as a broken index; only the latter throws.
        Assert.Equal("empty", index.Name);
        Assert.Empty(index.Packages);
    }

    [Fact]
    public async Task FetchIndexAsync_rejects_an_index_larger_than_the_download_limit()
    {
        // The bound is enforced by reading, so this test really streams MaxDownloadBytes+1 KiB through the reader.
        using var service = new ExtensionRepositoryService(Http(new RepeatingContent(ExtensionRepositoryService.MaxDownloadBytes + 1024)));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.FetchIndexAsync("https://example.test/repo.json", CancellationToken.None));

        Assert.Contains("larger than 128 MB", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchIndexAsync_accepts_an_uppercase_sha256_and_normalizes_it()
    {
        var sha = Convert.ToHexString(SHA256.HashData("zip"u8)).ToUpperInvariant();
        Assert.NotEqual(sha, sha.ToLowerInvariant());
        using var service = new ExtensionRepositoryService(Http(
            $$"""{ "schema": 1, "packages": [{ "id": "a", "version": "1.0.0", "url": "https://example.test/a.zip", "sha256": "{{sha}}" }] }"""));

        var index = await service.FetchIndexAsync("https://example.test/repo.json", CancellationToken.None);

        Assert.Equal(sha.ToLowerInvariant(), Assert.Single(index.Packages).Sha256);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(65)]
    public async Task FetchIndexAsync_rejects_a_sha256_that_is_not_64_hex_digits(int length)
    {
        var sha = new string('a', length);
        using var service = new ExtensionRepositoryService(Http(
            $$"""{ "schema": 1, "packages": [{ "id": "a", "version": "1.0.0", "url": "https://example.test/a.zip", "sha256": "{{sha}}" }] }"""));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.FetchIndexAsync("https://example.test/repo.json", CancellationToken.None));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchIndexAsync_rejects_an_index_with_more_packages_than_the_entry_limit()
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData("zip"u8));
        var packages = Enumerable.Range(0, ExtensionRepositoryService.MaxIndexPackages + 1)
            .Select(index => $$"""{ "id": "p{{index}}", "version": "1.0.0", "url": "https://example.test/{{index}}.zip", "sha256": "{{sha}}" }""");
        using var service = new ExtensionRepositoryService(Http(
            $$"""{ "schema": 1, "packages": [{{string.Join(",", packages)}}] }"""));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.FetchIndexAsync("https://example.test/repo.json", CancellationToken.None));

        // Every row becomes a settings row and a validated entry; the bound keeps a hostile index from turning one fetch
        // into unbounded work. One above the limit is already refused, so the boundary is the constant and not "huge".
        Assert.Contains(
            $"{ExtensionRepositoryService.MaxIndexPackages + 1} packages",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAndExtract_verifies_the_hash_case_insensitively()
    {
        var zip = MakeZip(("manifest.json", "{}"));
        using var service = new ExtensionRepositoryService(Http(zip));
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            // Indices are written by hand and by scripts: an uppercase digest names the same bytes.
            var package = new RepositoryPackage(
                "a", "1.0.0", "plugin", "", "", "https://example.test/a.zip",
                Convert.ToHexString(SHA256.HashData(zip)), zip.Length, "", "");

            var extracted = await service.DownloadAndExtractAsync(package, root, null, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(extracted, "manifest.json")));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task DownloadAndExtract_rejects_a_declared_length_over_the_limit_before_reading_the_body()
    {
        using var service = new ExtensionRepositoryService(Http(new UnreadableContent(ExtensionRepositoryService.MaxDownloadBytes + 1)));
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            var package = new RepositoryPackage(
                "a", "1.0.0", "plugin", "", "", "https://example.test/a.zip",
                new string('a', 64), 0, "", "");

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.DownloadAndExtractAsync(package, root, null, CancellationToken.None));

            Assert.Contains("larger than the 128 MB limit", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task DownloadAndExtract_round_trips_a_valid_zip()
    {
        var zip = MakeZip(("manifest.json", """{ "id": "a", "version": "1.0.0", "kind": "plugin", "source": "class P {}" }"""));
        var sha = Convert.ToHexStringLower(SHA256.HashData(zip));
        using var service = new ExtensionRepositoryService(Http(zip));
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            var package = new RepositoryPackage("a", "1.0.0", "plugin", "", "", "https://example.test/a.zip", sha, zip.Length, "", "");

            var extracted = await service.DownloadAndExtractAsync(package, root, null, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(extracted, "manifest.json")));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task DownloadAndExtract_rejects_a_hash_mismatch()
    {
        var zip = MakeZip(("manifest.json", "{}"));
        using var service = new ExtensionRepositoryService(Http(zip));
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            var package = new RepositoryPackage(
                "a", "1.0.0", "plugin", "", "", "https://example.test/a.zip",
                Convert.ToHexStringLower(SHA256.HashData("other"u8)), zip.Length, "", "");

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.DownloadAndExtractAsync(package, root, null, CancellationToken.None));

            Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task ExtractLocalZip_rejects_a_parent_traversal_entry()
    {
        using var service = new ExtensionRepositoryService(Http([]));
        var zipPath = WriteZip(("manifest.json", "{}"), ("../evil.txt", "x"));
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ExtractLocalZipAsync(zipPath, root, CancellationToken.None));

            Assert.Contains("unsafe path", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "evil.txt")));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task ExtractLocalZip_rejects_a_compression_bomb()
    {
        using var service = new ExtensionRepositoryService(Http([]));
        // 10 MiB of zeros compresses to a few KiB: far beyond the 100:1 ratio bound, and under the 16 MiB per-file bound.
        var zipPath = WriteZip(("zeros.bin", new string('\0', 10 * 1024 * 1024)));
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ExtractLocalZipAsync(zipPath, root, CancellationToken.None));

            Assert.Contains(":1", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task Download_cancellation_aborts_and_removes_the_temporary_file()
    {
        // Small and streaming: the cancel lands in the middle of the download itself (the fake delays each read), not
        // after the whole body has been buffered.
        var zip = MakeZipBytes(
            ("manifest.json", "{}"u8.ToArray()),
            ("payload.bin", RandomNumberGenerator.GetBytes(16 * 1024)));
        using var service = new ExtensionRepositoryService(Slow(zip, 40));
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            var package = new RepositoryPackage(
                "a", "1.0.0", "plugin", "", "", "https://example.test/a.zip",
                Convert.ToHexStringLower(SHA256.HashData(zip)), zip.Length, "", "");
            using var cts = new CancellationTokenSource();
            var task = service.DownloadAndExtractAsync(package, root, null, cts.Token);
            cts.CancelAfter(120);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task ExtractLocalZip_returns_an_empty_directory_for_a_package_without_entries()
    {
        using var service = new ExtensionRepositoryService(Http([]));
        var zipPath = WriteZip();
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            var extracted = await service.ExtractLocalZipAsync(zipPath, root, CancellationToken.None);

            // An empty package is not this service's rejection to make: the host refuses a package without a manifest,
            // with a message about the manifest rather than a confusing "not a zip" from the extractor.
            Assert.True(Directory.Exists(extracted));
            Assert.Empty(Directory.EnumerateFileSystemEntries(extracted));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task ExtractLocalZip_rejects_a_file_that_is_not_a_zip_and_leaves_nothing_behind()
    {
        using var service = new ExtensionRepositoryService(Http([]));
        var zipPath = Path.Combine(Path.GetTempPath(), "forge-repo-not-a-zip-" + Guid.NewGuid().ToString("N") + ".zip");
        await File.WriteAllBytesAsync(zipPath, Enumerable.Repeat((byte)0x7A, 4096).ToArray());
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ExtractLocalZipAsync(zipPath, root, CancellationToken.None));

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
            File.Delete(zipPath);
        }
    }

    [Theory]
    [InlineData(ExtensionRepositoryService.MaxEntryCount, true)]
    [InlineData(ExtensionRepositoryService.MaxEntryCount + 1, false)]
    public async Task ExtractLocalZip_accepts_exactly_the_entry_count_limit(int entryCount, bool accepted)
    {
        using var service = new ExtensionRepositoryService(Http([]));
        var zipPath = WriteZipBytes(Enumerable.Range(0, entryCount)
            .Select(index => ($"e{index:D5}.txt", Array.Empty<byte>()))
            .ToArray());
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            if (accepted)
            {
                var extracted = await service.ExtractLocalZipAsync(zipPath, root, CancellationToken.None);
                Assert.Equal(entryCount, Directory.EnumerateFiles(extracted).Count());
            }
            else
            {
                var exception = await Assert.ThrowsAsync<InvalidDataException>(
                    () => service.ExtractLocalZipAsync(zipPath, root, CancellationToken.None));

                Assert.Contains("4097 entries", exception.Message, StringComparison.Ordinal);
                Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            }
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
            File.Delete(zipPath);
        }
    }

    [Theory]
    [InlineData(ExtensionRepositoryService.MaxEntryBytes, true)]
    [InlineData(ExtensionRepositoryService.MaxEntryBytes + 1, false)]
    public async Task ExtractLocalZip_accepts_exactly_the_per_file_limit(long size, bool accepted)
    {
        using var service = new ExtensionRepositoryService(Http([]));
        // Incompressible so the compression-ratio bound is not the one that fires here.
        var zipPath = WriteZipBytes(("big.bin", RandomNumberGenerator.GetBytes((int)size)));
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            if (accepted)
            {
                var extracted = await service.ExtractLocalZipAsync(zipPath, root, CancellationToken.None);
                Assert.Equal(size, new FileInfo(Path.Combine(extracted, "big.bin")).Length);
            }
            else
            {
                var exception = await Assert.ThrowsAsync<InvalidDataException>(
                    () => service.ExtractLocalZipAsync(zipPath, root, CancellationToken.None));

                Assert.Contains("larger than the 16 MB per-file limit", exception.Message, StringComparison.Ordinal);
                Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            }
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
            File.Delete(zipPath);
        }
    }

    [Theory]
    [InlineData(ExtensionRepositoryService.MaxCompressionRatio, true)]
    [InlineData(ExtensionRepositoryService.MaxCompressionRatio + 1, false)]
    public async Task ExtractLocalZip_accepts_exactly_the_compression_ratio_limit(int ratio, bool accepted)
    {
        using var service = new ExtensionRepositoryService(Http([]));
        var (zip, length) = ZipWithCompressionRatio(ratio);
        var zipPath = Path.Combine(Path.GetTempPath(), "forge-repo-ratio-" + Guid.NewGuid().ToString("N") + ".zip");
        await File.WriteAllBytesAsync(zipPath, zip);
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            if (accepted)
            {
                var extracted = await service.ExtractLocalZipAsync(zipPath, root, CancellationToken.None);
                Assert.Equal(length, new FileInfo(Path.Combine(extracted, "zeros.bin")).Length);
            }
            else
            {
                var exception = await Assert.ThrowsAsync<InvalidDataException>(
                    () => service.ExtractLocalZipAsync(zipPath, root, CancellationToken.None));

                Assert.Contains(":1", exception.Message, StringComparison.Ordinal);
                Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            }
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task ExtractLocalZip_rejects_an_entry_whose_declared_size_lies()
    {
        using var service = new ExtensionRepositoryService(Http([]));
        // 1 MiB of zeros with a central directory that claims 1000 bytes: every size bound reads the lie, so the
        // extractor must still refuse the entry instead of writing a megabyte it never accounted for.
        var zipPath = WriteZipBytes(("zeros.bin", new byte[1024 * 1024]));
        var bytes = await File.ReadAllBytesAsync(zipPath);
        await File.WriteAllBytesAsync(zipPath, PatchFirstCentralDirectoryUncompressedSize(bytes, 1000));
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ExtractLocalZipAsync(zipPath, root, CancellationToken.None));

            Assert.Contains("CRC32", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task ExtractLocalZip_refuses_a_package_whose_entries_expand_past_the_total_limit()
    {
        using var service = new ExtensionRepositoryService(Http([]));
        // Five declared-honest 15 MiB entries: no single one breaks the per-file bound or the ratio bound (stored, so
        // 1:1), only their sum does. The limit is therefore enforced from the bytes actually written during the copy, not
        // from a total of the archive's own size fields - the fifth entry is refused in the middle of its copy.
        var payload = new byte[15 * 1024 * 1024];
        var zipPath = WriteStoredZipBytes(Enumerable.Range(0, 5)
            .Select(index => ($"part{index}.bin", payload))
            .ToArray());
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ExtractLocalZipAsync(zipPath, root, CancellationToken.None));

            Assert.Contains("64 MB limit", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task ExtractLocalZip_refuses_a_package_that_lies_about_an_entry_size()
    {
        using var service = new ExtensionRepositoryService(Http([]));
        // The central directory claims 1000 bytes for an entry that really holds 17 MiB. Every size bound in the
        // extractor reads that claim, so the bytes themselves are the only witness: the entry has to be refused with
        // nothing left on disk. (Two independent guards cover it - the copy loop counts the bytes it actually receives,
        // and the archive reader validates the entry's size and CRC against that claim before it hands any byte over -
        // so this asserts the outcome, not which one spoke first.)
        var zipPath = WriteZipBytes(("zeros.bin", new byte[17 * 1024 * 1024]));
        var bytes = await File.ReadAllBytesAsync(zipPath);
        await File.WriteAllBytesAsync(zipPath, PatchCentralDirectoryUncompressedSizes(bytes, 1000));
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ExtractLocalZipAsync(zipPath, root, CancellationToken.None));

            // The refusal lands before the oversized bytes are kept: the partial extraction is removed with the rest.
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
            File.Delete(zipPath);
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../../evil")]
    [InlineData("..\\..\\evil")]
    [InlineData("C:\\evil")]
    public async Task DownloadAndExtract_never_turns_the_index_id_into_a_path_segment(string id)
    {
        var zip = MakeZip(("manifest.json", "{}"));
        using var service = new ExtensionRepositoryService(Http(zip));
        var root = TestTempFiles.NewDirectory("forge-repo");
        var sibling = Path.Combine(Path.GetDirectoryName(root)!, "evil");
        try
        {
            // The id is display data written by whoever published the index: it names the package in the UI, it never
            // names a directory. The extraction directory is a fresh GUID, so a hostile id cannot escape the root.
            var package = new RepositoryPackage(
                id, "1.0.0", "plugin", "", "", "https://example.test/a.zip",
                Convert.ToHexStringLower(SHA256.HashData(zip)), zip.Length, "", "");

            var extracted = await service.DownloadAndExtractAsync(package, root, null, CancellationToken.None);

            Assert.Equal(
                Path.GetFullPath(root),
                Path.GetFullPath(Path.GetDirectoryName(extracted)!),
                StringComparer.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(extracted, "manifest.json")));
            Assert.False(Directory.Exists(sibling));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task ExtractLocalZip_accepts_a_package_that_carries_explicit_directory_entries()
    {
        using var service = new ExtensionRepositoryService(Http([]));
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)))
            {
                writer.Write("{}");
            }

            archive.CreateEntry("lib/");
            using (var writer = new StreamWriter(archive.CreateEntry("lib/plugin.dll").Open(), new UTF8Encoding(false)))
            {
                writer.Write("payload");
            }
        }

        var zipPath = Path.Combine(Path.GetTempPath(), "forge-repo-dirs-" + Guid.NewGuid().ToString("N") + ".zip");
        await File.WriteAllBytesAsync(zipPath, buffer.ToArray());
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            var extracted = await service.ExtractLocalZipAsync(zipPath, root, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(extracted, "lib", "plugin.dll")));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task Cancelling_during_extraction_removes_the_download_and_the_partial_directory()
    {
        // Many small entries make the unpack long enough to be cancelled halfway, deterministically: the test waits for
        // the first extracted file instead of sleeping.
        var zip = MakeZipBytes(Enumerable.Range(0, ExtensionRepositoryService.MaxEntryCount)
            .Select(index => ($"e{index:D5}.bin", new byte[64]))
            .ToArray());
        using var service = new ExtensionRepositoryService(Http(zip));
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            var package = new RepositoryPackage(
                "a", "1.0.0", "plugin", "", "", "https://example.test/a.zip",
                Convert.ToHexStringLower(SHA256.HashData(zip)), zip.Length, "", "");
            using var cts = new CancellationTokenSource();
            var task = service.DownloadAndExtractAsync(package, root, null, cts.Token);

            await WaitForFirstExtractedFileAsync(root, cts);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task Two_installs_of_the_same_package_use_separate_directories()
    {
        var zip = MakeZipBytes(("manifest.json", "{}"u8.ToArray()), ("payload.bin", RandomNumberGenerator.GetBytes(4096)));
        using var service = new ExtensionRepositoryService(Http(zip));
        var root = TestTempFiles.NewDirectory("forge-repo");
        try
        {
            var package = new RepositoryPackage(
                "a", "1.0.0", "plugin", "", "", "https://example.test/a.zip",
                Convert.ToHexStringLower(SHA256.HashData(zip)), zip.Length, "", "");

            // Two clicks on one row (or two rows of the same id) must not share a temp file or pay each other's cleanup.
            var both = await Task.WhenAll(
                service.DownloadAndExtractAsync(package, root, null, CancellationToken.None),
                service.DownloadAndExtractAsync(package, root, null, CancellationToken.None));

            Assert.Equal(2, both.Distinct(StringComparer.Ordinal).Count());
            foreach (var extracted in both)
            {
                Assert.Equal(4096, new FileInfo(Path.Combine(extracted, "payload.bin")).Length);
            }

            Assert.Equal(2, Directory.EnumerateFileSystemEntries(root).Count());
            Assert.DoesNotContain(".download-", string.Join('|', Directory.EnumerateFileSystemEntries(root)), StringComparison.Ordinal);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    private static HttpClient Http(byte[] body) => new(new FixedHandler(body)) { Timeout = Timeout.InfiniteTimeSpan };

    private static HttpClient Http(HttpContent content) =>
        new(new FixedHandler(content)) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Waits until the extractor has written at least one file under <paramref name="root"/>, then cancels.</summary>
    private static async Task WaitForFirstExtractedFileAsync(string root, CancellationTokenSource cts)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Directory.Exists(root) &&
                Directory.EnumerateDirectories(root).Any(directory => Directory.EnumerateFiles(directory).Any()))
            {
                cts.Cancel();
                return;
            }

            await Task.Delay(2);
        }

        throw new TimeoutException("the extractor never wrote a file, so cancellation could not be exercised halfway");
    }

    /// <summary>
    /// Builds a zip with one real (CRC-correct) entry whose <paramref name="ratio"/> uncompressed:compressed ratio is
    /// exact. Deflate's compressed size for a given payload length is implementation detail, so the length is searched;
    /// the windows for 100:1 and 101:1 sit around 1 KB of zeros.
    /// </summary>
    private static (byte[] Zip, int Length) ZipWithCompressionRatio(int ratio)
    {
        for (var length = 512; length <= 8192; length++)
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("zeros.bin");
                using var output = entry.Open();
                output.Write(new byte[length], 0, length);
            }

            var zip = buffer.ToArray();
            using var read = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var entryRead = read.Entries[0];
            if (entryRead.Length / entryRead.CompressedLength == ratio)
            {
                return (zip, length);
            }
        }

        throw new InvalidOperationException($"no payload length compresses to exactly {ratio}:1 in this runtime");
    }

    /// <summary>Rewrites the uncompressed size of the first central-directory record, leaving the deflate payload alone.</summary>
    private static byte[] PatchFirstCentralDirectoryUncompressedSize(byte[] zip, uint uncompressedSize)
    {
        var patched = (byte[])zip.Clone();
        for (var index = 0; index + 46 <= patched.Length; index++)
        {
            if (patched[index] != 0x50 || patched[index + 1] != 0x4B ||
                patched[index + 2] != 0x01 || patched[index + 3] != 0x02)
            {
                continue;
            }

            BitConverter.TryWriteBytes(patched.AsSpan(index + 24, 4), uncompressedSize);
            return patched;
        }

        throw new InvalidOperationException("the zip has no central directory record");
    }

    /// <summary>Rewrites the uncompressed size of every central-directory record, leaving the deflate payloads alone.</summary>
    private static byte[] PatchCentralDirectoryUncompressedSizes(byte[] zip, uint uncompressedSize)
    {
        var patched = (byte[])zip.Clone();
        var records = 0;
        for (var index = 0; index + 46 <= patched.Length; index++)
        {
            if (patched[index] != 0x50 || patched[index + 1] != 0x4B ||
                patched[index + 2] != 0x01 || patched[index + 3] != 0x02)
            {
                continue;
            }

            BitConverter.TryWriteBytes(patched.AsSpan(index + 24, 4), uncompressedSize);
            records++;
        }

        if (records == 0)
        {
            throw new InvalidOperationException("the zip has no central directory record");
        }

        return patched;
    }

    private static HttpClient Http(string json) => Http(Encoding.UTF8.GetBytes(json));

    private static HttpClient Slow(byte[] body, int delayMilliseconds) =>
        new(new FixedHandler(body, delayMilliseconds)) { Timeout = Timeout.InfiniteTimeSpan };

    private static byte[] MakeZip(params (string Name, string Content)[] entries)
    {
        var path = WriteZip(entries);
        try
        {
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] MakeZipBytes(params (string Name, byte[] Content)[] entries)
    {
        var path = WriteZipBytes(entries);
        try
        {
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteZip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "forge-repo-" + Guid.NewGuid().ToString("N") + ".zip");
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        return path;
    }

    private static string WriteZipBytes(params (string Name, byte[] Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "forge-repo-" + Guid.NewGuid().ToString("N") + ".zip");
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var output = entry.Open();
            output.Write(content, 0, content.Length);
        }

        return path;
    }

    /// <summary>The same zip with every entry stored instead of deflated, so a test can build an archive whose declared
    /// total is large without paying for compression and without tripping the ratio bound.</summary>
    private static string WriteStoredZipBytes(params (string Name, byte[] Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "forge-repo-" + Guid.NewGuid().ToString("N") + ".zip");
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using var output = entry.Open();
            output.Write(content, 0, content.Length);
        }

        return path;
    }

    private sealed class FixedHandler : HttpMessageHandler
    {
        private readonly Func<HttpContent> content;

        public FixedHandler(byte[] body, int delayMilliseconds = 0)
            : this(() => delayMilliseconds > 0 ? new SlowContent(body, delayMilliseconds) : new ByteArrayContent(body))
        {
        }

        public FixedHandler(HttpContent content) => this.content = () => content;

        private FixedHandler(Func<HttpContent> content) => this.content = content;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });
    }

    /// <summary>An index that is nothing but padding: the reader has to stop at the bound instead of buffering it all.</summary>
    private sealed class RepeatingContent(long bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = bytes;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            var chunk = new byte[16 * 1024];
            System.Text.Encoding.UTF8.GetBytes("{\"schema\":1", chunk);
            long written = 0;
            while (written < bytes)
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(chunk.Length, bytes - written);
                await stream.WriteAsync(chunk.AsMemory(0, count), ct).ConfigureAwait(false);
                written += count;
            }
        }
    }

    /// <summary>A body that must never be touched: the declared length alone has to stop the download.</summary>
    private sealed class UnreadableContent(long declaredBytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = declaredBytes;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("the body was read even though its declared length exceeds the limit");

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) =>
            throw new InvalidOperationException("the body was read even though its declared length exceeds the limit");
    }

    /// <summary>
    /// A body that arrives in delayed 4 KiB chunks, so a cancel can land in the middle of a download. It is a real
    /// stream: the delay happens on the read the downloader performs. (A plain <see cref="HttpContent"/> would be
    /// buffered to completion before the downloader ever sees a byte, which made this the slowest test in the suite and
    /// put the cancel at the extractor instead of the download.)
    /// </summary>
    private sealed class SlowContent(byte[] body, int delayMilliseconds) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new SlowStream(body, delayMilliseconds));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            using var slow = new SlowStream(body, delayMilliseconds);
            await slow.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return true;
        }
    }

    /// <summary>Yields <see cref="ChunkSize"/> bytes, then waits <paramref name="delayMilliseconds"/> before the next
    /// read; the wait observes the token, so a cancelled download stops at the read it cancelled.</summary>
    private sealed class SlowStream(byte[] body, int delayMilliseconds) : Stream
    {
        public const int ChunkSize = 4096;

        private int offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => body.Length;

        public override long Position
        {
            get => offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("the downloader reads asynchronously");

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (offset >= body.Length)
            {
                return 0;
            }

            await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            var count = Math.Min(Math.Min(ChunkSize, buffer.Length), body.Length - offset);
            body.AsSpan(offset, count).CopyTo(buffer.Span);
            offset += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
