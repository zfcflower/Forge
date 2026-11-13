using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Forge.App.Logging;

/// <summary>
/// Forwards <see cref="Trace"/> output of the projects that do not reference Microsoft.Extensions.Logging (Core,
/// Workspace, HostClient, Editor, ...) into <see cref="AppLog"/>; Avalonia's own log goes through
/// <see cref="AvaloniaLogSink"/> instead, because its trace sink drops the level. A message starting
/// with <c>[tag]</c> logs under category <c>tag</c> with the prefix removed, anything else under <c>trace</c>.
/// <c>TraceWarning</c>/<c>TraceError</c> map to Warning/Error, the rest to Information. It never throws.
/// </summary>
internal sealed class TraceBridgeListener : TraceListener
{
    internal const string DefaultCategory = "trace";

    private const int MaxTagLength = 40;
    private const int MaxPendingCharacters = 16 * 1024;

    private static readonly ConcurrentDictionary<string, ILogger> Loggers = new(StringComparer.Ordinal);

    private readonly Lock gate = new();
    private readonly StringBuilder pending = new();

    public override void Write(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        try
        {
            string? ready = null;
            lock (gate)
            {
                pending.Append(message);
                if (pending.Length >= MaxPendingCharacters)
                {
                    ready = TakePending();
                }
            }

            if (ready is not null)
            {
                Emit(LogLevel.Information, ready);
            }
        }
        catch (Exception)
        {
            // A diagnostics bridge must never fail its caller.
        }
    }

    public override void WriteLine(string? message) => WriteLine(message, LogLevel.Information);

    public override void WriteLine(string? message, string? category)
        => WriteLine(string.IsNullOrEmpty(category) ? message : category + ": " + message, LogLevel.Information);

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        => WriteLine(message, Map(eventType));

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
    {
        string? text;
        try
        {
            text = args is { Length: > 0 } && format is not null ? string.Format(CultureInfo.InvariantCulture, format, args) : format;
        }
        catch (FormatException)
        {
            text = format;
        }

        WriteLine(text, Map(eventType));
    }

    internal static LogLevel Map(TraceEventType type) => type switch
    {
        TraceEventType.Critical or TraceEventType.Error => LogLevel.Error,
        TraceEventType.Warning => LogLevel.Warning,
        _ => LogLevel.Information,
    };

    /// <summary>Splits a leading <c>[tag]</c> off <paramref name="message"/>; the category is <see cref="DefaultCategory"/> without one.</summary>
    internal static (string Category, string Text) Parse(string message)
    {
        if (message.Length > 2 && message[0] == '[')
        {
            var close = message.IndexOf(']');
            if (close is > 1 and <= MaxTagLength + 1 && IsTag(message.AsSpan(1, close - 1)))
            {
                return (message.Substring(1, close - 1), message.Substring(close + 1).TrimStart());
            }
        }

        return (DefaultCategory, message);
    }

    private static bool IsTag(ReadOnlySpan<char> tag)
    {
        foreach (var ch in tag)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ' '))
            {
                return false;
            }
        }

        return true;
    }

    private void WriteLine(string? message, LogLevel level)
    {
        try
        {
            string line;
            lock (gate)
            {
                if (pending.Length > 0)
                {
                    pending.Append(message);
                    line = TakePending();
                }
                else
                {
                    line = message ?? string.Empty;
                }
            }

            Emit(level, line);
        }
        catch (Exception)
        {
            // A diagnostics bridge must never fail its caller.
        }
    }

    private string TakePending()
    {
        var text = pending.ToString();
        pending.Clear();
        return text;
    }

    private static void Emit(LogLevel level, string line)
    {
        line = line.TrimEnd('\r', '\n');
        if (line.Length == 0)
        {
            return;
        }

        var (category, text) = Parse(line);
        var logger = Loggers.GetOrAdd(category, static name => AppLog.Create(name));
        if (logger.IsEnabled(level))
        {
            logger.Log(level, default, text, null, static (state, _) => state);
        }
    }
}
