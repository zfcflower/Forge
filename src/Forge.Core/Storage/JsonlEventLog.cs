using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Storage;

/// <summary>One line of the per-session event log.</summary>
public sealed record EventLogEntry(long Seq, ChatMessage Message, TokenUsage? Usage, DateTimeOffset At);

/// <summary>
/// Append-only JSONL mirror of a session's messages (<c>&lt;dir&gt;/&lt;sessionId&gt;.jsonl</c>).
/// Writes are buffered per session and flushed on demand or at disposal; <see cref="RecoverAsync"/>
/// replays lines whose sequence numbers never reached SQLite.
/// </summary>
public sealed class JsonlEventLog : IAsyncDisposable, IDisposable
{
    private const int FileBufferSize = 64 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _directory;
    private readonly ConcurrentDictionary<string, Lazy<SessionWriter>> _writers = new(StringComparer.Ordinal);
    private int _disposed;

    public JsonlEventLog(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public string DirectoryPath => _directory;

    public string GetPath(string sessionId) =>
        Path.Combine(_directory, StoragePaths.SafeSegment(sessionId, nameof(sessionId)) + ".jsonl");

    public async Task AppendAsync(
        string sessionId,
        long seq,
        ChatMessage message,
        TokenUsage? usage,
        DateTimeOffset at,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var writer = _writers.GetOrAdd(
            sessionId,
            static (id, state) => new Lazy<SessionWriter>(() => state.Open(id), LazyThreadSafetyMode.ExecutionAndPublication),
            this).Value;
        var bytes = EncodeLine(seq, message, usage, at);
        await writer.AppendAsync(bytes, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<EventLogEntry> ReadAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var path = GetPath(sessionId);
        if (!File.Exists(path))
        {
            yield break;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[FileBufferSize];
        var pending = new ArrayBufferWriter<byte>(1024);
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var start = 0;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != (byte)'\n')
                {
                    continue;
                }

                pending.Write(buffer.AsSpan(start, index - start));
                if (TryParseLine(pending.WrittenSpan, out var entry))
                {
                    yield return entry;
                }

                pending.Clear();
                start = index + 1;
            }

            if (start < read)
            {
                pending.Write(buffer.AsSpan(start, read - start));
            }
        }

        if (pending.WrittenCount > 0 && TryParseLine(pending.WrittenSpan, out var tail))
        {
            yield return tail;
        }
    }

    /// <summary>
    /// Replays log lines above the highest sequence <paramref name="store"/> has persisted. The store
    /// assigns sequence numbers itself, so only such a contiguous tail can be restored; a line that
    /// would land on a different number aborts recovery instead of corrupting the order.
    /// </summary>
    public async Task<int> RecoverAsync(string sessionId, ISessionStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var entries = new List<EventLogEntry>();
        await foreach (var entry in ReadAsync(sessionId, ct).ConfigureAwait(false))
        {
            entries.Add(entry);
        }

        if (entries.Count == 0)
        {
            return 0;
        }

        var lastSeq = await store.LastSeqAsync(sessionId, ct).ConfigureAwait(false);
        entries.Sort(static (left, right) => left.Seq.CompareTo(right.Seq));

        var pending = new List<EventLogEntry>(entries.Count);
        var expected = lastSeq + 1;
        foreach (var entry in entries)
        {
            if (entry.Seq < expected)
            {
                continue;
            }

            if (entry.Seq != expected)
                throw new InvalidOperationException(
                    $"Log entry {entry.Seq} of session '{sessionId}' cannot be recovered: expected {expected}. Only a contiguous tail can be replayed.");

            pending.Add(entry);
            expected++;
        }

        var recovered = 0;
        foreach (var entry in pending)
        {
            ct.ThrowIfCancellationRequested();
            var assigned = await store.AppendAsync(sessionId, entry.Message, entry.Usage, ct).ConfigureAwait(false);
            if (assigned != entry.Seq)
                throw new InvalidOperationException(
                    $"Log entry {entry.Seq} of session '{sessionId}' cannot be recovered: the store assigned {assigned}. Only a contiguous tail can be replayed.");

            lastSeq = entry.Seq;
            recovered++;
        }

        return recovered;
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        foreach (var writer in _writers.Values)
        {
            if (writer.IsValueCreated)
            {
                await writer.Value.FlushAsync(ct).ConfigureAwait(false);
            }
        }
    }

    public async Task FlushAsync(string sessionId, CancellationToken ct)
    {
        if (_writers.TryGetValue(sessionId, out var writer) && writer.IsValueCreated)
        {
            await writer.Value.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var pair in _writers)
        {
            if (_writers.TryRemove(pair.Key, out var writer) && writer.IsValueCreated)
            {
                await writer.Value.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var pair in _writers)
        {
            if (_writers.TryRemove(pair.Key, out var writer) && writer.IsValueCreated)
            {
                writer.Value.Dispose();
            }
        }
    }

    private static byte[] EncodeLine(long seq, ChatMessage message, TokenUsage? usage, DateTimeOffset at)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", seq);
            writer.WriteString("at", StorageDb.FormatTimestamp(at));
            if (usage is not null)
            {
                writer.WritePropertyName("usage");
                writer.WriteRawValue(MessageJson.SerializeUsage(usage)!, skipInputValidation: true);
            }

            writer.WritePropertyName("message");
            MessageJson.WriteMessage(writer, message);
            writer.WriteEndObject();
        }

        var written = buffer.WrittenSpan;
        var line = new byte[written.Length + 1];
        written.CopyTo(line);
        line[^1] = (byte)'\n';
        return line;
    }

    private static bool TryParseLine(ReadOnlySpan<byte> line, out EventLogEntry entry)
    {
        entry = null!;
        var trimmed = line.Trim((byte)'\r');
        if (trimmed.IsEmpty)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed.ToArray());
            var root = document.RootElement;
            if (!root.TryGetProperty("seq", out var seqElement) ||
                !root.TryGetProperty("message", out var messageElement) ||
                !root.TryGetProperty("at", out var atElement))
            {
                return false;
            }

            var usage = root.TryGetProperty("usage", out var usageElement)
                ? MessageJson.ParseUsage(usageElement.GetRawText())
                : null;
            entry = new EventLogEntry(
                seqElement.GetInt64(),
                MessageJson.Deserialize(messageElement.GetRawText()),
                usage,
                StorageDb.ParseTimestamp(atElement.GetString() ?? throw new JsonException("Missing timestamp.")));
            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private SessionWriter Open(string sessionId)
    {
        Directory.CreateDirectory(_directory);
        var path = GetPath(sessionId);
        var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            FileBufferSize,
            FileOptions.Asynchronous);
        try
        {
            TruncateTornTail(stream);
            stream.Seek(0, SeekOrigin.End);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        return new SessionWriter(stream);
    }

    private static void TruncateTornTail(FileStream stream)
    {
        var length = stream.Length;
        if (length == 0)
        {
            return;
        }

        var lineStart = FindLastLineStart(stream, length);
        var tailLength = length - lineStart;
        if (tailLength == 0)
        {
            return;
        }

        if (tailLength <= int.MaxValue && IsCompleteLine(stream, lineStart, (int)tailLength))
        {
            stream.Seek(0, SeekOrigin.End);
            stream.WriteByte((byte)'\n');
            return;
        }

        stream.SetLength(lineStart);
    }

    private static long FindLastLineStart(FileStream stream, long length)
    {
        var buffer = new byte[FileBufferSize];
        var position = length;
        while (position > 0)
        {
            var size = (int)Math.Min(buffer.Length, position);
            position -= size;
            stream.Seek(position, SeekOrigin.Begin);
            stream.ReadExactly(buffer.AsSpan(0, size));
            var newline = buffer.AsSpan(0, size).LastIndexOf((byte)'\n');
            if (newline >= 0)
            {
                return position + newline + 1;
            }
        }

        return 0;
    }

    private static bool IsCompleteLine(FileStream stream, long position, int length)
    {
        var line = new byte[length];
        stream.Seek(position, SeekOrigin.Begin);
        stream.ReadExactly(line);
        return TryParseLine(line, out _);
    }

    private sealed class SessionWriter(FileStream stream) : IAsyncDisposable, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _disposed;

        public async ValueTask AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask FlushAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _gate.Wait();
            try
            {
                stream.Dispose();
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }
    }
}
