using System.Diagnostics;
using Avalonia;

namespace Forge.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var traceFile = Environment.GetEnvironmentVariable("FORGE_TRACE_FILE");
        if (!string.IsNullOrWhiteSpace(traceFile))
        {
            Trace.Listeners.Add(new TextWriterTraceListener(traceFile) { TraceOutputOptions = TraceOptions.None });
            Trace.AutoFlush = true;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
