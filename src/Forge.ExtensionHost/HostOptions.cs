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

    public static readonly TimeSpan DefaultToolTimeout = TimeSpan.FromSeconds(120);

    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Named pipe the main program connects to.</summary>
    public required string PipeName { get; init; }

    /// <summary>Directory holding one sub-directory per plugin.</summary>
    public required string PluginsDirectory { get; init; }

    /// <summary>Writable host state directory; holds <c>approvals.json</c>.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Runtime used to launch a worker started from a <c>.dll</c> (<c>--dotnet</c>).</summary>
    public string RuntimePath { get; init; } = DefaultRuntimeName;

    /// <summary>Worker executable or assembly; <c>null</c> when it could not be located.</summary>
    public string? WorkerPath { get; init; }

    /// <summary>Upper bound on one <c>worker.execute</c>; exceeding it kills the worker process tree.</summary>
    public TimeSpan ToolTimeout { get; init; } = DefaultToolTimeout;

    /// <summary>How long a freshly started worker has to connect to its pipe.</summary>
    public TimeSpan ConnectTimeout { get; init; } = DefaultConnectTimeout;

    /// <summary>Deadline applied to requests the host sends over the main-program pipe.</summary>
    public TimeSpan RequestTimeout { get; init; } = DefaultRequestTimeout;

    /// <summary>In-flight tool executions allowed on one worker before further calls wait.</summary>
    public int MaxConcurrentExecutionsPerWorker { get; init; } = 4;

    public static HostOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? pipe = null;
        string? pluginsArgument = null;
        string? dataArgument = null;
        string? runtime = null;
        string? worker = null;
        var toolTimeout = DefaultToolTimeout;
        var concurrency = 4;

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
                case "--tool-timeout":
                    toolTimeout = ParseSeconds(RequireValue(args, ref index, name));
                    break;
                case "--concurrency":
                    concurrency = ParseConcurrency(RequireValue(args, ref index, name));
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

        return new HostOptions
        {
            PipeName = IpcPipe.ValidatePipeName(pipe),
            PluginsDirectory = pluginsDirectory,
            DataDirectory = dataDirectory,
            RuntimePath = string.IsNullOrWhiteSpace(runtime) ? DefaultRuntimePath() : runtime,
            WorkerPath = FindWorkerExecutable(worker),
            ToolTimeout = toolTimeout,
            MaxConcurrentExecutionsPerWorker = concurrency,
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

        var roots = new List<string>(3);
        AddRoot(roots, AppContext.BaseDirectory);
        AddRoot(roots, Path.GetDirectoryName(Environment.ProcessPath));
        AddRoot(roots, SiblingProjectOutput());

        foreach (var root in roots)
        {
            foreach (var extension in new[] { ".dll", ".exe" })
            {
                var candidate = Path.Combine(root, WorkerExecutableName + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>The worker's own build output when both projects are built side by side
    /// (<c>&lt;root&gt;/src/Forge.ExtensionHost/bin/&lt;configuration&gt;/&lt;tfm&gt;</c> →
    /// <c>&lt;root&gt;/src/Forge.PluginWorker/bin/&lt;configuration&gt;/&lt;tfm&gt;</c>), so a host run from the repository
    /// finds its worker without an extra flag.</summary>
    private static string? SiblingProjectOutput()
    {
        var target = new DirectoryInfo(AppContext.BaseDirectory);
        if (target.Parent is not { } configuration ||
            configuration.Parent is not { } projectOutput ||
            projectOutput.Parent?.Parent is not { } projectsRoot)
        {
            return null;
        }

        var sibling = Path.Combine(projectsRoot.FullName, WorkerExecutableName, "bin", configuration.Name, target.Name);
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
