using System.Globalization;
using System.Text;

namespace Forge.App.Services;

/// <summary>
/// The preview of a file-backed row: the first lines of a file, or the lines around a position. A read is bounded in
/// characters and lines, runs on the thread pool, and the last file is kept decoded so moving the selection between the
/// lines of one file costs nothing - the palette's arrow keys are the hot path here, not the disk.
/// </summary>
internal sealed class PalettePreview
{
    /// <summary>Lines of a file preview when the row has no position: the head of the file.</summary>
    public const int HeadLines = 40;

    /// <summary>Lines shown before and after a position, so a match has context without becoming a file viewer.</summary>
    public const int AroundLines = 12;

    /// <summary>
    /// Characters kept from one file at most. Past it a preview stops and shows the tail rather than pretending the file
    /// is short; the bound exists so a generated megabyte file cannot turn one arrow key into a large allocation.
    /// </summary>
    public const int MaxPreviewChars = 256 * 1024;

    private const char LineBreak = '\n';
    private const char CarriageReturn = '\r';

    private readonly Lock gate = new();
    private string? cachedPath;
    private string[]? cachedLines;

    /// <summary>
    /// The preview text of <paramref name="absolutePath"/>: numbered lines, the head when <paramref name="line"/> is 0 or
    /// 1, otherwise the window around the 1-based line. Null when the file cannot be read (missing, unreadable, oversized
    /// before the position was reached).
    /// </summary>
    public async Task<string?> ReadAsync(string absolutePath, int line, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        try
        {
            var lines = await Task.Run(() => Lines(absolutePath, ct), ct).ConfigureAwait(false);
            return Window(lines, line);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or EncoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Drops the decoded file; called when the palette closes so a long session holds no file text.</summary>
    public void Clear()
    {
        lock (gate)
        {
            cachedPath = null;
            cachedLines = null;
        }
    }

    private string[] Lines(string path, CancellationToken ct)
    {
        lock (gate)
        {
            if (cachedPath is not null && cachedLines is not null && string.Equals(cachedPath, path, StringComparison.Ordinal))
            {
                return cachedLines;
            }
        }

        var read = ReadLines(path, ct);
        lock (gate)
        {
            cachedPath = path;
            cachedLines = read;
        }

        return read;
    }

    /// <summary>
    /// Decodes at most <see cref="MaxPreviewChars"/> characters into lines. The characters are read in chunks and split
    /// here rather than with <see cref="StreamReader.ReadLine"/>, because that method materializes a whole line before any
    /// budget can apply - and a minified bundle is one line of megabytes, which is exactly the file a preview must not
    /// allocate. An over-budget line is kept truncated.
    /// </summary>
    private static string[] ReadLines(string path, CancellationToken ct)
    {
        var lines = new List<string>(256);
        var budget = MaxPreviewChars;
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[8 * 1024];
        var line = new StringBuilder(256);
        while (budget > 0)
        {
            ct.ThrowIfCancellationRequested();
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            var remaining = buffer.AsSpan(0, read);
            while (remaining.Length > 0 && budget > 0)
            {
                var newline = remaining.IndexOf(LineBreak);
                if (newline < 0)
                {
                    AppendBounded(line, remaining, ref budget);
                    break;
                }

                AppendBounded(line, remaining[..newline], ref budget);
                TrimCarriageReturn(line);
                lines.Add(line.ToString());
                line.Clear();
                budget--;
                remaining = remaining[(newline + 1)..];
            }
        }

        if (line.Length > 0)
        {
            TrimCarriageReturn(line);
            lines.Add(line.ToString());
        }

        return [.. lines];
    }

    /// <summary>Appends what the budget still allows of <paramref name="text"/>; the budget counts characters.</summary>
    private static void AppendBounded(StringBuilder line, ReadOnlySpan<char> text, ref int budget)
    {
        if (budget <= 0 || text.IsEmpty)
        {
            return;
        }

        if (text.Length > budget)
        {
            text = text[..budget];
        }

        line.Append(text);
        budget -= text.Length;
    }

    private static void TrimCarriageReturn(StringBuilder line)
    {
        if (line.Length > 0 && line[^1] == CarriageReturn)
        {
            line.Length--;
        }
    }

    private static string? Window(string[] lines, int line)
    {
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        var first = 1;
        var last = Math.Min(lines.Length, HeadLines);
        if (line > 1)
        {
            if (line > lines.Length)
            {
                // The position is in the part the character budget cut off: show the tail rather than a wrong window.
                first = Math.Max(1, lines.Length - AroundLines * 2);
                last = lines.Length;
            }
            else
            {
                first = Math.Max(1, line - AroundLines);
                last = Math.Min(lines.Length, line + AroundLines);
            }
        }

        var builder = new StringBuilder((last - first + 1) * 32);
        for (var index = first; index <= last; index++)
        {
            if (builder.Length > 0)
            {
                builder.Append(LineBreak);
            }

            builder.Append(index.ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append("  ").Append(lines[index - 1]);
        }

        return builder.ToString();
    }
}
