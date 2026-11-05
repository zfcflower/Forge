using System.Globalization;
using Forge.Ipc;

namespace Forge.ExtensionHost;

/// <summary>Parsed command line of the extension host process. Unknown switches are ignored so the main program can
/// pass extra flags without breaking the host.</summary>
public sealed record HostOptions
{
    public const string DefaultRuntimeName = "dotnet";

    public const string WorkerExecutableName = "Forge.PluginWorker";

    public const string WorkerEnvironmentVariable = "FORGE_PLUGIN_WORKER";

    public const string ExecutorExecutableName = "Forge.ScriptExecutor";

    public const string ExecutorEnvironmentVariable = "FORGE_SCRIPT_EXECUTOR";

    public static readonly TimeSpan DefaultToolTimeout = TimeSpan.FromSeconds(120);

    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Script workers the host runs scripts on when <c>--script-workers</c> is not given.</summary>
    public const int DefaultScriptWorkerCount = 4;

    public const int MaxScriptWorkers = 16;

    /// <summary>Named pipe the main program connects to.</summary>
    public required string PipeName { get; init; }

    /// <summary>Directory holding one sub-directory per plugin.</summary>
    public required string PluginsDirectory { get; init; }

    /// <summary>Writable host state directory; holds the approval database <c>approvals.db</c>.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Runtime used to launch a worker started from a <c>.dll</c> (<c>--dotnet</c>).</summary>
    public string RuntimePath { get; init; } = DefaultRuntimeName;

    /// <summary>Worker executable or assembly; <c>null</c> when it could not be located.</summary>
    public string? WorkerPath { get; init; }

    /// <summary>Single-use script executor executable or assembly; <c>null</c> when it could not be located.</summary>
    public string? ExecutorPath { get; init; }

    /// <summary>The executor path <c>--executor</c> or <see cref="ExecutorEnvironmentVariable"/> named, before it was
    /// resolved. A value here that does not exist is a hard <c>executor_missing</c> failure; it is never a reason to fall
    /// back to discovery, because that would run an executor nobody named.</summary>
    public string? RequestedExecutorPath { get; init; }

    /// <summary>Upper bound on one <c>worker.execute</c>; exceeding it kills the worker process tree.</summary>
    public TimeSpan ToolTimeout { get; init; } = DefaultToolTimeout;

    /// <summary>How long a freshly started worker has to connect to its pipe.</summary>
    public TimeSpan ConnectTimeout { get; init; } = DefaultConnectTimeout;

    /// <summary>In-flight tool executions allowed on one worker before further calls wait.</summary>
    public int MaxConcurrentExecutionsPerWorker { get; init; } = 4;

    /// <summary>Upper bound on the script executions running at the same time; each one owns a fresh executor process.</summary>
    public int ScriptWorkerCount { get; init; } = DefaultScriptWorkerCount;

    public static HostOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? pipe = null;
        string? pluginsArgument = null;
        string? dataArgument = null;
        string? runtime = null;
        string? worker = null;
        string? executor = null;
        var toolTimeout = DefaultToolTimeout;
        var concurrency = 4;
        var scriptWorkers = DefaultScriptWorkerCount;

        for (var index = 0; index < args.Count; index++)
        {
            var name = args[index];
            switch (name)
            {
                case "--pipe":
                    pipe = RequireValue(args, ref index, name);
                    break;
                case "--plugins":
                    pluginsArgument = RequireValue(args, ref index, name);
                    break;
                case "--data":
                    dataArgument = RequireValue(args, ref index, name);
                    break;
                case "--dotnet":
                    runtime = RequireValue(args, ref index, name);
                    break;
                case "--worker":
                    worker = RequireValue(args, ref index, name);
                    break;
                case "--executor":
                    executor = RequireValue(args, ref index, name);
                    break;
                case "--tool-timeout":
                    toolTimeout = ParseSeconds(RequireValue(args, ref index, name));
                    break;
                case "--concurrency":
                    concurrency = ParseConcurrency(RequireValue(args, ref index, name));
                    break;
                case "--script-workers":
                    scriptWorkers = ParseScriptWorkers(RequireValue(args, ref index, name));
                    break;
                default:
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(pipe))
        {
            throw new ArgumentException("--pipe <name> is required.", nameof(args));
        }

        var dataDirectory = string.IsNullOrWhiteSpace(dataArgument)
            ? DefaultDataDirectory()
            : Path.GetFullPath(dataArgument);
        var pluginsDirectory = string.IsNullOrWhiteSpace(pluginsArgument)
            ? Path.Combine(dataDirectory, "plugins")
            : Path.GetFullPath(pluginsArgument);
        var requestedExecutor = string.IsNullOrWhiteSpace(executor)
            ? Environment.GetEnvironmentVariable(ExecutorEnvironmentVariable)
            : executor;

        return new HostOptions
        {
            PipeName = IpcPipe.ValidatePipeName(pipe),
            PluginsDirectory = pluginsDirectory,
            DataDirectory = dataDirectory,
            RuntimePath = string.IsNullOrWhiteSpace(runtime) ? DefaultRuntimePath() : runtime,
            WorkerPath = FindWorkerExecutable(worker),
            ExecutorPath = FindExecutorExecutable(requestedExecutor),
            RequestedExecutorPath = string.IsNullOrWhiteSpace(requestedExecutor) ? null : requestedExecutor,
            ToolTimeout = toolTimeout,
            MaxConcurrentExecutionsPerWorker = concurrency,
            ScriptWorkerCount = scriptWorkers,
        };
    }

    /// <summary>The current process when it is the <c>dotnet</c> muxer, so a host started through it launches workers
    /// with the same runtime; otherwise the <c>dotnet</c> on <c>PATH</c>.</summary>
    public static string DefaultRuntimePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), DefaultRuntimeName, StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return DefaultRuntimeName;
    }

    public static string DefaultDataDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local)
            ? Path.Combine(Path.GetTempPath(), "forge-extension-host")
            : Path.Combine(local, "Forge", "extension-host");
    }

    /// <summary>Resolves the worker to launch: an explicit path, then <see cref="WorkerEnvironmentVariable"/>, then
    /// <c>Forge.PluginWorker.dll</c>/<c>.exe</c> next to the host executable.</summary>
    public static string? FindWorkerExecutable(string? explicitPath = null, string? environmentValue = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            return File.Exists(full) ? full : null;
        }

        var fromEnvironment = string.IsNullOrWhiteSpace(environmentValue)
            ? Environment.GetEnvironmentVariable(WorkerEnvironmentVariable)
            : environmentValue;
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            var full = Path.GetFullPath(fromEnvironment);
            return File.Exists(full) ? full : null;
        }

        return FindSiblingExecutable(WorkerExecutableName);
    }

    /// <summary>Looks for <paramref name="executableName"/> next to the host, next to its process, then in that project's
    /// own build output.</summary>
    private static string? FindSiblingExecutable(string executableName)
    {
        var roots = new List<string>(3);
        AddRoot(roots, AppContext.BaseDirectory);
        AddRoot(roots, Path.GetDirectoryName(Environment.ProcessPath));
        AddRoot(roots, SiblingProjectOutput(executableName));

        foreach (var root in roots)
        {
            foreach (var extension in new[] { ".dll", ".exe" })
            {
                var candidate = Path.Combine(root, executableName + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Resolves the single-use script executor to launch: an explicit path, then
    /// <see cref="ExecutorEnvironmentVariable"/>, then <c>Forge.ScriptExecutor.dll</c>/<c>.exe</c> next to the host.</summary>
    public static string? FindExecutorExecutable(string? explicitPath = null, string? environmentValue = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            return File.Exists(full) ? full : null;
        }

        var fromEnvironment = string.IsNullOrWhiteSpace(environmentValue)
            ? Environment.GetEnvironmentVariable(ExecutorEnvironmentVariable)
            : environmentValue;
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            var full = Path.GetFullPath(fromEnvironment);
            return File.Exists(full) ? full : null;
        }

        return FindSiblingExecutable(ExecutorExecutableName);
    }

    /// <summary>The worker's own build output when both projects are built side by side
    /// (<c>&lt;root&gt;/src/Forge.ExtensionHost/bin/&lt;configuration&gt;/&lt;tfm&gt;</c> →
    /// <c>&lt;root&gt;/src/Forge.PluginWorker/bin/&lt;configuration&gt;/&lt;tfm&gt;</c>), so a host run from the repository
    /// finds its worker without an extra flag.</summary>
    private static string? SiblingProjectOutput(string projectName)
    {
        var target = new DirectoryInfo(AppContext.BaseDirectory);
        if (target.Parent is not { } configuration ||
            configuration.Parent is not { } projectOutput ||
            projectOutput.Parent?.Parent is not { } projectsRoot)
        {
            return null;
        }

        var sibling = Path.Combine(projectsRoot.FullName, projectName, "bin", configuration.Name, target.Name);
        return Directory.Exists(sibling) ? sibling : null;
    }

    private static void AddRoot(List<string> roots, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var full = Path.GetFullPath(root);
        if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            roots.Add(full);
        }
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string name)
    {
        var next = index + 1;
        if (next >= args.Count)
        {
            throw new ArgumentException($"{name} requires a value.");
        }

        var value = args[next];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} requires a non-empty value.");
        }

        index = next;
        return value;
    }

    private static int ParseConcurrency(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var concurrency) ||
            concurrency < 1 ||
            concurrency > 64)
        {
            throw new FormatException($"'{value}' is not a valid concurrency (1 <= n <= 64).");
        }

        return concurrency;
    }

    private static int ParseScriptWorkers(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var workers) ||
            workers < 1 ||
            workers > MaxScriptWorkers)
        {
            throw new FormatException($"'{value}' is not a valid script worker count (1 <= n <= {MaxScriptWorkers}).");
        }

        return workers;
    }

    private static TimeSpan ParseSeconds(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            double.IsNaN(seconds) ||
            seconds <= 0 ||
            seconds > 86_400)
        {
            throw new FormatException($"'{value}' is not a valid number of seconds (0 < seconds <= 86400).");
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
