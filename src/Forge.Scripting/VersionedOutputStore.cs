using System.Globalization;

namespace Forge.Scripting;

/// <summary>
/// Persists compiled plugin images under <c>&lt;baseDirectory&gt;/&lt;pluginId&gt;/&lt;version&gt;/</c>. Writes are atomic
/// (temp file then move) and an existing output file is never overwritten, so a given version directory always holds
/// the bytes that were first stored there.
/// </summary>
public sealed class VersionedOutputStore
{
    public const string DefaultFileName = "plugin.dll";

    public VersionedOutputStore(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        BaseDirectory = Path.GetFullPath(baseDirectory);
    }

    public string BaseDirectory { get; }

    /// <summary>Version directory name for a hash: its first <paramref name="prefixLength"/> characters.</summary>
    public static string VersionFor(string hash, int prefixLength = 16)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentOutOfRangeException.ThrowIfLessThan(prefixLength, 1);
        return hash.Length <= prefixLength ? hash : hash[..prefixLength];
    }

    public string GetVersionDirectory(string pluginId, string version) =>
        Path.Combine(BaseDirectory, Segment(pluginId, nameof(pluginId)), Segment(version, nameof(version)));

    public string GetOutputPath(string pluginId, string version, string fileName = DefaultFileName) =>
        Path.Combine(GetVersionDirectory(pluginId, version), Segment(fileName, nameof(fileName)));

    public bool Exists(string pluginId, string version, string fileName = DefaultFileName) =>
        File.Exists(GetOutputPath(pluginId, version, fileName));

    /// <summary>Version directory names that exist for a plugin, ordinal ascending. Empty when the plugin has none.</summary>
    public IReadOnlyList<string> ListVersions(string pluginId)
    {
        var directory = Path.Combine(BaseDirectory, Segment(pluginId, nameof(pluginId)));
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var versions = new List<string>();
        foreach (var path in Directory.EnumerateDirectories(directory))
        {
            versions.Add(Path.GetFileName(path));
        }

        versions.Sort(StringComparer.Ordinal);
        return versions.AsReadOnly();
    }

    /// <summary>Writes <paramref name="bytes"/> and returns the output path. If the output file already exists it is
    /// left untouched and returned as is; <paramref name="existing"/> reports that case.</summary>
    public string Save(
        string pluginId,
        string version,
        ReadOnlySpan<byte> bytes,
        out bool existing,
        string fileName = DefaultFileName)
    {
        var target = GetOutputPath(pluginId, version, fileName);
        if (File.Exists(target))
        {
            existing = true;
            return target;
        }

        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            existing = !MoveIntoPlace(temp, target);
            return target;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public string Save(string pluginId, string version, ReadOnlySpan<byte> bytes, string fileName = DefaultFileName) =>
        Save(pluginId, version, bytes, out _, fileName);

    /// <summary>Stores the image of a successful compilation; the version defaults to the cache key prefix.</summary>
    public string Save(
        string pluginId,
        CompilationResult result,
        string? version = null,
        string fileName = DefaultFileName)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Success || result.AssemblyBytes is not { Length: > 0 } bytes)
        {
            throw new InvalidOperationException("Only a successful compilation can be stored.");
        }

        return Save(pluginId, version ?? VersionFor(result.CacheKey), bytes, out _, fileName);
    }

    public async Task<string> SaveAsync(
        string pluginId,
        string version,
        ReadOnlyMemory<byte> bytes,
        string fileName = DefaultFileName,
        CancellationToken ct = default)
    {
        var target = GetOutputPath(pluginId, version, fileName);
        if (File.Exists(target))
        {
            return target;
        }

        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            MoveIntoPlace(temp, target);
            return target;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public Task<string> SaveAsync(
        string pluginId,
        CompilationResult result,
        string? version = null,
        string fileName = DefaultFileName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Success || result.AssemblyBytes is not { Length: > 0 } bytes)
        {
            throw new InvalidOperationException("Only a successful compilation can be stored.");
        }

        return SaveAsync(pluginId, version ?? VersionFor(result.CacheKey), bytes, fileName, ct);
    }

    public bool TryRead(string pluginId, string version, out byte[] bytes, string fileName = DefaultFileName)
    {
        var path = GetOutputPath(pluginId, version, fileName);
        if (!File.Exists(path))
        {
            bytes = [];
            return false;
        }

        bytes = File.ReadAllBytes(path);
        return true;
    }

    /// <summary>Total bytes stored for a plugin, across all versions.</summary>
    public long GetSize(string pluginId)
    {
        var directory = Path.Combine(BaseDirectory, Segment(pluginId, nameof(pluginId)));
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        foreach (var version in ListVersions(pluginId))
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(directory, version)))
            {
                total += new FileInfo(file).Length;
            }
        }

        return total;
    }

    /// <summary>Moves the temp file into place unless a concurrent writer already produced the target.</summary>
    private static bool MoveIntoPlace(string temp, string target)
    {
        try
        {
            File.Move(temp, target, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(target))
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Segment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Path segment must not be empty.", parameterName);
        }

        if (value.Contains("..", StringComparison.Ordinal) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains('/', StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"'{value}' is not a valid path segment."),
                parameterName);
        }

        return value;
    }
}
