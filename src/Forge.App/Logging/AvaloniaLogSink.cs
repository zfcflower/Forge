using System.Globalization;
using System.Text;
using Avalonia.Logging;
using Microsoft.Extensions.Logging;

namespace Forge.App.Logging;

/// <summary>
/// Avalonia's log into <see cref="AppLog"/> with its level intact. <c>LogToTrace</c> writes plain
/// <c>Trace.WriteLine</c> lines, so every binding or layout warning would arrive as Information and vanish under
/// <c>FORGE_LOG_LEVEL=Warning</c>. The area (<c>Binding</c>, <c>Layout</c>, ...) becomes the category; like
/// <c>LogToTrace</c> it reports Warning and above only.
/// </summary>
internal sealed class AvaloniaLogSink(LogEventLevel minimum = LogEventLevel.Warning) : ILogSink
{
    private readonly ConcurrentLoggers loggers = new();

    public bool IsEnabled(LogEventLevel level, string area)
        => level >= minimum && loggers.For(area).IsEnabled(Map(level));

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        => Log(level, area, source, messageTemplate, []);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (level < minimum)
        {
            return;
        }

        try
        {
            var logger = loggers.For(area);
            var mapped = Map(level);
            if (!logger.IsEnabled(mapped))
            {
                return;
            }

            var text = Render(messageTemplate, propertyValues);
            if (source is not null)
            {
                text += " (" + source.GetType().Name + ")";
            }

            logger.Log(mapped, default, text, null, static (state, _) => state);
        }
        catch (Exception)
        {
            // Logging must never fail a binding or layout pass.
        }
    }

    internal static LogLevel Map(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevel.Trace,
        LogEventLevel.Debug => LogLevel.Debug,
        LogEventLevel.Information => LogLevel.Information,
        LogEventLevel.Warning => LogLevel.Warning,
        LogEventLevel.Error => LogLevel.Error,
        _ => LogLevel.Critical,
    };

    /// <summary>Fills <c>{Name}</c> holes left to right with <paramref name="values"/>; extra holes stay as written.</summary>
    internal static string Render(string template, object?[] values)
    {
        if (values.Length == 0 || template.IndexOf('{') < 0)
        {
            return template;
        }

        var builder = new StringBuilder(template.Length + 32);
        var next = 0;
        var position = 0;
        while (position < template.Length)
        {
            var open = template.IndexOf('{', position);
            var close = open < 0 ? -1 : template.IndexOf('}', open + 1);
            if (open < 0 || close < 0 || next >= values.Length)
            {
                builder.Append(template, position, template.Length - position);
                break;
            }

            builder.Append(template, position, open - position);
            builder.Append(Convert.ToString(values[next++], CultureInfo.InvariantCulture));
            position = close + 1;
        }

        return builder.ToString();
    }

    private sealed class ConcurrentLoggers
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ILogger> map = new(StringComparer.Ordinal);

        public ILogger For(string area)
            => map.GetOrAdd(string.IsNullOrEmpty(area) ? "avalonia" : area, static name => AppLog.Create(name));
    }
}
