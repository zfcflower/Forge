using System.Text;

namespace Forge.CodeMode;

internal sealed class ScriptLog(int limit)
{
    private readonly StringBuilder buffer = new();
    private readonly Lock gate = new();
    private bool truncated;

    public void Write(object? value)
    {
        var line = value?.ToString() ?? "null";
        lock (gate)
        {
            if (buffer.Length >= limit)
            {
                truncated = true;
                return;
            }

            var room = limit - buffer.Length;
            if (line.Length > room)
            {
                buffer.Append(line, 0, room);
                truncated = true;
                return;
            }

            buffer.Append(line).Append('\n');
        }
    }

    public string Snapshot()
    {
        lock (gate)
        {
            if (buffer.Length == 0)
            {
                return string.Empty;
            }

            return truncated
                ? buffer.ToString().TrimEnd('\n') + "\n[log truncated at " + limit + " chars]"
                : buffer.ToString().TrimEnd('\n');
        }
    }
}
