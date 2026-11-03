using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Forge.IO;

/// <summary>A small JSON state file owned by this application, shared by several processes. Every update is a
/// cross-process-locked read-modify-write on the latest on-disk value, so one process's update never overwrites
/// another's. Not for user workspace files (other programs do not honour the lock). AOT-safe: serialization uses
/// the caller's source-generated <see cref="JsonTypeInfo{T}"/>.</summary>
public sealed class AtomicJsonStore<T> where T : class, new()
{
    private const string LockSuffix = ".lock";

    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(10);

    private readonly string path;
    private readonly string lockPath;
    private readonly JsonTypeInfo<T> typeInfo;
    private readonly TimeSpan lockTimeout;
    private readonly AtomicFileOptions fileOptions;

    public AtomicJsonStore(string path, JsonTypeInfo<T> typeInfo, TimeSpan? lockTimeout = null, AtomicFileOptions? fileOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        var fullPath = System.IO.Path.GetFullPath(path);
        this.path = fullPath;
        lockPath = fullPath + LockSuffix;
        this.typeInfo = typeInfo;
        this.lockTimeout = lockTimeout ?? DefaultLockTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(this.lockTimeout, TimeSpan.Zero, nameof(lockTimeout));
        this.fileOptions = fileOptions ?? new AtomicFileOptions();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(this.fileOptions.RetryBudget, TimeSpan.Zero, nameof(fileOptions));
    }

    public string Path => path;

    /// <summary>Reads the latest value without locking. A missing file yields a new <typeparamref name="T"/>;
    /// a corrupt file throws <see cref="InvalidDataException"/> naming the path — never treated as empty.</summary>
    public T Read(CancellationToken cancellationToken = default)
    {
        var content = AtomicFile.ReadAllBytesShared(path, fileOptions, cancellationToken);
        if (content is null || content.Length == 0)
        {
            return new T();
        }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize(content, typeInfo);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"State file '{path}' is not a valid {typeInfo.Type.Name} document.", exception);
        }

        return value ?? throw new InvalidDataException($"State file '{path}' contains no {typeInfo.Type.Name} value.");
    }

    /// <summary>Under the cross-process lock: re-read the latest value, apply <paramref name="mutate"/>, persist
    /// atomically, return the new value. If <paramref name="mutate"/> or the write throws, nothing is written.
    /// Lock acquisition past the timeout throws <see cref="TimeoutException"/>.</summary>
    public T Update(Func<T, T> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        using var held = FileLock.Acquire(lockPath, lockTimeout, cancellationToken);
        var current = Read(cancellationToken);
        var updated = mutate(current) ?? throw new InvalidOperationException($"The mutation of '{path}' returned null.");
        var content = JsonSerializer.SerializeToUtf8Bytes(updated, typeInfo);
        AtomicFile.WriteAllBytes(path, content, fileOptions, cancellationToken);
        return updated;
    }

    /// <summary>Cheap change stamp (length + last write UTC) for cache invalidation; null when the file is missing.</summary>
    public (long Length, DateTime LastWriteUtc)? Stamp()
    {
        var info = new FileInfo(path);
        try
        {
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
