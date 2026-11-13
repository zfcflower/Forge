using Forge.App.Logging;
using Microsoft.Extensions.Logging;
using Xunit;
using ZLogger;

namespace Forge.App.Tests.Logging;

public sealed class AppLogSetupTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-log-tests-" + Guid.NewGuid().ToString("N"));

    public AppLogSetupTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("Trace", LogLevel.Trace)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("INFORMATION", LogLevel.Information)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData(" error ", LogLevel.Error)]
    [InlineData("verbose", LogLevel.Information)]
    [InlineData("", LogLevel.Information)]
    [InlineData(null, LogLevel.Information)]
    [InlineData("Critical", LogLevel.Information)]
    public void ParseLevel_accepts_the_documented_names_and_falls_back_to_information(string? value, LogLevel expected)
        => Assert.Equal(expected, AppLogSetup.ParseLevel(value));

    [Fact]
    public void FileName_uses_the_local_day_and_the_sequence()
    {
        var opened = new DateTimeOffset(2026, 3, 9, 8, 0, 0, TimeSpan.Zero);

        var name = AppLogSetup.FileName(opened, 2);

        Assert.Equal($"forge-{opened.ToLocalTime():yyyyMMdd}_2.log", name);
    }

    [Fact]
    public void PruneOldLogs_deletes_only_old_log_files()
    {
        var now = new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Local);
        var oldLog = Touch("forge-20260101_0.log", now.AddDays(-15));
        var edgeLog = Touch("forge-20260616_0.log", now.AddDays(-13));
        var freshLog = Touch("forge-20260630_0.log", now.AddHours(-1));
        var oldOther = Touch("notes.txt", now.AddDays(-60));
        var sub = Path.Combine(directory, "sub");
        Directory.CreateDirectory(sub);
        var nested = Path.Combine(sub, "forge-old.log");
        File.WriteAllText(nested, "x");
        File.SetLastWriteTime(nested, now.AddDays(-90));

        var deleted = AppLogSetup.PruneOldLogs(directory, now, TimeSpan.FromDays(14));

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(oldLog));
        Assert.True(File.Exists(edgeLog));
        Assert.True(File.Exists(freshLog));
        Assert.True(File.Exists(oldOther));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void PruneOldLogs_on_a_missing_directory_is_a_no_op()
        => Assert.Equal(0, AppLogSetup.PruneOldLogs(Path.Combine(directory, "missing"), DateTime.Now, TimeSpan.FromDays(14)));

    [Fact]
    public void PruneOldLogs_survives_a_file_that_is_held_open()
    {
        var now = new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Local);
        var path = Touch("forge-held.log", now.AddDays(-30));
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var failure = Record.Exception(() => AppLogSetup.PruneOldLogs(directory, now, TimeSpan.FromDays(14)));

        Assert.Null(failure);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(File.Exists(path));
        }
    }

    [Fact]
    public void The_file_factory_writes_one_plain_text_line_per_entry_and_flushes_on_dispose()
    {
        var logs = Path.Combine(directory, "logs");
        var factory = AppLogSetup.CreateFactory(logs, LogLevel.Information);
        var logger = factory.CreateLogger("unit");

        logger.ZLogInformation($"hello {7}");
        logger.ZLogDebug($"below the minimum");
        logger.ZLogError(new InvalidOperationException("boom"), $"it failed");
        factory.Dispose();

        var file = Assert.Single(Directory.GetFiles(logs, "forge-*.log"));
        var text = File.ReadAllText(file);
        Assert.Contains(" INF unit hello 7", text, StringComparison.Ordinal);
        Assert.DoesNotContain("below the minimum", text, StringComparison.Ordinal);
        Assert.Contains(" ERR unit it failed", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: boom", text, StringComparison.Ordinal);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} INF unit hello 7", text);
    }

    private string Touch(string name, DateTime lastWrite)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTime(path, lastWrite);
        return path;
    }

    [Fact]
    public void Configure_with_an_unwritable_log_directory_keeps_the_app_running_without_a_file()
    {
        // A file where the logs directory should be: ZLogger cannot open its stream and throws from LoggerFactory.Create.
        File.WriteAllText(Path.Combine(directory, AppLogSetup.LogsDirectoryName), "not a directory");
        var before = AppLog.Current;
        var listeners = System.Diagnostics.Trace.Listeners.Count;

        using (var logging = AppLogSetup.Configure(directory))
        {
            Assert.Same(before, AppLog.Current);
            Assert.Equal(listeners, System.Diagnostics.Trace.Listeners.Count);
            AppLog.Create("probe").ZLogWarning($"dropped, not thrown");
        }

        Assert.Same(before, AppLog.Current);
    }

    [Fact]
    public void Configure_installs_the_file_factory_and_the_bridge_and_teardown_is_idempotent()
    {
        var before = AppLog.Current;
        var logging = AppLogSetup.Configure(directory);
        try
        {
            Assert.NotSame(before, AppLog.Current);
            Assert.Contains(System.Diagnostics.Trace.Listeners.Cast<System.Diagnostics.TraceListener>(), static l => l is TraceBridgeListener);
            System.Diagnostics.Trace.WriteLine("[bridge-probe] through the bridge");
        }
        finally
        {
            logging.Dispose();
            logging.Dispose();
        }

        Assert.Same(before, AppLog.Current);
        Assert.DoesNotContain(System.Diagnostics.Trace.Listeners.Cast<System.Diagnostics.TraceListener>(), static l => l is TraceBridgeListener);
        var text = string.Concat(Directory.GetFiles(Path.Combine(directory, AppLogSetup.LogsDirectoryName)).Select(File.ReadAllText));
        Assert.Matches(@"(?m)^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} INF bridge-probe through the bridge\r?$", text);
    }
}
