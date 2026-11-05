using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Forge.Plugin.Sdk;

namespace Forge.Scripting.Tests;

/// <summary>Disposable child-process harness for the non-cooperative loop tests.</summary>
internal static class ScriptCodeChildProcess
{
    internal const string ModeVariable = "FORGE_SCRIPTING_TEST_CHILD_MODE";
    internal const string TimeoutLoopMode = "timeout-loop";
    internal const string CancelLoopMode = "cancel-loop";
    internal const string ResultPrefix = "RESULT";

    internal static Process Start(string mode)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var info = new ProcessStartInfo(string.IsNullOrEmpty(host) ? "dotnet" : host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(typeof(ScriptCodeChildProcess).Assembly.Location);
        info.Environment[ModeVariable] = mode;
        return Process.Start(info) ?? throw new InvalidOperationException("The child test process did not start.");
    }

    internal static string? WaitForLine(Process process, TimeSpan timeout)
    {
        var pending = Task.Run(process.StandardOutput.ReadLine);
        return pending.Wait(timeout) ? pending.Result : null;
    }

    [ModuleInitializer]
    internal static void RunWhenRequested()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ModeVariable)))
        {
            return;
        }

        var mode = Environment.GetEnvironmentVariable(ModeVariable)!;
        try
        {
            Run(mode);
        }
        catch (Exception exception)
        {
            Console.Out.WriteLine($"{ResultPrefix} fatal={exception.GetType().Name}: {exception.Message}");
            Console.Out.Flush();
        }

        // The abandoned script is still running here by design; the parent reaps this process.
        Console.In.ReadLine();
        Environment.Exit(0);
    }

    private static void Run(string mode)
    {
        var runner = new ScriptCodeRunner();
        var context = new ExtensionCallContext("child-run", "child-session", "/tmp", 0, []);
        RecordingHostFacade host;
        CodeExecutionResult result;
        if (string.Equals(mode, TimeoutLoopMode, StringComparison.Ordinal))
        {
            host = new RecordingHostFacade();
            var request = new CodeExecutionRequest("while (true) { }", context, 500, [], new CodeExecutionOptions());
            result = runner.ExecuteAsync(request, host, CancellationToken.None).GetAwaiter().GetResult();
        }
        else if (string.Equals(mode, CancelLoopMode, StringComparison.Ordinal))
        {
            // Warms the facade call path on this thread before the run below.
            var warmupHost = new RecordingHostFacade
            {
                Handler = (_, _, _) => Task.FromResult(new HostToolResult("warm")),
            };
            using (var warmup = JsonDocument.Parse("{}"))
            {
                warmupHost.CallToolAsync("started", warmup.RootElement.Clone(), CancellationToken.None).GetAwaiter().GetResult();
            }

            var cts = new CancellationTokenSource();
            host = new RecordingHostFacade
            {
                Handler = (_, _, _) =>
                {
                    cts.Cancel();
                    return Task.FromResult(new HostToolResult("go"));
                },
            };
            var tools = new[] { new ToolDeclaration("started", "started", "{}", ToolSideEffect.ReadOnly) };
            var request = new CodeExecutionRequest(
                "await Tools.Call(\"started\"); while (true) { }", context, 60_000, tools, new CodeExecutionOptions());
            result = runner.ExecuteAsync(request, host, cts.Token).GetAwaiter().GetResult();
        }
        else
        {
            throw new InvalidOperationException($"Unknown child test mode '{mode}'.");
        }

        var flat = result.Content.Replace("\r", " ").Replace('\n', ' ');
        if (flat.Length > 600)
        {
            flat = flat[..600];
        }

        Console.Out.WriteLine(
            $"{ResultPrefix} IsError={result.IsError} RequiresTermination={result.RequiresTermination} Calls={host.CallCount} Content={flat}");
        Console.Out.Flush();
    }
}
