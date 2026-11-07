using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Forge.IO.Tests;

internal static class ChildProcess
{
    internal const string ModeVariable = "FORGE_IO_TEST_CHILD_MODE";
    internal const string PathVariable = "FORGE_IO_TEST_CHILD_PATH";
    internal const string CountVariable = "FORGE_IO_TEST_CHILD_COUNT";
    internal const string BytesVariable = "FORGE_IO_TEST_CHILD_BYTES";

    internal const string HoldLockMode = "hold-lock";
    internal const string IncrementMode = "increment";
    internal const string ReadyLine = "locked";
    internal const string DoneLine = "done";

    /// <summary>Exits with the code given by <see cref="CountVariable"/>.</summary>
    internal const string ExitCodeMode = "exit-code";

    /// <summary>Prints the value of the variable named by <see cref="PathVariable"/>, or <c>&lt;unset&gt;</c>.</summary>
    internal const string PrintEnvironmentMode = "print-env";

    /// <summary>Writes <see cref="CountVariable"/> bytes of <c>x</c> to standard output and exits.</summary>
    internal const string WriteStdoutMode = "write-stdout";

    /// <summary>Writes <see cref="CountVariable"/> bytes to standard output and <see cref="BytesVariable"/> to standard error.</summary>
    internal const string WriteBothMode = "write-both";

    /// <summary>Writes a UTF-8 BOM followed by <c>hello\n</c> to standard output.</summary>
    internal const string WriteBomMode = "write-bom";

    /// <summary>Prints every argument it was given, one per line, between angle brackets.</summary>
    internal const string PrintArgumentsMode = "print-args";

    /// <summary>Writes <c>started\n</c>, flushes it, and then sleeps until it is killed.</summary>
    internal const string SleepAfterOutputMode = "sleep-after-output";

    /// <summary>Starts a grandchild that outlives a naive kill, prints its id, and then sleeps until it is killed.</summary>
    internal const string SpawnGrandchildMode = "spawn-grandchild";

    /// <summary>Reads standard input to its end and prints <c>eof:&lt;bytes&gt;</c>.</summary>
    internal const string ReadStdinMode = "read-stdin";

    private const string UnsetLine = "<unset>";

    /// <summary>The host that runs the test assembly: the muxer <c>dotnet test</c> used, or <c>dotnet</c> from <c>PATH</c>.</summary>
    public static string HostPath =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

    /// <summary>The test assembly, which is also the entry point of every child process.</summary>
    public static string AssemblyPath => typeof(ChildProcess).Assembly.Location;

    public static Process Start(string mode, string path, int count = 0)
    {
        var info = new ProcessStartInfo(HostPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(AssemblyPath);
        info.Environment[ModeVariable] = mode;
        info.Environment[PathVariable] = path;
        info.Environment[CountVariable] = count.ToString(CultureInfo.InvariantCulture);
        return Process.Start(info) ?? throw new InvalidOperationException("The child test process did not start.");
    }

    /// <summary>A <see cref="ProcessRequest"/> that runs this assembly as the child process in <paramref name="mode"/>.</summary>
    public static ProcessRequest Request(
        string mode,
        string workingDirectory,
        int count = 0,
        int bytes = 0,
        string? path = null,
        TimeSpan? timeout = null,
        int maxStdoutBytes = 512 * 1024,
        int maxStderrBytes = 512 * 1024,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ModeVariable] = mode,
            [PathVariable] = path ?? string.Empty,
            [CountVariable] = count.ToString(CultureInfo.InvariantCulture),
            [BytesVariable] = bytes.ToString(CultureInfo.InvariantCulture),
        };

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                variables[pair.Key] = pair.Value;
            }
        }

        return new ProcessRequest
        {
            FileName = HostPath,
            Arguments = [AssemblyPath],
            WorkingDirectory = workingDirectory,
            Timeout = timeout ?? TimeSpan.FromSeconds(60),
            MaxStdoutBytes = maxStdoutBytes,
            MaxStderrBytes = maxStderrBytes,
            Environment = variables,
        };
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
            case ExitCodeMode:
            {
                Environment.Exit(int.Parse(Environment.GetEnvironmentVariable(CountVariable) ?? "0", CultureInfo.InvariantCulture));
                return;
            }
            case PrintEnvironmentMode:
            {
                var value = Environment.GetEnvironmentVariable(path);
                WriteText((value ?? UnsetLine) + "\n");
                return;
            }
            case WriteStdoutMode:
            {
                using (var output = Console.OpenStandardOutput())
                {
                    WriteBytes(output, Count(), (byte)'x');
                }

                return;
            }
            case WriteBothMode:
            {
                using (var output = Console.OpenStandardOutput())
                using (var error = Console.OpenStandardError())
                {
                    WriteBytes(output, Count(), (byte)'o');
                    WriteBytes(error, ExtraBytes(), (byte)'e');
                }

                return;
            }
            case WriteBomMode:
            {
                using (var output = Console.OpenStandardOutput())
                {
                    output.Write([0xEF, 0xBB, 0xBF]);
                    output.Write(Encoding.UTF8.GetBytes("hello\n"));
                    output.Flush();
                }

                return;
            }
            case PrintArgumentsMode:
            {
                var builder = new StringBuilder();
                foreach (var argument in Environment.GetCommandLineArgs()[1..])
                {
                    builder.Append('<').Append(argument).Append(">\n");
                }

                WriteText(builder.ToString());
                return;
            }
            case SleepAfterOutputMode:
            {
                WriteText("started\n");
                Thread.Sleep(Timeout.Infinite);
                return;
            }
            case SpawnGrandchildMode:
            {
                using var grandchild = StartGrandchild();
                var id = grandchild.Id.ToString(CultureInfo.InvariantCulture);
                if (path.Length > 0)
                {
                    // Written before the sleep so a test that only sees the cancellation still learns the pid.
                    File.WriteAllText(path, id);
                }

                WriteText(id + "\n");
                Thread.Sleep(Timeout.Infinite);
                return;
            }
            case ReadStdinMode:
            {
                var total = 0;
                using (var input = Console.OpenStandardInput())
                {
                    var buffer = new byte[4096];
                    while (true)
                    {
                        var read = input.Read(buffer, 0, buffer.Length);
                        if (read == 0)
                        {
                            break;
                        }

                        total += read;
                    }
                }

                WriteText("eof:" + total.ToString(CultureInfo.InvariantCulture) + "\n");
                return;
            }
            default:
                throw new InvalidOperationException($"Unknown child test mode '{mode}'.");
        }
    }

    private static int Count() =>
        int.Parse(Environment.GetEnvironmentVariable(CountVariable) ?? "0", CultureInfo.InvariantCulture);

    private static int ExtraBytes() =>
        int.Parse(Environment.GetEnvironmentVariable(BytesVariable) ?? "0", CultureInfo.InvariantCulture);

    private static void WriteText(string text)
    {
        using var output = Console.OpenStandardOutput();
        output.Write(Encoding.UTF8.GetBytes(text));
        output.Flush();
    }

    private static void WriteBytes(Stream stream, int count, byte value)
    {
        var buffer = new byte[Math.Min(64 * 1024, Math.Max(1, count))];
        Array.Fill(buffer, value);
        var written = 0;
        while (written < count)
        {
            var take = Math.Min(count - written, buffer.Length);
            stream.Write(buffer, 0, take);
            written += take;
        }

        stream.Flush();
    }

    private static Process StartGrandchild()
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } command ? command : "cmd.exe")
            : new ProcessStartInfo("/bin/sh");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        if (OperatingSystem.IsWindows())
        {
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add("ping");
            info.ArgumentList.Add("-n");
            info.ArgumentList.Add("20");
            info.ArgumentList.Add("127.0.0.1");
        }
        else
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("sleep 20");
        }

        return Process.Start(info) ?? throw new InvalidOperationException("The grandchild process did not start.");
    }

    private static TestState Increment(TestState state)
    {
        state.Count++;
        return state;
    }
}
