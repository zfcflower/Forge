using System.Text.Json;
using Forge.Abstractions;
using Forge.ExtensionHost;
using Forge.HostClient;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>
/// Shutdown fence for the script worker pool: <c>DisposeAsync</c> returns only after the owned child is gone, no
/// further worker starts afterwards, and concurrent shutdowns join the same teardown. A never-connecting
/// <see cref="FakeScriptWorker"/> pins the startup race, a pipe-holding one pins the single-flight race, and the
/// queued-script drain rides along as a no-lingering-work baseline. The concurrent-dispose test stays red until the
/// single-flight retire fix lands.
/// </summary>
public sealed class HostShutdownFenceTests
{
    private static readonly TimeSpan StepLimit = TestBudgets.Safety(TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(10));

    [Fact]
    public async Task Dispose_waits_for_a_starting_script_worker_and_starts_no_new_one()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-shutdown-fence-" + Guid.NewGuid().ToString("N")[..10]);
        var plugins = Path.Combine(root, "plugins");
        var data = Path.Combine(root, "data");
        var fakeDirectory = Path.Combine(root, "fake");
        Directory.CreateDirectory(plugins);
        Directory.CreateDirectory(data);
        var pipe = "forge-shutdown-fence-" + Guid.NewGuid().ToString("N")[..16];

        // Starts the worker process but never connects the worker pipe: the parent's startup stalls
        // after the child exists and before any session is published.
        var fakeWorker = FakeScriptWorker.Build(fakeDirectory);
        var host = new Forge.ExtensionHost.ExtensionHost(new HostOptions
        {
            PipeName = pipe,
            PluginsDirectory = plugins,
            DataDirectory = data,
            RuntimePath = "dotnet",
            WorkerPath = fakeWorker,
            ExecutorPath = fakeWorker,
            ScriptWorkerCount = 1,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        });
        var runTask = host.RunAsync();
        await using var client = new ExtensionHostClient(
            new HostConnection { PipeName = pipe, HostExecutablePath = null, StartTimeout = TimeSpan.FromSeconds(30) },
            null,
            null,
            new ExtensionHostClientOptions { RequestTimeout = TimeSpan.FromSeconds(60) });
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        try
        {
            await client.StartAsync(ct);
            var runCode = client.CreateRunCodeTool(new EmptyRegistry());

            // The script needs a real compilation and run; its worker never gets past connecting.
            using var scriptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var script = InvokeScriptAsync(
                runCode, """return 1;""", TimeSpan.FromSeconds(30), data, scriptCts.Token);

            // Start barrier: the child process is alive, the parent is blocked in startup.
            await WaitForFileAsync(
                Path.Combine(fakeDirectory, FakeScriptWorker.ReadyFile),
                "the starting worker to report itself", ct);

            await host.DisposeAsync();

            // The fence: DisposeAsync returns only after the owned child is gone.
            var pid = FakeScriptWorker.ReadPid(fakeDirectory);
            Assert.False(
                IsRunning(pid),
                $"DisposeAsync returned while the owned starting worker (pid {pid}) was still alive.");
            Assert.Equal(1, FakeScriptWorker.StartedCount(fakeDirectory));

            // No further worker can start or recover afterwards: the next script ends as an explicit shutdown
            // error, never as success, and never spawns. A shutdown refusal arrives as an error ToolResult
            // (host_stopping), a torn-down pipe as a disconnect exception; both are legitimate, success is not.
            ToolResult? secondResult = null;
            Exception? secondFault = null;
            try
            {
                secondResult = await InvokeScriptAsync(runCode, """return 2;""", TimeSpan.FromSeconds(5), data, ct);
            }
            catch (Exception exception)
            {
                secondFault = exception;
            }

            ShutdownFenceOracle.AssertShutdownOutcome(secondResult, secondFault, "the post-shutdown script call");
            Assert.Equal(1, FakeScriptWorker.StartedCount(fakeDirectory));

            // The first script ends the same way: an explicit shutdown error or an allowed shutdown exception,
            // never a successful result and never a hang.
            var firstEnding = await EndAsAsync(script, "the first script call", ct);
            ShutdownFenceOracle.AssertShutdownOutcome(firstEnding.Result, firstEnding.Fault, "the first script call");
        }
        finally
        {
            try
            {
                File.WriteAllText(Path.Combine(fakeDirectory, "release.txt"), "release");
            }
            catch (IOException)
            {
            }

            await client.DisposeAsync();
            DeleteDirectory(root);
        }

        Assert.Equal(0, await runTask.WaitAsync(ct));
    }

    [Fact]
    public async Task Running_and_queued_scripts_shut_down_without_lingering_work()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-shutdown-drain-" + Guid.NewGuid().ToString("N")[..10]);
        var plugins = Path.Combine(root, "plugins");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(plugins);
        Directory.CreateDirectory(data);
        var pipe = "forge-shutdown-drain-" + Guid.NewGuid().ToString("N")[..16];

        var host = new Forge.ExtensionHost.ExtensionHost(new HostOptions
        {
            PipeName = pipe,
            PluginsDirectory = plugins,
            DataDirectory = data,
            RuntimePath = "dotnet",
            WorkerPath = Path.Combine(AppContext.BaseDirectory, "Forge.PluginWorker.dll"),
            ScriptWorkerCount = 1,
        });
        var blocker = new BlockerTool();
        var runTask = host.RunAsync();
        await using var client = new ExtensionHostClient(
            new HostConnection { PipeName = pipe, HostExecutablePath = null, StartTimeout = TimeSpan.FromSeconds(30) },
            name => name == BlockerTool.Name ? blocker : null,
            (_, _) => ValueTask.FromResult(ApprovalDecision.Allow),
            new ExtensionHostClientOptions { RequestTimeout = TimeSpan.FromSeconds(60) });
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        try
        {
            await client.StartAsync(ct);
            var runCode = client.CreateRunCodeTool(new SingleToolRegistry(blocker));

            // One script runs inside the only script worker; the main-program tool call proves it is there.
            using var runningCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var running = InvokeScriptAsync(
                runCode, """await Tools.Call("blocker", new {}); return "a-done";""",
                TimeSpan.FromSeconds(60), data, runningCts.Token);
            await blocker.Entered.WaitAsync(TimeSpan.FromSeconds(30), ct);

            // A second script queues on the single slot: with the first script still running,
            // starting it would require a slot that does not exist.
            using var queuedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var queued = InvokeScriptAsync(
                runCode, """return "b-done";""", TimeSpan.FromSeconds(60), data, queuedCts.Token);

            await host.DisposeAsync();

            // The in-flight forwarded call is torn down with its worker instead of lingering in it.
            // The teardown rides whichever shutdown path wins the race, so it is awaited with a bound
            // instead of assumed the moment DisposeAsync returns.
            await WaitForConditionAsync(
                () => blocker.WasCancelled, "the running script's worker to be torn down", ct);
            Assert.True(blocker.WasCancelled);

            // Neither script outlives the shutdown: each ends as an explicit shutdown error once its worker
            // is gone, or faults when our cancellation wins the race, and the queued one never starts.
            // A bounded wait turns a hung call into an explicit failure instead of masking it.
            runningCts.Cancel();
            queuedCts.Cancel();
            var runningEnding = await EndAsAsync(running, "the running script call", ct);
            ShutdownFenceOracle.AssertShutdownOutcome(runningEnding.Result, runningEnding.Fault, "the running script call");
            var queuedEnding = await EndAsAsync(queued, "the queued script call", ct);
            ShutdownFenceOracle.AssertShutdownOutcome(queuedEnding.Result, queuedEnding.Fault, "the queued script call");
        }
        finally
        {
            blocker.Release();
            await client.DisposeAsync();
            DeleteDirectory(root);
        }

        Assert.Equal(0, await runTask.WaitAsync(ct));
    }

    [Fact]
    public async Task Concurrent_dispose_calls_join_the_same_script_worker_teardown()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-shutdown-join-" + Guid.NewGuid().ToString("N")[..10]);
        var plugins = Path.Combine(root, "plugins");
        var data = Path.Combine(root, "data");
        var fakeDirectory = Path.Combine(root, "fake");
        Directory.CreateDirectory(plugins);
        Directory.CreateDirectory(data);
        var pipe = "forge-shutdown-join-" + Guid.NewGuid().ToString("N")[..16];

        // The worker connects the pipe and reads the first frame, then answers nothing until the host kills the
        // process tree, so the graceful-stop round trip always runs its full window: a shutdown waiter that returns
        // early is observable without timing guesses.
        var fakeWorker = FakeScriptWorker.Build(fakeDirectory, FakeWorkerMode.ConnectHold);
        var host = new Forge.ExtensionHost.ExtensionHost(new HostOptions
        {
            PipeName = pipe,
            PluginsDirectory = plugins,
            DataDirectory = data,
            RuntimePath = "dotnet",
            WorkerPath = fakeWorker,
            ExecutorPath = fakeWorker,
            ScriptWorkerCount = 1,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        });
        var runTask = host.RunAsync();
        await using var client = new ExtensionHostClient(
            new HostConnection { PipeName = pipe, HostExecutablePath = null, StartTimeout = TimeSpan.FromSeconds(30) },
            null,
            null,
            new ExtensionHostClientOptions { RequestTimeout = TimeSpan.FromSeconds(60) });
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        try
        {
            await client.StartAsync(ct);
            var runCode = client.CreateRunCodeTool(new EmptyRegistry());

            var script = InvokeScriptAsync(
                runCode, """return 1;""", TimeSpan.FromSeconds(30), data, ct);

            // Lease barrier: the fake holds the pipe and has seen the first frame, so the pool adopted and leased it.
            await WaitForFileAsync(
                Path.Combine(fakeDirectory, FakeScriptWorker.RequestSeenFile),
                "the held worker to take the lease", ct);
            var pid = FakeScriptWorker.ReadPid(fakeDirectory);
            Assert.True(IsRunning(pid), "the held worker exited before the shutdown; the stall did not hold.");

            var first = host.DisposeAsync();
            var second = host.DisposeAsync();

            // Single-flight: no shutdown waiter - either DisposeAsync caller or the ending RunAsync - may complete
            // while the owned child is still alive. Both early-return paths complete synchronously, so the snapshot
            // below needs no timing window; every waiter is still awaited afterwards, win or lose.
            var firstDoneAtJoin = first.IsCompleted;
            var secondDoneAtJoin = second.IsCompleted;
            var runDoneAtJoin = runTask.IsCompleted;
            var childAliveAtJoin = IsRunning(pid);

            await first;
            await second;
            Assert.Equal(0, await runTask.WaitAsync(ct));

            Assert.False(
                (firstDoneAtJoin || secondDoneAtJoin || runDoneAtJoin) && childAliveAtJoin,
                "a shutdown waiter returned while the owned script worker was still alive; concurrent shutdowns must join the same teardown.");

            // All callers completed: no child left, no post-stop spawn, no unobserved work.
            Assert.False(IsRunning(pid), $"the teardown finished but the owned script worker (pid {pid}) was still alive.");
            Assert.Equal(1, FakeScriptWorker.StartedCount(fakeDirectory));
            var heldEnding = await EndAsAsync(script, "the held script call", ct);
            ShutdownFenceOracle.AssertShutdownOutcome(heldEnding.Result, heldEnding.Fault, "the held script call");

            ToolResult? postResult = null;
            Exception? postFault = null;
            try
            {
                postResult = await InvokeScriptAsync(runCode, """return 2;""", TimeSpan.FromSeconds(5), data, ct);
            }
            catch (Exception exception)
            {
                postFault = exception;
            }

            ShutdownFenceOracle.AssertShutdownOutcome(postResult, postFault, "the post-shutdown script call");
            Assert.Equal(1, FakeScriptWorker.StartedCount(fakeDirectory));
        }
        finally
        {
            try
            {
                File.WriteAllText(Path.Combine(fakeDirectory, "release.txt"), "release");
            }
            catch (IOException)
            {
            }

            await client.DisposeAsync();
            DeleteDirectory(root);
        }

        Assert.Equal(0, await runTask.WaitAsync(ct));
    }

    private static Task<ToolResult> InvokeScriptAsync(
        ITool runCode, string code, TimeSpan timeout, string workingDirectory, CancellationToken ct) =>
        runCode.InvokeAsync(
            new ToolCall(
                Guid.NewGuid().ToString("N"),
                "run_code",
                JsonSerializer.Serialize(new { code, timeout_seconds = timeout.TotalSeconds })),
            new ToolContext("shutdown-fence", workingDirectory),
            ct).AsTask();

    private static async Task<(ToolResult? Result, Exception? Fault)> EndAsAsync(
        Task<ToolResult> call, string what, CancellationToken ct)
    {
        try
        {
            return (await call.WaitAsync(TimeSpan.FromSeconds(15), ct), null);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{what} never ended after the shutdown.");
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, string what, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TestBudgets.Condition);
        while (!condition())
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }
        }
    }

    private static async Task WaitForFileAsync(string path, string what, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TestBudgets.Condition);
        while (!File.Exists(path))
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {what} ('{path}').");
            }
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
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

    private sealed class EmptyRegistry : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools => [];
        public bool TryGet(string name, out ITool tool)
        {
            tool = null!;
            return false;
        }
    }

    private sealed class SingleToolRegistry(ITool tool) : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools => [tool];
        public bool TryGet(string name, out ITool found)
        {
            found = tool;
            return string.Equals(tool.Definition.Name, name, StringComparison.Ordinal);
        }
    }

    /// <summary>Main-program tool the script calls: entering proves the script runs in a worker, and the
    /// token proves the worker was torn down when the forwarded call is cancelled.</summary>
    private sealed class BlockerTool : ITool
    {
        public const string Name = "blocker";

        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int cancelled;

        public ToolDefinition Definition { get; } = new(Name, "Test barrier tool", "{}");

        public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

        public Task Entered => entered.Task;

        public bool WasCancelled => Volatile.Read(ref cancelled) != 0;

        public void Release() => released.TrySetResult();

        public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
        {
            entered.TrySetResult();
            try
            {
                await released.Task.WaitAsync(ct);
                return new ToolResult("released");
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref cancelled, 1);
                throw;
            }
        }
    }
}
