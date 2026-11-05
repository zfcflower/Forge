using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Forge.Ipc;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.Integration.Tests;

/// <summary>Plugin whose <c>hold</c> tool keeps its worker slot until a release file appears.</summary>
internal static class PluginQueueBudgetSources
{
    public const string HoldTool = "hold";
    public const string MarkTool = "mark";
    public const string EchoTool = "echo";
    public const string PidTool = "pid";

    public static string Gated(string id) =>
        $$"""
        public sealed class TestPlugin : IPlugin
        {
            public PluginInfo Info => new("{{id}}", "1.0.0", "queue budget test plugin");

            public void Register(IRegistrationContext context)
            {
                context.AddTool(new EchoTool());
                context.AddTool(new PidTool());
                context.AddTool(new MarkTool());
                context.AddTool(new HoldTool());
                context.AddCommand(new MarkCommand());
            }

            internal static string Arg(JsonElement args, string name) =>
                args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) ? value.ToString() : "";
        }

        public sealed class EchoTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("echo", "Echoes the text argument", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
                Task.FromResult(new ToolResult("g:" + TestPlugin.Arg(invocation.Arguments, "text")));
        }

        public sealed class PidTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("pid", "Reports the worker process id", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
                Task.FromResult(new ToolResult(Environment.ProcessId.ToString()));
        }

        public sealed class MarkTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("mark", "Writes the marker file and returns", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
            {
                var marker = TestPlugin.Arg(invocation.Arguments, "marker");
                if (marker.Length > 0)
                {
                    File.WriteAllText(marker, "ran");
                }

                return Task.FromResult(new ToolResult("marked"));
            }
        }

        public sealed class MarkCommand : IPluginCommand
        {
            public CommandDeclaration Declaration { get; } =
                new("markcmd", "Writes the marker file and returns", "");

            public Task<CommandOutput> ExecuteAsync(CommandInvocation invocation, CancellationToken ct)
            {
                var marker = "";
                try
                {
                    using var document = JsonDocument.Parse(invocation.Arguments);
                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("marker", out var value))
                    {
                        marker = value.ToString();
                    }
                }
                catch (JsonException)
                {
                }

                if (marker.Length > 0)
                {
                    File.WriteAllText(marker, "ran");
                }

                return Task.FromResult(new CommandOutput("marked"));
            }
        }

        public sealed class HoldTool : IPluginTool
        {
            public ToolDeclaration Declaration { get; } =
                new("hold", "Writes its marker, then holds the worker until the release file exists; ignores cancellation", "{\"type\":\"object\"}", ToolSideEffect.ReadOnly);

            public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
            {
                var marker = TestPlugin.Arg(invocation.Arguments, "marker");
                var release = TestPlugin.Arg(invocation.Arguments, "release");
                if (marker.Length > 0)
                {
                    File.WriteAllText(marker, "held");
                }

                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (!File.Exists(release) && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20);
                }

                return new ToolResult(File.Exists(release) ? "held:released" : "held:timeout");
            }
        }
        """;
}

/// <summary>An Extension Host process driven over a raw peer, so per-request budgets and call contexts can be set.</summary>
internal sealed class PluginQueueBudgetHost : IAsyncDisposable
{
    private readonly Process process;
    private readonly ConcurrentQueue<string> output = new();
    private readonly bool ownsRoot;
    private bool disposed;

    private PluginQueueBudgetHost(Process process, string root, string pipe, bool ownsRoot)
    {
        this.process = process;
        Root = root;
        Pipe = pipe;
        this.ownsRoot = ownsRoot;
    }

    public string Root { get; }

    public string Pipe { get; }

    public string PluginsDirectory => Path.Combine(Root, "plugins");

    public string DataDirectory => Path.Combine(Root, "data");

    public int ProcessId => process.Id;

    public IReadOnlyList<string> Output => output.ToArray();

    /// <summary>Creates an empty root for a sequence of hosts that share plugins and data. The caller deletes it.</summary>
    public static string NewRoot(string tag)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-budget-" + tag + "-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path.Combine(root, "plugins"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        return root;
    }

    /// <summary>Starts a host; a caller-supplied root is shared across hosts and is not deleted on dispose.</summary>
    public static PluginQueueBudgetHost Start(IReadOnlyList<string> hostArguments, string tag = "queue", string? root = null)
    {
        var ownsRoot = root is null;
        root ??= Path.Combine(Path.GetTempPath(), "forge-budget-" + tag + "-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path.Combine(root, "plugins"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        var pipe = "forge-budget-" + Guid.NewGuid().ToString("N")[..20];
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
        info.ArgumentList.Add(Path.Combine(root, "plugins"));
        info.ArgumentList.Add("--data");
        info.ArgumentList.Add(Path.Combine(root, "data"));
        foreach (var argument in hostArguments)
        {
            info.ArgumentList.Add(argument);
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("The Extension Host did not start.");
        var host = new PluginQueueBudgetHost(process, root, pipe, ownsRoot);
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                host.output.Enqueue(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                host.output.Enqueue(args.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return host;
    }

    public void WritePlugin(string id, string source)
    {
        var directory = Path.Combine(PluginsDirectory, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(new { id, version = "1.0.0", source = "Plugin.cs" }));
        File.WriteAllText(Path.Combine(directory, "Plugin.cs"), source);
    }

    /// <summary>Connects a raw peer, retrying while the host is still coming up.</summary>
    public async Task<IpcPeer> ConnectAsync(CancellationToken ct)
    {
        while (true)
        {
            var transport = new NamedPipeClientTransport(Pipe, new IpcPipeOptions { ConnectTimeout = TimeSpan.FromSeconds(2) });
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

    /// <summary>Waits for the host process to exit on its own and returns its exit code; the bound is the caller's.</summary>
    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
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

        await process.WaitForExitAsync(CancellationToken.None);
        process.Dispose();
        if (ownsRoot)
        {
            DeleteDirectory(Root);
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
}

/// <summary>Wire helpers for raw <c>plugin.execute</c> calls and the status/approval handshake.</summary>
internal static class PluginQueueBudgetWire
{
    /// <summary>A bound <c>plugin.execute</c> request; a non-empty chain makes it nested under <paramref name="rootRunId"/>.</summary>
    public static JsonElement Execute(
        string root,
        string pluginId,
        string tool,
        object args,
        string runId,
        string rootRunId,
        IReadOnlyList<string> chain,
        int timeoutMilliseconds) =>
        JsonSerializer.SerializeToElement(new
        {
            pluginId,
            tool,
            args = JsonSerializer.SerializeToElement(args),
            sessionId = "queue-budget",
            workingDir = root,
            depth = chain.Count,
            context = new
            {
                runId,
                sessionId = "queue-budget",
                workingDirectory = root,
                depth = chain.Count,
                pluginChain = chain,
                rootRunId,
            },
            timeoutMilliseconds,
        });

    public static (string Content, bool IsError) ReadToolResult(IpcMessage message)
    {
        var result = message.Result ?? throw new InvalidOperationException("the plugin.execute answer carried no result");
        return (result.GetProperty("content").GetString() ?? string.Empty, result.GetProperty("isError").GetBoolean());
    }

    /// <summary>A bound <c>plugin.command</c> request sharing the caller's budget and root identity.</summary>
    public static JsonElement ExecuteCommand(
        string root,
        string pluginId,
        string command,
        string arguments,
        string runId,
        string rootRunId,
        IReadOnlyList<string> chain,
        int timeoutMilliseconds) =>
        JsonSerializer.SerializeToElement(new
        {
            pluginId,
            command,
            arguments,
            context = new
            {
                runId,
                sessionId = "queue-budget",
                workingDirectory = root,
                depth = chain.Count,
                pluginChain = chain,
                rootRunId,
            },
            timeoutMilliseconds,
        });

    public static (string Output, bool IsError) ReadCommandResult(IpcMessage message)
    {
        var result = message.Result ?? throw new InvalidOperationException("the plugin.command answer carried no result");
        return (result.GetProperty("output").GetString() ?? string.Empty, result.GetProperty("isError").GetBoolean());
    }

    /// <summary>Refreshes, waits for the plugin's status row and approves it with the hashes the host reports.</summary>
    public static async Task ApproveAsync(IpcPeer peer, string pluginId, CancellationToken ct)
    {
        await peer.SendRequestAsync(Sdk.ExtensionProtocol.PluginsRefresh, Empty(), ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var response = await peer.SendRequestAsync(Sdk.ExtensionProtocol.PluginsStatus, Empty(), deadline.Token);
            foreach (var entry in response.Result!.Value.GetProperty("plugins").EnumerateArray())
            {
                if (entry.GetProperty("pluginId").GetString() != pluginId)
                {
                    continue;
                }

                await peer.SendRequestAsync(
                    Sdk.ExtensionProtocol.PluginApprove,
                    JsonSerializer.SerializeToElement(new
                    {
                        pluginId,
                        version = entry.GetProperty("version").GetString(),
                        sourceHash = entry.GetProperty("sourceHash").GetString(),
                        dependencyHash = entry.GetProperty("dependencyHash").GetString(),
                        sdkVersion = entry.GetProperty("sdkVersion").GetString(),
                    }),
                    ct);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
        }
    }

    public static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();
}

/// <summary>Bounded polling shared by the queue-budget tests. Each wait has its own limit; none is open-ended.</summary>
internal static class PluginQueueBudgetWait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan limit, CancellationToken ct, string what)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        while (!condition())
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out after {limit.TotalSeconds:0} s waiting for {what}.");
            }
        }
    }
}
