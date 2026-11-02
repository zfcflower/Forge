using System.Text;

namespace Forge.Mcp;

internal readonly record struct SseEvent(string? Id, string? Name, string Data);

/// <summary>Reads server-sent events until the stream ends; blank lines terminate an event.</summary>
internal sealed class SseEventStream : IDisposable
{
    private readonly StreamReader reader;
    private readonly StringBuilder data = new();

    public SseEventStream(Stream stream)
    {
        reader = new StreamReader(stream, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: false);
    }

    public async ValueTask<SseEvent?> ReadAsync(CancellationToken ct)
    {
        data.Clear();
        string? id = null;
        string? name = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                return data.Length == 0 ? null : new SseEvent(id, name, data.ToString());
            }

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    return new SseEvent(id, name, data.ToString());
                }

                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.Length > 0 && value[0] == ' ')
            {
                value = value[1..];
            }

            switch (field)
            {
                case "data":
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    break;
                case "id":
                    id = value;
                    break;
                case "event":
                    name = value;
                    break;
            }
        }
    }

    public void Dispose() => reader.Dispose();
}
