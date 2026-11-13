using Avalonia;
using Avalonia.Logging;
using Forge.App.Composition;
using Forge.App.Logging;

namespace Forge.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Logging is configured before Avalonia starts and disposed after the lifetime returns, which flushes the file
        // buffers. A crash never reaches that dispose, so the crash handler logs and flushes on its own.
        using var logging = AppLogSetup.Configure(AppPaths.Create());
        AppLogSetup.CaptureCrashes(logging);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToForge();

    /// <summary>Like <c>LogToTrace</c>, set while the builder is configured so setup-time warnings are kept too.</summary>
    private static AppBuilder LogToForge(this AppBuilder builder)
    {
        Logger.Sink = new AvaloniaLogSink();
        return builder;
    }
}
