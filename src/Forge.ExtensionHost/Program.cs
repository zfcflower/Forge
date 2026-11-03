using System.Runtime.InteropServices;

namespace Forge.ExtensionHost;

/// <summary>
/// Entry point of the extension host: a resident, JIT-side process that the main program starts and talks to over a
/// named pipe. Exit codes: 0 when <c>host.stop</c> or a closed main-program pipe ends the run, 2 for an unusable command
/// line, 1 when the pipe could not be opened.
/// </summary>
public static class Program
{
    public const string Usage =
        """
        forge-extension-host --pipe <name> [--plugins <dir>] [--data <dir>] [--dotnet <path>] [--worker <path>] [--tool-timeout <seconds>] [--concurrency <n>]
          --pipe <name>           named pipe the main program connects to (required)
          --plugins <dir>         directory with one sub-directory per plugin (default <data>/plugins)
          --data <dir>            host state directory, holds approvals.json (default %LOCALAPPDATA%/Forge/extension-host)
          --dotnet <path>         runtime used to launch workers from a .dll (default: the dotnet that runs the host)
          --worker <path>         Forge.PluginWorker.dll/.exe (default: $FORGE_PLUGIN_WORKER, then next to the host)
          --tool-timeout <s>      per-call deadline before the worker process tree is killed (default 120)
          --concurrency <n>       in-flight calls per worker (default 4)
        """;

    public static async Task<int> Main(string[] args)
    {
        HostOptions options;
        try
        {
            options = HostOptions.Parse(args);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            Console.Error.WriteLine($"forge-extension-host: {exception.Message}");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        using var termination = RegisterTermination(cancellation);

        if (options.WorkerPath is null)
        {
            Console.Error.WriteLine(
                "forge-extension-host: no plugin worker was found; approved plugins cannot run until --worker or " +
                $"{HostOptions.WorkerEnvironmentVariable} points at {HostOptions.WorkerExecutableName}.dll/.exe.");
        }

        ExtensionHost host;
        try
        {
            host = new ExtensionHost(options);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"forge-extension-host: the host state in '{options.DataDirectory}' is unusable: {exception.Message}");
            return 1;
        }

        await using (host)
        {
            return await host.RunAsync(cancellation.Token).ConfigureAwait(false);
        }
    }

    private static PosixSignalRegistration? RegisterTermination(CancellationTokenSource cancellation)
    {
        try
        {
            return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            });
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
