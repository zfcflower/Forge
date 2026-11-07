using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Forge.IO.Tools;

namespace Forge.IO;

/// <summary>One line of a search result: a matching line, or a context line around one.</summary>
public readonly record struct GrepLine(string Path, int LineNumber, string Text, bool IsMatch);

/// <summary>One grep call: where to search, what for, and the bounds that keep the answer finite.</summary>
public sealed record GrepRequest
{
    /// <summary>The workspace root; every reported path is relative to it.</summary>
    public required string Root { get; init; }

    /// <summary>An absolute path inside <see cref="Root"/> to start from; a directory is walked, a file is searched alone.</summary>
    public required string StartPath { get; init; }

    public required string Pattern { get; init; }

    /// <summary>An optional glob a file's workspace-relative path has to match; <see cref="GlobMatcher.IsPathMatch(string)"/> semantics.</summary>
    public string? Glob { get; init; }

    /// <summary>Treat <see cref="Pattern"/> as a .NET regular expression instead of a literal string.</summary>
    public bool Regex { get; init; }

    public bool CaseSensitive { get; init; } = true;

    /// <summary>Context lines reported before and after every match.</summary>
    public int ContextLines { get; init; }

    /// <summary>Maximum number of matching lines; reaching it truncates the search.</summary>
    public int Limit { get; init; } = ToolOptions.DefaultGrepMaxResults;

    /// <summary>A file larger than this is not read; it is counted and reported instead.</summary>
    public int MaxFileBytes { get; init; } = ToolOptions.DefaultGrepMaxFileBytes;

    /// <summary>Files read at once; clamped to <c>[1, 8]</c>.</summary>
    public int Concurrency { get; init; } = ToolOptions.DefaultSearchConcurrency;
}

/// <summary>What one grep call found and what it had to leave out.</summary>
public sealed record GrepResult
{
    /// <summary>Matching and context lines in completion order (the search runs files concurrently, so it is not sorted).</summary>
    public required IReadOnlyList<GrepLine> Lines { get; init; }

    /// <summary>Matching lines counted towards <see cref="GrepRequest.Limit"/>, never more than the limit.</summary>
    public required int MatchCount { get; init; }

    /// <summary>True when the limit stopped the search before it ran out of files or lines.</summary>
    public bool Truncated { get; init; }

    public int FilesScanned { get; init; }

    public int FilesSkippedTooLarge { get; init; }

    public int FilesSkippedBinary { get; init; }

    /// <summary>Files whose regular expression did not finish within the two second match timeout; their lines are dropped.</summary>
    public IReadOnlyList<string> RegexTimeoutFiles { get; init; } = [];

    public static GrepResult Empty { get; } = new() { Lines = [], MatchCount = 0 };
}

/// <summary>
/// The managed search engine. Files are enumerated lazily by <see cref="FileWalker"/> through a
/// <see cref="GitIgnore"/> filter and handed to a bounded channel (two entries per reader) that a fixed number of
/// readers drain, so neither the queue nor the task count grows with the size of the tree. Literal patterns are found
/// with <see cref="MemoryExtensions.IndexOf(ReadOnlySpan{char}, ReadOnlySpan{char}, StringComparison)"/> and never build
/// a <see cref="Regex"/>; regular expressions run with a two second match timeout and a file that times out is dropped
/// and reported.
/// </summary>
public static class GrepEngine
{
    private const int MaxConcurrency = ToolOptions.DefaultSearchConcurrency;

    /// <summary>Bytes of a file inspected for a NUL before it is treated as binary.</summary>
    private const int BinarySniffBytes = 8 * 1024;

    /// <summary>Files whose timeout is reported before the report cuts off.</summary>
    private const int MaxTimeoutReports = 20;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    public static async Task<GrepResult> SearchAsync(GrepRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Root);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StartPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Pattern);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxFileBytes, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(request.ContextLines);
        ct.ThrowIfCancellationRequested();

        var search = new Search(request, Math.Clamp(request.Concurrency, 1, MaxConcurrency), ct);
        return await search.RunAsync(ct).ConfigureAwait(false);
    }

    private sealed class Search
    {
        private readonly GrepRequest request;
        private readonly int concurrency;
        private readonly CancellationToken external;
        private readonly Regex? regex;
        private readonly StringComparison comparison;
        private readonly GlobMatcher? glob;
        private readonly bool singleFile;
        private readonly List<GrepLine> lines = [];
        private readonly List<string> timeouts = [];
        private readonly Lock gate = new();
        private CancellationTokenSource? stopSource;
        private int matches;
        private int truncated;
        private int scanned;
        private int tooLarge;
        private int binary;

        public Search(GrepRequest request, int concurrency, CancellationToken external)
        {
            this.request = request;
            this.concurrency = concurrency;
            this.external = external;

            regex = request.Regex
                ? new Regex(
                    request.Pattern,
                    request.CaseSensitive ? RegexOptions.CultureInvariant : RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                    MatchTimeout)
                : null;
            comparison = request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            glob = string.IsNullOrEmpty(request.Glob) ? null : GlobMatcher.Compile(request.Glob);
            singleFile = File.Exists(request.StartPath);
        }

        public async Task<GrepResult> RunAsync(CancellationToken ct)
        {
            var channel = Channel.CreateBounded<WalkEntry>(new BoundedChannelOptions(concurrency * 2)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stopSource = stop;
            var token = stop.Token;

            var producer = Task.Run(() => ProduceAsync(channel.Writer, token), CancellationToken.None);
            var consumers = new Task[concurrency];
            for (var index = 0; index < consumers.Length; index++)
            {
                consumers[index] = Task.Run(() => ConsumeAsync(channel.Reader, token), CancellationToken.None);
            }

            Task[] all = [producer, .. consumers];
            try
            {
                await Task.WhenAll(all).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!external.IsCancellationRequested)
            {
                // The limit stopped the search; what was collected so far is the answer.
            }

            return Build();
        }

        private IEnumerable<WalkEntry> Enumerate()
        {
            if (singleFile)
            {
                var relative = WorkspacePath.RelativeTo(request.Root, request.StartPath);
                if (glob is null || glob.IsPathMatch(relative))
                {
                    yield return new WalkEntry(request.StartPath, relative, IsDirectory: false);
                }

                yield break;
            }

            var ignore = new GitIgnore(request.Root);
            foreach (var entry in FileWalker.Walk(request.Root, request.StartPath, int.MaxValue, ignore, external))
            {
                if (entry.IsDirectory)
                {
                    continue;
                }

                // A link is not followed: ripgrep does not follow links either, and following one could read content
                // from outside the workspace through a file the repository happens to contain.
                if (entry.IsLink)
                {
                    continue;
                }

                if (glob is not null && !glob.IsPathMatch(entry.RelativePath))
                {
                    continue;
                }

                yield return entry;
            }
        }

        private async Task ProduceAsync(ChannelWriter<WalkEntry> writer, CancellationToken token)
        {
            try
            {
                foreach (var entry in Enumerate())
                {
                    await writer.WriteAsync(entry, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!external.IsCancellationRequested)
            {
                writer.TryComplete();
                return;
            }
            catch (Exception exception)
            {
                writer.TryComplete(exception);
                return;
            }

            writer.TryComplete();
        }

        private async Task ConsumeAsync(ChannelReader<WalkEntry> reader, CancellationToken token)
        {
            try
            {
                while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var entry))
                    {
                        if (Volatile.Read(ref truncated) != 0)
                        {
                            return;
                        }

                        await ScanAsync(entry, token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (!external.IsCancellationRequested)
            {
            }
        }

        private async Task ScanAsync(WalkEntry entry, CancellationToken token)
        {
            byte[]? buffer = null;
            try
            {
                await using var stream = new FileStream(
                    entry.FullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                var length = stream.Length;
                if (length == 0)
                {
                    Interlocked.Increment(ref scanned);
                    return;
                }

                if (length > request.MaxFileBytes)
                {
                    Interlocked.Increment(ref tooLarge);
                    return;
                }

                Interlocked.Increment(ref scanned);
                buffer = ArrayPool<byte>.Shared.Rent((int)length);
                var read = await stream
                    .ReadAtLeastAsync(buffer.AsMemory(0, (int)length), (int)length, throwOnEndOfStream: false, token)
                    .ConfigureAwait(false);

                var bytes = buffer.AsSpan(0, read);
                var probe = bytes[..Math.Min(read, BinarySniffBytes)];
                if (probe.IndexOf((byte)0) >= 0)
                {
                    Interlocked.Increment(ref binary);
                    return;
                }

                // A NUL past the probe window ends the scan there, which is where ripgrep stops reporting a file too: the
                // hits before it are kept, the ones after it are not. Without this the two backends disagree on any file
                // whose first NUL is beyond the probe.
                var beyond = probe.Length < bytes.Length ? bytes[probe.Length..].IndexOf((byte)0) : -1;
                if (beyond >= 0)
                {
                    Interlocked.Increment(ref binary);
                    bytes = bytes[..(probe.Length + beyond)];
                }

                var text = Encoding.UTF8.GetString(bytes);
                var scan = new FileScan();
                try
                {
                    ScanText(entry.RelativePath, text, scan);
                }
                catch (RegexMatchTimeoutException)
                {
                    // The lines found before the timeout are dropped: the file is reported instead of half-searched.
                    Interlocked.Add(ref matches, -scan.Reserved);
                    lock (gate)
                    {
                        if (timeouts.Count < MaxTimeoutReports)
                        {
                            timeouts.Add(entry.RelativePath);
                        }
                    }

                    return;
                }

                if (scan.Lines is not { Count: > 0 } found)
                {
                    return;
                }

                lock (gate)
                {
                    lines.AddRange(found);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // A file that vanished or cannot be read is skipped: one bad file must not fail the whole search.
            }
            finally
            {
                if (buffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        private void ScanText(string path, string text, FileScan scan)
        {
            var context = request.ContextLines;
            Span<ContextSlot> slots = context is > 0 and <= 8
                ? stackalloc ContextSlot[8]
                : context > 8 ? new ContextSlot[context] : default;

            int lineNumber = 1, lastEmitted = 0, contextUntil = 0, slotCount = 0, index = 0;
            while (index < text.Length)
            {
                var newline = text.IndexOf('\n', index);
                var start = index;
                var length = newline < 0 ? text.Length - index : newline - index;
                var next = newline < 0 ? text.Length : newline + 1;
                if (length > 0 && text[start + length - 1] == '\r')
                {
                    length--;
                }

                var line = text.AsSpan(start, length);
                if (IsMatch(line))
                {
                    if (!Reserve())
                    {
                        break;
                    }

                    scan.Reserved++;
                    scan.Lines ??= new List<GrepLine>();
                    if (context > 0)
                    {
                        // Only lines after the last one reported are missing, so two close matches never repeat a line.
                        var first = Math.Max(0, slotCount - context);
                        for (var ordinal = first; ordinal < slotCount; ordinal++)
                        {
                            ref readonly var slot = ref slots[ordinal % context];
                            if (slot.Line <= lastEmitted)
                            {
                                continue;
                            }

                            scan.Lines.Add(new GrepLine(
                                path,
                                slot.Line,
                                text.Substring(slot.Start, slot.Length),
                                IsMatch: false));
                            lastEmitted = slot.Line;
                        }
                    }

                    scan.Lines.Add(new GrepLine(path, lineNumber, line.ToString(), IsMatch: true));
                    lastEmitted = lineNumber;
                    contextUntil = lineNumber + context;
                }
                else if (lineNumber <= contextUntil && lineNumber > lastEmitted)
                {
                    scan.Lines ??= new List<GrepLine>();
                    scan.Lines.Add(new GrepLine(path, lineNumber, line.ToString(), IsMatch: false));
                    lastEmitted = lineNumber;
                }

                if (context > 0)
                {
                    // A slot holds offsets into the decoded file, so remembering context lines costs no copy.
                    slots[slotCount % context] = new ContextSlot(lineNumber, start, length);
                    slotCount++;
                }

                lineNumber++;
                index = next;
            }
        }

        private bool IsMatch(ReadOnlySpan<char> line) =>
            regex is { } compiled
                ? compiled.IsMatch(line)
                : line.IndexOf(request.Pattern.AsSpan(), comparison) >= 0;

        /// <summary>Takes one of the <see cref="GrepRequest.Limit"/> result slots, or stops the search.</summary>
        private bool Reserve()
        {
            if (Interlocked.Increment(ref matches) <= request.Limit)
            {
                return true;
            }

            Interlocked.Exchange(ref truncated, 1);
            var source = stopSource;
            if (source is not null)
            {
                try
                {
                    source.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The call already returned; there is nothing left to stop.
                }
            }

            return false;
        }

        private GrepResult Build() => new()
        {
            Lines = lines,
            MatchCount = Math.Min(matches, request.Limit),
            Truncated = truncated != 0,
            FilesScanned = scanned,
            FilesSkippedTooLarge = tooLarge,
            FilesSkippedBinary = binary,
            RegexTimeoutFiles = timeouts,
        };
    }

    private sealed class FileScan
    {
        public List<GrepLine>? Lines { get; set; }

        /// <summary>Result slots this file took, refunded when a regular expression times out.</summary>
        public int Reserved { get; set; }
    }

    private readonly struct ContextSlot(int line, int start, int length)
    {
        public int Line { get; } = line;

        public int Start { get; } = start;

        public int Length { get; } = length;
    }
}
