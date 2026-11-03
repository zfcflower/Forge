using System.Text.Json.Serialization.Metadata;

namespace Forge.IO;

/// <summary>A small JSON state file owned by this application, shared by several processes. Every update is a
/// cross-process-locked read-modify-write on the latest on-disk value, so one process's update never overwrites
/// another's. Not for user workspace files (other programs do not honour the lock). AOT-safe: serialization uses
/// the caller's source-generated <see cref="JsonTypeInfo{T}"/>.</summary>
public sealed class AtomicJsonStore<T> where T : class, new()
{
    public AtomicJsonStore(string path, JsonTypeInfo<T> typeInfo, TimeSpan? lockTimeout = null, AtomicFileOptions? fileOptions = null)
        => throw new NotImplementedException();

    public string Path => throw new NotImplementedException();

    /// <summary>Reads the latest value without locking. A missing file yields a new <typeparamref name="T"/>;
    /// a corrupt file throws <see cref="InvalidDataException"/> naming the path — never treated as empty.</summary>
    public T Read(CancellationToken cancellationToken = default) => throw new NotImplementedException();

    /// <summary>Under the cross-process lock: re-read the latest value, apply <paramref name="mutate"/>, persist
    /// atomically, return the new value. If <paramref name="mutate"/> or the write throws, nothing is written.
    /// Lock acquisition past the timeout throws <see cref="TimeoutException"/>.</summary>
    public T Update(Func<T, T> mutate, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    /// <summary>Cheap change stamp (length + last write UTC) for cache invalidation; null when the file is missing.</summary>
    public (long Length, DateTime LastWriteUtc)? Stamp() => throw new NotImplementedException();
}
