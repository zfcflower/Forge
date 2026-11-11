using System.Diagnostics;
using System.Runtime.CompilerServices;
using Forge.Core.Storage;

namespace Forge.Core.Tests.Storage;

/// <summary>
/// Child-process seam for <see cref="StorageSchemaConcurrencyPinTests"/>: opening the test
/// assembly with <c>FORGE_STORAGE_SCHEMA_CHILD_MODE=open</c> opens the same <c>forge.db</c>
/// through <see cref="SqliteSessionStore"/> and exits, so the parent can prove two OS
/// processes can first-open the same fresh database concurrently. Mirrors the proven
/// <c>ApprovalChildProcess</c> pattern in <c>Forge.Approval.Tests</c>.
/// </summary>
internal static class StorageSchemaConcurrencyChildProcess
{
    internal const string ModeVariable = "FORGE_STORAGE_SCHEMA_CHILD_MODE";
    internal const string DirectoryVariable = "FORGE_STORAGE_SCHEMA_CHILD_DIR";
    internal const string OpenMode = "open";
    internal const string ReadyLine = "schema-child-ready";
    internal const string DoneLine = "schema-child-done";

    internal static Process Start(string directory)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var info = new ProcessStartInfo(string.IsNullOrEmpty(host) ? "dotnet" : host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(typeof(StorageSchemaConcurrencyChildProcess).Assembly.Location);
        info.Environment[ModeVariable] = OpenMode;
        info.Environment[DirectoryVariable] = directory;
        return Process.Start(info) ?? throw new InvalidOperationException("The schema child process did not start.");
    }

    [ModuleInitializer]
    internal static void RunWhenRequested()
    {
        var mode = Environment.GetEnvironmentVariable(ModeVariable);
        if (string.IsNullOrEmpty(mode))
        {
            return;
        }

        // The module-initializer thread must return promptly: blocking it on genuinely
        // asynchronous work deadlocks. A foreground worker thread opens the database
        // instead; the host keeps the process alive until the worker exits.
        var worker = new Thread(() =>
        {
            int exitCode;
            try
            {
                if (!string.Equals(mode, OpenMode, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Unknown schema child mode '{mode}'.");
                }

                var directory = Environment.GetEnvironmentVariable(DirectoryVariable)
                    ?? throw new InvalidOperationException("The schema child process has no directory.");
                exitCode = Open(directory);
            }
            catch (Exception exception)
            {
                Console.Out.WriteLine("fatal " + exception.GetType().Name + ": " + exception.Message);
                Console.Out.Flush();
                exitCode = 1;
            }

            Environment.Exit(exitCode);
        })
        {
            IsBackground = false,
            Name = "schema-concurrency-child",
        };
        worker.Start();
    }

    private static int Open(string directory)
    {
        Console.Out.WriteLine(ReadyLine);
        Console.Out.Flush();
        using var store = new SqliteSessionStore(directory);
        store.CreateSessionAsync("schema-child-" + Environment.ProcessId, "child session", CancellationToken.None).GetAwaiter().GetResult();
        Console.Out.WriteLine(DoneLine);
        Console.Out.Flush();
        return 0;
    }
}
