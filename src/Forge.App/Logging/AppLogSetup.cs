using System.Diagnostics;
using Forge.App.Composition;
using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Providers;

namespace Forge.App.Logging;

/// <summary>Builds the file-backed factory, installs it in <see cref="AppLog"/> and bridges <see cref="Trace"/> into it.</summary>
internal static class AppLogSetup
{
    public const string LevelVariable = "FORGE_LOG_LEVEL";

    internal const string LogsDirectoryName = "logs";
    internal const string LogExtension = ".log";
    internal const int RollSizeKilobytes = 10 * 1024;

    internal static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    /// <summary>
    /// Configures logging for the running app: rolling file under <c>&lt;data root&gt;/logs</c>, the Trace bridge, and a
    /// background cleanup of old files. Disposing the result restores the null factory and flushes the file.
    /// </summary>
    public static IDisposable Configure(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Configure(paths.UserDataDirectory);
    }

    /// <summary>The same as <see cref="Configure(AppPaths)"/> for an explicit data root, so tests never touch the real one.</summary>
    internal static IDisposable Configure(string dataRoot)
    {
        var directory = Path.Combine(dataRoot, LogsDirectoryName);
        var level = ParseLevel(Environment.GetEnvironmentVariable(LevelVariable));
        ILoggerFactory factory;
        try
        {
            factory = CreateFactory(directory, level);
        }
        catch (Exception)
        {
            // An unwritable data root (a file named "logs", ACLs, a locked file) must not stop the app from starting:
            // run without a log file rather than without a window.
            return NoLogging.Instance;
        }

        var installed = AppLog.Use(factory);
        var bridge = new TraceBridgeListener();
        Trace.Listeners.Add(bridge);
        StartPrune(directory);
        return new Teardown(factory, installed, bridge);
    }

    /// <summary>
    /// Logs unhandled exceptions as Critical and, when the process is going down, disposes <paramref name="logging"/> so
    /// the buffered lines (and the crash itself) reach the file. Unobserved task exceptions are Warnings.
    /// </summary>
    public static void CaptureCrashes(IDisposable logging)
    {
        ArgumentNullException.ThrowIfNull(logging);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                AppLogs.Crash.ZLogCritical(exception, $"unhandled exception (terminating: {args.IsTerminating})");
            }
            else
            {
                AppLogs.Crash.ZLogCritical($"unhandled non-exception object (terminating: {args.IsTerminating})");
            }

            if (args.IsTerminating)
            {
                logging.Dispose();
            }
        };
        TaskScheduler.UnobservedTaskException += static (_, args) =>
            AppLogs.Crash.ZLogWarning(args.Exception, $"unobserved task exception");
    }

    /// <summary>Parses <c>FORGE_LOG_LEVEL</c>; anything other than the five documented names (case-insensitive) is Information.</summary>
    internal static LogLevel ParseLevel(string? value)
    {
        if (value is not null)
        {
            var text = value.Trim();
            if (text.Equals("Trace", StringComparison.OrdinalIgnoreCase))
            {
                return LogLevel.Trace;
            }

            if (text.Equals("Debug", StringComparison.OrdinalIgnoreCase))
            {
                return LogLevel.Debug;
            }

            if (text.Equals("Warning", StringComparison.OrdinalIgnoreCase))
            {
                return LogLevel.Warning;
            }

            if (text.Equals("Error", StringComparison.OrdinalIgnoreCase))
            {
                return LogLevel.Error;
            }
        }

        return LogLevel.Information;
    }

    /// <summary><c>forge-yyyyMMdd_seq.log</c>; the date is the local day the file was opened.</summary>
    internal static string FileName(DateTimeOffset openedAt, int sequence)
        => $"forge-{openedAt.ToLocalTime():yyyyMMdd}_{sequence}{LogExtension}";

    internal static ILoggerFactory CreateFactory(string logsDirectory, LogLevel minimumLevel)
        => LoggerFactory.Create(builder => builder
            .SetMinimumLevel(minimumLevel)
            .AddZLoggerRollingFile(options =>
            {
                options.FilePathSelector = (openedAt, sequence) => Path.Combine(logsDirectory, FileName(openedAt, sequence));
                options.RollingInterval = RollingInterval.Day;
                options.RollingSizeKB = RollSizeKilobytes;
                options.UsePlainTextFormatter(ConfigureFormatter);
            }));

    /// <summary>
    /// Deletes <c>*.log</c> files in <paramref name="directory"/> last written before <paramref name="now"/> minus
    /// <paramref name="maxAge"/>. Other files and every failure are left alone. Returns the number of files deleted.
    /// </summary>
    internal static int PruneOldLogs(string directory, DateTime now, TimeSpan maxAge)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            var cutoff = now - maxAge;
            foreach (var path in Directory.EnumerateFiles(directory, "*" + LogExtension, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTime(path) < cutoff)
                    {
                        File.Delete(path);
                        deleted++;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // In use or read-only: it goes on the next start.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Retention is best effort and must not hurt startup.
        }

        return deleted;
    }

    private static void StartPrune(string directory)
    {
        var thread = new Thread(() => PruneOldLogs(directory, DateTime.Now, Retention))
        {
            IsBackground = true,
            Name = "forge-log-retention",
        };
        thread.Start();
    }

    // "2026-11-13 10:16:19.123 INF category message": ZLogger writes the stamp, level and category as UTF-8 straight into
    // its buffer, so a line costs no string. Exceptions use its default (the full text on the following lines).
    private static void ConfigureFormatter(ZLogger.Formatters.PlainTextZLoggerFormatter formatter)
        => formatter.SetPrefixFormatter(
            $"{0:local-longdate} {1:short} {2} ",
            static (in MessageTemplate template, in LogInfo info) => template.Format(info.Timestamp, info.LogLevel, info.Category));

    private sealed class NoLogging : IDisposable
    {
        public static readonly NoLogging Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class Teardown(ILoggerFactory factory, IDisposable installed, TraceListener bridge) : IDisposable
    {
        private int disposed;

        // Runs from Main's using and possibly first from the crash handler; only the first call tears down.
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            Trace.Listeners.Remove(bridge);
            installed.Dispose();
            factory.Dispose();
        }
    }
}
