using System.Buffers;

namespace Forge.Core.Providers;

public readonly record struct SseEvent(string? Event, string Data);

/// <summary>
/// Incremental server-sent-events parser. One <see cref="ReadAsync"/> call yields one dispatched
/// event, so callers can consume a response body without materialising lines.
/// </summary>
public sealed class SseReader
{
    private const int DefaultBufferSize = 8192;
    private const int BomLength = 3;

    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private readonly ArrayBufferWriter<byte> _line;
    private readonly System.Text.StringBuilder _data = new(256);

    private int _offset;
    private int _available;
    private bool _endOfStream;
    private bool _finished;
    private bool _bomChecked;
    private bool _skipLineFeed;
    private bool _hasData;
    private string? _eventName;

    public SseReader(Stream stream, int bufferSize = DefaultBufferSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 128);
        _stream = stream;
        _buffer = new byte[bufferSize];
        _line = new ArrayBufferWriter<byte>(256);
    }

    public async ValueTask<SseEvent?> ReadAsync(CancellationToken ct)
    {
        while (!_finished)
        {
            if (!_bomChecked)
            {
                if (_available - _offset >= BomLength)
                {
                    _bomChecked = true;
                    if (_buffer[_offset] == 0xEF && _buffer[_offset + 1] == 0xBB && _buffer[_offset + 2] == 0xBF)
                    {
                        _offset += BomLength;
                    }

                    continue;
                }

                if (!_endOfStream)
                {
                    await FillAsync(ct).ConfigureAwait(false);
                    continue;
                }

                _bomChecked = true;
                continue;
            }

            if (_offset >= _available)
            {
                if (_endOfStream)
                {
                    _finished = true;
                    SseEvent? flushed = null;
                    if (_line.WrittenCount > 0)
                    {
                        flushed = AcceptLine(_line.WrittenSpan);
                        _line.Clear();
                    }

                    return flushed ?? Dispatch();
                }

                await FillAsync(ct).ConfigureAwait(false);
                continue;
            }

            if (_skipLineFeed)
            {
                _skipLineFeed = false;
                if (_buffer[_offset] == (byte)'\n')
                {
                    _offset++;
                    continue;
                }
            }

            var span = _buffer.AsSpan(_offset, _available - _offset);
            var terminator = span.IndexOfAny((byte)'\r', (byte)'\n');
            if (terminator < 0)
            {
                _line.Write(span);
                _offset = _available;
                continue;
            }

            if (terminator > 0)
            {
                _line.Write(span[..terminator]);
            }

            _offset += terminator + 1;
            _skipLineFeed = span[terminator] == (byte)'\r';
            var dispatched = AcceptLine(_line.WrittenSpan);
            _line.Clear();
            if (dispatched is { } sse)
            {
                return sse;
            }
        }

        return null;
    }

    private async ValueTask FillAsync(CancellationToken ct)
    {
        if (_offset > 0)
        {
            var remaining = _available - _offset;
            if (remaining > 0)
            {
                _buffer.AsSpan(_offset, remaining).CopyTo(_buffer);
            }

            _available = remaining;
            _offset = 0;
        }

        var read = await _stream.ReadAsync(_buffer.AsMemory(_available), ct).ConfigureAwait(false);
        if (read == 0)
        {
            _endOfStream = true;
            return;
        }

        _available += read;
    }

    private SseEvent? AcceptLine(ReadOnlySpan<byte> rawLine)
    {
        if (rawLine.Length > 0 && rawLine[^1] == (byte)'\r')
        {
            rawLine = rawLine[..^1];
        }

        if (rawLine.IsEmpty)
        {
            return Dispatch();
        }

        if (rawLine[0] == (byte)':')
        {
            return null;
        }

        var line = System.Text.Encoding.UTF8.GetString(rawLine);
        var colon = line.IndexOf(':');
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? string.Empty : line[(colon + 1)..];
        if (value.Length > 0 && value[0] == ' ')
        {
            value = value[1..];
        }

        switch (field)
        {
            case "event":
                _eventName = value;
                break;
            case "data":
                if (_hasData)
                {
                    _data.Append('\n');
                }

                _data.Append(value);
                _hasData = true;
                break;
        }

        return null;
    }

    private SseEvent? Dispatch()
    {
        if (!_hasData && _eventName is null)
        {
            return null;
        }

        var evt = new SseEvent(_eventName, _data.ToString());
        _eventName = null;
        _hasData = false;
        _data.Clear();
        return evt;
    }
}
