using System.Net;
using System.Text;

namespace Forge.App.Tests;

/// <summary>
/// The smallest transport the three SSE adapters can be driven through: a handler that answers from a script, and a
/// response body that serves one pre-rendered frame per read so the adapter's read loop genuinely interleaves awaits
/// (a single in-memory burst would make the streaming time untestable). An optional pause before each read makes the
/// measured time deterministic, and holding the stream open past its last frame lets a test cancel mid-answer.
/// </summary>
internal static class FakeSse
{
    /// <summary>One server-sent event frame: a data line plus the blank line that dispatches it.</summary>
    public static string Frame(string data) => "data: " + data + "\n\n";

    /// <summary>The OpenAI family's terminating frame, which carries no JSON.</summary>
    public static string Done => "data: [DONE]\n\n";

    /// <summary>A streamed success response whose body is the given stream, one frame per read.</summary>
    public static HttpResponseMessage Ok(Stream body) => new(HttpStatusCode.OK)
    {
        Content = new StreamedContent(body),
    };

    /// <summary>A 400 whose body names the mentioned field, the refusal shape the one-shot downgrade reacts to.</summary>
    public static HttpResponseMessage Refused(string mention) => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent($"{{\"error\":{{\"message\":\"Unknown parameter: '{mention}'.\"}}}}", Encoding.UTF8, "application/json"),
    };

    /// <summary>Serves each frame as its own read with the given pause before every one, so the adapter's measured
    /// streaming time is a multiple of the pause rather than a microsecond burst. When <paramref name="holdAtEnd"/> is
    /// set the stream blocks forever once the frames run out (until the caller cancels) instead of ending.</summary>
    public static ChunkedSseStream Body(IReadOnlyList<string> frames, TimeSpan pause = default, bool holdAtEnd = false) =>
        new(frames, pause, holdAtEnd);

    /// <summary>Answers each request from a script keyed by attempt number, counting the attempts it saw.</summary>
    internal sealed class Handler(Func<int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(answer(Attempts));
        }
    }

    internal sealed class ChunkedSseStream(IReadOnlyList<string> frames, TimeSpan pause, bool holdAtEnd) : Stream
    {
        private readonly byte[][] chunks = [.. frames.Select(static frame => Encoding.UTF8.GetBytes(frame))];
        private int frame;
        private int offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (frame >= chunks.Length)
            {
                if (holdAtEnd)
                {
                    // The answer is not finished and never will be: only a cancellation ends this read.
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }

                return 0;
            }

            if (pause > TimeSpan.Zero)
            {
                await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
            }

            var chunk = chunks[frame];
            var take = Math.Min(chunk.Length - offset, buffer.Length);
            chunk.AsSpan(offset, take).CopyTo(buffer.Span);
            offset += take;
            if (offset == chunk.Length)
            {
                frame++;
                offset = 0;
            }

            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Content whose stream is one the test controls; read straight through, never serialized to a buffer.</summary>
    private sealed class StreamedContent(Stream stream) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult(stream);
    }
}
