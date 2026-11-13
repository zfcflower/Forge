using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.Ipc;

namespace Forge.HostClient.Tests;

/// <summary>
/// Out-of-process regression harness for the <c>run_code</c> self-registration stack overflow.
/// The old <c>RunCodeProxyTool</c> reads its own <see cref="ITool.Definition"/> from its fingerprint and
/// description paths; once the proxy itself sits in the live registry that recursion overflows the stack
/// and kills the process. It must die here in a child, never in the test runner: the parent
/// (<see cref="RunCodeLiveRegistryTests"/>) runs this scenario through an isolated <c>dotnet test</c>
/// child invocation, then only asserts exit 0 plus explicit stdout markers inside a bound, draining
/// stderr and killing the whole child tree on a hang.
/// </summary>
internal static class RunCodeRegistryChildProcess
{
    internal const string ModeVariable = "FORGE_HOSTCLIENT_TEST_CHILD_MODE";
    internal const string LiveRegistryMode = "live-registry";

    internal const string DefinitionOk = "run_code_live_registry definition ok";
    internal const string InvokeOk = "run_code_live_registry invoke ok tools=[normal]";
    internal const string MutualOk = "run_code_live_registry mutual ok";
    internal const string UpdatesOk = "run_code_live_registry updates ok";
    internal const string SuccessMarker = "run_code_live_registry ok";

    internal static Process Start(string mode)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var assembly = typeof(RunCodeRegistryChildProcess).Assembly.Location;
        var info = new ProcessStartInfo(string.IsNullOrEmpty(host) ? "dotnet" : host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("test");
        info.ArgumentList.Add(assembly);
        info.ArgumentList.Add("--no-build");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(ActiveConfiguration());
        info.ArgumentList.Add("--nologo");
        info.ArgumentList.Add("--filter");
        info.ArgumentList.Add("FullyQualifiedName~Live_registry_child_scenario");
        info.ArgumentList.Add("--logger");
        info.ArgumentList.Add("console;verbosity=detailed");
        info.Environment[ModeVariable] = mode;
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

        if (!string.Equals(mode, LiveRegistryMode, StringComparison.Ordinal))
        {
            Console.Out.WriteLine(
                "fatal " + nameof(InvalidOperationException) + ": Unknown child test mode '" + mode + "'.");
            Console.Out.Flush();
            Environment.Exit(1);
        }
    }

    private static string ActiveConfiguration() =>
        typeof(RunCodeRegistryChildProcess).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
            is string configuration && !string.IsNullOrWhiteSpace(configuration)
            ? configuration
            : "Debug";

    internal static void Check(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("run_code live-registry check failed: " + what + ".");
        }
    }

    internal static List<string> ToolNamesOf(IpcMessage request) =>
        request.Read<Forge.Plugin.Sdk.CodeExecutionRequest>().Tools.Select(tool => tool.Name).ToList();

    /// <summary>A mutable registry that keeps every insertion (unlike the name-keyed test double), so two
    /// <c>run_code</c> proxies can share one live surface the way a real program registry would.</summary>
    internal sealed class LiveListRegistry : IToolRegistry
    {
        private readonly Lock gate = new();
        private readonly List<ITool> tools = [];

        public IReadOnlyList<ITool> Tools
        {
            get
            {
                lock (gate)
                {
                    return [.. tools];
                }
            }
        }

        public bool TryGet(string name, out ITool tool)
        {
            lock (gate)
            {
                foreach (var candidate in tools)
                {
                    if (string.Equals(candidate.Definition.Name, name, StringComparison.Ordinal))
                    {
                        tool = candidate;
                        return true;
                    }
                }
            }

            tool = null!;
            return false;
        }

        public void Add(ITool tool)
        {
            ArgumentNullException.ThrowIfNull(tool);
            lock (gate)
            {
                tools.Add(tool);
            }
        }

        public void Remove(string name)
        {
            lock (gate)
            {
                tools.RemoveAll(candidate =>
                    string.Equals(candidate.Definition.Name, name, StringComparison.Ordinal));
            }
        }
    }
}
