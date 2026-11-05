using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Forge.Ipc;

namespace Forge.Integration.Tests;

/// <summary>Starts real product processes for tests that must observe behavior outside the fixture client:
/// a raw host whose stderr is captured, and a single-use script executor spoken to directly over its pipe.
/// Everything started here is torn down by the test; no foreign processes are ever touched.</summary>
internal sealed class RawHost : IAsyncDisposable
{
    private readonly Process process;
    private readonly ConcurrentQueue<string> stderr = new();
    private bool disposed;

    private RawHost(Process process, string root, string pipe)
    {
        this.process = process;
        Root = root;
        Pipe = pipe;
        Plugins = Path.Combine(root, "plugins");
        Data = Path.Combine(root, "data");
    }

    public string Root { get; }

    public string Pipe { get; }

    public string Plugins { get; }

    public string Data { get; }

    public static RawHost StartNew()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-e2e-raw-" + Guid.NewGuid().ToString("N")[..10]);
        var plugins = Path.Combine(root, "plugins");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(plugins);
        Directory.CreateDirectory(data);
        var pipe = "forge-e2e-raw-" + Guid.NewGuid().ToString("N")[..20];
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
        info.ArgumentList.Add(pipe);
        info.ArgumentList.Add("--plugins");
        info.ArgumentList.Add(plugins);
        info.ArgumentList.Add("--data");
        info.ArgumentList.Add(data);
        var process = Process.Start(info) ?? throw new InvalidOperationException("The raw host did not start.");
        var host = new RawHost(process, root, pipe);
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                host.stderr.Enqueue(args.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return host;
    }

    /// <summary>Connects a raw peer to the host, retrying while the host is still coming up.</summary>
    public static async Task<IpcPeer> ConnectAsync(string pipe, CancellationToken ct)
    {
        while (true)
        {
            var transport = new NamedPipeClientTransport(pipe, new IpcPipeOptions { ConnectTimeout = TimeSpan.FromSeconds(2) });
            try
            {
                await transport.StartAsync(ct);
                var peer = new IpcPeer(transport, new IpcPeerOptions { DefaultTimeout = TimeSpan.FromSeconds(60) });
                await peer.StartAsync(ct);
                return peer;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
            {
                await transport.DisposeAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
            }
        }
    }

    public static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();

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

    public async ValueTask DisposeAsync()
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

    private static void TryDeleteDirectory(string path)
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
                JsonDocument.Parse("{\"content\":\"the test host provides no tools.\",\"isError\":true}").RootElement.Clone(),
                requestCt);
            await peer.StartAsync(ct);
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

    public Task<IpcMessage> SendAsync(string operation, JsonElement args, CancellationToken ct) =>
        Peer.SendRequestAsync(operation, args, ct);

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
