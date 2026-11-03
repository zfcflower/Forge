using System.Buffers;
using System.Buffers.Binary;

namespace Forge.Ipc;

internal enum FrameStatus
{
    NeedMore,
    Frame,
    Empty,
    Oversized,
    Fatal,
}

/// <summary>Splits a byte stream into 4-byte little-endian length prefixed JSON bodies.</summary>
internal struct FrameExtractor
{
    public const int HeaderBytes = 4;

    private long discard;

    public FrameStatus TryExtract(
        ref ReadOnlySequence<byte> buffer,
        int maxFrameBytes,
        long maxDiscardBytes,
        out ReadOnlyMemory<byte> body,
        out int declaredLength)
    {
        body = ReadOnlyMemory<byte>.Empty;
        declaredLength = 0;
        while (true)
        {
            if (discard > 0L)
            {
                var take = buffer.Length < discard ? buffer.Length : (int)discard;
                if (take == 0)
                {
                    return FrameStatus.NeedMore;
                }

                buffer = buffer.Slice(take);
                discard -= take;
                continue;
            }

            if (buffer.Length < HeaderBytes)
            {
                return FrameStatus.NeedMore;
            }

            int length;
            if (buffer.FirstSpan.Length >= HeaderBytes)
            {
                length = BinaryPrimitives.ReadInt32LittleEndian(buffer.FirstSpan);
            }
            else
            {
                Span<byte> header = stackalloc byte[HeaderBytes];
                buffer.Slice(0, HeaderBytes).CopyTo(header);
                length = BinaryPrimitives.ReadInt32LittleEndian(header);
            }

            if (length == 0)
            {
                buffer = buffer.Slice(HeaderBytes);
                return FrameStatus.Empty;
            }

            if (length < 0 || length > maxFrameBytes)
            {
                declaredLength = length;
                if (length < 0 || length > maxDiscardBytes)
                {
                    return FrameStatus.Fatal;
                }

                buffer = buffer.Slice(HeaderBytes);
                discard = length;
                return FrameStatus.Oversized;
            }

            if (buffer.Length < HeaderBytes + (long)length)
            {
                return FrameStatus.NeedMore;
            }

            var slice = buffer.Slice(HeaderBytes, length);
            body = slice.IsSingleSegment ? slice.First : BuffersExtensions.ToArray(slice);
            buffer = buffer.Slice(HeaderBytes + length);
            declaredLength = length;
            return FrameStatus.Frame;
        }
    }
}
