using Forge.App.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZLogger;

namespace Forge.App.Tests.Logging;

public sealed class AppLogTests
{
    [Fact]
    public void A_logger_created_before_a_factory_is_installed_reaches_the_factory_installed_later()
    {
        var early = AppLog.Create("early");
        early.ZLogWarning($"dropped by the default null factory");

        using var capture = new LogCapture();
        early.ZLogWarning($"seen {42}");

        var entry = Assert.Single(capture.Entries);
        Assert.Equal("early", entry.Category);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("seen 42", entry.Message);
    }

    [Fact]
    public void Disposing_the_scope_restores_the_previous_factory_for_existing_loggers()
    {
        var logger = AppLog.Create("restore");
        var outer = new LogCapture();
        using (var inner = new LogCapture())
        {
            logger.ZLogInformation($"to inner");
            Assert.Single(inner.Entries);
        }

        logger.ZLogInformation($"to outer");
        Assert.Equal(["[restore] to outer"], outer.Lines);
        outer.Dispose();

        logger.ZLogError($"nobody listens");
        Assert.Same(NullLoggerFactory.Instance, AppLog.Current);
    }

    [Fact]
    public void The_exception_overload_carries_the_exception()
    {
        using var capture = new LogCapture();
        var failure = new InvalidOperationException("boom");

        AppLog.Create("errors").ZLogError(failure, $"it failed");

        var entry = Assert.Single(capture.Entries);
        Assert.Same(failure, entry.Exception);
        Assert.Equal(LogLevel.Error, entry.Level);
    }

    [Fact]
    public void A_level_below_the_minimum_is_not_enabled_and_not_captured()
    {
        using var capture = new LogCapture(LogLevel.Warning);
        var logger = AppLog.Create("filtered");

        Assert.False(logger.IsEnabled(LogLevel.Information));
        logger.ZLogInformation($"quiet");

        Assert.Empty(capture.Entries);
    }

    [Fact]
    public void Disposing_a_scope_twice_does_not_undo_a_later_installation()
    {
        using var first = new LoggerFactory();
        var scope = AppLog.Use(first);
        scope.Dispose();
        using var second = new LogCapture();

        scope.Dispose();
        AppLog.Create("twice").ZLogInformation($"still second");

        Assert.Single(second.Entries);
    }

    [Fact]
    public void A_factory_disposed_before_first_use_does_not_throw_from_IsEnabled_Log_or_BeginScope()
    {
        var factory = LoggerFactory.Create(static builder => builder.SetMinimumLevel(LogLevel.Trace));
        using var scope = AppLog.Use(factory);
        factory.Dispose();

        var logger = AppLog.Create("late");

        Assert.False(logger.IsEnabled(LogLevel.Warning));
        logger.LogWarning("dropped");
        using var nested = logger.BeginScope("scope");
    }
}
