using System.Reflection;
using System.Text;
using Forge.Abstractions;

namespace Forge.Core.Providers;

#if DEBUG

/// <summary>
/// Debug builds only: writes one file per model request holding the bytes that went out and the bytes that came back, exactly
/// as they crossed the wire — request line, headers, body, then the response's status line, headers and raw body. The files go
/// to <c>.log</c> under the process's working directory, which the repository ignores; there is no switch, because a Debug build
/// exists to be diagnosed.
/// <para>
/// It exists because the interesting failures are never "the call took long": they are "we sent this field, twice, with the
/// wrong cache breakpoint". Nothing above the adapter can answer that — the body only exists inside
/// <see cref="HttpChatProvider.StreamAsync"/> — and nothing in a Release build should. So the capture is compiled out
/// (<c>#if DEBUG</c> around this file and its call sites) rather than switched off at run time.
/// </para>
/// </summary>
internal sealed class WireDump : IDisposable
{
    private static readonly string? Root = ResolveRoot();
    private static int sequence;

    private readonly FileStream stream;
    private bool attached;
    private bool closed;
    private bool dead;

    private WireDump(FileStream stream) => this.stream = stream;

    /// <summary>Starts one request's file, or returns null when this build is not capturing.</summary>
    public static WireDump? Start(
        HttpRequestMessage message,
        byte[] body,
        string providerId,
        int attempt,
        bool downgraded)
    {
        if (Root is null)
        {
            return null;
        }

        FileStream? stream = null;
        string? path = null;
        try
        {
            var number = Interlocked.Increment(ref sequence);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd'-'HHmmss'-'fff", System.Globalization.CultureInfo.InvariantCulture);
            path = Path.Combine(Root, $"{stamp}-{number:0000}-{providerId}-a{attempt}.http");
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
            Write(stream, $"# {providerId} attempt {attempt} downgraded={downgraded}\n");
            Write(stream, $"{message.Method} {message.RequestUri} HTTP/1.1\n");
            foreach (var header in message.Headers)
            {
                Write(stream, $"{header.Key}: {string.Join(", ", header.Value)}\n");
            }

            if (message.Content is { } content)
            {
                foreach (var header in content.Headers)
                {
                    Write(stream, $"{header.Key}: {string.Join(", ", header.Value)}\n");
                }
            }

            Write(stream, "\n");
            stream.Write(body);
            stream.Flush();
            return new WireDump(stream);
        }
        catch (Exception)
        {
            // Diagnostics never fail a turn: release the handle, drop the half file, and leave the turn alone.
            stream?.Dispose();
            TryDelete(path);
            return null;
        }
    }

    /// <summary>Records the status line and headers, then lets the body reach the file as the caller reads it.</summary>
    public void Response(HttpResponseMessage response)
    {
        try
        {
            Write(stream, "\n\n---- response ----\n");
            Write(stream, $"{(int)response.StatusCode} {response.ReasonPhrase} {response.Version}\n");
            foreach (var header in response.Headers)
            {
                Write(stream, $"{header.Key}: {string.Join(", ", header.Value)}\n");
            }

            foreach (var header in response.Content.Headers)
            {
                Write(stream, $"{header.Key}: {string.Join(", ", header.Value)}\n");
            }

            Write(stream, "\n");
            stream.Flush();
            // The replacement content starts with no headers of its own; everything the provider might read stays with it.
            var original = response.Content;
            var teed = new StreamContent(new TeeStream(original.ReadAsStream(), this));
            foreach (var header in original.Headers)
            {
                teed.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Content = teed;
            attached = true;
        }
        catch (Exception)
        {
            // Same contract as Start: swallow.
        }
    }

    /// <summary>
    /// Closes the file for an attempt that never produced a readable response: the status the server refused with, the body
    /// it refused with, and the exception. A rejection reaches the caller as an exception, so without this the file would end
    /// at the request with no word about what came back.
    /// </summary>
    public void Failed(Exception? failure)
    {
        try
        {
            Write(stream, "\n\n---- response ----\n");
            if (failure is ProviderException { StatusCode: { } status })
            {
                // Only a real HTTP answer has a status; a timeout or a refused connection never reached the server.
                Write(stream, $"!! refusal status {status.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
            }

            Write(stream, $"!! {failure?.GetType().Name}: {failure?.Message}\n");
            stream.Flush();
        }
        catch (Exception)
        {
            // Swallow.
        }
    }

    public void Dispose()
    {
        if (attached)
        {
            // The tee owns the file once the body is being read: it closes when the provider finishes or abandons the stream.
            return;
        }

        Close();
    }

    /// <summary>Appends one chunk of the response; a disk that fails mid-stream turns the capture off, never the turn.</summary>
    private void Record(ReadOnlySpan<byte> chunk)
    {
        if (dead)
        {
            return;
        }

        try
        {
            stream.Write(chunk);
        }
        catch (Exception)
        {
            dead = true;
        }
    }

    private async ValueTask RecordAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct)
    {
        if (dead)
        {
            return;
        }

        try
        {
            await stream.WriteAsync(chunk, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            dead = true;
        }
    }

    private void Close()
    {
        if (closed)
        {
            return;
        }

        closed = true;
        try
        {
            stream.Dispose();
        }
        catch (Exception)
        {
            // Swallow.
        }
    }

    private static void Write(Stream target, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        target.Write(bytes);
    }

    /// <summary>The nearest ancestor holding <c>Forge.slnx</c>, or null when this is not a source tree.</summary>
    private static string? RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            for (var depth = 0; current is not null && depth < 8; depth++, current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, "Forge.slnx")))
                {
                    return current.FullName;
                }
            }
        }

        return null;
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Swallow: a leftover file is the lesser problem.
        }
    }

    /// <summary>
    /// <c>.log</c> at the repository root — the nearest ancestor of the working directory or of the executable that holds
    /// <c>Forge.slnx</c> — so a Debug build launched from its <c>bin/</c> output still writes where a person looks. Null when the
    /// directory cannot be created, in which case nothing is captured.
    /// </summary>
    private static string? ResolveRoot()
    {
        // Only the app itself: a test host and a benchmark also build Debug and also send requests, and their traffic is not
        // what anyone opens .log/ to read.
        if (Assembly.GetEntryAssembly()?.GetName().Name != "Forge.App")
        {
            return null;
        }

        var configured = Path.Combine(RepositoryRoot() ?? Directory.GetCurrentDirectory(), ".log");
        try
        {
            System.IO.Directory.CreateDirectory(configured);
            return configured;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Passes every read through unchanged and appends it to the file.</summary>
    private sealed class TeeStream(Stream inner, WireDump owner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
            {
                owner.Record(buffer.AsSpan(offset, read));
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read > 0)
            {
                await owner.RecordAsync(buffer[..read], ct).ConfigureAwait(false);
            }

            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            if (read > 0)
            {
                owner.Record(buffer[..read]);
            }

            return read;
        }

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Close();
            }

            base.Dispose(disposing);
        }
    }
}

#endif
