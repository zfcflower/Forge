using System.Collections.Concurrent;
using Forge.App.Logging;
using Microsoft.Extensions.Logging;

namespace Forge.App.Tests.Logging;

/// <summary>
/// Installs a capturing <see cref="ILoggerFactory"/> as the process-wide <see cref="AppLog"/> factory for the duration
/// of a test and restores the previous one on dispose. <see cref="Lines"/> are <c>[category] message</c>, the shape the
/// old Trace lines had.
/// </summary>
internal sealed class LogCapture : IDisposable
{
    private readonly ConcurrentQueue<Entry> entries = new();
    private readonly ILoggerFactory factory;
    private readonly IDisposable installed;

    public LogCapture(LogLevel minimumLevel = LogLevel.Trace)
    {
        factory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(minimumLevel)
            .AddProvider(new Provider(entries)));
        installed = AppLog.Use(factory);
    }

    public IReadOnlyList<Entry> Entries => [.. entries];

    public IReadOnlyList<string> Lines => [.. entries.Select(static entry => "[" + entry.Category + "] " + entry.Message)];

    public void Dispose()
    {
        installed.Dispose();
        factory.Dispose();
    }

    internal sealed record Entry(string Category, LogLevel Level, string Message, Exception? Exception);

    private sealed class Provider(ConcurrentQueue<Entry> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, sink);

        public void Dispose()
        {
        }
    }

    private sealed class Capturing(string category, ConcurrentQueue<Entry> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => sink.Enqueue(new Entry(category, logLevel, formatter(state, exception), exception));
    }
}
