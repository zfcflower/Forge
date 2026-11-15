using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.App.Composition;
using Forge.Plugin.Sdk;

namespace Forge.App.Services;

/// <summary>Whether an available package version compares against the installed one.</summary>
public enum VersionComparison
{
    /// <summary>The installed version is lower; an update is available.</summary>
    Older,
    Same,
    Newer,
    /// <summary>Either version is not a comparable <see cref="Version"/>.</summary>
    Incomparable,
}

/// <summary>One progress snapshot of a download; the UI reads it, it never blocks the download thread.</summary>
internal readonly record struct RepositoryDownloadProgress(long BytesReceived, long? TotalBytes);

/// <summary>One package of a repository index after validation.</summary>
internal sealed record RepositoryPackage(
    string Id,
    string Version,
    string Kind,
    string Title,
    string Description,
    string Url,
    string Sha256,
    long SizeBytes,
    string SdkVersion,
    string PublishedAt);

/// <summary>A repository index whose schema this app understands.</summary>
internal sealed record RepositoryIndex(string Name, IReadOnlyList<RepositoryPackage> Packages);

/// <summary>
/// The extension repository (wave W10a): fetch the static <c>repo.json</c> index, compare versions with
/// <see cref="Version"/>, download a package with progress and cancellation, verify its SHA-256, and extract it into the
/// agreed installation temp root with the zip-safety bounds. All IO runs on the caller's thread (the settings section runs
/// it in the background); the UI only receives progress snapshots and the final extracted directory.
/// </summary>
internal sealed class ExtensionRepositoryService : IDisposable
{
    /// <summary>Zip safety bounds (internal constants, not configuration).</summary>
    public const long MaxEntryBytes = 16 * 1024 * 1024;
    public const long MaxTotalBytes = 64 * 1024 * 1024;
    public const int MaxEntryCount = 4096;
    public const int MaxCompressionRatio = 100;

    /// <summary>Largest downloaded zip the service accepts, before and independent of the index's declared size.</summary>
    public const long MaxDownloadBytes = MaxTotalBytes * 2;

    /// <summary>
    /// Entry bound of a repository index. The index is a hand-written file read into memory and then rendered as one row
    /// per package, so an index that names hundreds of thousands of packages is either corrupt or hostile: 10,000 is far
    /// above any real repository (and above the 4,096 entries a single package may carry) while keeping the validation
    /// and the settings list bounded.
    /// </summary>
    public const int MaxIndexPackages = 10_000;

    /// <summary>Size of the buffer an entry is copied through while its real length is counted.</summary>
    private const int CopyBufferSize = 64 * 1024;

    private const int DownloadBufferSize = 64 * 1024;

    /// <summary>The one HTTP client of the service; the service owns it whoever created it (see the constructor).</summary>
    private readonly HttpClient http;

    /// <summary>
    /// Takes the client the repository talks through and owns it: the composition root builds it (so it is routed through
    /// the one network stack) and the repository service disposes it with the settings section. Passing null creates the
    /// standalone default, which the service disposes the same way. The client must carry no whole-response timeout
    /// (<see cref="Timeout.InfiniteTimeSpan"/>): a download is bounded by the user's cancel and by
    /// <see cref="MaxDownloadBytes"/>, not by a timer.
    /// </summary>
    public ExtensionRepositoryService(HttpClient? http = null) => this.http = http ?? CreateHttpClient();

    /// <summary>The directory the host accepts extracted packages from: the host data directory plus the agreed name.</summary>
    public static string DefaultInstallTempRoot() =>
        Path.Combine(AppPaths.Create().HostDataDirectory, ExtensionInstallPaths.TempDirectoryName);

    /// <summary>
    /// Empties the installation temp root. A run that was killed while downloading or extracting leaves directories the
    /// host will never accept (they are not under an app-owned lifetime), so the section clears the root once when it is
    /// built at startup. Best effort: a locked leftover is retried by the next start, never reported as an error.
    /// </summary>
    public static void ClearInstallTempRoot() => TryDeleteDirectory(DefaultInstallTempRoot());

    public static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    public void Dispose() => http.Dispose();

    /// <summary>
    /// Compares two dotted version strings by their numeric <see cref="Version"/> fields. A version that does not parse is
    /// <see cref="VersionComparison.Incomparable"/> rather than "newer", so the UI never prompts for an update it cannot
    /// order.
    /// </summary>
    public static VersionComparison CompareVersions(string? installed, string? available)
    {
        if (!Version.TryParse(installed, out var installedVersion) ||
            !Version.TryParse(available, out var availableVersion))
        {
            return VersionComparison.Incomparable;
        }

        return installedVersion < availableVersion
            ? VersionComparison.Older
            : installedVersion == availableVersion
                ? VersionComparison.Same
                : VersionComparison.Newer;
    }

    /// <summary>Fetches and validates the repository index. A missing file, an unusable schema or a malformed entry
    /// throws with a message the settings section shows verbatim; it never returns an empty successful index.</summary>
    public async Task<RepositoryIndex> FetchIndexAsync(string indexUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(indexUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidDataException($"'{indexUrl}' is not an http or https repository index URL.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Forge/1.0 (extensions)");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidDataException($"the repository index could not be read: HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd() + $" for {uri}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(stream, MaxDownloadBytes, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException($"the repository index is larger than {MaxDownloadBytes / (1024 * 1024)} MB.");

        RepositoryIndexDocument document;
        try
        {
            document = JsonSerializer.Deserialize(bytes, RepositoryJsonContext.Default.RepositoryIndexDocument)
                ?? throw new JsonException("the document is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"the repository index is not valid JSON: {exception.Message}", exception);
        }

        return ValidateIndex(document);
    }

    /// <summary>
    /// Downloads a package to a temporary file, verifies its SHA-256 against the index and extracts it into a fresh
    /// directory under <paramref name="destinationRoot"/>; the returned path is the extracted package the host installs.
    /// Progress is reported per buffer, cancellation aborts the download and removes the temporary file. The directory
    /// name is a fresh GUID and nothing else: an index id is display data and is never turned into a path segment.
    /// </summary>
    public async Task<string> DownloadAndExtractAsync(
        RepositoryPackage package,
        string destinationRoot,
        IProgress<RepositoryDownloadProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        Directory.CreateDirectory(destinationRoot);
        var tempFile = Path.Combine(destinationRoot, ".download-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await DownloadToAsync(package.Url, package.Sha256, tempFile, progress, ct).ConfigureAwait(false);
            var extracted = Path.Combine(destinationRoot, NewExtractionDirectoryName());
            try
            {
                ExtractZip(tempFile, extracted, ct);
                return extracted;
            }
            catch
            {
                TryDeleteDirectory(extracted);
                throw;
            }
        }
        finally
        {
            TryDeleteFile(tempFile);
        }
    }

    /// <summary>Extracts a local zip (no index, so no SHA-256) into a fresh directory under the destination root. The
    /// directory name is a fresh GUID, so neither the index id nor the picked file name becomes a path segment.</summary>
    public Task<string> ExtractLocalZipAsync(string zipPath, string destinationRoot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        if (!File.Exists(zipPath))
        {
            throw new InvalidDataException($"the package file '{zipPath}' does not exist.");
        }

        Directory.CreateDirectory(destinationRoot);
        var extracted = Path.Combine(destinationRoot, NewExtractionDirectoryName());
        try
        {
            ExtractZip(zipPath, extracted, ct);
            return Task.FromResult(extracted);
        }
        catch
        {
            TryDeleteDirectory(extracted);
            throw;
        }
    }

    /// <summary>Name of one extraction directory: a GUID and a readable prefix, never anything a package or an index
    /// supplied.</summary>
    private static string NewExtractionDirectoryName() => "package-" + Guid.NewGuid().ToString("N");

    private async Task DownloadToAsync(
        string url,
        string expectedSha256,
        string tempFile,
        IProgress<RepositoryDownloadProgress>? progress,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidDataException($"the package URL '{url}' is not http or https.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Forge/1.0 (extensions)");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidDataException($"the package could not be downloaded: HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd() + $" for {uri}.");
        }

        var total = response.Content.Headers.ContentLength is { } length && length >= 0 ? length : (long?)null;
        if (total is { } declared && declared > MaxDownloadBytes)
        {
            throw new InvalidDataException($"the package is {declared} bytes, larger than the {MaxDownloadBytes / (1024 * 1024)} MB limit.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferSize);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(
                tempFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                DownloadBufferSize,
                useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, DownloadBufferSize), ct).ConfigureAwait(false)) > 0)
            {
                received += read;
                if (received > MaxDownloadBytes)
                {
                    throw new InvalidDataException($"the package is larger than the {MaxDownloadBytes / (1024 * 1024)} MB limit.");
                }

                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                progress?.Report(new RepositoryDownloadProgress(received, total));
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"the package SHA-256 is '{actual}', not the '{expectedSha256}' the repository index declared.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads at most <paramref name="limit"/> bytes; null when the stream is larger.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, long limit, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var output = new MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > limit)
                {
                    return null;
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Extracts a zip with the package-format safety bounds: no absolute paths, no <c>..</c>, no symlinks, and per-file,
    /// total, entry-count and compression-ratio limits. A path is normalized with <c>/</c> separators before the checks.
    /// <para>Directory entries. Writers disagree on whether a directory is an entry of its own: Info-ZIP <c>zip -r</c>,
    /// macOS Archive Utility and <c>ZipArchive.CreateEntry("lib/")</c> emit <c>lib/</c>, others emit only the files below
    /// it. A trailing separator is therefore recognized as "this is a directory" <em>before</em> the segment rules run, and
    /// the remaining segments are checked exactly like those of a file.</para>
    /// <para>Limits are counted from the bytes actually inflated, not from the sizes the archive declares: those are
    /// attacker-controlled metadata, and a lying central directory must not get a megabyte written for a claimed kilobyte.
    /// The declared sizes are still checked first, because they reject an honest oversized package before it is unpacked.
    /// </para>
    /// </summary>
    private static void ExtractZip(string zipPath, string destination, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > MaxEntryCount)
        {
            throw new InvalidDataException($"the package has {archive.Entries.Count} entries, more than the {MaxEntryCount} limit.");
        }

        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();

                var relative = entry.FullName.Replace('\\', '/');
                var isDirectory = relative.EndsWith('/');
                if (isDirectory)
                {
                    relative = relative[..^1];
                }

                if (relative.Length == 0)
                {
                    continue; // the archive root itself ("" or "/")
                }

                if (relative.StartsWith('/') || Path.IsPathRooted(relative) || IsDriveQualified(relative))
                {
                    throw new InvalidDataException($"the package contains the absolute path '{entry.FullName}'.");
                }

                foreach (var segment in relative.Split('/'))
                {
                    if (segment.Length == 0 || segment == ".." || segment == ".")
                    {
                        throw new InvalidDataException($"the package contains the unsafe path '{entry.FullName}'.");
                    }
                }

                if (IsSymlink(entry))
                {
                    throw new InvalidDataException($"the package contains a symbolic link '{entry.FullName}'.");
                }

                if (entry.Length > MaxEntryBytes)
                {
                    throw new InvalidDataException(
                        $"'{entry.FullName}' is {entry.Length} bytes, larger than the {MaxEntryBytes / (1024 * 1024)} MB per-file limit.");
                }

                if (entry.Length > 0 && entry.CompressedLength > 0 &&
                    entry.Length / entry.CompressedLength > MaxCompressionRatio)
                {
                    throw new InvalidDataException(
                        $"'{entry.FullName}' expands {entry.Length / Math.Max(entry.CompressedLength, 1)}:1, more than the {MaxCompressionRatio}:1 limit.");
                }

                // Every target is normalized and proven to stay under the extraction root, so no entry can write outside it
                // even if a rule above is ever weakened.
                var target = ResolveTarget(root, relative, entry.FullName);
                if (isDirectory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                long entryBytes = 0;
                using (var input = entry.Open())
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        entryBytes += read;
                        total += read;

                        // Checked before the bytes are written: a package that lies about its sizes stops at the bound
                        // instead of first producing the oversized file.
                        if (entryBytes > MaxEntryBytes)
                        {
                            throw new InvalidDataException(
                                $"'{entry.FullName}' expands to more than the {MaxEntryBytes / (1024 * 1024)} MB per-file limit.");
                        }

                        if (total > MaxTotalBytes)
                        {
                            throw new InvalidDataException(
                                $"the package expands to more than the {MaxTotalBytes / (1024 * 1024)} MB limit.");
                        }

                        output.Write(buffer, 0, read);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>A <c>C:/…</c> style path is not rooted on Unix but is still absolute as far as a Windows host is concerned,
    /// and a package is written on one platform and unpacked on another.</summary>
    private static bool IsDriveQualified(string relative) =>
        relative.Length > 1 && relative[1] == ':' && char.IsAsciiLetter(relative[0]);

    /// <summary>The absolute target of one entry, refused unless it stays strictly inside <paramref name="root"/>.</summary>
    private static string ResolveTarget(string root, string relative, string entryName)
    {
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, comparison))
        {
            throw new InvalidDataException($"the package entry '{entryName}' resolves outside the extraction directory.");
        }

        return target;
    }

    private static bool IsSymlink(ZipArchiveEntry entry)
    {
        // On Unix the zip external attributes carry the file mode in the high 16 bits; a symlink has
        // S_IFLNK == 0120000 octal (0xA000) in the type bits.
        if (!OperatingSystem.IsWindows())
        {
            return ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;
        }

        // Windows zip entries carry DOS attributes in the low bits; the reparse-point bit is the best signal.
        return (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private static RepositoryIndex ValidateIndex(RepositoryIndexDocument document)
    {
        if (document.Schema != 1)
        {
            throw new InvalidDataException($"the repository index has schema {document.Schema}; this app understands schema 1.");
        }

        if (document.Packages is not { } declared)
        {
            throw new InvalidDataException("the repository index has no package list.");
        }

        if (declared.Count > MaxIndexPackages)
        {
            throw new InvalidDataException(
                $"the repository index lists {declared.Count} packages, more than the {MaxIndexPackages} limit.");
        }

        var packages = new List<RepositoryPackage>(declared.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in declared)
        {
            if (package is null)
            {
                throw new InvalidDataException("the repository index holds an empty package entry.");
            }

            if (string.IsNullOrWhiteSpace(package.Id))
            {
                throw new InvalidDataException("the repository index holds a package without an id.");
            }

            if (!seen.Add(package.Id))
            {
                throw new InvalidDataException($"the repository index lists package '{package.Id}' twice.");
            }

            if (string.IsNullOrWhiteSpace(package.Version))
            {
                throw new InvalidDataException($"the repository index package '{package.Id}' has no version.");
            }

            if (!Uri.TryCreate(package.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            {
                throw new InvalidDataException($"the repository index package '{package.Id}' has an unusable URL '{package.Url}'.");
            }

            if (string.IsNullOrWhiteSpace(package.Sha256) || package.Sha256.Length != 64 ||
                !package.Sha256.All(static c => Uri.IsHexDigit(c)))
            {
                throw new InvalidDataException($"the repository index package '{package.Id}' has no usable SHA-256.");
            }

            if (package.SizeBytes < 0)
            {
                throw new InvalidDataException($"the repository index package '{package.Id}' has a negative size.");
            }

            packages.Add(new RepositoryPackage(
                package.Id,
                package.Version,
                NormalizeKind(package.Id, package.Kind),
                package.Title ?? string.Empty,
                package.Description ?? string.Empty,
                package.Url,
                package.Sha256.ToLowerInvariant(),
                package.SizeBytes,
                package.SdkVersion ?? string.Empty,
                package.PublishedAt ?? string.Empty));
        }

        return new RepositoryIndex(document.Name ?? string.Empty, packages);
    }

    private static string NormalizeKind(string packageId, string? kind)
    {
        var declared = kind?.Trim() ?? string.Empty;
        if (declared.Length == 0)
        {
            return "plugin";
        }

        if (string.Equals(declared, "plugin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(declared, "workflow", StringComparison.OrdinalIgnoreCase))
        {
            return declared.ToLowerInvariant();
        }

        throw new InvalidDataException($"the repository index package '{packageId}' has the kind '{declared}'; expected 'plugin' or 'workflow'.");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>On-disk shape of <c>repo.json</c> (schema 1).</summary>
internal sealed class RepositoryIndexDocument
{
    public int Schema { get; set; }

    public string? Name { get; set; }

    public List<RepositoryPackageDocument>? Packages { get; set; }
}

internal sealed class RepositoryPackageDocument
{
    public string? Id { get; set; }

    public string? Version { get; set; }

    public string? Kind { get; set; }

    public string? Title { get; set; }

    public string? Description { get; set; }

    public string? Url { get; set; }

    public string? Sha256 { get; set; }

    public long SizeBytes { get; set; }

    public string? SdkVersion { get; set; }

    public string? PublishedAt { get; set; }
}

/// <summary>The repository index's source-generated context (no reflection, AOT safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RepositoryIndexDocument))]
internal sealed partial class RepositoryJsonContext : JsonSerializerContext;
