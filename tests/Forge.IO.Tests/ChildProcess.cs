using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Forge.IO.Tests;

internal static class ChildProcess
{
    internal const string ModeVariable = "FORGE_IO_TEST_CHILD_MODE";
    internal const string PathVariable = "FORGE_IO_TEST_CHILD_PATH";
    internal const string CountVariable = "FORGE_IO_TEST_CHILD_COUNT";
    internal const string HoldLockMode = "hold-lock";
    internal const string IncrementMode = "increment";
    internal const string ReadyLine = "locked";
    internal const string DoneLine = "done";

    public static Process Start(string mode, string path, int count = 0)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var info = new ProcessStartInfo(string.IsNullOrEmpty(host) ? "dotnet" : host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(typeof(ChildProcess).Assembly.Location);
        info.Environment[ModeVariable] = mode;
        info.Environment[PathVariable] = path;
        info.Environment[CountVariable] = count.ToString(CultureInfo.InvariantCulture);
        return Process.Start(info) ?? throw new InvalidOperationException("The child test process did not start.");
    }

    [ModuleInitializer]
    internal static void RunWhenRequested()
    {
        var mode = Environment.GetEnvironmentVariable(ModeVariable);
        if (string.IsNullOrEmpty(mode))
        {
            return;
        }

        var path = Environment.GetEnvironmentVariable(PathVariable)
            ?? throw new InvalidOperationException("The child test process has no path.");
        switch (mode)
        {
            case HoldLockMode:
            {
                _ = FileLock.Acquire(path, TestSupport.TestLimit);
                Console.Out.WriteLine(ReadyLine);
                Console.Out.Flush();
                Thread.Sleep(Timeout.Infinite);
                return;
            }
            case IncrementMode:
            {
                var count = int.Parse(Environment.GetEnvironmentVariable(CountVariable) ?? "0", CultureInfo.InvariantCulture);
                var store = TestSupport.NewStore(path, TestSupport.TestLimit);
                for (var i = 0; i < count; i++)
                {
                    store.Update(Increment);
                }

                Console.Out.WriteLine(DoneLine);
                Console.Out.Flush();
                Environment.Exit(0);
                return;
            }
            default:
                throw new InvalidOperationException($"Unknown child test mode '{mode}'.");
        }
    }

    private static TestState Increment(TestState state)
    {
        state.Count++;
        return state;
    }
}
