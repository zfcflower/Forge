using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Forge.Abstractions;
using Forge.HostClient;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>Supervisor/host-path tests for the single-use executor, driven through the REAL host and its
/// <c>ScriptExecutorSupervisor</c> (never by speaking to the executor pipe directly). The supervisor surface
/// exposes no executor pid and this suite adds no production hook, so process identity is read from the OS
/// process tree (<see cref="HostProcessTree"/>): the executor is a direct child of the host process, the
/// plugin worker's pid is known via its <c>pid</c> tool, and everything else is derived by set difference.
/// No sleep is used for synchronization: every wait rides the fixture's bounded barrier helpers.</summary>
[Collection("SingleUseExecutorFilesystem")]
public sealed class SingleUseExecutorHostTests
{
    [Fact]
    public async Task MissingExecutor_FailsClosed_WithExecutorMissing()
    {
        // The shadow directory contains everything the host needs except Forge.ScriptExecutor.*. Pointing
        // --executor at the (absent) executor path there makes HostOptions resolve ExecutorPath to null, so the
        // supervisor must fail the call with executor_missing instead of falling back to in-process execution.
        var shadow = ShadowHostWithoutExecutor();
        try
        {
            var missingExecutor = Path.Combine(shadow, "Forge.ScriptExecutor.dll");
            Assert.False(File.Exists(missingExecutor), "The shadow host must not contain a script executor.");
            await using var fx = new E2eFixture(
                hostArguments: ["--executor", missingExecutor],
                toolResolver: _ => null,
                approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow),
                hostExecutablePath: Path.Combine(shadow, "Forge.ExtensionHost.dll"));
            fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
            await fx.StartAsync();
            await fx.ApproveViaStatusAsync("alpha");
            var hostPid = fx.Client.HostProcessId ?? throw new InvalidOperationException("The client did not start a host.");
            var workerPid = await fx.WorkerPidAsync("alpha");
            Assert.True(workerPid > 0, "The plugin worker has no process id.");
            var baseline = new HashSet<int>(HostProcessTree.GetChildPids(hostPid));
            Assert.Contains(workerPid, baseline);

            var registry = new ScriptRegistry();
            var first = await RunScriptAsync(fx, registry, "return 1;", "missing-executor");
            Assert.True(first.IsError, first.Content);
            Assert.Contains("executor_missing", first.Content, StringComparison.Ordinal);

            // Fail-closed and deterministic: the host is still fully alive (plugin status answers, the worker
            // is untouched) and a second call fails the same way, with no executor process spawned or lingering
            // in between: the host's children are exactly the pre-call baseline both times.
            var statuses = await fx.Client.GetPluginStatusesAsync(fx.Ct);
            Assert.NotEmpty(statuses);
            Assert.Equal(
                baseline,
                new HashSet<int>(HostProcessTree.GetChildPids(hostPid)));
            var second = await RunScriptAsync(fx, registry, "return 2;", "missing-executor-again");
            Assert.True(second.IsError, second.Content);
            Assert.Contains("executor_missing", second.Content, StringComparison.Ordinal);
            Assert.Equal(
                baseline,
                new HashSet<int>(HostProcessTree.GetChildPids(hostPid)));
        }
        finally
        {
            DeleteDirectory(shadow);
        }
    }

    [Fact]
    public async Task TwoScriptCallsThroughHost_UseFreshExecutorProcesses_AndLeaveNoneBehind()
    {
        var gate = new GateTool();
        await using var fx = new E2eFixture(
            toolResolver: name => name == GateTool.Name ? gate : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveViaStatusAsync("alpha");
        var hostPid = fx.Client.HostProcessId ?? throw new InvalidOperationException("The client did not start a host.");
        var workerPid = await fx.WorkerPidAsync("alpha");
        Assert.True(workerPid > 0, "The plugin worker has no process id.");
        var baseline = new HashSet<int>(HostProcessTree.GetChildPids(hostPid));
        Assert.Contains(workerPid, baseline);

        // Two calls through the host: each must run in its own executor process (spawn per call, never reused),
        // and after each call returns its executor must be gone (kill+wait before return).
        var firstPid = await RunGatedScriptAsync(fx, gate, baseline, hostPid, "run-one");
        var secondPid = await RunGatedScriptAsync(fx, gate, baseline, hostPid, "run-two");
        Assert.NotEqual(firstPid, secondPid);
    }

    [Fact]
    public async Task HangingScriptBeyondBudget_IsKilled_AndExecutorDisappears()
    {
        var gate = new GateTool();
        await using var fx = new E2eFixture(
            toolResolver: name => name == GateTool.Name ? gate : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveViaStatusAsync("alpha");
        var hostPid = fx.Client.HostProcessId ?? throw new InvalidOperationException("The client did not start a host.");
        var workerPid = await fx.WorkerPidAsync("alpha");
        var baseline = new HashSet<int>(HostProcessTree.GetChildPids(hostPid));
        Assert.Contains(workerPid, baseline);

        // The gate records entry and never releases: the script hangs past its 5 s product budget, the host kills
        // the whole executor tree, and the executor pid disappears.
        var registry = new ScriptRegistry(gate);
        var pending = RunScriptAsync(
            fx, registry, "await Tools.Call(\"gate\", new {}); while (true) {}", "hang", timeoutSeconds: 5);
        await fx.WaitUntilAsync(() => !gate.Entered.IsEmpty, "the hanging script to reach its gate call");
        var during = new HashSet<int>(HostProcessTree.GetChildPids(hostPid));
        during.ExceptWith(baseline);
        var executorPid = Assert.Single(during);

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(60), fx.Ct);
        Assert.True(result.IsError, result.Content);
        // Either 5 s enforcer may answer first through run_code; both prove the budget path.
        Assert.True(
            result.Content.Contains("did not finish within 5 s; its whole executor process tree was killed", StringComparison.Ordinal)
                || result.Content.Contains("was stopped after 5 s (run_code timeout)", StringComparison.Ordinal),
            result.Content);
        await fx.WaitUntilAsync(
            () => new HashSet<int>(HostProcessTree.GetChildPids(hostPid)).SetEquals(baseline),
            "the killed executor to be gone after the hung call returned");
        Assert.False(E2eFixture.IsRunning(executorPid), $"The killed executor (pid {executorPid}) survived the budget kill.");
    }

    private static async Task<int> RunGatedScriptAsync(
        E2eFixture fx, GateTool gate, HashSet<int> baseline, int hostPid, string marker)
    {
        gate.Reset();
        var registry = new ScriptRegistry(gate);
        var pending = RunScriptAsync(
            fx, registry, "await Tools.Call(\"gate\", new {}); return \"" + marker + "\";", marker,
            timeoutSeconds: 60);
        await fx.WaitUntilAsync(() => !gate.Entered.IsEmpty, $"the {marker} script to reach its gate call");
        var during = new HashSet<int>(HostProcessTree.GetChildPids(hostPid));
        during.ExceptWith(baseline);
        var executorPid = Assert.Single(during);
        Assert.True(E2eFixture.IsRunning(executorPid), $"The {marker} executor (pid {executorPid}) was not running mid-call.");
        gate.Release();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(60), fx.Ct);
        Assert.False(result.IsError, result.Content);
        Assert.Contains(marker, result.Content, StringComparison.Ordinal);
        await fx.WaitUntilAsync(
            () => new HashSet<int>(HostProcessTree.GetChildPids(hostPid)).SetEquals(baseline),
            $"the {marker} executor to be gone after its call returned");
        Assert.False(E2eFixture.IsRunning(executorPid), $"The {marker} executor (pid {executorPid}) survived its call.");
        return executorPid;
    }

    private static Task<ToolResult> RunScriptAsync(
        E2eFixture fx, ScriptRegistry registry, string code, string session, double? timeoutSeconds = null)
    {
        var runCode = fx.Client.CreateRunCodeTool(registry);
        var arguments = timeoutSeconds is null
            ? JsonSerializer.Serialize(new { code })
            : JsonSerializer.Serialize(new { code, timeout_seconds = timeoutSeconds });
        return runCode.InvokeAsync(
            new ToolCall(Guid.NewGuid().ToString("N"), "run_code", arguments),
            new ToolContext(session, fx.Root),
            fx.Ct).AsTask();
    }

    /// <summary>Copies the test output to a temp directory minus every <c>Forge.ScriptExecutor.*</c> file, so the
    /// host launched from there cannot discover an executor. Reads only; parallel tests keep using the original.</summary>
    private static string ShadowHostWithoutExecutor()
    {
        var source = AppContext.BaseDirectory;
        var shadow = Path.Combine(Path.GetTempPath(), "forge-shadow-host-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(shadow);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (Path.GetFileName(file).StartsWith("Forge.ScriptExecutor.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = Path.Combine(shadow, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        Assert.True(
            File.Exists(Path.Combine(shadow, "Forge.ExtensionHost.dll")),
            "The shadow host is missing Forge.ExtensionHost.dll.");
        return shadow;
    }

    private static void DeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 49)
                {
                    return;
                }

                Thread.Sleep(100);
            }
        }
    }

    private sealed class GateTool : ITool
    {
        public const string Name = "gate";

        private TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> Entered { get; } = new();

        public ToolDefinition Definition { get; } = new(Name, "Host-path test gate tool", """{"type":"object"}""");

        public ToolSideEffect SideEffect => ToolSideEffect.Execute;

        public void Release() => release.TrySetResult();

        public void Reset()
        {
            while (Entered.TryDequeue(out _))
            {
            }

            release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
        {
            Entered.Enqueue("gate");
            try
            {
                await release.Task.WaitAsync(ct);
                return new ToolResult("go");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }

    private sealed class ScriptRegistry : IToolRegistry
    {
        private readonly Dictionary<string, ITool> tools;

        public ScriptRegistry(params ITool[] entries)
        {
            tools = new Dictionary<string, ITool>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                tools[entry.Definition.Name] = entry;
            }

            Tools = tools.Values.ToArray();
        }

        public IReadOnlyList<ITool> Tools { get; }

        public bool TryGet(string name, out ITool tool) => tools.TryGetValue(name, out tool!);
    }
}

/// <summary>OS process-tree seam for the host-path tests: lists the direct child pids of a process. The supervisor
/// exposes no executor pid and these tests add no production hook, so this snapshot is the strongest available
/// evidence that each host-driven call spawned exactly one executor and left none behind.</summary>
internal static class HostProcessTree
{
    public static IReadOnlyList<int> GetChildPids(int parentPid)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsChildPids(parentPid);
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacChildPids(parentPid);
        }

        return ProcChildPids(parentPid);
    }

    private static IReadOnlyList<int> WindowsChildPids(int parentPid)
    {
        const uint TH32CS_SNAPPROCESS = 0x00000002;
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == -1)
        {
            throw new InvalidOperationException("The process snapshot could not be created.");
        }

        try
        {
            var children = new List<int>();
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return children;
            }

            do
            {
                if (entry.th32ParentProcessID == (uint)parentPid)
                {
                    children.Add((int)entry.th32ProcessID);
                }
            }
            while (Process32Next(snapshot, ref entry));

            return children;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static IReadOnlyList<int> ProcChildPids(int parentPid)
    {
        var children = new List<int>();
        string[] directories;
        try
        {
            directories = Directory.GetDirectories("/proc");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return children;
        }

        foreach (var directory in directories)
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid))
            {
                continue;
            }

            try
            {
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var closeParen = stat.LastIndexOf(')');
                if (closeParen < 0)
                {
                    continue;
                }

                var fields = stat[(closeParen + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 2 && int.TryParse(fields[1], out var ppid) && ppid == parentPid)
                {
                    children.Add(pid);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
            }
        }

        return children;
    }

    /// <summary>Reads direct children from <c>ps -axo pid=,ppid=</c>; macOS has no <c>/proc</c>, so the
    /// Linux <see cref="ProcChildPids"/> fallback cannot see the host's worker and executor children there.</summary>
    private static IReadOnlyList<int> MacChildPids(int parentPid)
    {
        var children = new List<int>();
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/ps",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-axo");
        startInfo.ArgumentList.Add("pid=,ppid=");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return children;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 &&
                    int.TryParse(parts[0], out var pid) &&
                    int.TryParse(parts[1], out var ppid) &&
                    ppid == parentPid)
                {
                    children.Add(pid);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return children;
        }

        return children;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile = string.Empty;

        public PROCESSENTRY32()
        {
        }
    }
}
