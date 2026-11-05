using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.HostClient;
using Microsoft.Data.Sqlite;

namespace Forge.Smoke;

/// <summary>
/// Headless verifier for the Native AOT ↔ JIT boundary: it starts the real JIT Extension Host (which spawns the real
/// PluginWorker and one single-use ScriptExecutor per script), connects the AOT-safe <see cref="ExtensionHostClient"/>
/// over a named pipe and drives <c>plugins.status</c> plus <c>run_code</c> round trips through the client's public
/// surface. Nothing here uses reflection, Roslyn, <c>dynamic</c> or AssemblyLoadContext.
/// </summary>
internal static class Program
{
    private const string TrivialScript = "return \"smoke-ok\";";

    private const string CallbackScript =
        "return await Tools.Call(\"" + SmokeEchoTool.Name + "\", new { text = \"callback\" });";

    private const string CallbackMarker = SmokeEchoTool.Marker + "callback";

    private const int OneLineLimit = 200;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(180);

    private const string Usage =
        """
        forge-smoke [--host <Forge.ExtensionHost.dll>] [--worker <Forge.PluginWorker.dll>] [--executor <Forge.ScriptExecutor.dll>] [--plugins <dir>]

          --host <path>       JIT Extension Host assembly (default: this tool's directory, then the walking-up sibling layout)
          --worker <path>     Forge.PluginWorker.dll the host spawns for approved plugins
          --executor <path>   Forge.ScriptExecutor.dll the host spawns once per run_code call
          --plugins <dir>     plugin directory the host scans (default: an empty temp directory)
          -h, --help          this text

        Each JIT assembly is resolved from the switch, then FORGE_SMOKE_JIT_DIR, then this tool's own directory, then the
        walking-up sibling layout (<root>/src/<project>/bin/<configuration>/net10.0, <root>/<project>/bin/<configuration>,
        <root>/artifacts/publish/<project>, <root>/publish/<project>), preferring the configuration this tool was built in.
        The host is started as `dotnet <host.dll> --pipe <name> --plugins <dir> --data <dir>` with FORGE_PLUGIN_WORKER and
        FORGE_SCRIPT_EXECUTOR pointing at the resolved worker and executor - the same convention the integration tests use.
        Exit codes: 0 = SMOKE OK, 1 = a verification step failed, 2 = usage or JIT-side lookup failed.
        """;

    public static async Task<int> Main(string[] args)
    {
        if (HasFlag(args, "--help") || HasFlag(args, "-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        SmokeOptions options;
        try
        {
            options = SmokeOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine("forge-smoke: " + exception.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        Console.WriteLine("forge-smoke: Native AOT extension-host roundtrip verifier");

        JitLayout layout;
        try
        {
            layout = JitLayout.Locate(options);
        }
        catch (SmokeFailure failure)
        {
            Report(failure);
            return 2;
        }

        Console.WriteLine("  host:     " + layout.HostPath);
        Console.WriteLine("  worker:   " + layout.WorkerPath);
        Console.WriteLine("  executor: " + layout.ExecutorPath);

        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            lifetime.Cancel();
        };

        try
        {
            await VerifyAsync(options, layout, lifetime.Token).ConfigureAwait(false);
        }
        catch (SmokeFailure failure)
        {
            Report(failure);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("SMOKE FAILED");
            Console.Error.WriteLine("  step:   canceled");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("SMOKE FAILED");
            Console.Error.WriteLine("  step:   unexpected");
            Console.Error.WriteLine("  reason: " + exception);
            return 1;
        }

        Console.WriteLine("SMOKE OK");
        return 0;
    }

    private static async Task VerifyAsync(SmokeOptions options, JitLayout layout, CancellationToken ct)
    {
        var workRoot = Path.Combine(Path.GetTempPath(), "forge-smoke-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var pluginsDirectory = options.PluginsDirectory is { Length: > 0 } plugins
                ? Path.GetFullPath(plugins)
                : Path.Combine(workRoot, "plugins");
            var dataDirectory = Path.Combine(workRoot, "data");
            Directory.CreateDirectory(pluginsDirectory);
            Directory.CreateDirectory(dataDirectory);

            var sqliteVersion = VerifySqlite(Path.Combine(workRoot, "sqlite"));
            Console.WriteLine("  STEP sqlite in the AOT main: OK (" + sqliteVersion + ")");

            var pipeName = "forge-smoke-" + Guid.NewGuid().ToString("N")[..20];
            Console.WriteLine("  pipe:     " + pipeName);
            Console.WriteLine("  plugins:  " + pluginsDirectory);

            await using var host = HostRun.Start(layout, pipeName, pluginsDirectory, dataDirectory);
            Console.WriteLine("  host pid: " + host.ProcessId.ToString(CultureInfo.InvariantCulture));

            var registry = new SmokeToolRegistry(new SmokeEchoTool());
            var connection = new HostConnection
            {
                PipeName = pipeName,
                HostExecutablePath = null,
                StartTimeout = StartTimeout,
            };
            var clientOptions = new ExtensionHostClientOptions
            {
                PluginsDirectory = pluginsDirectory,
                DataDirectory = dataDirectory,
                RequestTimeout = StepTimeout,
                StopRequestTimeout = TimeSpan.FromSeconds(10),
                ProcessExitTimeout = TimeSpan.FromSeconds(10),
            };

            await using var client = new ExtensionHostClient(
                connection, registry.Resolve, ApproveAll, clientOptions);

            await ConnectToHostAsync(client, host, pipeName, ct).ConfigureAwait(false);
            Console.WriteLine(
                "  STEP start the Extension Host: OK (status " + client.Status +
                ", " + client.Tools.Count.ToString(CultureInfo.InvariantCulture) + " plugin tool(s), " +
                client.Commands.Count.ToString(CultureInfo.InvariantCulture) + " command(s))");

            var statuses = await StepAsync(
                "plugins.status", host, () => client.GetPluginStatusesAsync(ct), ct).ConfigureAwait(false);
            Console.WriteLine(
                "  STEP plugins.status: OK (" + statuses.Count.ToString(CultureInfo.InvariantCulture) + " plugin row(s))");

            var runCode = client.CreateRunCodeTool(registry);
            var context = new ToolContext("forge-smoke", workRoot);

            var trivial = await StepAsync(
                "run_code",
                host,
                () => runCode
                    .InvokeAsync(new ToolCall("smoke-trivial", "run_code", RunCodeArguments(TrivialScript)), context, ct)
                    .AsTask(),
                ct).ConfigureAwait(false);
            RequireNoError("run_code", trivial, host);
            if (!trivial.Content.Contains("smoke-ok", StringComparison.Ordinal))
            {
                throw new SmokeFailure(
                    "run_code",
                    "the script ran but returned '" + OneLine(trivial.Content) + "' instead of smoke-ok.",
                    host.DiagnosticsTail());
            }

            Console.WriteLine("  STEP run_code: OK (result '" + OneLine(trivial.Content) + "')");

            var callback = await StepAsync(
                "run_code + host.tool_call",
                host,
                () => runCode
                    .InvokeAsync(new ToolCall("smoke-callback", "run_code", RunCodeArguments(CallbackScript)), context, ct)
                    .AsTask(),
                ct).ConfigureAwait(false);
            RequireNoError("run_code + host.tool_call", callback, host);
            if (!callback.Content.Contains(CallbackMarker, StringComparison.Ordinal))
            {
                throw new SmokeFailure(
                    "run_code + host.tool_call",
                    "the script called back into the main program but returned '" + OneLine(callback.Content) + "'.",
                    host.DiagnosticsTail());
            }

            Console.WriteLine("  STEP run_code + host.tool_call: OK (result '" + OneLine(callback.Content) + "')");

            await PluginRoundtripAsync(client, host, workRoot, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    /// <summary>
    /// Proves the AOT-compiled main side can open, write and read a SQLite file: the persistence stack is measured, not
    /// declared, to be AOT compatible, so the smoke exercises it instead of assuming it.
    /// </summary>
    private static string VerifySqlite(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "smoke.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using (var create = connection.CreateCommand())
            {
                create.CommandText = "CREATE TABLE smoke (id INTEGER PRIMARY KEY, value TEXT NOT NULL);";
                create.ExecuteNonQuery();
            }

            using (var insert = connection.CreateCommand())
            {
                insert.CommandText = "INSERT INTO smoke (value) VALUES ('smoke-ok');";
                insert.ExecuteNonQuery();
            }

            using var read = connection.CreateCommand();
            read.CommandText = "SELECT sqlite_version(), value FROM smoke LIMIT 1;";
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                throw new SmokeFailure("sqlite in the AOT main", "the probe query returned no row.", null);
            }

            var value = reader.GetString(1);
            if (!string.Equals(value, "smoke-ok", StringComparison.Ordinal))
            {
                throw new SmokeFailure(
                    "sqlite in the AOT main", "the probe read back '" + OneLine(value) + "'.", null);
            }

            return "sqlite " + reader.GetString(0);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or DllNotFoundException or IOException)
        {
            throw new SmokeFailure(
                "sqlite in the AOT main",
                exception.GetType().Name + ": " + exception.Message + " (database '" + path + "')",
                null);
        }
    }

    /// <summary>Waits for the host to accept the pipe, failing fast (with the captured output) when it exits first.</summary>
    private static async Task ConnectToHostAsync(
        ExtensionHostClient client, HostRun host, string pipeName, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(StartTimeout);
        var watchdog = CancelWhenHostExitsAsync(host, budget);
        try
        {
            await client.StartAsync(budget.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("Connecting to the Extension Host was canceled.", exception, ct);
            }

            var exit = host.ExitDescription();
            throw new SmokeFailure(
                "start the Extension Host",
                exit is null
                    ? "the host did not accept the pipe '" + pipeName + "' within " +
                      StartTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s: " + exception.Message
                    : "the host " + exit + " before it accepted the pipe '" + pipeName + "'.",
                host.DiagnosticsTail());
        }
        finally
        {
            await budget.CancelAsync().ConfigureAwait(false);
            await watchdog.ConfigureAwait(false);
        }
    }

    private static async Task CancelWhenHostExitsAsync(HostRun host, CancellationTokenSource budget)
    {
        try
        {
            await host.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            await budget.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static async Task<T> StepAsync<T>(string step, HostRun host, Func<Task<T>> body, CancellationToken ct)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new SmokeFailure(step, exception.GetType().Name + ": " + exception.Message, host.DiagnosticsTail());
        }
    }

    private static void RequireNoError(string step, ToolResult result, HostRun host)
    {
        if (result.IsError)
        {
            throw new SmokeFailure(step, "the host returned an error: " + OneLine(result.Content), host.DiagnosticsTail());
        }
    }

    /// <summary>Proves the plugin leg only when a plugin is actually registered; the smoke never invents one.</summary>
    private static async Task PluginRoundtripAsync(
        ExtensionHostClient client, HostRun host, string workingDirectory, CancellationToken ct)
    {
        if (client.Commands.Count > 0)
        {
            var command = client.Commands[0];
            var name = command.Definition.Name;
            var result = await StepAsync(
                "plugin command '" + name + "'",
                host,
                () => command
                    .ExecuteAsync(string.Empty, new CommandContext("forge-smoke", workingDirectory), ct)
                    .AsTask(),
                ct).ConfigureAwait(false);
            PrintPluginResult("plugin command roundtrip", name, result.IsError, result.Output);
            return;
        }

        if (client.Tools.Count > 0)
        {
            var tool = client.Tools[0];
            var name = tool.Definition.Name;
            var result = await StepAsync(
                "plugin tool '" + name + "'",
                host,
                () => tool
                    .InvokeAsync(
                        new ToolCall("smoke-plugin", name, "{}"),
                        new ToolContext("forge-smoke", workingDirectory),
                        ct)
                    .AsTask(),
                ct).ConfigureAwait(false);
            PrintPluginResult("plugin tool roundtrip", name, result.IsError, result.Content);
            return;
        }

        Console.WriteLine(
            "  STEP plugin tool/command roundtrip: skipped (no plugin is registered; run_code already proved the " +
            "AOT main -> JIT host -> single-use executor path)");
    }

    private static void PrintPluginResult(string what, string subject, bool isError, string? text)
    {
        if (isError)
        {
            Console.WriteLine(
                "  STEP " + what + ": the host answered '" + subject +
                "' with an error (informational; the roundtrip itself was proven): " + OneLine(text ?? string.Empty));
            return;
        }

        Console.WriteLine("  STEP " + what + ": OK ('" + subject + "')");
    }

    private static ValueTask<ApprovalDecision> ApproveAll(ApprovalRequest request, CancellationToken ct) =>
        ValueTask.FromResult(ApprovalDecision.Allow);

    /// <summary>Builds <c>{"code":"..."}</c> with a writer, so the AOT side never needs reflection-based JSON.</summary>
    private static string RunCodeArguments(string code, double? timeoutSeconds = null)
    {
        var buffer = new ArrayBufferWriter<byte>(code.Length + 64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("code", code);
            if (timeoutSeconds is { } seconds)
            {
                writer.WriteNumber("timeout_seconds", seconds);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string OneLine(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, OneLineLimit));
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (builder.Length >= OneLineLimit)
            {
                builder.Append("...");
                break;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static void Report(SmokeFailure failure)
    {
        Console.Error.WriteLine("SMOKE FAILED");
        Console.Error.WriteLine("  step:   " + failure.Step);
        Console.Error.WriteLine("  reason: " + failure.Message);
        if (string.IsNullOrWhiteSpace(failure.HostOutput))
        {
            return;
        }

        Console.Error.WriteLine("  Extension Host output (tail):");
        foreach (var line in failure.HostOutput.Split('\n'))
        {
            Console.Error.WriteLine("    " + line.TrimEnd('\r'));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static bool HasFlag(IReadOnlyList<string> args, string flag)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], flag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Command line of the smoke verifier; the three JIT paths are optional overrides.</summary>
internal sealed record SmokeOptions
{
    public string? HostPath { get; init; }

    public string? WorkerPath { get; init; }

    public string? ExecutorPath { get; init; }

    public string? PluginsDirectory { get; init; }

    public static SmokeOptions Parse(IReadOnlyList<string> args)
    {
        var options = new SmokeOptions();
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--host":
                    options = options with { HostPath = Value(args, ref index) };
                    break;
                case "--worker":
                    options = options with { WorkerPath = Value(args, ref index) };
                    break;
                case "--executor":
                    options = options with { ExecutorPath = Value(args, ref index) };
                    break;
                case "--plugins":
                    options = options with { PluginsDirectory = Value(args, ref index) };
                    break;
                default:
                    throw new ArgumentException("unknown argument '" + args[index] + "'.");
            }
        }

        return options;
    }

    private static string Value(IReadOnlyList<string> args, ref int index)
    {
        var next = index + 1;
        if (next >= args.Count || string.IsNullOrWhiteSpace(args[next]))
        {
            throw new ArgumentException(args[index] + " requires a value.");
        }

        index = next;
        return args[next];
    }
}

/// <summary>The JIT-side executables the host needs: itself, its plugin worker and its single-use script executor.</summary>
internal sealed record JitLayout(string HostPath, string WorkerPath, string ExecutorPath)
{
    private const string HostAssemblyName = "Forge.ExtensionHost";

    private const string WorkerAssemblyName = "Forge.PluginWorker";

    private const string ExecutorAssemblyName = "Forge.ScriptExecutor";

    private const string TargetFramework = "net10.0";

    private const string JitDirectoryEnvironmentVariable = "FORGE_SMOKE_JIT_DIR";

    private const int ProbeDepth = 8;

    private const int MaxReportedProbes = 12;

    private static readonly string[] ConfigurationCandidates =
#if DEBUG
        ["Debug", "Release"];
#else
        ["Release", "Debug"];
#endif

    public static JitLayout Locate(SmokeOptions options) => new(
        Resolve(options.HostPath, HostAssemblyName, "--host"),
        Resolve(options.WorkerPath, WorkerAssemblyName, "--worker"),
        Resolve(options.ExecutorPath, ExecutorAssemblyName, "--executor"));

    private static string Resolve(string? explicitPath, string assemblyName, string switchName)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            if (!File.Exists(full))
            {
                throw new SmokeFailure(
                    "locate the JIT side", switchName + " '" + explicitPath + "' does not exist.", null);
            }

            return full;
        }

        var fileName = assemblyName + ".dll";
        var probed = new List<string>();
        foreach (var directory in CandidateDirectories(fileName))
        {
            probed.Add(directory);
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var details = new StringBuilder()
            .Append(fileName)
            .Append(" was not found. Build the JIT side first (dotnet build Forge.slnx), pass ")
            .Append(switchName)
            .Append(" <path>, or set ")
            .Append(JitDirectoryEnvironmentVariable)
            .Append(".\nSearched:");
        for (var index = 0; index < probed.Count && index < MaxReportedProbes; index++)
        {
            details.Append("\n  ").Append(probed[index]);
        }

        if (probed.Count > MaxReportedProbes)
        {
            details.Append("\n  ... and ").Append(probed.Count - MaxReportedProbes).Append(" more");
        }

        throw new SmokeFailure("locate the JIT side", details.ToString(), null);
    }

    private static IEnumerable<string> CandidateDirectories(string fileName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jitDirectory = Environment.GetEnvironmentVariable(JitDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(jitDirectory))
        {
            var full = Path.GetFullPath(jitDirectory);
            if (seen.Add(full))
            {
                yield return full;
            }
        }

        var start = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; start is not null && depth < ProbeDepth; depth++, start = start.Parent)
        {
            foreach (var directory in DirectoriesUnder(start.FullName, fileName))
            {
                if (seen.Add(directory))
                {
                    yield return directory;
                }
            }
        }
    }

    private static IEnumerable<string> DirectoriesUnder(string root, string fileName)
    {
        var projectName = Path.GetFileNameWithoutExtension(fileName);
        yield return root;

        foreach (var configuration in ConfigurationCandidates)
        {
            foreach (var projectRoot in new[] { Path.Combine(root, "src", projectName), Path.Combine(root, projectName) })
            {
                var bin = Path.Combine(projectRoot, "bin", configuration);
                yield return bin;
                yield return Path.Combine(bin, TargetFramework);
                foreach (var framework in FrameworkDirectories(bin))
                {
                    yield return framework;
                }

                yield return Path.Combine(projectRoot, "publish");
            }

            yield return Path.Combine(root, "bin", configuration);
            yield return Path.Combine(root, "bin", configuration, TargetFramework);
            yield return Path.Combine(root, "artifacts", "publish", projectName, configuration);
            yield return Path.Combine(root, "artifacts", "publish", projectName);
            yield return Path.Combine(root, "publish", projectName);
            yield return Path.Combine(root, "publish", configuration);
        }
    }

    private static IEnumerable<string> FrameworkDirectories(string bin)
    {
        if (!Directory.Exists(bin))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(bin);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>The Extension Host child process: its pid, the captured output tails and its teardown. Everything the host
/// spawns (plugin worker, single-use executors) belongs to its process tree, so killing the tree is enough.</summary>
internal sealed class HostRun : IAsyncDisposable
{
    private const string DotnetRuntime = "dotnet";

    /// <summary>Mirrors <c>HostOptions.WorkerEnvironmentVariable</c>; the host reads it when it spawns a plugin worker.</summary>
    private const string WorkerEnvironmentVariable = "FORGE_PLUGIN_WORKER";

    /// <summary>Mirrors <c>HostOptions.ExecutorEnvironmentVariable</c>; the host reads it when it spawns a script executor.</summary>
    private const string ExecutorEnvironmentVariable = "FORGE_SCRIPT_EXECUTOR";

    private const int TailLines = 40;

    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(10);

    private readonly Process process;

    private int processId;

    private readonly Lock gate = new();

    private readonly Queue<string> stdout = new();

    private readonly Queue<string> stderr = new();

    private bool disposed;

    private HostRun(Process process) => this.process = process;

    public int ProcessId => processId;

    public bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                return true;
            }
        }
    }

    public static HostRun Start(JitLayout layout, string pipeName, string pluginsDirectory, string dataDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = DotnetRuntime,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(layout.HostPath);
        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add("--plugins");
        info.ArgumentList.Add(pluginsDirectory);
        info.ArgumentList.Add("--data");
        info.ArgumentList.Add(dataDirectory);
        info.Environment[WorkerEnvironmentVariable] = layout.WorkerPath;
        info.Environment[ExecutorEnvironmentVariable] = layout.ExecutorPath;

        var process = new Process { StartInfo = info };
        var host = new HostRun(process);
        process.OutputDataReceived += (_, args) => host.Append(host.stdout, args.Data);
        process.ErrorDataReceived += (_, args) => host.Append(host.stderr, args.Data);
        try
        {
            if (!process.Start())
            {
                throw new SmokeFailure(
                    "start the Extension Host",
                    "the process did not start: '" + DotnetRuntime + " " + layout.HostPath + "'.",
                    null);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            process.Dispose();
            throw new SmokeFailure(
                "start the Extension Host",
                "'" + DotnetRuntime + " " + layout.HostPath + "' could not be started: " + exception.Message,
                null);
        }

        host.processId = process.Id;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return host;
    }

    public string? ExitDescription()
    {
        if (!HasExited)
        {
            return null;
        }

        try
        {
            return "exited with code " + process.ExitCode.ToString(CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "exited";
        }
    }

    public async Task WaitForExitAsync(CancellationToken ct)
    {
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    /// <summary>The captured host output, for failure reports.</summary>
    public string DiagnosticsTail()
    {
        var text = new StringBuilder();
        text.Append("host pid ").Append(processId.ToString(CultureInfo.InvariantCulture)).Append(": ");
        text.Append(ExitDescription() ?? "running when this failure was recorded").Append('\n');
        AppendTail(text, "stdout", stdout);
        AppendTail(text, "stderr", stderr);
        return text.ToString().TrimEnd();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (!await WaitForSelfExitAsync(ExitGrace).ConfigureAwait(false))
        {
            KillTree();
            await WaitForSelfExitAsync(KillTimeout).ConfigureAwait(false);
        }

        process.Dispose();
    }

    private async Task<bool> WaitForSelfExitAsync(TimeSpan limit)
    {
        if (HasExited)
        {
            return true;
        }

        using var budget = new CancellationTokenSource(limit);
        try
        {
            await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or ObjectDisposedException)
        {
            return HasExited;
        }
    }

    private void KillTree()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
        }
    }

    private void Append(Queue<string> target, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (gate)
        {
            target.Enqueue(line);
            while (target.Count > TailLines)
            {
                target.Dequeue();
            }
        }
    }

    private void AppendTail(StringBuilder text, string name, Queue<string> lines)
    {
        string[] snapshot;
        lock (gate)
        {
            snapshot = [.. lines];
        }

        if (snapshot.Length == 0)
        {
            text.Append(name).Append(": (empty)\n");
            return;
        }

        text.Append(name).Append(":\n");
        foreach (var line in snapshot)
        {
            text.Append("  ").Append(line).Append('\n');
        }
    }
}

/// <summary>The main-program tool surface the smoke hands to <c>run_code</c>; it also answers the host's tool callbacks.</summary>
internal sealed class SmokeToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> tools = new(StringComparer.Ordinal);

    public SmokeToolRegistry(params ITool[] entries)
    {
        foreach (var entry in entries)
        {
            tools[entry.Definition.Name] = entry;
        }

        Tools = [.. tools.Values];
    }

    public IReadOnlyList<ITool> Tools { get; }

    public bool TryGet(string name, out ITool tool) => tools.TryGetValue(name, out tool!);

    public ITool? Resolve(string name) => tools.TryGetValue(name, out var tool) ? tool : null;
}

/// <summary>One deterministic read-only main-program tool; a script calling it proves the callback and result legs.</summary>
internal sealed class SmokeEchoTool : ITool
{
    public const string Name = "smoke_echo";

    /// <summary>Prefix of the content a successful call returns; the script's result must contain marker + argument.</summary>
    public const string Marker = "smoke-tool-ok:";

    private const string SchemaJson =
        """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}""";

    public ToolDefinition Definition { get; } = new(
        Name,
        "Echoes the 'text' argument back; proves the host.tool_call callback leg of run_code.",
        SchemaJson);

    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

    public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var text = "(absent)";
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("text", out var element) &&
                element.ValueKind == JsonValueKind.String)
            {
                text = element.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
        }

        return ValueTask.FromResult(new ToolResult(Marker + text));
    }
}

/// <summary>One failed verification step, with the host output captured up to that point.</summary>
internal sealed class SmokeFailure : Exception
{
    public SmokeFailure(string step, string message, string? hostOutput)
        : base(message)
    {
        Step = step;
        HostOutput = hostOutput;
    }

    public string Step { get; }

    public string? HostOutput { get; }
}
