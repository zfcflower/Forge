using System.Text;

namespace Forge.Core.Tests.Providers;

/// <summary>Serves queued chunks one read at a time, so callers see the same chunk boundaries a socket would produce.</summary>
internal sealed class ScriptedStream(
    IEnumerable<byte[]> chunks,
    bool hangWhenExhausted = false,
    TimeSpan? delay = null,
    Action? onRead = null) : Stream
{
    private readonly Queue<byte[]> _chunks = new(chunks.Where(chunk => chunk.Length > 0));
    private byte[]? _current;
    private int _offset;

    public bool IsDisposed { get; private set; }

    public long BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public static ScriptedStream FromText(string text, int chunkSize, TimeSpan? delay = null, bool hangWhenExhausted = false) =>
        new(Split(Encoding.UTF8.GetBytes(text), chunkSize), hangWhenExhausted, delay);

    public static IEnumerable<byte[]> Split(byte[] bytes, int chunkSize)
    {
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            yield return bytes[offset..Math.Min(bytes.Length, offset + chunkSize)];
        }
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        onRead?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        if (_current is null || _offset >= _current.Length)
        {
            if (!_chunks.TryDequeue(out _current))
            {
                _current = null;
                if (hangWhenExhausted)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }

                return 0;
            }

            _offset = 0;
        }

        if (delay is { } pause)
        {
            await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
        }

        var take = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsSpan(_offset, take).CopyTo(buffer.Span);
        _offset += take;
        BytesRead += take;
        return take;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            IsDisposed = true;
        }

        base.Dispose(disposing);
    }
}
