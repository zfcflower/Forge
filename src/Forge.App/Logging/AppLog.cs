using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.App.Logging;

/// <summary>
/// The process-wide logger factory. It defaults to <see cref="NullLoggerFactory"/> so tests and headless runs write
/// nothing; <see cref="AppLogSetup"/> installs the file-backed factory before Avalonia starts. Callers keep a
/// <c>static readonly ILogger</c> from <see cref="Create"/>, which resolves the current factory lazily, so a logger
/// created before configuration (or before a test installs a capturing factory) still reaches the right sink.
/// </summary>
internal static class AppLog
{
    private static readonly Lock Gate = new();
    private static ILoggerFactory current = NullLoggerFactory.Instance;

    internal static ILoggerFactory Current => Volatile.Read(ref current);

    /// <summary>A logger for <paramref name="category"/> that follows whichever factory is installed at call time.</summary>
    public static ILogger Create(string category)
    {
        ArgumentException.ThrowIfNullOrEmpty(category);
        return new ForwardingLogger(category);
    }

    /// <summary>
    /// Installs <paramref name="factory"/> until the returned scope is disposed, then restores the previous factory.
    /// The caller keeps ownership of <paramref name="factory"/> and disposes it after the scope.
    /// </summary>
    internal static IDisposable Use(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ILoggerFactory previous;
        lock (Gate)
        {
            previous = current;
            Volatile.Write(ref current, factory);
        }

        return new Restore(factory, previous);
    }

    private sealed class Restore(ILoggerFactory installed, ILoggerFactory previous) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            lock (Gate)
            {
                // Only undo our own installation; a nested scope that already replaced it restores in its own order.
                if (ReferenceEquals(current, installed))
                {
                    Volatile.Write(ref current, previous);
                }
            }
        }
    }

    private sealed class ForwardingLogger(string category) : ILogger
    {
        private Binding? binding;

        private ILogger Inner()
        {
            var factory = Current;
            var cached = Volatile.Read(ref binding);
            if (cached is not null && ReferenceEquals(cached.Factory, factory))
            {
                return cached.Logger;
            }

            ILogger logger;
            try
            {
                logger = factory.CreateLogger(category);
            }
            catch (ObjectDisposedException)
            {
                // The factory was disposed at shutdown between reading Current and creating the logger.
                return NullLogger.Instance;
            }

            var created = new Binding(factory, logger);
            Volatile.Write(ref binding, created);
            return created.Logger;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => Inner().BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => Inner().IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                Inner().Log(logLevel, eventId, state, exception, formatter);
            }
            catch (ObjectDisposedException)
            {
                // A factory disposed at shutdown while a background task still logs: drop the line.
            }
        }

        private sealed record Binding(ILoggerFactory Factory, ILogger Logger);
    }
}
