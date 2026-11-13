using System.Collections.Concurrent;
using System.Diagnostics;
using Forge.Ipc;
using Forge.Ipc.Protocol;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.Integration.Tests;

/// <summary>Caller-owned temp directories and a unique pipe name for a raw host, created before the process starts so a
/// test can seed the plugins directory (and read the data directory) itself.</summary>
internal sealed class RawHostLayout
{
    private RawHostLayout(string root, string pipe)
    {
        Root = root;
        Plugins = Path.Combine(root, "plugins");
        Data = Path.Combine(root, "data");
        Pipe = pipe;
    }

    public string Root { get; }

    public string Plugins { get; }

    public string Data { get; }

    public string Pipe { get; }

    /// <summary>Creates the temp root/plugins/data directories and a pipe name under <paramref name="prefix"/>.</summary>
    public static RawHostLayout Create(string prefix = "forge-e2e-raw-")
    {
        var layout = new RawHostLayout(
            Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..10]),
            prefix + Guid.NewGuid().ToString("N")[..20]);
        Directory.CreateDirectory(layout.Plugins);
        Directory.CreateDirectory(layout.Data);
        return layout;
    }
}

/// <summary>Starts real product processes for tests that must observe behavior outside the fixture client:
/// a raw host whose stderr is captured, and a single-use script executor spoken to directly over its pipe.
/// Everything started here is torn down by the test; no foreign processes are ever touched.</summary>
internal sealed class RawHost : IAsyncDisposable
{
    private readonly Process process;
    private readonly ConcurrentQueue<string> stderr = new();
    private bool disposed;

    private RawHost(Process process, RawHostLayout layout)
    {
        this.process = process;
        Root = layout.Root;
        Pipe = layout.Pipe;
        Plugins = layout.Plugins;
        Data = layout.Data;
    }

    public string Root { get; }

    public string Pipe { get; }

    public string Plugins { get; }

    public string Data { get; }

    public static RawHost StartNew() => Start(RawHostLayout.Create());

    /// <summary>Starts a raw host on <paramref name="layout"/>; <paramref name="environment"/> entries are added to its
    /// environment and every stdout/stderr line is also appended to <paramref name="output"/> when given.</summary>
    public static RawHost Start(
        RawHostLayout layout,
        IReadOnlyDictionary<string, string>? environment = null,
        List<string>? output = null)
    {
        var info = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(E2eFixture.HostAssemblyPath);
        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(layout.Pipe);
        info.ArgumentList.Add("--plugins");
        info.ArgumentList.Add(layout.Plugins);
        info.ArgumentList.Add("--data");
        info.ArgumentList.Add(layout.Data);
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                info.Environment[pair.Key] = pair.Value;
            }
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("The raw host did not start.");
        var host = new RawHost(process, layout);
        void Capture(string? data)
        {
            if (data is not null && output is not null)
            {
                lock (output)
                {
                    output.Add(data);
                }
            }
        }

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                host.stderr.Enqueue(args.Data);
            }

            Capture(args.Data);
        };
        if (output is not null)
        {
            process.OutputDataReceived += (_, args) => Capture(args.Data);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return host;
    }

    /// <summary>Connects a raw peer to the host, retrying while the host is still coming up.</summary>
    public static async Task<IpcPeer> ConnectAsync(string pipe, CancellationToken ct)
    {
        var transport = await ConnectTransportAsync(pipe, ct);
        var peer = new IpcPeer(transport, new IpcPeerOptions { DefaultTimeout = TimeSpan.FromSeconds(60) });
        await peer.StartAsync(ct);
        return peer;
    }

    /// <summary>Connects a raw transport to the host, retrying while the host is still coming up, for a test that builds
    /// its own peer around it.</summary>
    public static Task<NamedPipeClientTransport> ConnectTransportAsync(string pipe, CancellationToken ct) =>
        PipeWait.ConnectAsync(pipe, () => "The host is started by this test.", ct);

    public async Task<string> WaitForStderrAsync(string needle, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var line in stderr)
            {
                if (line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    return line;
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"Timed out after {limit.TotalSeconds:0} s waiting for host stderr to contain '{needle}'.");
    }

    /// <summary>Kills the host, waits for it to exit, and removes its temp root - the teardown every test owes it.</summary>
    public async Task StopAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Kill(process);
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
        }

        process.Dispose();
        TryDeleteDirectory(Root);
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    public static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>Deletes <paramref name="path"/> recursively, leaving a directory the OS still holds open behind.</summary>
    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A real <c>Forge.ScriptExecutor</c> (or <c>Forge.PluginWorker</c>) process spoken to directly over its
/// pipe, so tests can prove the executor boundary without any fake binary.</summary>
internal sealed class DirectWorker : IAsyncDisposable
{
    private readonly Process process;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> stderr = new();

    private DirectWorker(
        Process process,
        IpcPeer peer,
        System.Collections.Concurrent.ConcurrentQueue<string> stderr,
        NamedPipeServerTransport server)
    {
        this.process = process;
        Peer = peer;
        this.stderr = stderr;
        this.server = server;
    }

    private readonly NamedPipeServerTransport server;

    public IpcPeer Peer { get; }

    public IReadOnlyList<string> Stderr => stderr.ToArray();

    public bool HasExited => process.HasExited;

    public static string WorkerAssemblyPath =>
        Path.Combine(AppContext.BaseDirectory, "Forge.PluginWorker.dll");

    public static string ExecutorAssemblyPath =>
        Path.Combine(AppContext.BaseDirectory, "Forge.ScriptExecutor.dll");

    public static Task<DirectWorker> StartExecutorAsync(CancellationToken ct) =>
        StartProcessAsync(ExecutorAssemblyPath, "forge-e2e-executor-", ct);

    public static Task<DirectWorker> StartWorkerAsync(CancellationToken ct) =>
        StartProcessAsync(WorkerAssemblyPath, "forge-e2e-worker-", ct);

    private static async Task<DirectWorker> StartProcessAsync(string assemblyPath, string pipePrefix, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var pipe = pipePrefix + Guid.NewGuid().ToString("N")[..20];
        var server = new NamedPipeServerTransport(pipe, new IpcPipeOptions { ConnectTimeout = TimeSpan.FromSeconds(30) });
        var accept = server.StartAsync(ct);
        var info = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(assemblyPath);
        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(pipe);
        var process = Process.Start(info) ?? throw new InvalidOperationException("The child process did not start.");
        var captured = new System.Collections.Concurrent.ConcurrentQueue<string>();
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                captured.Enqueue(args.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await accept;
            var peer = new IpcPeer(server, new IpcPeerOptions { DefaultTimeout = TimeSpan.FromSeconds(60) });
            peer.RequestReceived += (message, requestCt) => peer.RespondAsync(
                message.Id,
                new Sdk.ToolResult("the test host provides no tools.", IsError: true),
                requestCt);
            await peer.StartAsync(ct);
            TestBudgets.NoteHostStart(Stopwatch.GetElapsedTime(start));
            return new DirectWorker(process, peer, captured, server);
        }
        catch (Exception ex)
        {
            var detail = "worker '" + pipe + "' did not connect back: exited=" + process.HasExited +
                " stderr=[" + string.Join(" | ", captured) + "] failure=" + ex.GetType().Name + ": " + ex.Message;
            await server.DisposeAsync();
            RawHost.Kill(process);
            process.Dispose();
            throw new InvalidOperationException(detail, ex);
        }
    }

    public async Task<IpcMessage> SendAsync<TArgs>(string operation, TArgs args, CancellationToken ct)
    {
        try
        {
            return await Peer.RequestAsync(operation, args, ct);
        }
        catch (IpcConnectionClosedException exception)
        {
            throw new IpcConnectionClosedException(exception.Message + " " + DescribeProcess(), exception);
        }
    }

    /// <summary>Sends a hand-built payload, for the protocol tests that need a value no formatter would produce.</summary>
    public async Task<IpcMessage> SendRawAsync(string operation, WirePayload payload, CancellationToken ct)
    {
        try
        {
            return await Peer.AskRawAsync(operation, payload, ct);
        }
        catch (IpcConnectionClosedException exception)
        {
            throw new IpcConnectionClosedException(exception.Message + " " + DescribeProcess(), exception);
        }
    }

    /// <summary>What the child was doing when a request found its pipe closed: an exited process with its exit code and
    /// stderr says this is the child, not the test.</summary>
    private string DescribeProcess()
    {
        string state;
        try
        {
            state = process.HasExited ? $"exited with code {process.ExitCode}" : $"is still running as pid {process.Id}";
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            state = "is no longer observable";
        }

        return Stderr.Count == 0
            ? $"The child process {state} and wrote no stderr."
            : $"The child process {state}; stderr: " + string.Join(" | ", Stderr);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Peer.StopAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
        }

        await Peer.DisposeAsync();
        await server.DisposeAsync();
        RawHost.Kill(process);
        process.Dispose();
    }
}
