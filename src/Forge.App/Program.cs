using Avalonia;
using Avalonia.Native;
using Avalonia.X11;
using Avalonia.Logging;
using Forge.App.Composition;
using Forge.App.Logging;
using Forge.App.Services;

namespace Forge.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Logging is configured before Avalonia starts and disposed after the lifetime returns, which flushes the file
        // buffers. A crash never reaches that dispose, so the crash handler logs and flushes on its own.
        var paths = AppPaths.Create();
        using var logging = AppLogSetup.Configure(paths);
        AppLogSetup.CaptureCrashes(logging);

        // Wave W10c: the applier is a process of its own — the staged new executable, started by the installed version. It
        // never touches Avalonia, the data-root lock or the composition root; it waits for the old process to exit and swaps
        // the install directory, and it must be able to do that even when the app being replaced is broken.
        if (UpdateApplyRequest.IsApplyCommand(args))
        {
            return UpdateApplyCommand.Run(args, paths.UserDataDirectory);
        }

        // Wave W10c: a swap that was never confirmed (the applier died, or the machine went down inside its 60 second window)
        // is handed back to the previous version before any UI exists. This process then exits instead of running an
        // unverified install, and the previous executable restarts the app.
        if (UpdateApplyStartup.RepairInterruptedSwap(paths.UserDataDirectory, args) is { } repairExitCode)
        {
            return repairExitCode;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Popups, flyouts and tooltips are drawn in the window's own overlay layer instead of each opening a native
            // window of its own (an HWND plus a render target per popup is what made the model picker slow to appear).
            .With(new Win32PlatformOptions { OverlayPopups = true, RenderingMode = Environment.GetEnvironmentVariable("FORGE_RENDER") == "soft" ? [Win32RenderingMode.Software] : [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software] })
            .With(new X11PlatformOptions { OverlayPopups = true })
            .With(new AvaloniaNativePlatformOptions { OverlayPopups = true })
            .WithInterFont()
            .LogToForge();

    /// <summary>Like <c>LogToTrace</c>, set while the builder is configured so setup-time warnings are kept too.</summary>
    private static AppBuilder LogToForge(this AppBuilder builder)
    {
        Logger.Sink = new AvaloniaLogSink();
        return builder;
    }
}
